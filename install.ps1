#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs/QuotaWidget'),
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'QuotaWidget'),
    [string]$RunKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    [string]$SourceDirectory,
    [string]$RepositoryDirectory = $PSScriptRoot,
    [string]$TemporaryDirectory = [IO.Path]::GetTempPath(),
    [string]$DotNetCommand = 'dotnet',
    [string]$QuitEventName = 'Local\QuotaWidget.Quit',
    [switch]$NoStart,
    [switch]$StatusLine,
    [string]$StatusLinePath = (Join-Path $env:USERPROFILE '.claude/statusline.sh'),
    [string]$StatusLineSourcePath = (Join-Path $PSScriptRoot 'statusline/statusline.sh'),
    [string]$OriginalBackup
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts/Deployment.ps1')

$InstallDirectory = Resolve-WidgetDirectory $InstallDirectory
$DataDirectory = Resolve-WidgetDirectory $DataDirectory
Assert-WidgetSeparateDirectories $InstallDirectory $DataDirectory
$statePath = Join-Path $DataDirectory 'install-state.json'
$state = Read-WidgetInstallState $statePath

# Validate optional statusline changes before publication, process stop or registry writes.
$replacement = $null
if ($StatusLine) {
    Write-Warning 'Legacy Bash block replacement. New installations should use configure-claude.ps1 instead.'
    $StatusLinePath = [IO.Path]::GetFullPath($StatusLinePath)
    $targetText = [IO.File]::ReadAllText($StatusLinePath)
    $targetBlock = Get-WidgetBlock $targetText -Required
    $sourceText = [IO.File]::ReadAllText([IO.Path]::GetFullPath($StatusLineSourcePath))
    $sourceBlock = Get-WidgetBlock $sourceText -Required
    $replacement = $targetText.Remove($targetBlock.Index, $targetBlock.Length).Insert($targetBlock.Index, $sourceBlock.Value)
    $replacement = $replacement.Replace("`r`n", "`n")
}
if ($OriginalBackup) {
    $OriginalBackup = [IO.Path]::GetFullPath($OriginalBackup)
    if ([IO.File]::ReadAllText($OriginalBackup) -match '# (>>>|<<<) quota-widget') {
        throw 'La sauvegarde originale ne doit contenir aucun marqueur quota-widget.'
    }
    if (-not $state['original_backup']) {
        $state['original_backup'] = $OriginalBackup
        $state['original_sha256'] = (Get-FileHash -LiteralPath $OriginalBackup -Algorithm SHA256).Hash
    }
}

try { $runtimes = & $DotNetCommand --list-runtimes }
catch { throw 'Commande dotnet indisponible. Installez le runtime .NET Desktop 10 avant de continuer.' }
if ($LASTEXITCODE -ne 0 -or -not ($runtimes -match '^Microsoft\.WindowsDesktop\.App 10\.')) {
    throw 'Le runtime .NET Desktop 10 est requis (Microsoft.WindowsDesktop.App 10.x). Installez-le avant de continuer.'
}

$staging = $null
if (-not $SourceDirectory) {
    $sdks = & $DotNetCommand --list-sdks
    if ($LASTEXITCODE -ne 0 -or -not ($sdks -match '^10\.')) {
        throw 'The .NET 10 SDK is required to install from source. A prebuilt package only requires Desktop Runtime 10.'
    }
}
try {
    if ($SourceDirectory) {
        $payload = Resolve-WidgetDirectory $SourceDirectory
        Assert-WidgetSeparateDirectories $payload $InstallDirectory
        Assert-WidgetSeparateDirectories $payload $DataDirectory
    }
    else {
        $staging = [IO.Path]::GetFullPath((Join-Path $TemporaryDirectory ('QuotaWidget-' + [guid]::NewGuid().ToString('N'))))
        $payload = $staging
        [IO.Directory]::CreateDirectory($staging) | Out-Null
        foreach ($project in 'QuotaWidget.App', 'QuotaWidget.Cli') {
            $projectPath = Join-Path $RepositoryDirectory "src/$project/$project.csproj"
            & $DotNetCommand publish $projectPath -c Release --self-contained false -o $staging | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Publication échouée : $project" }
        }
    }
    foreach ($binary in 'QuotaWidget.exe', 'quotas.exe') {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $binary) -PathType Leaf)) {
            throw "Binaire publié manquant : $binary"
        }
    }
    Stop-WidgetInstance $InstallDirectory $QuitEventName
    [IO.Directory]::CreateDirectory($InstallDirectory) | Out-Null
    Get-ChildItem -LiteralPath $payload -Force | Copy-Item -Destination $InstallDirectory -Recurse -Force
    $executable = Join-Path $InstallDirectory 'QuotaWidget.exe'
    if ($executable.Contains('"')) { throw "Le chemin de l’exécutable contient un guillemet invalide." }
    if (-not (Test-Path -LiteralPath $RunKeyPath)) { New-Item -Path $RunKeyPath -Force | Out-Null }
    New-ItemProperty -LiteralPath $RunKeyPath -Name QuotaWidget -Value ('"' + $executable + '"') -PropertyType String -Force | Out-Null
    if ($StatusLine) {
        $backup = Backup-WidgetStatusLine $StatusLinePath
        Write-WidgetAtomicText $StatusLinePath $replacement
        $state['statusline_path'] = $StatusLinePath
        $state['statusline_sha256'] = (Get-FileHash -LiteralPath $StatusLinePath -Algorithm SHA256).Hash
        Write-Host "Bloc de barre d’état installé ; sauvegarde : $backup"
    }
    $state['installed_at'] = [DateTimeOffset]::UtcNow.ToString('O')
    foreach ($field in 'statusline_path', 'statusline_sha256', 'original_backup', 'original_sha256') {
        if (-not $state.Contains($field)) { $state[$field] = $null }
    }
    Write-WidgetAtomicText $statePath ($state | ConvertTo-Json -Depth 10)
    if (-not $NoStart) { Start-Process -FilePath $executable -WorkingDirectory $InstallDirectory | Out-Null }
    Write-Host "Quotas IA installé dans $InstallDirectory. Démarrage avec Windows activé."
}
finally {
    if ($staging -and (Test-Path -LiteralPath $staging)) {
        if (-not (Test-WidgetChildPath $staging $TemporaryDirectory)) { throw 'Chemin de préparation hors du dossier temporaire.' }
        $checked = Resolve-WidgetDirectory $staging
        Remove-Item -LiteralPath $checked -Recurse -Force
    }
}
