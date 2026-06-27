using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller;
using Shadowsocks.Model;
using Shadowsocks.Model.Transfer;
using Shadowsocks.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UnitTest;

[TestClass]
[DoNotParallelize]
public class ConfigPersistenceTest
{
    [TestMethod]
    public void SaveConfigCreatesReadableConfig()
    {
        RunInTempDirectory(_ =>
        {
            Global.GuiConfig = CreateConfig(1090, "saved.example");

            Global.SaveConfig();

            Assert.IsTrue(File.Exists("gui-config.json"));
            var saved = JsonSerializer.Deserialize<Configuration>(File.ReadAllText("gui-config.json"));
            Assert.IsNotNull(saved);
            Assert.AreEqual(1090, saved.LocalPort);
            Assert.AreEqual("saved.example", saved.Configs[0].server);
        });
    }

    [TestMethod]
    public void SaveConfigCreatesBackupWhenReplacingExistingConfig()
    {
        RunInTempDirectory(_ =>
        {
            File.WriteAllText("gui-config.json", JsonUtils.Serialize(CreateConfig(1080, "old.example"), true));
            Global.GuiConfig = CreateConfig(1091, "new.example");

            Global.SaveConfig();

            Assert.IsTrue(File.Exists(AtomicFile.BackupPath("gui-config.json")));
            var backup = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(AtomicFile.BackupPath("gui-config.json")));
            Assert.IsNotNull(backup);
            Assert.AreEqual(1080, backup.LocalPort);

            var saved = JsonSerializer.Deserialize<Configuration>(File.ReadAllText("gui-config.json"));
            Assert.IsNotNull(saved);
            Assert.AreEqual(1091, saved.LocalPort);
        });
    }

    [TestMethod]
    public void LoadFileRestoresFromBackupWhenPrimaryIsCorrupt()
    {
        RunInTempDirectory(_ =>
        {
            var backupConfig = CreateConfig(1092, "backup.example");
            File.WriteAllText("gui-config.json", "{ broken");
            File.WriteAllText(AtomicFile.BackupPath("gui-config.json"), JsonUtils.Serialize(backupConfig, true));

            var loaded = Global.LoadFile("gui-config.json");

            Assert.AreEqual(1092, loaded.LocalPort);
            Assert.AreEqual("backup.example", loaded.Configs[0].server);
            Assert.IsTrue(Directory.GetFiles(Environment.CurrentDirectory, "gui-config.json.corrupt-*").Any());

            var restored = JsonSerializer.Deserialize<Configuration>(File.ReadAllText("gui-config.json"));
            Assert.IsNotNull(restored);
            Assert.AreEqual(1092, restored.LocalPort);
        });
    }

    [TestMethod]
    public void LoadFileReturnsDefaultAndPreservesCorruptFilesWhenPrimaryAndBackupAreInvalid()
    {
        RunInTempDirectory(_ =>
        {
            File.WriteAllText("gui-config.json", "{ broken");
            File.WriteAllText(AtomicFile.BackupPath("gui-config.json"), "{ also broken");

            var loaded = Global.LoadFile("gui-config.json");

            Assert.AreEqual(1080, loaded.LocalPort);
            Assert.AreEqual(1, loaded.Configs.Count);
            Assert.IsTrue(Directory.GetFiles(Environment.CurrentDirectory, "gui-config.json.corrupt-*").Any());
            Assert.IsTrue(Directory.GetFiles(Environment.CurrentDirectory, "gui-config.json.bak.corrupt-*").Any());
        });
    }

    [TestMethod]
    public void TransferLogRestoresFromBackupWhenPrimaryIsCorrupt()
    {
        RunInTempDirectory(_ =>
        {
            var backup = new Dictionary<string, ServerTrans>
            {
                ["server-1"] = new() { TotalUploadBytes = 10, TotalDownloadBytes = 20 }
            };
            File.WriteAllText("transfer_log.json", "{ broken");
            File.WriteAllText(AtomicFile.BackupPath("transfer_log.json"), JsonUtils.Serialize(backup, true));

            var loaded = ServerTransferTotal.Load();

            Assert.IsTrue(loaded.Servers.ContainsKey("server-1"));
            Assert.AreEqual(10, loaded.Servers["server-1"].TotalUploadBytes);
            Assert.AreEqual(20, loaded.Servers["server-1"].TotalDownloadBytes);
            Assert.IsTrue(Directory.GetFiles(Environment.CurrentDirectory, "transfer_log.json.corrupt-*").Any());
            Assert.IsTrue(File.Exists("transfer_log.json"));
        });
    }

    [TestMethod]
    public void TransferLogFallsBackToEmptyWhenPrimaryIsCorrupt()
    {
        RunInTempDirectory(_ =>
        {
            File.WriteAllText("transfer_log.json", "{ broken");

            var loaded = ServerTransferTotal.Load();

            Assert.AreEqual(0, loaded.Servers.Count);
            Assert.IsTrue(Directory.GetFiles(Environment.CurrentDirectory, "transfer_log.json.corrupt-*").Any());
        });
    }

    private static Configuration CreateConfig(int localPort, string serverHost)
    {
        var config = new Configuration
        {
            LocalPort = localPort,
            Configs =
            {
                new Server { server = serverHost }
            }
        };
        config.FixConfiguration();
        return config;
    }

    private static void RunInTempDirectory(Action<string> action)
    {
        var oldDirectory = Environment.CurrentDirectory;
        var oldConfig = Global.GuiConfig;
        var oldSaveToFile = Logging.SaveToFile;
        var directory = Path.Combine(Path.GetTempPath(), "ssr-config-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            Logging.SaveToFile = false;
            Logging.DefaultOut = Console.Out;
            Logging.DefaultError = Console.Error;
            Environment.CurrentDirectory = directory;
            action(directory);
        }
        finally
        {
            Environment.CurrentDirectory = oldDirectory;
            Global.GuiConfig = oldConfig;
            Logging.SaveToFile = oldSaveToFile;
            Directory.Delete(directory, true);
        }
    }
}
