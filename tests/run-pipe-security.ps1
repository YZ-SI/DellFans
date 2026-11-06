#requires -Version 5.1
# Offline: pipes use unique DellFans.Test.<guid> names; the real service pipe, SCM state and hardware are never touched.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$caseRoot=Join-Path ([IO.Path]::GetTempPath()) ('DellFans-PipeSecurity-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($caseRoot)
try {
    $compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $exe=Join-Path $caseRoot 'PipeSecurityTests.exe'
    & $compiler /nologo /platform:x64 /codepage:65001 /langversion:5 /target:exe /main:PipeSecurityTests ('/out:'+$exe) `
        /r:System.dll /r:System.Core.dll /r:System.Management.dll /r:System.Web.Extensions.dll /r:System.ServiceProcess.dll `
        (Join-Path $root 'src\ThermalCore.cs') (Join-Path $root 'src\MiniJson.cs') (Join-Path $root 'src\CapabilityProbe.cs') (Join-Path $root 'src\CapabilityHardware.cs') (Join-Path $root 'src\Sensors.cs') (Join-Path $root 'src\SensorDriver.cs') (Join-Path $root 'src\SmmFanControl.cs') (Join-Path $root 'src\ResourceMonitor.cs') `
        (Join-Path $root 'src\FloorControl.cs') (Join-Path $root 'src\ControlPipe.cs') (Join-Path $root 'src\ThermalService.cs') `
        (Join-Path $PSScriptRoot 'PipeSecurityTests.cs')
    if($LASTEXITCODE -ne 0){throw 'Pipe security test compilation failed.'}
    $psi=New-Object Diagnostics.ProcessStartInfo $exe
    $psi.UseShellExecute=$false; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
    $p=[Diagnostics.Process]::Start($psi)
    $out=$p.StandardOutput.ReadToEnd(); $err=$p.StandardError.ReadToEnd(); $p.WaitForExit()
    Write-Host $out.TrimEnd()
    if($p.ExitCode -ne 0){throw ('Pipe security tests failed: '+$err.Trim())}
} finally { Remove-Item -LiteralPath $caseRoot -Recurse -Force }
