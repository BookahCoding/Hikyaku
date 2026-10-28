# Renders the app icon (src/Hikyaku.App/Assets/Hikyaku.ico) from the same mark as the maintainer's Android app (BookahHermes
# ic_launcher_foreground_paper: washi-paper "H" on Ukiyo-e vermilion). Re-run only if the mark changes; the .ico is committed.
#   ./scripts/make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
Set-Location (Split-Path $PSScriptRoot -Parent)

$paper = [Windows.Media.Color]::FromRgb(0xF4, 0xED, 0xD9)
$vermilion = [Windows.Media.Color]::FromRgb(0xA6, 0x3F, 0x2D)
$mark = @(
  'M38.5,30a5.5,5.5 0,0 1,5.5 5.5v37a5.5,5.5 0,0 1,-5.5 5.5a5.5,5.5 0,0 1,-5.5 -5.5v-37a5.5,5.5 0,0 1,5.5 -5.5z',
  'M69.5,30a5.5,5.5 0,0 1,5.5 5.5v37a5.5,5.5 0,0 1,-5.5 5.5a5.5,5.5 0,0 1,-5.5 -5.5v-37a5.5,5.5 0,0 1,5.5 -5.5z',
  'M38,43h18a5,5 0,0 1,5 5a5,5 0,0 1,-5 5h-18a5,5 0,0 1,-5 -5a5,5 0,0 1,5 -5z',
  'M52,55h18a5,5 0,0 1,5 5a5,5 0,0 1,-5 5h-18a5,5 0,0 1,-5 -5a5,5 0,0 1,5 -5z'
)

function Render([int]$size) {
  $dv = New-Object Windows.Media.DrawingVisual
  $dc = $dv.RenderOpen()
  $r = $size * 0.22
  $dc.DrawRoundedRectangle((New-Object Windows.Media.SolidColorBrush $vermilion), $null, (New-Object Windows.Rect 0, 0, $size, $size), $r, $r)
  # The Android mark sits in a 108-unit grid with a 66-unit safe zone; show the middle 70 units so it fills the tile.
  $scale = $size / 70.0
  $dc.PushTransform((New-Object Windows.Media.MatrixTransform ([Windows.Media.Matrix]::new($scale, 0, 0, $scale, -19 * $scale, -19 * $scale))))
  foreach ($d in $mark) { $dc.DrawGeometry((New-Object Windows.Media.SolidColorBrush $paper), $null, [Windows.Media.Geometry]::Parse($d)) }
  $dc.Pop()
  $dc.Close()
  $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
  $bmp.Render($dv)
  $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
  $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
  $ms = New-Object IO.MemoryStream
  $enc.Save($ms)
  return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { ,(Render $s) }
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)       # ICONDIR
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {                                        # ICONDIRENTRY (PNG frames)
  $s = $sizes[$i]; $len = $images[$i].Length
  $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
  $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$len); $w.Write([uint32]$offset); $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path (Get-Location) 'src/Hikyaku.App/Assets/Hikyaku.ico'), $out.ToArray())
[IO.File]::WriteAllBytes((Join-Path (Get-Location) 'docs/images/icon-256.png'), $images[-1])
Write-Host "Icon written: src/Hikyaku.App/Assets/Hikyaku.ico ($($sizes -join ', ') px)"
