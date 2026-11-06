#requires -Version 5.1
# Runs every offline test runner. Skips runners that need the installed service, hardware or a package archive.
$ErrorActionPreference='Stop'
$skip=@('run-all.ps1')
$failed=0
$started=Get-Date
foreach($name in @('run.ps1') + @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'run-*.ps1' | Where-Object { $skip -notcontains $_.Name } | ForEach-Object Name)){
    # Native stderr (cargo/csc progress) must not become a terminating error: relax the preference around the call and treat any throw as a failed runner.
    $ErrorActionPreference='Continue'
    try { $output=& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $name) 2>&1 | Out-String; $code=$LASTEXITCODE }
    catch { $output=($_ | Out-String); $code=1 }
    finally { $ErrorActionPreference='Stop' }
    $tail=(($output -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 1) -join '')
    if($code -ne 0){$failed++; Write-Host ("FAIL {0} (exit {1})`n{2}" -f $name,$code,$output)} else {Write-Host ("PASS {0}: {1}" -f $name,$tail)}
}
if($failed){throw "$failed test runner(s) failed."}
# All green: remove the temp folders/files the runners created during this run (kept on failure as evidence).
Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Force | Where-Object { $_.Name -match '^(DellFans-|dell-performance-safety-run-)' -and $_.CreationTime -ge $started } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
