using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuotaWidget.Core;

public sealed class ClaudeStatusLineProducer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] ResetFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
    ];
    private readonly string stateDirectory;
    private readonly TimeProvider clock;

    public ClaudeStatusLineProducer(string stateDirectory, TimeProvider? clock = null)
    {
        this.stateDirectory = ClaudeLocalFiles.FullPath(stateDirectory);
        this.clock = clock ?? TimeProvider.System;
    }

    public string Process(string json)
    {
        try { return ProcessCore(json); }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or UnauthorizedAccessException or JsonException or DecoderFallbackException
            or System.Security.SecurityException)
        {
            throw ClaudeLocalFiles.Invalid();
        }
    }

    private string ProcessCore(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var inputBytes = StrictUtf8.GetBytes(json);
        if (inputBytes.Length > ClaudeLocalFiles.MaximumBytes)
        {
            throw ClaudeLocalFiles.Invalid();
        }

        using var document = JsonDocument.Parse(inputBytes, new() { MaxDepth = 20 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw ClaudeLocalFiles.Invalid();
        }

        var sessionId = SafeString(root.Property("session_id"), 128);
        if (sessionId is null || !Guid.TryParseExact(sessionId, "D", out _))
        {
            throw ClaudeLocalFiles.Invalid();
        }

        PrepareStateDirectory();
        var statePath = Path.Combine(stateDirectory, $"{sessionId}.json");
        var previous = ReadPrevious(statePath, sessionId);
        var now = clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        var apiDuration = NonNegative(root.Property("cost").Property("total_api_duration_ms"));
        var measuredAt = PreviousMeasurement(previous, apiDuration, now);
        var state = BuildState(root, sessionId, now, measuredAt, apiDuration);
        ClaudeLocalFiles.AtomicWrite(statePath, ClaudeLocalFiles.Serialize(state));
        return BuildLine(state);
    }

    private void PrepareStateDirectory()
    {
        if (Directory.Exists(stateDirectory))
        {
            ClaudeLocalFiles.AssertSafeDirectoryChain(stateDirectory);
        }
        else if (File.Exists(stateDirectory))
        {
            throw ClaudeLocalFiles.Invalid();
        }
        else
        {
            ClaudeLocalFiles.AssertSafeDirectoryChain(Path.GetDirectoryName(stateDirectory) ?? throw ClaudeLocalFiles.Invalid());
            Directory.CreateDirectory(stateDirectory);
            ClaudeLocalFiles.AssertSafeDirectoryChain(stateDirectory);
        }
    }

    private static JsonObject? ReadPrevious(string path, string sessionId)
    {
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) { throw ClaudeLocalFiles.Invalid(); }
            return null;
        }
        ClaudeLocalFiles.AssertRegularFile(path);
        try
        {
            var value = ClaudeLocalFiles.ParseObject(ClaudeLocalFiles.ReadBytes(path));
            return StringValue(value["session_id"], 128) == sessionId ? value : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static string? PreviousMeasurement(JsonObject? previous, double? current, string now)
    {
        if (previous is null)
        {
            return null;
        }
        var measured = StringValue(previous["measured_at"], 80);
        if (current is null)
        {
            return measured;
        }
        var prior = NumberValue(previous["api_duration_ms"]);
        return prior is not null && prior == current ? measured : now;
    }

    private static JsonObject BuildState(JsonElement root, string sessionId, string now,
        string? measuredAt, double? apiDuration)
    {
        var state = new JsonObject
        {
            ["session_id"] = sessionId,
            ["rendered_at"] = now,
            ["measured_at"] = measuredAt,
        };
        AddNumber(state, "api_duration_ms", apiDuration);
        AddNumber(state, "input_tokens", NonNegative(root.Property("context_window").Property("total_input_tokens")));
        var modelId = SafeString(root.Property("model").Property("id"), 160);
        if (modelId is not null)
        {
            AddObject(state, "model", ("id", modelId),
                ("display_name", SafeString(root.Property("model").Property("display_name"), 80)));
        }
        AddObject(state, "effort", ("level", SafeString(root.Property("effort").Property("level"), 40)));
        AddObject(state, "agent", ("name", SafeString(root.Property("agent").Property("name"), 120)));
        AddString(state, "session_name", SafeString(root.Property("session_name"), 200));
        AddString(state, "cwd", SafeString(root.Property("workspace").Property("current_dir"), 1024)
            ?? SafeString(root.Property("cwd"), 1024));
        var rates = new JsonObject();
        AddRateWindow(rates, "five_hour", root.Property("rate_limits").Property("five_hour"));
        AddRateWindow(rates, "seven_day", root.Property("rate_limits").Property("seven_day"));
        if (rates.Count > 0) { state["rate_limits"] = rates; }
        return state;
    }

    private static void AddRateWindow(JsonObject rates, string name, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) { return; }
        var window = new JsonObject();
        var used = input.Property("used_percentage").Number();
        if (used is >= 0 and <= 100) { window["used_percentage"] = used.Value; }
        if (ResetValue(input.Property("resets_at")) is { } reset) { window["resets_at"] = reset; }
        if (window.Count > 0) { rates[name] = window; }
    }

    private static JsonValue? ResetValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch)
            && epoch is >= 0 and <= 253402300799L)
        {
            return JsonValue.Create(epoch);
        }
        var text = SafeString(value, 80);
        return text is not null && DateTimeOffset.TryParseExact(text, ResetFormats,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? JsonValue.Create(text) : null;
    }

    private static string BuildLine(JsonObject state)
    {
        List<string> parts = [];
        var model = state["model"] as JsonObject;
        AddPart(parts, StringValue(model?["display_name"], 80) ?? StringValue(model?["id"], 160));
        AddPart(parts, StringValue((state["effort"] as JsonObject)?["level"], 40));
        AddPart(parts, StringValue((state["agent"] as JsonObject)?["name"], 120)
            ?? StringValue(state["session_name"], 200));
        var rates = state["rate_limits"] as JsonObject;
        AddPercentage(parts, "5h", (rates?["five_hour"] as JsonObject)?["used_percentage"]);
        AddPercentage(parts, "7d", (rates?["seven_day"] as JsonObject)?["used_percentage"]);
        return parts.Count == 0 ? "Claude" : $"Claude | {string.Join(" | ", parts)}";
    }

    private static void AddPercentage(List<string> parts, string label, JsonNode? value)
    {
        if (NumberValue(value) is { } number)
        {
            parts.Add($"{label} {Math.Round(number, 0, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}%");
        }
    }

    private static void AddPart(List<string> parts, string? value) { if (value is not null) { parts.Add(value); } }
    private static void AddString(JsonObject target, string name, string? value) { if (value is not null) { target[name] = value; } }
    private static void AddNumber(JsonObject target, string name, double? value) { if (value is not null) { target[name] = value.Value; } }

    private static void AddObject(JsonObject target, string name, params (string Name, string? Value)[] values)
    {
        var child = new JsonObject();
        foreach (var value in values) { AddString(child, value.Name, value.Value); }
        if (child.Count > 0) { target[name] = child; }
    }

    private static double? NonNegative(JsonElement value)
    {
        var number = value.Number();
        return number is >= 0 ? number : null;
    }
    private static string? SafeString(JsonElement value, int maximum) => value.Text() is { } text ? Safe(text, maximum) : null;
    private static string? StringValue(JsonNode? node, int maximum) => node is JsonValue value
        && value.TryGetValue<string>(out var text) ? Safe(text, maximum) : null;
    private static double? NumberValue(JsonNode? node) => node is JsonValue value
        && value.TryGetValue<double>(out var number) && double.IsFinite(number) && number >= 0 ? number : null;
    private static string? Safe(string text, int maximum) => !string.IsNullOrWhiteSpace(text)
        && text.Length <= maximum && text.IndexOfAny(['\0', '\r', '\n']) < 0 ? text : null;
}
