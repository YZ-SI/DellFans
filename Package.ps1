#requires -Version 5.1
# Creates dist\DellFans-<version>-win-x64.zip, the portable release: one folder with the programs from bin\, their licenses and a
# short read-me. Users unzip it and run DellFans.exe; nothing is installed. Also writes <zip>.sha256.
# Only reads bin\ and writes the archive; it never touches services, the registry or program folders.
param([string]$BinaryDirectory,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root=$PSScriptRoot
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root 'dist'}
[void][IO.Directory]::CreateDirectory($OutputDirectory)
if(-not $BinaryDirectory){$BinaryDirectory=Join-Path $root 'bin'}
$BinaryDirectory=[IO.Path]::GetFullPath($BinaryDirectory).TrimEnd('\')
$programs=@('DellFans.exe','DellFans.Cli.exe','DellFans.Service.exe','DellFans.Tray.exe','DellFans.Watchdog.exe')
$files=$programs+@('DellFans.exe.config','DellFans.Cli.exe.config','DellFans.Service.exe.config','IntelMSR.bin','THIRD-PARTY-NOTICES.txt')
foreach($name in $files){ if(-not (Test-Path -LiteralPath (Join-Path $BinaryDirectory $name))){throw "Missing from bin\: $name (run build.ps1 first)"} }
$versions=@(@('DellFans.exe','DellFans.Cli.exe','DellFans.Service.exe') | ForEach-Object {[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $BinaryDirectory $_)).FileVersion} | Select-Object -Unique)
if($versions.Count -ne 1){throw 'Package requires matching GUI, CLI and controller build versions.'}
$version=[Version]$versions[0]
$label='{0}.{1}.{2}' -f $version.Major,$version.Minor,$version.Build
$folder='DellFans-'+$label
$path=Join-Path $OutputDirectory ($folder+'-win-x64.zip')
$readme=@"
DellFans $label (Windows 10 x64, .NET Framework 4.8)

使用：解压到本地文件夹，双击 DellFans.exe。首次运行会请求管理员权限以启动后台控速程序（不安装任何服务）。
关闭窗口后由托盘继续控速；托盘菜单“退出 DellFans”会把风扇交回 BIOS 自动并退出。
第一次打开窗口会检测这台电脑的 Dell 风扇接口；未通过时不会控制风扇。
不再使用时：从托盘退出，关闭“登录后启动”，然后删除本文件夹。

Usage: unzip to a local folder and run DellFans.exe. The first run asks for administrator permission to start the background
controller (nothing is installed as a service). After the window closes the tray keeps control; its "Exit DellFans" item hands
the fan back to BIOS automatic and exits. The first time the window opens it checks this computer's Dell fan interface; until
it passes, DellFans never controls the fan. To stop using it: exit from the tray, turn off sign-in start, delete this folder.

https://github.com/YZ-SI/DellFans
"@
$stream=[IO.File]::Open($path,[IO.FileMode]::Create,[IO.FileAccess]::Write)
$zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach($name in $files){[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $BinaryDirectory $name),"$folder/$name",[IO.Compression.CompressionLevel]::Optimal)}
    $licenses=Join-Path $BinaryDirectory 'licenses'
    if(Test-Path -LiteralPath $licenses){Get-ChildItem -LiteralPath $licenses -Recurse -File | ForEach-Object {[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$_.FullName,"$folder/licenses/"+$_.FullName.Substring($licenses.Length+1).Replace('\','/'),[IO.Compression.CompressionLevel]::Optimal)}}
    $entry=$zip.CreateEntry("$folder/README.txt")
    $writer=[IO.StreamWriter]::new($entry.Open(),[Text.UTF8Encoding]::new($true))
    try{$writer.Write($readme.Replace("`r`n","`n").Replace("`n","`r`n"))}finally{$writer.Dispose()}
} finally { $zip.Dispose(); $stream.Dispose() }
$hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
[IO.File]::WriteAllText(($path+'.sha256'),($hash+'  '+[IO.Path]::GetFileName($path)+[Environment]::NewLine))
Get-Item -LiteralPath $path | Select-Object FullName,Length
