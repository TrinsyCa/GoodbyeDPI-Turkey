# Kurulum sihirbazinin gorsellerini GoodbyeDPI'in orijinal logosundan (src\icon.ico) uretir.
#   Banner.bmp (493x58)  : her sayfanin ust seridi. WixUI basligi solda siyah yazdigi icin zemin beyaz, logo sagda.
#   Dialog.bmp (493x312) : karsilama/bitis sayfasi. Sol 164 px panel gorsel, sag taraf beyaz (metin oraya yazilir).
# Logo degisirse yeniden calistirin:  .\gui\installer\make-bitmaps.ps1
Add-Type -AssemblyName System.Drawing, PresentationCore, WindowsBase
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$iconPath = Join-Path $repo 'src\icon.ico'

# icon.ico icindeki goruntuler PNG sikistirmali; System.Drawing.Icon bunlari cizemiyor.
# WPF kod cozucusuyle istenen boyuttaki kareyi alip System.Drawing goruntusune cevir.
function Get-Logo([int]$size) {
    $stream = [IO.File]::OpenRead($iconPath)
    try {
        $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create($stream,
            [System.Windows.Media.Imaging.BitmapCreateOptions]::None, [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        $frame = $decoder.Frames | Sort-Object { [math]::Abs($_.PixelWidth - $size) } | Select-Object -First 1
        $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($frame))
        $png = New-Object IO.MemoryStream
        $encoder.Save($png)
        return [System.Drawing.Image]::FromStream($png)
    } finally {
        $stream.Dispose()
    }
}

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.TextRenderingHint = 'ClearTypeGridFit'
    return $bmp, $g
}

# --- Banner ---
$bmp, $g = New-Canvas 493 58
$g.Clear([System.Drawing.Color]::White)
$g.DrawImage((Get-Logo 48), (New-Object System.Drawing.Rectangle (493 - 48 - 10), 5, 48, 48))
$g.Dispose()
$bmp.Save((Join-Path $PSScriptRoot 'Banner.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$bmp.Dispose()

# --- Karsilama / bitis ---
$bmp, $g = New-Canvas 493 312
$g.Clear([System.Drawing.Color]::White)
$panel = New-Object System.Drawing.Rectangle 0, 0, 164, 312
$gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush $panel,
    ([System.Drawing.Color]::FromArgb(0x2A, 0x1B, 0x4D)), ([System.Drawing.Color]::FromArgb(0x0B, 0x0C, 0x10)), 70
$g.FillRectangle($gradient, $panel)
# Logonun arkasinda yumusak isik
$glow = New-Object System.Drawing.Drawing2D.GraphicsPath
$glow.AddEllipse(22, 52, 120, 120)
$glowBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
$glowBrush.CenterColor = [System.Drawing.Color]::FromArgb(110, 0x8B, 0x5C, 0xF6)
$glowBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0x8B, 0x5C, 0xF6))
$g.FillPath($glowBrush, $glow)
$g.DrawImage((Get-Logo 64), (New-Object System.Drawing.Rectangle 50, 80, 64, 64))

$center = New-Object System.Drawing.StringFormat
$center.Alignment = 'Center'
$white = [System.Drawing.Brushes]::White
$muted = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x9A, 0xA0, 0xAC))
$g.DrawString('GoodbyeDPI', (New-Object System.Drawing.Font 'Segoe UI', 15, ([System.Drawing.FontStyle]::Bold)), $white, (New-Object System.Drawing.RectangleF 0, 162, 164, 30), $center)
$g.DrawString('Turkey', (New-Object System.Drawing.Font 'Segoe UI', 11), $muted, (New-Object System.Drawing.RectangleF 0, 190, 164, 24), $center)
$g.DrawString('Windows Kontrol Paneli', (New-Object System.Drawing.Font 'Segoe UI', 8), $muted, (New-Object System.Drawing.RectangleF 0, 276, 164, 20), $center)
$g.Dispose()
$bmp.Save((Join-Path $PSScriptRoot 'Dialog.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$bmp.Dispose()
Write-Host "Banner.bmp ve Dialog.bmp olusturuldu."
