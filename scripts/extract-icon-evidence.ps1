# T-A icon forensics: extract the baked PE icon from the published exes into PNG files,
# verify the pixels match the brand style (rounded transparent corner + #D13438 red body + white strokes),
# and print sizes and SHA256 hashes for the receipt.
# ASCII-only source on purpose (PowerShell 5.1 parses BOM-less files as ANSI).

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$evidenceDirectory = Join-Path (Join-Path $repoRoot "dist") "icon-evidence"
New-Item -ItemType Directory -Force -Path $evidenceDirectory | Out-Null

foreach ($name in @("PackKit.Player", "PackKit.Builder")) {
    $exePath = (Resolve-Path (Join-Path (Join-Path (Join-Path $repoRoot "dist") $name) ($name + ".exe"))).Path
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($exePath)
    if ($null -eq $icon) {
        throw "Icon extraction returned null for $exePath"
    }

    $bitmap = $icon.ToBitmap()
    $pngPath = Join-Path $evidenceDirectory ($name + "-icon.png")
    $bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

    # Pixel checks: rounded corner transparent, body area brand red, at least one white stroke pixel.
    $cornerPixel = $bitmap.GetPixel(1, 1)
    $redCount = 0
    $whiteCount = 0
    for ($x = 0; $x -lt $bitmap.Width; $x++) {
        for ($y = 0; $y -lt $bitmap.Height; $y++) {
            $pixel = $bitmap.GetPixel($x, $y)
            if ($pixel.A -gt 200 -and $pixel.R -gt 180 -and $pixel.G -lt 110 -and $pixel.B -lt 110) { $redCount++ }
            if ($pixel.A -gt 200 -and $pixel.R -gt 240 -and $pixel.G -gt 240 -and $pixel.B -gt 240) { $whiteCount++ }
        }
    }

    $pngBytes = (Get-Item $pngPath).Length
    $hash = Get-FileHash -Path $pngPath -Algorithm SHA256
    Write-Host ("{0}" -f $exePath)
    Write-Host ("  icon {0}x{1} -> {2} ({3} bytes) SHA256={4}" -f $bitmap.Width, $bitmap.Height, $pngPath, $pngBytes, $hash.Hash)
    Write-Host ("  corner(1,1).A={0} redPixels={1} whitePixels={2}" -f $cornerPixel.A, $redCount, $whiteCount)
    if ($cornerPixel.A -ne 0 -or $redCount -lt 100 -or $whiteCount -lt 10) {
        throw ("Brand pixel verification failed for {0}: cornerA={1} red={2} white={3}" -f $exePath, $cornerPixel.A, $redCount, $whiteCount)
    }

    $bitmap.Dispose()
    $icon.Dispose()
}

Write-Host "FORENSICS-PASS"
