using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuotaWidget.Core;

internal static class ClaudeLocalFiles
{
    internal const int MaximumBytes = 1024 * 1024;
    internal const string InvalidDataMessage = "Claude integration data is invalid.";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static JsonSerializerOptions CompactJson { get; } = new() { WriteIndented = false };

    internal static InvalidDataException Invalid() => new(InvalidDataMessage);

    internal static string FullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw Invalid();
        }

        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Invalid();
        }
    }

    internal static void AssertSafeTarget(string path)
    {
        var fullPath = FullPath(path);
        if (File.Exists(fullPath))
        {
            AssertRegularFile(fullPath);
        }
        else if (Directory.Exists(fullPath))
        {
            throw Invalid();
        }

        var parent = Path.GetDirectoryName(fullPath) ?? throw Invalid();
        AssertSafeDirectoryChain(parent);
    }

    internal static void AssertSafeDirectoryChain(string directory)
    {
        DirectoryInfo? current = new(FullPath(directory));
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Invalid();
            }
            current = current.Parent;
        }
    }

    internal static void AssertRegularFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw Invalid();
        }
    }

    internal static byte[] ReadBytes(string path)
    {
        AssertRegularFile(path);
        var info = new FileInfo(path);
        if (info.Length > MaximumBytes)
        {
            throw Invalid();
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > MaximumBytes)
        {
            throw Invalid();
        }
        return bytes;
    }

    internal static JsonObject ParseObject(byte[] bytes)
    {
        try
        {
            var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            var text = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
            return JsonNode.Parse(text, documentOptions: new() { MaxDepth = 30 }) as JsonObject ?? throw Invalid();
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            throw Invalid();
        }
    }

    internal static byte[] Serialize(JsonNode value)
    {
        byte[] bytes;
        try { bytes = Encoding.UTF8.GetBytes(value.ToJsonString(CompactJson)); }
        catch (JsonException) { throw Invalid(); }
        if (bytes.Length > MaximumBytes)
        {
            throw Invalid();
        }
        return bytes;
    }

    internal static void AtomicWrite(string path, byte[] bytes)
    {
        if (bytes.Length > MaximumBytes)
        {
            throw Invalid();
        }

        var fullPath = FullPath(path);
        AssertSafeTarget(fullPath);
        var parent = Path.GetDirectoryName(fullPath) ?? throw Invalid();
        Directory.CreateDirectory(parent);
        AssertSafeDirectoryChain(parent);
        var temporary = Path.Combine(parent, $".quota-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(fullPath))
            {
                AssertRegularFile(fullPath);
            }
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static string NewBackupPath(string path, TimeProvider clock)
    {
        AssertRegularFile(path);
        var fullPath = FullPath(path);
        var parent = Path.GetDirectoryName(fullPath) ?? throw Invalid();
        var name = $"{Path.GetFileName(fullPath)}.backup-{clock.GetUtcNow():yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        return Path.Combine(parent, name);
    }

    internal static void CopyBackup(string source, string destination)
    {
        AssertRegularFile(source);
        AssertSafeTarget(destination);
        File.Copy(source, destination, false);
        AssertRegularFile(destination);
    }
}
