using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller.HttpRequest;
using Shadowsocks.Controller.Service;
using Shadowsocks.Model;
using Shadowsocks.Model.Transfer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnitTest;

[TestClass]
public class UpdateSubscribeManagerTest
{
    [TestMethod]
    public async Task ManualRequestWhileRunningIsQueued()
    {
        var config = new Configuration();
        var first = Subscribe("https://example.com/1", "sub-1");
        var second = Subscribe("https://example.com/2", "sub-2");
        config.ServerSubscribes.Add(first);
        config.ServerSubscribes.Add(second);
        var firstResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updater = new FakeUpdateNode();
        updater.Responses[first.Url] = _ => firstResponse.Task;
        updater.Responses[second.Url] = _ => Task.FromResult(EncodedLinks(ServerFor("second.example", "sub-2").SsrLink));
        var manager = new UpdateSubscribeManager();
        var allCompleted = WaitForAllCompleted(manager);

        var firstStatus = manager.CreateTask(config, updater, true, new List<ServerSubscribe> { first });
        await updater.WaitForCallCountAsync(1);
        var secondStatus = manager.CreateTask(config, updater, true, new List<ServerSubscribe> { second });
        firstResponse.SetResult(EncodedLinks(ServerFor("first.example", "sub-1").SsrLink));
        var summary = await allCompleted;

        Assert.AreEqual(SubscribeUpdateScheduleStatus.Started, firstStatus);
        Assert.AreEqual(SubscribeUpdateScheduleStatus.Queued, secondStatus);
        CollectionAssert.AreEqual(new[] { first.Url, second.Url }, updater.Calls);
        Assert.AreEqual(2, summary.SuccessCount);
    }

    [TestMethod]
    public async Task AutoRequestForRunningSubscribeIsDeduplicated()
    {
        var config = new Configuration();
        var subscribe = Subscribe("https://example.com/1", "sub-1");
        subscribe.AutoCheckUpdate = true;
        config.ServerSubscribes.Add(subscribe);
        var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updater = new FakeUpdateNode();
        updater.Responses[subscribe.Url] = _ => response.Task;
        var manager = new UpdateSubscribeManager();
        var allCompleted = WaitForAllCompleted(manager);

        var firstStatus = manager.CreateTask(config, updater, false);
        await updater.WaitForCallCountAsync(1);
        var secondStatus = manager.CreateTask(config, updater, false);
        response.SetResult(EncodedLinks(ServerFor("first.example", "sub-1").SsrLink));
        var summary = await allCompleted;

        Assert.AreEqual(SubscribeUpdateScheduleStatus.Started, firstStatus);
        Assert.AreEqual(SubscribeUpdateScheduleStatus.AlreadyQueued, secondStatus);
        Assert.AreEqual(1, updater.Calls.Count);
        Assert.AreEqual(1, summary.SuccessCount);
    }

    [TestMethod]
    public async Task FailedSubscriptionDoesNotBlockNextSubscription()
    {
        var config = new Configuration();
        var first = Subscribe("https://example.com/fail", "sub-fail");
        var second = Subscribe("https://example.com/success", "sub-ok");
        config.ServerSubscribes.Add(first);
        config.ServerSubscribes.Add(second);
        var updater = new FakeUpdateNode();
        updater.Responses[first.Url] = _ => throw new InvalidOperationException("download failed");
        updater.Responses[second.Url] = _ => Task.FromResult(EncodedLinks(ServerFor("ok.example", "sub-ok").SsrLink));
        var manager = new UpdateSubscribeManager();
        var allCompleted = WaitForAllCompleted(manager);

        manager.CreateTask(config, updater, true);
        var summary = await allCompleted;

        Assert.AreEqual(1, summary.SuccessCount);
        Assert.AreEqual(1, summary.FailureCount);
        Assert.IsTrue(config.Configs.Any(server => server.server == "ok.example"));
    }

