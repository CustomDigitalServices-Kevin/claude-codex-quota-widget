using System.Text.Json;
using QuotaWidget.Core;

namespace QuotaWidget.Core.Tests;

public sealed class NativeClaudeProducerTests
{
    private const string SessionId = "11111111-2222-4333-8444-555555555555";

    [Fact]
    public void ProcessWritesOnlyWhitelistedStateAndHumanStatus()
    {
        using var directory = new TestDirectory();
        var producer = new ClaudeStatusLineProducer(directory.PathFor("state"), TestData.Clock);

        var line = producer.Process(Payload(125));

        Assert.Equal("Claude | Sonnet Test | high | UI reviewer | 5h 43% | 7d 17%", line);
        var raw = File.ReadAllText(directory.PathFor($"state/{SessionId}.json"));
        Assert.DoesNotContain("SECRET", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("private prompt", raw, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        Assert.Equal(SessionId, root.GetProperty("session_id").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("measured_at").ValueKind);
        Assert.Equal("claude-sonnet-test", root.GetProperty("model").GetProperty("id").GetString());
        Assert.Equal("Sonnet Test", root.GetProperty("model").GetProperty("display_name").GetString());
        Assert.Equal("C:\\synthetic\\workspace", root.GetProperty("cwd").GetString());
        Assert.Equal(11, root.EnumerateObject().Count());
    }

    [Fact]
    public void ProcessDoesNotFreshenSameCounterButDoesFreshenLowerCounter()
    {
        using var directory = new TestDirectory();
        var state = directory.PathFor($"state/{SessionId}.json");
        new ClaudeStatusLineProducer(directory.PathFor("state"),
            new TestClock(TestData.Now)).Process(Payload(125));
        new ClaudeStatusLineProducer(directory.PathFor("state"),
            new TestClock(TestData.Now.AddMinutes(1))).Process(Payload(126));
        var changed = Measurement(state);

        new ClaudeStatusLineProducer(directory.PathFor("state"),
            new TestClock(TestData.Now.AddMinutes(2))).Process(Payload(126));
        Assert.Equal(changed, Measurement(state));

        new ClaudeStatusLineProducer(directory.PathFor("state"),
            new TestClock(TestData.Now.AddMinutes(3))).Process(Payload(2));
        Assert.Equal(TestData.Now.AddMinutes(3), Measurement(state));
    }

    [Fact]
    public void ProcessPreservesEpochAndIsoResetTypesAndOmitsInvalidNumbers()
    {
        using var directory = new TestDirectory();
        var json = $$$$"""
            {"session_id":"{{{{SessionId}}}}","cost":{"total_api_duration_ms":"unknown"},
             "context_window":{"total_input_tokens":-1},"rate_limits":{
               "five_hour":{"used_percentage":25.25,"resets_at":1893459600},
               "seven_day":{"used_percentage":101,"resets_at":"2030-01-07T01:00:00+01:00"}}}
            """;

        new ClaudeStatusLineProducer(directory.PathFor("state"), TestData.Clock).Process(json);

        using var state = JsonDocument.Parse(File.ReadAllText(directory.PathFor($"state/{SessionId}.json")));
        var root = state.RootElement;
        Assert.False(root.TryGetProperty("api_duration_ms", out _));
        Assert.False(root.TryGetProperty("input_tokens", out _));
        var five = root.GetProperty("rate_limits").GetProperty("five_hour");
        Assert.Equal(JsonValueKind.Number, five.GetProperty("resets_at").ValueKind);
        Assert.Equal(1893459600, five.GetProperty("resets_at").GetInt64());
        var seven = root.GetProperty("rate_limits").GetProperty("seven_day");
        Assert.False(seven.TryGetProperty("used_percentage", out _));
        Assert.Equal(JsonValueKind.String, seven.GetProperty("resets_at").ValueKind);
    }

    [Fact]
    public void ProcessRejectsOversizeAndInvalidSessionWithoutWriting()
    {
        using var directory = new TestDirectory();
        var producer = new ClaudeStatusLineProducer(directory.PathFor("state"), TestData.Clock);

        var oversized = Assert.Throws<InvalidDataException>(() => producer.Process(new string('x', 1024 * 1024 + 1)));
        var invalid = Assert.Throws<InvalidDataException>(() => producer.Process("{\"session_id\":\"../private\"}"));

        Assert.Equal("Claude integration data is invalid.", oversized.Message);
        Assert.Equal(oversized.Message, invalid.Message);
        Assert.False(Directory.Exists(directory.PathFor("state")));
    }

    [Fact]
    public void ProcessDoesNotReadOversizedPriorState()
    {
        using var directory = new TestDirectory();
        var state = directory.Write($"state/{SessionId}.json", new string(' ', 1024 * 1024 + 1));

        new ClaudeStatusLineProducer(directory.PathFor("state"), TestData.Clock).Process(Payload(125));

        Assert.InRange(new FileInfo(state).Length, 1, 1024 * 1024);
        using var document = JsonDocument.Parse(File.ReadAllText(state));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("measured_at").ValueKind);
    }

    [Fact]
    public void ProcessUsesNeutralStatusWhenNoOptionalDataExists()
    {
        using var directory = new TestDirectory();
        var producer = new ClaudeStatusLineProducer(directory.PathFor("state"), TestData.Clock);
        Assert.Equal("Claude", producer.Process($"{{\"session_id\":\"{SessionId}\"}}"));
    }

    private static DateTimeOffset? Measurement(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var value = document.RootElement.GetProperty("measured_at");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetDateTimeOffset();
    }

    private static string Payload(double apiDuration) => $$$$"""
        {"session_id":"{{{{SessionId}}}}","session_name":"Review dashboard","cwd":"C:\\synthetic\\fallback",
         "workspace":{"current_dir":"C:\\synthetic\\workspace"},
         "model":{"id":"claude-sonnet-test","display_name":"Sonnet Test"},
         "effort":{"level":"high"},"agent":{"name":"UI reviewer"},
         "cost":{"total_api_duration_ms":{{{{apiDuration}}}},"SECRET":"never-write-me"},
         "context_window":{"total_input_tokens":321,"prompt":"private prompt"},
         "rate_limits":{"five_hour":{"used_percentage":42.5,"resets_at":"2030-01-01T01:00:00Z"},
                        "seven_day":{"used_percentage":17,"resets_at":"2030-01-07T01:00:00Z"}}}
        """;
}
