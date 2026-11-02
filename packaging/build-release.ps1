#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.2',
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [string]$NsisZipPath = $env:QUOTAWIDGET_NSIS_ZIP,
    [switch]$BuildTestInstallers,
    [ValidatePattern('^[A-Za-z0-9-]{1,32}$')][string]$TestSuffix = 'local',
    [switch]$SkipSourceArchive
)

$ErrorActionPreference = 'Stop'
$script:NsisVersion = '3.13'
$script:NsisUrl = 'https://downloads.sourceforge.net/project/nsis/NSIS%203/3.13/nsis-3.13.zip'
$script:NsisSha256 = 'BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8'
$script:NsisSize = 2363925

function Assert-NoReparseChain([string]$Path) {
    for ($item = [IO.DirectoryInfo]::new($Path); $item; $item = $item.Parent) {
        if ($item.Exists -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Reparse paths are not allowed in release builds: $($item.FullName)"
        }
    }
}

function Assert-ChildPath([string]$Path, [string]$Parent) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe build path outside repository artifacts: $full"
    }
    Assert-NoReparseChain $full
    return $full
}

function Reset-BuildDirectory([string]$Path, [string]$ArtifactsRoot) {
    $checked = Assert-ChildPath $Path $ArtifactsRoot
    if (Test-Path -LiteralPath $checked) {
        Remove-Item -LiteralPath $checked -Recurse -Force
    }
    [IO.Directory]::CreateDirectory($checked) | Out-Null
}

function Invoke-Publish([string]$Project, [string]$Destination) {
    & dotnet publish $Project -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=false -p:PublishTrimmed=false `
        -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version `
        -o $Destination -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $Project" }
}

function Copy-ReleaseDocuments([string]$Repository, [string]$Payload, [string]$NsisRoot) {
    foreach ($name in 'README.md', 'README.fr.md', 'LICENSE', 'PRIVACY.md', 'SECURITY.md') {
        Copy-Item -LiteralPath (Join-Path $Repository $name) -Destination $Payload
    }
    $dotnetRoot = Split-Path (Get-Command dotnet -ErrorAction Stop).Source
    Copy-Item -LiteralPath (Join-Path $dotnetRoot 'LICENSE.txt') `
        -Destination (Join-Path $Payload 'DOTNET-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') `
        -Destination (Join-Path $Payload 'DOTNET-THIRD-PARTY-NOTICES.txt')
    Copy-Item -LiteralPath (Join-Path $NsisRoot 'COPYING') `
        -Destination (Join-Path $Payload 'NSIS-COPYING.txt')
}

function New-PayloadManifest([string]$Payload) {
    $items = Get-ChildItem -LiteralPath $Payload -File -Recurse | Sort-Object FullName
    $entries = foreach ($item in $items) {
        $relative = [IO.Path]::GetRelativePath($Payload, $item.FullName).Replace('\', '/')
        [ordered]@{ path = $relative; size = $item.Length; sha256 = (Get-FileHash $item.FullName -Algorithm SHA256).Hash }
    }
    $manifest = [ordered]@{
        schema = 1
        note = 'This manifest covers every payload file except payload-manifest.json itself.'
        files = @($entries)
    }
    $path = Join-Path $Payload 'payload-manifest.json'
    [IO.File]::WriteAllText($path, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    return @($entries)
}

function ConvertTo-NsisLiteral([string]$Value) {
    return $Value.Replace('$', '$$').Replace('"', '$\"').Replace('/', '\')
}

function New-DeleteInclude([string]$Payload, [string]$Path) {
    $files = Get-ChildItem -LiteralPath $Payload -File -Recurse | Sort-Object FullName
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('!ifndef PAYLOAD_ROOT')
    $lines.Add('  !error "PAYLOAD_ROOT is required"')
    $lines.Add('!endif')
    foreach ($file in $files) {
        $relative = ConvertTo-NsisLiteral ([IO.Path]::GetRelativePath($Payload, $file.FullName))
        $lines.Add("Delete `"`${PAYLOAD_ROOT}\$relative`"")
    }
    $directories = Get-ChildItem -LiteralPath $Payload -Directory -Recurse |
        Sort-Object { $_.FullName.Length } -Descending
    foreach ($directory in $directories) {
        $relative = ConvertTo-NsisLiteral ([IO.Path]::GetRelativePath($Payload, $directory.FullName))
        $lines.Add("RMDir `"`${PAYLOAD_ROOT}\$relative`"")
    }
    [IO.File]::WriteAllLines($Path, $lines, [Text.UTF8Encoding]::new($false))
}

