using System.Globalization;
using System.Text.Json;
using QuotaWidget.Cli;

namespace QuotaWidget.Core.Tests;

public sealed partial class PersistenceAndCliTests
{
    [Fact]
    public async Task AtomicReplacementRetriesUntilWindowsReaderReleasesTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var directory = new TestDirectory();
        var path = directory.PathFor("snapshot.json");
        AtomicJsonFile.Write(path, new { Version = 1 });
        using var lockAcquired = new ManualResetEventSlim();
        using var releaseLock = new ManualResetEventSlim();
        using var writerStarted = new ManualResetEventSlim();
        var locker = Task.Run(() =>
        {
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            lockAcquired.Set();
            releaseLock.Wait();
        });
        Assert.True(lockAcquired.Wait(TimeSpan.FromSeconds(10)), "The target lock must be acquired before the writer starts.");
        var writer = Task.Run(() =>
        {
            writerStarted.Set();
            AtomicJsonFile.Write(path, new { Version = 2 });
        });
        try
        {
            Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(10)), "The writer must start while the target is locked.");
            await Task.Delay(600);
            Assert.False(writer.IsCompleted, "The writer must keep retrying beyond the former 250 ms budget.");
        }
        finally
        {
            releaseLock.Set();
            await locker;
        }
        await writer;
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(2, json.RootElement.GetProperty("version").GetInt32());
        Assert.Empty(Directory.GetFiles(directory.Root, "*.tmp"));
    }

    [Fact]
    public void AtomicReplacementFailureIsBoundedPreservesTargetAndRemovesTemporary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var directory = new TestDirectory();
        var path = directory.PathFor("snapshot.json");
        AtomicJsonFile.Write(path, new { Version = 1 });
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var exception = Record.Exception(() => AtomicJsonFile.Write(path, new { Version = 2 }));
            Assert.True(exception is IOException or UnauthorizedAccessException);
            Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(1900));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(4));
        }
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.Empty(Directory.GetFiles(directory.Root, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CliReplacesObsoleteCyclesOnCollectionAndCachedSnapshotWithoutDoubleCounting(bool reverse)
    {
        using var directory = new TestDirectory();
        var now = new DateTimeOffset(2026, 10, 30, 10, 31, 0, TimeSpan.Zero);
        var old = TestData.Metadata() + "\n" + TestData.Journal(23,
            at: new(2026, 10, 28, 6, 35, 0, TimeSpan.Zero),
            resetsAt: new(2026, 11, 2, 6, 50, 0, TimeSpan.Zero)) + "\n";
        var recent = TestData.Metadata() + "\n" + TestData.Journal(5, at: now,
            resetsAt: new(2026, 11, 4, 6, 35, 0, TimeSpan.Zero)) + "\n";
        var first = directory.Write("app/2026/10/30/rollout-a.jsonl", reverse ? recent : old);
        var second = directory.Write("app/2026/10/30/rollout-b.jsonl", reverse ? old : recent);
        File.SetLastWriteTimeUtc(first, now.UtcDateTime);
        File.SetLastWriteTimeUtc(second, now.AddMinutes(-1).UtcDateTime);
        foreach (var source in new[] { "collecte_passive", "instantane" })
        {
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            using var error = new StringWriter(CultureInfo.InvariantCulture);
            Assert.Equal(0, QuotasCommand.Run([.. Options(directory, now), "--json"], output, error));
            Assert.Equal("", error.ToString());
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(source, json.RootElement.GetProperty("source").GetString());
            var window = Assert.Single(json.RootElement.GetProperty("codex").GetProperty("fenetres").EnumerateArray());
            Assert.Equal(5, window.GetProperty("utilise").GetDouble());
            var diagnostic = Assert.Single(json.RootElement.GetProperty("diagnostics").EnumerateArray(),
                d => d.GetProperty("code").GetString() == WindowAggregator.ReplacedDiagnosticCode);
            Assert.Equal(1, diagnostic.GetProperty("nombre").GetInt32());
        }
        List<Diagnostic> readDiagnostics = [];
        var snapshot = Assert.IsType<Snapshot>(SnapshotStore.Read(directory.PathFor("data/snapshot.json"), readDiagnostics));
        Assert.Equal(1, Assert.Single(snapshot.Diagnostics, d => d.Code == WindowAggregator.ReplacedDiagnosticCode).Count);
        using var arbitration = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(0, QuotasCommand.Run([.. Options(directory, now), "--arbitrage", "--json"], arbitration, errors));
        using var result = JsonDocument.Parse(arbitration.ToString());
        Assert.Equal(5, result.RootElement.GetProperty("codex").GetProperty("utilise").GetDouble());
        Assert.Equal("gpt", result.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(3, result.RootElement.GetProperty("etape").GetInt32());
    }

    [Fact]
    public async Task ReaderNeverSeesPartialJsonDuringTwoHundredAtomicReplacements()
    {
        using var directory = new TestDirectory();
        var path = directory.PathFor("snapshot.json");
        var observation = TestData.ParseJournal(TestData.Journal());
        var snapshot = new Snapshot(1, TestData.Now, [observation], [], []);
        SnapshotStore.Write(path, snapshot);
        using var stop = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var reads = 0;
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var json = JsonDocument.Parse(stream))
                {
                    Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
                    Assert.Single(json.RootElement.GetProperty("observations").EnumerateArray());
                    Interlocked.Increment(ref reads);
                    started.Set();
                }
                // Leave a deterministic scheduling window for the atomic mover instead of
                // immediately reacquiring the file in a tight loop for the entire two-second budget.
                Thread.Sleep(1);
            }
        });
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "The concurrent reader must start before replacements.");
            for (var index = 0; index < 200; index++)
            {
                SnapshotStore.Write(path, snapshot with { WrittenAt = TestData.Now.AddSeconds(index) });
            }
        }
        finally
        {
            await stop.CancelAsync();
            await reader;
        }
        Assert.True(reads > 0);
        Assert.Empty(Directory.GetFiles(directory.Root, "*.tmp"));
    }

    [Fact]
    public void SnapshotContainsObservationsAndFingerprintsWithoutVerdictsOrRawAccounts()
    {
        using var directory = new TestDirectory();
        var observations = CodexParser.ParseAppServer(
            File.ReadAllText(TestData.Fixture("constructed", "app-server-multiple-buckets.json")), "constructed", TestData.Now);
        var snapshot = new Snapshot(1, TestData.Now, observations, StateBuilder.SaveBlockages(StateBuilder.Build(observations)), []);
        var path = directory.PathFor("snapshot.json");
        SnapshotStore.Write(path, snapshot);
        var content = File.ReadAllText(path);
        Assert.DoesNotContain("constructed-account", content, StringComparison.Ordinal);
        Assert.DoesNotContain("verdict", content, StringComparison.Ordinal);
        Assert.Contains("measured_at", content, StringComparison.Ordinal);
        Assert.Contains("observed_at", content, StringComparison.Ordinal);
        List<Diagnostic> diagnostics = [];
        var restored = Assert.IsType<Snapshot>(SnapshotStore.Read(path, diagnostics));
        Assert.Equal(observations.Length, restored.Observations.Length);
        Assert.Equal(observations[0].Account, restored.Observations[0].Account);
        Assert.Empty(diagnostics);
        Assert.Throws<ArgumentException>(() => SnapshotStore.Write(path,
            snapshot with { Observations = [observations[0] with { Account = "raw-account" }] }));
    }

    [Fact]
    public void CorruptOrUnsupportedSnapshotFallsBackWithDiagnostic()
    {
        using var directory = new TestDirectory();
        foreach (var json in new[] { "{broken", "{}", "null", "{\"version\":999}" })
        {
            var path = directory.Write("snapshot.json", json);
            List<Diagnostic> diagnostics = [];
            Assert.Null(SnapshotStore.Read(path, diagnostics));
            Assert.Single(diagnostics);
        }
    }

    [Fact]
    public void CollectionKeepsLastKnownMeasurementWithoutRefreshingItsTimestamp()
    {
        using var directory = new TestDirectory();
        var claudePath = directory.Write("claude/session.json",
            File.ReadAllText(TestData.Fixture("claude", "claude-quotas.d", "session-01.json")));
        var collector = new QuotaCollector(TestData.Clock);
        var original = collector.Collect(directory.PathFor("claude"), [directory.PathFor("codex")]);
        File.WriteAllText(claudePath, "{broken");
        var next = new QuotaCollector(new TestClock(TestData.Now.AddHours(7)))
            .Collect(directory.PathFor("claude"), [directory.PathFor("codex")], original);
        Assert.Equal(original.Observations[0].MeasuredAt, Assert.Single(next.Observations).MeasuredAt);
        Assert.Contains(next.Diagnostics, d => d.Code == "derniere_mesure_conservee");
        var state = StateBuilder.Build(next.Observations, next.Blockages);
        Assert.True(Arbitrator.Decide(state.Claude, state.Codex, null, TestData.Now.AddHours(7)).Uncertain);
        File.Delete(claudePath);
        var missing = collector.Collect(directory.PathFor("claude"), [directory.PathFor("codex")], next);
        Assert.Single(missing.Observations);
    }

}
