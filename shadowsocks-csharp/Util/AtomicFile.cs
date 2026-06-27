using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Shadowsocks.Util;

public static class AtomicFile
{
    public static string BackupPath(string path)
    {
        return Path.GetFullPath(path) + ".bak";
    }

    public static string TempPath(string path)
    {
        return Path.GetFullPath(path) + ".tmp";
    }

    public static void WriteAllTextAtomic(string path, string content, Encoding encoding)
    {
        var targetPath = Path.GetFullPath(path);
        var tempPath = TempPath(targetPath);
        var backupPath = BackupPath(targetPath);
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, encoding))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(true);
            }

            if (File.Exists(targetPath))
            {
                ReplaceExisting(tempPath, targetPath, backupPath);
            }
            else
            {
                File.Move(tempPath, targetPath);
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static bool TryReadValidJson<T>(string path, out T value, out Exception error)
    {
        value = default;
        error = null;

        try
        {
            if (!File.Exists(path))
            {
                error = new FileNotFoundException($@"File not found: {path}", path);
                return false;
            }

            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            if (value is null)
            {
                error = new InvalidDataException($@"File contains empty JSON: {path}");
                return false;
            }

            return true;
        }
        catch (Exception e)
        {
            error = e;
            value = default;
            return false;
        }
    }

    public static string PreserveCorruptFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var corruptPath = GetUniqueCorruptPath(path);
        try
        {
            File.Move(path, corruptPath);
        }
        catch (Exception)
        {
            try
            {
                File.Copy(path, corruptPath, false);
            }
            catch
            {
                return null;
            }
        }

        return corruptPath;
    }

    private static void ReplaceExisting(string tempPath, string targetPath, string backupPath)
    {
        try
        {
            File.Replace(tempPath, targetPath, backupPath, true);
        }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            File.Copy(targetPath, backupPath, true);
            File.Move(tempPath, targetPath, true);
        }
    }

    private static string GetUniqueCorruptPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var candidate = $@"{fullPath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        for (var i = 1; ; i++)
        {
            var next = $@"{candidate}-{i}";
            if (!File.Exists(next))
            {
                return next;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the original exception from the atomic write path.
        }
    }
}
