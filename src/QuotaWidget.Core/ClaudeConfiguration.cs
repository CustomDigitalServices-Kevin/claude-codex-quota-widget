using System.Text;
using System.Text.Json.Nodes;

namespace QuotaWidget.Core;

public sealed record ClaudeConfigurationResult(
    bool Changed,
    string SettingsPath,
    string? BackupPath,
    string Command,
    string Message);

public static class ClaudeConfiguration
{
    private const int MetadataVersion = 1;
    private static readonly TimeProvider Clock = TimeProvider.System;

    public static string Preview(string executablePath)
    {
        try
        {
            var command = BuildCommand(executablePath, out _);
            return Fragment(command).ToJsonString(ClaudeLocalFiles.CompactJson);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (IsConfigurationFailure(exception)) { throw ClaudeLocalFiles.Invalid(); }
    }

    public static ClaudeConfigurationResult Apply(string settingsPath, string executablePath,
        string integrationPath, bool replaceExisting = false)
    {
        try { return ApplyCore(settingsPath, executablePath, integrationPath, replaceExisting); }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (IsConfigurationFailure(exception)) { throw ClaudeLocalFiles.Invalid(); }
    }

    private static ClaudeConfigurationResult ApplyCore(string settingsPath, string executablePath,
        string integrationPath, bool replaceExisting)
    {
        var command = BuildCommand(executablePath, out var executable);
        var settings = ClaudeLocalFiles.FullPath(settingsPath);
        var integration = ClaudeLocalFiles.FullPath(integrationPath);
        ValidateDistinct(settings, executable, integration);
        ClaudeLocalFiles.AssertSafeTarget(settings);
        ClaudeLocalFiles.AssertSafeTarget(integration);
        RejectActiveIntegration(integration);

        var existed = File.Exists(settings);
        var originalBytes = existed ? ClaudeLocalFiles.ReadBytes(settings) : null;
        var root = originalBytes is null ? new JsonObject() : ClaudeLocalFiles.ParseObject(originalBytes);
        var hadStatusLine = root.TryGetPropertyValue("statusLine", out var priorStatusLine);
        if (hadStatusLine && !replaceExisting)
        {
            throw ClaudeLocalFiles.Invalid();
        }

        var backupPath = existed ? ClaudeLocalFiles.NewBackupPath(settings, Clock) : null;
        root["statusLine"] = Fragment(command)["statusLine"]?.DeepClone();
        var settingsBytes = ClaudeLocalFiles.Serialize(root);
        var metadata = Metadata(settings, executable, command, backupPath, hadStatusLine, priorStatusLine);
        var metadataBytes = ClaudeLocalFiles.Serialize(metadata);

        if (backupPath is not null)
        {
            ClaudeLocalFiles.CopyBackup(settings, backupPath);
        }
        try
        {
            ClaudeLocalFiles.AtomicWrite(settings, settingsBytes);
            try { ClaudeLocalFiles.AtomicWrite(integration, metadataBytes); }
            catch
            {
                RestoreOriginal(settings, originalBytes);
                throw;
            }
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ClaudeLocalFiles.Invalid();
        }

        return new(true, settings, backupPath, command, "Claude status line configured.");
    }

    public static ClaudeConfigurationResult Remove(string settingsPath, string executablePath,
        string integrationPath)
    {
        try { return RemoveCore(settingsPath, executablePath, integrationPath); }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (IsConfigurationFailure(exception)) { throw ClaudeLocalFiles.Invalid(); }
    }

    private static ClaudeConfigurationResult RemoveCore(string settingsPath, string executablePath,
        string integrationPath)
    {
        var command = BuildCommand(executablePath, out var executable);
        var settings = ClaudeLocalFiles.FullPath(settingsPath);
        var integration = ClaudeLocalFiles.FullPath(integrationPath);
        ValidateDistinct(settings, executable, integration);
        ClaudeLocalFiles.AssertSafeTarget(settings);
        ClaudeLocalFiles.AssertSafeTarget(integration);
        if (!File.Exists(integration))
        {
            return new(false, settings, null, command, "No owned Claude status line was recorded.");
        }

        var metadata = ClaudeLocalFiles.ParseObject(ClaudeLocalFiles.ReadBytes(integration));
        var backupPath = NullableString(metadata, "backup_path");
        ValidateMetadata(metadata, settings, executable, command);
        if (!RequiredBoolean(metadata, "active"))
        {
            return new(false, settings, backupPath, command, "Claude status line is already removed.");
        }
        if (!File.Exists(settings))
        {
            RetireIntegration(metadata, integration);
            return new(true, settings, backupPath, command, "Claude settings are absent; ownership record retired.");
        }

        var settingsBytes = ClaudeLocalFiles.ReadBytes(settings);
        var root = ClaudeLocalFiles.ParseObject(settingsBytes);
        if (!OwnsCurrentStatusLine(root, command))
        {
            RetireIntegration(metadata, integration);
            return new(true, settings, backupPath, command, "Claude status line was changed by the user; ownership record retired.");
        }

        RestoreStatusLine(root, metadata);
        var restoredBytes = ClaudeLocalFiles.Serialize(root);
        metadata["active"] = false;
        var inactiveBytes = ClaudeLocalFiles.Serialize(metadata);
        try
        {
            ClaudeLocalFiles.AtomicWrite(settings, restoredBytes);
            try { ClaudeLocalFiles.AtomicWrite(integration, inactiveBytes); }
            catch
            {
                ClaudeLocalFiles.AtomicWrite(settings, settingsBytes);
                throw;
            }
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ClaudeLocalFiles.Invalid();
        }
        return new(true, settings, backupPath, command, "Claude status line removed.");
    }

    private static JsonObject Fragment(string command) => new()
    {
        ["statusLine"] = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["padding"] = 0,
        },
    };

