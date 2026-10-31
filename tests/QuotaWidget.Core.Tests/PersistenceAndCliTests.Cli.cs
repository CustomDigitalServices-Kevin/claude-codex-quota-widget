using System.Globalization;
using System.Text.Json;
using QuotaWidget.Cli;

namespace QuotaWidget.Core.Tests;

public sealed partial class PersistenceAndCliTests
{
    [Theory]
    [InlineData("--json")]
    [InlineData("--arbitrage", "--json")]
    [InlineData("--text")]
    [InlineData()]
    public void CliRunsEndToEndWithTemporaryProfiles(params string[] mode)
    {
        using var directory = new TestDirectory();
        PrepareProfiles(directory);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var code = QuotasCommand.Run([.. Options(directory), .. mode], output, error,
            timeZone: TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris"));
        Assert.Equal(0, code);
        Assert.Equal("", error.ToString());
        if (mode.Contains("--json", StringComparer.Ordinal))
        {
            using var json = JsonDocument.Parse(output.ToString());
            if (mode.Contains("--arbitrage", StringComparer.Ordinal))
            {
                Assert.Equal("gpt", json.RootElement.GetProperty("verdict").GetString());
                Assert.Equal(3, json.RootElement.GetProperty("etape").GetInt32());
                Assert.True(File.Exists(directory.PathFor("data/arbitrage-etat.json")));
            }
            else
            {
                Assert.Equal("collecte_passive", json.RootElement.GetProperty("source").GetString());
                Assert.Equal(2, json.RootElement.GetProperty("claude").GetProperty("fenetres").GetArrayLength());
                Assert.Equal(24, json.RootElement.GetProperty("codex").GetProperty("fenetres")[0].GetProperty("utilise").GetDouble());
            }
            Assert.DoesNotContain("00000000-0000-4000-8000-000000000001", output.ToString(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("Plafond absolu non exposé par les sources", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Mesure Claude rafraîchie seulement par une session Claude Code interactive", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Messages ChatGPT (chat) : non exposés", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("remise dim. 21:00", output.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CliUsesFreshSnapshotAndRecomputesAgeAndPastReset()
    {
        using var directory = new TestDirectory();
        var window = TestData.Window() with { MeasuredAt = TestData.Now.AddMinutes(-10), ResetsAt = TestData.Now.AddSeconds(30) };
        var snapshot = new Snapshot(1, TestData.Now, [TestData.Observation(window)], [], []);
        SnapshotStore.Write(directory.PathFor("data/snapshot.json"), snapshot);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var options = Options(directory, TestData.Now.AddMinutes(1));
        Assert.Equal(0, QuotasCommand.Run([.. options, "--json"], output, error));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("instantane", json.RootElement.GetProperty("source").GetString());
        var resultWindow = json.RootElement.GetProperty("claude").GetProperty("fenetres")[0];
        Assert.Equal(660, resultWindow.GetProperty("age_s").GetDouble());
        Assert.True(resultWindow.GetProperty("remise_passee").GetBoolean());
    }

    [Fact]
    public void SnapshotAtTwoMinutesIsRecollectedAndEmptySourcesSucceed()
    {
        using var directory = new TestDirectory();
        SnapshotStore.Write(directory.PathFor("data/snapshot.json"),
            new(1, TestData.Now.AddMinutes(-2), [], [], []));
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(0, QuotasCommand.Run([.. Options(directory), "--json"], output, error));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("collecte_passive", json.RootElement.GetProperty("source").GetString());
        Assert.Empty(json.RootElement.GetProperty("claude").GetProperty("fenetres").EnumerateArray());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, QuotasCommand.Run([.. Options(directory), "--arbitrage", "--json"], output, error));
        using var arbitration = JsonDocument.Parse(output.ToString());
        Assert.Equal("equilibre", arbitration.RootElement.GetProperty("verdict").GetString());
        Assert.True(arbitration.RootElement.GetProperty("incertain").GetBoolean());
    }

    [Fact]
    public void FrenchAndEnglishCulturesProduceIdenticalJson()
    {
        using var directory = new TestDirectory();
        PrepareProfiles(directory);
        var original = CultureInfo.CurrentCulture;
        try
        {
            using var error = new StringWriter(CultureInfo.InvariantCulture);
            using var initial = new StringWriter(CultureInfo.InvariantCulture);
            Assert.Equal(0, QuotasCommand.Run([.. Options(directory), "--json"], initial, error));
            foreach (var mode in new[] { new[] { "--json" }, new[] { "--arbitrage", "--json" } })
            {
                using var french = new StringWriter(CultureInfo.InvariantCulture);
                using var english = new StringWriter(CultureInfo.InvariantCulture);
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                Assert.Equal(0, QuotasCommand.Run([.. Options(directory), .. mode], french, error));
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
                Assert.Equal(0, QuotasCommand.Run([.. Options(directory), .. mode], english, error));
                Assert.Equal(french.ToString(), english.ToString());
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--data-dir")]
    [InlineData("--json", "--text")]
    [InlineData("--now", "not-a-date")]
    [InlineData("--now", "2026-10-30T10:00:00")]
    public void InvalidOptionsReturnOneWithoutStackTrace(params string[] args)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(1, QuotasCommand.Run(args, output, error));
        Assert.Equal("", output.ToString());
        Assert.StartsWith("Erreur :", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteFailureReturnsOneWithShortFrenchError()
    {
        using var directory = new TestDirectory();
        directory.Write("data", "A file prevents creation of the data directory.");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(1, QuotasCommand.Run([.. Options(directory), "--json"], output, error));
        Assert.Equal("", output.ToString());
        Assert.Contains("impossible", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ParisFallTransitionUsesUtcAgeAndCorrectLocalResetTime()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
        var before = new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero);
        var after = before.AddHours(1);
        var dayBefore = before.AddDays(-1);
        Assert.Equal(TimeSpan.FromHours(2), TimeZoneInfo.ConvertTime(before, zone).Offset);
        Assert.Equal(TimeSpan.FromHours(1), TimeZoneInfo.ConvertTime(after, zone).Offset);
        Assert.Equal("remise dim. 02:30", OutputFormatter.ResetText(before, dayBefore, zone));
        Assert.Equal("remise dim. 02:30", OutputFormatter.ResetText(after, dayBefore, zone));
        Assert.Equal(3600, QuotaCalculations.Age(before, after));
        Assert.Equal("mesure il y a 1 h", OutputFormatter.AgeText(QuotaCalculations.Age(before, after)));
        Assert.Equal("mesure inconnue", OutputFormatter.AgeText(null));
        Assert.Equal("remise passée, en attente de mesure", OutputFormatter.ResetText(before, after, zone));
    }

    private static void PrepareProfiles(TestDirectory directory)
    {
        directory.Write("claude/session-01.json", File.ReadAllText(TestData.Fixture("claude", "claude-quotas.d", "session-01.json")));
        directory.Write("app/2026/10/29/rollout-app.jsonl", File.ReadAllText(TestData.Fixture("codex", "rollout-app-sample.jsonl")));
        directory.Write("cli/2026/10/30/rollout-cli.jsonl", File.ReadAllText(TestData.Fixture("codex", "rollout-cli-sample.jsonl")));
    }

    private static string[] Options(TestDirectory directory, DateTimeOffset? now = null) =>
    [
        "--claude-dir", directory.PathFor("claude"),
        "--codex-sessions", directory.PathFor("app"),
        "--codex-sessions", directory.PathFor("cli"),
        "--data-dir", directory.PathFor("data"),
        "--now", (now ?? TestData.Now).ToString("O", CultureInfo.InvariantCulture),
    ];
}
