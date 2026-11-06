# Regenerates every file in assets\icon (SVG, ICO, preview.png) from IconBuilder.cs.
# Needs only the .NET Framework C# compiler that ships with Windows. No network, no extra tools.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$tmp  = Join-Path ([IO.Path]::GetTempPath()) ('dellfans-icon-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $exe = Join-Path $tmp 'IconBuilder.exe'
    & $csc /nologo /target:exe /platform:anycpu "/out:$exe" /r:System.Drawing.dll (Join-Path $here 'IconBuilder.cs')
    if ($LASTEXITCODE -ne 0) { throw 'csc failed' }
    & $exe $here
    if ($LASTEXITCODE -ne 0) { throw 'IconBuilder failed' }
} finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
