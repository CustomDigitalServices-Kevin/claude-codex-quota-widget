using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuotaWidget.Core;

public sealed record CodexActiveOptions(string DataDirectory, string? ExecutablePath = null)
{
    // Test-only overrides are inaccessible to production callers and never exposed by the CLI.
    internal TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(15);
    internal TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(20);
    internal bool DisableIntervalForTests { get; init; }
    internal string? UserProfileForTests { get; init; }
    internal string? ScenarioForTests { get; init; }
}

public sealed record CodexActiveState(DateTimeOffset? LastAttempt = null, DateTimeOffset? LastSuccess = null,
    int Failures = 0, bool AuthenticationSuspended = false, Observation[]? LastObservations = null)
{
    [JsonIgnore]
    public DateTimeOffset? NextAttempt => LastAttempt?.AddMinutes(Failures switch
    { <= 1 => 10, 2 => 20, 3 => 40, _ => 60 });
}

public sealed record ActiveReadResult(Observation[] Observations, Diagnostic? Diagnostic, CodexActiveState State);

public sealed class CodexActiveSource(CodexActiveOptions options, TimeProvider clock,
    Action<string>? sent = null, Action<string>? received = null)
{
    public async Task<ActiveReadResult> ReadAsync(bool resumeAuthentication = false, CancellationToken cancellationToken = default)
    {
        var state = new CodexActiveState();
        try
        {
            var home = Path.Combine(Path.GetFullPath(options.DataDirectory), "codex-home");
            if (!IsDedicatedProfile(home, options.UserProfileForTests
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
            { return Failure("profil_partage_refuse", state); }

            Directory.CreateDirectory(options.DataDirectory);
            using var lease = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
            if (lease is null) { return Failure("lecture_directe_occupee", state); }
            var statePath = Path.Combine(options.DataDirectory, "codex-active-state.json");
            // Fail closed if a damaged state would otherwise bypass the cross-process floor.
            if (File.Exists(statePath))
            {
                var loaded = AtomicJsonFile.Read<CodexActiveState>(statePath) ?? throw new JsonException("Missing active state.");
                if (loaded.Failures < 0 || loaded.LastObservations?.Any(o => o is null || o.Provider != "codex"
                    || o.Source != "app-server" || o.Windows is null || o.Windows.Any(w => w is null)
                    || !SnapshotStore.ValidAccount(o.Account)) == true)
                { throw new JsonException("Invalid active state."); }
                state = loaded;
            }
            if (resumeAuthentication && state.AuthenticationSuspended)
            {
                state = state with { AuthenticationSuspended = false };
                AtomicJsonFile.Write(statePath, state);
            }
            if (state.AuthenticationSuspended) { return Failure("authentification_refusee", state); }
            var now = clock.GetUtcNow();
            if (!options.DisableIntervalForTests && state.NextAttempt is { } next && now < next)
            { return Failure("lecture_directe_trop_recente", state); }
            state = state with { LastAttempt = now };
            AtomicJsonFile.Write(statePath, state);
            var executable = CodexExecutable.Resolve(options.ExecutablePath);
            string? error = null;
            Observation[] observations = [];
            try
            {
                if (executable is null) { error = "codex_introuvable"; }
                else
                {
                    Directory.CreateDirectory(home);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(options.TotalTimeout);
                    var token = timeout.Token;
                    var login = await LoginStatusAsync(executable, home, token).ConfigureAwait(false);
                    error = login;
                    if (login is null)
                    {
                        observations = await ReadLimitsAsync(executable, home, token).ConfigureAwait(false);
                        if (observations.All(o => o.Account is null))
                        {
                            var account = new CodexPassiveSource(clock).Read([Path.Combine(home, "sessions")])
                                .Observations.OrderByDescending(o => o.EvidenceAt).ThenByDescending(o => o.ObservedAt)
                                .FirstOrDefault()?.Account;
                            observations = observations.Select(o => o with { Account = account }).ToArray();
                        }
                    }
                }
            }
            catch (RpcFailure exception) { error = exception.Authentication ? "authentification_refusee" : "erreur_lecture_directe"; }
            catch (OperationCanceledException) { error = cancellationToken.IsCancellationRequested ? "lecture_directe_annulee" : "lecture_directe_delai_depasse"; }
            catch (Exception exception) when (exception is IOException or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
            { error = "erreur_lecture_directe"; }

            state = error is null
                ? state with { LastSuccess = clock.GetUtcNow(), Failures = 0, AuthenticationSuspended = false, LastObservations = observations }
                : state with { Failures = Math.Min(state.Failures, int.MaxValue - 1) + 1, AuthenticationSuspended = error == "authentification_refusee" };
            AtomicJsonFile.Write(statePath, state);
            return error is null ? new(observations, null, state) : Failure(error, state);
        }
        catch (OperationCanceledException) { return Failure("lecture_directe_annulee", state); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return Failure("etat_lecture_directe_illisible", state); }
    }

    private static ActiveReadResult Failure(string code, CodexActiveState state) => new(state.LastObservations ?? [], new(code, "app-server",
        NextAttemptAt: state.NextAttempt, LastAttemptAt: state.LastAttempt), state);

    private async Task<FileStream?> AcquireLockAsync(CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(options.DataDirectory, "codex-active.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (elapsed.Elapsed < options.LockTimeout)
            { await Task.Delay(TimeSpan.FromMilliseconds(50), token).ConfigureAwait(false); }
            catch (IOException) { return null; }
        } while (elapsed.Elapsed < options.LockTimeout);
        return null;
    }

    private static readonly string[] SharedProfileNames = [".codex", ".codex-cli"];

    internal static bool IsDedicatedProfile(string home, string userProfile)
    {
        var actual = ResolveDirectory(home);
        return SharedProfileNames.All(name =>
        {
            var forbidden = ResolveDirectory(Path.Combine(userProfile, name));
            return !actual.Equals(forbidden, StringComparison.OrdinalIgnoreCase)
                && !actual.StartsWith(forbidden + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        });
    }

    // Resolve every existing ancestor, including directory junctions, before comparing boundaries.
    private static string ResolveDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            { current = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Unresolvable profile link."); }
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private Process Start(string executable, string home, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.Environment["CODEX_HOME"] = home;
        if (options.ScenarioForTests is { } scenario) { info.Environment["QUOTAWIDGET_FAKE_SCENARIO"] = scenario; }
        foreach (var argument in arguments) { info.ArgumentList.Add(argument); }
        return Process.Start(info) ?? throw new IOException("Process did not start.");
    }

    private async Task<string?> LoginStatusAsync(string executable, string home, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = Start(executable, home, "login", "status");
        var stdout = DrainAsync(process.StandardOutput, true, token);
        var stderr = DrainAsync(process.StandardError, true, token);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var text = (await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false));
            if (process.ExitCode == 1 && text.Contains("Not logged in", StringComparison.OrdinalIgnoreCase))
            { return "profil_dedie_non_connecte"; }
            return process.ExitCode == 0 && text.Contains("Logged in", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) ? null : "etat_connexion_inconnu";
        }
        finally
        {
            await StopAsync(process).ConfigureAwait(false);
            await ObserveReadersAsync(stdout, stderr).ConfigureAwait(false);
        }
    }

    private async Task<Observation[]> ReadLimitsAsync(string executable, string home, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = Start(executable, home, "app-server");
        var stderr = DrainAsync(process.StandardError, false, token);
        try
        {
            await CodexProtocol.SendAsync(process.StandardInput, "initialize", sent, token).ConfigureAwait(false);
            _ = await ResponseAsync(process.StandardOutput, 1, token).ConfigureAwait(false);
            await CodexProtocol.SendAsync(process.StandardInput, "initialized", sent, token).ConfigureAwait(false);
            await CodexProtocol.SendAsync(process.StandardInput, "account/rateLimits/read", sent, token).ConfigureAwait(false);
            var result = await ResponseAsync(process.StandardOutput, 2, token).ConfigureAwait(false);
            return CodexParser.ParseAppServer(result.GetRawText(), "app-server", clock.GetUtcNow());
        }
        finally
        {
            await StopAsync(process).ConfigureAwait(false);
            await ObserveReadersAsync(stderr).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> ResponseAsync(StreamReader reader, int id, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.Property("method").Text() is { } method)
            {
                // Only the method name is retained; strip controls to prevent log injection.
                received?.Invoke(new string(method.Where(c => !char.IsControl(c)).Take(128).ToArray()));
                continue;
            }
            if (root.Property("id").Integer() != id) { continue; }
            if (root.Property("error").ValueKind == JsonValueKind.Object)
            {
                var message = root.Property("error").Property("message").Text() ?? "";
                throw new RpcFailure(message.Contains("401", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase));
            }
            var result = root.Property("result");
            if (result.ValueKind != JsonValueKind.Undefined) { return result.Clone(); }
            throw new JsonException("Missing JSON-RPC result.");
        }
        throw new IOException("No JSON-RPC response.");
    }

    private static async Task<string> DrainAsync(StreamReader reader, bool retain, CancellationToken token)
    {
        var buffer = new char[4096];
        var text = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (retain && text.Length < 16384) { text.Append(buffer, 0, Math.Min(count, 16384 - text.Length)); }
        }
        return text.ToString();
    }

    private static async Task ObserveReadersAsync(params Task<string>[] readers)
    {
        try { await Task.WhenAll(readers).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private static async Task StopAsync(Process process)
    {
        try { process.StandardInput.Close(); }
        catch (IOException) { }
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await process.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    private sealed class RpcFailure(bool authentication) : Exception
    {
        public bool Authentication { get; } = authentication;
    }
}
