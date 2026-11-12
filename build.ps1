# Builds DeskNotes with the C# compiler that ships with Windows (.NET Framework 4.x) — nothing to install.
# Usage:  .\build.ps1            -> bin\DeskNotes.exe and bin\DeskNotes-Setup.exe
#         .\build.ps1 -Install   -> also replaces the running copy in %LOCALAPPDATA%\Programs\DeskNotes
#         .\build.ps1 -Release   -> also publishes bin\DeskNotes-Setup.exe as a GitHub release (needs the gh CLI)
param([switch]$Install, [switch]$Release)

$ErrorActionPreference = "Stop"
$fw   = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$root = $PSScriptRoot
$bin  = Join-Path $root "bin"
$app  = Join-Path $bin "DeskNotes.exe"
$icon = Join-Path $root "assets\icon.ico"
$setup = Join-Path $bin "DeskNotes-Setup.exe"
New-Item -ItemType Directory -Force $bin | Out-Null

$config = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup><supportedRuntime version="v4.0"/></startup>
  <runtime><gcConcurrent enabled="false"/></runtime>
</configuration>
'@

# the app: optimized, no debug info, x64
& "$fw\csc.exe" /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /debug- /win32icon:$icon /lib:"$fw\WPF" `
    /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll `
    /r:System.Runtime.Serialization.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    /out:$app (Join-Path $root "DeskNotes.cs")
if ($LASTEXITCODE) { throw "App build failed" }
Set-Content "$app.config" $config -Encoding utf8

# the installer, with the app embedded inside it
& "$fw\csc.exe" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /debug- /win32icon:$icon `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    /resource:"$app,DeskNotes.exe" /out:$setup (Join-Path $root "installer\Setup.cs")
if ($LASTEXITCODE) { throw "Installer build failed" }
Write-Host ("Built {0} and {1} ({2:N0} KB)" -f $app, $setup, ((Get-Item $setup).Length / 1KB))

if ($Install) {
    $dst = "$env:LOCALAPPDATA\Programs\DeskNotes"
    New-Item -ItemType Directory -Force $dst | Out-Null
    $data = "$env:APPDATA\DeskNotes\data.json"
    if (Test-Path $data) { Copy-Item $data "$env:APPDATA\DeskNotes\data.backup.json" -Force }
    Get-Process DeskNotes -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 1
    Copy-Item $app, "$app.config" $dst -Force
    Start-Process "$dst\DeskNotes.exe"
    Write-Host "Installed and started $dst\DeskNotes.exe"
}

if ($Release) {
    # version comes from the installer: const string Version = "x.y.z";
    $ver = [regex]::Match((Get-Content (Join-Path $root "installer\Setup.cs") -Raw), 'const string Version = "([^"]+)"').Groups[1].Value
    if (-not $ver) { throw "Version not found in installer\Setup.cs" }
    & gh release view "v$ver" *> $null
    if ($LASTEXITCODE -eq 0) { & gh release upload "v$ver" $setup --clobber }
    else { & gh release create "v$ver" $setup --title "DeskNotes $ver" --notes "Скачай DeskNotes-Setup.exe и запусти." }
    if ($LASTEXITCODE) { throw "Release failed" }
    Write-Host "Published v$ver"
}
