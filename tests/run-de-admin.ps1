#requires -Version 5.1
# Offline: builds the three executables into a temp folder, inspects their embedded manifests and calls pure static methods by
# reflection. Nothing is launched with a service/hardware argument and no service or pipe is contacted.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$caseRoot=Join-Path ([IO.Path]::GetTempPath()) ('DellFans-DeAdmin-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($caseRoot)
try {
    $bin=Join-Path $caseRoot 'bin'
    & (Join-Path $root 'build.ps1') -OutputDirectory $bin | Out-Null
    $compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $exe=Join-Path $caseRoot 'DeAdminTests.exe'
    & $compiler /nologo /platform:x64 /codepage:65001 /langversion:5 /target:exe /main:DeAdminTests ('/out:'+$exe) /r:System.dll /r:System.Core.dll (Join-Path $PSScriptRoot 'DeAdminTests.cs')
    if($LASTEXITCODE -ne 0){throw 'De-admin test compilation failed.'}
    $psi=New-Object Diagnostics.ProcessStartInfo $exe
    $psi.UseShellExecute=$false; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
    $psi.Arguments='"'+$bin+'"'
    $p=[Diagnostics.Process]::Start($psi)
    $out=$p.StandardOutput.ReadToEnd(); $err=$p.StandardError.ReadToEnd(); $p.WaitForExit()
    Write-Host $out.TrimEnd()
    if($p.ExitCode -ne 0){throw ('De-admin tests failed: '+$err.Trim())}
} finally {
    $expected=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    $actual=[IO.Path]::GetFullPath($caseRoot)
    if($actual.StartsWith($expected,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($actual).StartsWith('DellFans-DeAdmin-')){Remove-Item -LiteralPath $actual -Recurse -Force}
}
