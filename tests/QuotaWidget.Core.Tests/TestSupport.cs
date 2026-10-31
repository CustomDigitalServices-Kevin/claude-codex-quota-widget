using System.Globalization;
using System.Text.Json;
using QuotaWidget.Core;

namespace QuotaWidget.Core.Tests;

internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class TestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));

    public TestDirectory() => Directory.CreateDirectory(Root);

    public string PathFor(string name) => Path.GetFullPath(Path.Combine(Root, name));

    public string Write(string relative, string content)
    {
        var path = PathFor(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Root);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(Root, true);
}

internal static class TestData
{
    public static DateTimeOffset Now { get; } = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);
    public static TestClock Clock { get; } = new(Now);
    public static string Fixture(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "fixtures", .. parts]);

    public static WindowObservation Window(string provider = "claude", double? used = 40,
        double remaining = 0.5, string? type = null, long? duration = 10080,
        DateTimeOffset? measured = null, string source = "fixture") =>
        new(provider, provider == "claude" ? "claude" : "codex", type ?? (provider == "claude" ? "seven_day" : "primary"),
            duration, used, duration is { } minutes ? Now.AddMinutes(minutes * remaining) : null,
            measured ?? Now, Now, source);

    public static ProviderState Provider(string provider, double? used = 40, double remaining = 0.5,
        bool blocked = false, double? fiveHour = null)
    {
        List<WindowObservation> windows = [];
        if (used is not null)
        {
            windows.Add(Window(provider, used, remaining));
        }
        if (fiveHour is { } five)
        {
            windows.Add(Window(provider, five, 0.5, "five_hour", 300));
        }
        return new(provider, provider == "codex" ? JsonSupport.Fingerprint("account-a") : null,
            null, [.. windows], blocked ? new("bloque", Now.AddMinutes(-5), "fixture") : Blockage.Unknown,
            null, null, null, null);
    }

    public static Observation Observation(WindowObservation window) =>
        new(window.Provider, window.Source, window.ObservedAt, window.MeasuredAt, [window],
            window.Provider == "codex" ? JsonSupport.Fingerprint("account-a") : null, Counter: window.Counter);

    public static string Journal(double used = 4.5, DateTimeOffset? at = null,
        bool reachedPresent = true, string? reached = null, bool successful = true, string counter = "codex",
        DateTimeOffset? resetsAt = null)
    {
        Dictionary<string, object?> limits = new(StringComparer.Ordinal)
        {
            ["limit_id"] = counter,
            ["primary"] = new { used_percent = used, window_minutes = 10080, resets_at = (resetsAt ?? Now.AddDays(5)).ToUnixTimeSeconds() },
            ["credits"] = new { balance = "12.50", unlimited = false, has_credits = true },
            ["plan_type"] = "pro",
        };
        if (reachedPresent)
        {
            limits["rate_limit_reached_type"] = reached;
        }
        return JsonSerializer.Serialize(new
        {
            timestamp = (at ?? Now).ToString("O", CultureInfo.InvariantCulture),
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = successful ? new { last_token_usage = new { total_tokens = 12 } } : null,
                rate_limits = limits,
            },
        });
    }

    public static string Metadata(string? account = "account-a") => JsonSerializer.Serialize(new
    {
        type = "session_meta", payload = new { creator_account_id = account, originator = "test-executor" },
    });

    public static Observation ParseJournal(string line, string source = "fixture") =>
        CodexParser.ParseJournal(line, source, Now, JsonSupport.Fingerprint("account-a"))
        ?? throw new InvalidDataException("The constructed event must contain rate limits.");
}
