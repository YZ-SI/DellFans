#requires -Version 5.1
# Dev tool: saves PNGs of the real main window (offline client only; the service pipe is never opened) per skin and page.
param([string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\window-captures'))
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'tests\ui-test-common.ps1')
Invoke-UiTest -Main 'CaptureWindow' -TestFiles @('CompactUiTests.cs','CaptureWindow.cs') -Arguments @($OutputDirectory) -Pattern '^Captured to'
