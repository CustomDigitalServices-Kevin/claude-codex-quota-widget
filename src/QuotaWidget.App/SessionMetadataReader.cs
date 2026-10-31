using System.Text;

namespace QuotaWidget.App;

internal readonly record struct SessionLineSegments(string[] Head, string[] Tail)
{
    internal IEnumerable<string> All()
    {
        foreach (var line in Head) { yield return line; }
        foreach (var line in Tail) { yield return line; }
    }
}

internal sealed class SessionReadBudget(int bytes)
{
    private int remaining = bytes;

    internal void Consume(int count)
    {
        if (count < 0 || count > remaining) { throw new InvalidDataException("Session read budget is exhausted."); }
        remaining -= count;
    }
}

internal static class SessionMetadataReader
{
    internal const int MaximumFileBytes = 1024 * 1024;
    private const int BufferBytes = 16 * 1024;

    internal static bool TryResolveRegularFile(string? candidate, IEnumerable<string> allowedRoots,
        out string path, out string root)
    {
        path = string.Empty;
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)) { return false; }
        string fullPath;
        try { fullPath = Path.GetFullPath(candidate); }
        catch (Exception exception) when (IsPathFailure(exception)) { return false; }

        foreach (var allowed in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(allowed) || !Path.IsPathFullyQualified(allowed)) { continue; }
            if (!IsContained(fullPath, allowed) || !IsSafePath(allowed, fullPath, false)) { continue; }
            path = fullPath;
            root = allowed;
            return true;
        }
        return false;
    }

    internal static bool IsSafeDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;
        }
        catch (Exception exception) when (IsFileFailure(exception) || IsPathFailure(exception)) { return false; }
    }

    internal static SessionLineSegments ReadHeadAndTailLines(string path, int headBytes, int tailBytes,
        SessionReadBudget budget, CancellationToken token)
    {
        if (headBytes < 0 || tailBytes < 0 || headBytes + tailBytes > MaximumFileBytes)
        { throw new ArgumentOutOfRangeException(nameof(headBytes)); }
        using var stream = Open(path);
        var length = stream.Length;
        if (length <= headBytes + tailBytes)
        {
            budget.Consume(checked((int)length));
            var bytes = ReadSegment(stream, 0, checked((int)length), token);
            return new(CompleteLines(bytes, true, true), []);
        }

        budget.Consume(headBytes + tailBytes);
        var head = ReadSegment(stream, 0, headBytes, token);
        var tail = ReadSegment(stream, length - tailBytes, tailBytes, token);
        return new(CompleteLines(head, true, false), CompleteLines(tail, false, true));
    }

    internal static string ReadText(string path, int maximumBytes, SessionReadBudget budget, CancellationToken token)
    {
        using var stream = Open(path);
        if (stream.Length > maximumBytes) { throw new InvalidDataException("Session metadata exceeds the read limit."); }
        budget.Consume(checked((int)stream.Length));
        var bytes = ReadSegment(stream, 0, checked((int)stream.Length), token);
        return Encoding.UTF8.GetString(bytes);
    }

    internal static string[] ReadTailLines(string path, int maximumBytes, SessionReadBudget budget,
        CancellationToken token)
    {
        using var stream = Open(path);
        var length = stream.Length;
        var count = checked((int)Math.Min(length, maximumBytes));
        budget.Consume(count);
        var startsAtBeginning = count == length;
        var bytes = ReadSegment(stream, length - count, count, token);
        return CompleteLines(bytes, startsAtBeginning, true);
    }

    internal static string? Sanitize(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) { return null; }
        var source = value.Trim();
        var builder = new StringBuilder(Math.Min(source.Length, maximumLength));
        foreach (var character in source)
        {
            if (builder.Length == maximumLength) { break; }
            builder.Append(char.IsControl(character) ? ' ' : character);
        }
        var result = builder.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    internal static bool IsFileFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException or System.Security.SecurityException or InvalidDataException;

    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, BufferBytes, FileOptions.SequentialScan);

    private static byte[] ReadSegment(FileStream stream, long offset, int count, CancellationToken token)
    {
        stream.Position = offset;
        var bytes = new byte[count];
        var total = 0;
        while (total < count)
        {
            token.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, total, Math.Min(BufferBytes, count - total));
            if (read == 0) { break; }
            total += read;
        }
        return total == bytes.Length ? bytes : bytes[..total];
    }

    private static string[] CompleteLines(byte[] bytes, bool startsAtBeginning, bool endsAtEnd)
    {
        if (bytes.Length == 0) { return []; }
        var text = Encoding.UTF8.GetString(bytes);
        var start = 0;
        if (!startsAtBeginning)
        {
            var boundary = text.IndexOf('\n');
            if (boundary < 0) { return []; }
            start = boundary + 1;
        }
        var end = text.Length;
        if (!endsAtEnd || text[^1] != '\n')
        {
            var boundary = text.LastIndexOf('\n');
            if (boundary < start) { return []; }
            end = boundary + 1;
        }
        if (end <= start) { return []; }
        return text[start..end].Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToArray();
    }

    private static bool IsContained(string candidate, string root)
    {
        try
        {
            var relative = Path.GetRelativePath(root, candidate);
            return relative != ".." && !Path.IsPathRooted(relative)
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (Exception exception) when (IsPathFailure(exception)) { return false; }
    }

    private static bool IsSafePath(string root, string candidate, bool directory)
    {
        try
        {
            var rootFull = Path.GetFullPath(root);
            if (!IsSafeDirectory(rootFull)) { return false; }
            var relative = Path.GetRelativePath(rootFull, candidate);
            var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            var current = rootFull;
            var directoryCount = directory ? segments.Length : segments.Length - 1;
            for (var index = 0; index < directoryCount; index++)
            {
                current = Path.Combine(current, segments[index]);
                if (!IsSafeDirectory(current)) { return false; }
            }
            var attributes = File.GetAttributes(candidate);
            if ((attributes & FileAttributes.ReparsePoint) != 0) { return false; }
            return directory
                ? (attributes & FileAttributes.Directory) != 0
                : (attributes & FileAttributes.Directory) == 0;
        }
        catch (Exception exception) when (IsFileFailure(exception) || IsPathFailure(exception)) { return false; }
    }

    private static bool IsPathFailure(Exception exception) => exception is ArgumentException
        or NotSupportedException or PathTooLongException;
}
