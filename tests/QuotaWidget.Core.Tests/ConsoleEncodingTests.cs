using System.Text;
using QuotaWidget.Cli;

namespace QuotaWidget.Core.Tests;

[CollectionDefinition("Console encoding", DisableParallelization = true)]
public sealed class ConsoleEncodingTestGroup
{
}

[Collection("Console encoding")]
public sealed class ConsoleEncodingTests
{
    [Theory]
    [InlineData("--text", 0, "exposé")]
    [InlineData("--arbitrage", 0, "réserve")]
    [InlineData("--invalide-é", 1, "é")]
    public void EntryPointSetsUtf8WithoutBomAndCapturedOutputContainsAccentedBytes(string mode, int exitCode, string word)
    {
        using var directory = new TestDirectory();
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        using var output = new StreamWriter(stdout, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var error = new StreamWriter(stderr, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var savedEncoding = Console.OutputEncoding;
        var savedOut = Console.Out;
        var savedError = Console.Error;
        try
        {
            Console.OutputEncoding = Encoding.ASCII;
            Console.SetOut(output);
            Console.SetError(error);
            Assert.Equal(exitCode, Program.Main([
                "--claude-dir", directory.PathFor("claude"),
                "--codex-sessions", directory.PathFor("codex"),
                "--data-dir", directory.PathFor("data"),
                "--now", "2026-10-30T10:00:00Z", mode,
            ]));
            Assert.Equal(Encoding.UTF8.CodePage, Console.OutputEncoding.CodePage);
            Assert.Empty(Console.OutputEncoding.GetPreamble());
            var bytes = exitCode == 0 ? stdout.ToArray() : stderr.ToArray();
            Assert.True(bytes.AsSpan().IndexOf("é"u8) >= 0);
            Assert.False(bytes.AsSpan().StartsWith("\uFEFF"u8));
            Assert.Contains(word, new UTF8Encoding(false, true).GetString(bytes), StringComparison.Ordinal);
            Assert.Empty(exitCode == 0 ? stderr.ToArray() : stdout.ToArray());
        }
        finally
        {
            Console.OutputEncoding = savedEncoding;
            Console.SetOut(savedOut);
            Console.SetError(savedError);
        }
    }
}
