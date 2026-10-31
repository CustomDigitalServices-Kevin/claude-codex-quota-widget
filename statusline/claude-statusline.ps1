#Requires -Version 7.5
<#
.SYNOPSIS
Renders a compact Claude Code status line and publishes local session measurements.
.DESCRIPTION
Reads one official Claude Code status-line JSON document from standard input. The script
writes a bounded, whitelisted JSON state file for the widget and never performs network calls.
.PARAMETER StateDirectory
Destination for per-session JSON files. The default follows the current user's Claude profile.
.EXAMPLE
Get-Content .\synthetic-payload.json -Raw | .\statusline\claude-statusline.ps1 -StateDirectory .\state
#>
[CmdletBinding()]
param(
    [string]$StateDirectory = (Join-Path $env:USERPROFILE '.claude/state/claude-quotas.d')
)

$ErrorActionPreference = 'Stop'
$maximumBytes = 1MB
$utf8 = [Text.UTF8Encoding]::new($false)

function Read-BoundedStandardInput {
    $stream = [Console]::OpenStandardInput()
    $buffer = [byte[]]::new(8192)
    $memory = [IO.MemoryStream]::new()
    try {
        while (($count = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if (($memory.Length + $count) -gt $maximumBytes) { throw 'InputTooLarge' }
            $memory.Write($buffer, 0, $count)
        }
        return $utf8.GetString($memory.ToArray())
    }
    finally { $memory.Dispose() }
}

function Get-SafeString([object]$Value, [int]$MaximumLength) {
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value)) { return $null }
    if ($Value.Length -gt $MaximumLength -or $Value.IndexOfAny([char[]]@(0, 10, 13)) -ge 0) { return $null }
    return $Value
}

function Get-FiniteNumber([object]$Value) {
    if ($Value -isnot [byte] -and $Value -isnot [sbyte] -and
        $Value -isnot [int16] -and $Value -isnot [uint16] -and
        $Value -isnot [int32] -and $Value -isnot [uint32] -and
        $Value -isnot [int64] -and $Value -isnot [uint64] -and
        $Value -isnot [single] -and $Value -isnot [double] -and $Value -isnot [decimal]) { return $null }
    $number = [double]$Value
    if ([double]::IsNaN($number) -or [double]::IsInfinity($number)) { return $null }
    return $number
}

function ConvertFrom-SafeJson([string]$Text, [int]$Depth) {
    return $Text | ConvertFrom-Json -AsHashtable -Depth $Depth -DateKind String
}

function Test-RegularPath([string]$Path, [switch]$Directory) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
    return $(if ($Directory) { $item.PSIsContainer } else { -not $item.PSIsContainer })
}

function Get-ResetValue([object]$Value) {
    $number = Get-FiniteNumber $Value
    if ($null -ne $number -and $number -ge 0 -and $number -le 253402300799 -and
        [math]::Truncate($number) -eq $number) { return [long]$number }
    $text = Get-SafeString $Value 80
    if (-not $text -or $text -cnotmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$') {
        return $null
    }
    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
        $text,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None,
        [ref]$parsed
    )) { return $null }
    return $text
}

function Get-RateWindow([object]$Window) {
    if ($Window -isnot [Collections.IDictionary]) { return $null }
    $result = [ordered]@{}
    $used = Get-FiniteNumber $Window.used_percentage
    if ($null -ne $used -and $used -ge 0 -and $used -le 100) { $result.used_percentage = $used }
    $reset = Get-ResetValue $Window.resets_at
    if ($null -ne $reset) { $result.resets_at = $reset }
    if ($result.Count -eq 0) { return $null }
    return $result
}

function Read-PreviousState([string]$Path, [string]$SessionId) {
    if (-not (Test-RegularPath $Path)) { return $null }
    $file = Get-Item -LiteralPath $Path -Force
    if ($file.Length -gt $maximumBytes) { return $null }
    try {
        $value = ConvertFrom-SafeJson ([IO.File]::ReadAllText($Path, $utf8)) 10
        if ($value -isnot [Collections.IDictionary] -or $value.session_id -cne $SessionId) { return $null }
        return $value
    }
    catch { return $null }
}

