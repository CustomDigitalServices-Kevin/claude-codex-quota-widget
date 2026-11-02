# Windows release packaging

The Windows setup is a per-user, self-contained Windows 11 `win-x64` package. It installs under
`%LOCALAPPDATA%\Programs\QuotaWidget`, writes only current-user registration, and does not
require the .NET SDK, the .NET runtime, PowerShell 7, Bash or `jq` after installation.

Claude integration is unchecked by default. If selected, the installer calls the bundled
helper, which refuses to replace an existing status line without separate explicit consent.
Uninstall removes the integration only when the helper still recognizes its exact ownership
record. User data under `%LOCALAPPDATA%\QuotaWidget` is preserved.

## Build

Download the official NSIS 3.13 Windows ZIP from the URL below. The build stops unless both
its byte size and SHA-256 match the pinned values.

- URL: `https://downloads.sourceforge.net/project/nsis/NSIS%203/3.13/nsis-3.13.zip`
- Size: `2363925` bytes
- SHA-256: `BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8`
- Canonical metadata: `https://nsis-dev.github.io/release-data/versions.json`

From a clean committed checkout with the .NET 10 SDK:

```powershell
./packaging/build-release.ps1 -NsisZipPath C:\tools\nsis-3.13.zip
```

The release directory contains the unsigned setup, source archive, payload and release
manifests, and `SHA256SUMS.txt`. The NSIS compiler is used only in the private build directory
and is never included in a release asset. The installed payload includes the applicable .NET
and NSIS license notices.

## Isolated lifecycle test

Build the production setup and isolated Claude-off, Claude-on and rollback test installers:

```powershell
./packaging/build-release.ps1 -NsisZipPath C:\tools\nsis-3.13.zip `
  -BuildTestInstallers -TestSuffix ci -SkipSourceArchive
./packaging/test-installer.ps1 `
  -OffSetup ./artifacts/build-1.0.2/test-installers/QuotaWidget-v1.0.2-test-ci-off-setup.exe `
  -OnSetup ./artifacts/build-1.0.2/test-installers/QuotaWidget-v1.0.2-test-ci-on-setup.exe `
  -RollbackSetup ./artifacts/build-1.0.2/test-installers/QuotaWidget-v1.0.2-test-ci-rollback-setup.exe `
  -LegacyPayload C:\build\quota-widget-v1.0.0-framework-dependent `
  -TestSuffix ci
```

The legacy payload must be published from public commit
`fa2fa8867d0469d369672326c2956fe70846a874` with `--self-contained false`. Test builds use a
dedicated registry namespace and synthetic Claude paths. They never read or change a real
Claude configuration. The lifecycle covers real v1.0.0 layout migration, Claude-off isolation,
Claude opt-in backup and restore, locked files, rollback, the native synthetic producer,
self-contained runtime loading, uninstall and data preservation.

The self-contained proof restricts child PATH to Windows system tools, points DOTNET_ROOT and
DOTNET_ROOT_X64 to an absent synthetic path, disables multilevel lookup, and checks that `coreclr.dll` loads
from the installed payload. This is not a clean virtual-machine test. The public installer is
unsigned, so Windows SmartScreen may show a reputation warning. Verify the published SHA-256;
do not disable Windows security features.
