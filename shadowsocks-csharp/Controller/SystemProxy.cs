using Shadowsocks.Controller.Service;
using Shadowsocks.Enums;
using Shadowsocks.Model;
using Shadowsocks.Util;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using WindowsProxy;

namespace Shadowsocks.Controller;

public sealed class SystemProxyStatus
{
    internal SystemProxyStatus(ProxyStatus nativeStatus)
        : this(
            nativeStatus.IsDirect,
            nativeStatus.IsProxy,
            nativeStatus.IsAutoProxyUrl,
            nativeStatus.IsAutoDetect,
            nativeStatus.ProxyServer,
            nativeStatus.ProxyBypass,
            nativeStatus.AutoConfigUrl,
            nativeStatus)
    {
    }

    public SystemProxyStatus(
        bool isDirect,
        bool isProxy,
        bool isAutoProxyUrl,
        bool isAutoDetect,
        string proxyServer,
        string proxyBypass,
        string autoConfigUrl)
        : this(isDirect, isProxy, isAutoProxyUrl, isAutoDetect, proxyServer, proxyBypass, autoConfigUrl, null)
    {
    }

    private SystemProxyStatus(
        bool isDirect,
        bool isProxy,
        bool isAutoProxyUrl,
        bool isAutoDetect,
        string proxyServer,
        string proxyBypass,
        string autoConfigUrl,
        ProxyStatus nativeStatus)
    {
        IsDirect = isDirect;
        IsProxy = isProxy;
        IsAutoProxyUrl = isAutoProxyUrl;
        IsAutoDetect = isAutoDetect;
        ProxyServer = proxyServer ?? string.Empty;
        ProxyBypass = proxyBypass ?? string.Empty;
        AutoConfigUrl = autoConfigUrl ?? string.Empty;
        NativeStatus = nativeStatus;
    }

    public bool IsDirect { get; }

    public bool IsProxy { get; }

    public bool IsAutoProxyUrl { get; }

    public bool IsAutoDetect { get; }

    public string ProxyServer { get; }

    public string ProxyBypass { get; }

    public string AutoConfigUrl { get; }

    internal ProxyStatus NativeStatus { get; }

    public override string ToString()
    {
        return $@"Direct={IsDirect}, Proxy={IsProxy}, AutoProxyUrl={IsAutoProxyUrl}, AutoDetect={IsAutoDetect}, Server='{ProxyServer}', Bypass='{ProxyBypass}', AutoConfigUrl='{AutoConfigUrl}'";
    }
}

public interface IWindowsProxyBackend : IDisposable
{
    string Server { get; set; }

    string Bypass { get; set; }

    string AutoConfigUrl { get; set; }

    string[] LanIp { get; }

    SystemProxyStatus Query();

    bool Set(SystemProxyStatus status);

    bool Direct();

    bool Pac();

    bool Global();
}

public static class SystemProxy
{
    private const int RetryCount = 3;
    private const int RetryDelayMilliseconds = 100;
    internal const string StateFileName = @"system-proxy-state.json";
    private static readonly Encoding StateEncoding = new UTF8Encoding(false);

    private static readonly object Lock = new();
    private static Func<IWindowsProxyBackend> _backendFactory = static () => new WindowsProxyBackend();
    private static SystemProxyStatus _old;
    private static bool _initialized;
    private static bool _initialStatusAvailable;
    private static bool _currentProcessHasWrittenState;

    public static bool Restore(int localPort = 0)
    {
        lock (Lock)
        {
            EnsureInitialized();

            if (!_initialStatusAvailable || _old == null)
            {
                Logging.Log(LogLevel.Warn, "System proxy initial status unavailable; falling back to direct mode.");
                var fallback = ApplyDirectFallback(localPort);
                if (fallback)
                {
                    DeleteStateMarker();
                }

                return fallback;
            }

            if (ApplyWithRetry("restore system proxy", backend => backend.Set(_old), actual => Matches(actual, _old)))
            {
                DeleteStateMarker();
                return true;
            }

            SystemProxyStatus current = null;
            try
            {
                using var backend = CreateBackend();
                current = backend.Query();
            }
            catch (Exception e)
            {
                Logging.LogUsefulException(e);
            }

            if (IsCurrentAppProxy(current, localPort))
            {
                Logging.Log(LogLevel.Warn, $@"System proxy restore failed and proxy still points to this app: {current}");
                var fallback = ApplyDirectFallback(localPort);
                if (fallback)
                {
                    DeleteStateMarker();
                }

                return fallback;
            }

            return false;
        }
    }

