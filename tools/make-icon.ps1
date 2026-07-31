# Generates src/ScreenTray/Assets/app.ico.
#
# Design: three rounded cards in a row, receding to the right, in analogous cool
# hues (blue, violet, teal). The front card is leftmost, matching the app, where the
# newest screenshot sits on the left. It carries a sun-and-mountain glyph.
#
# One geometry at every size, simply scaled. An earlier version substituted a
# simplified single-card design below 24px for legibility, which meant the taskbar
# showed the three-card icon while the tray and title bar showed something else.
# Consistency wins: it is the same app, so it gets the same icon everywhere.

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    if ($d -le 0) { $p.AddRectangle((New-Object System.Drawing.RectangleF $x, $y, $w, $h)); return $p }
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Laid out on a 32x32 grid and scaled from there. Drawn back to front, so the front
# card ends up on top and each one behind shows as a sliver on the right.
$Cards = @(
    @{ X = 13.0; C1 = @(46, 196, 182);  C2 = @(20, 150, 140) }   # back:   teal
    @{ X = 7.0;  C1 = @(150, 110, 250); C2 = @(107, 62, 224) }   # middle: violet
    @{ X = 1.0;  C1 = @(80, 150, 250);  C2 = @(28, 88, 214) }    # front:  blue
)

$CardWidth = 18.0
$CardTop = 6.0
$CardHeight = 20.0

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $f = $size / 32.0
    $radius = [Math]::Max(0.75, 3 * $f)

    $frontRect = $null
    foreach ($card in $Cards) {
        $rect = New-Object System.Drawing.RectangleF ($card.X * $f), ($CardTop * $f), ($CardWidth * $f), ($CardHeight * $f)
        $path = New-RoundedPath $rect.X $rect.Y $rect.Width $rect.Height $radius
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $rect,
            [System.Drawing.Color]::FromArgb(255, $card.C1[0], $card.C1[1], $card.C1[2]),
            [System.Drawing.Color]::FromArgb(255, $card.C2[0], $card.C2[1], $card.C2[2]),
            [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
        $g.FillPath($brush, $path)
        $brush.Dispose(); $path.Dispose()
        $frontRect = $rect
    }

    # Sun and mountain, on the front card only.
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240, 255, 255, 255))
    $sunD = $frontRect.Width * 0.22
    $g.FillEllipse($white,
        ($frontRect.X + $frontRect.Width * 0.17),
        ($frontRect.Y + $frontRect.Height * 0.17),
        $sunD, $sunD)
    $pts = @(
        (New-Object System.Drawing.PointF ($frontRect.X + $frontRect.Width * 0.10), ($frontRect.Y + $frontRect.Height * 0.84)),
        (New-Object System.Drawing.PointF ($frontRect.X + $frontRect.Width * 0.45), ($frontRect.Y + $frontRect.Height * 0.38)),
        (New-Object System.Drawing.PointF ($frontRect.X + $frontRect.Width * 0.90), ($frontRect.Y + $frontRect.Height * 0.84))
    )
    $g.FillPolygon($white, $pts)
    $white.Dispose()
    $g.Dispose()
    return $bmp
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Frame $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\ScreenTray\Assets\app.ico'
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs

# ICONDIR
$bw.Write([uint16]0)              # reserved
$bw.Write([uint16]1)              # type: icon
$bw.Write([uint16]$pngs.Count)

$offset = 6 + (16 * $pngs.Count)
foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $bw.Write([byte]$dim)          # width
    $bw.Write([byte]$dim)          # height
    $bw.Write([byte]0)             # palette count
    $bw.Write([byte]0)             # reserved
    $bw.Write([uint16]1)           # colour planes
    $bw.Write([uint16]32)          # bits per pixel
    $bw.Write([uint32]$p.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $bw.Write($p.Bytes) }

$bw.Flush(); $bw.Dispose(); $fs.Dispose()

Write-Output "wrote $out ($((Get-Item $out).Length) bytes, $($pngs.Count) frames: $($sizes -join ', '))"
