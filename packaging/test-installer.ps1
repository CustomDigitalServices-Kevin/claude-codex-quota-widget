#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OffSetup,
    [Parameter(Mandatory)][string]$OnSetup,
    [Parameter(Mandatory)][string]$RollbackSetup,
    [Parameter(Mandatory)][string]$LegacyPayload,
    [ValidatePattern('^[A-Za-z0-9-]{1,32}$')][string]$TestSuffix = 'local',
    [string]$TestRoot = (Join-Path ([IO.Path]::GetTempPath()) "QuotaWidget-Packaging-$TestSuffix")
)

$ErrorActionPreference = 'Stop'

function Assert-NoReparseChain([string]$Path) {
    for ($item = [IO.DirectoryInfo]::new($Path); $item; $item = $item.Parent) {
        if ($item.Exists -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Reparse paths are not allowed in packaging tests: $($item.FullName)"
        }
    }
}

function Assert-SafeTestRoot([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $full.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($full) -notlike 'QuotaWidget-Packaging-*') {
        throw 'TestRoot must be a dedicated QuotaWidget-Packaging-* directory under system temp.'
    }
    Assert-NoReparseChain $full
    return $full
}

function Reset-TestRoot([string]$Path) {
    Assert-NoReparseChain $Path
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
    [IO.Directory]::CreateDirectory($Path) | Out-Null
}

function Invoke-NativeProcess([string]$FilePath, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    Set-IsolatedRuntimeEnvironment $start
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process) { throw "Unable to start process: $FilePath" }
    $process.WaitForExit(); $code = $process.ExitCode; $process.Dispose()
    return $code
}

function Set-IsolatedRuntimeEnvironment([Diagnostics.ProcessStartInfo]$Start) {
    $Start.Environment['PATH'] = "$env:SystemRoot\System32;$env:SystemRoot\System32\WindowsPowerShell\v1.0"
    $Start.Environment['DOTNET_ROOT'] = 'C:\QuotaWidget-Synthetic-Missing-Runtime'
    $Start.Environment['DOTNET_ROOT_X64'] = 'C:\QuotaWidget-Synthetic-Missing-Runtime'
    $Start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
}

function Invoke-Setup([string]$Path, [string]$InstallDirectory, [int]$ExpectedExitCode = 0) {
    $code = Invoke-NativeProcess $Path @('/S', "/D=$InstallDirectory")
    if ($code -ne $ExpectedExitCode) {
        throw "Unexpected installer exit code $code (expected $ExpectedExitCode): $Path"
    }
}

function Assert-Installed([string]$InstallDirectory, [string]$RegistryPath, [bool]$ExpectedStartup = $true) {
    foreach ($name in 'QuotaWidget.exe', 'quotas.exe', 'coreclr.dll', 'payload-manifest.json') {
        $path = Join-Path $InstallDirectory "app/$name"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Installed file missing: $path" }
    }
    if (-not (Test-Path -LiteralPath $RegistryPath)) { throw "Test registry key missing: $RegistryPath" }
    $run = (Get-ItemProperty -LiteralPath (Join-Path $RegistryPath 'Run') -ErrorAction SilentlyContinue).QuotaWidget
    $expected = '"' + (Join-Path $InstallDirectory 'app/QuotaWidget.exe') + '"'
    if (($ExpectedStartup -and $run -ne $expected) -or (-not $ExpectedStartup -and $run)) {
        throw "Unexpected startup registration: $run"
    }
}

function Copy-LegacyLayout([string]$Payload, [string]$InstallDirectory) {
    $source = [IO.Path]::GetFullPath($Payload)
    if (-not (Test-Path -LiteralPath (Join-Path $source 'QuotaWidget.exe') -PathType Leaf)) {
        throw 'The real v1.0.0 framework-dependent payload is missing.'
    }
    [IO.Directory]::CreateDirectory($InstallDirectory) | Out-Null
    Get-ChildItem -LiteralPath $source -File | Copy-Item -Destination $InstallDirectory
    [IO.File]::WriteAllText((Join-Path $InstallDirectory 'legacy-user-file.keep'), 'preserve')
}