    public static bool RecoverFromPreviousRun(int configuredLocalPort = 0)
    {
        lock (Lock)
        {
            if (_currentProcessHasWrittenState || !TryLoadStateMarker(out var marker))
            {
                return true;
            }

            if (!Same(marker.AppId, GetAppId()))
            {
                Logging.Info("Discarding stale system proxy marker from another app directory.");
                DeleteStateMarker();
                return true;
            }

            var localPort = marker.LocalPort > 0 ? marker.LocalPort : configuredLocalPort;
            var current = QueryCurrentStatus();
            if (!IsCurrentAppProxy(current, localPort) && !IsCurrentAppPac(current, localPort, marker.PacUrl))
            {
                Logging.Info("Discarding stale system proxy marker because current proxy no longer points to this app.");
                DeleteStateMarker();
                return true;
            }

            if (marker.OldStatus != null && ApplySnapshotWithRetry(marker.OldStatus))
            {
                Logging.Info("Recovered stale system proxy from startup marker.");
                DeleteStateMarker();
                return true;
            }

            current = QueryCurrentStatus();
            if (IsCurrentAppProxy(current, localPort) || IsCurrentAppPac(current, localPort, marker.PacUrl))
            {
                Logging.Log(LogLevel.Warn, "System proxy marker restore failed; falling back to direct mode.");
                var fallback = ApplyDirectFallback(localPort);
                if (fallback)
                {
                    DeleteStateMarker();
                }

                return fallback;
            }

            return false;
        }
    }

    public static bool Update(Configuration config, PACServer pacSrv)
    {
        lock (Lock)
        {
            EnsureInitialized();

            var sysProxyMode = config.SysProxyMode;
            var globalBypass = string.Empty;
            var updated = sysProxyMode switch
            {
                ProxyMode.Direct => ApplyWithRetry(
                    "set system proxy direct",
                    backend => backend.Direct(),
                    IsDirect),
                ProxyMode.Pac => ApplyWithRetry(
                    "set system proxy PAC",
                    backend =>
                    {
                        backend.AutoConfigUrl = pacSrv?.PacUrl ?? string.Empty;
                        return backend.Pac();
                    },
                    actual => IsPac(actual, pacSrv?.PacUrl ?? string.Empty)),
                ProxyMode.Global => ApplyWithRetry(
                    "set system proxy global",
                    backend =>
                    {
                        backend.Server = $@"localhost:{config.LocalPort}";
                        globalBypass = string.Join(';', backend.LanIp);
                        backend.Bypass = globalBypass;
                        return backend.Global();
                    },
                    actual => IsGlobal(actual, config.LocalPort, globalBypass)),
                _ => true
            };

            if (updated)
            {
                if (sysProxyMode is ProxyMode.Pac or ProxyMode.Global)
                {
                    WriteStateMarker(config.LocalPort, sysProxyMode, pacSrv?.PacUrl ?? string.Empty);
                }
                else if (sysProxyMode is ProxyMode.Direct)
                {
                    DeleteStateMarker();
                }
            }

            return updated;
        }
    }

    internal static void SetBackendFactoryForTesting(Func<IWindowsProxyBackend> backendFactory, SystemProxyStatus initialStatus = null)
    {
        lock (Lock)
        {
            _backendFactory = backendFactory ?? throw new ArgumentNullException(nameof(backendFactory));
            _old = initialStatus;
            _initialStatusAvailable = initialStatus != null;
            _initialized = true;
        }
    }

    internal static void ResetBackendFactoryForTesting()
    {
        lock (Lock)
        {
            _backendFactory = static () => new WindowsProxyBackend();
            _old = null;
            _initialStatusAvailable = false;
            _initialized = false;
            _currentProcessHasWrittenState = false;
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            using var backend = CreateBackend();
            _old = backend.Query();
            _initialStatusAvailable = true;
            Logging.Info($@"Captured initial system proxy status: {_old}");
        }
        catch (Exception e)
        {
            _old = null;
            _initialStatusAvailable = false;
            Logging.Log(LogLevel.Warn, "System proxy initial status unavailable.");
            Logging.LogUsefulException(e);
        }
        finally
        {
            _initialized = true;
        }
    }

    private static bool ApplyDirectFallback(int localPort)
    {
        return ApplyWithRetry("set system proxy direct fallback", backend => backend.Direct(), actual => IsDirect(actual) && !IsCurrentAppProxy(actual, localPort));
    }

    private static bool ApplySnapshotWithRetry(SystemProxyStatusSnapshot snapshot)
    {
        if (snapshot.IsAutoDetect)
        {
            Logging.Log(LogLevel.Warn, "Cannot exactly restore auto-detect system proxy mode from marker.");
            return false;
        }

        return ApplyWithRetry(
            "restore system proxy from marker",
            backend => ApplySnapshot(backend, snapshot),
            actual => Matches(actual, snapshot.ToStatus()));
    }

