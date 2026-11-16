$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$iconSizes = @(16, 24, 32, 48, 64, 128, 256)
$iconImages = [Collections.Generic.List[byte[]]]::new()
foreach ($size in $iconSizes) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size / 256.0, $size / 256.0)
    $shape = [Drawing.Drawing2D.GraphicsPath]::new()
    foreach ($arc in @(@(8, 8, 48, 48, 180, 90), @(200, 8, 48, 48, 270, 90), @(200, 200, 48, 48, 0, 90), @(8, 200, 48, 48, 90, 90))) {
        $shape.AddArc([single]$arc[0], [single]$arc[1], [single]$arc[2], [single]$arc[3], [single]$arc[4], [single]$arc[5])
    }
    $shape.CloseFigure()
    $blue = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(34, 75, 149))
    $white = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(242, 247, 255))
    $green = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(83, 221, 167))
    $pen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(34, 75, 149), 11)
    $pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $graphics.FillPath($blue, $shape)
    $graphics.FillEllipse($white, 46, 39, 144, 144)
    $graphics.FillEllipse($blue, 92, 24, 128, 128)
    $graphics.FillEllipse($green, 147, 146, 70, 70)
    $graphics.DrawLines($pen, [Drawing.PointF[]]@([Drawing.PointF]::new(164, 180), [Drawing.PointF]::new(176, 191), [Drawing.PointF]::new(200, 167)))
    $png = [IO.MemoryStream]::new()
    $bitmap.Save($png, [Drawing.Imaging.ImageFormat]::Png)
    $iconImages.Add($png.ToArray())
    $png.Dispose(); $pen.Dispose(); $green.Dispose(); $white.Dispose(); $blue.Dispose(); $shape.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$iconFile = [IO.File]::Create((Join-Path $PSScriptRoot 'app.ico'))
$writer = [IO.BinaryWriter]::new($iconFile)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$iconSizes.Count)
    $offset = 6 + 16 * $iconSizes.Count
    for ($i = 0; $i -lt $iconSizes.Count; $i++) {
        $dimension = if ($iconSizes[$i] -eq 256) { 0 } else { $iconSizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$iconImages[$i].Length); $writer.Write([uint32]$offset)
        $offset += $iconImages[$i].Length
    }
    foreach ($pngBytes in $iconImages) { $writer.Write([byte[]]$pngBytes) }
} finally { $writer.Dispose() }
