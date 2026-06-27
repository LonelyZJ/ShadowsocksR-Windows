using Shadowsocks.Controller;
using Shadowsocks.Enums;
using Shadowsocks.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Shadowsocks.Model.Transfer
{
    [Serializable]
    public class ServerTransferTotal
    {
        private const string LogFile = @"transfer_log.json";

        public Dictionary<string, ServerTrans> Servers = new();
        private int _saveCounter;
        private DateTime _saveTime;
        private static readonly Encoding LogEncoding = new UTF8Encoding(false);

        private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);
        private const int MaxSaveCounter = 256;

        public static ServerTransferTotal Load()
        {
            if (TryLoad(LogFile, out var config, out var loadError))
            {
                return config;
            }

            var backupPath = AtomicFile.BackupPath(LogFile);
            if (TryLoad(backupPath, out config, out var backupError))
            {
                Log(LogLevel.Warn, $@"Failed to load {LogFile}, restored from {backupPath}. {loadError}");
                try
                {
                    AtomicFile.WriteAllTextAtomic(LogFile, JsonUtils.Serialize(config.Servers, true), LogEncoding);
                }
                catch (Exception e)
                {
                    Log(LogLevel.Warn, $@"Failed to restore {LogFile} from backup.");
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
                Log(LogLevel.Error, $@"Failed to load {LogFile}. {loadError}");
            }

            config = new ServerTransferTotal();
            config.Init();
            return config;
        }

        private void Init()
        {
            _saveCounter = MaxSaveCounter;
            _saveTime = DateTime.Now;
            if (Servers == null)
            {
                Servers = new Dictionary<string, ServerTrans>();
            }
        }

        public static void Save(ServerTransferTotal config, List<Server> servers = null)
        {
            try
            {
                var currentServers = config.Servers;
                Dictionary<string, ServerTrans> snapshot;
                lock (currentServers)
                {
                    var source = currentServers.AsEnumerable();
                    if (servers != null)
                    {
                        source = source.Where(pair => servers.Exists(server => server.Id == pair.Key));
                    }

                    snapshot = source.ToDictionary(
                        pair => pair.Key,
                        pair => new ServerTrans
                        {
                            TotalUploadBytes = pair.Value.TotalUploadBytes,
                            TotalDownloadBytes = pair.Value.TotalDownloadBytes
                        });

                    if (servers != null)
                    {
                        config.Servers = snapshot.ToDictionary(
                            pair => pair.Key,
                            pair => new ServerTrans
                            {
                                TotalUploadBytes = pair.Value.TotalUploadBytes,
                                TotalDownloadBytes = pair.Value.TotalDownloadBytes
                            });
                    }
                }

                var jsonString = JsonUtils.Serialize(snapshot, true);
                AtomicFile.WriteAllTextAtomic(LogFile, jsonString, LogEncoding);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                Logging.LogUsefulException(e);
            }
        }

        private static bool TryLoad(string filename, out ServerTransferTotal config, out Exception error)
        {
            config = null;
            if (!AtomicFile.TryReadValidJson<Dictionary<string, ServerTrans>>(filename, out var servers, out error))
            {
                if (error is not FileNotFoundException)
                {
                    AtomicFile.PreserveCorruptFile(filename);
                }

                return false;
            }

            config = new ServerTransferTotal { Servers = servers };
            config.Init();
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

        public void Clear(string serverId)
        {
            lock (Servers)
            {
                if (Servers.TryGetValue(serverId, out var trans))
                {
                    trans.TotalUploadBytes = 0;
                    trans.TotalDownloadBytes = 0;
                }
            }
        }

        public void AddUpload(string serverId, long size)
        {
            lock (Servers)
            {
                if (Servers.TryGetValue(serverId, out var trans))
                {
                    trans.TotalUploadBytes += size;
                }
                else
                {
                    Servers.Add(serverId, new ServerTrans());
                }
            }
            if (--_saveCounter <= 0)
            {
                _saveCounter = MaxSaveCounter;
                if (DateTime.Now - _saveTime > MinInterval)
                {
                    lock (Servers)
                    {
                        Save(this);
                        _saveTime = DateTime.Now;
                    }
                }
            }
        }

        public void AddDownload(string server, long size)
        {
            lock (Servers)
            {
                if (Servers.TryGetValue(server, out var trans))
                {
                    trans.TotalDownloadBytes += size;
                }
                else
                {
                    Servers.Add(server, new ServerTrans());
                }
            }
            if (--_saveCounter <= 0)
            {
                _saveCounter = MaxSaveCounter;
                if (DateTime.Now - _saveTime > MinInterval)
                {
                    lock (Servers)
                    {
                        Save(this);
                        _saveTime = DateTime.Now;
                    }
                }
            }
        }
    }
}
