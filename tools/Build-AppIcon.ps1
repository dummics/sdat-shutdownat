[CmdletBinding()]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot "..\assets\sdat_logo.png"),
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\assets\sdat_logo.ico"),
    [string]$PreviewPath = (Join-Path $PSScriptRoot "..\assets\sdat_logo_256.png")
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
}
