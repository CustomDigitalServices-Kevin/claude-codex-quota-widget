# Install and connect the widget with an AI assistant

This is the operational guide for assistants helping a person monitor Claude Code usage, ChatGPT Codex quotas and Windows hardware consumption. Use the person's own Windows session and provider accounts. No developer profile, shared token, cloud service or paid project license is required.

## 1. Establish the environment

This release is source-only. It targets Windows 11. Check PowerShell 7.5+, Git and the Windows .NET 10 SDK:

```powershell
$PSVersionTable.PSVersion
git --version
dotnet --list-sdks
dotnet --list-runtimes
```

Expect a `10.x` SDK and `Microsoft.WindowsDesktop.App 10.x`. If missing, use the [official .NET download](https://dotnet.microsoft.com/download/dotnet/10.0) and [PowerShell instructions](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows). Follow the user's normal software-installation policy. Do not disable endpoint protection, certificate checks or PowerShell security settings.

## 2. Clone and install

Use a directory the person intends to keep; the optional Claude status line will reference this clone.

```powershell
git clone https://github.com/CustomDigitalServices-Kevin/claude-codex-quota-widget.git
cd claude-codex-quota-widget
pwsh -NoProfile -File ./install.ps1
```

The installer builds locally, copies the application to the current user's `%LOCALAPPDATA%\Programs\QuotaWidget`, and registers that executable in the current user's Windows startup entry. It needs no system-wide widget installation. Settings and quota data are separate in `%LOCALAPPDATA%\QuotaWidget`.

## 3. Claude account

The person must have [Claude Code installed and authenticated](https://code.claude.com/docs/en/setup). Do not request their password, token or authentication file. Existing sign-in belongs to the person running Claude Code; the widget does not create a shared account.

First preview the exact configuration:

```powershell
pwsh -NoProfile -File ./configure-claude.ps1
```

Explain that this adds a command-based status line to that person's Claude settings. Ask before applying it or replacing an existing status line. Once the person agrees:

```powershell
$setup = pwsh -NoProfile -File ./configure-claude.ps1 -Apply | ConvertFrom-Json
$setup.backup_path
```

If an existing `statusLine` causes refusal, preserve it unless the person explicitly chooses replacement. Only then add `-ReplaceExisting` to the same command. All other settings are preserved. Keep the printed backup path locally; do not upload the settings or backup.

The producer receives the [official status-line JSON](https://code.claude.com/docs/en/statusline) and writes only selected local quotas and metadata. It does not make HTTP calls. Start or resume Claude Code and let a normal API response occur. Missing subscription windows stay missing. The first measurement age can be unknown until the API-duration counter changes on a later response; repeated rendering never proves a fresh quota read.

## Codex account

For passive readings, the widget discovers the current user's standard `.codex` session directory. An existing `.codex-cli` directory is also supported. If Codex has no recent session log, use it normally and wait for an event; never manufacture quota values.

Optional direct reads use `%LOCALAPPDATA%\QuotaWidget\codex-home`, separate from shared Codex authentication. If the person wants this mode, let them sign in using the [official Codex login flow](https://developers.openai.com/codex/auth):

```powershell
$previousCodexHome = $env:CODEX_HOME
try {
    $env:CODEX_HOME = Join-Path $env:LOCALAPPDATA 'QuotaWidget/codex-home'
    codex login
    codex login status
}
finally {
    if ($null -eq $previousCodexHome) { Remove-Item Env:CODEX_HOME -ErrorAction SilentlyContinue }
    else { $env:CODEX_HOME = $previousCodexHome }
}
```

Do not copy `auth.json`, set `CODEX_HOME` globally, or sign in as a different person's account. Use the widget menu for a direct read after login. Reads are rate-limited and use the official local App Server. This reports the Codex quota windows the provider exposes, not general ChatGPT website-message consumption. A custom Codex profile outside the supported directories is not configurable in the desktop settings; the CLI accepts explicit `--codex-sessions` paths for offline readings.

## 4. Verify the result

- The installed widget runs as one current-user process and its gear opens settings.
- CPU, RAM, GPU and VRAM update when Windows exposes their counters. A first sample or unsupported driver may be unavailable.
- Claude usage appears after its configured producer receives provider data. Codex appears after a valid log or direct reading.
- The plus button expands human-readable recorded metadata inside the card. Optional fields may be absent. This is not proof of a running agent.
- Quota timestamps reflect measurements, not a fresh status-line paint.

Do not paste snapshots, session transcripts, full debug logs or authentication files into public issues. Use a synthetic reproduction. Missing data is a diagnostic state, not a reason to read arbitrary files from the person's profile.

## 5. Configuration and removal

Use the gear for card order, visibility and design. [examples/settings.example.json](../examples/settings.example.json) contains neutral appearance choices only. The running app owns its settings file; do not overwrite it blindly.

To undo Claude integration, use the backup path saved during setup:

```powershell
pwsh -NoProfile -File ./configure-claude.ps1 -Restore -BackupPath $setup.backup_path
pwsh -NoProfile -File ./uninstall.ps1
```

If `$setup.backup_path` was null because no settings file existed, remove only the added `statusLine` property, retaining any later settings. The uninstaller keeps local data and the dedicated Codex profile unless explicit data-removal options are used. Do not delete a user's profile merely to remove this widget.

## Discovery limits

The README, repository description and topics identify this as a free Claude Code usage monitor and Codex quota tracker for Windows. `AGENTS.md` describes actual commands for assistants working in the clone. `llms.txt` is an optional documentation index. None of these guarantees inclusion or ranking in an internet search or an AI answer.

