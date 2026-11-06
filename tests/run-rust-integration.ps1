#requires -Version 5.1
# Offline integration test of the watchdog contract between the C# service side (the real ProcessWatchdogStarter) and the Rust
# DellFans.Watchdog.exe. Builds everything into a temp folder; the parent is `ping -t` and DellFans.Cli.exe is a stub, so no firmware,
# service, pipe or real --recover is involved.
param([switch]$KeepWork)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
# cargo and csc write progress to stderr; under 'Stop' Windows PowerShell 5.1 turns redirected stderr lines into terminating errors.
function Invoke-Native([scriptblock]$command){
    $previous=$ErrorActionPreference; $ErrorActionPreference='Continue'
    try{ & $command 2>&1 | Out-Null } finally { $ErrorActionPreference=$previous }
}
$work=Join-Path ([IO.Path]::GetTempPath()) ('DellFans-RustIntegration-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
try {
    # 1. Build: the C# programs plus the Rust Tray/Watchdog (build.ps1 copies them into the output folder), and the stub CLI.
    $bin=Join-Path $work 'bin'
    & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1') -OutputDirectory $bin | Out-Null
    if($LASTEXITCODE -ne 0){throw 'build.ps1 failed.'}
    foreach($name in @('DellFans.Tray.exe','DellFans.Watchdog.exe')){if(-not(Test-Path -LiteralPath (Join-Path $bin $name))){throw "build.ps1 did not produce $name."}}
    $cargo=(Get-Command cargo -ErrorAction SilentlyContinue|Select-Object -First 1).Source
    if(-not $cargo){$cargo=Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'}
    Invoke-Native { & $cargo build --release --offline --manifest-path (Join-Path $root 'rust\Cargo.toml') -p dellfans-testtools }
    if($LASTEXITCODE -ne 0){throw 'Building the stub CLI failed.'}

    # 2. Stage: harness (real C# sources + test), Rust watchdog and stub CLI in one folder, which is the harness's BaseDirectory.
    $stage=Join-Path $work 'stage'
    [void][IO.Directory]::CreateDirectory($stage)
    Copy-Item -LiteralPath (Join-Path $bin 'DellFans.Watchdog.exe') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $root 'rust\target\release\dellfans-clistub.exe') -Destination (Join-Path $stage 'DellFans.Cli.exe')
    $compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs'|Select-Object -ExpandProperty FullName)
    $output=& $compiler /nologo /platform:x64 /codepage:65001 /langversion:5 /target:exe /main:RustWatchdogIntegration ('/out:'+(Join-Path $stage 'Harness.exe')) `
        /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll /r:System.Web.Extensions.dll /r:System.ServiceProcess.dll /r:System.Security.dll `
        @sources (Join-Path $PSScriptRoot 'RustWatchdogIntegration.cs') 2>&1
    if($LASTEXITCODE -ne 0){$output; throw 'Harness compilation failed.'}

    # 3. Run (about 25 s).
    & (Join-Path $stage 'Harness.exe')
    if($LASTEXITCODE -ne 0){throw 'Rust watchdog integration failed.'}
} finally {
    foreach($p in @(Get-Process DellFans.Watchdog -ErrorAction SilentlyContinue|Where-Object {$_.Path -and $_.Path.StartsWith($work,[StringComparison]::OrdinalIgnoreCase)})){Stop-Process -Id $p.Id -Force}
    if(-not $KeepWork -and $work.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $work).StartsWith('DellFans-RustIntegration-')){
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
