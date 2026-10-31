using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuotaWidget.Core;

namespace QuotaWidget.Core.Tests;

public sealed class NativeClaudeConfigurationTests
{
    [Fact]
    public void PreviewReturnsEncodedWindowsPowerShellCommand()
    {
        using var directory = new TestDirectory();
        var executable = directory.Write("app folder/quotas.exe", "synthetic");

        using var preview = JsonDocument.Parse(ClaudeConfiguration.Preview(executable));

        var status = preview.RootElement.GetProperty("statusLine");
        Assert.Equal("command", status.GetProperty("type").GetString());
        var command = status.GetProperty("command").GetString()!;
        const string prefix = "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand ";
        Assert.StartsWith(prefix, command, StringComparison.Ordinal);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(command[prefix.Length..]));
        Assert.Contains("[Console]::InputEncoding=$u", script, StringComparison.Ordinal);
        Assert.Contains("[Console]::OutputEncoding=$u", script, StringComparison.Ordinal);
        Assert.Contains($"& '{executable.Replace('\\', '/')}' --claude-statusline", script, StringComparison.Ordinal);
        Assert.EndsWith("exit $LASTEXITCODE", script, StringComparison.Ordinal);
        Assert.Equal(0, status.GetProperty("padding").GetInt32());
    }

    [Fact]
    public void ApplyRejectsExistingStatusLineBeforeBackupOrMutation()
    {
        using var directory = new TestDirectory();
        var executable = directory.Write("quotas.exe", "synthetic");
        var settings = directory.Write("profile/settings.json",
            "{\"statusLine\":{\"type\":\"command\",\"command\":\"keep-me\"},\"theme\":\"dark\"}");
        var integration = directory.PathFor("widget/claude-integration.json");
        var original = File.ReadAllBytes(settings);

        var error = Assert.Throws<InvalidDataException>(() =>
            ClaudeConfiguration.Apply(settings, executable, integration));

        Assert.Equal("Claude integration data is invalid.", error.Message);
        Assert.Equal(original, File.ReadAllBytes(settings));
        Assert.False(File.Exists(integration));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(settings)!, "*.backup-*"));
    }

    [Fact]
    public void ApplyBacksUpExactBytesAndRemoveRestoresOnlyStatusLine()
    {
        using var directory = new TestDirectory();
        var executable = directory.Write("program/quotas.exe", "synthetic");
        var settings = directory.PathFor("profile/settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        var original = new UTF8Encoding(true).GetBytes(
            "{\r\n  \"theme\":\"dark\",\r\n  \"lastSeen\":\"2030-06-01T12:34:56+05:30\",\r\n  \"statusLine\":{\"type\":\"command\",\"command\":\"old\"}\r\n}");
        File.WriteAllBytes(settings, original);
        var integration = directory.PathFor("widget/integration.json");

        var applied = ClaudeConfiguration.Apply(settings, executable, integration, replaceExisting: true);

        Assert.True(applied.Changed);
        Assert.NotNull(applied.BackupPath);
        Assert.Equal(original, File.ReadAllBytes(applied.BackupPath));
        var changed = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        changed["theme"] = "light";
        changed["later"] = true;
        File.WriteAllText(settings, changed.ToJsonString());

        var removed = ClaudeConfiguration.Remove(settings, executable, integration);

        Assert.True(removed.Changed);
        using var final = JsonDocument.Parse(File.ReadAllText(settings));
        Assert.Equal("light", final.RootElement.GetProperty("theme").GetString());
        Assert.True(final.RootElement.GetProperty("later").GetBoolean());
        Assert.Equal("old", final.RootElement.GetProperty("statusLine").GetProperty("command").GetString());
        Assert.Equal("2030-06-01T12:34:56+05:30", final.RootElement.GetProperty("lastSeen").GetString());
    }

    [Fact]
    public void RemoveDeletesOnlyOwnedPropertyAndPreservesLaterSettings()
    {
        using var directory = new TestDirectory();
        var executable = directory.Write("quotas.exe", "synthetic");
        var settings = directory.Write("profile/settings.json", "{\"theme\":\"dark\"}");
        var integration = directory.PathFor("widget/integration.json");
        ClaudeConfiguration.Apply(settings, executable, integration);
        var changed = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        changed["theme"] = "light";
        changed["newKey"] = new JsonObject { ["keep"] = true };
        File.WriteAllText(settings, changed.ToJsonString());

        ClaudeConfiguration.Remove(settings, executable, integration);

        using var final = JsonDocument.Parse(File.ReadAllText(settings));
        Assert.False(final.RootElement.TryGetProperty("statusLine", out _));
        Assert.Equal("light", final.RootElement.GetProperty("theme").GetString());
        Assert.True(final.RootElement.GetProperty("newKey").GetProperty("keep").GetBoolean());
    }

    [Fact]
    public void RemoveLeavesUserEditedCommandUntouched()
    {
        using var directory = new TestDirectory();
        var executable = directory.Write("quotas.exe", "synthetic");
        var settings = directory.Write("profile/settings.json", "{\"theme\":\"dark\"}");
        var integration = directory.PathFor("widget/integration.json");
        ClaudeConfiguration.Apply(settings, executable, integration);
        var changed = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        changed["statusLine"]!["command"] = "user-command";
        File.WriteAllText(settings, changed.ToJsonString());
        var userBytes = File.ReadAllBytes(settings);

        var result = ClaudeConfiguration.Remove(settings, executable, integration);

        Assert.True(result.Changed);
        Assert.Equal(userBytes, File.ReadAllBytes(settings));
        using var final = JsonDocument.Parse(File.ReadAllText(settings));
        Assert.Equal("user-command", final.RootElement.GetProperty("statusLine").GetProperty("command").GetString());
        Assert.False(ReadActive(integration));

        var reapplied = ClaudeConfiguration.Apply(settings, executable, integration, replaceExisting: true);
        Assert.True(reapplied.Changed);
    }

    [Fact]
    public void RemoveRetiresOwnershipWhenSettingsAreAbsentAndAllowsReapply()
    {
        using var directory = new TestDirectory();
        var executable = directory.Write("quotas.exe", "synthetic");
        var settings = directory.PathFor("profile/settings.json");
        var integration = directory.PathFor("widget/integration.json");
        ClaudeConfiguration.Apply(settings, executable, integration);
        File.Delete(settings);

        var removed = ClaudeConfiguration.Remove(settings, executable, integration);

        Assert.True(removed.Changed);
        Assert.False(File.Exists(settings));
        Assert.False(ReadActive(integration));
        var reapplied = ClaudeConfiguration.Apply(settings, executable, integration);
        Assert.True(reapplied.Changed);
        Assert.True(File.Exists(settings));
    }

    [Theory]
    [InlineData("type")]
    [InlineData("padding")]
    [InlineData("extra")]
    public void RemovePreservesAnyUserModifiedFragmentAndRetiresOwnership(string mutation)
    {
        using var directory = new TestDirectory();
        var executable = directory.Write("quotas.exe", "synthetic");
        var settings = directory.Write("profile/settings.json", "{\"theme\":\"dark\"}");
        var integration = directory.PathFor("widget/integration.json");
        ClaudeConfiguration.Apply(settings, executable, integration);
        var changed = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        var statusLine = changed["statusLine"]!.AsObject();
        if (mutation == "type") { statusLine["type"] = "prompt"; }
        if (mutation == "padding") { statusLine["padding"] = 1; }
        if (mutation == "extra") { statusLine["userKey"] = true; }
        File.WriteAllText(settings, changed.ToJsonString());
        var userBytes = File.ReadAllBytes(settings);

        var removed = ClaudeConfiguration.Remove(settings, executable, integration);

        Assert.True(removed.Changed);
        Assert.Equal(userBytes, File.ReadAllBytes(settings));
        Assert.False(ReadActive(integration));
        Assert.True(ClaudeConfiguration.Apply(settings, executable, integration, replaceExisting: true).Changed);
    }

    [Fact]
    public void InvalidOrOversizedSettingsAreRejectedBeforeMutation()
    {
        using var directory = new TestDirectory();
        var executable = directory.Write("quotas.exe", "synthetic");
        var integration = directory.PathFor("widget/integration.json");
        var invalid = directory.Write("invalid/settings.json", "{not-json");

        Assert.Throws<InvalidDataException>(() => ClaudeConfiguration.Apply(invalid, executable, integration));
        Assert.Equal("{not-json", File.ReadAllText(invalid));
        Assert.False(File.Exists(integration));

        var oversized = directory.Write("large/settings.json", new string(' ', 1024 * 1024 + 1));
        Assert.Throws<InvalidDataException>(() => ClaudeConfiguration.Apply(oversized, executable, integration));
        Assert.Equal(1024 * 1024 + 1, new FileInfo(oversized).Length);
    }

    [Fact]
    public void ReparseSettingsAreRejectedAndSpecialExecutablePathIsEncoded()
    {
        using var directory = new TestDirectory();
        var executable = directory.Write("quotas.exe", "synthetic");
        var specialExecutable = directory.Write("app $`' folder/quota widget.exe", "synthetic");
        var preview = JsonNode.Parse(ClaudeConfiguration.Preview(specialExecutable))!.AsObject();
        var command = preview["statusLine"]!["command"]!.GetValue<string>();
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(command[(command.LastIndexOf(' ') + 1)..]));
        Assert.Contains(specialExecutable.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal),
            script, StringComparison.Ordinal);

        var target = directory.Write("target/settings.json", "{\"theme\":\"dark\"}");
        var link = directory.PathFor("link/settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        Assert.Throws<InvalidDataException>(() => ClaudeConfiguration.Apply(
            link, executable, directory.PathFor("widget/integration.json")));
        Assert.Equal("{\"theme\":\"dark\"}", File.ReadAllText(target));
    }

    private static bool ReadActive(string integration)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(integration));
        return document.RootElement.GetProperty("active").GetBoolean();
    }
}