function Assert-LegacyMigration([string]$InstallDirectory) {
    $known = @('quotas.deps.json', 'quotas.dll', 'quotas.exe', 'quotas.runtimeconfig.json',
        'QuotaWidget.Core.dll', 'QuotaWidget.deps.json', 'QuotaWidget.dll', 'QuotaWidget.exe',
        'QuotaWidget.runtimeconfig.json')
    foreach ($name in $known) {
        if (Test-Path -LiteralPath (Join-Path $InstallDirectory $name)) {
            throw "Known legacy file remained after migration: $name"
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $InstallDirectory 'legacy-user-file.keep'))) {
        throw 'An unknown legacy-root file was not preserved.'
    }
}

function Wait-Uninstall([string]$InstallDirectory, [string]$RegistryPath, [string]$MenuPath) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $pending = (Test-Path -LiteralPath (Join-Path $InstallDirectory 'Uninstall.exe')) -or
            (Test-Path -LiteralPath (Join-Path $InstallDirectory 'app/QuotaWidget.exe')) -or
            (Test-Path -LiteralPath $RegistryPath) -or (Test-Path -LiteralPath $MenuPath)
        if (-not $pending) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Normal silent uninstall did not finish within 20 seconds.'
}

function Invoke-PackagingSmoke([string]$InstallDirectory, [string]$SmokeDirectory) {
    [IO.Directory]::CreateDirectory($SmokeDirectory) | Out-Null
    $code = Invoke-NativeProcess (Join-Path $InstallDirectory 'app/QuotaWidget.exe') @('--packaging-smoke', $SmokeDirectory)
    if ($code -ne 0) { throw "Application packaging smoke failed with exit code $code." }
    $result = Get-Content -LiteralPath (Join-Path $SmokeDirectory 'smoke.json') -Raw | ConvertFrom-Json
    if ($result.schema -ne 1 -or $result.synthetic -ne $true -or $result.metadataDetails -ne 1 -or
        $result.screenshot -ne $true -or -not (Test-Path -LiteralPath (Join-Path $SmokeDirectory 'widget.png'))) {
        throw 'Application smoke result or screenshot is invalid.'
    }
    $appRoot = [IO.Path]::GetFullPath((Join-Path $InstallDirectory 'app')).TrimEnd('\') + '\'
    foreach ($field in 'executablePath', 'coreClrPath') {
        $value = [IO.Path]::GetFullPath($result.$field)
        if (-not $value.StartsWith($appRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$field was loaded outside the self-contained installation: $value"
        }
    }
}

function Invoke-NativeClaudeProducer([string]$InstallDirectory, [string]$StateDirectory) {
    [IO.Directory]::CreateDirectory($StateDirectory) | Out-Null
    $session = '37d857d8-1f89-48a7-b85c-b3bdde87bd81'
    $json = @{ session_id = $session; cost = @{ total_api_duration_ms = 1200 } } | ConvertTo-Json -Compress
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $InstallDirectory 'app/quotas.exe'))
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true
    Set-IsolatedRuntimeEnvironment $start
    $start.ArgumentList.Add('--claude-statusline'); $start.ArgumentList.Add('--state-dir'); $start.ArgumentList.Add($StateDirectory)
    $process = [Diagnostics.Process]::Start($start)
    $process.StandardInput.Write($json); $process.StandardInput.Close()
    $process.StandardOutput.ReadToEnd() | Out-Null; $process.WaitForExit()
    $code = $process.ExitCode; $process.Dispose()
    if ($code -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $StateDirectory "$session.json"))) {
        throw 'Native Claude producer smoke failed.'
    }
}

$root = Assert-SafeTestRoot $TestRoot
$setups = @($OffSetup, $OnSetup, $RollbackSetup) | ForEach-Object { [IO.Path]::GetFullPath($_) }
foreach ($setup in $setups) {
    if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw "Setup missing: $setup" }
}
Reset-TestRoot $root
$install = Join-Path $root 'install'; $data = "$install-data"
$settings = Join-Path $data 'synthetic-claude/settings.json'
$integration = Join-Path $data 'claude-integration.json'
$registry = "HKCU:\Software\QuotaWidget\InstallerTests\$TestSuffix"
Copy-LegacyLayout $LegacyPayload $install
New-Item -Path (Join-Path $registry 'Run') -Force | Out-Null
New-ItemProperty -LiteralPath (Join-Path $registry 'Run') -Name QuotaWidget -Value ('"' + (Join-Path $install 'QuotaWidget.exe') + '"') -Force | Out-Null
[IO.Directory]::CreateDirectory((Split-Path $settings)) | Out-Null
[IO.File]::WriteAllText($settings, '{malformed', [Text.UTF8Encoding]::new($false))
$malformedHash = (Get-FileHash $settings -Algorithm SHA256).Hash
$preferences = Join-Path $data 'settings.json'
$profile = Join-Path $data 'codex-home/profile.keep'
[IO.Directory]::CreateDirectory((Split-Path $profile)) | Out-Null
[IO.File]::WriteAllText($preferences, '{"Theme":"Black","Visible":false}')
[IO.File]::WriteAllText($profile, 'synthetic isolated profile sentinel')
$preferencesHash = (Get-FileHash $preferences -Algorithm SHA256).Hash
$profileHash = (Get-FileHash $profile -Algorithm SHA256).Hash

