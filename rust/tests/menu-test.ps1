# Opens the custom tray menu (right-click on the deck) against the fake service, screenshots it, exercises keyboard
# and outside-click dismissal, then opens/closes it N times and compares GDI/USER/handle counts.
# Usage: menu-test.ps1 [-Theme black] [-Lang zh-CN] [-Cycles 50] [-Shot path.png]
param([string]$Theme = 'black', [string]$Lang = 'zh-CN', [int]$Cycles = 50, [string]$Shot = '', [int]$DownKeys = 0)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class MT {
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowW(string c, string n);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr h, uint f);
 [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
'@
$ui = 'HKCU:\Software\DellFans\Ui'
$existed = Test-Path 'HKCU:\Software\DellFans'
New-Item $ui -Force | Out-Null
Set-ItemProperty $ui DockVisible 1 -Type DWord
Set-ItemProperty $ui Theme $Theme
Set-ItemProperty $ui Language $Lang
$env:DELLFANS_PIPE = "DellFans.Test.m$PID"
$svc = Start-Process "$root\target\release\dellfans-fakesvc.exe" -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 400
$tray = Start-Process "$root\dist-test\DellFans.Tray.exe" -PassThru
function Snap($tag) { $tray.Refresh(); "{0}: priv={1:N2}MiB handles={2} GDI={3} USER={4}" -f $tag, ($tray.PrivateMemorySize64 / 1MB), $tray.HandleCount, [MT]::GetGuiResources($tray.Handle, 0), [MT]::GetGuiResources($tray.Handle, 1) }
function RightClick($x, $y) {
    [MT]::SetCursorPos($x, $y) | Out-Null; Start-Sleep -Milliseconds 120
    [MT]::SetCursorPos($x + 1, $y) | Out-Null; Start-Sleep -Milliseconds 120
    [MT]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 40; [MT]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero)
}
function Key([byte[]]$vks) { foreach ($v in $vks) { [MT]::keybd_event($v, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 30; [MT]::keybd_event($v, 0, 2, [UIntPtr]::Zero); Start-Sleep -Milliseconds 80 } }
function MenuOpen() { [MT]::FindWindowW('DellFans.Menu', [NullString]::Value) -ne [IntPtr]::Zero }
try {
    Start-Sleep 3
    $scr = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $dx = $scr.Right - 147; $dy = $scr.Top + 60   # capsule centre at default placement
    Snap 'baseline'
    $t0 = Get-Date
    RightClick $dx $dy
    $found = $false
    while (((Get-Date) - $t0).TotalSeconds -lt 3 -and -not $found) { Start-Sleep -Milliseconds 50; $found = MenuOpen }
    "menu open after right-click: $found (after {0:N0} ms)" -f ((Get-Date) - $t0).TotalMilliseconds
    if ($DownKeys -gt 0) { Key (@(0x28) * $DownKeys); Start-Sleep -Milliseconds 300 }
    if ($Shot) {
        $b = New-Object Drawing.Bitmap 520, 560
        $g = [Drawing.Graphics]::FromImage($b)
        $g.CopyFromScreen($dx - 260, [Math]::Max(0, $dy - 40), 0, 0, $b.Size)   # menu opens below the cursor here (no room above)
        $b.Save($Shot, [Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $b.Dispose()
    }
    Key 0x1B; Start-Sleep -Milliseconds 300
    "closed by Esc: $(-not (MenuOpen))"
    RightClick $dx $dy; Start-Sleep -Milliseconds 400
    [MT]::SetCursorPos(20, 600) | Out-Null; Start-Sleep -Milliseconds 100
    [MT]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [MT]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 300
    "closed by outside click: $(-not (MenuOpen))"
    # keyboard activation: Down x2 + Enter selects "balanced" (row 2 of the modes group) -> SetMode on the fake service
    RightClick $dx $dy; Start-Sleep -Milliseconds 400
    Key 0x28, 0x28, 0x0D; Start-Sleep -Milliseconds 600
    "closed by Enter: $(-not (MenuOpen))"
    Snap 'before cycles'
    $fail = 0
    for ($i = 1; $i -le $Cycles; $i++) {
        RightClick $dx $dy; Start-Sleep -Milliseconds 250
        if (-not (MenuOpen)) { $fail++ }
        Key 0x1B; Start-Sleep -Milliseconds 120
        if ($i -in 1, 10, 25, 50) { Snap "after $i open/close" }
    }
    "cycles=$Cycles failures-to-open=$fail"
}
finally {
    $h = [MT]::FindWindowW('DellFans.Tray.Msg', [NullString]::Value)
    if ($h -ne [IntPtr]::Zero) { [MT]::PostMessageW($h, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
    $tray.WaitForExit(3000) | Out-Null
    if (-not $tray.HasExited) { Stop-Process -Id $tray.Id -Force }
    Stop-Process -Id $svc.Id -Force -ErrorAction SilentlyContinue
    if (-not $existed) { Remove-Item 'HKCU:\Software\DellFans' -Recurse -Force }
}
