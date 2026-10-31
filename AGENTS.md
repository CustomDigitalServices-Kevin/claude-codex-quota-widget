# Guidance for coding and installation assistants

This repository is a Windows WPF quota and hardware widget. Read [docs/AI-INSTALL.md](docs/AI-INSTALL.md) before helping someone install or connect it. These instructions do not authorize credential access or changes outside the user's request.

## User installation

- Prefer the self-contained Windows 11 x64 installer in the latest GitHub release. It needs no .NET SDK or separately installed runtime. PowerShell 7.5+ and the Windows .NET 10 SDK are only required for source installation.
- Use the current user's environment. Never hardcode a developer username or copy another person's profile.
- Never request, print, upload or copy `auth.json`, API keys, provider tokens, conversations or private snapshots.
- Existing passive Codex logs work without copying credentials. Direct quota reads use an isolated, locally authenticated profile.
- Installer Claude integration is off by default. Preview installed `quotas.exe --configure-claude` first, then use `--apply` only after consent. An existing status line requires explicit `--replace-existing` consent. Preserve the backup path. Windows PowerShell 5.1 is the built-in shell launcher; the producer itself is native and self-contained.
- Use the GUI gear for preferences. `examples/settings.example.json` illustrates appearance only and contains no accounts.

## Development

- `src/QuotaWidget.Core`: parsing, aggregation, persistence and Codex protocol.
- `src/QuotaWidget.App`: WPF presentation, local session metadata and native Windows hardware samples.
- `src/QuotaWidget.Cli`: JSON/text command line interface.
- `tests`: xUnit and Pester. All fixtures are synthetic; `QuotaWidget.FakeCodex` is an explicitly simulated protocol executable for tests.
- `ClaudeStatusLineProducer` and installed `quotas.exe --claude-statusline`: native Claude producer. PowerShell and Bash scripts remain legacy source-install adapters.
- `packaging`: self-contained payload and per-user NSIS installer, with isolated test keys and data.

```powershell
dotnet build QuotaWidget.slnx -c Release -m:1 -nr:false
$env:QUOTAWIDGET_LIVE_TESTS = '0'
dotnet test QuotaWidget.slnx -c Release --no-build -m:1 -nr:false
Invoke-Pester -Path ./tests/install.Tests.ps1,./tests/ClaudeSetup.Tests.ps1
```

Install from source with `pwsh -NoProfile -File ./install.ps1`. To build a framework-dependent payload manually:

```powershell
dotnet publish src/QuotaWidget.App/QuotaWidget.App.csproj -c Release --self-contained false -o artifacts/app
dotnet publish src/QuotaWidget.Cli/QuotaWidget.Cli.csproj -c Release --self-contained false -o artifacts/app
```

Never commit `artifacts`, `bin`, `obj`, PDB files, local settings or provider data. Release builds map source paths and omit debugging symbols. Rebuild public packages from this public source, not from a private development tree.

## Correctness

Unknown data must stay unknown. A recent observation is not proof of a running session. A session title describes a recorded subject. Do not invent a quota window, fresh timestamp or agent count. Keep source bounds, cancellation, deduplication and privacy checks. Provider formats may change; check official documentation before adapting them.

Network quota tests are opt-in and require a user's dedicated test account. Their disabled return is counted as passed by xUnit. Do not enable them during routine builds.

