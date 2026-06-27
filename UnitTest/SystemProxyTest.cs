using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller;
using Shadowsocks.Controller.Service;
using Shadowsocks.Enums;
using Shadowsocks.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UnitTest;

[TestClass]
public class SystemProxyTest
{
    private TempCurrentDirectory _tempDirectory;
    private bool _oldSaveToFile;

    [TestInitialize]
    public void Setup()
    {
        _oldSaveToFile = Logging.SaveToFile;
        Logging.SaveToFile = false;
        Logging.DefaultOut = Console.Out;
        Logging.DefaultError = Console.Error;
        _tempDirectory = new TempCurrentDirectory();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SystemProxy.ResetBackendFactoryForTesting();
        Logging.SaveToFile = _oldSaveToFile;
        _tempDirectory?.Dispose();
    }

    [TestMethod]
    public void RestoreRetriesWhenSetReturnsFalse()
    {
        var oldStatus = DirectStatus();
        var host = new FakeProxyHost { State = GlobalStatus(1080) };
        host.SetBehaviors.Enqueue(new FakeBehavior(false, false));
        host.SetBehaviors.Enqueue(new FakeBehavior(false, false));
        host.SetBehaviors.Enqueue(new FakeBehavior(true, true));
        SystemProxy.SetBackendFactoryForTesting(() => new FakeWindowsProxyBackend(host), oldStatus);

        var restored = SystemProxy.Restore(1080);

        Assert.IsTrue(restored);
        Assert.AreEqual(3, host.SetCalls);
        Assert.AreEqual(0, host.DirectCalls);
        AssertStatus(oldStatus, host.State);
    }

    [TestMethod]
    public void RestoreRetriesWhenQueryDoesNotMatchExpectedStatus()
    {
        var oldStatus = DirectStatus();
        var host = new FakeProxyHost { State = GlobalStatus(1080) };
        host.SetBehaviors.Enqueue(new FakeBehavior(true, false));
        host.SetBehaviors.Enqueue(new FakeBehavior(true, true));
        SystemProxy.SetBackendFactoryForTesting(() => new FakeWindowsProxyBackend(host), oldStatus);

        var restored = SystemProxy.Restore(1080);

        Assert.IsTrue(restored);
        Assert.AreEqual(2, host.SetCalls);
        Assert.AreEqual(0, host.DirectCalls);
        AssertStatus(oldStatus, host.State);
    }

    [TestMethod]
    public void RestoreFallsBackToDirectWhenInitialStatusUnavailable()
    {
        var host = new FakeProxyHost { State = GlobalStatus(1080) };
        SystemProxy.SetBackendFactoryForTesting(() => new FakeWindowsProxyBackend(host));

        var restored = SystemProxy.Restore(1080);

        Assert.IsTrue(restored);
        Assert.AreEqual(0, host.SetCalls);
        Assert.AreEqual(1, host.DirectCalls);
        Assert.IsFalse(host.State.IsProxy);
    }

    [TestMethod]
    public void RestoreFallsBackToDirectWhenCurrentProxyStillPointsToThisApp()
    {
        var oldStatus = new SystemProxyStatus(true, true, false, false, "corp-proxy:8080", string.Empty, string.Empty);
        var host = new FakeProxyHost { State = GlobalStatus(1080) };
        host.SetBehaviors.Enqueue(new FakeBehavior(false, false));
        host.SetBehaviors.Enqueue(new FakeBehavior(false, false));
        host.SetBehaviors.Enqueue(new FakeBehavior(false, false));
        SystemProxy.SetBackendFactoryForTesting(() => new FakeWindowsProxyBackend(host), oldStatus);

        var restored = SystemProxy.Restore(1080);

        Assert.IsTrue(restored);
        Assert.AreEqual(3, host.SetCalls);
        Assert.AreEqual(1, host.DirectCalls);
        Assert.IsFalse(host.State.IsProxy);
    }

