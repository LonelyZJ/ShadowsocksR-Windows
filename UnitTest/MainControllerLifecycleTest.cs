using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller;
using Shadowsocks.Enums;
using Shadowsocks.Model;
using System;
using System.Collections.Generic;
using System.IO;

namespace UnitTest;

[TestClass]
public class MainControllerLifecycleTest
{
    [TestCleanup]
    public void Cleanup()
    {
        SystemProxy.ResetBackendFactoryForTesting();
    }

    [TestMethod]
    public void StopDoesNotMarkControllerStoppedWhenProxyRestoreFails()
    {
        using var _ = new TempCurrentDirectory();
        var host = new FakeProxyHost { State = DirectStatus() };
        host.SetBehaviors.Enqueue(new FakeBehavior(false, false));
        host.SetBehaviors.Enqueue(new FakeBehavior(false, false));
        host.SetBehaviors.Enqueue(new FakeBehavior(false, false));
        SystemProxy.SetBackendFactoryForTesting(() => new FakeWindowsProxyBackend(host), DirectStatus());
        Global.GuiConfig = new Configuration
        {
            SysProxyMode = ProxyMode.Global,
            LocalPort = 1080
        };
        var controller = new MainController();

        var stopped = controller.Stop();

        Assert.IsFalse(stopped);
        Assert.IsFalse(controller.IsStoppedForTesting);
        Assert.AreEqual(3, host.SetCalls);
    }

    [TestMethod]
    public void ShutdownPreventsLaterToggleModeFromUpdatingSystemProxy()
    {
        using var _ = new TempCurrentDirectory();
        var host = new FakeProxyHost { State = GlobalStatus(1080) };
        SystemProxy.SetBackendFactoryForTesting(() => new FakeWindowsProxyBackend(host), DirectStatus());
        Global.GuiConfig = new Configuration
        {
            SysProxyMode = ProxyMode.Global,
            LocalPort = 1080
        };
        var controller = new MainController();

        var shutdown = controller.Shutdown();
        controller.ToggleMode(ProxyMode.Direct);

        Assert.IsTrue(shutdown);
        Assert.IsTrue(controller.IsShuttingDownForTesting);
        Assert.AreEqual(ProxyMode.Global, Global.GuiConfig.SysProxyMode);
        Assert.AreEqual(1, host.SetCalls);
        Assert.AreEqual(0, host.DirectCalls);
    }

    private static SystemProxyStatus DirectStatus()
    {
        return new SystemProxyStatus(true, false, false, false, string.Empty, string.Empty, string.Empty);
    }

    private static SystemProxyStatus GlobalStatus(int port)
    {
        return new SystemProxyStatus(true, true, false, false, $@"localhost:{port}", "localhost;127.*;10.*", string.Empty);
    }

    private readonly record struct FakeBehavior(bool Result, bool UpdateState);

    private sealed class FakeProxyHost
    {
        public SystemProxyStatus State { get; set; } = DirectStatus();

        public Queue<FakeBehavior> SetBehaviors { get; } = new();

        public int SetCalls { get; set; }

        public int DirectCalls { get; set; }
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
            var behavior = _host.SetBehaviors.Count > 0
                ? _host.SetBehaviors.Dequeue()
                : new FakeBehavior(true, true);
            if (behavior.UpdateState)
            {
                _host.State = status;
            }

            return behavior.Result;
        }

        public bool Direct()
        {
            _host.DirectCalls++;
            _host.State = DirectStatus();
            return true;
        }

        public bool Pac()
        {
            return true;
        }

        public bool Global()
        {
            _host.State = new SystemProxyStatus(true, true, false, false, Server, Bypass, string.Empty);
            return true;
        }

        public void Dispose()
        {
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
                // The transfer log save is asynchronous; leaving a temp directory is safer than racing it.
            }
        }
    }
}
