# Builds DeskNotes.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# Usage:  .\build.ps1            -> builds .\bin\DeskNotes.exe
#         .\build.ps1 -Install   -> also replaces the running copy in %LOCALAPPDATA%\Programs\DeskNotes
param([switch]$Install)

$ErrorActionPreference = "Stop"
$fw  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$out = Join-Path $PSScriptRoot "bin\DeskNotes.exe"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

& "$fw\csc.exe" /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /lib:"$fw\WPF" `
    /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll `
    /r:System.Runtime.Serialization.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    /out:$out (Join-Path $PSScriptRoot "DeskNotes.cs")
if ($LASTEXITCODE) { throw "Build failed" }
Write-Host "Built $out"

if ($Install) {
    $dst = "$env:LOCALAPPDATA\Programs\DeskNotes"
    New-Item -ItemType Directory -Force $dst | Out-Null
    $data = "$env:APPDATA\DeskNotes\data.json"
    if (Test-Path $data) { Copy-Item $data "$env:APPDATA\DeskNotes\data.backup.json" -Force }
    Get-Process DeskNotes -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 1
    Copy-Item $out "$dst\DeskNotes.exe" -Force
    Start-Process "$dst\DeskNotes.exe"
    Write-Host "Installed and started $dst\DeskNotes.exe"
}
