#requires -Version 5.1
param([ValidateRange(30,3600)][int]$Seconds=120, [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$Label='resources')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$appFolder=[IO.Path]::GetFullPath((Join-Path $root 'bin'))+[IO.Path]::DirectorySeparatorChar
$installedFolder=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'DellFans'))+[IO.Path]::DirectorySeparatorChar
$ids=@(Get-Process DellFans* -ErrorAction SilentlyContinue | Where-Object {$_.Path -and ($_.Path.StartsWith($appFolder,[StringComparison]::OrdinalIgnoreCase) -or $_.Path.StartsWith($installedFolder,[StringComparison]::OrdinalIgnoreCase))} | Select-Object -ExpandProperty Id)
$snapshotPath=Join-Path $root 'reports\live-latest.json'
if(Get-Service -Name DellFans -ErrorAction SilentlyContinue){$snapshotPath=Join-Path $env:ProgramData 'DellFans\reports\live-latest.json'}
if(-not $ids.Count){throw 'No DellFans process is running in this workspace.'}
function Totals([int[]]$ProcessIds) {
    $items=@(foreach($processId in $ProcessIds){Get-Process -Id $processId -ErrorAction SilentlyContinue})
    [pscustomobject]@{CpuSeconds=($items.CPU|Measure-Object -Sum).Sum;WorkingSetMB=($items.WorkingSet64|Measure-Object -Sum).Sum/1MB;
        PrivateMB=($items.PrivateMemorySize64|Measure-Object -Sum).Sum/1MB;Handles=($items.HandleCount|Measure-Object -Sum).Sum;
        Threads=($items|ForEach-Object {$_.Threads.Count}|Measure-Object -Sum).Sum;Alive=$items.Count}
}
$cores=[Environment]::ProcessorCount
$samples=[Collections.Generic.List[object]]::new()
$first=Totals $ids; $previous=$first
$wmiIds=@(Get-Process WmiPrvSE -ErrorAction SilentlyContinue|Select-Object -ExpandProperty Id)
$firstWmi=Totals $wmiIds; $previousWmi=$firstWmi
$clock=[Diagnostics.Stopwatch]::StartNew(); $previousTime=0.0
while($clock.Elapsed.TotalSeconds -lt $Seconds){
    Start-Sleep -Milliseconds 2000
    $now=$clock.Elapsed.TotalSeconds; $current=Totals $ids; $wmi=Totals $wmiIds
    $elapsed=$now-$previousTime
    $snapshot=$null
    try {$snapshot=Get-Content -LiteralPath $snapshotPath -Raw|ConvertFrom-Json} catch {}
    $samples.Add([pscustomobject]@{ElapsedSeconds=[math]::Round($now,3);CpuPercent=100*($current.CpuSeconds-$previous.CpuSeconds)/$elapsed/$cores;
        SharedWmiProviderCpuPercent=100*($wmi.CpuSeconds-$previousWmi.CpuSeconds)/$elapsed/$cores;WorkingSetMB=$current.WorkingSetMB;
        PrivateMB=$current.PrivateMB;Handles=$current.Handles;Threads=$current.Threads;Alive=$current.Alive;PollMs=$snapshot.PollDurationMilliseconds;
        SnapshotUtc=$snapshot.PolledAtUtc;TargetRpm=$snapshot.ManualControl.TargetRpm;ActualRpm=$snapshot.ManualControl.ActualRpm;Mode=$snapshot.FanMode;
        InternalResources=$snapshot.Resources;Background=$snapshot.Background})
    $previous=$current; $previousWmi=$wmi; $previousTime=$now
}
$summary=[pscustomobject]@{Label=$Label;AtUtc=[DateTime]::UtcNow.ToString('o');DurationSeconds=$previousTime;ProcessIds=$ids;LogicalProcessors=$cores;
    CpuPercentBasis='100% = all logical processors; application plus its existing watchdog';
    AverageCpuPercent=100*($previous.CpuSeconds-$first.CpuSeconds)/$previousTime/$cores;PeakSampleCpuPercent=($samples.CpuPercent|Measure-Object -Maximum).Maximum;
    WorkingSetMeanMB=($samples.WorkingSetMB|Measure-Object -Average).Average;WorkingSetPeakMB=($samples.WorkingSetMB|Measure-Object -Maximum).Maximum;
    PrivateStartMB=$first.PrivateMB;PrivateEndMB=$previous.PrivateMB;PrivateChangeMB=$previous.PrivateMB-$first.PrivateMB;
    MaximumHandles=($samples.Handles|Measure-Object -Maximum).Maximum;MaximumThreads=($samples.Threads|Measure-Object -Maximum).Maximum;
    SharedWmiAverageCpuPercent=100*($previousWmi.CpuSeconds-$firstWmi.CpuSeconds)/$previousTime/$cores;
    SharedWmiCaveat='Shared provider totals include other software and are not attributed exclusively to DellFans.';
    ProcessPopulationStable=(@($samples|Where-Object {$_.Alive -ne $ids.Count}).Count -eq 0)}
$report=Join-Path $root ('reports\'+$Label+'.json')
[IO.File]::WriteAllText($report,([pscustomobject]@{Summary=$summary;Samples=@($samples.ToArray())}|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
$summary|ConvertTo-Json -Depth 4
