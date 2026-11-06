#requires -Version 5.1
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$bin=Join-Path $root 'bin'
& $compiler /nologo /platform:x64 /codepage:65001 /langversion:5 /target:exe /main:CoreTests `
    ('/out:'+(Join-Path $bin 'CoreTests.exe')) /r:System.dll /r:System.Core.dll /r:System.Management.dll /r:System.Web.Extensions.dll `
    /r:System.ServiceProcess.dll /r:System.Security.dll `
    (Join-Path $root 'src\ThermalCore.cs') (Join-Path $root 'src\MiniJson.cs') (Join-Path $root 'src\CapabilityProbe.cs') (Join-Path $root 'src\Sensors.cs') (Join-Path $root 'src\SensorDriver.cs') (Join-Path $root 'src\SmmFanControl.cs') (Join-Path $root 'src\ResourceMonitor.cs') (Join-Path $root 'src\FloorControl.cs') (Join-Path $PSScriptRoot 'CoreTests.cs')
if($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& (Join-Path $bin 'CoreTests.exe')
if($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
