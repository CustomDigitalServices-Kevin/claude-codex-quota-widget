#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; RequiredVersion = '5.7.1' }

Describe 'Installation et désinstallation de Quotas IA' {
    BeforeAll {
        $repository = Split-Path $PSScriptRoot -Parent
        $installer = Join-Path $repository 'install.ps1'
        $uninstaller = Join-Path $repository 'uninstall.ps1'
        $utf8 = [Text.UTF8Encoding]::new($false)
    }

    BeforeEach {
        $scenario = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $source = Join-Path $scenario 'published'
        $installed = Join-Path $scenario 'installation with spaces'
        $data = Join-Path $scenario 'data'
        $target = Join-Path $scenario 'statusline.sh'
        $original = Join-Path $scenario 'original.sh'
        $runKey = 'HKCU:\Software\QuotaWidgetTests\' + [guid]::NewGuid().ToString('N')
        $quitName = 'Local\QuotaWidgetTests.' + [guid]::NewGuid().ToString('N') + '.Quit'
        [IO.Directory]::CreateDirectory($source) | Out-Null
        # These are explicitly dummy published files. They are never executed or published.
        [IO.File]::WriteAllText((Join-Path $source 'QuotaWidget.exe'), 'dummy app for copy assertions', $utf8)
        [IO.File]::WriteAllText((Join-Path $source 'quotas.exe'), 'dummy CLI for copy assertions', $utf8)
        [IO.File]::WriteAllText((Join-Path $source 'QuotaWidget.Core.dll'), 'dummy library', $utf8)
        $oldText = "#!/bin/bash`nprintf 'avant é'`n# >>> quota-widget old`nprintf 'ancien bloc'`n# <<< quota-widget`nprintf 'après'`n"
        [IO.File]::WriteAllText($target, $oldText, $utf8)
        [IO.File]::WriteAllText($original, "#!/bin/bash`nprintf 'original é'`n", $utf8)
        $installArgs = @{
            SourceDirectory = $source; InstallDirectory = $installed; DataDirectory = $data
            RunKeyPath = $runKey; StatusLinePath = $target; NoStart = $true; QuitEventName = $quitName
            TemporaryDirectory = $scenario
        }
        $uninstallArgs = @{
            InstallDirectory = $installed; DataDirectory = $data; RunKeyPath = $runKey
            StatusLinePath = $target; QuitEventName = $quitName
        }
        Mock dotnet {
            $global:LASTEXITCODE = 0
            'Microsoft.WindowsDesktop.App 10.0.0 [constructed test runtime]'
        }
        Mock Get-Process { @() } -ParameterFilter { $Name -eq 'QuotaWidget' }
        Mock Start-Process { throw 'The tests must never launch the application.' }
        Mock Stop-Process { throw 'The tests must never terminate a real process.' }
    }

    AfterEach {
        try {
            Should -Invoke Start-Process -Times 0
            Should -Invoke Stop-Process -Times 0
        }
        finally {
            # Only the GUID test subkey is removed, even after a failed assertion.
            if (Test-Path -LiteralPath $runKey) { Remove-Item -LiteralPath $runKey -Recurse -Force }
        }
    }

    It 'copie les deux binaires et écrit le chemin Run entier entre guillemets' {
        & $installer @installArgs
        [IO.File]::ReadAllText((Join-Path $installed 'QuotaWidget.exe')) | Should -Be 'dummy app for copy assertions'
        Test-Path -LiteralPath (Join-Path $installed 'quotas.exe') | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $installed 'QuotaWidget.Core.dll') | Should -BeTrue
        (Get-ItemProperty -LiteralPath $runKey).QuotaWidget | Should -Be ('"' + (Join-Path $installed 'QuotaWidget.exe') + '"')
        Should -Invoke dotnet -Times 1
    }

    It 'remplace le seul bloc, sauvegarde, conserve LF et ne produit aucun BOM' {
        & $installer @installArgs -StatusLine
        $text = [IO.File]::ReadAllText($target)
        $text | Should -Match 'render_codex_quotas'
        $text | Should -Not -Match 'ancien bloc'
        $text | Should -Match "printf 'avant é'"
        $text | Should -Match "printf 'après'"
        $backups = @(Get-ChildItem -LiteralPath $scenario -Filter 'statusline.sh.bak-*')
        $backups.Count | Should -Be 1
        [IO.File]::ReadAllText($backups[0].FullName) | Should -BeExactly $oldText
        $bytes = [IO.File]::ReadAllBytes($target)
        ($bytes -contains 13) | Should -BeFalse
        [BitConverter]::ToString($bytes[0..2]) | Should -Not -Be 'EF-BB-BF'
        @([IO.Directory]::GetFiles($scenario, '*.tmp')).Count | Should -Be 0
    }

    It 'refuse une cible sans marqueurs avant toute installation' {
        [IO.File]::WriteAllText($target, "printf 'intact'`n", $utf8)
        { & $installer @installArgs -StatusLine } | Should -Throw '*marqueurs*'
        [IO.File]::ReadAllText($target) | Should -BeExactly "printf 'intact'`n"
        Test-Path -LiteralPath $installed | Should -BeFalse
        Test-Path -LiteralPath $runKey | Should -BeFalse
    }

    It 'refuse une sauvegarde originale contenant un marqueur' {
        [IO.File]::WriteAllText($original, $oldText, $utf8)
        { & $installer @installArgs -StatusLine -OriginalBackup $original } | Should -Throw '*aucun marqueur*'
    }

    It 'préserve les champs originaux lors de la réinstallation' {
        & $installer @installArgs -StatusLine -OriginalBackup $original
        $statePath = Join-Path $data 'install-state.json'
        $first = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        $another = Join-Path $scenario 'another.sh'
        [IO.File]::WriteAllText($another, "printf 'autre'`n", $utf8)
        & $installer @installArgs -StatusLine -OriginalBackup $another
        $second = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        $second.original_backup | Should -BeExactly $first.original_backup
        $second.original_sha256 | Should -BeExactly $first.original_sha256
        $second.statusline_sha256 | Should -Be (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        $second.installed_at | Should -Not -BeNullOrEmpty
    }

    It 'ne suppose pas que la sauvegarde du bloc est une sauvegarde originale' {
        & $installer @installArgs -StatusLine
        $state = Get-Content -LiteralPath (Join-Path $data 'install-state.json') -Raw | ConvertFrom-Json
        $state.original_backup | Should -BeNullOrEmpty
        $state.original_sha256 | Should -BeNullOrEmpty
    }

    It 'restaure les octets originaux si les deux empreintes sont inchangées' {
        # A BOM and CRLF in the original prove that full restoration is byte-for-byte.
        [IO.File]::WriteAllText($original, "#!/bin/bash`r`nprintf 'original é'`r`n", [Text.UTF8Encoding]::new($true))
        $expected = [Convert]::ToBase64String([IO.File]::ReadAllBytes($original))
        & $installer @installArgs -StatusLine -OriginalBackup $original
        & $uninstaller @uninstallArgs
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($target)) | Should -BeExactly $expected
        Test-Path -LiteralPath $installed | Should -BeFalse
        (Get-ItemProperty -LiteralPath $runKey).PSObject.Properties.Name | Should -Not -Contain 'QuotaWidget'
    }

    It 'retire le bloc seul si la cible a été modifiée' {
        & $installer @installArgs -StatusLine -OriginalBackup $original
        [IO.File]::AppendAllText($target, "printf 'modification à garder'`n", $utf8)
        & $uninstaller @uninstallArgs
        $text = [IO.File]::ReadAllText($target)
        $text | Should -Not -Match 'quota-widget'
        $text | Should -Match 'modification à garder'
        $text | Should -Match "printf 'avant é'"
        $text | Should -Match "printf 'après'"
        $text | Should -Not -Match "printf 'original é'"
    }

    It 'retire le bloc seul si la sauvegarde originale a été modifiée' {
        & $installer @installArgs -StatusLine -OriginalBackup $original
        [IO.File]::AppendAllText($original, 'changed backup', $utf8)
        & $uninstaller @uninstallArgs
        [IO.File]::ReadAllText($target) | Should -Not -Match 'quota-widget|changed backup'
        [IO.File]::ReadAllText($target) | Should -Match "printf 'avant é'"
    }

    It 'ne modifie pas une barre sans bloc' {
        & $installer @installArgs
        [IO.File]::WriteAllText($target, "printf 'intact'`n", $utf8)
        & $uninstaller @uninstallArgs
        [IO.File]::ReadAllText($target) | Should -BeExactly "printf 'intact'`n"
        @(Get-ChildItem -LiteralPath $scenario -Filter 'statusline.sh.bak-*').Count | Should -Be 0
    }

    It 'épargne codex-home avec RemoveData' {
        & $installer @installArgs
        $profile = Join-Path $data 'codex-home'
        [IO.Directory]::CreateDirectory($profile) | Out-Null
        [IO.File]::WriteAllText((Join-Path $profile 'test-profile.txt'), 'dummy dedicated profile', $utf8)
        [IO.File]::WriteAllText((Join-Path $data 'settings.json'), '{}', $utf8)
        & $uninstaller @uninstallArgs -RemoveData
        @(Get-ChildItem -LiteralPath $data).Name | Should -Be 'codex-home'
        [IO.File]::ReadAllText((Join-Path $profile 'test-profile.txt')) | Should -Be 'dummy dedicated profile'
    }

    It 'supprime codex-home seulement avec RemoveCodexProfile' {
        & $installer @installArgs
        [IO.Directory]::CreateDirectory((Join-Path $data 'codex-home')) | Out-Null
        & $uninstaller @uninstallArgs -RemoveCodexProfile
        Test-Path -LiteralPath (Join-Path $data 'codex-home') | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $data 'install-state.json') | Should -BeTrue
    }

    It 'refuse un runtime Desktop autre que 10' {
        Mock dotnet { $global:LASTEXITCODE = 0; 'Microsoft.WindowsDesktop.App 9.0.1 [constructed test runtime]' }
        { & $installer @installArgs } | Should -Throw '*Desktop 10*'
        Test-Path -LiteralPath $installed | Should -BeFalse
    }
}