    [TestMethod]
    public void UpdateGlobalRetriesWhenApplyFails()
    {
        var host = new FakeProxyHost { State = DirectStatus() };
        host.GlobalBehaviors.Enqueue(new FakeBehavior(false, false));
        host.GlobalBehaviors.Enqueue(new FakeBehavior(true, true));
        SystemProxy.SetBackendFactoryForTesting(() => new FakeWindowsProxyBackend(host), DirectStatus());
        var config = new Configuration
        {
            SysProxyMode = ProxyMode.Global,
            LocalPort = 1080
        };

        var updated = SystemProxy.Update(config, null);

        Assert.IsTrue(updated);
        Assert.AreEqual(2, host.GlobalCalls);
        Assert.AreEqual("localhost:1080", host.State.ProxyServer);
        Assert.IsTrue(host.State.IsProxy);
    }

    [TestMethod]
    public void UpdateGlobalCreatesStartupRecoveryMarker()
    {
        var host = new FakeProxyHost { State = DirectStatus() };
        SystemProxy.SetBackendFactoryForTesting(() => new FakeWindowsProxyBackend(host), DirectStatus());
        var config = new Configuration
        {
            SysProxyMode = ProxyMode.Global,
            LocalPort = 1080
        };

        var updated = SystemProxy.Update(config, null);

        Assert.IsTrue(updated);
        Assert.IsTrue(File.Exists(SystemProxy.StateFileName));
        using var document = JsonDocument.Parse(File.ReadAllText(SystemProxy.StateFileName));
        Assert.AreEqual(1080, document.RootElement.GetProperty("LocalPort").GetInt32());
        Assert.AreEqual((int)ProxyMode.Global, document.RootElement.GetProperty("Mode").GetInt32());
    }

    [TestMethod]
    public void RestoreDeletesStartupRecoveryMarker()
    {
        var host = new FakeProxyHost { State = DirectStatus() };
        SystemProxy.SetBackendFactoryForTesting(() => new FakeWindowsProxyBackend(host), DirectStatus());
        var config = new Configuration
        {
            SysProxyMode = ProxyMode.Global,
            LocalPort = 1080
        };
        Assert.IsTrue(SystemProxy.Update(config, null));
        Assert.IsTrue(File.Exists(SystemProxy.StateFileName));

        var restored = SystemProxy.Restore(1080);

        Assert.IsTrue(restored);
        Assert.IsFalse(File.Exists(SystemProxy.StateFileName));
    }

    [TestMethod]
    public void RecoverFromPreviousRunRestoresOldProxyWhenCurrentProxyPointsToThisApp()
    {
        var oldStatus = new SystemProxyStatus(true, true, false, false, "corp-proxy:8080", "localhost", string.Empty);
        var firstRunHost = new FakeProxyHost { State = DirectStatus() };
        SystemProxy.SetBackendFactoryForTesting(() => new FakeWindowsProxyBackend(firstRunHost), oldStatus);
        var config = new Configuration
        {
            SysProxyMode = ProxyMode.Global,
            LocalPort = 1080
        };
        Assert.IsTrue(SystemProxy.Update(config, null));
        SystemProxy.ResetBackendFactoryForTesting();

        var secondRunHost = new FakeProxyHost { State = GlobalStatus(1080) };
        SystemProxy.SetBackendFactoryForTesting(() => new FakeWindowsProxyBackend(secondRunHost));

        var recovered = SystemProxy.RecoverFromPreviousRun(1080);

        Assert.IsTrue(recovered);
        Assert.AreEqual(1, secondRunHost.GlobalCalls);
        Assert.AreEqual("corp-proxy:8080", secondRunHost.State.ProxyServer);
        Assert.AreEqual("localhost", secondRunHost.State.ProxyBypass);
        Assert.IsFalse(File.Exists(SystemProxy.StateFileName));
    }

    [TestMethod]
    public void RecoverFromPreviousRunDoesNotChangeUserModifiedProxy()
    {
        var oldStatus = DirectStatus();
        var firstRunHost = new FakeProxyHost { State = DirectStatus() };
        SystemProxy.SetBackendFactoryForTesting(() => new FakeWindowsProxyBackend(firstRunHost), oldStatus);
        var config = new Configuration
        {
            SysProxyMode = ProxyMode.Global,
            LocalPort = 1080
        };
        Assert.IsTrue(SystemProxy.Update(config, null));
        SystemProxy.ResetBackendFactoryForTesting();

        var userStatus = new SystemProxyStatus(true, true, false, false, "corp-proxy:8080", string.Empty, string.Empty);
        var secondRunHost = new FakeProxyHost { State = userStatus };
        SystemProxy.SetBackendFactoryForTesting(() => new FakeWindowsProxyBackend(secondRunHost));

        var recovered = SystemProxy.RecoverFromPreviousRun(1080);

        Assert.IsTrue(recovered);
        Assert.AreEqual(0, secondRunHost.SetCalls);
        Assert.AreEqual(0, secondRunHost.DirectCalls);
        Assert.AreEqual(0, secondRunHost.GlobalCalls);
        AssertStatus(userStatus, secondRunHost.State);
        Assert.IsFalse(File.Exists(SystemProxy.StateFileName));
    }

