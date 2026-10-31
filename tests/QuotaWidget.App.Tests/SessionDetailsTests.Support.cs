using System.Text;
using System.IO;

namespace QuotaWidget.App.Tests;

public sealed partial class SessionDetailsTests
{
    private sealed class SessionFixture : IDisposable
    {
        internal SessionFixture()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "QuotaWidget-SessionDetails-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Root);
        }

        internal string Root { get; }
        internal string Path(params string[] segments) => segments.Aggregate(Root, System.IO.Path.Combine);

        internal string Directory(params string[] segments)
        {
            var path = Path(segments);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        internal string Write(string path, params string[] lines) => WriteRaw(path,
            string.Join(Environment.NewLine, lines) + Environment.NewLine);

        internal string WriteRaw(string path, string value)
        {
            var relative = System.IO.Path.GetRelativePath(Root, path);
            if (System.IO.Path.IsPathRooted(relative) || relative == ".."
                || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            { throw new InvalidOperationException("Fixture path escaped its temporary root."); }
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value);
            return path;
        }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Root, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string LargeJournal(string header, string tail, int minimumLength)
    {
        var builder = new StringBuilder(minimumLength + tail.Length + 8);
        builder.AppendLine(header);
        while (builder.Length < minimumLength) { builder.AppendLine("{}"); }
        builder.AppendLine(tail);
        return builder.ToString();
    }
}
