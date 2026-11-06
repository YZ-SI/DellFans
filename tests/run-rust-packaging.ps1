#requires -Version 5.1
# Offline checks of the release packaging: build.ps1 builds both Rust programs and Package.ps1 puts exactly the programs, their
# configs, IntelMSR.bin, licenses and a read-me into one folder of the zip (no scripts). Writes only to a temp folder.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$checks=0
function Require([bool]$condition,[string]$message){ if(-not $condition){throw "FAIL $message"}; $script:checks++ }
$tray='DellFans.Tray.exe'; $dog='DellFans.Watchdog.exe'
$buildText=Get-Content -LiteralPath (Join-Path $root 'build.ps1') -Raw
Require ($buildText.Contains('-SkipRust') -and $buildText.Contains('--offline') -and $buildText.Contains($tray) -and $buildText.Contains($dog)) 'build.ps1 builds and copies the Rust programs'
$bin=Join-Path $root 'bin'
if(Test-Path -LiteralPath (Join-Path $bin $tray)){
    $out=Join-Path ([IO.Path]::GetTempPath()) ('DellFans-package-'+[Guid]::NewGuid().ToString('N'))
    try {
        $zip=(& (Join-Path $root 'Package.ps1') -OutputDirectory $out).FullName
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive=[IO.Compression.ZipFile]::OpenRead($zip)
        try { $names=@($archive.Entries | ForEach-Object FullName) } finally { $archive.Dispose() }
        $folders=@($names | ForEach-Object { $_.Split('/')[0] } | Select-Object -Unique)
        Require ($folders.Count -eq 1 -and $folders[0] -match '^DellFans-\d+\.\d+\.\d+$') 'the zip holds one DellFans-<version> folder'
        $top=@($names | Where-Object { $_.Split('/').Count -eq 2 } | ForEach-Object { $_.Split('/')[1] } | Sort-Object)
        $expected=@('DellFans.Cli.exe','DellFans.Cli.exe.config','DellFans.exe','DellFans.exe.config','DellFans.Service.exe','DellFans.Service.exe.config',$tray,$dog,'IntelMSR.bin','README.txt','THIRD-PARTY-NOTICES.txt' | Sort-Object)
        Require ((Compare-Object $top $expected) -eq $null) ('top-level files are exactly the programs, configs, module, notices and read-me: '+($top -join ', '))
        Require (@($names | Where-Object { $_ -match '\.(ps1|cmd|bat)$' -or $_ -match 'Tests?\.exe$' }).Count -eq 0) 'no scripts or test programs'
        Require (@($names | Where-Object { $_ -match '/licenses/LICENSE-MIT$' }).Count -eq 1) 'licenses are included'
        Require (Test-Path -LiteralPath ($zip+'.sha256')) 'a .sha256 is written'
    } finally { Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue }
}
Write-Host "PASS release packaging: $checks checks"
