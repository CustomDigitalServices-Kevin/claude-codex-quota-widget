using System.Diagnostics;
using System.Text.Json;
using QuotaWidget.Core;
using Xunit.Abstractions;

namespace QuotaWidget.Core.Tests;

internal static class FakeCodex
{
    internal static string Executable => Path.Combine(AppContext.BaseDirectory, "fake-codex", "QuotaWidget.FakeCodex.exe");
    // Synthetic protocol responses based on the official Codex App Server schema.
    internal const string Result = """
        {"accountId":"account-a","rateLimits":{"limitId":"codex","planType":"pro",
        "primary":{"usedPercent":81,"windowDurationMins":300,"resetsAt":1793365200},
        "credits":{"balance":"12.5","unlimited":false,"hasCredits":true}},
        "ordinaryUsageAllowed":true,"rateLimitResetCredits":{"availableCount":2,"credits":[]}}
        """;

    internal static string Scenario(TestDirectory directory, string? result = null, string? error = null,
        string? finalExtras = null, string? login = null, string name = "scenario")
    {
        var response = error is null ? "{\"id\":2,\"result\":" + JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(result ?? Result)) + "}"
            : "{\"id\":2,\"error\":{\"code\":-1,\"message\":" + JsonSerializer.Serialize(error) + "}}";
        return directory.Write(name + ".jsonl", (login ?? "{\"exitCode\":0,\"stderr\":\"Logged in\"}") + "\n"
            + "{\"expect\":\"initialize\",\"messages\":[{\"id\":900,\"result\":{}},{\"method\":\"server/notice\",\"params\":{\"secret\":\"hidden\"}},{\"id\":1,\"result\":{}}]}\n"
            + "{\"expect\":\"initialized\"}\n"
            + "{\"expect\":\"account/rateLimits/read\",\"messages\":[{\"id\":2,\"method\":\"server/request\",\"params\":{\"secret\":\"hidden\"}},"
            + response + "]" + (finalExtras is null ? "" : "," + finalExtras) + "}\n");
    }

    internal static CodexActiveOptions Options(TestDirectory directory, string scenario) =>
        new(directory.PathFor("data"), Executable)
        { ScenarioForTests = scenario, UserProfileForTests = directory.PathFor("user") };

    internal static int Launches(string scenario) => File.Exists(scenario + ".launches")
        ? File.ReadAllLines(scenario + ".launches").Length : 0;
}

public sealed partial class CodexActiveTests(ITestOutputHelper output)
{
    private static readonly string[] AllowedSequence = ["initialize", "initialized", "account/rateLimits/read"];
    private static readonly string[] ServerMethods = ["server/notice", "server/request"];
    [Fact]
    public void ResolutionUsesExplicitNativeThenPathAndRejectsShims()
    {
        using var directory = new TestDirectory();
        var native = CodexExecutable.NativePath(directory.Root);
        var pathExe = directory.Write("path/codex.exe", "test marker, never executed");
        var explicitExe = directory.Write("explicit.exe", "test marker, never executed");
        Assert.Equal(pathExe, CodexExecutable.Resolve(applicationData: directory.Root, searchPath: Path.GetDirectoryName(pathExe)));
        Directory.CreateDirectory(Path.GetDirectoryName(native)!);
        File.WriteAllText(native, "test marker, never executed");
        Assert.Equal(native, CodexExecutable.Resolve(applicationData: directory.Root, searchPath: Path.GetDirectoryName(pathExe)));
        Assert.Equal(explicitExe, CodexExecutable.Resolve(explicitExe, directory.Root, Path.GetDirectoryName(pathExe)));
        foreach (var suffix in new[] { ".cmd", ".bat", ".ps1" })
        { Assert.Null(CodexExecutable.Resolve(directory.Write("shim" + suffix, "marker"), directory.Root)); }
        Assert.Null(CodexExecutable.Resolve(directory.PathFor("missing.exe")));
        Assert.Null(CodexExecutable.Resolve(applicationData: directory.PathFor("empty"), searchPath: directory.PathFor("empty")));
    }

    [Theory]
    [InlineData("account/logout")]
    [InlineData("account/rateLimitResetCredit/consume")]
    public async Task ForbiddenMethodsThrowBeforeWriting(string method)
    {
        using var writer = new StringWriter();
        await Assert.ThrowsAsync<InvalidOperationException>(() => CodexProtocol.SendAsync(writer, method));
        Assert.Empty(writer.ToString());
    }

