#requires -Version 5.1
# Offline sensor-layer tests: byte-fixture decoders, fake MSR/ESIF/storage sources, fake clock, installer gates. Touches no hardware.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$caseRoot=Join-Path ([IO.Path]::GetTempPath()) ('dell-sensor-tests-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($caseRoot)
$exe=Join-Path $caseRoot 'SensorTests.exe'
Push-Location $root
try {
    & $compiler /nologo /platform:x64 /codepage:65001 /langversion:5 /target:exe /main:SensorTests ('/out:'+$exe) `
        /r:System.dll /r:System.Core.dll /r:System.Management.dll /r:System.Web.Extensions.dll /r:System.ServiceProcess.dll /r:System.Security.dll `
         `
        (Join-Path $root 'src\ThermalCore.cs') (Join-Path $root 'src\MiniJson.cs') (Join-Path $root 'src\CapabilityProbe.cs') (Join-Path $root 'src\Sensors.cs') (Join-Path $root 'src\SensorDriver.cs') (Join-Path $root 'src\SmmFanControl.cs') `
        (Join-Path $root 'src\ResourceMonitor.cs') (Join-Path $root 'src\FloorControl.cs') (Join-Path $PSScriptRoot 'SensorTests.cs')
    if($LASTEXITCODE -ne 0){throw 'Sensor test compilation failed.'}
    & $exe
    if($LASTEXITCODE -ne 0){throw 'Sensor tests failed.'}
} finally { Pop-Location; if(Test-Path -LiteralPath $caseRoot){Remove-Item -LiteralPath $caseRoot -Recurse -Force} }
