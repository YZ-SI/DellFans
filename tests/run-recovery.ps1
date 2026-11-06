#requires -Version 5.1
param([string]$EvidenceDirectory,[string]$TestCase)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not $EvidenceDirectory){$EvidenceDirectory=Join-Path ([IO.Path]::GetTempPath()) ('DellFans-RecoveryEvidence-'+[Guid]::NewGuid().ToString('N'))}
[void][IO.Directory]::CreateDirectory($EvidenceDirectory)
$buildRoot=Join-Path ([IO.Path]::GetTempPath()) ('DellFans-RecoveryBuild-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($buildRoot)
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe=Join-Path $buildRoot 'RecoveryTests.exe'
try {
    $output=& $compiler /nologo /platform:x64 /codepage:65001 /langversion:5 /target:exe /main:RecoveryTests ('/out:'+$exe) `
        /r:System.dll /r:System.Core.dll /r:System.Management.dll /r:System.Web.Extensions.dll /r:System.ServiceProcess.dll /r:System.Security.dll `
         `
        (Join-Path $root 'src\ThermalCore.cs') (Join-Path $root 'src\MiniJson.cs') (Join-Path $root 'src\CapabilityProbe.cs') (Join-Path $root 'src\Sensors.cs') (Join-Path $root 'src\SensorDriver.cs') (Join-Path $root 'src\SmmFanControl.cs') `
        (Join-Path $root 'src\ResourceMonitor.cs') (Join-Path $root 'src\FloorControl.cs') `
        (Join-Path $PSScriptRoot 'RecoveryTests.cs') 2>&1
    $compileCode=$LASTEXITCODE
    $output | Tee-Object -FilePath (Join-Path $EvidenceDirectory 'compile.txt')
    if($compileCode -ne 0){throw 'Recovery test compilation failed.'}
    $arguments=@(); if($TestCase){$arguments=@($TestCase)}
    $output=& $exe @arguments 2>&1
    $testCode=$LASTEXITCODE
    $output | Tee-Object -FilePath (Join-Path $EvidenceDirectory 'result.txt')
    if($testCode -ne 0){throw 'Recovery behavior tests failed.'}
} finally {
    $expected=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    $actual=[IO.Path]::GetFullPath($buildRoot)
    if($actual.StartsWith($expected,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($actual).StartsWith('DellFans-RecoveryBuild-')){
        Remove-Item -LiteralPath $actual -Recurse -Force
    }
}
