# Temporary self-check probe: launch the published PackKit.Builder.exe (no UAC required),
# capture its main window screenshot after it settles, then close it (process owned by this probe).
# Also extracts the associated icon of notepad.exe to prove icon extraction is per-exe
# (result must NOT look like the red SP brand icon).
# ASCII-only source on purpose (PowerShell 5.1 parses BOM-less files as ANSI).

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeMethods
{
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
"@

# Project root is derived from this script's own location (scripts/ sits directly under the repo root),
# so the probe keeps working after the repo moves between machines or workspaces.
$repoRoot = Split-Path -Parent $PSScriptRoot
$distRoot = Join-Path $repoRoot "dist"
$builderExe = Join-Path $distRoot "PackKit.Builder\PackKit.Builder.exe"
$iconEvidenceRoot = Join-Path $distRoot "icon-evidence"
$shotPath = Join-Path $iconEvidenceRoot "builder-window-icon-check.png"
$notepadPngPath = Join-Path $iconEvidenceRoot "notepad-extracted-icon.png"
New-Item -ItemType Directory -Force -Path $iconEvidenceRoot | Out-Null

# --- Part 1: launch Builder, screenshot the window, close it ---
$process = Start-Process -FilePath $builderExe -PassThru
$visible = $false
$windowHandle = [IntPtr]::Zero
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    Start-Sleep -Milliseconds 500
    $process.Refresh()
    if ($process.HasExited) {
        throw "Builder process exited unexpectedly with code $($process.ExitCode)."
    }
    if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
        $windowHandle = $process.MainWindowHandle
        $visible = $true
        break
    }
}
if (-not $visible) {
    Stop-Process -Id $process.Id -Force
    throw "Builder main window did not appear within 20 seconds."
}

[NativeMethods]::SetForegroundWindow($windowHandle) | Out-Null
Start-Sleep -Milliseconds 1500

$rect = New-Object NativeMethods+RECT
[NativeMethods]::GetWindowRect($windowHandle, [ref]$rect) | Out-Null
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
Write-Host ("builder window rect: {0},{1} {2}x{3}" -f $rect.Left, $rect.Top, $width, $height)

$bitmap = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($width, $height)))
$bitmap.Save($shotPath, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()
Write-Host ("builder window screenshot saved: {0}" -f $shotPath)

# Sample the pixel just right of the title text start where the brand icon should sit
# (title bar icon area): scan the top-left 48x32 strip for brand-red pixels.
$stripBitmap = New-Object System.Drawing.Bitmap($shotPath)
$redCount = 0
for ($x = 0; $x -lt 56 -and $x -lt $stripBitmap.Width; $x++) {
    for ($y = 0; $y -lt 36 -and $y -lt $stripBitmap.Height; $y++) {
        $pixel = $stripBitmap.GetPixel($x, $y)
        if ($pixel.A -gt 200 -and $pixel.R -gt 150 -and $pixel.G -lt 110 -and $pixel.B -lt 110) {
            $redCount++
        }
    }
}
$stripBitmap.Dispose()
Write-Host ("title-bar strip brand-red pixels: {0}" -f $redCount)

Stop-Process -Id $process.Id -Force
Write-Host "builder process closed"

# --- Part 2: extract notepad.exe icon; a non-brand result proves per-exe extraction ---
$notepadPath = Join-Path $env:windir "System32\notepad.exe"
$notepadIcon = [System.Drawing.Icon]::ExtractAssociatedIcon($notepadPath)
$notepadBitmap = $notepadIcon.ToBitmap()
$notepadBitmap.Save($notepadPngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$notepadRed = 0
$notepadWhite = 0
for ($x = 0; $x -lt $notepadBitmap.Width; $x++) {
    for ($y = 0; $y -lt $notepadBitmap.Height; $y++) {
        $pixel = $notepadBitmap.GetPixel($x, $y)
        if ($pixel.A -gt 200 -and $pixel.R -gt 180 -and $pixel.G -lt 110 -and $pixel.B -lt 110) { $notepadRed++ }
        if ($pixel.A -gt 200 -and $pixel.R -gt 240 -and $pixel.G -gt 240 -and $pixel.B -gt 240) { $notepadWhite++ }
    }
}
Write-Host ("notepad icon {0}x{1} redPixels={2} whitePixels={3} -> {4}" -f $notepadBitmap.Width, $notepadBitmap.Height, $notepadRed, $notepadWhite, $notepadPngPath)
$notepadBitmap.Dispose()
$notepadIcon.Dispose()

if ($notepadRed -gt 100) {
    throw "notepad icon looks brand-red; extraction may be hard-coded to the brand icon."
}

Write-Host "BUILDER-ICON-PROBE-PASS"
