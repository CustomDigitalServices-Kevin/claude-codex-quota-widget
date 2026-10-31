#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs/QuotaWidget'),
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'QuotaWidget'),
    [string]$RunKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    [string]$StatusLinePath = (Join-Path $env:USERPROFILE '.claude/statusline.sh'),
    [string]$QuitEventName = 'Local\QuotaWidget.Quit',
    [switch]$RemoveData,
    [switch]$RemoveCodexProfile
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts/Deployment.ps1')

$InstallDirectory = Resolve-WidgetDirectory $InstallDirectory
$DataDirectory = Resolve-WidgetDirectory $DataDirectory
Assert-WidgetSeparateDirectories $InstallDirectory $DataDirectory
$state = Read-WidgetInstallState (Join-Path $DataDirectory 'install-state.json')
if (-not $PSBoundParameters.ContainsKey('StatusLinePath') -and $state['statusline_path']) {
    $StatusLinePath = $state['statusline_path']
}
if (Test-Path -LiteralPath $RunKeyPath) {
    if ((Get-ItemProperty -LiteralPath $RunKeyPath).PSObject.Properties.Name -contains 'QuotaWidget') {
        Remove-ItemProperty -LiteralPath $RunKeyPath -Name QuotaWidget
    }
}
Write-Host 'Démarrage avec Windows désactivé.'
Stop-WidgetInstance $InstallDirectory $QuitEventName
if (Test-Path -LiteralPath $InstallDirectory) {
    # Resolve again immediately before recursive deletion; never delete a root or a link.
    $checked = Resolve-WidgetDirectory $InstallDirectory
    Remove-Item -LiteralPath $checked -Recurse -Force
    Write-Host "Dossier d’installation supprimé : $checked"
}
Restore-WidgetStatusLine ([IO.Path]::GetFullPath($StatusLinePath)) $state
if (Test-Path -LiteralPath $DataDirectory) {
    foreach ($item in Get-ChildItem -LiteralPath $DataDirectory -Force) {
        $isCodex = $item.Name -eq 'codex-home'
        if (($isCodex -and $RemoveCodexProfile) -or (-not $isCodex -and $RemoveData)) {
            $full = [IO.Path]::GetFullPath($item.FullName)
            if (-not (Test-WidgetChildPath $full $DataDirectory)) { throw 'Chemin de données hors du dossier demandé.' }
            Remove-Item -LiteralPath $full -Recurse -Force
        }
    }
    if ($RemoveData -and @(Get-ChildItem -LiteralPath $DataDirectory -Force).Count -eq 0) {
        Remove-Item -LiteralPath $DataDirectory
    }
}
if ($RemoveData) { Write-Host 'Données supprimées ; codex-home conservé sauf option explicite.' }
else { Write-Host "Données conservées : $DataDirectory" }
if ($RemoveCodexProfile) { Write-Host 'Profil codex-home supprimé.' }
Write-Host 'Désinstallation terminée.'
