using System.Text.Json;
using QuotaWidget.Cli;

namespace QuotaWidget.Core.Tests;

public sealed class NativeClaudeCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "QuotaWidgetCliTests", Guid.NewGuid().ToString("N"));

    public NativeClaudeCommandTests() => Directory.CreateDirectory(root);

    [Fact]
    public void ProducerRoutesStandardInputWithoutAccountDiscovery()
    {
        var output = new StringWriter();
        var input = """{"session_id":"00000000-0000-4000-8000-000000000031","model":{"id":"synthetic"}}""";
        var result = NativeClaudeCommands.Run(["--claude-statusline", "--state-dir", root], new StringReader(input), output, new StringWriter());
        Assert.Equal(0, result);
        Assert.Single(Directory.GetFiles(root, "*.json"));
        Assert.NotEmpty(output.ToString());
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--apply")]
    public void ProducerRefusesUnknownOptionsWithoutWrites(string option)
    {
        Assert.Equal(1, NativeClaudeCommands.Run(["--claude-statusline", option], new StringReader("{}"), new StringWriter(), new StringWriter()));
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public void OversizedInputDoesNotLeakIntoErrors()
    {
        const string secret = "SYNTHETIC_PRIVATE_INPUT";
        var error = new StringWriter();
        var input = secret + new string('x', 1024 * 1024);
        Assert.Equal(1, NativeClaudeCommands.Run(["--claude-statusline", "--state-dir", root], new StringReader(input), new StringWriter(), error));
        Assert.DoesNotContain(secret, error.ToString());
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public void PreviewDoesNotReadMalformedSettings()
    {
        var settings = Path.Combine(root, "settings.json");
        var executable = Path.Combine(root, "quotas.exe");
        File.WriteAllText(settings, "malformed synthetic settings");
        File.WriteAllBytes(executable, []);
        var output = new StringWriter();
        Assert.Equal(0, NativeClaudeCommands.Run(["--configure-claude", "--settings-path", settings], TextReader.Null, output, new StringWriter(), executable));
        using var fragment = JsonDocument.Parse(output.ToString());
        Assert.Equal("command", fragment.RootElement.GetProperty("statusLine").GetProperty("type").GetString());
        Assert.Equal("malformed synthetic settings", File.ReadAllText(settings));
        Assert.Equal(2, Directory.GetFiles(root).Length);
    }

    [Fact]
    public void RemoveWithoutIntegrationDoesNotReadSettingsOrRequireExecutable()
    {
        Assert.Equal(0, NativeClaudeCommands.Run(["--unconfigure-claude",
            "--integration-path", Path.Combine(root, "absent.json"), "--settings-path", Path.Combine(root, "unread.json")],
            TextReader.Null, new StringWriter(), new StringWriter(), Path.Combine(root, "absent.exe")));
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public void ReplacementNeedsApplyAndOtherCommandsAreDelegated()
    {
        Assert.Equal(1, NativeClaudeCommands.Run(["--configure-claude", "--replace-existing"], TextReader.Null, new StringWriter(), new StringWriter()));
        Assert.Null(NativeClaudeCommands.Run(["--json"], TextReader.Null, new StringWriter(), new StringWriter()));
        Assert.Null(WidgetExitCommand.Run(["--json"], new StringWriter()));
        Assert.Equal(1, WidgetExitCommand.Run(["--request-widget-exit", "--extra"], new StringWriter()));
    }

    public void Dispose()
    {
        Directory.Delete(root, true);
        GC.SuppressFinalize(this);
    }
}
