# SfxDemo build script: publish the bare shell exe (self-contained single-file),
# generate fixture source files locally, then append the payload to a copy of
# the shell to produce demo\SfxDemo-packed.exe. The packing itself is performed
# by the shell's "pack" mode, which calls PackKit.Core
# PayloadWriter.WriteToFileAsync (real contract code, no duplicated logic).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File demo\SfxDemo\build-demo.ps1
#
# Requirements:
#   - .NET SDK 8.0 or newer on PATH.
#   - This file is saved as UTF-8 with BOM; comments are kept ASCII-only on
#     purpose to avoid Windows PowerShell 5.1 encoding pitfalls.

[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$projectDir = $PSScriptRoot
$demoDir = Split-Path -Parent $projectDir
$projectPath = Join-Path $projectDir "SfxDemo.csproj"
$shellExePath = Join-Path $projectDir "bin\$Configuration\net8.0\win-x64\publish\SfxDemo.exe"
$outputExePath = Join-Path $demoDir "SfxDemo-packed.exe"
$fixtureDir = Join-Path $demoDir "fixtures"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "dotnet CLI not found on PATH. Install .NET SDK 8.0 or newer."
}

Write-Host "== Step 1/3: publish shell exe (self-contained single-file win-x64)"
& dotnet publish $projectPath -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $shellExePath)) {
    Write-Error "Published shell exe not found at expected path: $shellExePath"
}

# Step 2/3: generate fixture source files locally (no network access involved).
New-Item -ItemType Directory -Force -Path $fixtureDir | Out-Null

# readme.txt: roughly 2KB of Chinese text (UTF-8 without BOM).
$readmePath = Join-Path $fixtureDir "readme.txt"
$readmeLine = "PackKit demo payload readme: this line proves Chinese text survives pack and extract byte-for-byte. Chinese sentence: 你好，这是单exe自解压装载的演示文本。"
$builder = New-Object System.Text.StringBuilder
while ($builder.Length -lt 1600) {
    [void]$builder.AppendLine($readmeLine)
}
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($readmePath, $builder.ToString(), $utf8NoBom)

# blob.bin: exactly 100KB deterministic pseudo-random bytes (fixed seed).
$blobPath = Join-Path $fixtureDir "blob.bin"
$blobBytes = New-Object byte[] 102400
(New-Object System.Random(20261008)).NextBytes($blobBytes)
[System.IO.File]::WriteAllBytes($blobPath, $blobBytes)

$readmeLength = (Get-Item $readmePath).Length
$blobLength = (Get-Item $blobPath).Length
Write-Host "== Fixture files ready: $readmePath ($readmeLength bytes), $blobPath ($blobLength bytes)"

# Step 3/3: append manifest + fixtures + 32-byte footer to a copy of the shell.
Write-Host "== Step 3/3: pack payload via PayloadWriter.WriteToFileAsync (shell pack mode)"
& $shellExePath pack $shellExePath $outputExePath $fixtureDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "Pack step failed with exit code $LASTEXITCODE."
}

$sizeMb = [math]::Round((Get-Item $outputExePath).Length / 1MB, 1)
Write-Host "== Done: $outputExePath ($sizeMb MB). Double-click it or run it from a console to verify." -ForegroundColor Green
