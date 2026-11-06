#requires -Version 5.1
# Read-only: samples CPU and memory counters of running DellFans processes (service, tray, watchdog and optionally a window you opened).
param([int]$GuiProcessId,[int]$Seconds=60,[string]$OutputDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'reports\ui-footprint'))
$ErrorActionPreference='Stop'
if($Seconds -lt 10 -or $Seconds -gt 3600){throw 'Seconds must be 10-3600.'}
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$targets=@()
if($GuiProcessId){$targets+=[pscustomobject]@{Role='GUI';Id=$GuiProcessId}}
$service=Get-CimInstance Win32_Service -Filter "Name='DellFans'"
if($service -and $service.ProcessId){$targets+=[pscustomobject]@{Role='Service';Id=[int]$service.ProcessId}}
foreach($process in @(Get-CimInstance Win32_Process -Filter "Name='DellFans.Tray.exe'")){$targets+=[pscustomobject]@{Role='Tray';Id=[int]$process.ProcessId}}
foreach($process in @(Get-CimInstance Win32_Process -Filter "Name='DellFans.Watchdog.exe'")){$targets+=[pscustomobject]@{Role='Watchdog';Id=[int]$process.ProcessId}}
if($targets.Count -eq 0){throw 'No running DellFans process was found to measure.'}
$previous=@{}; $identities=@{}; $totals=@{}; $samples=[Collections.Generic.List[object]]::new()
foreach($target in $targets){$process=Get-Process -Id $target.Id;try{$previous[$target.Id]=$process.TotalProcessorTime.TotalSeconds;$identities[$target.Id]=$process.StartTime.ToUniversalTime().Ticks;$totals[$target.Id]=0.0}finally{$process.Dispose()}}
$clock=[Diagnostics.Stopwatch]::StartNew();$lastTime=0.0;$logical=[Environment]::ProcessorCount
$raw=Join-Path $OutputDirectory 'process-samples.jsonl';[IO.File]::WriteAllText($raw,'',[Text.UTF8Encoding]::new($false))
while($clock.Elapsed.TotalSeconds -lt $Seconds){
    Start-Sleep -Milliseconds ([int][Math]::Min(5000,($Seconds-$clock.Elapsed.TotalSeconds)*1000))
    $now=$clock.Elapsed.TotalSeconds;$elapsed=$now-$lastTime
    foreach($target in $targets){
        $process=Get-Process -Id $target.Id
        try{
            if($process.StartTime.ToUniversalTime().Ticks -ne $identities[$target.Id]){throw 'A measured process restarted.'}
            $cpu=$process.TotalProcessorTime.TotalSeconds;$spent=$cpu-$previous[$target.Id];$totals[$target.Id]+=$spent;$previous[$target.Id]=$cpu
            $row=[pscustomobject]@{AtUtc=[DateTime]::UtcNow.ToString('o');ElapsedSeconds=$now;IntervalSeconds=$elapsed;Role=$target.Role;ProcessId=$target.Id;CpuPercent=$spent*100/($elapsed*$logical);WorkingSetMiB=$process.WorkingSet64/1MB;PrivateMiB=$process.PrivateMemorySize64/1MB;Handles=$process.HandleCount;Threads=$process.Threads.Count}
            $samples.Add($row);[IO.File]::AppendAllText($raw,($row|ConvertTo-Json -Compress)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
        }finally{$process.Dispose()}
    }
    $lastTime=$now
}
$summary=[ordered]@{AtUtc=[DateTime]::UtcNow.ToString('o');ElapsedSeconds=$lastTime;LogicalProcessors=$logical;Mode='Read-only process counters of already running DellFans processes; UI interaction not recorded';Processes=@()}
foreach($target in $targets){$rows=@($samples|Where-Object ProcessId -eq $target.Id);$summary.Processes+= [ordered]@{Role=$target.Role;ProcessId=$target.Id;CpuMeanPercent=$totals[$target.Id]*100/($lastTime*$logical);CpuPeakIntervalPercent=($rows.CpuPercent|Measure-Object -Maximum).Maximum;WorkingSetMeanMiB=($rows.WorkingSetMiB|Measure-Object -Average).Average;WorkingSetPeakMiB=($rows.WorkingSetMiB|Measure-Object -Maximum).Maximum;PrivateMeanMiB=($rows.PrivateMiB|Measure-Object -Average).Average;HandlesFirst=$rows[0].Handles;HandlesLast=$rows[-1].Handles;ThreadsFirst=$rows[0].Threads;ThreadsLast=$rows[-1].Threads}}
$summary|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $OutputDirectory 'performance-summary.json') -Encoding UTF8
$summary|ConvertTo-Json -Depth 5
