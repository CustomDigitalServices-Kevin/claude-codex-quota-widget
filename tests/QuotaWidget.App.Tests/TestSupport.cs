using System.IO;
using QuotaWidget.Core;

namespace QuotaWidget.App.Tests;

internal sealed class TestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Root);
    public string PathFor(string name) => Path.Combine(Root, name);
    public void Dispose() => Directory.Delete(Root, true);
}

internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
}

internal static class TestData
{
    public static DateTimeOffset Now { get; } = new(2026, 10, 30, 10, 31, 0, TimeSpan.FromHours(1));
    public static WindowObservation Window(string provider, long duration, double used, DateTimeOffset reset, int age = 2) =>
        new(provider, provider, duration == 300 ? "five_hour" : "seven_day", duration, used, reset,
            Now.AddMinutes(-age), Now, "constructed-fixture");
    public static ProviderState Provider(string provider, params WindowObservation[] windows) =>
        new(provider, null, null, windows, Blockage.Unknown, null, null, null, null);
    public static QuotaState State { get; } = new(
        Provider("claude", Window("claude", 300, 31, Now.AddMinutes(19)),
            Window("claude", 10080, 68, new(2026, 11, 1, 21, 0, 0, TimeSpan.FromHours(1)))),
        Provider("codex", Window("codex", 10080, 4, new(2026, 11, 4, 7, 35, 0, TimeSpan.FromHours(1)), 1)), []);
    public static Snapshot Snapshot { get; } = new(1, Now, [], [], []);
}
