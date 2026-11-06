#requires -Version 5.1
param([string]$ProductDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(-not $ProductDirectory){$ProductDirectory=Join-Path $root 'bin'}
& $compiler /nologo /platform:x64 /codepage:65001 /langversion:5 /target:exe /main:PreviewCommandTests `
    ('/out:'+(Join-Path $ProductDirectory 'PreviewCommandTests.exe')) /r:System.dll /r:System.Core.dll /r:System.Drawing.dll `
    ('/r:'+(Join-Path $ProductDirectory 'DellFans.Cli.exe')) (Join-Path $PSScriptRoot 'PreviewCommandTests.cs')
if($LASTEXITCODE -ne 0){throw 'Offline preview checks did not compile.'}
& (Join-Path $ProductDirectory 'PreviewCommandTests.exe')
if($LASTEXITCODE -ne 0){throw 'Offline preview check failed.'}
