using CryptoBase;
using Microsoft.VisualStudio.Threading;
using Microsoft.Win32;
using Shadowsocks.Controller;
using Shadowsocks.Enums;
using Shadowsocks.Model;
using Shadowsocks.Util;
using SingleInstance;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Utils = Shadowsocks.Util.Utils;

namespace Shadowsocks
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Directory.SetCurrentDirectory(Path.GetDirectoryName(Utils.GetExecutablePath()) ?? throw new InvalidOperationException());
            var identifier = $@"Global\{Controller.HttpRequest.UpdateChecker.Name}_{Directory.GetCurrentDirectory().GetClassicHashCode()}";
            using var singleInstance = new SingleInstanceService(identifier);
            if (!singleInstance.TryStartSingleInstance())
            {
                SendCommand(singleInstance, args.Length <= 0 ? Constants.ParameterMultiplyInstance : string.Join(' ', args));
                return;
            }
            using var d = singleInstance.Received.Subscribe(ArgumentsReceived);

            var app = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };

            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
            app.Exit += App_Exit;

            Global.LoadConfig();
            Controller.SystemProxy.RecoverFromPreviousRun(Global.GuiConfig.LocalPort);

            I18NUtil.SetLanguage(Global.GuiConfig.LangName);
            ViewUtils.SetResource(app.Resources, @"../View/NotifyIconResources.xaml", 1);

            Global.Controller = new MainController();

            // Logging
            Logging.DefaultOut = Console.Out;
            Logging.DefaultError = Console.Error;

            Global.ViewController = new MenuViewController(Global.Controller);
            SystemEvents.SessionEnding += (_, _) => ShutdownController();

            Global.Controller.Reload();
            if (Global.GuiConfig.IsDefaultConfig())
            {
                var res = MessageBox.Show(
                $@"{I18NUtil.GetAppStringValue(@"DefaultConfigMessage")}{Environment.NewLine}{I18NUtil.GetAppStringValue(@"DefaultConfigQuestion")}",
                Controller.HttpRequest.UpdateChecker.Name, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.OK);
                switch (res)
                {
                    case MessageBoxResult.Yes:
                    {
                        Global.Controller.ShowConfigForm();
                        break;
                    }
                    case MessageBoxResult.No:
                    {
                        Global.Controller.ShowSubscribeWindow();
                        break;
                    }
                    default:
                    {
                        ShutdownController();
                        return;
                    }
                }
            }

            Reg.SetUrlProtocol(@"ssr");
            Reg.SetUrlProtocol(@"sub");

            singleInstance.StartListenServer();
            app.Run();
        }

        private static int _shutdownStarted;
        private static void ShutdownController()
        {
            if (Interlocked.Exchange(ref _shutdownStarted, 1) == 0)
            {
                Global.ViewController?.Shutdown();
            }

            Global.Controller?.Shutdown();
            Global.Controller = null;
        }

        private static void App_Exit(object sender, ExitEventArgs e)
        {
            Reg.RemoveUrlProtocol(@"ssr");
            Reg.RemoveUrlProtocol(@"sub");
            ShutdownController();
        }

        private static void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            switch (e.Mode)
            {
                case PowerModes.Resume:
                {
                    Logging.Info("os wake up");
                    if (Global.Controller != null)
                    {
                        Task.Run(() =>
                        {
                            Thread.Sleep(10 * 1000);
                            try
                            {
                                Global.Controller.Reload();
                                Logging.Info("controller started");
                            }
                            catch (Exception ex)
                            {
                                Logging.LogUsefulException(ex);
                            }
                        }).Forget();
                    }
                    break;
                }
                case PowerModes.Suspend:
                {
                    if (Global.Controller != null)
                    {
                        Global.Controller.Stop();
                        Logging.Info("controller stopped");
                    }
                    Logging.Info("os suspend");
                    break;
                }
            }
        }

        private static int _exited;
        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (Interlocked.Increment(ref _exited) == 1)
            {
                Logging.Log(LogLevel.Error, $@"{e.ExceptionObject}");
                ShutdownController();
                ShowUnexpectedError(e.ExceptionObject);
                Environment.Exit(1);
            }
        }

        private static void ShowUnexpectedError(object exceptionObject)
        {
            void Show()
            {
                MessageBox.Show(
                $@"{I18NUtil.GetAppStringValue(@"UnexpectedError")}{Environment.NewLine}{exceptionObject}",
                Controller.HttpRequest.UpdateChecker.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            }

            try
            {
                if (Application.Current?.Dispatcher == null)
                {
                    Show();
                }
                else
                {
                    using var shown = new ManualResetEventSlim();
                    ViewUtils.RunOnUiThread(() =>
                    {
                        try
                        {
                            Show();
                        }
                        finally
                        {
                            shown.Set();
                        }
                    });
                    shown.Wait(TimeSpan.FromSeconds(30));
                }
            }
            catch
            {
                // The process is already terminating.
            }
        }

        private static void SendCommand(ISingleInstanceService service, string command)
        {
            try
            {
                using var completed = new ManualResetEventSlim();
                SendCommandAsync(service, command, completed).Forget();
                completed.Wait();
            }
            catch
            {
                // ignored
            }
        }

        private static async Task SendCommandAsync(ISingleInstanceService service, string command, ManualResetEventSlim completed)
        {
            try
            {
                await service.SendMessageToFirstInstanceAsync(command);
            }
            catch
            {
                // ignored
            }
            finally
            {
                completed.Set();
            }
        }

        private static void ArgumentsReceived((string, Action<string>) receive)
        {
            var (message, endFunc) = receive;
            var args = message
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet();

            if (args.Contains(Constants.ParameterMultiplyInstance))
            {
                ViewUtils.RunOnUiThread(() =>
                {
                    MessageBox.Show(I18NUtil.GetAppStringValue(@"SuccessiveInstancesMessage1") + Environment.NewLine +
                                    I18NUtil.GetAppStringValue(@"SuccessiveInstancesMessage2"),
                        I18NUtil.GetAppStringValue(@"SuccessiveInstancesCaption"), MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            ViewUtils.RunOnUiThread(() =>
            {
                Global.ViewController?.ImportAddress(string.Join(Environment.NewLine, args));
            });

            endFunc(string.Empty);
        }
    }
}
