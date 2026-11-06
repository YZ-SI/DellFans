# Needs `build.ps1 -TestHooks` (test-hooks tray in dist-test). Runs dellfans-tray against dellfans-fakesvc (private test pipe) and samples memory / GDI / USER objects.
# Usage: tray-measure.ps1 -Deck 0|1 [-Seconds 90] [-Shot path.png]
# Touches only HKCU\Software\DellFans\Ui (removed at the end if it did not exist) and %LOCALAPPDATA%\DellFans\tray.log.
param([int]$Deck = 0, [int]$Seconds = 90, [string]$Shot = '', [string]$Theme = '', [int]$Hover = 0, [string]$Lang = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class G { [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr h, uint f);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowW(string c, string n);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); }
'@
$ui = 'HKCU:\Software\DellFans\Ui'
$existed = Test-Path 'HKCU:\Software\DellFans'
New-Item $ui -Force | Out-Null
Set-ItemProperty $ui DockVisible $Deck -Type DWord
if ($Theme) { Set-ItemProperty $ui Theme $Theme }
if ($Lang) { Set-ItemProperty $ui Language $Lang }
$pipe = "DellFans.Test.$PID"
$env:DELLFANS_PIPE = $pipe
$fakelog = Join-Path $env:TEMP "fake-$PID.log"
$env:DELLFANS_FAKELOG = $fakelog
$svc = Start-Process "$root\target\release\dellfans-fakesvc.exe" -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 500
$tray = Start-Process "$root\dist-test\DellFans.Tray.exe" -PassThru
try {
    Start-Sleep -Seconds 3
    $mem = @(); $gdi = @(); $usr = @()
    $n = [int]($Seconds / 2)
    for ($i = 0; $i -lt $n; $i++) {
        $tray.Refresh()
        $mem += $tray.PrivateMemorySize64
        $gdi += [G]::GetGuiResources($tray.Handle, 0)
        $usr += [G]::GetGuiResources($tray.Handle, 1)
        if ($Shot -and $Hover -and $i -eq 4) {
            Add-Type -AssemblyName System.Windows.Forms
            $scr = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea
            [G]::SetCursorPos($scr.Right - 110, $scr.Top + 48) | Out-Null
            Start-Sleep -Milliseconds 300
            [G]::SetCursorPos($scr.Right - 112, $scr.Top + 50) | Out-Null
            Start-Sleep -Milliseconds 800
        }
        if ($Shot -and $i -eq 5) {
            Add-Type -AssemblyName System.Drawing, System.Windows.Forms
            $ws = Get-Process -Id $tray.Id
            $b = New-Object Drawing.Bitmap 700, 420
            $g = [Drawing.Graphics]::FromImage($b)
            $scr = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea
            $g.CopyFromScreen($scr.Right - 700, $scr.Top, 0, 0, $b.Size)
            $b.Save($Shot, [Drawing.Imaging.ImageFormat]::Png)
            $g.Dispose(); $b.Dispose()
        }
        Start-Sleep -Seconds 2
    }
    $half = $mem[([int]($mem.Count / 2))..($mem.Count - 1)] | Sort-Object
    $med = $half[[int]($half.Count / 2)]
    '{0}: private median(last half)={1:N2} MiB  max={2:N2} MiB  GDI first/last={3}/{4}  USER first/last={5}/{6}  samples={7}' -f `
        $(if ($Deck) { 'deck ON ' } else { 'deck OFF' }), ($med / 1MB), (($mem | Measure-Object -Maximum).Maximum / 1MB), $gdi[0], $gdi[-1], $usr[0], $usr[-1], $mem.Count
    'GDI series: ' + ($gdi -join ',')
    'USER series: ' + ($usr -join ',')
    'fake service saw {0} requests' -f (@(Get-Content $fakelog -ErrorAction SilentlyContinue).Count)
}
finally {
    # graceful: WM_CLOSE to the hidden window removes the tray icon
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class W { [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowW(string c, string n); [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l); }' -ErrorAction SilentlyContinue
    $h = [W]::FindWindowW('DellFans.Tray.Msg', [NullString]::Value)
    if ($h -ne [IntPtr]::Zero) { [W]::PostMessageW($h, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
    $tray.WaitForExit(3000) | Out-Null
    if (-not $tray.HasExited) { Stop-Process -Id $tray.Id -Force }
    Stop-Process -Id $svc.Id -Force -ErrorAction SilentlyContinue
    Remove-Item $fakelog -ErrorAction SilentlyContinue
    if (-not $existed) { Remove-Item 'HKCU:\Software\DellFans' -Recurse -Force }
}
