# Shared implementation for the two entry scripts. No action runs when dot-sourced.
Set-StrictMode -Version Latest

function Resolve-WidgetDirectory {
    param([Parameter(Mandatory)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $root = [IO.Path]::GetPathRoot($full).TrimEnd('\', '/')
    if ($full -eq $root -or $full -eq [Environment]::GetFolderPath('UserProfile') -or
        $full -eq [Environment]::GetFolderPath('LocalApplicationData')) {
        throw "Dossier trop large pour cette opération : $full"
    }
    if (Test-Path -LiteralPath $full) {
        $item = Get-Item -LiteralPath $full -Force
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Le chemin doit être un dossier sans lien : $full"
        }
    }
    return $full
}

function Test-WidgetChildPath {
    param([string]$Path, [string]$Parent)
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    return $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-WidgetSeparateDirectories {
    param([string]$First, [string]$Second)
    if ($First -eq $Second -or (Test-WidgetChildPath $First $Second) -or (Test-WidgetChildPath $Second $First)) {
        throw "Les dossiers de données, de source et d’installation doivent être distincts et non imbriqués."
    }
}

function Write-WidgetAtomicText {
    param([string]$Path, [string]$Text)
    $full = [IO.Path]::GetFullPath($Path)
    $directory = [IO.Path]::GetDirectoryName($full)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporary = Join-Path $directory ('.' + [IO.Path]::GetFileName($full) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporary, $Text, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $full, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Get-WidgetBlock {
    param([string]$Text, [switch]$Required)
    $starts = [regex]::Matches($Text, '(?m)^# >>> quota-widget[^\r\n]*\r?$')
    $ends = [regex]::Matches($Text, '(?m)^# <<< quota-widget[^\r\n]*\r?$')
    if ($starts.Count -eq 0 -and $ends.Count -eq 0 -and -not $Required) { return $null }
    if ($starts.Count -ne 1 -or $ends.Count -ne 1 -or $ends[0].Index -lt $starts[0].Index) {
        throw 'Un seul bloc complet à marqueurs quota-widget est requis ; aucun emplacement ne sera deviné.'
    }
    return [regex]::Match($Text, '(?m)^# >>> quota-widget[^\r\n]*\r?\n[\s\S]*?^# <<< quota-widget[^\r\n]*(?:\r?\n|$)')
}

function Backup-WidgetStatusLine {
    param([string]$Path)
    $backup = $Path + '.bak-' + (Get-Date -Format 'yyyyMMdd-HHmm')
    if (Test-Path -LiteralPath $backup) {
        # Keep the specified minute stamp, adding a suffix rather than overwriting a backup.
        $backup += '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    }
    Copy-Item -LiteralPath $Path -Destination $backup
    return $backup
}

function Read-WidgetInstallState {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return @{} }
    try {
        $state = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
        if ($state -isnot [System.Collections.IDictionary]) { throw 'Invalid state object.' }
        return $state
    }
    catch { throw "État d’installation illisible ; conserver ce fichier et le vérifier avant de continuer." }
}

function Get-WidgetProcesses {
    param([string]$InstallDirectory)
    Get-Process -Name QuotaWidget -ErrorAction SilentlyContinue | Where-Object {
        $processPath = $_.Path
        $processPath -and (Test-WidgetChildPath $processPath $InstallDirectory)
    }
}

function Stop-WidgetInstance {
    param([string]$InstallDirectory, [string]$QuitEventName)
    $signal = $null
    try {
        $signal = [Threading.EventWaitHandle]::OpenExisting($QuitEventName)
        $signal.Set() | Out-Null
    }
    catch [Threading.WaitHandleCannotBeOpenedException] { }
    finally { if ($null -ne $signal) { $signal.Dispose() } }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $running = @(Get-WidgetProcesses $InstallDirectory)
        if ($running.Count -eq 0) { return }
        if ($watch.Elapsed.TotalSeconds -ge 10) { break }
        Start-Sleep -Milliseconds 100
    } while ($true)
    Get-WidgetProcesses $InstallDirectory | Stop-Process -Force
}

function Restore-WidgetStatusLine {
    param([string]$Path, [System.Collections.IDictionary]$State)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    $text = [IO.File]::ReadAllText($Path)
    $block = Get-WidgetBlock $text
    if ($null -eq $block) { return }
    $canRestore = $State['statusline_path'] -eq [IO.Path]::GetFullPath($Path) -and
        $State['statusline_sha256'] -and $State['original_backup'] -and $State['original_sha256'] -and
        (Test-Path -LiteralPath $State['original_backup'] -PathType Leaf)
    if ($canRestore) {
        $canRestore = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $State['statusline_sha256'] -and
            (Get-FileHash -LiteralPath $State['original_backup'] -Algorithm SHA256).Hash -eq $State['original_sha256']
    }
    $backup = Backup-WidgetStatusLine $Path
    if ($canRestore) {
        # Restore the exact original bytes, including its original encoding and line endings.
        $temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
        try {
            Copy-Item -LiteralPath $State['original_backup'] -Destination $temporary
            [IO.File]::Move($temporary, $Path, $true)
        }
        finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
        Write-Host "Barre d’état restaurée intégralement ; sauvegarde : $backup"
    }
    else {
        Write-WidgetAtomicText $Path ($text.Remove($block.Index, $block.Length))
        Write-Host "Bloc quota-widget retiré ; modifications conservées ; sauvegarde : $backup"
    }
}
