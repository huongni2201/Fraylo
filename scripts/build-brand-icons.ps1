# Tạo lại icon Fraylo từ SVG gốc bằng WPF có sẵn trên Windows.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationCore, WindowsBase
$brandDir = Join-Path (Split-Path -Parent $PSScriptRoot) "src\Fraylo.Web\public\brand"
[xml]$mark = Get-Content (Join-Path $brandDir "fraylo-mark.svg") -Raw
$paths = @($mark.svg.path)
if ($paths.Count -ne 2) { throw "Logo Fraylo phải có hai path." }

$whitePaths = ($paths | ForEach-Object { '<path fill="#FFFFFF" d="' + $_.d + '"/>' }) -join ""
$iconSvg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256"><rect width="256" height="256" rx="56" fill="#FF5A45"/><g transform="translate(48 36) scale(6.7)">' + $whitePaths + '</g></svg>'
[IO.File]::WriteAllText((Join-Path $brandDir "fraylo-icon.svg"), $iconSvg)

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.PushTransform((New-Object System.Windows.Media.ScaleTransform ($size / 256.0), ($size / 256.0)))
    $coral = [System.Windows.Media.BrushConverter]::new().ConvertFromString("#FF5A45")
    $context.DrawRoundedRectangle($coral, $null, [System.Windows.Rect]::new(0, 0, 256, 256), 56, 56)
    $context.PushTransform([System.Windows.Media.TranslateTransform]::new(48, 36))
    $context.PushTransform([System.Windows.Media.ScaleTransform]::new(6.7, 6.7))
    foreach ($path in $paths) {
        $context.DrawGeometry([System.Windows.Media.Brushes]::White, $null, [System.Windows.Media.Geometry]::Parse($path.d))
    }
    $context.Pop()
    $context.Pop()
    $context.Pop()
    $context.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
    } finally { $stream.Dispose() }
}

$output = [IO.File]::Create((Join-Path $brandDir "fraylo.ico"))
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($image in $images) {
        $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Bytes.Length
    }
    foreach ($image in $images) { $writer.Write([byte[]]$image.Bytes) }
} finally { $writer.Dispose() }
Write-Host "Fraylo: SVG + ICO ($($sizes -join ', ') px) tại $brandDir"