    private static bool ApplySnapshot(IWindowsProxyBackend backend, SystemProxyStatusSnapshot snapshot)
    {
        if (snapshot.IsProxy)
        {
            backend.Server = snapshot.ProxyServer;
            backend.Bypass = snapshot.ProxyBypass;
            return backend.Global();
        }

        if (snapshot.IsAutoProxyUrl)
        {
            backend.AutoConfigUrl = snapshot.AutoConfigUrl;
            return backend.Pac();
        }

        return backend.Direct();
    }

    private static bool ApplyWithRetry(string operation, Func<IWindowsProxyBackend, bool> apply, Func<SystemProxyStatus, bool> verify)
    {
        for (var attempt = 1; attempt <= RetryCount; attempt++)
        {
            try
            {
                using var backend = CreateBackend();
                var applied = apply(backend);
                var actual = backend.Query();

                if (applied && verify(actual))
                {
                    if (attempt > 1)
                    {
                        Logging.Info($@"{operation} succeeded on attempt {attempt}: {actual}");
                    }
                    return true;
                }

                Logging.Log(LogLevel.Warn, $@"{operation} failed on attempt {attempt}. Applied={applied}, actual={actual}");
            }
            catch (Exception e)
            {
                Logging.Log(LogLevel.Warn, $@"{operation} threw on attempt {attempt}.");
                Logging.LogUsefulException(e);
            }

            if (attempt < RetryCount)
            {
                Thread.Sleep(RetryDelayMilliseconds);
            }
        }

        return false;
    }

    private static IWindowsProxyBackend CreateBackend()
    {
        return _backendFactory();
    }

    private static bool Matches(SystemProxyStatus actual, SystemProxyStatus expected)
    {
        return actual != null
               && expected != null
               && actual.IsDirect == expected.IsDirect
               && actual.IsProxy == expected.IsProxy
               && actual.IsAutoProxyUrl == expected.IsAutoProxyUrl
               && actual.IsAutoDetect == expected.IsAutoDetect
               && Same(actual.ProxyServer, expected.ProxyServer)
               && Same(actual.ProxyBypass, expected.ProxyBypass)
               && Same(actual.AutoConfigUrl, expected.AutoConfigUrl);
    }

    private static bool IsDirect(SystemProxyStatus status)
    {
        return status is { IsDirect: true, IsProxy: false, IsAutoProxyUrl: false };
    }

    private static bool IsPac(SystemProxyStatus status, string pacUrl)
    {
        return status is { IsAutoProxyUrl: true, IsProxy: false }
               && Same(status.AutoConfigUrl, pacUrl);
    }

    private static bool IsGlobal(SystemProxyStatus status, int localPort, string bypass)
    {
        return status is { IsProxy: true }
               && IsCurrentAppProxy(status, localPort)
               && Same(status.ProxyBypass, bypass);
    }

    private static bool Same(string left, string right)
    {
        return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCurrentAppProxy(SystemProxyStatus status, int localPort)
    {
        if (status == null || !status.IsProxy || string.IsNullOrWhiteSpace(status.ProxyServer))
        {
            return false;
        }

        var entries = status.ProxyServer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return entries.Any(entry => IsCurrentAppProxyEntry(entry, localPort));
    }

    private static bool IsCurrentAppPac(SystemProxyStatus status, int localPort, string expectedPacUrl)
    {
        if (status == null || !status.IsAutoProxyUrl || string.IsNullOrWhiteSpace(status.AutoConfigUrl))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectedPacUrl) && Same(status.AutoConfigUrl, expectedPacUrl))
        {
            return true;
        }

