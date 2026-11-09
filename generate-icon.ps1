#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $writer.Write([int]40)
    $writer.Write([int]$size)
    $writer.Write([int]($size * 2))
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([int]0)
    $writer.Write([int]($size * $size * 4 + $maskStride * $size))
    foreach ($unused in 1..4) { $writer.Write([int]0) }
    # Original blue registry-cube motif, drawn independently of Windows resources.
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $u = $x * 16.0 / $size
            $v = $y * 16.0 / $size
            $column = [int][Math]::Floor(($u - 1) / 5)
            $row = [int][Math]::Floor(($v - 2) / 5)
            $localX = $u - 1 - $column * 5
            $localY = $v - 2 - $row * 5
            if ($column -ge 0 -and $column -lt 3 -and $row -ge 0 -and $row -lt 3 -and $localX -lt 4 -and $localY -lt 4) {
                if ($localX -lt 0.8 -or $localY -lt 0.8) { $writer.Write([byte[]]@(155, 100, 20, 255)) }
                else { $writer.Write([byte[]]@(225, 175, 75, 255)) }
            }
            else { $writer.Write([byte[]]@(0, 0, 0, 0)) }
        }
    }
    for ($y = $size - 1; $y -ge 0; $y--) {
        $mask = [byte[]]::new($maskStride)
        for ($x = 0; $x -lt $size; $x++) {
            $alphaOffset = 40 + (($size - 1 - $y) * $size + $x) * 4 + 3
            if ($stream.GetBuffer()[$alphaOffset] -eq 0) {
                $index = [int][Math]::Floor($x / 8)
                $mask[$index] = $mask[$index] -bor (128 -shr ($x % 8))
            }
        }
        $writer.Write($mask)
    }
    $writer.Flush()
    ,$stream.ToArray()
    $writer.Dispose()
    $stream.Dispose()
}
$destination = Join-Path $PSScriptRoot 'managed\Assets\RegistryEditor.ico'
New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
$output = [IO.File]::Create($destination)
$icon = [IO.BinaryWriter]::new($output)
try {
    $icon.Write([uint16]0); $icon.Write([uint16]1); $icon.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $icon.Write([byte]$dimension); $icon.Write([byte]$dimension)
        $icon.Write([byte]0); $icon.Write([byte]0)
        $icon.Write([uint16]1); $icon.Write([uint16]32)
        $icon.Write([uint32]$images[$i].Length); $icon.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $icon.Write([byte[]]$image) }
}
finally { $icon.Dispose(); $output.Dispose() }
