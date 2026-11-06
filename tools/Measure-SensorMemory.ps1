#requires -Version 5.1
# Read-only: runs the in-house sensor layer (ESIF/MSR + storage) as a resident process and reports private memory, poll time,
# acquisition timing and the freshness of the last readings. No SMM, BIOS, fan or service calls; no driver is installed or loaded here.
param([int]$Seconds=120)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$caseRoot=Join-Path ([IO.Path]::GetTempPath()) ('DellFans-SensorProbe-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($caseRoot)
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe=Join-Path $caseRoot 'SensorMemoryProbe.exe'
$sources=@('ThermalCore.cs','MiniJson.cs','CapabilityProbe.cs','Sensors.cs','SensorDriver.cs','SmmFanControl.cs','ResourceMonitor.cs','FloorControl.cs',) | ForEach-Object {Join-Path $root ('src\'+$_)}
& $compiler /nologo /platform:x64 /optimize+ /codepage:65001 /langversion:5 /target:exe /main:SensorMemoryProbe ('/out:'+$exe) `
    /r:System.dll /r:System.Core.dll /r:System.Management.dll /r:System.Web.Extensions.dll /r:System.ServiceProcess.dll /r:System.Security.dll `
    @sources (Join-Path $PSScriptRoot 'SensorMemoryProbe.cs')
if($LASTEXITCODE -ne 0){throw 'Probe compilation failed.'}
Copy-Item -LiteralPath (Join-Path $root 'bin\DellFans.exe.config') -Destination ($exe+'.config')
Copy-Item -LiteralPath (Join-Path $root 'bin\IntelMSR.bin') -Destination $caseRoot
& $exe $Seconds
$code=$LASTEXITCODE
Remove-Item -LiteralPath $caseRoot -Recurse -Force
if($code -ne 0){throw 'Probe failed.'}
