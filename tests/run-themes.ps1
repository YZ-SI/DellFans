#requires -Version 5.1
# Offline: skin validation, shared tokens and the published DIY templates; no window, service or registry access.
param([string]$EvidenceName='latest')
. (Join-Path $PSScriptRoot 'ui-test-common.ps1')
Invoke-UiTest -Main 'ThemeTests' -TestFiles @('ThemeTests.cs') -Arguments @((Split-Path -Parent $PSScriptRoot)) -Pattern '^PASS \d+ theme assertions'
