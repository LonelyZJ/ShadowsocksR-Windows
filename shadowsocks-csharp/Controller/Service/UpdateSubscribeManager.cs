using Microsoft.VisualStudio.Threading;
using Shadowsocks.Controller.HttpRequest;
using Shadowsocks.Enums;
using Shadowsocks.Model;
using Shadowsocks.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Controller.Service
{
    public enum SubscribeUpdateScheduleStatus
    {
        NoSubscriptions,
        Started,
        Queued,
        AlreadyQueued
    }

    public sealed class SubscribeUpdateEventArgs : EventArgs
    {
        public SubscribeUpdateEventArgs(ServerSubscribe subscribe, bool notify, bool manual)
        {
            Subscribe = subscribe;
            Notify = notify;
            Manual = manual;
        }

        public ServerSubscribe Subscribe { get; }

        public bool Notify { get; }

        public bool Manual { get; }
    }

    public sealed class SubscribeUpdateResult : EventArgs
    {
        public ServerSubscribe Subscribe { get; init; }

        public bool Notify { get; set; }

        public bool Success { get; init; }

        public string ErrorMessage { get; init; } = string.Empty;

        public int ParsedCount { get; init; }

        public int AddedCount { get; init; }

        public int RemovedCount { get; init; }

        public int UpdatedCount { get; init; }

        public bool NoChange { get; init; }

        public string GroupName { get; init; } = string.Empty;
    }

    public sealed class SubscribeUpdateSummaryEventArgs : EventArgs
    {
        public bool Notify { get; init; }

        public int SuccessCount { get; init; }

        public int FailureCount { get; init; }

        public int ParsedCount { get; init; }

        public int AddedCount { get; init; }

        public int RemovedCount { get; init; }

        public int UpdatedCount { get; init; }
    }

    public class UpdateSubscribeManager
    {
        private readonly object _lock = new();
        private readonly Queue<SubscribeUpdateRequest> _queue = new();
        private readonly HashSet<string> _queuedAutoSubscribeKeys = new(StringComparer.OrdinalIgnoreCase);

        private Configuration _config;
        private UpdateNode _updater;
        private bool _running;
        private bool _summaryNotify;
        private int _successCount;
        private int _failureCount;
        private int _parsedCount;
        private int _addedCount;
        private int _removedCount;
        private int _updatedCount;

        public event EventHandler<SubscribeUpdateEventArgs> UpdateStarted;

        public event EventHandler<SubscribeUpdateResult> SubscribeCompleted;

        public event EventHandler<SubscribeUpdateSummaryEventArgs> AllCompleted;

        public event EventHandler<SubscribeUpdateResult> UpdateFailed;

        public SubscribeUpdateScheduleStatus CreateTask(Configuration config, UpdateNode updater, bool updateManually, List<ServerSubscribe> serverSubscribe = null)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (updater == null)
            {
                throw new ArgumentNullException(nameof(updater));
            }

            var requests = BuildRequests(config, updateManually, serverSubscribe);
            if (requests.Count == 0)
            {
                return SubscribeUpdateScheduleStatus.NoSubscriptions;
            }

            var status = SubscribeUpdateScheduleStatus.Started;
            var shouldStart = false;
            lock (_lock)
            {
                _config ??= config;
                _updater ??= updater;

                if (_running)
                {
                    status = SubscribeUpdateScheduleStatus.AlreadyQueued;
                }
                else
                {
                    ResetSummary();
                    _running = true;
                    shouldStart = true;
                }

                var added = EnqueueRequests(requests, updateManually);
                if (_running && !shouldStart && added > 0)
                {
                    status = SubscribeUpdateScheduleStatus.Queued;
                }
            }

            if (shouldStart)
            {
                ProcessQueueAsync().Forget();
            }

            return status;
        }

        public ServerSubscribe CurrentServerSubscribe { get; private set; }

        private async Task ProcessQueueAsync()
        {
            while (true)
            {
                SubscribeUpdateRequest request = default;
                SubscribeUpdateSummaryEventArgs summary = null;
                lock (_lock)
                {
                    if (_queue.Count == 0)
                    {
                        summary = CreateSummary();
                        CurrentServerSubscribe = null;
                        _config = null;
                        _updater = null;
                        _running = false;
                    }
                    else
                    {
                        request = _queue.Dequeue();
                        if (!request.Manual)
                        {
                            _queuedAutoSubscribeKeys.Remove(GetSubscribeKey(request.Subscribe));
                        }

                        CurrentServerSubscribe = request.Subscribe;
                    }
                }

                if (summary != null)
                {
                    AllCompleted?.Invoke(this, summary);
                    return;
                }

                UpdateStarted?.Invoke(this, new SubscribeUpdateEventArgs(request.Subscribe, request.Notify, request.Manual));

                SubscribeUpdateResult result;
                try
                {
                    var content = await _updater.CheckUpdateAsync(_config, request.Subscribe, request.Notify, CancellationToken.None);
                    result = ApplySubscribeUpdate(_config, request.Subscribe, content);
                    result.Notify = request.Notify;
                }
                catch (Exception e)
                {
                    Logging.LogUsefulException(e);
                    result = new SubscribeUpdateResult
                    {
                        Subscribe = request.Subscribe,
                        Notify = request.Notify,
                        Success = false,
                        ErrorMessage = e.Message,
                        GroupName = request.Subscribe.Tag
                    };
                }

                RecordResult(result);
                SubscribeCompleted?.Invoke(this, result);
                if (!result.Success)
                {
                    Logging.Log(LogLevel.Warn, $@"Subscription update failed: {result.GroupName}. {result.ErrorMessage}");
                    UpdateFailed?.Invoke(this, result);
                }
            }
        }

        private static List<SubscribeUpdateRequest> BuildRequests(Configuration config, bool updateManually, List<ServerSubscribe> serverSubscribe)
        {
            var subscribes = serverSubscribe?.Count > 0
                ? serverSubscribe
                : updateManually
                    ? config.ServerSubscribes
                    : config.ServerSubscribes.Where(server => server.AutoCheckUpdate).ToList();

            return subscribes
                .Where(subscribe => subscribe != null)
                .Select(subscribe => new SubscribeUpdateRequest(subscribe, updateManually, updateManually))
                .ToList();
        }

        private int EnqueueRequests(IEnumerable<SubscribeUpdateRequest> requests, bool updateManually)
        {
            var added = 0;
            foreach (var request in requests)
            {
                if (!updateManually)
                {
                    var key = GetSubscribeKey(request.Subscribe);
                    if (CurrentServerSubscribe != null && SameSubscribe(CurrentServerSubscribe, request.Subscribe)
                        || !_queuedAutoSubscribeKeys.Add(key))
                    {
                        continue;
                    }
                }

                _queue.Enqueue(request);
                added++;
            }

            return added;
        }

        internal static SubscribeUpdateResult ApplySubscribeUpdate(Configuration config, ServerSubscribe subscribe, string rawResult)
        {
            var resultBuilder = new SubscribeUpdateResultBuilder(subscribe);
            if (string.IsNullOrWhiteSpace(rawResult))
            {
                return resultBuilder.Failure("Subscription response is empty.");
            }

            var content = rawResult.TrimEnd('\r', '\n', ' ');
            try
            {
                content = Base64.DecodeBase64(content);
            }
            catch (FormatException e)
            {
                return resultBuilder.Failure(e.Message);
            }

            var urls = content.GetLines().Where(url => url.StartsWith(@"ssr://", StringComparison.OrdinalIgnoreCase)).ToList();
            if (urls.Count == 0)
            {
                return resultBuilder.Failure("Subscription response contains no SSR links.");
            }

            var lastGroup = ResolveSubscribeGroup(config, subscribe, urls);
            resultBuilder.GroupName = lastGroup;

            var selectedServer = config.Configs.ElementAtOrDefault(config.Index);
            var firstInsertIndex = config.Configs.Count;
            var oldServers = config.Configs.FindAll(server => server.SubTag == lastGroup);
            var index = config.Configs.FindIndex(server => server.SubTag == lastGroup);
            if (index >= 0)
            {
                firstInsertIndex = index;
            }

            var newServers = new List<Server>();
            foreach (var url in urls)
            {
                try
                {
                    var server = new Server(url, lastGroup) { Index = firstInsertIndex++ };
                    newServers.Add(server);
                }
                catch
                {
                    // Keep processing the rest of this subscription.
                }
            }

            foreach (var newServer in newServers.Where(newServer => string.IsNullOrEmpty(newServer.Group)))
            {
                newServer.Group = lastGroup;
            }

            if (newServers.Count == 0)
            {
                return resultBuilder.Failure("Subscription response contains no valid SSR links.");
            }

            var removeServers = oldServers.Except(newServers).ToList();
            var addServers = newServers.Except(oldServers).ToList();
            var updatedCount = newServers.Count - addServers.Count;

            foreach (var server in removeServers)
            {
                server.Connections.CloseAll();
                config.Configs.Remove(server);
            }

            foreach (var server in addServers)
            {
                if (server.Index > config.Configs.Count)
                {
                    server.Index = config.Configs.Count;
                }

                config.Configs.Insert(server.Index, server);
            }

            RestoreSelectedServer(config, selectedServer);

            foreach (var serverSubscribe in config.ServerSubscribes.Where(serverSubscribe => serverSubscribe.Url == subscribe.Url))
            {
                serverSubscribe.LastUpdateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }

            var defaultServer = new Server();
            config.Configs.RemoveAll(server => server.IsMatchServer(defaultServer));
            if (ReferenceEquals(config, Global.GuiConfig))
            {
                Global.SaveConfig();
            }

            return resultBuilder.Success(
                newServers.Count,
                addServers.Count,
                removeServers.Count,
                updatedCount,
                addServers.Count == 0 && removeServers.Count == 0);
        }

        private static string ResolveSubscribeGroup(Configuration config, ServerSubscribe subscribe, List<string> urls)
        {
            var lastGroup = subscribe.OriginTag;
            if (string.IsNullOrEmpty(lastGroup))
            {
                foreach (var url in urls)
                {
                    try
                    {
                        var server = new Server(url, null);
                        if (string.IsNullOrEmpty(server.Group)
                            || config.ServerSubscribes.Any(existing => existing.Tag == server.Group))
                        {
                            continue;
                        }

                        var serverSubscribe = config.ServerSubscribes.Find(sub =>
                            sub.Url == subscribe.Url && string.IsNullOrEmpty(sub.OriginTag));

                        if (serverSubscribe != null)
                        {
                            lastGroup = serverSubscribe.Tag = server.Group;
                        }

                        break;
                    }
                    catch
                    {
                        // Ignore invalid links while trying to infer a group name.
                    }
                }
            }

            return string.IsNullOrEmpty(lastGroup) ? subscribe.UrlMd5 : lastGroup;
        }

        private static void RestoreSelectedServer(Configuration config, Server selectedServer)
        {
            var selectedIndex = -1;
            if (selectedServer is not null)
            {
                selectedIndex = config.Configs.FindIndex(server => server.Id == selectedServer.Id);

                if (selectedIndex < 0)
                {
                    selectedIndex = config.Configs.FindIndex(server =>
                        server.SubTag == selectedServer.SubTag && server.IsMatchServer(selectedServer)
                    );
                }

                if (selectedIndex < 0)
                {
                    selectedIndex = config.Configs.FindIndex(server =>
                        server.SubTag == selectedServer.SubTag
                        && server.Group == selectedServer.Group
                        && server.Remarks == selectedServer.Remarks
                    );
                }

                if (selectedIndex < 0)
                {
                    selectedIndex = config.Configs.FindIndex(server =>
                        server.SubTag == selectedServer.SubTag
                        && server.Group == selectedServer.Group
                    );
                }

                if (selectedIndex < 0)
                {
                    selectedIndex = config.Configs.FindIndex(server => server.SubTag == selectedServer.SubTag);
                }
            }

            config.Index = selectedIndex < 0 ? default : selectedIndex;
        }

        private void RecordResult(SubscribeUpdateResult result)
        {
            lock (_lock)
            {
                _summaryNotify |= result.Notify;
                if (result.Success)
                {
                    _successCount++;
                }
                else
                {
                    _failureCount++;
                }

                _parsedCount += result.ParsedCount;
                _addedCount += result.AddedCount;
                _removedCount += result.RemovedCount;
                _updatedCount += result.UpdatedCount;
            }
        }

        private SubscribeUpdateSummaryEventArgs CreateSummary()
        {
            return new SubscribeUpdateSummaryEventArgs
            {
                Notify = _summaryNotify,
                SuccessCount = _successCount,
                FailureCount = _failureCount,
                ParsedCount = _parsedCount,
                AddedCount = _addedCount,
                RemovedCount = _removedCount,
                UpdatedCount = _updatedCount
            };
        }

        private void ResetSummary()
        {
            _summaryNotify = false;
            _successCount = 0;
            _failureCount = 0;
            _parsedCount = 0;
            _addedCount = 0;
            _removedCount = 0;
            _updatedCount = 0;
        }

        private static string GetSubscribeKey(ServerSubscribe subscribe)
        {
            return subscribe?.Url ?? string.Empty;
        }

        private static bool SameSubscribe(ServerSubscribe left, ServerSubscribe right)
        {
            return string.Equals(GetSubscribeKey(left), GetSubscribeKey(right), StringComparison.OrdinalIgnoreCase);
        }

        private readonly record struct SubscribeUpdateRequest(ServerSubscribe Subscribe, bool Notify, bool Manual);

        private sealed class SubscribeUpdateResultBuilder
        {
            private readonly ServerSubscribe _subscribe;

            public SubscribeUpdateResultBuilder(ServerSubscribe subscribe)
            {
                _subscribe = subscribe;
                GroupName = subscribe?.Tag ?? string.Empty;
            }

            public string GroupName { get; set; }

            public SubscribeUpdateResult Failure(string errorMessage)
            {
                return new SubscribeUpdateResult
                {
                    Subscribe = _subscribe,
                    Success = false,
                    ErrorMessage = errorMessage,
                    GroupName = GroupName
                };
            }

            public SubscribeUpdateResult Success(int parsedCount, int addedCount, int removedCount, int updatedCount, bool noChange)
            {
                return new SubscribeUpdateResult
                {
                    Subscribe = _subscribe,
                    Success = true,
                    ParsedCount = parsedCount,
                    AddedCount = addedCount,
                    RemovedCount = removedCount,
                    UpdatedCount = updatedCount,
                    NoChange = noChange,
                    GroupName = GroupName
                };
            }
        }
    }
}