function Write-AtomicJson([string]$Path, [object]$Value) {
    $json = $Value | ConvertTo-Json -Depth 6 -Compress
    $bytes = $utf8.GetBytes($json)
    if ($bytes.Length -gt $maximumBytes) { throw 'StateTooLarge' }
    if (Test-Path -LiteralPath $Path) {
        if (-not (Test-RegularPath $Path)) { throw 'UnsafeTarget' }
    }
    $temporary = Join-Path (Split-Path $Path -Parent) ('.quota-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllBytes($temporary, $bytes)
        [IO.File]::Move($temporary, $Path, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

try {
    $payload = ConvertFrom-SafeJson (Read-BoundedStandardInput) 20
    if ($payload -isnot [Collections.IDictionary]) { throw 'InvalidPayload' }
    $sessionId = Get-SafeString $payload.session_id 128
    $parsedSessionId = [guid]::Empty
    if (-not $sessionId -or -not [guid]::TryParseExact($sessionId, 'D', [ref]$parsedSessionId)) { throw 'InvalidSession' }

    if (Test-Path -LiteralPath $StateDirectory) {
        if (-not (Test-RegularPath $StateDirectory -Directory)) { throw 'UnsafeStateDirectory' }
    }
    else { [IO.Directory]::CreateDirectory($StateDirectory) | Out-Null }

    $statePath = Join-Path $StateDirectory ($sessionId + '.json')
    $previous = Read-PreviousState $statePath $sessionId
    $now = [DateTimeOffset]::UtcNow.ToString('O')
    $apiDuration = Get-FiniteNumber $payload.cost.total_api_duration_ms
    $measuredAt = if ($previous) { Get-SafeString $previous.measured_at 80 } else { $null }
    if ($null -ne $apiDuration -and $previous) {
        $priorDuration = Get-FiniteNumber $previous.api_duration_ms
        if ($null -eq $priorDuration -or $priorDuration -ne $apiDuration) { $measuredAt = $now }
    }

    $state = [ordered]@{ session_id = $sessionId; rendered_at = $now; measured_at = $measuredAt }
    if ($null -ne $apiDuration -and $apiDuration -ge 0) { $state.api_duration_ms = $apiDuration }
    $inputTokens = Get-FiniteNumber $payload.context_window.total_input_tokens
    if ($null -ne $inputTokens -and $inputTokens -ge 0) { $state.input_tokens = $inputTokens }

    $modelId = Get-SafeString $payload.model.id 160
    if ($modelId) { $state.model = [ordered]@{ id = $modelId } }
    $effort = Get-SafeString $payload.effort.level 40
    if ($effort) { $state.effort = [ordered]@{ level = $effort } }
    $agentName = Get-SafeString $payload.agent.name 120
    if ($agentName) { $state.agent = [ordered]@{ name = $agentName } }
    $sessionName = Get-SafeString $payload.session_name 200
    if ($sessionName) { $state.session_name = $sessionName }
    $cwd = Get-SafeString $payload.workspace.current_dir 1024
    if (-not $cwd) { $cwd = Get-SafeString $payload.cwd 1024 }
    if ($cwd) { $state.cwd = $cwd }

    $rates = [ordered]@{}
    $fiveHour = Get-RateWindow $payload.rate_limits.five_hour
    $sevenDay = Get-RateWindow $payload.rate_limits.seven_day
    if ($fiveHour) { $rates.five_hour = $fiveHour }
    if ($sevenDay) { $rates.seven_day = $sevenDay }
    if ($rates.Count -gt 0) { $state.rate_limits = $rates }
    Write-AtomicJson $statePath $state

    $parts = [Collections.Generic.List[string]]::new()
    $displayName = Get-SafeString $payload.model.display_name 80
    if (-not $displayName) { $displayName = $modelId }
    if ($displayName) { $parts.Add($displayName) }
    if ($effort) { $parts.Add($effort) }
    if ($agentName) { $parts.Add($agentName) }
    elseif ($sessionName) { $parts.Add($sessionName) }
    if ($fiveHour -and $fiveHour.Contains('used_percentage')) {
        $parts.Add(('5h {0}%' -f [math]::Round($fiveHour.used_percentage, 0, [MidpointRounding]::AwayFromZero)))
    }
    if ($sevenDay -and $sevenDay.Contains('used_percentage')) {
        $parts.Add(('7d {0}%' -f [math]::Round($sevenDay.used_percentage, 0, [MidpointRounding]::AwayFromZero)))
    }
    if ($parts.Count -eq 0) { 'Claude' } else { 'Claude | ' + ($parts -join ' | ') }
}
catch {
    'Claude unavailable'
}