Invoke-Setup $setups[0] $install
Assert-Installed $install $registry; Assert-LegacyMigration $install
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) "Quota Widget Test $TestSuffix"
if (-not (Test-Path -LiteralPath (Join-Path $menu 'Quota Widget.lnk'))) { throw 'Owned Start menu shortcut is missing.' }
if ((Get-FileHash $settings -Algorithm SHA256).Hash -ne $malformedHash -or (Test-Path $integration)) {
    throw 'Claude-off installation inspected or changed the synthetic settings.'
}
Invoke-PackagingSmoke $install (Join-Path $root 'smoke')
Invoke-NativeClaudeProducer $install (Join-Path $root 'claude-state')

$lockedPath = Join-Path $install 'app/QuotaWidget.Core.dll'
$lock = [IO.File]::Open($lockedPath, 'Open', 'Read', 'Read')
try { if ((Invoke-NativeProcess $setups[0] @('/S', "/D=$install")) -eq 0) { throw 'Locked payload was replaced.' } }
finally { $lock.Dispose() }
Assert-Installed $install $registry
Remove-ItemProperty -LiteralPath (Join-Path $registry 'Run') -Name QuotaWidget
Invoke-Setup $setups[0] $install
Assert-Installed $install $registry $false

$original = '{"custom":"preserve"}'
[IO.File]::WriteAllText($settings, $original, [Text.UTF8Encoding]::new($false))
$originalHash = (Get-FileHash $settings -Algorithm SHA256).Hash
Invoke-Setup $setups[1] $install
$metadata = Get-Content $integration -Raw | ConvertFrom-Json
if (-not $metadata.active -or -not (Test-Path $metadata.backup_path) -or
    (Get-FileHash $metadata.backup_path -Algorithm SHA256).Hash -ne $originalHash) {
    throw 'Claude opt-in backup or ownership metadata is invalid.'
}
$appHash = (Get-FileHash (Join-Path $install 'app/QuotaWidget.exe') -Algorithm SHA256).Hash
if ((Invoke-NativeProcess $setups[2] @('/S', "/D=$install")) -eq 0) { throw 'Forced commit failure unexpectedly succeeded.' }
if ((Get-FileHash (Join-Path $install 'app/QuotaWidget.exe') -Algorithm SHA256).Hash -ne $appHash) {
    throw 'Rollback did not restore the installed application.'
}

$uninstaller = Join-Path $install 'Uninstall.exe'
$code = Invoke-NativeProcess $uninstaller @('/S')
if ($code -ne 0) { throw "Uninstaller failed with exit code $code" }
Wait-Uninstall $install $registry $menu
$restored = Get-Content $settings -Raw | ConvertFrom-Json
if ($restored.custom -ne 'preserve' -or $restored.PSObject.Properties.Name -contains 'statusLine') {
    throw 'Uninstall did not restore synthetic Claude settings.'
}
if (-not (Test-Path (Join-Path $install 'legacy-user-file.keep'))) { throw 'Unknown legacy file was removed.' }
if ((Get-FileHash $preferences -Algorithm SHA256).Hash -ne $preferencesHash -or
    (Get-FileHash $profile -Algorithm SHA256).Hash -ne $profileHash) {
    throw 'Widget preferences or isolated profile data changed.'
}

[pscustomobject]@{
    LegacyUpgrade = $true; ClaudeOff = $true; ClaudeOptInRestore = $true; LockedFileRejected = $true
    Rollback = $true; NativeCli = $true; SelfContainedRuntime = $true; Uninstall = $true; DataPreserved = $true
    TestRoot = $root
}
