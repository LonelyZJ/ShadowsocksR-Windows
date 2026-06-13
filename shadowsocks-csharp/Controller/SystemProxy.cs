using Shadowsocks.Controller.Service;
using Shadowsocks.Enums;
using Shadowsocks.Model;
using System;
using System.Linq;
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

    private static readonly object Lock = new();
    private static Func<IWindowsProxyBackend> _backendFactory = static () => new WindowsProxyBackend();
    private static SystemProxyStatus _old;
    private static bool _initialized;
    private static bool _initialStatusAvailable;

    public static bool Restore(int localPort = 0)
    {
        lock (Lock)
        {
            EnsureInitialized();

            if (!_initialStatusAvailable || _old == null)
            {
                Logging.Log(LogLevel.Warn, "System proxy initial status unavailable; falling back to direct mode.");
                return ApplyDirectFallback(localPort);
            }

            if (ApplyWithRetry("restore system proxy", backend => backend.Set(_old), actual => Matches(actual, _old)))
            {
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
                return ApplyDirectFallback(localPort);
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
            return sysProxyMode switch
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
