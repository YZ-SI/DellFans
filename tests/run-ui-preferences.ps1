#requires -Version 5.1
# Offline: preferences against temporary HKCU subkeys (injected into UiPreferences); the real Software\DellFans and Run keys are never touched.
param([string]$EvidenceName='final')
. (Join-Path $PSScriptRoot 'ui-test-common.ps1')
Invoke-UiTest -Main 'UiPreferenceTests' -TestFiles @('UiPreferenceTests.cs') -Pattern '^RESULT \d+ checks passed'
