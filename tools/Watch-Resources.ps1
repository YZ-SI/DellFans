#requires -Version 5.1
# Read-only soak monitor: every IntervalSeconds, logs private memory, handles, GDI and USER objects of the
# resident processes to CSV. It never opens the IPC pipe or touches the fan, service or scheduled tasks.
param(
    [double]$Hours = 24,
    [int]$IntervalSeconds = 60,
    [string[]]$ProcessNames = @('DellFans', 'DellFans.Service', 'DellFans.Watchdog', 'DellFans.Tray', 'DellFans.Cli'),
    [string]$Output = (Join-Path ([IO.Path]::GetTempPath()) ('DellFans-soak-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.csv'))
)
$ErrorActionPreference = 'Stop'
Add-Type -Namespace Soak -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags);
[DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int id);
[DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
'@
function Get-GuiObjects([int]$id) {
    $handle = [Soak.Native]::OpenProcess(0x1000, $false, $id) # PROCESS_QUERY_LIMITED_INFORMATION
    if ($handle -eq [IntPtr]::Zero) { return @($null, $null) }
    try { return @([Soak.Native]::GetGuiResources($handle, 0), [Soak.Native]::GetGuiResources($handle, 1)) }
    finally { [void][Soak.Native]::CloseHandle($handle) }
}
'TimeUtc,ProcessId,Name,PrivateMiB,WorkingSetMiB,Handles,Gdi,User,CpuSeconds' | Set-Content -LiteralPath $Output -Encoding UTF8
$end = (Get-Date).AddHours($Hours)
Write-Host "Logging to $Output until $end"
while ((Get-Date) -lt $end) {
    $now = (Get-Date).ToUniversalTime().ToString('o')
    foreach ($process in @(Get-Process -Name $ProcessNames -ErrorAction SilentlyContinue)) {
        # A process may exit between listing and reading; skip it instead of ending a 24 h run.
        try {
            $gui = Get-GuiObjects $process.Id
            '{0},{1},{2},{3:0.0},{4:0.0},{5},{6},{7},{8:0.00}' -f $now, $process.Id, $process.Name,
                ($process.PrivateMemorySize64 / 1MB), ($process.WorkingSet64 / 1MB), $process.HandleCount, $gui[0], $gui[1],
                $process.TotalProcessorTime.TotalSeconds | Add-Content -LiteralPath $Output -Encoding UTF8
        } catch { Write-Warning ("Skipped {0} ({1}): {2}" -f $process.Name, $process.Id, $_.Exception.Message) }
    }
    Start-Sleep -Seconds $IntervalSeconds
}
# Summary: first and last sample per process, so growth is visible without a spreadsheet.
Import-Csv -LiteralPath $Output | Group-Object ProcessId | ForEach-Object {
    $first = $_.Group[0]; $last = $_.Group[-1]
    [pscustomobject]@{ Name = $last.Name; Pid = $_.Name; Samples = $_.Count
        PrivateMiB = "$($first.PrivateMiB) -> $($last.PrivateMiB)"; Handles = "$($first.Handles) -> $($last.Handles)"
        Gdi = "$($first.Gdi) -> $($last.Gdi)"; User = "$($first.User) -> $($last.User)" }
} | Format-Table -AutoSize
