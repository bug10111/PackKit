# PackKit brand icon generator.
# Draws the approved brand style: red rounded square background + white bold "SP" text,
# and writes a multi-size ICO to assets\sp.ico (16/24/32/48/64/128 as BMP entries, 256 as PNG entry).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\gen-brand-icon.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\gen-brand-icon.ps1 -OutputPath <path-to-ico>
#
# Notes:
#   - Source is ASCII-only on purpose (Windows PowerShell 5.1 parses BOM-less files as ANSI).
#   - Idempotent: every run redraws deterministically and overwrites the output file.
#   - Requires .NET Framework / .NET System.Drawing (built into Windows PowerShell).

[CmdletBinding()]
param(
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path (Join-Path $repoRoot "assets") "sp.ico"
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$brandText = "SP"
$backgroundRed = [System.Drawing.Color]::FromArgb(255, 0xD1, 0x34, 0x38)

# Draw one square canvas: red rounded square + white bold "SP" centered.
function New-BrandBitmap([int]$PixelSize) {
    $bitmap = New-Object System.Drawing.Bitmap($PixelSize, $PixelSize)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.Color]::Transparent)

    # Rounded square background; corner radius scales with the canvas size.
    $radius = [Math]::Max(2, [int][Math]::Round($PixelSize * 0.22))
    $diameter = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $diameter, $diameter, 180, 90)
    $path.AddArc($PixelSize - $diameter, 0, $diameter, $diameter, 270, 90)
    $path.AddArc($PixelSize - $diameter, $PixelSize - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc(0, $PixelSize - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    $backgroundBrush = New-Object System.Drawing.SolidBrush($backgroundRed)
    $graphics.FillPath($backgroundBrush, $path)
    $backgroundBrush.Dispose()
    $path.Dispose()

    # Centered "SP"; start from a size-derived font and shrink until the text fits the safe area.
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $textRectangle = New-Object System.Drawing.RectangleF(0, 0, $PixelSize, $PixelSize)
    $fontSize = [float]([Math]::Round($PixelSize * 0.58))
    while ($fontSize -gt 2) {
        $probeFont = New-Object System.Drawing.Font("Segoe UI", $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $measured = $graphics.MeasureString($brandText, $probeFont)
        $probeFont.Dispose()
        if ($measured.Width -le ($PixelSize * 0.82) -and $measured.Height -le ($PixelSize * 0.78)) {
            break
        }

        $fontSize -= [Math]::Max(0.5, $PixelSize / 64.0)
    }

    $font = New-Object System.Drawing.Font("Segoe UI", $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $graphics.DrawString($brandText, $font, $textBrush, $textRectangle, $format)
    $textBrush.Dispose()
    $font.Dispose()
    $format.Dispose()
    $graphics.Dispose()
    return $bitmap
}

# Encode a bitmap as a 32bpp BMP entry (BITMAPINFOHEADER + bottom-up BGRA rows + empty AND mask).
function ConvertTo-IcoBmpBytes([System.Drawing.Bitmap]$Bitmap) {
    $pixelSize = $Bitmap.Width
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)

    # BITMAPINFOHEADER; height is doubled because ICO DIBs store XOR pixels plus the AND mask.
    $writer.Write([UInt32]40)
    $writer.Write([Int32]$pixelSize)
    $writer.Write([Int32]($pixelSize * 2))
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]0)
    $writer.Write([UInt32]($pixelSize * $pixelSize * 4))
    $writer.Write([Int32]0)
    $writer.Write([Int32]0)
    $writer.Write([UInt32]0)
    $writer.Write([UInt32]0)

    # Bottom-up BGRA rows; Format32bppArgb in-memory layout is already BGRA on little-endian Windows.
    $bounds = New-Object System.Drawing.Rectangle(0, 0, $pixelSize, $pixelSize)
    $locked = $Bitmap.LockBits($bounds, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $rowLength = $pixelSize * 4
    $row = New-Object byte[] $rowLength
    for ($y = $pixelSize - 1; $y -ge 0; $y--) {
        $rowPointer = [IntPtr]($locked.Scan0.ToInt64() + ($y * $locked.Stride))
        [System.Runtime.InteropServices.Marshal]::Copy($rowPointer, $row, 0, $rowLength)
        $writer.Write($row)
    }
    $Bitmap.UnlockBits($locked)

    # AND mask rows (1bpp, padded to 32 bits): all zero, the alpha channel is authoritative.
    $maskRowLength = ([int][Math]::Ceiling($pixelSize / 32.0)) * 4
    $maskRow = New-Object byte[] $maskRowLength
    for ($y = 0; $y -lt $pixelSize; $y++) {
        $writer.Write($maskRow)
    }

    $writer.Flush()
    $bytes = $stream.ToArray()
    $writer.Dispose()
    $stream.Dispose()
    # Comma wrapper keeps the byte[] a single pipeline object (PowerShell would otherwise unroll it element-wise).
    return , $bytes
}

# Encode a bitmap as PNG bytes (used for the 256px entry; Win10/11 explorer supports PNG-compressed ICO entries).
function ConvertTo-IcoPngBytes([System.Drawing.Bitmap]$Bitmap) {
    $stream = New-Object System.IO.MemoryStream
    $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    # Comma wrapper keeps the byte[] a single pipeline object (PowerShell would otherwise unroll it element-wise).
    return , $bytes
}

$entries = @()
foreach ($size in $sizes) {
    $bitmap = New-BrandBitmap -PixelSize $size
    if ($size -ge 256) {
        $entryBytes = ConvertTo-IcoPngBytes -Bitmap $bitmap
    }
    else {
        $entryBytes = ConvertTo-IcoBmpBytes -Bitmap $bitmap
    }

    $bitmap.Dispose()
    $entries += ,@{ Size = $size; Bytes = $entryBytes }
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$stream = [System.IO.File]::Create($OutputPath)
$writer = New-Object System.IO.BinaryWriter($stream)

# ICONDIR: reserved, type=icon, entry count.
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$entries.Count)

# ICONDIRENTRY table; 256px entries declare dimension byte 0 (means 256).
$imageOffset = 6 + (16 * $entries.Count)
foreach ($entry in $entries) {
    $dimension = [byte]0
    if ($entry.Size -lt 256) {
        $dimension = [byte]$entry.Size
    }

    $writer.Write($dimension)
    $writer.Write($dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$entry.Bytes.Length)
    $writer.Write([UInt32]$imageOffset)
    $imageOffset += $entry.Bytes.Length
}

# Image data blobs in the same order as the directory table.
foreach ($entry in $entries) {
    $writer.Write($entry.Bytes)
}

$writer.Flush()
$writer.Dispose()
$stream.Dispose()

# Self-check: header magic, declared entry count and total size must match what was written.
$fileBytes = [System.IO.File]::ReadAllBytes($OutputPath)
$headerOk = ($fileBytes.Length -gt 6) -and ($fileBytes[0] -eq 0) -and ($fileBytes[1] -eq 0) -and ($fileBytes[2] -eq 1) -and ($fileBytes[3] -eq 0)
$declaredCount = [BitConverter]::ToUInt16($fileBytes, 4)
$expectedTotal = 6 + (16 * $entries.Count)
foreach ($entry in $entries) {
    $expectedTotal += $entry.Bytes.Length
}
if (-not $headerOk -or $declaredCount -ne $entries.Count -or $fileBytes.Length -ne $expectedTotal) {
    throw ("ICO self-check failed: header={0} declaredEntries={1} expected={2} fileSize={3} expectedTotal={4}" -f $headerOk, $declaredCount, $entries.Count, $fileBytes.Length, $expectedTotal)
}

$hash = Get-FileHash -Path $OutputPath -Algorithm SHA256
Write-Host ("OK {0} ({1} bytes, {2} entries) SHA256={3}" -f $OutputPath, $fileBytes.Length, $declaredCount, $hash.Hash)
