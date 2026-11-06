#requires -Version 5.1
param([string]$EvidenceName='final',[string]$TemplatePath)
$ErrorActionPreference='Stop'
if($EvidenceName -notmatch '^[a-z0-9-]+$'){throw 'EvidenceName must contain lowercase letters, digits or hyphens.'}
$root=Split-Path -Parent $PSScriptRoot
$evidence=Join-Path $root 'reports\external-language'
[void][IO.Directory]::CreateDirectory($evidence)
$caseRoot=Join-Path ([IO.Path]::GetTempPath()) ('DellFans-ExternalLanguage-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($caseRoot)
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe=Join-Path $caseRoot 'ExternalLanguageTests.exe'
$log=Join-Path $evidence ($EvidenceName+'.txt')
try {
    @(
        ('Command: tests/run-external-language.ps1 -EvidenceName '+$EvidenceName)
        ('Localization.cs SHA256: '+(Get-FileHash -LiteralPath (Join-Path $root 'src\Localization.cs') -Algorithm SHA256).Hash)
        ('ExternalLanguageTests.cs SHA256: '+(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'ExternalLanguageTests.cs') -Algorithm SHA256).Hash)
    ) | Set-Content -LiteralPath $log -Encoding UTF8
    & $compiler /nologo /platform:x64 /codepage:65001 /langversion:5 /target:exe /main:ExternalLanguageTests `
        ('/out:'+$exe) /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
        (Join-Path $root 'src\Localization.cs') (Join-Path $PSScriptRoot 'ExternalLanguageTests.cs') 2>&1 | Tee-Object -FilePath $log -Append
    if($LASTEXITCODE -ne 0){throw 'External language test compilation failed.'}
    if($TemplatePath){ & $exe ([IO.Path]::GetFullPath($TemplatePath)) 2>&1 | Tee-Object -FilePath $log -Append }
    else { & $exe 2>&1 | Tee-Object -FilePath $log -Append }
    $testExit=$LASTEXITCODE
    ('EXIT '+$testExit) | Tee-Object -FilePath $log -Append
    if($testExit -ne 0){throw 'External language tests failed.'}
} finally {
    $expected=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    $actual=[IO.Path]::GetFullPath($caseRoot)
    if($actual.StartsWith($expected,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($actual).StartsWith('DellFans-ExternalLanguage-')){
        Remove-Item -LiteralPath $actual -Recurse -Force
    }
}