    [TestMethod]
    public void RecoverFromPreviousRunFallsBackToDirectWhenSnapshotRestoreFails()
    {
        var oldStatus = new SystemProxyStatus(true, true, false, false, "corp-proxy:8080", "localhost", string.Empty);
        var firstRunHost = new FakeProxyHost { State = DirectStatus() };
        SystemProxy.SetBackendFactoryForTesting(() => new FakeWindowsProxyBackend(firstRunHost), oldStatus);
        var config = new Configuration
        {
            SysProxyMode = ProxyMode.Global,
            LocalPort = 1080
        };
        Assert.IsTrue(SystemProxy.Update(config, null));
        SystemProxy.ResetBackendFactoryForTesting();

        var secondRunHost = new FakeProxyHost { State = GlobalStatus(1080) };
        secondRunHost.GlobalBehaviors.Enqueue(new FakeBehavior(false, false));
        secondRunHost.GlobalBehaviors.Enqueue(new FakeBehavior(false, false));
        secondRunHost.GlobalBehaviors.Enqueue(new FakeBehavior(false, false));
        SystemProxy.SetBackendFactoryForTesting(() => new FakeWindowsProxyBackend(secondRunHost));

        var recovered = SystemProxy.RecoverFromPreviousRun(1080);

        Assert.IsTrue(recovered);
        Assert.AreEqual(3, secondRunHost.GlobalCalls);
        Assert.AreEqual(1, secondRunHost.DirectCalls);
        Assert.IsFalse(secondRunHost.State.IsProxy);
        Assert.IsFalse(File.Exists(SystemProxy.StateFileName));
    }

    [TestMethod]
    public void RecoverFromPreviousRunPreservesCorruptMarker()
    {
        File.WriteAllText(SystemProxy.StateFileName, "{ broken");
        var host = new FakeProxyHost { State = GlobalStatus(1080) };
        SystemProxy.SetBackendFactoryForTesting(() => new FakeWindowsProxyBackend(host));

        var recovered = SystemProxy.RecoverFromPreviousRun(1080);

        Assert.IsTrue(recovered);
        Assert.IsTrue(Directory.GetFiles(Environment.CurrentDirectory, SystemProxy.StateFileName + ".corrupt-*").Any());
        Assert.AreEqual(0, host.DirectCalls);
        Assert.AreEqual(0, host.GlobalCalls);
    }

    [TestMethod]
    public void UpdateDirectSucceedsWhenStatusIsVerified()
    {
        var host = new FakeProxyHost { State = GlobalStatus(1080) };
        SystemProxy.SetBackendFactoryForTesting(() => new FakeWindowsProxyBackend(host), DirectStatus());
        var config = new Configuration
        {
            SysProxyMode = ProxyMode.Direct,
            LocalPort = 1080
        };

        var updated = SystemProxy.Update(config, null);

        Assert.IsTrue(updated);
        Assert.AreEqual(1, host.DirectCalls);
        Assert.AreEqual(0, host.PacCalls);
        Assert.AreEqual(0, host.GlobalCalls);
        AssertStatus(DirectStatus(), host.State);
    }

    [TestMethod]
    public void UpdatePacSucceedsWhenStatusIsVerified()
    {
        var host = new FakeProxyHost { State = DirectStatus() };
        SystemProxy.SetBackendFactoryForTesting(() => new FakeWindowsProxyBackend(host), DirectStatus());
        var config = new Configuration
        {
            SysProxyMode = ProxyMode.Pac,
            LocalPort = 1080
        };
        var pacServer = new PACServer(null);
        pacServer.UpdatePacUrl(config);

        var updated = SystemProxy.Update(config, pacServer);

        Assert.IsTrue(updated);
        Assert.AreEqual(0, host.DirectCalls);
        Assert.AreEqual(1, host.PacCalls);
        Assert.AreEqual(0, host.GlobalCalls);
        Assert.IsTrue(host.State.IsAutoProxyUrl);
        Assert.IsFalse(host.State.IsProxy);
        Assert.AreEqual(pacServer.PacUrl, host.State.AutoConfigUrl);
    }

