#requires -Version 5.1
param([string]$OutputDirectory,[switch]$SkipRust)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$bin = if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $root 'bin'}
[void][IO.Directory]::CreateDirectory($bin)
# No third-party managed libraries: sensors are read by src\Sensors.cs. IntelMSR.bin is the one shipped third-party file (LGPL-2.1+, see licenses\).
$module = Join-Path $root 'third-party\PawnIO.Modules\IntelMSR.bin'
$moduleHash = 'ED41A0D0DE082F4668A9CACCB9681EBBAD97A8ED3452C41918DDA1EB454B77A9'
if ((Get-FileHash -LiteralPath $module -Algorithm SHA256).Hash -ne $moduleHash) { throw 'IntelMSR.bin does not match its pinned SHA-256.' }
Copy-Item -LiteralPath $module -Destination (Join-Path $bin 'IntelMSR.bin') -Force
[void][IO.Directory]::CreateDirectory((Join-Path $bin 'licenses'))
Copy-Item -Path (Join-Path $root 'licenses\*') -Destination (Join-Path $bin 'licenses') -Recurse -Force
foreach($name in @('LICENSE-MIT','LICENSE-APACHE')){Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $bin 'licenses') -Force}
Copy-Item -LiteralPath (Join-Path $root 'licenses\THIRD-PARTY-NOTICES.txt') -Destination $bin -Force
# Remove files left by builds that bundled LibreHardwareMonitor.
foreach($stale in @('LibreHardwareMonitorLib.dll','RAMSPDToolkit-NDD.dll','DiskInfoToolkit.dll','HidSharp.dll','BlackSharp.Core.dll','System.Memory.dll','System.Buffers.dll',
    'System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll','LibreHardwareMonitor-LICENSE.txt')) {
    $path = Join-Path $bin $stale
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$common = @('/nologo','/platform:x64','/optimize+','/codepage:65001','/langversion:5',
    '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',
    '/r:System.Management.dll','/r:System.Web.Extensions.dll','/r:System.ServiceProcess.dll','/r:System.Security.dll')
$adminManifest = '/win32manifest:' + (Join-Path $root 'src\app.manifest')
$icon = '/win32icon:' + (Join-Path $root 'assets\icon\dellfans.ico')
# The interface runs as the invoking user; only the service and the CLI need administrator rights.
& $compiler @common $icon ('/win32manifest:' + (Join-Path $root 'src\app.gui.manifest')) /target:winexe ('/out:' + (Join-Path $bin 'DellFans.exe')) @sources
if ($LASTEXITCODE -ne 0) { throw 'GUI compilation failed.' }
& $compiler @common $icon $adminManifest /target:exe ('/out:' + (Join-Path $bin 'DellFans.Cli.exe')) @sources
if ($LASTEXITCODE -ne 0) { throw 'CLI compilation failed.' }
& $compiler @common $icon $adminManifest /target:exe ('/out:' + (Join-Path $bin 'DellFans.Service.exe')) @sources
if ($LASTEXITCODE -ne 0) { throw 'Service compilation failed.' }
$config = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup>
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />
  </startup>
</configuration>
'@
foreach ($name in @('DellFans.exe','DellFans.Cli.exe','DellFans.Service.exe')) {
    [IO.File]::WriteAllText((Join-Path $bin ($name + '.config')), $config, (New-Object Text.UTF8Encoding($false)))
}
# DellFans.Tray.exe and DellFans.Watchdog.exe come from the Rust workspace (release profile, no test hooks).
if(-not $SkipRust){
    $cargo=(Get-Command cargo -ErrorAction SilentlyContinue|Select-Object -First 1).Source
    if(-not $cargo -and $env:USERPROFILE){$fallback=Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'; if(Test-Path -LiteralPath $fallback){$cargo=$fallback}}
    if(-not $cargo){throw 'cargo was not found, so DellFans.Tray.exe and DellFans.Watchdog.exe cannot be built. Install Rust (rustup) or pass -SkipRust to build the C# programs only.'}
    $manifestPath=Join-Path $root 'rust\Cargo.toml'
    # cargo reports progress on stderr; merging it here keeps callers that capture stderr (tests\run-all.ps1) from treating it as an error.
    function Invoke-Cargo([string[]]$cargoArguments){
        $previous=$ErrorActionPreference; $ErrorActionPreference='Continue'
        try{& $cargo build --release --manifest-path $manifestPath @cargoArguments 2>&1|ForEach-Object{Write-Host "$_"}; return $LASTEXITCODE}
        finally{$ErrorActionPreference=$previous}
    }
    if((Invoke-Cargo @('--offline')) -ne 0 -and (Invoke-Cargo @()) -ne 0){throw 'Rust build failed.'}
    $rustOut=Join-Path $root 'rust\target\release'
    Copy-Item -LiteralPath (Join-Path $rustOut 'dellfans-tray.exe') -Destination (Join-Path $bin 'DellFans.Tray.exe') -Force
    Copy-Item -LiteralPath (Join-Path $rustOut 'dellfans-watchdog.exe') -Destination (Join-Path $bin 'DellFans.Watchdog.exe') -Force
}
Write-Host "Built: $bin\DellFans.exe and DellFans.Cli.exe$(if(-not $SkipRust){', DellFans.Tray.exe and DellFans.Watchdog.exe'})"
