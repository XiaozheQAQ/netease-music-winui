[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetsPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'winuitest\Assets'

# This is a code-native vector mark. Draw at 4x resolution, then downsample
# for small taskbar sizes; no external imagery or image generation is used.
function New-LumaBitmap([int]$Width, [int]$Height, [int]$MarkSize) {
    $scale = 4
    $bitmap = [System.Drawing.Bitmap]::new($Width * $scale, $Height * $scale)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TranslateTransform((($Width - $MarkSize) / 2) * $scale, (($Height - $MarkSize) / 2) * $scale)
    $graphics.ScaleTransform(($MarkSize / 256) * $scale, ($MarkSize / 256) * $scale)

    $outline = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $outline.AddArc(4, 4, 112, 112, 180, 90)
    $outline.AddArc(140, 4, 112, 112, 270, 90)
    $outline.AddArc(140, 140, 112, 112, 0, 90)
    $outline.AddArc(4, 140, 112, 112, 90, 90)
    $outline.CloseFigure()
    $pink = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Point]::new(4, 4), [System.Drawing.Point]::new(252, 252),
        [System.Drawing.ColorTranslator]::FromHtml('#F4617E'),
        [System.Drawing.ColorTranslator]::FromHtml('#E94764'))
    $graphics.FillPath($pink, $outline)

    $note = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $note.AddLine(111, 82, 111, 81)
    $note.AddBezier(111, 81, 111, 77, 112, 76, 117, 75)
    $note.AddLine(117, 75, 184, 59)
    $note.AddBezier(184, 59, 188, 58, 190, 59, 190, 65)
    $note.AddLine(190, 65, 190, 158)
    $note.AddLine(190, 158, 178, 158)
    $note.AddLine(178, 158, 178, 88)
    $note.AddLine(178, 88, 123, 101)
    $note.AddLine(123, 101, 123, 181)
    $note.AddLine(123, 181, 111, 181)
    $note.CloseFigure()
    $graphics.FillPath([System.Drawing.Brushes]::White, $note)
    foreach ($head in @(@(101, 181), @(168, 158))) {
        $headPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $headPath.AddEllipse(($head[0] - 25), ($head[1] - 17), 50, 34)
        $rotation = [System.Drawing.Drawing2D.Matrix]::new()
        $rotation.RotateAt(-20, [System.Drawing.PointF]::new($head[0], $head[1]))
        $headPath.Transform($rotation)
        $graphics.FillPath([System.Drawing.Brushes]::White, $headPath)
        $rotation.Dispose()
        $headPath.Dispose()
    }
    $graphics.Dispose()
    $outline.Dispose()
    $pink.Dispose()
    $note.Dispose()

    $output = [System.Drawing.Bitmap]::new($Width, $Height)
    $smallGraphics = [System.Drawing.Graphics]::FromImage($output)
    $smallGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $smallGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $smallGraphics.DrawImage($bitmap, 0, 0, $Width, $Height)
    $smallGraphics.Dispose()
    $bitmap.Dispose()
    return $output
}

$logoSpecs = @(
    @{ Name = 'Luma.png'; Width = 256; Height = 256; Mark = 256 },
    @{ Name = 'Square150x150Logo.scale-200.png'; Width = 300; Height = 300; Mark = 260 },
    @{ Name = 'Square44x44Logo.scale-200.png'; Width = 88; Height = 88; Mark = 88 },
    @{ Name = 'Square44x44Logo.targetsize-24_altform-unplated.png'; Width = 24; Height = 24; Mark = 24 },
    @{ Name = 'StoreLogo.png'; Width = 50; Height = 50; Mark = 50 },
    @{ Name = 'LockScreenLogo.scale-200.png'; Width = 48; Height = 48; Mark = 48 },
    @{ Name = 'Wide310x150Logo.scale-200.png'; Width = 620; Height = 300; Mark = 230 },
    @{ Name = 'SplashScreen.scale-200.png'; Width = 1240; Height = 600; Mark = 200 }
)
foreach ($spec in $logoSpecs) {
    $logoBitmap = New-LumaBitmap $spec.Width $spec.Height $spec.Mark
    $logoBitmap.Save((Join-Path $assetsPath $spec.Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $logoBitmap.Dispose()
}

# A Windows icon containing native PNG entries at all common shell resolutions.
$iconEntries = foreach ($iconSize in @(16, 24, 32, 48, 64, 128, 256)) {
    $iconBitmap = New-LumaBitmap $iconSize $iconSize $iconSize
    $pngBuffer = [System.IO.MemoryStream]::new()
    $iconBitmap.Save($pngBuffer, [System.Drawing.Imaging.ImageFormat]::Png)
    [PSCustomObject]@{ Size = $iconSize; Data = $pngBuffer.ToArray() }
    $pngBuffer.Dispose()
    $iconBitmap.Dispose()
}
$iconFile = [System.IO.File]::Create((Join-Path $assetsPath 'Luma.ico'))
$writer = [System.IO.BinaryWriter]::new($iconFile)
try {
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$iconEntries.Count)
    $offset = 6 + 16 * $iconEntries.Count
    foreach ($entry in $iconEntries) {
        $sizeByte = if ($entry.Size -eq 256) { 0 } else { $entry.Size }
        $writer.Write([Byte]$sizeByte)
        $writer.Write([Byte]$sizeByte)
        $writer.Write([Byte]0)
        $writer.Write([Byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$entry.Data.Length)
        $writer.Write([UInt32]$offset)
        $offset += $entry.Data.Length
    }
    foreach ($entry in $iconEntries) { $writer.Write([Byte[]]$entry.Data) }
}
finally { $writer.Dispose() }
Write-Host 'Luma SVG mark, multi-resolution icon and MSIX logos are ready.'
