#requires -Version 5.1
# Offline: loads bin\DellFans.Service.exe for reflection only; nothing runs, no service or hardware calls.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$caseRoot=Join-Path ([IO.Path]::GetTempPath()) ('DellFans-StartupLoad-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($caseRoot)
try {
    $compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $exe=Join-Path $caseRoot 'StartupLoadTests.exe'
    & $compiler /nologo /platform:x64 /codepage:65001 /langversion:5 /target:exe /main:StartupLoadTests ('/out:'+$exe) /r:System.dll /r:System.Core.dll (Join-Path $PSScriptRoot 'StartupLoadTests.cs')
    if($LASTEXITCODE -ne 0){throw 'Startup load test compilation failed.'}
    & $exe (Join-Path $root 'bin\DellFans.Service.exe')
    if($LASTEXITCODE -ne 0){throw 'Startup load test failed.'}
} finally { Remove-Item -LiteralPath $caseRoot -Recurse -Force }
