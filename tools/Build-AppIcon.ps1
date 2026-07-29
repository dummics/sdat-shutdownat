[CmdletBinding()]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot "..\assets\sdat_logo.png"),
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\assets\sdat_logo.ico"),
    [string]$PreviewPath = (Join-Path $PSScriptRoot "..\assets\sdat_logo_256.png"),
    [ValidateRange(0, 0.5)]
    [double]$ContentPaddingRatio = 0.05
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Add-Type -AssemblyName PresentationCore

$sourceFull = (Resolve-Path -LiteralPath $SourcePath).Path
$outputFull = [IO.Path]::GetFullPath($OutputPath)
$previewFull = [IO.Path]::GetFullPath($PreviewPath)
$outputDirectory = Split-Path -Parent $outputFull
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$decoder = [Windows.Media.Imaging.BitmapDecoder]::Create(
    [Uri]$sourceFull,
    [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
    [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
$source = $decoder.Frames[0]

# Trim transparent canvas before generating Windows icon sizes. The canonical
# artwork intentionally has generous presentation padding, but keeping that
# padding in a 16 px tray icon makes the actual mark unreadably small.
$formatted = [Windows.Media.Imaging.FormatConvertedBitmap]::new()
$formatted.BeginInit()
$formatted.Source = $source
$formatted.DestinationFormat = [Windows.Media.PixelFormats]::Bgra32
$formatted.EndInit()
$stride = $formatted.PixelWidth * 4
$pixels = [byte[]]::new($stride * $formatted.PixelHeight)
$formatted.CopyPixels($pixels, $stride, 0)
$minX = $formatted.PixelWidth
$minY = $formatted.PixelHeight
$maxX = -1
$maxY = -1
for ($y = 0; $y -lt $formatted.PixelHeight; $y++) {
    for ($x = 0; $x -lt $formatted.PixelWidth; $x++) {
        if ($pixels[($y * $stride) + ($x * 4) + 3] -le 8) { continue }
        if ($x -lt $minX) { $minX = $x }
        if ($x -gt $maxX) { $maxX = $x }
        if ($y -lt $minY) { $minY = $y }
        if ($y -gt $maxY) { $maxY = $y }
    }
}
if ($maxX -lt $minX -or $maxY -lt $minY) {
    throw "The source logo contains no visible pixels."
}

$contentWidth = $maxX - $minX + 1
$contentHeight = $maxY - $minY + 1
$padding = [Math]::Ceiling([Math]::Max($contentWidth, $contentHeight) * $ContentPaddingRatio)
$cropSize = [Math]::Min(
    [Math]::Min($source.PixelWidth, $source.PixelHeight),
    [Math]::Max($contentWidth, $contentHeight) + (2 * $padding))
$centerX = ($minX + $maxX + 1) / 2
$centerY = ($minY + $maxY + 1) / 2
$cropX = [Math]::Clamp([Math]::Floor($centerX - ($cropSize / 2)), 0, $source.PixelWidth - $cropSize)
$cropY = [Math]::Clamp([Math]::Floor($centerY - ($cropSize / 2)), 0, $source.PixelHeight - $cropSize)
$source = [Windows.Media.Imaging.CroppedBitmap]::new(
    $source,
    [Windows.Int32Rect]::new($cropX, $cropY, $cropSize, $cropSize))

$sizes = @(256, 128, 64, 48, 32, 24, 16)
$images = [Collections.Generic.List[byte[]]]::new()

foreach ($size in $sizes) {
    $scaleX = $size / [double]$source.PixelWidth
    $scaleY = $size / [double]$source.PixelHeight
    $transform = [Windows.Media.ScaleTransform]::new($scaleX, $scaleY)
    $resized = [Windows.Media.Imaging.TransformedBitmap]::new($source, $transform)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($resized))
    $stream = [IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        $images.Add($stream.ToArray())
    }
    finally {
        $stream.Dispose()
    }
}

[IO.File]::WriteAllBytes($previewFull, $images[0])

$iconStream = [IO.File]::Create($outputFull)
$writer = [IO.BinaryWriter]::new($iconStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)

    $offset = 6 + (16 * $sizes.Count)
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]
        $image = $images[$index]
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Length
    }

    foreach ($image in $images) {
        $writer.Write($image)
    }
}
finally {
    $writer.Dispose()
    $iconStream.Dispose()
}

[pscustomobject]@{
    Source = $sourceFull
    Icon = $outputFull
    Preview = $previewFull
    Sizes = $sizes -join ", "
    Crop = "$cropX,$cropY ${cropSize}x${cropSize}"
}
