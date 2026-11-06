#requires -Version 5.1
# Offline: opens every dialog headlessly (no live pipe, no real Explorer); preferences go to temporary HKCU subkeys.
. (Join-Path $PSScriptRoot 'ui-test-common.ps1')
Invoke-UiTest -Main 'DialogTests' -TestFiles @('CompactUiTests.cs','DialogTests.cs') -Arguments @((Split-Path -Parent $PSScriptRoot)) -Pattern '^RESULT \d+ checks passed'
