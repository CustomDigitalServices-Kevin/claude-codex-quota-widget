#Requires -Version 7.5
<#
.SYNOPSIS
Previews, applies, or restores the Claude Code statusLine configuration.
.DESCRIPTION
The default action is a no-write JSON preview. -Apply merges only statusLine and backs up an
existing settings file. Existing statusLine configuration requires explicit -ReplaceExisting.
-Restore accepts only a generated backup associated with the selected settings path.
.PARAMETER Apply
Writes the statusLine fragment to SettingsPath after validation and backup.
.PARAMETER ReplaceExisting
Allows -Apply to replace an existing statusLine value.
.PARAMETER Restore
Restores BackupPath byte for byte and backs up the file being replaced.
.PARAMETER BackupPath
Generated settings backup to restore. Valid only with -Restore.
.PARAMETER SettingsPath
Claude Code settings file. Defaults to the current user's profile and is never read in preview mode.
.PARAMETER StatusLineScriptPath
Path embedded in the generated command. The file must already exist.
.EXAMPLE
.\configure-claude.ps1
.EXAMPLE
.\configure-claude.ps1 -Apply
.EXAMPLE
.\configure-claude.ps1 -Restore -BackupPath C:\profile\settings.json.backup-...
#>
[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$ReplaceExisting,
    [switch]$Restore,
    [string]$BackupPath,
    [string]$SettingsPath = (Join-Path $env:USERPROFILE '.claude/settings.json'),
    [string]$StatusLineScriptPath = (Join-Path $PSScriptRoot 'statusline/claude-statusline.ps1')
)

$ErrorActionPreference = 'Stop'
$maximumBytes = 1MB
$utf8 = [Text.UTF8Encoding]::new($false)

function Test-RegularFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $item = Get-Item -LiteralPath $Path -Force
    return (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0)
}

function ConvertFrom-SettingsJson([string]$Text) {
    return $Text | ConvertFrom-Json -AsHashtable -Depth 30 -DateKind String
}

function Assert-SafeTarget([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'SettingsPath is required.' }
    if (Test-Path -LiteralPath $Path) {
        if (-not (Test-RegularFile $Path)) { throw 'SettingsPath must be a regular file.' }
    }
    $parent = Split-Path -Parent $Path
    if (-not $parent) { throw 'SettingsPath must include a parent directory.' }
    if (Test-Path -LiteralPath $parent) {
        $item = Get-Item -LiteralPath $parent -Force
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'SettingsPath parent must be a regular directory.'
        }
    }
}

function New-Backup([string]$Path) {
    if (-not (Test-RegularFile $Path)) { return $null }
    $leaf = Split-Path $Path -Leaf
    $name = '{0}.backup-{1}-{2}' -f $leaf, [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), [guid]::NewGuid().ToString('N')
    $destination = Join-Path (Split-Path $Path -Parent) $name
    [IO.File]::Copy($Path, $destination, $false)
    return $destination
}

function Write-AtomicBytes([string]$Path, [byte[]]$Bytes) {
    if ($Bytes.Length -gt $maximumBytes) { throw 'The resulting settings file exceeds 1 MiB.' }
    Assert-SafeTarget $Path
    $parent = Split-Path $Path -Parent
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $temporary = Join-Path $parent ('.settings-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllBytes($temporary, $Bytes)
        [IO.File]::Move($temporary, $Path, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

if ($Apply -and $Restore) { throw 'Choose either -Apply or -Restore.' }
if ($ReplaceExisting -and -not $Apply) { throw '-ReplaceExisting requires -Apply.' }
if ($BackupPath -and -not $Restore) { throw '-BackupPath is valid only with -Restore.' }

if ($Restore) {
    if ([string]::IsNullOrWhiteSpace($BackupPath)) { throw '-Restore requires -BackupPath.' }
    Assert-SafeTarget $SettingsPath
    if (-not (Test-RegularFile $BackupPath)) { throw 'BackupPath must be a regular file.' }
    $backup = Get-Item -LiteralPath $BackupPath -Force
    if ($backup.Length -gt $maximumBytes) { throw 'The backup exceeds 1 MiB.' }
    $targetDirectory = [IO.Path]::GetFullPath((Split-Path $SettingsPath -Parent)).TrimEnd('\', '/')
    $backupDirectory = [IO.Path]::GetFullPath($backup.DirectoryName).TrimEnd('\', '/')
    $expectedPrefix = (Split-Path $SettingsPath -Leaf) + '.backup-'
    if ($backupDirectory -cne $targetDirectory -or -not $backup.Name.StartsWith($expectedPrefix, [StringComparison]::Ordinal)) {
        throw 'BackupPath does not belong to SettingsPath.'
    }
    $currentBackup = New-Backup $SettingsPath
    Write-AtomicBytes $SettingsPath ([IO.File]::ReadAllBytes($backup.FullName))
    [ordered]@{ restored = $true; settings_path = [IO.Path]::GetFullPath($SettingsPath); replaced_backup = $currentBackup } |
        ConvertTo-Json -Compress
    return
}

if (-not (Test-RegularFile $StatusLineScriptPath)) { throw 'Status line script was not found.' }
$resolvedScript = [IO.Path]::GetFullPath((Get-Item -LiteralPath $StatusLineScriptPath).FullName).Replace('\', '/')
if ($resolvedScript.IndexOfAny([char[]]@(0, 10, 13, 34, 36, 96)) -ge 0) {
    throw 'Status line script path contains unsupported characters.'
}
$fragment = [ordered]@{
    statusLine = [ordered]@{
        type = 'command'
        command = 'pwsh -NoProfile -File "' + $resolvedScript + '"'
        padding = 0
    }
}

if (-not $Apply) {
    $fragment | ConvertTo-Json -Depth 4 -Compress
    return
}

Assert-SafeTarget $SettingsPath
$settings = [ordered]@{}
$existingBytes = $null
if (Test-Path -LiteralPath $SettingsPath) {
    $file = Get-Item -LiteralPath $SettingsPath -Force
    if ($file.Length -gt $maximumBytes) { throw 'The settings file exceeds 1 MiB.' }
    $existingBytes = [IO.File]::ReadAllBytes($SettingsPath)
    try { $settings = ConvertFrom-SettingsJson ($utf8.GetString($existingBytes)) }
    catch { throw 'The existing settings file is not valid JSON.' }
    if ($settings -isnot [Collections.IDictionary]) { throw 'The existing settings root must be a JSON object.' }
    if ($settings.Contains('statusLine') -and -not $ReplaceExisting) {
        throw 'A statusLine setting already exists. Use -ReplaceExisting to replace it explicitly.'
    }
}
$settings.statusLine = $fragment.statusLine
$newBytes = $utf8.GetBytes(($settings | ConvertTo-Json -Depth 30))
if ($newBytes.Length -gt $maximumBytes) { throw 'The resulting settings file exceeds 1 MiB.' }
$backup = if ($null -ne $existingBytes) { New-Backup $SettingsPath } else { $null }
try { Write-AtomicBytes $SettingsPath $newBytes }
catch {
    if ($backup -and (Test-RegularFile $backup)) { [IO.File]::Copy($backup, $SettingsPath, $true) }
    throw
}
[ordered]@{ applied = $true; settings_path = [IO.Path]::GetFullPath($SettingsPath); backup_path = $backup } |
    ConvertTo-Json -Compress
