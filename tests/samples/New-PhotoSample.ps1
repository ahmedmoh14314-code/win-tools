# Generates photo-960x720.jpg: a photograph-like scene (sky gradient, soft shapes, hard edges, a line of text, sensor-like
# noise) encoded by GDI+ at quality 93, the way a phone saves a photo. GDI+ is independent of the WIC code under test.
# It exists so the Explorer harness can compress something that real compression applies to (the 64x48 samples are
# too small to save 8 %). Deterministic: fixed shapes and a seeded random generator.
# Run with Windows PowerShell 5.1:  powershell.exe -NoProfile -File tests\samples\New-PhotoSample.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$dir = $PSScriptRoot
$w = 960; $h = 720
$bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

# Sky and ground.
$sky = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Rectangle 0, 0, $w, 420), ([System.Drawing.Color]::FromArgb(70, 120, 200)), ([System.Drawing.Color]::FromArgb(225, 210, 190)), 90.0
$g.FillRectangle($sky, 0, 0, $w, 420)
$ground = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Rectangle 0, 420, $w, 300), ([System.Drawing.Color]::FromArgb(90, 110, 60)), ([System.Drawing.Color]::FromArgb(40, 55, 30)), 90.0
$g.FillRectangle($ground, 0, 420, $w, 300)

# Soft shapes (hills, a sun, foliage) and hard-edged objects (a house, a fence).
$rnd = New-Object System.Random 41
for ($i = 0; $i -lt 60; $i++) {
    $c = [System.Drawing.Color]::FromArgb(70, $rnd.Next(30, 120), $rnd.Next(80, 160), $rnd.Next(20, 80))
    $b = New-Object System.Drawing.SolidBrush $c
    $x = $rnd.Next(-60, $w); $y = $rnd.Next(380, 700); $s = $rnd.Next(40, 160)
    $g.FillEllipse($b, $x, $y, $s, [int]($s * 0.6)); $b.Dispose()
}
$sun = New-Object System.Drawing.Drawing2D.GraphicsPath
$sun.AddEllipse(700, 70, 140, 140)
$glow = New-Object System.Drawing.Drawing2D.PathGradientBrush $sun
$glow.CenterColor = [System.Drawing.Color]::FromArgb(255, 250, 220); $glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 230, 180))
$g.FillPath($glow, $sun)
$wall = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 170, 140))
$roof = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 50, 40))
$g.FillRectangle($wall, 180, 330, 220, 160)
$g.FillPolygon($roof, [System.Drawing.Point[]]@((New-Object System.Drawing.Point 160, 335), (New-Object System.Drawing.Point 290, 250), (New-Object System.Drawing.Point 420, 335)))
$dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(50, 60, 80))
$g.FillRectangle($dark, 220, 370, 50, 50); $g.FillRectangle($dark, 310, 370, 50, 50); $g.FillRectangle($dark, 270, 420, 40, 70)
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(235, 235, 225)), 4
for ($x = 470; $x -lt 940; $x += 28) { $g.DrawLine($pen, $x, 470, $x, 540) }
$g.DrawLine($pen, 460, 485, 940, 485); $g.DrawLine($pen, 460, 520, 940, 520)
$font = New-Object System.Drawing.Font 'Segoe UI', 22, ([System.Drawing.FontStyle]::Bold)
$g.DrawString('WinTools sample 2026', $font, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)), 30, 640)
$g.Dispose()

# Sensor-like noise, luminance and a little colour, from the seeded generator. Light, as a phone's output is after its
# own noise reduction: white noise of +-6 on flat GDI+ gradients made the image behave like a high-ISO shot, which
# Compress rightly reports as already optimized at High and Balanced (measured 2026-10-24).
$rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
$data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadWrite, $bmp.PixelFormat)
$bytes = New-Object byte[] ($data.Stride * $h)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
for ($i = 0; $i -lt $bytes.Length; $i += 3) {
    $n = $rnd.Next(-2, 3)
    for ($k = 0; $k -lt 3; $k++) {
        $v = $bytes[$i + $k] + $n + $rnd.Next(-1, 2)
        $bytes[$i + $k] = [byte][Math]::Max(0, [Math]::Min(255, $v))
    }
}
[System.Runtime.InteropServices.Marshal]::Copy($bytes, 0, $data.Scan0, $bytes.Length)
$bmp.UnlockBits($data)

$codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$params = New-Object System.Drawing.Imaging.EncoderParameters 1
$params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), ([long]93)
$out = Join-Path $dir 'photo-960x720.jpg'
$bmp.Save($out, $codec, $params); $bmp.Dispose()
$f = Get-Item $out
'{0}  {1} bytes  {2}' -f $f.Name, $f.Length, (Get-FileHash $f.FullName -Algorithm SHA256).Hash
