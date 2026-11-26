param(
    [Parameter(Mandatory)] [string]$Source,
    [Parameter(Mandatory)] [string]$OutDir
)
# Genera PhantomPad.ico (color), White.ico y Black.ico (siluetas para la bandeja) y una vista previa.
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
New-Item -ItemType Directory -Force $OutDir | Out-Null

function Resize-Bitmap([System.Drawing.Image]$img, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.DrawImage($img, 0, 0, $size, $size)
    $g.Dispose()
    return $bmp
}

# Silueta: solo el cuerpo claro del fantasma (luminancia alta), pintado de un color sólido.
function New-Silhouette([System.Drawing.Bitmap]$src, [System.Drawing.Color]$color) {
    $w = $src.Width; $h = $src.Height
    $out = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            $p = $src.GetPixel($x, $y)
            $lum = 0.299 * $p.R + 0.587 * $p.G + 0.114 * $p.B
            # rampa suave entre 90 y 150 de luminancia para bordes limpios
            $t = [Math]::Min(1.0, [Math]::Max(0.0, ($lum - 90) / 60.0))
            $a = [int]([Math]::Round($t * $p.A))
            $out.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($a, $color.R, $color.G, $color.B))
        }
    }
    return (Get-CroppedSquare $out)
}

# Recorta al contorno visible y lo centra en un cuadrado con un margen pequeño,
# para que la silueta llene el icono de la bandeja (16 px).
function Get-CroppedSquare([System.Drawing.Bitmap]$bmp) {
    $minX = $bmp.Width; $minY = $bmp.Height; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $bmp.Height; $y++) {
        for ($x = 0; $x -lt $bmp.Width; $x++) {
            if ($bmp.GetPixel($x, $y).A -gt 20) {
                if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    $w = $maxX - $minX + 1; $h = $maxY - $minY + 1
    [int]$side = [Math]::Ceiling([Math]::Max($w, $h) * 1.08)
    $sq = New-Object System.Drawing.Bitmap $side, $side, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($sq)
    [int]$dx = [Math]::Floor(($side - $w) / 2); [int]$dy = [Math]::Floor(($side - $h) / 2)
    $g.DrawImage($bmp, (New-Object System.Drawing.Rectangle $dx, $dy, $w, $h), (New-Object System.Drawing.Rectangle $minX, $minY, $w, $h), [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    return (Resize-Bitmap $sq 256)
}

function Save-Ico([System.Drawing.Bitmap[]]$bitmaps, [string]$path) {
    $pngs = foreach ($b in $bitmaps) {
        $ms = New-Object System.IO.MemoryStream
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        , $ms.ToArray()
    }
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$bitmaps.Count)
    $offset = 6 + 16 * $bitmaps.Count
    for ($i = 0; $i -lt $bitmaps.Count; $i++) {
        $s = $bitmaps[$i].Width
        $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
        $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    $bw.Close(); $fs.Close()
}

$srcImg = [System.Drawing.Image]::FromFile($Source)
$big = Resize-Bitmap $srcImg 256
$whiteBig = New-Silhouette $big ([System.Drawing.Color]::White)
$blackBig = New-Silhouette $big ([System.Drawing.Color]::Black)

$color = foreach ($s in $sizes) { Resize-Bitmap $srcImg $s }
$white = foreach ($s in $sizes) { Resize-Bitmap $whiteBig $s }
$black = foreach ($s in $sizes) { Resize-Bitmap $blackBig $s }

Save-Ico $color (Join-Path $OutDir 'PhantomPad.ico')
Save-Ico $white (Join-Path $OutDir 'PhantomPad-White.ico')
Save-Ico $black (Join-Path $OutDir 'PhantomPad-Black.ico')

# Vista previa: 3 filas (color / blanco sobre oscuro / negro sobre claro) a 16, 24, 32, 48, 64, 128 px
$show = 16, 24, 32, 48, 64, 128
$pad = 16; $rowH = 128 + 2 * $pad
[int]$width = ($show | Measure-Object -Sum).Sum + $pad * ($show.Count + 1)
[int]$height = $rowH * 3
$prev = New-Object System.Drawing.Bitmap $width, $height
$g = [System.Drawing.Graphics]::FromImage($prev)
$bgs = [System.Drawing.Color]::FromArgb(255, 32, 32, 32), [System.Drawing.Color]::FromArgb(255, 32, 32, 32), [System.Drawing.Color]::FromArgb(255, 240, 240, 240)
$sets = $color, $white, $black
for ($r = 0; $r -lt 3; $r++) {
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $bgs[$r]), 0, $r * $rowH, $width, $rowH)
    $x = $pad
    foreach ($s in $show) {
        $bmp = $sets[$r][[Array]::IndexOf($sizes, $s)]
        $g.DrawImageUnscaled($bmp, $x, $r * $rowH + $pad + (128 - $s))
        $x += $s + $pad
    }
}
$g.Dispose()
$prev.Save((Join-Path $OutDir 'preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$srcImg.Dispose()
Get-ChildItem $OutDir | Select-Object Name, Length
