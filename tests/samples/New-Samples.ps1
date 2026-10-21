# Regenerates the committed test images with GDI+ (System.Drawing), an encoder independent of the WIC code under test.
# Run with Windows PowerShell 5.1:  powershell.exe -NoProfile -File tests\samples\New-Samples.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$dir = $PSScriptRoot

function Save-Png($bmp, $name) { $bmp.Save((Join-Path $dir $name), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose() }
function Save-Jpeg($bmp, $name) {
    $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
    $params = New-Object System.Drawing.Imaging.EncoderParameters 1
    $params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), ([long]95)
    $bmp.Save((Join-Path $dir $name), $codec, $params); $bmp.Dispose()
}
function New-Quadrants {
    # 64x48, opaque: top-left red, top-right green, bottom-left blue, bottom-right yellow.
    $b = New-Object System.Drawing.Bitmap 64, 48, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    for ($y = 0; $y -lt 48; $y++) { for ($x = 0; $x -lt 64; $x++) {
        $c = if ($y -lt 24) { if ($x -lt 32) { [System.Drawing.Color]::FromArgb(255, 0, 0) } else { [System.Drawing.Color]::FromArgb(0, 255, 0) } }
             else { if ($x -lt 32) { [System.Drawing.Color]::FromArgb(0, 0, 255) } else { [System.Drawing.Color]::FromArgb(255, 255, 0) } }
        $b.SetPixel($x, $y, $c) } }
    $b
}

Save-Png (New-Quadrants) 'quadrants-64x48.png'
Save-Jpeg (New-Quadrants) 'quadrants-64x48.jpg'

# 32x32 with alpha: left half fully transparent red, right half 50% (alpha 128) blue.
$a = New-Object System.Drawing.Bitmap 32, 32, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
for ($y = 0; $y -lt 32; $y++) { for ($x = 0; $x -lt 32; $x++) {
    $c = if ($x -lt 16) { [System.Drawing.Color]::FromArgb(0, 255, 0, 0) } else { [System.Drawing.Color]::FromArgb(128, 0, 0, 255) }
    $a.SetPixel($x, $y, $c) } }
Save-Png $a 'alpha-32x32.png'

Get-ChildItem $dir -Include *.png, *.jpg -Recurse | ForEach-Object { '{0}  {1} bytes  {2}' -f $_.Name, $_.Length, (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
