# Builds the workspace offline and copies the shipping binaries to rust\dist with their product names.
# -TestHooks also builds a tray with the `test-hooks` feature (DELLFANS_PIPE, no identity check) into rust\dist-test; never shipped.
param([switch]$TestHooks)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
cargo build --release --offline
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
New-Item -ItemType Directory -Force dist | Out-Null
Copy-Item target\release\dellfans-tray.exe dist\DellFans.Tray.exe -Force
Copy-Item target\release\dellfans-watchdog.exe dist\DellFans.Watchdog.exe -Force
if ($TestHooks) {
    cargo build --release --offline -p dellfans-tray --features test-hooks --target-dir target-test
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    New-Item -ItemType Directory -Force dist-test | Out-Null
    Copy-Item target-test\release\dellfans-tray.exe dist-test\DellFans.Tray.exe -Force
}
Get-ChildItem dist\*.exe | ForEach-Object { '{0}  {1:N0} bytes' -f $_.Name, $_.Length }
