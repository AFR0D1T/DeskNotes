# Draws the DeskNotes icon (dark chalkboard sticker with a red push pin) and writes assets\icon.ico.
# Run once; the .ico is committed, so normal builds don't need this script.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "..\assets\icon.ico"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function Draw([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 256.0
    $g.TranslateTransform(128 * $k, 136 * $k); $g.RotateTransform(-6); $g.TranslateTransform(-128 * $k, -136 * $k)

    $r = New-Object System.Drawing.RectangleF (30 * $k), (40 * $k), (196 * $k), (196 * $k)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $c = 22 * $k
    $path.AddArc($r.X, $r.Y, $c, $c, 180, 90); $path.AddArc($r.Right - $c, $r.Y, $c, $c, 270, 90)
    $path.AddArc($r.Right - $c, $r.Bottom - $c, $c, $c, 0, 90); $path.AddArc($r.X, $r.Bottom - 2 * $c, 2 * $c, 2 * $c, 90, 90)
    $path.CloseFigure()
    $shadow = $path.Clone(); $m = New-Object System.Drawing.Drawing2D.Matrix; $m.Translate(6 * $k, 9 * $k); $shadow.Transform($m)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(90, 0, 0, 0))), $shadow)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x3A, 0x3B, 0x3F))), $path)

    $chalk = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(235, 0xEC, 0xEA, 0xE4)), ([math]::Max(1.5, 11 * $k))
    $chalk.StartCap = 'Round'; $chalk.EndCap = 'Round'
    if ($s -ge 32) {
        $g.DrawRectangle($chalk, 58 * $k, 98 * $k, 26 * $k, 26 * $k)
        $g.DrawLine($chalk, 104 * $k, 111 * $k, 196 * $k, 108 * $k)
        $g.DrawRectangle($chalk, 58 * $k, 150 * $k, 26 * $k, 26 * $k)
        $orange = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0xFF, 0xB0, 0x20)), ([math]::Max(1.5, 11 * $k)); $orange.StartCap = 'Round'; $orange.EndCap = 'Round'
        $g.DrawLine($orange, 104 * $k, 163 * $k, 176 * $k, 165 * $k)
        $g.DrawLines($chalk, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF (62 * $k), (110 * $k)), (New-Object System.Drawing.PointF (70 * $k), (120 * $k)), (New-Object System.Drawing.PointF (92 * $k), (88 * $k))))
    } else {
        $g.DrawLine($chalk, 60 * $k, 110 * $k, 196 * $k, 108 * $k)
        $g.DrawLine($chalk, 60 * $k, 165 * $k, 176 * $k, 165 * $k)
    }
    $g.ResetTransform()
    $pin = New-Object System.Drawing.RectangleF (104 * $k), (14 * $k), (48 * $k), (48 * $k)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(80, 0, 0, 0))), $pin.X + 5 * $k, $pin.Y + 7 * $k, $pin.Width, $pin.Height)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xD8, 0x43, 0x3F))), $pin)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(120, 255, 255, 255))), $pin.X + 11 * $k, $pin.Y + 9 * $k, 14 * $k, 14 * $k)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$images = foreach ($s in $sizes) { , (Draw $s) }
$fs = [System.IO.File]::Create($out); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $images[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$d.Length); $w.Write([uint32]$offset)
    $offset += $d.Length
}
foreach ($d in $images) { $w.Write($d) }
$w.Close()
Write-Host "Wrote $out"