    private static SystemProxyStatus DirectStatus()
    {
        return new SystemProxyStatus(true, false, false, false, string.Empty, string.Empty, string.Empty);
    }

    private static SystemProxyStatus GlobalStatus(int port)
    {
        return new SystemProxyStatus(true, true, false, false, $@"localhost:{port}", "localhost;127.*;10.*", string.Empty);
    }

    private static void AssertStatus(SystemProxyStatus expected, SystemProxyStatus actual)
    {
        Assert.AreEqual(expected.IsDirect, actual.IsDirect);
        Assert.AreEqual(expected.IsProxy, actual.IsProxy);
        Assert.AreEqual(expected.IsAutoProxyUrl, actual.IsAutoProxyUrl);
        Assert.AreEqual(expected.IsAutoDetect, actual.IsAutoDetect);
        Assert.AreEqual(expected.ProxyServer, actual.ProxyServer);
        Assert.AreEqual(expected.ProxyBypass, actual.ProxyBypass);
        Assert.AreEqual(expected.AutoConfigUrl, actual.AutoConfigUrl);
    }

    private readonly record struct FakeBehavior(bool Result, bool UpdateState);

    private sealed class FakeProxyHost
    {
        public SystemProxyStatus State { get; set; } = DirectStatus();

        public Queue<FakeBehavior> SetBehaviors { get; } = new();

        public Queue<FakeBehavior> DirectBehaviors { get; } = new();

        public Queue<FakeBehavior> GlobalBehaviors { get; } = new();

        public int SetCalls { get; set; }

        public int DirectCalls { get; set; }

        public int PacCalls { get; set; }

        public int GlobalCalls { get; set; }
    }

    private sealed class FakeWindowsProxyBackend : IWindowsProxyBackend
    {
        private readonly FakeProxyHost _host;

        public FakeWindowsProxyBackend(FakeProxyHost host)
        {
            _host = host;
        }

        public string Server { get; set; } = string.Empty;

        public string Bypass { get; set; } = string.Empty;

        public string AutoConfigUrl { get; set; } = string.Empty;

        public string[] LanIp { get; } = { "localhost", "127.*", "10.*" };

        public SystemProxyStatus Query()
        {
            return _host.State;
        }

        public bool Set(SystemProxyStatus status)
        {
            _host.SetCalls++;
            var behavior = Next(_host.SetBehaviors);
            if (behavior.UpdateState)
            {
                _host.State = status;
            }

            return behavior.Result;
        }

        public bool Direct()
        {
            _host.DirectCalls++;
            var behavior = Next(_host.DirectBehaviors);
            if (behavior.UpdateState)
            {
                _host.State = DirectStatus();
            }

            return behavior.Result;
        }

        public bool Pac()
        {
            _host.PacCalls++;
            _host.State = new SystemProxyStatus(true, false, true, false, string.Empty, string.Empty, AutoConfigUrl);
            return true;
        }

        public bool Global()
        {
            _host.GlobalCalls++;
            var behavior = Next(_host.GlobalBehaviors);
            if (behavior.UpdateState)
            {
                _host.State = new SystemProxyStatus(true, true, false, false, Server, Bypass, string.Empty);
            }

            return behavior.Result;
        }

        public void Dispose()
        {
        }

        private static FakeBehavior Next(Queue<FakeBehavior> behaviors)
        {
            return behaviors.Count > 0 ? behaviors.Dequeue() : new FakeBehavior(true, true);
        }
    }

    private sealed class TempCurrentDirectory : IDisposable
    {
        private readonly string _originalDirectory;
        private readonly string _tempDirectory;

        public TempCurrentDirectory()
        {
            _originalDirectory = Directory.GetCurrentDirectory();
            _tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDirectory);
            Directory.SetCurrentDirectory(_tempDirectory);
        }

        public void Dispose()
        {
            Directory.SetCurrentDirectory(_originalDirectory);
            try
            {
                Directory.Delete(_tempDirectory, true);
            }
            catch
            {
                // Tests must not fail because another process briefly holds a temp file.
            }
        }
    }
}
