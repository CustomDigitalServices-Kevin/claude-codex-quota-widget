#Requires -Version 7.5
#Requires -Modules @{ ModuleName = 'Pester'; RequiredVersion = '5.7.1' }

Describe 'Claude Code public setup' {
    BeforeAll {
        $repository = Split-Path $PSScriptRoot -Parent
        $producer = Join-Path $repository 'statusline/claude-statusline.ps1'
        $configurator = Join-Path $repository 'configure-claude.ps1'
        $pwsh = (Get-Process -Id $PID).Path
        $utf8 = [Text.UTF8Encoding]::new($false)

        function Invoke-Producer([string]$InputText, [string]$StateDirectory) {
            $start = [Diagnostics.ProcessStartInfo]::new()
            $start.FileName = $pwsh
            $start.UseShellExecute = $false
            $start.RedirectStandardInput = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $start.ArgumentList.Add('-NoProfile')
            $start.ArgumentList.Add('-File')
            $start.ArgumentList.Add($producer)
            $start.ArgumentList.Add('-StateDirectory')
            $start.ArgumentList.Add($StateDirectory)
            $process = [Diagnostics.Process]::Start($start)
            $process.StandardInput.Write($InputText)
            $process.StandardInput.Close()
            $stdout = $process.StandardOutput.ReadToEnd().Trim()
            $stderr = $process.StandardError.ReadToEnd().Trim()
            $process.WaitForExit()
            return [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout; Stderr = $stderr }
        }

        function New-Payload(
            [double]$ApiDuration = 125,
            [string]$SessionId = '11111111-2222-4333-8444-555555555555'
        ) {
            [ordered]@{
                session_id = $SessionId
                session_name = 'Review dashboard'
                cwd = 'C:\synthetic\fallback'
                workspace = @{ current_dir = 'C:\synthetic\workspace' }
                model = @{ id = 'claude-sonnet-test'; display_name = 'Sonnet Test' }
                effort = @{ level = 'high' }
                agent = @{ name = 'UI reviewer' }
                cost = @{ total_api_duration_ms = $ApiDuration; SECRET = 'never-write-me' }
                context_window = @{ total_input_tokens = 321; prompt = 'private prompt' }
                rate_limits = @{
                    five_hour = @{ used_percentage = 42.5; resets_at = '2030-01-01T01:00:00Z'; SECRET = 'hidden' }
                    seven_day = @{ used_percentage = 17; resets_at = '2030-01-07T01:00:00Z' }
                }
                credentials = 'private credential'
            } | ConvertTo-Json -Depth 8 -Compress
        }
    }

    BeforeEach {
        $scenario = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $stateDirectory = Join-Path $scenario 'state'
        $settingsPath = Join-Path $scenario 'profile with spaces\settings.json'
        $dummyProducer = Join-Path $scenario 'scripts (safe) with spaces\claude line.ps1'
        [IO.Directory]::CreateDirectory((Split-Path $dummyProducer -Parent)) | Out-Null
        [IO.File]::WriteAllText($dummyProducer, "'synthetic'", $utf8)
    }

    It 'starts with unknown measurement, advances on cost change, and never freshens an unchanged cost' {
        $first = Invoke-Producer (New-Payload -ApiDuration 125) $stateDirectory
        $first.ExitCode | Should -Be 0
        $first.Stderr | Should -BeNullOrEmpty
        $first.Stdout | Should -Be 'Claude | Sonnet Test | high | UI reviewer | 5h 43% | 7d 17%'
        $statePath = Join-Path $stateDirectory '11111111-2222-4333-8444-555555555555.json'
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        $state.session_id | Should -BeExactly '11111111-2222-4333-8444-555555555555'
        $state.measured_at | Should -BeNullOrEmpty

        $state.measured_at = '2029-12-01T00:00:00.0000000+00:00'
        [IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json -Depth 8 -Compress), $utf8)
        Invoke-Producer (New-Payload -ApiDuration 125) $stateDirectory | Out-Null
        $sameRaw = [IO.File]::ReadAllText($statePath, $utf8)
        $sameRaw | Should -Match '"measured_at":"2029-12-01T00:00:00\.0000000\+00:00"'
        $same = $sameRaw | ConvertFrom-Json

        Invoke-Producer (New-Payload -ApiDuration 126) $stateDirectory | Out-Null
        $changed = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        $changed.measured_at | Should -Not -BeNullOrEmpty
        $changed.measured_at | Should -Not -BeExactly $same.measured_at
    }

    It 'persists only the metadata and measurements on the public whitelist' {
        Invoke-Producer (New-Payload) $stateDirectory | Out-Null
        $statePath = Join-Path $stateDirectory '11111111-2222-4333-8444-555555555555.json'
        $raw = [IO.File]::ReadAllText($statePath, $utf8)
        $raw | Should -Not -Match 'SECRET|private prompt|private credential|credentials|prompt'
        $state = $raw | ConvertFrom-Json
        $state.model.id | Should -BeExactly 'claude-sonnet-test'
        $state.effort.level | Should -BeExactly 'high'
        $state.agent.name | Should -BeExactly 'UI reviewer'
        $state.session_name | Should -BeExactly 'Review dashboard'
        $state.cwd | Should -BeExactly 'C:\synthetic\workspace'
        $raw | Should -Match '"resets_at":"2030-01-01T01:00:00Z"'
        $state.PSObject.Properties.Name | Should -Be @('session_id', 'rendered_at', 'measured_at', 'api_duration_ms', 'input_tokens', 'model', 'effort', 'agent', 'session_name', 'cwd', 'rate_limits')
    }

    It 'uses the exact UUID filename required by the session reader' {
        Invoke-Producer (New-Payload) $stateDirectory | Out-Null
        $files = @(Get-ChildItem -LiteralPath $stateDirectory -File)
        $files.Count | Should -Be 1
        $files[0].Name | Should -BeExactly '11111111-2222-4333-8444-555555555555.json'
        $state = [IO.File]::ReadAllText($files[0].FullName, $utf8) | ConvertFrom-Json
        $state.session_id | Should -BeExactly '11111111-2222-4333-8444-555555555555'
    }

    It 'preserves a bounded numeric reset epoch without converting it to text' {
        $payload = New-Payload | ConvertFrom-Json -AsHashtable
        $payload.rate_limits.five_hour.resets_at = 1893459600
        $payload.rate_limits.seven_day.resets_at = 'not-an-iso-timestamp'
        Invoke-Producer ($payload | ConvertTo-Json -Depth 8 -Compress) $stateDirectory | Out-Null
        $state = Get-Content -LiteralPath (Join-Path $stateDirectory '11111111-2222-4333-8444-555555555555.json') -Raw | ConvertFrom-Json
        $state.rate_limits.five_hour.resets_at | Should -BeOfType [long]
        $state.rate_limits.five_hour.resets_at | Should -Be 1893459600
        $state.rate_limits.seven_day.PSObject.Properties.Name | Should -Not -Contain 'resets_at'
    }

    It 'keeps useful metadata when Claude has not supplied rate limits yet' {
        $payload = New-Payload | ConvertFrom-Json -AsHashtable
        $payload.Remove('rate_limits')
        Invoke-Producer ($payload | ConvertTo-Json -Depth 8 -Compress) $stateDirectory | Out-Null
        $state = Get-Content -LiteralPath (Join-Path $stateDirectory '11111111-2222-4333-8444-555555555555.json') -Raw | ConvertFrom-Json
        $state.model.id | Should -BeExactly 'claude-sonnet-test'
        $state.PSObject.Properties.Name | Should -Not -Contain 'rate_limits'
    }

    It 'uses a neutral label when the payload proves no activity or quota' {
        $payload = @{ session_id = '11111111-2222-4333-8444-555555555555' } | ConvertTo-Json -Compress
        $result = Invoke-Producer $payload $stateDirectory
        $result.Stdout | Should -BeExactly 'Claude'
        $result.Stdout | Should -Not -Match 'active'
    }

    It 'rejects unsafe session identifiers without disclosing input' {
        $result = Invoke-Producer (New-Payload -SessionId '..\SECRET') $stateDirectory
        $result.Stdout | Should -BeExactly 'Claude unavailable'
        $result.Stderr | Should -BeNullOrEmpty
        $result.Stdout | Should -Not -Match 'SECRET'
        Test-Path -LiteralPath $stateDirectory | Should -BeFalse
    }

    It 'rejects stdin larger than one MiB without creating state' {
        $result = Invoke-Producer ('x' * (1MB + 1)) $stateDirectory
        $result.Stdout | Should -BeExactly 'Claude unavailable'
        $result.Stderr | Should -BeNullOrEmpty
        Test-Path -LiteralPath $stateDirectory | Should -BeFalse
    }

    It 'is a no-write dry run and emits a valid quoted forward-slash command' {
        $fragment = (& $configurator -SettingsPath $settingsPath -StatusLineScriptPath $dummyProducer) | ConvertFrom-Json
        Test-Path -LiteralPath $settingsPath | Should -BeFalse
        $fragment.statusLine.type | Should -BeExactly 'command'
        $fragment.statusLine.command | Should -Match '^pwsh -NoProfile -File "[A-Z]:/'
        $fragment.statusLine.command | Should -Not -Match '\\'
        $fragment.statusLine.command | Should -Match 'scripts \(safe\) with spaces/claude line\.ps1"$'
    }

    It 'rejects shell expansion characters before changing settings' {
        $unsafeProducer = Join-Path $scenario 'scripts ($parenthesis)\claude line.ps1'
        [IO.Directory]::CreateDirectory((Split-Path $unsafeProducer -Parent)) | Out-Null
        [IO.File]::WriteAllText($unsafeProducer, "'synthetic'", $utf8)
        [IO.Directory]::CreateDirectory((Split-Path $settingsPath -Parent)) | Out-Null
        $original = '{"theme":"dark"}'
        [IO.File]::WriteAllText($settingsPath, $original, $utf8)

        { & $configurator -Apply -SettingsPath $settingsPath -StatusLineScriptPath $unsafeProducer } |
            Should -Throw '*unsupported characters*'
        [IO.File]::ReadAllText($settingsPath, $utf8) | Should -BeExactly $original
        @(Get-ChildItem -LiteralPath (Split-Path $settingsPath -Parent) -Filter 'settings.json.backup-*').Count | Should -Be 0
    }

    It 'merges only statusLine and backs up the prior bytes' {
        [IO.Directory]::CreateDirectory((Split-Path $settingsPath -Parent)) | Out-Null
        $original = [Text.UTF8Encoding]::new($true).GetBytes("{`r`n  `"theme`": `"dark`",`r`n  `"nested`": { `"keep`": true }`r`n}")
        [IO.File]::WriteAllBytes($settingsPath, $original)
        $result = (& $configurator -Apply -SettingsPath $settingsPath -StatusLineScriptPath $dummyProducer) | ConvertFrom-Json
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($result.backup_path)) | Should -BeExactly ([Convert]::ToBase64String($original))
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        $settings.theme | Should -BeExactly 'dark'
        $settings.nested.keep | Should -BeTrue
        $settings.statusLine.command | Should -Match 'claude line\.ps1"$'
    }

    It 'preserves an unrelated ISO offset value as a JSON string through apply and restore' {
        [IO.Directory]::CreateDirectory((Split-Path $settingsPath -Parent)) | Out-Null
        $original = $utf8.GetBytes('{"lastSeen":"2030-06-01T12:34:56+05:30","theme":"light"}')
        [IO.File]::WriteAllBytes($settingsPath, $original)
        $applied = (& $configurator -Apply -SettingsPath $settingsPath -StatusLineScriptPath $dummyProducer) | ConvertFrom-Json
        $settings = [IO.File]::ReadAllText($settingsPath, $utf8) | ConvertFrom-Json -DateKind String
        $settings.lastSeen | Should -BeOfType [string]
        $settings.lastSeen | Should -BeExactly '2030-06-01T12:34:56+05:30'

        & $configurator -Restore -BackupPath $applied.backup_path -SettingsPath $settingsPath | Out-Null
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($settingsPath)) |
            Should -BeExactly ([Convert]::ToBase64String($original))
    }

    It 'refuses an existing statusLine unless replacement is explicit' {
        [IO.Directory]::CreateDirectory((Split-Path $settingsPath -Parent)) | Out-Null
        $original = '{"statusLine":{"type":"command","command":"keep-me"},"theme":"dark"}'
        [IO.File]::WriteAllText($settingsPath, $original, $utf8)
        { & $configurator -Apply -SettingsPath $settingsPath -StatusLineScriptPath $dummyProducer } | Should -Throw '*-ReplaceExisting*'
        [IO.File]::ReadAllText($settingsPath, $utf8) | Should -BeExactly $original
        @(Get-ChildItem -LiteralPath (Split-Path $settingsPath -Parent) -Filter 'settings.json.backup-*').Count | Should -Be 0

        & $configurator -Apply -ReplaceExisting -SettingsPath $settingsPath -StatusLineScriptPath $dummyProducer | Out-Null
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        $settings.theme | Should -BeExactly 'dark'
        $settings.statusLine.command | Should -Not -BeExactly 'keep-me'
    }

    It 'restores the selected generated backup byte for byte and backs up the replaced target' {
        [IO.Directory]::CreateDirectory((Split-Path $settingsPath -Parent)) | Out-Null
        $original = [Text.UTF8Encoding]::new($true).GetBytes("{`r`n  `"theme`": `"before é`"`r`n}")
        [IO.File]::WriteAllBytes($settingsPath, $original)
        $applied = (& $configurator -Apply -SettingsPath $settingsPath -StatusLineScriptPath $dummyProducer) | ConvertFrom-Json
        $replacement = $utf8.GetBytes('{"theme":"after"}')
        [IO.File]::WriteAllBytes($settingsPath, $replacement)
        $restored = (& $configurator -Restore -BackupPath $applied.backup_path -SettingsPath $settingsPath) | ConvertFrom-Json
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($settingsPath)) | Should -BeExactly ([Convert]::ToBase64String($original))
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($restored.replaced_backup)) | Should -BeExactly ([Convert]::ToBase64String($replacement))
    }
}