    [Fact]
    public async Task NormalResponseUsesOnlyWhitelistDedicatedEnvironmentAndFingerprints()
    {
        using var directory = new TestDirectory();
        var scenario = FakeCodex.Scenario(directory);
        var options = FakeCodex.Options(directory, scenario);
        List<string> sent = [];
        List<string> received = [];
        var result = await new CodexActiveSource(options, TestData.Clock, sent.Add, received.Add).ReadAsync();
        Assert.Null(result.Diagnostic);
        var observation = Assert.Single(result.Observations);
        Assert.Equal(JsonSupport.Fingerprint("account-a"), observation.Account);
        Assert.Equal("app-server", observation.Source);
        Assert.True(observation.OrdinaryUsageAllowed);
        Assert.Equal(2, observation.ResetCredits?.AvailableCount);
        Assert.Equal(12.5, observation.Credits?.Balance);
        Assert.Equal(AllowedSequence, sent);
        Assert.Equal(sent, File.ReadAllLines(scenario + ".methods"));
        Assert.Equal(ServerMethods, received);
        foreach (var line in File.ReadAllLines(scenario + ".launches"))
        {
            using var launch = JsonDocument.Parse(line);
            Assert.Equal(Path.Combine(options.DataDirectory, "codex-home"), launch.RootElement.GetProperty("home").GetString());
        }
        Assert.Equal(TestData.Now, result.State.LastSuccess);
        Assert.Equal(0, result.State.Failures);
    }

    [Theory]
    [InlineData(1, "Not logged in", "profil_dedie_non_connecte")]
    [InlineData(0, "Not logged in", "etat_connexion_inconnu")]
    [InlineData(1, "Logged in", "etat_connexion_inconnu")]
    [InlineData(0, "", "etat_connexion_inconnu")]
    public async Task LoginRequiresBothExitCodeAndText(int exitCode, string text, string diagnostic)
    {
        using var directory = new TestDirectory();
        var scenario = FakeCodex.Scenario(directory, login: JsonSerializer.Serialize(new { exitCode, stdout = text }));
        var result = await new CodexActiveSource(FakeCodex.Options(directory, scenario), TestData.Clock).ReadAsync();
        Assert.Equal(diagnostic, result.Diagnostic?.Code);
        Assert.Equal(1, FakeCodex.Launches(scenario));
        Assert.False(File.Exists(scenario + ".methods"));
    }

    [Theory]
    [InlineData(".codex")]
    [InlineData(".codex-cli")]
    public async Task SharedProfilesAndDescendantsAreRefusedBeforeLaunching(string name)
    {
        using var directory = new TestDirectory();
        var scenario = FakeCodex.Scenario(directory);
        var user = directory.PathFor("user");
        foreach (var suffix in new[] { "", "nested" })
        {
            var shared = Path.Combine(user, name, suffix);
            Assert.False(CodexActiveSource.IsDedicatedProfile(shared, user));
            var options = FakeCodex.Options(directory, scenario) with { DataDirectory = shared };
            Assert.Equal("profil_partage_refuse", (await new CodexActiveSource(options, TestData.Clock).ReadAsync()).Diagnostic?.Code);
        }
        Assert.True(CodexActiveSource.IsDedicatedProfile(Path.Combine(user, name + "-different"), user));
        Assert.Equal(0, FakeCodex.Launches(scenario));
    }