        if (!Uri.TryCreate(status.AutoConfigUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.Trim('[', ']');
        var isLocalHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                          || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                          || host.Equals("::1", StringComparison.OrdinalIgnoreCase);

        return isLocalHost
               && (localPort <= 0 || uri.Port == localPort)
               && uri.AbsolutePath.Equals("/pac", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCurrentAppProxyEntry(string entry, int localPort)
    {
        var server = entry;
        var equalsIndex = server.IndexOf('=');
        if (equalsIndex >= 0)
        {
            server = server[(equalsIndex + 1)..];
        }

        server = server.Trim();
        if (server.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || server.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || server.StartsWith("socks=", StringComparison.OrdinalIgnoreCase))
        {
            server = server.Replace("socks=", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        if (!server.Contains("://", StringComparison.Ordinal))
        {
            server = $@"http://{server}";
        }

        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.Trim('[', ']');
        var isLocalHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                          || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                          || host.Equals("::1", StringComparison.OrdinalIgnoreCase);

        return isLocalHost && (localPort <= 0 || uri.Port == localPort);
    }

    private static SystemProxyStatus QueryCurrentStatus()
    {
        try
        {
            using var backend = CreateBackend();
            return backend.Query();
        }
        catch (Exception e)
        {
            Logging.LogUsefulException(e);
            return null;
        }
    }

    private static bool TryLoadStateMarker(out SystemProxyStateMarker marker)
    {
        marker = null;
        if (!File.Exists(StateFilePath))
        {
            return false;
        }

        if (AtomicFile.TryReadValidJson<SystemProxyStateMarker>(StateFilePath, out marker, out var error)
            && marker != null)
        {
            return true;
        }

        Logging.Log(LogLevel.Warn, $@"System proxy marker is unreadable: {error}");
        AtomicFile.PreserveCorruptFile(StateFilePath);
        return false;
    }

    private static void WriteStateMarker(int localPort, ProxyMode mode, string pacUrl)
    {
        try
        {
            var marker = new SystemProxyStateMarker
            {
                LocalPort = localPort,
                Mode = mode,
                UpdatedAt = DateTimeOffset.UtcNow,
                OldStatus = _initialStatusAvailable ? SystemProxyStatusSnapshot.FromStatus(_old) : null,
                AppId = GetAppId(),
                PacUrl = pacUrl
            };
            AtomicFile.WriteAllTextAtomic(StateFilePath, JsonUtils.Serialize(marker, true), StateEncoding);
            _currentProcessHasWrittenState = true;
        }
        catch (Exception e)
        {
            Logging.Log(LogLevel.Warn, "Failed to write system proxy marker.");
            Logging.LogUsefulException(e);
        }
    }

    private static void DeleteStateMarker()
    {
        try
        {
            if (File.Exists(StateFilePath))
            {
                File.Delete(StateFilePath);
            }
        }
        catch (Exception e)
        {
            Logging.LogUsefulException(e);
        }
        finally
        {
            _currentProcessHasWrittenState = false;
        }
    }

    private static string StateFilePath => Path.Combine(Directory.GetCurrentDirectory(), StateFileName);

    private static string GetAppId()
    {
        var directory = Path.GetFullPath(Directory.GetCurrentDirectory()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant()));
        return Convert.ToHexString(bytes);
    }

    internal sealed class SystemProxyStateMarker
    {
        public int LocalPort { get; set; }

        public ProxyMode Mode { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public SystemProxyStatusSnapshot OldStatus { get; set; }

        public string AppId { get; set; } = string.Empty;

        public string PacUrl { get; set; } = string.Empty;
    }

    internal sealed class SystemProxyStatusSnapshot
    {
        public bool IsDirect { get; set; }

        public bool IsProxy { get; set; }

        public bool IsAutoProxyUrl { get; set; }

        public bool IsAutoDetect { get; set; }

        public string ProxyServer { get; set; } = string.Empty;

        public string ProxyBypass { get; set; } = string.Empty;

        public string AutoConfigUrl { get; set; } = string.Empty;

        public static SystemProxyStatusSnapshot FromStatus(SystemProxyStatus status)
        {
            if (status == null)
            {
                return null;
            }

            return new SystemProxyStatusSnapshot
            {
                IsDirect = status.IsDirect,
                IsProxy = status.IsProxy,
                IsAutoProxyUrl = status.IsAutoProxyUrl,
                IsAutoDetect = status.IsAutoDetect,
                ProxyServer = status.ProxyServer,
                ProxyBypass = status.ProxyBypass,
                AutoConfigUrl = status.AutoConfigUrl
            };
        }

        public SystemProxyStatus ToStatus()
        {
            return new SystemProxyStatus(IsDirect, IsProxy, IsAutoProxyUrl, IsAutoDetect, ProxyServer, ProxyBypass, AutoConfigUrl);
        }
    }

    private sealed class WindowsProxyBackend : IWindowsProxyBackend
    {
        private readonly ProxyService _proxy = new();

        public string Server
        {
            get => _proxy.Server;
            set => _proxy.Server = value;
        }

        public string Bypass
        {
            get => _proxy.Bypass;
            set => _proxy.Bypass = value;
        }

        public string AutoConfigUrl
        {
            get => _proxy.AutoConfigUrl;
            set => _proxy.AutoConfigUrl = value;
        }

        public string[] LanIp => ProxyService.LanIp;

        public SystemProxyStatus Query()
        {
            return new SystemProxyStatus(_proxy.Query());
        }

        public bool Set(SystemProxyStatus status)
        {
            if (status?.NativeStatus == null)
            {
                throw new InvalidOperationException("Cannot restore a system proxy status that was not captured from Windows.");
            }

            return _proxy.Set(status.NativeStatus);
        }

        public bool Direct()
        {
            return _proxy.Direct();
        }

        public bool Pac()
        {
            return _proxy.Pac();
        }

        public bool Global()
        {
            return _proxy.Global();
        }

        public void Dispose()
        {
            _proxy.Dispose();
        }
    }
}
