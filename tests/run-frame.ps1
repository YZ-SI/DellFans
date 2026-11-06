#requires -Version 5.1
# Offline: the borderless main window (hit tests, no native frame, minimise / maximise / restore / close, keyboard and accessibility) against OfflineThermalClient.
. (Join-Path $PSScriptRoot 'ui-test-common.ps1')
Invoke-UiTest -Main 'FrameTests' -TestFiles @('CompactUiTests.cs','FrameTests.cs') -Pattern '^RESULT \d+ checks passed'