    [TestMethod]
    public void MergeAddsNewServersRemovesMissingServersAndPreservesSelection()
    {
        var config = new Configuration();
        var subscribe = Subscribe("https://example.com/sub", "sub");
        config.ServerSubscribes.Add(subscribe);
        var oldRemoved = ServerFor("old.example", "sub");
        var oldKept = ServerFor("keep.example", "sub");
        oldRemoved.SubTag = "sub";
        oldKept.SubTag = "sub";
        oldKept.SpeedLog = new ServerSpeedLog(10, 20);
        config.Configs.Add(oldRemoved);
        config.Configs.Add(oldKept);
        config.Index = 1;
        var newKept = ServerFor("keep.example", "sub");
        var newAdded = ServerFor("new.example", "sub");

        var result = UpdateSubscribeManager.ApplySubscribeUpdate(
            config,
            subscribe,
            EncodedLinks(newKept.SsrLink, newAdded.SsrLink));

        Assert.IsTrue(result.Success);
        Assert.AreEqual(2, result.ParsedCount);
        Assert.AreEqual(1, result.AddedCount);
        Assert.AreEqual(1, result.RemovedCount);
        Assert.AreEqual(1, result.UpdatedCount);
        Assert.IsFalse(config.Configs.Any(server => server.server == "old.example"));
        Assert.IsTrue(config.Configs.Any(server => server.server == "new.example"));
        Assert.AreSame(oldKept, config.Configs[config.Index]);
        Assert.AreEqual(10, oldKept.SpeedLog.TotalUploadBytes);
        Assert.AreEqual(20, oldKept.SpeedLog.TotalDownloadBytes);
    }

    [TestMethod]
    public void MergeUnchangedServersReturnsNoChange()
    {
        var config = new Configuration();
        var subscribe = Subscribe("https://example.com/sub", "sub");
        config.ServerSubscribes.Add(subscribe);
        var oldServer = ServerFor("same.example", "sub");
        oldServer.SubTag = "sub";
        config.Configs.Add(oldServer);
        config.Index = 0;
        var newServer = ServerFor("same.example", "sub");

        var result = UpdateSubscribeManager.ApplySubscribeUpdate(config, subscribe, EncodedLinks(newServer.SsrLink));

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.NoChange);
        Assert.AreEqual(0, result.AddedCount);
        Assert.AreEqual(0, result.RemovedCount);
        Assert.AreEqual(1, result.UpdatedCount);
        Assert.AreSame(oldServer, config.Configs[0]);
    }

    [TestMethod]
    public void BadSubscriptionContentDoesNotModifyConfig()
    {
        var config = new Configuration();
        var subscribe = Subscribe("https://example.com/sub", "sub");
        config.ServerSubscribes.Add(subscribe);
        var oldServer = ServerFor("old.example", "sub");
        oldServer.SubTag = "sub";
        config.Configs.Add(oldServer);

        var badBase64 = UpdateSubscribeManager.ApplySubscribeUpdate(config, subscribe, "not base64");
        var badSsr = UpdateSubscribeManager.ApplySubscribeUpdate(
            config,
            subscribe,
            Convert.ToBase64String(Encoding.UTF8.GetBytes("ssr://bad")));

        Assert.IsFalse(badBase64.Success);
        Assert.IsFalse(badSsr.Success);
        Assert.AreEqual(1, config.Configs.Count);
        Assert.AreSame(oldServer, config.Configs[0]);
    }

    private static Task<SubscribeUpdateSummaryEventArgs> WaitForAllCompleted(UpdateSubscribeManager manager)
    {
        var completion = new TaskCompletionSource<SubscribeUpdateSummaryEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.AllCompleted += (_, args) => completion.TrySetResult(args);
        return WithTimeout(completion.Task);
    }

    private static async Task<T> WithTimeout<T>(Task<T> task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (completed != task)
        {
            Assert.Fail("Timed out waiting for subscription update.");
        }

        return await task;
    }

    private static ServerSubscribe Subscribe(string url, string tag)
    {
        return new ServerSubscribe
        {
            Url = url,
            Tag = tag
        };
    }

    private static Server ServerFor(string host, string group)
    {
        return new Server
        {
            server = host,
            Server_Port = 8388,
            Password = "password",
            Method = "aes-256-cfb",
            Protocol = "origin",
            obfs = "plain",
            Group = group,
            Remarks = host
        };
    }

    private static string EncodedLinks(params string[] links)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, links)));
    }

    private sealed class FakeUpdateNode : UpdateNode
    {
        private readonly TaskCompletionSource<object> _callSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Dictionary<string, Func<ServerSubscribe, Task<string>>> Responses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Calls { get; } = new();

        public override Task<string> CheckUpdateAsync(Configuration config, ServerSubscribe subscribeTask, bool notify, CancellationToken ct)
        {
            Calls.Add(subscribeTask.Url);
            _callSignal.TrySetResult(null);
            if (Responses.TryGetValue(subscribeTask.Url, out var response))
            {
                return response(subscribeTask);
            }

            return Task.FromResult(string.Empty);
        }

        public async Task WaitForCallCountAsync(int count)
        {
            while (Calls.Count < count)
            {
                await WithTimeout(_callSignal.Task);
            }
        }
    }
}
