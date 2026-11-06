#requires -Version 5.1
param([string]$EvidenceName='final')
$ErrorActionPreference='Stop'
if($EvidenceName -notmatch '^[a-z0-9-]+$'){throw 'EvidenceName must contain lowercase letters, digits or hyphens.'}
$root=Split-Path -Parent $PSScriptRoot
$evidence=Join-Path $root 'reports\compact-a-localization'
[void][IO.Directory]::CreateDirectory($evidence)
$caseRoot=Join-Path ([IO.Path]::GetTempPath()) ('DellFans-Localization-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($caseRoot)
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe=Join-Path $caseRoot 'LocalizationTests.exe'
$log=Join-Path $evidence ($EvidenceName+'.txt')
try {
    @(
        ('Command: tests/run-localization.ps1 -EvidenceName '+$EvidenceName)
        ('Localization.cs SHA256: '+(Get-FileHash -LiteralPath (Join-Path $root 'src\Localization.cs') -Algorithm SHA256).Hash)
        ('LocalizationTests.cs SHA256: '+(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'LocalizationTests.cs') -Algorithm SHA256).Hash)
    ) | Set-Content -LiteralPath $log -Encoding UTF8
    & $compiler /nologo /platform:x64 /codepage:65001 /langversion:5 /target:exe /main:LocalizationTests `
        ('/out:'+$exe) /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
        (Join-Path $root 'src\Localization.cs') (Join-Path $PSScriptRoot 'LocalizationTests.cs') 2>&1 | Tee-Object -FilePath $log -Append
    if($LASTEXITCODE -ne 0){throw 'Localization test compilation failed.'}
    & $exe 2>&1 | Tee-Object -FilePath $log -Append
    $testExit=$LASTEXITCODE
    ('EXIT '+$testExit) | Tee-Object -FilePath $log -Append
    if($testExit -ne 0){throw 'Offline localization tests failed.'}
} finally {
    $expected=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    $actual=[IO.Path]::GetFullPath($caseRoot)
    if($actual.StartsWith($expected,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($actual).StartsWith('DellFans-Localization-')){
        Remove-Item -LiteralPath $actual -Recurse -Force
    }
}
