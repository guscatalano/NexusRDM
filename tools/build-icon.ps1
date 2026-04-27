# Generates src/NexusRDM/Assets/AppIcon.ico from a procedural design that
# matches the in-app palette: dark rounded square with a stylized "N"
# made of three accent nodes connected by network edges.
#
# Run this from the repo root:  pwsh tools/build-icon.ps1
#
# Output: src/NexusRDM/Assets/AppIcon.ico (multi-res 16/24/32/48/64/128/256).

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$repo    = Split-Path -Parent $PSScriptRoot
$outDir  = Join-Path $repo 'src/NexusRDM/Assets'
$outIco  = Join-Path $outDir 'AppIcon.ico'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# Palette (matches App.xaml NxBg0/NxAccent/NxSsh/NxRdp).
$bg     = [System.Drawing.Color]::FromArgb(255,  26,  26,  31)
$panel  = [System.Drawing.Color]::FromArgb(255,  36,  36,  44)
$accent = [System.Drawing.Color]::FromArgb(255, 124, 110, 247)
$accent2= [System.Drawing.Color]::FromArgb(255, 165, 153, 255)
$ssh    = [System.Drawing.Color]::FromArgb(255,  61, 214, 140)
$rdp    = [System.Drawing.Color]::FromArgb(255,  77, 166, 255)

function New-IconBitmap([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode    = 'AntiAlias'
    $g.InterpolationMode= 'HighQualityBicubic'
    $g.PixelOffsetMode  = 'HighQuality'

    [single]$sz   = $size
    [single]$rArc = [Math]::Max(2.0, $sz * 0.18)
    [single]$x0   = 0.5
    [single]$y0   = 0.5
    [single]$w    = $sz - 1
    [single]$h    = $sz - 1
    $rect = [System.Drawing.RectangleF]::new($x0, $y0, $w, $h)

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($rect.X,                   $rect.Y,                   $rArc*2, $rArc*2, 180, 90) | Out-Null
    $path.AddArc($rect.Right - $rArc*2,     $rect.Y,                   $rArc*2, $rArc*2, 270, 90) | Out-Null
    $path.AddArc($rect.Right - $rArc*2,     $rect.Bottom - $rArc*2,    $rArc*2, $rArc*2, 0,   90) | Out-Null
    $path.AddArc($rect.X,                   $rect.Bottom - $rArc*2,    $rArc*2, $rArc*2, 90,  90) | Out-Null
    $path.CloseAllFigures()

    $bgBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, $bg, $panel, [single]90)
    $g.FillPath($bgBrush, $path)
    $bgBrush.Dispose()

    [single]$nodeR = [Math]::Max(1.0, $sz * 0.11)
    [single]$pad   = $sz * 0.26
    $tl = [System.Drawing.PointF]::new($pad,        $pad)
    $bl = [System.Drawing.PointF]::new($pad,        $sz - $pad)
    $tr = [System.Drawing.PointF]::new($sz - $pad,  $pad)
    $br = [System.Drawing.PointF]::new($sz - $pad,  $sz - $pad)

    [single]$edgeWidth = [Math]::Max(1.0, $sz * 0.06)
    $edgePen = [System.Drawing.Pen]::new($accent, $edgeWidth)
    $edgePen.StartCap = 'Round'
    $edgePen.EndCap   = 'Round'

    $g.DrawLine($edgePen, $tl, $bl)         # left vertical
    $g.DrawLine($edgePen, $bl, $tr)         # diagonal (the N stroke)
    $g.DrawLine($edgePen, $tr, $br)         # right vertical
    $edgePen.Dispose()

    function Draw-Node($g, $p, $color, [single]$nodeR) {
        $glow = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $glow.AddEllipse($p.X - $nodeR*1.45, $p.Y - $nodeR*1.45, $nodeR*2.9, $nodeR*2.9) | Out-Null
        $pgb = [System.Drawing.Drawing2D.PathGradientBrush]::new($glow)
        $pgb.CenterColor    = $color
        $pgb.SurroundColors = ,([System.Drawing.Color]::FromArgb(0, $color.R, $color.G, $color.B))
        $g.FillPath($pgb, $glow)
        $pgb.Dispose()
        $glow.Dispose()

        $core = [System.Drawing.RectangleF]::new([single]($p.X - $nodeR), [single]($p.Y - $nodeR), [single]($nodeR*2), [single]($nodeR*2))
        $coreBrush = [System.Drawing.SolidBrush]::new($color)
        $g.FillEllipse($coreBrush, $core)
        $coreBrush.Dispose()

        [single]$hlR = $nodeR * 0.45
        $hl = [System.Drawing.RectangleF]::new([single]($p.X - $nodeR*0.4), [single]($p.Y - $nodeR*0.6), [single]($hlR*2), [single]($hlR*2))
        $hlBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(160, 255, 255, 255))
        $g.FillEllipse($hlBrush, $hl)
        $hlBrush.Dispose()
    }

    Draw-Node $g $tl $accent2 $nodeR
    Draw-Node $g $tr $rdp     $nodeR
    Draw-Node $g $bl $ssh     $nodeR

    $g.Dispose()
    return $bmp
}

# Build a multi-frame ICO. Each frame is the PNG bytes of a bitmap.
$sizes  = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms  = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    $bmp.Dispose()
    $frames += [pscustomobject]@{ Size = $s; Bytes = $bytes }
}

# ICONDIR (6 bytes) + ICONDIRENTRY (16 bytes) per frame + payloads.
$fs   = New-Object System.IO.FileStream($outIco, [System.IO.FileMode]::Create)
$bw   = New-Object System.IO.BinaryWriter($fs)

$bw.Write([uint16]0)                  # reserved
$bw.Write([uint16]1)                  # type = 1 (icon)
$bw.Write([uint16]$frames.Count)

$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $w = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $h = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $bw.Write([byte]$w)               # width   (0 = 256)
    $bw.Write([byte]$h)               # height  (0 = 256)
    $bw.Write([byte]0)                # colors in palette
    $bw.Write([byte]0)                # reserved
    $bw.Write([uint16]1)              # color planes
    $bw.Write([uint16]32)             # bits per pixel
    $bw.Write([uint32]$f.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}

foreach ($f in $frames) { $bw.Write($f.Bytes) }

$bw.Flush()
$bw.Dispose()
$fs.Dispose()

Write-Host "Wrote $outIco ($([System.IO.File]::ReadAllBytes($outIco).Length) bytes, $($frames.Count) frames)"
