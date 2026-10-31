# Install and connect the widget with an AI assistant

Use the person's own Windows session and provider accounts. No shared account, provider token or paid project license is included.

## Install the Windows release

Prefer the [latest GitHub release](https://github.com/CustomDigitalServices-Kevin/claude-codex-quota-widget/releases/latest) and its `QuotaWidget-Setup-1.0.1-win-x64.exe`. Windows 11 x64 is the supported package target. The package includes .NET: no SDK, compilation, Git, separate .NET runtime or PowerShell 7 is required.

Run as the ordinary Windows user. The wizard installs into `%LOCALAPPDATA%\Programs\QuotaWidget`, creates a Start menu shortcut and offers startup at sign-in. Claude integration is unchecked by default. Explain its effect before selecting it. Provider applications and personal sign-in remain separate.

The community installer is unsigned. Windows may show publisher or reputation warnings. Check the release's SHA256 checksums according to the user's software policy. Never disable endpoint protection, certificate verification or shell security settings. Do not promise a fixed click count through Windows reputation prompts.

Settings and the isolated Codex profile live separately in `%LOCALAPPDATA%\QuotaWidget`. Updates and uninstall preserve them. The bundled .NET runtime is updated through subsequent widget releases.

## Claude account

The person needs [Claude Code installed and authenticated](https://code.claude.com/docs/en/setup). Never request passwords, tokens, `auth.json`, conversations or private settings contents.

If integration was not selected in the installer, preview the native helper:

```powershell
$quotas = Join-Path $env:LOCALAPPDATA 'Programs/QuotaWidget/app/quotas.exe'
& $quotas --configure-claude
```

Explain that this adds a command-based status line to that person's Claude settings. Apply only after consent:

```powershell
& $quotas --configure-claude --apply
```

An existing status line is preserved by default. Only distinct replacement consent justifies adding `--replace-existing`. The helper makes a byte-exact backup, preserves unrelated properties and prints the backup path. Keep backups private. Invalid JSON and unsafe paths cause refusal.

The native producer receives [official status-line JSON](https://code.claude.com/docs/en/statusline), persists selected local quotas and metadata, and makes no HTTP request. Windows PowerShell 5.1, included in Windows 11, serves only as the shell-compatible launcher. It needs no PowerShell 7, Bash or jq installation.

Start or resume Claude Code and allow a normal API response. Unsupported quota windows remain absent. Initial measurement age may be unknown until a later API-duration change. Repainting never proves a fresh quota reading.

## Codex account

Passive readings discover the current user's standard `.codex` session directory, and `.codex-cli` when present. If no valid recent log exists, use Codex normally and wait for an event. Never manufacture quota values.

Optional direct reads use `%LOCALAPPDATA%\QuotaWidget\codex-home`, separate from shared authentication. If desired, the person signs in through the [official Codex flow](https://developers.openai.com/codex/auth):

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

Never copy credentials, set `CODEX_HOME` globally, or sign in as another person. Use the widget's direct-read menu after login. Reads use the official local App Server and are rate-limited. This measures exposed Codex windows, not all ChatGPT website conversations. Desktop custom profile paths are not configurable; the CLI supports explicit `--codex-sessions` paths for offline readings.

## Verify

- The widget opens and the gear shows preferences.
- CPU, RAM, GPU and VRAM update when Windows exposes their counters.
- Claude appears after its producer receives data; Codex after a valid log or direct reading.
- The plus button expands human-readable recorded metadata. Missing fields remain absent. A recently observed session is not proof of a working agent.
- Quota ages follow measurements, not rendering time.

Do not upload snapshots, transcripts, full logs, backups or authentication files to public issues. Reproduce problems with synthetic fixtures.

## Settings and removal

Use the gear for card visibility, order and design. [settings.example.json](../examples/settings.example.json) contains appearance only. Do not overwrite a running app's settings file.

Uninstall from Windows Settings > Apps > Installed apps > Quota Widget, or its Start menu shortcut. Preferences, observations and isolated Codex authentication remain. Only its own Claude status line is restored or removed if the current command still belongs to the installation. Other settings and user-edited commands remain untouched.

To undo only native Claude integration:

```powershell
& $quotas --unconfigure-claude
```

## Source installation alternative

Developers need Windows 11, Git, PowerShell 7.5+, the Windows .NET 10 SDK and Desktop Runtime. Use [official .NET downloads](https://dotnet.microsoft.com/download/dotnet/10.0) and [PowerShell installation instructions](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows).

```powershell
git clone https://github.com/CustomDigitalServices-Kevin/claude-codex-quota-widget.git
cd claude-codex-quota-widget
pwsh -NoProfile -File ./install.ps1
pwsh -NoProfile -File ./configure-claude.ps1
```

The legacy source helper previews by default. `-Apply` needs consent; `-ReplaceExisting` needs separate replacement consent. It references the retained clone and uses the PowerShell producer. Keep its backup path. Undo it with `configure-claude.ps1 -Restore -BackupPath <saved-path>` before `uninstall.ps1`. If no prior settings existed, remove only its added statusLine property, preserving later changes. These scripts are not required by the Windows executable installer.

## Discovery

The README, description and topics identify a free Claude Code usage monitor and Codex quota tracker for Windows. `AGENTS.md` contains actual commands; `llms.txt` indexes documentation. They help discoverability without guaranteeing search ranking or inclusion in an AI answer.
