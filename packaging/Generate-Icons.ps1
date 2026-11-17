$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'Assets'
[IO.Directory]::CreateDirectory($assetDir) | Out-Null
$canvas = New-Object Drawing.Bitmap 128,128
$g = [Drawing.Graphics]::FromImage($canvas)
$g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([Drawing.Color]::Transparent)
$blue = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(30,91,166)),7
$fill = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(225,239,252))
$orange = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(235,128,30)),8
$orange.StartCap = [Drawing.Drawing2D.LineCap]::Round
$orange.EndCap = [Drawing.Drawing2D.LineCap]::ArrowAnchor
$centre = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(24,135,117)),4
$centre.DashStyle = [Drawing.Drawing2D.DashStyle]::Dash
$g.FillRectangle($fill,24,32,70,62)
$g.FillEllipse($fill,76,32,26,62)
$g.DrawLine($blue,24,32,88,32)
$g.DrawLine($blue,24,94,88,94)
$g.DrawArc($blue,10,32,28,62,90,180)
$g.DrawEllipse($blue,76,32,26,62)
$g.DrawLine($centre,8,63,119,63)
$g.DrawLine($orange,60,9,60,49)
$g.DrawLine($orange,60,119,60,77)
foreach($size in @(16,32,128)) {
    $small = New-Object Drawing.Bitmap $size,$size
    $sg = [Drawing.Graphics]::FromImage($small)
    $sg.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $sg.DrawImage($canvas,0,0,$size,$size)
    $small.Save((Join-Path $assetDir "RoundAlign$size.png"),[Drawing.Imaging.ImageFormat]::Png)
    $sg.Dispose(); $small.Dispose()
}
$iconPath = Join-Path $assetDir 'RoundAlign.ico'
$stream = [IO.File]::Create($iconPath)
$writer = New-Object IO.BinaryWriter $stream
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]2)
$offset = 6 + 16 * 2
$payloads = @()
foreach($size in @(16,32)) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $assetDir "RoundAlign$size.png"))
    $payloads += ,$bytes
    $writer.Write([byte]$size); $writer.Write([byte]$size)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$bytes.Length); $writer.Write([uint32]$offset)
    $offset += $bytes.Length
}
foreach($bytes in $payloads) { $writer.Write([byte[]]$bytes) }
$writer.Dispose(); $stream.Dispose()
$blue.Dispose(); $orange.Dispose(); $centre.Dispose(); $fill.Dispose(); $g.Dispose(); $canvas.Dispose()

# Rectangular duct icon, distinct from the round command.
foreach ($size in @(16,32,128)) {
    $bitmap = New-Object Drawing.Bitmap $size,$size
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.ScaleTransform(($size / 32.0),($size / 32.0))
    $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(30,91,166)),2
    $axisPen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(24,135,117)),1
    $axisPen.DashStyle = [Drawing.Drawing2D.DashStyle]::Dash
    $arrowPen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(235,128,30)),2
    $arrowPen.EndCap = [Drawing.Drawing2D.LineCap]::ArrowAnchor
    $graphics.FillRectangle([Drawing.Brushes]::AliceBlue,5,10,22,12)
    $graphics.DrawRectangle($pen,5,10,22,12)
    $graphics.DrawLine($pen,21,10,21,22)
    $graphics.DrawLine($axisPen,1,16,31,16)
    $graphics.DrawLine($arrowPen,15,1,15,12)
    $graphics.DrawLine($arrowPen,15,31,15,20)
    $bitmap.Save((Join-Path $assetDir "RecAlign$size.png"),[Drawing.Imaging.ImageFormat]::Png)
    $pen.Dispose(); $axisPen.Dispose(); $arrowPen.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
