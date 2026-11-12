# Takes the README screenshots and measures resource use. Runs in GitHub Actions (workflow "Screenshots"),
# or locally on a spare Windows account: it REPLACES %APPDATA%\DeskNotes with demo data, so never run it on
# the account where your real notes live (it refuses if data.json already exists, unless -Force).
param([string]$Exe = (Join-Path $PSScriptRoot "..\bin\DeskNotes.exe"), [string]$Out = (Join-Path $PSScriptRoot "..\docs"), [switch]$Force)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing, PresentationCore, WindowsBase
Add-Type -Name W -Namespace DN -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SystemParametersInfo(int a, int b, string c, int d);
[DllImport("user32.dll")] public static extern bool SetSysColors(int n, int[] e, int[] c);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
[DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
[DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
[DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
public struct RECT { public int Left, Top, Right, Bottom; }
public static IntPtr Find(uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { found = h; return false; } return true; }, IntPtr.Zero);
    return found;
}
'@

$data = Join-Path $env:APPDATA "DeskNotes"
if ((Test-Path (Join-Path $data "data.json")) -and -not $Force) { throw "$data\data.json exists: this would overwrite real notes. Use -Force on a spare account." }
New-Item -ItemType Directory -Force $data, (Join-Path $data "fonts"), $Out | Out-Null

# the handwriting font the sticker uses by default (Google Fonts, OFL)
$font = Join-Path $data "fonts\Caveat.ttf"
if (-not (Test-Path $font)) { Invoke-WebRequest "https://raw.githubusercontent.com/google/fonts/main/ofl/caveat/Caveat%5Bwght%5D.ttf" -OutFile $font }

# plain desk-coloured background instead of the wallpaper
[DN.W]::SystemParametersInfo(0x14, 0, "", 3) | Out-Null
[DN.W]::SetSysColors(1, @(1), @(0x00B8B2AA)) | Out-Null   # 0x00BBGGRR

function Date([int]$daysAgo) { "/Date(" + [DateTimeOffset]::UtcNow.AddDays(-$daysAgo).ToUnixTimeMilliseconds() + ")/" }
function Item($title, [bool]$done = $false, [int]$tag = -1, $details = "", $subs = @(), [int]$age = 0) {
    [ordered]@{ Title = $title; Done = $done; Tag = $tag; Details = $details; Subs = @($subs | ForEach-Object { [ordered]@{ Text = $_[0]; Done = [bool]$_[1] } }); Created = (Date $age) }
}

# a small drawing made of strokes, stored the way the sticker stores it (ISF, base64)
function Ink {
    $sc = New-Object System.Windows.Ink.StrokeCollection
    function Add($pts) {
        $c = New-Object System.Windows.Input.StylusPointCollection
        foreach ($p in $pts) { $c.Add([System.Windows.Input.StylusPoint]::new([double]$p[0], [double]$p[1])) }
        $s = [System.Windows.Ink.Stroke]::new($c)
        $s.DrawingAttributes.Width = 3; $s.DrawingAttributes.Height = 3; $s.DrawingAttributes.FitToCurve = $true
        $s.DrawingAttributes.Color = [System.Windows.Media.Color]::FromRgb(0xEC, 0xEA, 0xE4)
        $sc.Add($s)
    }
    $wob = { param($i) [math]::Sin($i * 1.7) * 1.2 }
    # sun
    Add (0..40 | ForEach-Object { $a = $_ / 40 * 2 * [math]::PI; ,@((230 + 26 * [math]::Cos($a) + (& $wob $_)), (70 + 26 * [math]::Sin($a))) })
    foreach ($k in 0..7) { $a = $k / 8 * 2 * [math]::PI; Add @(@((230 + 36 * [math]::Cos($a)), (70 + 36 * [math]::Sin($a))), @((230 + 50 * [math]::Cos($a)), (70 + 50 * [math]::Sin($a)))) }
    # house
    Add @(@(50, 250), @(52, 175), @(150, 173), @(148, 250), @(50, 250))
    Add @(@(40, 180), @(100, 120), @(160, 178))
    Add @(@(88, 250), @(88, 210), @(112, 210), @(112, 250))
    Add @(@(62, 190), @(80, 190), @(80, 206), @(62, 206), @(62, 190))
    # ground and a little tree
    Add (0..30 | ForEach-Object { ,@((20 + $_ * 9), (252 + [math]::Sin($_ * .8) * 2)) })
    Add @(@(210, 252), @(212, 205))
    Add (0..30 | ForEach-Object { $a = $_ / 30 * 2 * [math]::PI; ,@((211 + 22 * [math]::Cos($a) + (& $wob $_)), (185 + 18 * [math]::Sin($a))) })
    $ms = New-Object System.IO.MemoryStream
    $sc.Save($ms, $true)
    [Convert]::ToBase64String($ms.ToArray())
}

$tabs = @(
    [ordered]@{ Name = "Дела"; Checklist = $true; Drawing = $false; Items = @(
        (Item "Позвонить в сервис насчёт ноутбука" $false 0),
        (Item "Купить продукты на неделю" $false -1 "" @(@("молоко, хлеб", $true), @("овощи", $false), @("кофе", $false))),
        (Item "Дописать отчёт до **пятницы**" $false 1 "Осталось: выводы и графики"),
        (Item "Записаться к стоматологу" $true 2),
        (Item "Оплатить интернет" $true -1),
        (Item "Прочитать статью про WPF" $false 3 "[Ссылка](https://learn.microsoft.com/dotnet/desktop/wpf/)")
    ) },
    [ordered]@{ Name = "Заметки"; Checklist = $false; Drawing = $false; Items = @(
        (Item "Идеи для подарка" $false 4 "Настольная лампа с тёплым светом, хорошая кружка, книга по фотографии" @() 1),
        (Item "Рецепт сырников" $false 1 "Творог 400 г, 1 яйцо, 2 ст. л. муки, сахар по вкусу.`nЖарить на среднем огне по 3 минуты с каждой стороны." @() 3),
        (Item "Wi-Fi на даче" $false -1 "Сеть: dacha_5G" @() 9)
    ) },
    [ordered]@{ Name = "Рисунки"; Checklist = $false; Drawing = $true; Items = @(); InkData = (Ink) }
)

function Scene($name, [int]$active, $look) {
    $store = [ordered]@{ Tabs = $tabs; Active = $active; DrawTabAdded = $true }
    $json = ($store | ConvertTo-Json -Depth 8) -replace '"/Date\((\d+)\)/"', '"\/Date($1)\/"'
    [IO.File]::WriteAllText((Join-Path $data "data.json"), $json, (New-Object Text.UTF8Encoding $false))
    $cfg = [ordered]@{ X = 70; Y = 60; W = 360; H = 500; Font = "Caveat"; HideFromCapture = $false }
    foreach ($k in $look.Keys) { $cfg[$k] = $look[$k] }
    [IO.File]::WriteAllText((Join-Path $data "settings.json"), ($cfg | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process $Exe -PassThru
    $h = [IntPtr]::Zero
    while ($h -eq [IntPtr]::Zero -and $sw.Elapsed.TotalSeconds -lt 20) { Start-Sleep -Milliseconds 20; $h = [DN.W]::Find([uint32]$p.Id) }
    $startup = $sw.Elapsed.TotalSeconds
    Start-Sleep 3
    $r = New-Object DN.W+RECT
    [DN.W]::GetWindowRect($h, [ref]$r) | Out-Null
    $m = 28
    $x = [math]::Max(0, $r.Left - $m); $y = [math]::Max(0, $r.Top - $m)
    $w = $r.Right - $r.Left + 2 * $m; $hh = $r.Bottom - $r.Top + 2 * $m
    $bmp = New-Object Drawing.Bitmap $w, $hh
    $g = [Drawing.Graphics]::FromImage($bmp)
    # SRCCOPY | CAPTUREBLT, so the layered (transparent) sticker window is included
    $dst = $g.GetHdc(); $src = [DN.W]::GetDC([IntPtr]::Zero)
    [DN.W]::BitBlt($dst, 0, 0, $w, $hh, $src, $x, $y, 0x40CC0020) | Out-Null
    [DN.W]::ReleaseDC([IntPtr]::Zero, $src) | Out-Null; $g.ReleaseHdc($dst)
    $bmp.Save((Join-Path $Out "$name.png"), [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return @{ Process = $p; Startup = $startup }
}

$s1 = Scene "screenshot-tasks" 0 @{}
# resource use of the first run: settle, then watch an idle minute
Start-Sleep 10
$p = $s1.Process; $p.Refresh()
$cpu0 = $p.TotalProcessorTime
Start-Sleep 60
$p.Refresh()
$stats = [ordered]@{
    startup_s       = [math]::Round($s1.Startup, 2)
    idle_cpu_ms_60s = [math]::Round(($p.TotalProcessorTime - $cpu0).TotalMilliseconds)
    working_set_mb  = [math]::Round($p.WorkingSet64 / 1MB, 1)
    private_mb      = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
    threads         = $p.Threads.Count
    priority        = "$($p.PriorityClass)"
    exe_kb          = [math]::Round((Get-Item $Exe).Length / 1KB)
}
$p.Kill(); $p.WaitForExit()

$s2 = Scene "screenshot-notes" 1 @{ Paper = "#FFE27A"; Ink = "#1E3F99"; Rotation = 1.5 }
$s2.Process.Kill(); $s2.Process.WaitForExit()
$s3 = Scene "screenshot-drawing" 2 @{}
$s3.Process.Kill(); $s3.Process.WaitForExit()

$stats | ConvertTo-Json | Set-Content (Join-Path $Out "stats.json") -Encoding utf8
$stats | Format-List