function New-PreflightInclude([string]$Payload, [string]$Path) {
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $Payload -File -Recurse | Sort-Object FullName) {
        $relative = ConvertTo-NsisLiteral ([IO.Path]::GetRelativePath($Payload, $file.FullName))
        $lines.Add("IfFileExists `"`$INSTDIR\app\$relative`" 0 +4")
        $lines.Add('ClearErrors')
        $lines.Add("FileOpen `$9 `"`$INSTDIR\app\$relative`" a")
        $lines.Add('IfErrors preflight_failed')
        $lines.Add('FileClose $9')
    }
    [IO.File]::WriteAllLines($Path, $lines, [Text.UTF8Encoding]::new($false))
}

function Expand-VerifiedNsis([string]$ZipPath, [string]$Destination) {
    if ([string]::IsNullOrWhiteSpace($ZipPath)) {
        throw "Provide -NsisZipPath. Official source: $script:NsisUrl"
    }
    $zip = [IO.Path]::GetFullPath($ZipPath)
    if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) { throw "NSIS archive not found: $zip" }
    $item = Get-Item -LiteralPath $zip
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    if ($item.Length -ne $script:NsisSize -or $hash -ne $script:NsisSha256) {
        throw "NSIS archive verification failed. Expected $script:NsisSha256 ($script:NsisSize bytes)."
    }
    Expand-Archive -LiteralPath $zip -DestinationPath $Destination
    $compiler = Join-Path $Destination "nsis-$script:NsisVersion/makensis.exe"
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
        throw 'makensis.exe is missing from the verified NSIS archive.'
    }
    return $compiler
}

function Invoke-NsisBuild(
    [string]$Compiler, [string]$ScriptPath, [string]$Payload, [string]$DeleteInclude, [string]$PreflightInclude,
    [string]$OutputFile, [string]$BuildVersion, [string[]]$AdditionalDefines
) {
    $arguments = @('/V2', "/DVERSION=$BuildVersion", "/DPAYLOAD_DIR=$Payload",
        "/DPAYLOAD_DELETE_INCLUDE=$DeleteInclude", "/DPAYLOAD_PREFLIGHT_INCLUDE=$PreflightInclude",
        "/DOUTPUT_FILE=$OutputFile")
    $arguments += $AdditionalDefines
    $arguments += $ScriptPath
    & $Compiler @arguments
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $OutputFile -PathType Leaf)) {
        throw "NSIS compilation failed: $OutputFile"
    }
}

function New-SourceArchive([string]$Repository, [string]$Destination) {
    $dirty = & git -C $Repository status --porcelain
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect Git status.' }
    if ($dirty) { throw 'Commit all source files before creating the source release archive.' }
    & git -C $Repository archive --format=zip --output=$Destination HEAD
    if ($LASTEXITCODE -ne 0) { throw 'git archive failed.' }
}

