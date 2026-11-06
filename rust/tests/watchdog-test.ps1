# Harness for DellFans.Watchdog.exe using a harmless parent (ping -t) and a stub DellFans.Cli.exe.
# Run after build.ps1. Touches only a temp work dir. No service, no hardware.
param([string]$Work = (Join-Path $env:TEMP 'dellfans-wd-test'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $Work | Out-Null
Copy-Item "$root\dist\DellFans.Watchdog.exe" "$Work\DellFans.Watchdog.exe"
Copy-Item "$root\target\release\dellfans-clistub.exe" "$Work\DellFans.Cli.exe"
$log = "$Work\clistub.log"
$results = @()

function New-Session($name, $parent) {
    $p = "$Work\fan-watchdog-$name.json"
    $rec = @{ ParentId = $parent.Id; ParentStartTicks = $parent.StartTime.ToUniversalTime().Ticks; Model = 'TestModel'
              Bios = '0.0'; RestoreOverride = 'Enabled'; ServiceOwned = $true } | ConvertTo-Json
    Set-Content $p $rec
    Set-Content "$p.armed" 'armed'
    Set-Content "$p.heartbeat" 'alive'
    $p
}
function Start-Wd($session) {
    $wd = Start-Process "$Work\DellFans.Watchdog.exe" -ArgumentList "`"$session`"" -PassThru -WindowStyle Hidden
    $t = Get-Date
    while (-not (Test-Path "$session.ready") -and ((Get-Date) - $t).TotalSeconds -lt 5) { Start-Sleep -Milliseconds 50 }
    $wd
}
function Calls() { if (Test-Path $log) { @(Get-Content $log) } else { @() } }
function Reset-Log() { Remove-Item $log -ErrorAction SilentlyContinue }
function Add-Result($n, $ok, $d) { $script:results += [pscustomobject]@{ Test = $n; Pass = $ok; Detail = $d }; "{0}: {1} ({2})" -f $n, $(if ($ok) { 'PASS' } else { 'FAIL' }), $d }

# A: heartbeat touched 20 s then stops, parent stays alive -> recovery 8..10 s after the last touch
Reset-Log
$ping = Start-Process ping -ArgumentList '-t 127.0.0.1' -PassThru -WindowStyle Hidden
$s = New-Session 'a' $ping
$wd = Start-Wd $s
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 20) { (Get-Item "$s.heartbeat").LastWriteTimeUtc = [DateTime]::UtcNow; $lastTouch = [DateTimeOffset]::UtcNow; Start-Sleep -Milliseconds 500 }
$n0 = (Calls).Count
$sw.Restart()
while ((Calls).Count -eq 0 -and $sw.Elapsed.TotalSeconds -lt 20) { Start-Sleep -Milliseconds 100 }
$c = @(Calls)
$lag = if ($c.Count) { (([int64]$c[0].Split("`t")[0]) - $lastTouch.ToUnixTimeMilliseconds()) / 1000.0 } else { 99 }
Add-Result 'A stale heartbeat triggers recovery' ($c.Count -ge 1 -and $lag -ge 7.5 -and $lag -le 10.5 -and $c[0] -match '--recover' -and $c[0] -match 'fan-watchdog-a\.json') ("after {0:N1}s since last touch; call: {1}" -f $lag, ($c -join ' | '))
$wd.WaitForExit(5000) | Out-Null
Add-Result 'A watchdog exits 0 after stub success' ($wd.HasExited -and $wd.ExitCode -eq 0) "exited=$($wd.HasExited) code=$($wd.ExitCode)"
Stop-Process -Id $ping.Id -Force -ErrorAction SilentlyContinue

# B: parent killed while heartbeat is fresh -> recovery within ~1 s
Reset-Log
$ping = Start-Process ping -ArgumentList '-t 127.0.0.1' -PassThru -WindowStyle Hidden
$s = New-Session 'b' $ping
$wd = Start-Wd $s
Start-Sleep -Seconds 2
(Get-Item "$s.heartbeat").LastWriteTimeUtc = [DateTime]::UtcNow
Stop-Process -Id $ping.Id -Force
$sw = [Diagnostics.Stopwatch]::StartNew()
while ((Calls).Count -eq 0 -and $sw.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 50 }
Add-Result 'B parent kill triggers recovery' ((Calls).Count -ge 1 -and $sw.Elapsed.TotalSeconds -le 1.6) ("after {0:N2}s" -f $sw.Elapsed.TotalSeconds)
$wd.WaitForExit(5000) | Out-Null

# C: .armed removed -> clean exit, no recovery
Reset-Log
$ping = Start-Process ping -ArgumentList '-t 127.0.0.1' -PassThru -WindowStyle Hidden
$s = New-Session 'c' $ping
$wd = Start-Wd $s
Start-Sleep -Seconds 2
Remove-Item "$s.armed"
$sw = [Diagnostics.Stopwatch]::StartNew()
$wd.WaitForExit(5000) | Out-Null
Add-Result 'C disarm exits cleanly' ($wd.HasExited -and $wd.ExitCode -eq 0 -and (Calls).Count -eq 0) ("exit after {0:N2}s code={1} calls={2}" -f $sw.Elapsed.TotalSeconds, $wd.ExitCode, (Calls).Count)
Stop-Process -Id $ping.Id -Force -ErrorAction SilentlyContinue

# D: failing recovery retries with backoff 1,2,4,8 s (5 tries) then exits 1
Reset-Log
$env:CLISTUB_EXIT = '1'
$ping = Start-Process ping -ArgumentList '-t 127.0.0.1' -PassThru -WindowStyle Hidden
$s = New-Session 'd' $ping
$wd = Start-Wd $s
Stop-Process -Id $ping.Id -Force
$sw = [Diagnostics.Stopwatch]::StartNew()
$wd.WaitForExit(30000) | Out-Null
$stamps = @(Calls | ForEach-Object { [int64]$_.Split("`t")[0] })
$gaps = for ($i = 1; $i -lt $stamps.Count; $i++) { [math]::Round(($stamps[$i] - $stamps[$i - 1]) / 1000, 1) }
Add-Result 'D retry/backoff' ($stamps.Count -eq 5 -and $wd.ExitCode -eq 1 -and ($gaps -join ',') -match '^(1\.\d|1),(2\.\d|2),(4\.\d|4),(8\.\d|8)$') ("calls=$($stamps.Count) gaps(s)=$($gaps -join ',') exit=$($wd.ExitCode) total={0:N1}s" -f $sw.Elapsed.TotalSeconds)
Remove-Item Env:CLISTUB_EXIT

# E: .recovered present during backoff stops retries
Reset-Log
$env:CLISTUB_EXIT = '1'
$ping = Start-Process ping -ArgumentList '-t 127.0.0.1' -PassThru -WindowStyle Hidden
$s = New-Session 'e' $ping
$wd = Start-Wd $s
Stop-Process -Id $ping.Id -Force
Start-Sleep -Milliseconds 700
Set-Content "$s.recovered" '{"AutomaticRestored":true,"Error":null}'
$wd.WaitForExit(10000) | Out-Null
Add-Result 'E .recovered ends retries' ($wd.HasExited -and $wd.ExitCode -eq 0 -and (Calls).Count -eq 1) "exit=$($wd.ExitCode) calls=$((Calls).Count)"
Remove-Item Env:CLISTUB_EXIT

# F: PID reuse guard (wrong start ticks) counts as dead parent; invalid name is rejected
Reset-Log
$ping = Start-Process ping -ArgumentList '-t 127.0.0.1' -PassThru -WindowStyle Hidden
$s = New-Session 'f' $ping
$j = Get-Content $s -Raw | ConvertFrom-Json; $j.ParentStartTicks = [int64]$j.ParentStartTicks + 10000000; $j | ConvertTo-Json | Set-Content $s
$wd = Start-Wd $s
$wd.WaitForExit(5000) | Out-Null
Add-Result 'F start-time mismatch recovers' ((Calls).Count -ge 1) "calls=$((Calls).Count)"
Stop-Process -Id $ping.Id -Force -ErrorAction SilentlyContinue
$bad = Start-Process "$Work\DellFans.Watchdog.exe" -ArgumentList "`"$Work\other.json`"" -PassThru -WindowStyle Hidden
$bad.WaitForExit(3000) | Out-Null
Add-Result 'F bad session name rejected' ($bad.ExitCode -eq 2) "exit=$($bad.ExitCode)"

# G: --recover exit 2 (path refused) stops at once with an error log and exit code 2
Reset-Log
$env:CLISTUB_EXIT = '2'
$ping = Start-Process ping -ArgumentList '-t 127.0.0.1' -PassThru -WindowStyle Hidden
$s = New-Session 'g' $ping
$wd = Start-Wd $s
Stop-Process -Id $ping.Id -Force
$wd.WaitForExit(10000) | Out-Null
Add-Result 'G exit 2 stops with error log' ($wd.HasExited -and $wd.ExitCode -eq 2 -and (Calls).Count -eq 1 -and (Test-Path "$s.watchdog-error")) "exit=$($wd.ExitCode) calls=$((Calls).Count) log=$(Test-Path "$s.watchdog-error")"
Remove-Item Env:CLISTUB_EXIT

# H: --recover exit 3 (parent alive, heartbeat fresh again) resumes monitoring: no second call, then disarm ends it
Reset-Log
$env:CLISTUB_EXIT = '3'
$ping = Start-Process ping -ArgumentList '-t 127.0.0.1' -PassThru -WindowStyle Hidden
$s = New-Session 'h' $ping
(Get-Item "$s.heartbeat").LastWriteTimeUtc = [DateTime]::UtcNow.AddSeconds(-30)
$wd = Start-Wd $s
$sw = [Diagnostics.Stopwatch]::StartNew()
while ((Calls).Count -eq 0 -and $sw.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 100 }
Start-Sleep -Seconds 4
$resumed = (-not $wd.HasExited) -and (Calls).Count -eq 1
Remove-Item "$s.armed"
$wd.WaitForExit(5000) | Out-Null
Add-Result 'H exit 3 resumes monitoring' ($resumed -and $wd.HasExited -and $wd.ExitCode -eq 0) "resumed=$resumed exit=$($wd.ExitCode) calls=$((Calls).Count)"
Remove-Item Env:CLISTUB_EXIT
Stop-Process -Id $ping.Id -Force -ErrorAction SilentlyContinue

Get-Process -Name DellFans.Watchdog -ErrorAction SilentlyContinue | Stop-Process -Force
$results | Format-Table -AutoSize
if ($results | Where-Object { -not $_.Pass }) { exit 1 }
