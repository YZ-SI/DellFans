#requires -Version 5.1
# Offline: capability probe with fake hardware and temp folders only; the write step runs against a fake.
# Never contacts the service, the SCM, ProgramData or the hardware.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$caseRoot=Join-Path ([IO.Path]::GetTempPath()) ('DellFans-CapabilityTests-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($caseRoot)
try {
    $compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $exe=Join-Path $caseRoot 'CapabilityTests.exe'
    $sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
    & $compiler /nologo /platform:x64 /codepage:65001 /langversion:5 /target:exe /main:CapabilityTests ('/out:'+$exe) `
        /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll /r:System.Web.Extensions.dll /r:System.ServiceProcess.dll /r:System.Security.dll `
        @sources (Join-Path $PSScriptRoot 'CapabilityTests.cs')
    if($LASTEXITCODE -ne 0){throw 'Capability test compilation failed.'}
    $psi=New-Object Diagnostics.ProcessStartInfo $exe
    $psi.UseShellExecute=$false; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
    $p=[Diagnostics.Process]::Start($psi)
    $out=$p.StandardOutput.ReadToEnd(); $err=$p.StandardError.ReadToEnd(); $p.WaitForExit()
    Write-Host $out.TrimEnd()
    if($p.ExitCode -ne 0){throw ('Capability tests failed: '+$err.Trim())}
} finally { Remove-Item -LiteralPath $caseRoot -Recurse -Force }
