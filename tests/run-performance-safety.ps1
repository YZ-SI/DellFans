#requires -Version 5.1
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$caseRoot=Join-Path ([IO.Path]::GetTempPath()) ('dell-performance-safety-run-'+[Guid]::NewGuid().ToString('N'))
$caseBin=Join-Path $caseRoot 'bin'
[void][IO.Directory]::CreateDirectory($caseBin)
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executable=Join-Path $caseBin 'PerformanceSafetyTests.exe'
$sources=@('ThermalCore.cs','MiniJson.cs','CapabilityProbe.cs','CapabilityHardware.cs','Sensors.cs','SensorDriver.cs','SmmFanControl.cs','ResourceMonitor.cs','FloorControl.cs','ControlPipe.cs','ThermalService.cs') | ForEach-Object {Join-Path $root ('src\'+$_)}
& $compiler /nologo /platform:x64 /codepage:65001 /langversion:5 /target:exe /main:PerformanceSafetyTests `
    ('/out:'+$executable) /r:System.dll /r:System.Core.dll /r:System.Management.dll /r:System.Web.Extensions.dll `
    /r:System.ServiceProcess.dll /r:System.Security.dll  `
    @sources (Join-Path $PSScriptRoot 'PerformanceSafetyTests.cs')
if($LASTEXITCODE -ne 0){throw 'Offline performance test compilation failed.'}
& $executable | Tee-Object -FilePath (Join-Path $caseRoot 'result.txt')
$caseExit=$LASTEXITCODE
Write-Output ('Offline artifacts: '+$caseRoot)
if($caseExit -ne 0){throw ('Offline performance safety tests failed with exit '+$caseExit)}
