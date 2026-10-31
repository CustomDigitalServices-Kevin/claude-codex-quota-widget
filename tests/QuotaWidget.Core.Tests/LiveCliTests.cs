using System.Globalization;
using System.Text.Json;
using QuotaWidget.Cli;
using QuotaWidget.Core;

namespace QuotaWidget.Core.Tests;

[CollectionDefinition("Fake Codex environment", DisableParallelization = true)]
public sealed class FakeCodexEnvironmentTestGroup
{
}

[Collection("Fake Codex environment")]
public sealed class LiveCliTests
{
    private static readonly string[] AllowedSequence = ["initialize", "initialized", "account/rateLimits/read"];
    [Fact]
    public void LiveReadsFakeExecutableAndSecondCallerUsesSnapshotWithFrenchReason()
    {
        using var directory = new TestDirectory();
        var scenario = FakeCodex.Scenario(directory);
        var saved = Environment.GetEnvironmentVariable("QUOTAWIDGET_FAKE_SCENARIO");
        Environment.SetEnvironmentVariable("QUOTAWIDGET_FAKE_SCENARIO", scenario);
        try
        {
            string[] args = ["--json", "--live", "--codex-executable", FakeCodex.Executable,
                "--claude-dir", directory.PathFor("claude"), "--codex-sessions", directory.PathFor("sessions"),
                "--data-dir", directory.PathFor("data"), "--now", TestData.Now.ToString("O", CultureInfo.InvariantCulture)];
            using var first = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, QuotasCommand.Run(args, first, error));
            Assert.Empty(error.ToString());
            using var json = JsonDocument.Parse(first.ToString());
            Assert.Equal("collecte_directe", json.RootElement.GetProperty("source").GetString());
            Assert.Contains("12.5", first.ToString(), StringComparison.Ordinal);
            using var second = new StringWriter();
            Assert.Equal(0, QuotasCommand.Run(args, second, error));
            Assert.Contains("Dernière lecture directe il y a 0 min", error.ToString(), StringComparison.Ordinal);
            Assert.Single(error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains("12.5", second.ToString(), StringComparison.Ordinal);
            Assert.Equal(2, FakeCodex.Launches(scenario));
            Assert.Equal(AllowedSequence, File.ReadAllLines(scenario + ".methods"));
        }
        finally { Environment.SetEnvironmentVariable("QUOTAWIDGET_FAKE_SCENARIO", saved); }
    }

    [Fact]
    public void WithoutLiveNeverStartsExecutableEvenWithExplicitPath()
    {
        using var directory = new TestDirectory();
        var scenario = FakeCodex.Scenario(directory);
        var saved = Environment.GetEnvironmentVariable("QUOTAWIDGET_FAKE_SCENARIO");
        Environment.SetEnvironmentVariable("QUOTAWIDGET_FAKE_SCENARIO", scenario);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, QuotasCommand.Run(["--json", "--codex-executable", FakeCodex.Executable,
                "--claude-dir", directory.PathFor("claude"), "--codex-sessions", directory.PathFor("sessions"),
                "--data-dir", directory.PathFor("data")], output, error, TestData.Clock));
            Assert.Equal(0, FakeCodex.Launches(scenario));
        }
        finally { Environment.SetEnvironmentVariable("QUOTAWIDGET_FAKE_SCENARIO", saved); }
    }

    [Fact]
    public void DisconnectedProfileKeepsPassiveObservationAndShowsLoginCommand()
    {
        using var directory = new TestDirectory();
        var scenario = FakeCodex.Scenario(directory, login: "{\"exitCode\":1,\"stderr\":\"Not logged in\"}");
        directory.Write("sessions/2026/10/30/rollout-test.jsonl", TestData.Metadata() + "\n" + TestData.Journal() + "\n");
        var saved = Environment.GetEnvironmentVariable("QUOTAWIDGET_FAKE_SCENARIO");
        Environment.SetEnvironmentVariable("QUOTAWIDGET_FAKE_SCENARIO", scenario);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, QuotasCommand.Run(["--json", "--live", "--codex-executable", FakeCodex.Executable,
                "--claude-dir", directory.PathFor("claude"), "--codex-sessions", directory.PathFor("sessions"),
                "--data-dir", directory.PathFor("data")], output, error, TestData.Clock));
            Assert.Contains("4.5", output.ToString(), StringComparison.Ordinal);
            Assert.Contains(ActiveDiagnostics.LoginCommand, error.ToString(), StringComparison.Ordinal);
            Assert.Equal(1, FakeCodex.Launches(scenario));
        }
        finally { Environment.SetEnvironmentVariable("QUOTAWIDGET_FAKE_SCENARIO", saved); }
    }
}
