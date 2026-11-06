#requires -Version 5.1
# Shared by the offline UI test runners: compiles every file in src\ plus the named test files, then runs the chosen entry class.
# Nothing here touches the service, scheduled tasks, HKLM or program directories.
function Invoke-UiTest {
    param([Parameter(Mandatory)][string]$Main,[Parameter(Mandatory)][string[]]$TestFiles,[string]$Pattern='',[string[]]$Arguments=@())
    $ErrorActionPreference='Stop'
    $root=Split-Path -Parent $PSScriptRoot
    $runtime=Join-Path ([IO.Path]::GetTempPath()) ('DellFans-'+$Main+'-'+[Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($runtime)
    try {
        $compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
        $sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
        $tests=@($TestFiles | ForEach-Object { Join-Path $PSScriptRoot $_ })
        $exe=Join-Path $runtime ($Main+'.exe')
        $log=& $compiler /nologo /platform:x64 /codepage:65001 /langversion:5 /target:exe ('/win32manifest:'+(Join-Path $root 'src\app.gui.manifest')) ('/main:'+$Main) ('/out:'+$exe) `
            /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll /r:System.Web.Extensions.dll /r:System.ServiceProcess.dll /r:System.Security.dll `
            @sources @tests 2>&1
        if($LASTEXITCODE -ne 0){ $log | ForEach-Object { Write-Host "$_" }; throw "$Main compilation failed." }
        $output=@(& $exe @Arguments 2>&1 | ForEach-Object { "$_" })
        $exit=$LASTEXITCODE
        $output | ForEach-Object { Write-Host $_ }
        if($exit -ne 0){ throw "$Main failed (exit $exit)." }
        if($Pattern -and -not ($output -match $Pattern)){ throw "$Main did not report completion ($Pattern)." }
    } finally { Remove-Item -LiteralPath $runtime -Recurse -Force -ErrorAction SilentlyContinue }
}
