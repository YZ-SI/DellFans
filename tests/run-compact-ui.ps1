#requires -Version 5.1
# Offline: drives the real window against OfflineThermalClient; preferences go to temporary HKCU subkeys, the service is never contacted.
. (Join-Path $PSScriptRoot 'ui-test-common.ps1')
Invoke-UiTest -Main 'CompactUiTests' -TestFiles @('CompactUiTests.cs') -Arguments @((Split-Path -Parent $PSScriptRoot)) -Pattern '^RESULT \d+ checks passed'