    [Fact]
    public async Task JunctionOrSymbolicLinkToSharedProfileIsRefused()
    {
        using var directory = new TestDirectory();
        var user = directory.PathFor("user");
        var shared = Path.Combine(user, ".codex");
        Directory.CreateDirectory(shared);
        var data = directory.PathFor("data");
        Directory.CreateDirectory(data);
        var link = Path.Combine(data, "codex-home");
        try { Directory.CreateSymbolicLink(link, shared); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        { output.WriteLine("Link test unavailable: " + exception.GetType().Name); return; }
        try
        {
            var scenario = FakeCodex.Scenario(directory);
            var result = await new CodexActiveSource(FakeCodex.Options(directory, scenario), TestData.Clock).ReadAsync();
            Assert.Equal("profil_partage_refuse", result.Diagnostic?.Code);
            Assert.Equal(0, FakeCodex.Launches(scenario));
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData("HTTP 401")]
    [InlineData("UNAUTHORIZED")]
    public async Task AuthenticationSuspensionPersistsAndCanBeResumed(string error)
    {
        using var directory = new TestDirectory();
        var scenario = FakeCodex.Scenario(directory, error: error);
        var options = FakeCodex.Options(directory, scenario);
        var first = await new CodexActiveSource(options, TestData.Clock).ReadAsync();
        Assert.Equal("authentification_refusee", first.Diagnostic?.Code);
        Assert.True(first.State.AuthenticationSuspended);
        var later = new TestClock(TestData.Now.AddHours(1));
        Assert.Equal("authentification_refusee", (await new CodexActiveSource(options, later).ReadAsync()).Diagnostic?.Code);
        Assert.Equal(2, FakeCodex.Launches(scenario));
        FakeCodex.Scenario(directory);
        var resumed = await new CodexActiveSource(options, later).ReadAsync(resumeAuthentication: true);
        Assert.Null(resumed.Diagnostic);
        Assert.False(resumed.State.AuthenticationSuspended);
        Assert.Equal(4, FakeCodex.Launches(scenario));
    }

    [Fact]
    public async Task ErrorBackoffIsTenTwentyFortyThenSixtyMinutes()
    {
        using var directory = new TestDirectory();
        var scenario = FakeCodex.Scenario(directory, error: "Forbidden, no dedicated authentication code");
        var options = FakeCodex.Options(directory, scenario);
        var now = TestData.Now;
        foreach (var minutes in new[] { 10, 20, 40, 60, 60 })
        {
            var result = await new CodexActiveSource(options, new TestClock(now)).ReadAsync();
            Assert.Equal("erreur_lecture_directe", result.Diagnostic?.Code);
            Assert.Equal(now.AddMinutes(minutes), result.State.NextAttempt);
            Assert.False(result.State.AuthenticationSuspended);
            now = result.State.NextAttempt!.Value;
        }
    }

    [Fact]
    public async Task IndependentCallersCheckFloorAfterLockAndDoNotDoubleLaunch()
    {
        using var directory = new TestDirectory();
        var scenario = FakeCodex.Scenario(directory, finalExtras: "\"delayMs\":200");
        var options = FakeCodex.Options(directory, scenario);
        var first = new CodexActiveSource(options, TestData.Clock).ReadAsync();
        var second = new CodexActiveSource(options, TestData.Clock).ReadAsync();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, result => result.Diagnostic is null);
        var refused = Assert.Single(results, result => result.Diagnostic?.Code == "lecture_directe_trop_recente");
        Assert.Equal(TestData.Now.AddMinutes(10), refused.Diagnostic?.NextAttemptAt);
        Assert.Equal(2, FakeCodex.Launches(scenario));
    }

    [Fact]
    public async Task HeldLockTimesOutWithoutLaunching()
    {
        using var directory = new TestDirectory();
        var scenario = FakeCodex.Scenario(directory);
        var options = FakeCodex.Options(directory, scenario) with { LockTimeout = TimeSpan.FromMilliseconds(100) };
        Directory.CreateDirectory(options.DataDirectory);
        using var lease = new FileStream(Path.Combine(options.DataDirectory, "codex-active.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var result = await new CodexActiveSource(options, TestData.Clock).ReadAsync();
        Assert.Equal("lecture_directe_occupee", result.Diagnostic?.Code);
        Assert.Equal(0, FakeCodex.Launches(scenario));
    }

    [Theory]
    [InlineData("hang")]
    [InlineData("delay")]
    [InlineData("empty")]
    public async Task HangingDelayedOrEmptyServerIsStoppedAndLockReleased(string kind)
    {
        using var directory = new TestDirectory();
        var step = kind switch
        {
            "hang" => "{\"expect\":\"initialize\",\"hang\":true,\"spawnChild\":true}",
            "delay" => "{\"expect\":\"initialize\",\"delayMs\":10000}",
            _ => "{\"expect\":\"initialize\"}",
        };
        var scenario = directory.Write("scenario.jsonl", "{\"exitCode\":0,\"stdout\":\"Logged in\"}\n" + step + "\n");
        var options = FakeCodex.Options(directory, scenario) with { TotalTimeout = TimeSpan.FromSeconds(1) };
        var result = await new CodexActiveSource(options, TestData.Clock).ReadAsync();
        Assert.Equal(kind == "empty" ? "erreur_lecture_directe" : "lecture_directe_delai_depasse", result.Diagnostic?.Code);
        using var lease = new FileStream(Path.Combine(options.DataDirectory, "codex-active.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        foreach (var line in File.ReadAllLines(scenario + ".pids"))
        {
            var pid = int.Parse(line, System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                using var process = Process.GetProcessById(pid);
                Assert.True(process.HasExited || process.WaitForExit(2000), "Fake process or descendant still alive.");
            }
            catch (ArgumentException) { /* The operating system already removed the exited process. */ }
        }
    }

}