function New-ReleaseMetadata(
    [string]$ReleaseRoot, [string]$SetupPath, [string]$SourcePath,
    [object[]]$PayloadEntries, [string]$Commit
) {
    $assets = [Collections.Generic.List[object]]::new()
    foreach ($path in @($SetupPath, $SourcePath)) {
        if ($path -and (Test-Path -LiteralPath $path)) {
            $item = Get-Item -LiteralPath $path
            $assets.Add([ordered]@{ name = $item.Name; size = $item.Length; sha256 = (Get-FileHash $path -Algorithm SHA256).Hash })
        }
    }
    $metadata = [ordered]@{
        schema = 1; version = $Version; runtime = $Runtime; selfContained = $true
        publishSingleFile = $false; publishTrimmed = $false; signed = $false; gitCommit = $Commit
        nsis = [ordered]@{ version = $script:NsisVersion; url = $script:NsisUrl; sha256 = $script:NsisSha256 }
        payload = $PayloadEntries; assets = $assets
    }
    $jsonPath = Join-Path $ReleaseRoot 'release-manifest.json'
    [IO.File]::WriteAllText($jsonPath, ($metadata | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $sumLines = $assets | ForEach-Object { "$($_.sha256)  $($_.name)" }
    [IO.File]::WriteAllLines((Join-Path $ReleaseRoot 'SHA256SUMS.txt'), $sumLines, [Text.UTF8Encoding]::new($false))
}

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = Join-Path $repository 'artifacts'
$buildRoot = Join-Path $artifacts "build-$Version"
$releaseRoot = Join-Path $artifacts "release-$Version"
Reset-BuildDirectory $buildRoot $artifacts
Reset-BuildDirectory $releaseRoot $artifacts
$payload = Join-Path $buildRoot 'payload'
[IO.Directory]::CreateDirectory($payload) | Out-Null

Invoke-Publish (Join-Path $repository 'src/QuotaWidget.App/QuotaWidget.App.csproj') $payload
Invoke-Publish (Join-Path $repository 'src/QuotaWidget.Cli/QuotaWidget.Cli.csproj') $payload
foreach ($binary in 'QuotaWidget.exe', 'quotas.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $binary) -PathType Leaf)) {
        throw "Published binary missing: $binary"
    }
}

$nsisRoot = Join-Path $buildRoot 'nsis'
$compiler = Expand-VerifiedNsis $NsisZipPath $nsisRoot
Copy-ReleaseDocuments $repository $payload (Split-Path $compiler)
$payloadEntries = New-PayloadManifest $payload
$deleteInclude = Join-Path $buildRoot 'payload-delete.nsh'
New-DeleteInclude $payload $deleteInclude
$preflightInclude = Join-Path $buildRoot 'payload-preflight.nsh'
New-PreflightInclude $payload $preflightInclude
$scriptPath = Join-Path $PSScriptRoot 'QuotaWidget.nsi'
$setupPath = Join-Path $releaseRoot "QuotaWidget-Setup-$Version-win-x64.exe"
Invoke-NsisBuild $compiler $scriptPath $payload $deleteInclude $preflightInclude $setupPath $Version @()

if ($BuildTestInstallers) {
    $testRoot = Join-Path $buildRoot 'test-installers'
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $variants = @(
        @{ Name = 'off'; Defines = @('/DTEST_BUILD=1', "/DTEST_SUFFIX=$TestSuffix") },
        @{ Name = 'on'; Defines = @('/DTEST_BUILD=1', "/DTEST_SUFFIX=$TestSuffix", '/DCLAUDE_TEST_OPTIN=1') },
        @{ Name = 'rollback'; Defines = @('/DTEST_BUILD=1', "/DTEST_SUFFIX=$TestSuffix", '/DTEST_FORCE_COMMIT_FAILURE=1') }
    )
    foreach ($variant in $variants) {
        $testPath = Join-Path $testRoot "QuotaWidget-v$Version-test-$TestSuffix-$($variant.Name)-setup.exe"
        Invoke-NsisBuild $compiler $scriptPath $payload $deleteInclude $preflightInclude $testPath $Version $variant.Defines
    }
}

$sourcePath = $null
if (-not $SkipSourceArchive) {
    $sourcePath = Join-Path $releaseRoot "QuotaWidget-v$Version-source.zip"
    New-SourceArchive $repository $sourcePath
}
$commit = (& git -C $repository rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Unable to read the Git commit.' }
New-ReleaseMetadata $releaseRoot $setupPath $sourcePath $payloadEntries $commit
[pscustomobject]@{ ReleaseDirectory = $releaseRoot; Setup = $setupPath; Source = $sourcePath; TestDirectory = (Join-Path $buildRoot 'test-installers') }
