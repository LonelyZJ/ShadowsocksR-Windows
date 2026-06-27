using Shadowsocks.Controller;
using Shadowsocks.Controller.HttpRequest;
using Shadowsocks.Controller.Service;
using Shadowsocks.Enums;
using Shadowsocks.Util;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Shadowsocks.Model
{
    public static class Global
    {
        private const string ConfigFile = @"gui-config.json";
        private static readonly Encoding ConfigEncoding = new UTF8Encoding(false);

        public static bool OSSupportsLocalIPv6 => Socket.OSSupportsIPv6;

        public static string LocalHost => OSSupportsLocalIPv6 ? $@"[{IPAddress.IPv6Loopback}]" : $@"{IPAddress.Loopback}";

        public static string AnyHost => OSSupportsLocalIPv6 ? $@"[{IPAddress.IPv6Any}]" : $@"{IPAddress.Any}";

        public static IPAddress IpLocal => OSSupportsLocalIPv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;

        public static IPAddress IpAny => OSSupportsLocalIPv6 ? IPAddress.IPv6Any : IPAddress.Any;

        public static Configuration GuiConfig;

        public static MainController Controller;

        public static MenuViewController ViewController;

        public static UpdateNode UpdateNodeChecker;

        public static UpdateSubscribeManager UpdateSubscribeManager;

        public static Configuration LoadFile(string filename)
        {
            if (TryLoadConfiguration(filename, out var config, out var loadError))
            {
                return config;
            }

            var backupPath = AtomicFile.BackupPath(filename);
            if (TryLoadConfiguration(backupPath, out config, out var backupError))
            {
                Log(LogLevel.Warn, $@"Failed to load {filename}, restored from {backupPath}. {loadError}");
                try
                {
                    AtomicFile.WriteAllTextAtomic(filename, JsonUtils.Serialize(config, true), ConfigEncoding);
                }
                catch (Exception e)
                {
                    Log(LogLevel.Warn, $@"Failed to restore {filename} from backup.");
                    Logging.LogUsefulException(e);
                    Console.Error.WriteLine(e);
                }

                return config;
            }

            if (backupError != null && backupError is not FileNotFoundException)
            {
                Log(LogLevel.Error, $@"Failed to load backup {backupPath}. {backupError}");
                AtomicFile.PreserveCorruptFile(backupPath);
            }

            if (loadError != null && loadError is not FileNotFoundException)
            {
                Log(LogLevel.Error, $@"Failed to load {filename}. {loadError}");
            }

            config = new Configuration();
            config.FixConfiguration();
            return config;
        }

        public static Configuration Load()
        {
            return LoadFile(ConfigFile);
        }

        private static Configuration Load(string configStr)
        {
            try
            {
                var config = JsonSerializer.Deserialize<Configuration>(configStr);
                if (config is not null)
                {
                    config.FixConfiguration();
                    return config;
                }
            }
            catch
            {
                // ignored
            }
            return null;
        }

        private static bool TryLoadConfiguration(string filename, out Configuration config, out Exception error)
        {
            config = null;
            if (!AtomicFile.TryReadValidJson<Configuration>(filename, out var loaded, out error))
            {
                if (error is not FileNotFoundException)
                {
                    AtomicFile.PreserveCorruptFile(filename);
                }

                return false;
            }

            loaded.FixConfiguration();
            config = loaded;
            return true;
        }

        private static void Log(LogLevel level, string message)
        {
            try
            {
                Logging.Log(level, message);
            }
            catch
            {
                Console.Error.WriteLine($@"[{level}] {message}");
            }
        }

        public static void LoadConfig()
        {
            GuiConfig = Load();
        }

        public static void SaveConfig()
        {
            if (GuiConfig.Index >= GuiConfig.Configs.Count)
            {
                GuiConfig.Index = GuiConfig.Configs.Count - 1;
            }
            else if (GuiConfig.Index < 0)
            {
                GuiConfig.Index = 0;
            }

            try
            {
                var jsonString = JsonUtils.Serialize(GuiConfig, true);
                AtomicFile.WriteAllTextAtomic(ConfigFile, jsonString, ConfigEncoding);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                Logging.LogUsefulException(e);
            }
        }
    }
}