    private static JsonObject Metadata(string settings, string executable, string command,
        string? backupPath, bool hadStatusLine, JsonNode? priorStatusLine) => new()
    {
        ["version"] = MetadataVersion,
        ["active"] = true,
        ["settings_path"] = settings,
        ["executable_path"] = executable,
        ["command"] = command,
        ["backup_path"] = backupPath,
        ["had_status_line"] = hadStatusLine,
        ["prior_status_line"] = priorStatusLine?.DeepClone(),
    };

    private static string BuildCommand(string executablePath, out string executable)
    {
        executable = ClaudeLocalFiles.FullPath(executablePath);
        ClaudeLocalFiles.AssertRegularFile(executable);
        var normalized = executable.Replace('\\', '/');
        if (normalized.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw ClaudeLocalFiles.Invalid();
        }
        var escaped = normalized.Replace("'", "''", StringComparison.Ordinal);
        var script = "$u=[Text.UTF8Encoding]::new($false,$true);"
            + "[Console]::InputEncoding=$u;[Console]::OutputEncoding=$u;"
            + $"& '{escaped}' --claude-statusline;exit $LASTEXITCODE";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return $"powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}";
    }

    private static void ValidateDistinct(params string[] paths)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (paths.Distinct(comparer).Count() != paths.Length)
        {
            throw ClaudeLocalFiles.Invalid();
        }
    }

    private static void RejectActiveIntegration(string integration)
    {
        if (!File.Exists(integration)) { return; }
        var metadata = ClaudeLocalFiles.ParseObject(ClaudeLocalFiles.ReadBytes(integration));
        if (RequiredBoolean(metadata, "active"))
        {
            throw ClaudeLocalFiles.Invalid();
        }
    }

    private static void ValidateMetadata(JsonObject metadata, string settings, string executable, string command)
    {
        if (RequiredInteger(metadata, "version") != MetadataVersion
            || !PathEquals(RequiredString(metadata, "settings_path"), settings)
            || !PathEquals(RequiredString(metadata, "executable_path"), executable)
            || RequiredString(metadata, "command") != command
            || !metadata.ContainsKey("had_status_line") || !metadata.ContainsKey("prior_status_line"))
        {
            throw ClaudeLocalFiles.Invalid();
        }
        _ = RequiredBoolean(metadata, "had_status_line");
    }

    private static bool OwnsCurrentStatusLine(JsonObject root, string command) =>
        root["statusLine"] is JsonObject statusLine
        && statusLine.Count == 3
        && StringValue(statusLine["type"]) == "command"
        && StringValue(statusLine["command"]) == command
        && statusLine["padding"] is JsonValue padding
        && padding.TryGetValue<int>(out var value)
        && value == 0;

    private static void RetireIntegration(JsonObject metadata, string integration)
    {
        metadata["active"] = false;
        ClaudeLocalFiles.AtomicWrite(integration, ClaudeLocalFiles.Serialize(metadata));
    }

    private static void RestoreStatusLine(JsonObject root, JsonObject metadata)
    {
        if (RequiredBoolean(metadata, "had_status_line"))
        {
            root["statusLine"] = metadata["prior_status_line"]?.DeepClone();
        }
        else
        {
            root.Remove("statusLine");
        }
    }

    private static void RestoreOriginal(string settings, byte[]? originalBytes)
    {
        if (originalBytes is not null)
        {
            ClaudeLocalFiles.AtomicWrite(settings, originalBytes);
        }
        else if (File.Exists(settings))
        {
            ClaudeLocalFiles.AssertRegularFile(settings);
            File.Delete(settings);
        }
    }

    private static string RequiredString(JsonObject value, string name) =>
        StringValue(value[name]) ?? throw ClaudeLocalFiles.Invalid();

    private static string? NullableString(JsonObject value, string name)
    {
        if (!value.ContainsKey(name)) { throw ClaudeLocalFiles.Invalid(); }
        return value[name] is null ? null : RequiredString(value, name);
    }

    private static string? StringValue(JsonNode? node) => node is JsonValue value
        && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
    private static bool RequiredBoolean(JsonObject value, string name) => value[name] is JsonValue item
        && item.TryGetValue<bool>(out var result) ? result : throw ClaudeLocalFiles.Invalid();
    private static int RequiredInteger(JsonObject value, string name) => value[name] is JsonValue item
        && item.TryGetValue<int>(out var result) ? result : throw ClaudeLocalFiles.Invalid();
    private static bool PathEquals(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool IsConfigurationFailure(Exception exception) => exception is ArgumentException
        or IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
