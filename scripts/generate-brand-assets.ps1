[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repoRoot = Split-Path -Parent $PSScriptRoot
$assetsPath = Join-Path $repoRoot 'winuitest\Assets'
$controlsPath = Join-Path $repoRoot 'winuitest\Controls'
$invariant = [Globalization.CultureInfo]::InvariantCulture

# One vector definition feeds SVG, first-frame WinUI geometry and every shell
# bitmap. Coordinates use a 256px artboard; no decoder or icon font is needed
# to paint the startup mark. Keep paths to absolute M/L/C/Z commands.
$brandRed = '#E51B23'
$shapes = @(
    @{ Id='Background'; Fill=$brandRed; Path='M60 4 L196 4 C226.928 4 252 29.072 252 60 L252 196 C252 226.928 226.928 252 196 252 L60 252 C29.072 252 4 226.928 4 196 L4 60 C4 29.072 29.072 4 60 4 Z' },
    @{ Id='Tail'; Stroke='#FFFFFF'; StrokeWidth=12; Path='M151 188 C174 204 195 194 195 176 C195 164 183 159 178 168' },
    @{ Id='Body'; Fill='#FFFFFF'; Path='M102 139 C92 156 90 178 94 200 C96 210 107 214 128 214 C149 214 160 210 162 200 C166 178 164 157 154 139 Z' },
    @{ Id='Head'; Fill='#FFFFFF'; Path='M82 88 C80 77 78 61 85 52 C87 49 104 61 112 70 C122 67 136 67 146 70 C154 61 171 49 174 52 C181 61 179 77 177 88 C185 99 188 112 181 127 C174 143 154 151 130 151 C105 151 85 143 78 127 C72 112 75 98 82 88 Z' },
    @{ Id='LeftEar'; Fill=$brandRed; Path='M89 64 C88 71 89 79 90 82 L102 77 C98 72 94 67 89 64 Z' },
    @{ Id='RightEar'; Fill=$brandRed; Path='M169 64 C170 71 169 79 168 82 L156 77 C160 72 164 67 169 64 Z' },
    @{ Id='LeftEye'; Stroke=$brandRed; StrokeWidth=4.8; Path='M99 106 C103 101 110 101 114 106' },
    @{ Id='RightEye'; Stroke=$brandRed; StrokeWidth=4.8; Path='M142 106 C146 101 153 101 157 106' },
    @{ Id='Nose'; Fill=$brandRed; Path='M124 117 C126 115 130 115 132 117 C133 119 129 123 128 123 C127 123 123 119 124 117 Z' },
    @{ Id='Smile'; Stroke=$brandRed; StrokeWidth=3.5; Path='M128 123 L128 128 C125 132 120 132 117 129 M128 128 C131 132 136 132 139 129' },
    @{ Id='Paws'; Stroke=$brandRed; StrokeWidth=3.5; Path='M117 178 C116 189 116 197 118 204 M139 178 C140 189 140 197 138 204' },
    @{ Id='LeftWhiskers'; Stroke='#FFFFFF'; StrokeWidth=5.5; Path='M91 110 C75 104 57 99 40 96 M89 118 C72 118 54 118 37 119 M91 126 C75 132 58 138 42 144' },
    @{ Id='RightWhiskers'; Stroke='#FFFFFF'; StrokeWidth=5.5; Path='M165 110 C181 104 199 99 216 96 M167 118 C184 118 202 118 219 119 M165 126 C181 132 198 138 214 144' }
)

function Get-ShapeMarkup($Shape, [ValidateSet('svg','xaml')][string]$Format) {
    $attributes = [Collections.Generic.List[string]]::new()
    if ($Format -eq 'svg') {
        $attributes.Add('d="' + $Shape.Path + '"')
        $attributes.Add('fill="' + $(if ($Shape.Fill) { $Shape.Fill } else { 'none' }) + '"')
        if ($Shape.Stroke) {
            $attributes.Add('stroke="' + $Shape.Stroke + '"')
            $attributes.Add('stroke-width="' + $Shape.StrokeWidth.ToString($invariant) + '"')
            $attributes.Add('stroke-linecap="round" stroke-linejoin="round"')
        }
        return '  <path ' + ($attributes -join ' ') + '/>'
    }
    $attributes.Add('Data="' + $Shape.Path + '"')
    if ($Shape.Fill) { $attributes.Add('Fill="' + $Shape.Fill + '"') }
    if ($Shape.Stroke) {
        $attributes.Add('Stroke="' + $Shape.Stroke + '"')
        $attributes.Add('StrokeThickness="' + $Shape.StrokeWidth.ToString($invariant) + '"')
        $attributes.Add('StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"')
    }
    return '            <Path ' + ($attributes -join ' ') + ' />'
}

$svgPaths = ($shapes | ForEach-Object { Get-ShapeMarkup $_ svg }) -join "`n"
@"
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <title>Luma Music</title>
$svgPaths
</svg>
"@ | Set-Content -LiteralPath (Join-Path $assetsPath 'Luma.svg') -Encoding utf8

$xamlPaths = ($shapes | ForEach-Object { Get-ShapeMarkup $_ xaml }) -join "`n"
@"
<UserControl
    x:Class="winuitest.Controls.LumaBrandMark"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    IsTabStop="False" IsHitTestVisible="False">
    <!-- Generated from scripts/generate-brand-assets.ps1. -->
    <Viewbox Stretch="Uniform">
        <Canvas Width="256" Height="256">
$xamlPaths
        </Canvas>
    </Viewbox>
</UserControl>
"@ | Set-Content -LiteralPath (Join-Path $controlsPath 'LumaBrandMark.xaml') -Encoding utf8
@'
using Microsoft.UI.Xaml.Controls;

namespace winuitest.Controls;

/// <summary>The shared vector brand mark, generated with the Windows shell assets.</summary>
public sealed partial class LumaBrandMark : UserControl
{
    public LumaBrandMark() => InitializeComponent();
}
'@ | Set-Content -LiteralPath (Join-Path $controlsPath 'LumaBrandMark.xaml.cs') -Encoding utf8

function ConvertTo-DrawingPath([string]$Data) {
    $tokens = [regex]::Matches($Data, '[MLCZ]|-?\d+(?:\.\d+)?') | ForEach-Object { $_.Value }
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    [single]$currentX = 0; [single]$currentY = 0
    [single]$startX = 0; [single]$startY = 0
    for ($index = 0; $index -lt $tokens.Count;) {
        $command = $tokens[$index++]
        switch ($command) {
            'M' {
                $path.StartFigure()
                $currentX = [single]::Parse($tokens[$index++], $invariant)
                $currentY = [single]::Parse($tokens[$index++], $invariant)
                $startX = $currentX; $startY = $currentY
            }
            'L' {
                $x = [single]::Parse($tokens[$index++], $invariant)
                $y = [single]::Parse($tokens[$index++], $invariant)
                $path.AddLine($currentX, $currentY, $x, $y)
                $currentX = $x; $currentY = $y
            }
            'C' {
                $x1 = [single]::Parse($tokens[$index++], $invariant)
                $y1 = [single]::Parse($tokens[$index++], $invariant)
                $x2 = [single]::Parse($tokens[$index++], $invariant)
                $y2 = [single]::Parse($tokens[$index++], $invariant)
                $x = [single]::Parse($tokens[$index++], $invariant)
                $y = [single]::Parse($tokens[$index++], $invariant)
                $path.AddBezier($currentX, $currentY, $x1, $y1, $x2, $y2, $x, $y)
                $currentX = $x; $currentY = $y
            }
            'Z' { $path.CloseFigure(); $currentX = $startX; $currentY = $startY }
            default { $path.Dispose(); throw "Unsupported brand path command: $command" }
        }
    }
    return $path
}

# Supersampling preserves the curved whiskers and round stroke caps at shell
# sizes, including the 16px and 24px entries in the multi-resolution ICO.
function New-LumaBitmap([int]$Width, [int]$Height, [int]$MarkSize) {
    $scale = 4
    $bitmap = [System.Drawing.Bitmap]::new($Width * $scale, $Height * $scale)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.TranslateTransform((($Width - $MarkSize) / 2) * $scale, (($Height - $MarkSize) / 2) * $scale)
        $graphics.ScaleTransform(($MarkSize / 256) * $scale, ($MarkSize / 256) * $scale)
        foreach ($shape in $shapes) {
            $path = ConvertTo-DrawingPath $shape.Path
            try {
                if ($shape.Fill) {
                    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($shape.Fill))
                    try { $graphics.FillPath($brush, $path) } finally { $brush.Dispose() }
                }
                if ($shape.Stroke) {
                    $pen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml($shape.Stroke), [single]$shape.StrokeWidth)
                    $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
                    try { $graphics.DrawPath($pen, $path) } finally { $pen.Dispose() }
                }
            } finally { $path.Dispose() }
        }
    } finally { $graphics.Dispose() }
    $output = [System.Drawing.Bitmap]::new($Width, $Height)
    $smallGraphics = [System.Drawing.Graphics]::FromImage($output)
    try {
        $smallGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $smallGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $smallGraphics.DrawImage($bitmap, 0, 0, $Width, $Height)
    } finally { $smallGraphics.Dispose(); $bitmap.Dispose() }
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
    try { $logoBitmap.Save((Join-Path $assetsPath $spec.Name), [System.Drawing.Imaging.ImageFormat]::Png) }
    finally { $logoBitmap.Dispose() }
}

$iconEntries = foreach ($iconSize in @(16, 24, 32, 48, 64, 128, 256)) {
    $iconBitmap = New-LumaBitmap $iconSize $iconSize $iconSize
    $pngBuffer = [System.IO.MemoryStream]::new()
    try {
        $iconBitmap.Save($pngBuffer, [System.Drawing.Imaging.ImageFormat]::Png)
        [PSCustomObject]@{ Size = $iconSize; Data = $pngBuffer.ToArray() }
    } finally { $pngBuffer.Dispose(); $iconBitmap.Dispose() }
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
} finally { $writer.Dispose() }
Write-Host 'Luma cat SVG, WinUI vector mark, multi-resolution icon and MSIX logos are ready.'
