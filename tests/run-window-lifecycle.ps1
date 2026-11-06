#requires -Version 5.1
# Offline: 20 open/close cycles of the real window against OfflineThermalClient; checks GDI/USER/handle growth and prints private memory.
. (Join-Path $PSScriptRoot 'ui-test-common.ps1')
Invoke-UiTest -Main 'ResourceHygieneTests' -TestFiles @('CompactUiTests.cs','ResourceHygieneTests.cs') -Pattern '^RESULT \d+ checks passed'
