$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$out = Join-Path (Split-Path $src -Parent) 'WiFi热点.exe'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }

$cscArgs = @(
    '/target:winexe',
    '/platform:anycpu',
    '/codepage:65001',
    '/optimize+',
    ('/win32manifest:' + (Join-Path $src 'app.manifest')),
    ('/resource:' + (Join-Path $src 'HotspotPs.ps1') + ',HotspotPs.ps1'),
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll',
    '/r:System.ServiceProcess.dll',
    ('/out:' + $out),
    (Join-Path $src 'WiFiHotspot.cs')
)

$icon = Join-Path $src 'app.ico'
if (Test-Path $icon) { $cscArgs = @('/win32icon:' + $icon) + $cscArgs }

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败，exit=$LASTEXITCODE" }
Get-Item $out | Select-Object FullName, Length, LastWriteTime
