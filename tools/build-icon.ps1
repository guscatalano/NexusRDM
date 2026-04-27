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
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode    = 'AntiAlias'
    $g.InterpolationMode= 'HighQualityBicubic'
    $g.PixelOffsetMode  = 'HighQuality'

    # Rounded-square background.
    $r       = [Math]::Max(2, [int]($size * 0.18))
    $rect    = New-Object System.Drawing.RectangleF(0.5, 0.5, $size-1, $size-1)
    $path    = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $r*2, $r*2, 180, 90)                       | Out-Null
    $path.AddArc($rect.Right - $r*2, $rect.Y, $r*2, $r*2, 270, 90)            | Out-Null
    $path.AddArc($rect.Right - $r*2, $rect.Bottom - $r*2, $r*2, $r*2, 0, 90)  | Out-Null
    $path.AddArc($rect.X, $rect.Bottom - $r*2, $r*2, $r*2, 90, 90)            | Out-Null
    $path.CloseAllFigures()

    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $bg, $panel, 90)
    $g.FillPath($bgBrush, $path)
    $bgBrush.Dispose()

    # Three nodes arranged like an "N": top-left, bottom-left, top-right.
    # The diagonal edge from top-left → bottom-right (running through the
    # bottom node toward the top-right) gives the "N" silhouette.
    $nodeR   = [Math]::Max(1.0, $size * 0.11)
    $pad     = $size * 0.26
    $tl      = New-Object System.Drawing.PointF($pad,           $pad)
    $bl      = New-Object System.Drawing.PointF($pad,           $size - $pad)
    $tr      = New-Object System.Drawing.PointF($size - $pad,   $pad)
    $br      = New-Object System.Drawing.PointF($size - $pad,   $size - $pad)

    # Edges.
    $edgeWidth = [Math]::Max(1.0, $size * 0.06)
    $edgePen   = New-Object System.Drawing.Pen($accent, $edgeWidth)
    $edgePen.StartCap = 'Round'
    $edgePen.EndCap   = 'Round'

    $g.DrawLine($edgePen, $tl, $bl)         # left vertical
    $g.DrawLine($edgePen, $bl, $tr)         # diagonal (the N stroke)
    $g.DrawLine($edgePen, $tr, $br)         # right vertical

    $edgePen.Dispose()

    # Nodes — color-coded: top-left = accent, top-right = RDP blue,
    # bottom-left = SSH green. Reads as "manager of multiple protocols".
    function Draw-Node($p, $color) {
        $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
        $glow.AddEllipse($p.X - $nodeR*1.45, $p.Y - $nodeR*1.45, $nodeR*2.9, $nodeR*2.9) | Out-Null
        $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
        $pgb.CenterColor   = $color
        $pgb.SurroundColors= ,([System.Drawing.Color]::FromArgb(0, $color.R, $color.G, $color.B))
        $g.FillPath($pgb, $glow)
        $pgb.Dispose()
        $glow.Dispose()

        $core = New-Object System.Drawing.RectangleF($p.X - $nodeR, $p.Y - $nodeR, $nodeR*2, $nodeR*2)
        $coreBrush = New-Object System.Drawing.SolidBrush($color)
        $g.FillEllipse($coreBrush, $core)
        $coreBrush.Dispose()

        # Inner highlight for depth.
        $hlR = $nodeR * 0.45
        $hl  = New-Object System.Drawing.RectangleF($p.X - $nodeR*0.4, $p.Y - $nodeR*0.6, $hlR*2, $hlR*2)
        $hlBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(160, 255, 255, 255))
        $g.FillEllipse($hlBrush, $hl)
        $hlBrush.Dispose()
    }

    Draw-Node $tl $accent2
    Draw-Node $tr $rdp
    Draw-Node $bl $ssh

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
