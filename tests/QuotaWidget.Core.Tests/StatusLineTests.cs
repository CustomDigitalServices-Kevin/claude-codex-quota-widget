using System.Text;
using QuotaWidget.Core;

namespace QuotaWidget.Core.Tests;

public sealed class StatusLineTests
{
    [Theory]
    [InlineData(300L, "5 h")]
    [InlineData(10080L, "7 j")]
    [InlineData(2880L, "2 j")]
    [InlineData(120L, "2 h")]
    [InlineData(45L, "45 min")]
    [InlineData(null, "durée inconnue")]
    public void DurationComesFromMinutes(long? minutes, string expected) =>
        Assert.Equal(expected, StatusLineFormatter.DurationLabel(minutes));

    [Fact]
    public void EmptyStateHasOnlyVersion() => Assert.Equal("v1\n",
        StatusLineFormatter.Format(StateBuilder.Build([]), TestData.Now, TimeZoneInfo.Utc));

    [Fact]
    public void UnknownFieldsRemainEmpty()
    {
        var window = TestData.Window("codex", 4.5) with { ResetsAt = null, MeasuredAt = null };
        var state = StateBuilder.Build([TestData.Observation(window)]);
        Assert.Equal("v1\ncodex\t7 j\t5\t\t\t\n", StatusLineFormatter.Format(state, TestData.Now, TimeZoneInfo.Utc));
        state = StateBuilder.Build([TestData.Observation(window with { UsedPercent = null })]);
        Assert.Equal("v1\ncodex\t7 j\t\t\t\t\n", StatusLineFormatter.Format(state, TestData.Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void ResetUsesFrenchLocalDateAndMeasurementEpoch()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
        var window = TestData.Window("codex", 4.5) with { ResetsAt = new(2026, 11, 4, 6, 35, 0, TimeSpan.Zero) };
        var result = StatusLineFormatter.Format(StateBuilder.Build([TestData.Observation(window)]), TestData.Now, zone);
        Assert.Contains("\tmer. 07:35\t" + TestData.Now.ToUnixTimeSeconds(), result, StringComparison.Ordinal);
        window = window with { ResetsAt = TestData.Now.AddHours(1) };
        result = StatusLineFormatter.Format(StateBuilder.Build([TestData.Observation(window)]), TestData.Now, zone);
        Assert.Contains("\t12:00\t", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotWritesAtomicCompactViewWithD9AndOnlyMainAccount()
    {
        using var directory = new TestDirectory();
        var old = TestData.Window("codex", 90) with { MeasuredAt = TestData.Now.AddHours(-1), ResetsAt = TestData.Now.AddDays(6) };
        var current = TestData.Window("codex", 4.5);
        Observation[] observations = [TestData.Observation(old), TestData.Observation(current),
            TestData.Observation(old) with { Account = "abcdef12" }];
        SnapshotStore.Write(directory.PathFor("snapshot.json"), new(1, TestData.Now, observations, [], []));
        var bytes = File.ReadAllBytes(directory.PathFor("statusline.tsv"));
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain('\r', text);
        Assert.Equal(2, text.Count(c => c == '\n'));
        Assert.Contains("codex\t7 j\t5\t", text, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef12", text, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(directory.Root, "*.tmp"));
    }
}
