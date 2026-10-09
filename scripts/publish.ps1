# PackKit publish script: builds self-contained single-file win-x64 exes
# for PackKit.Builder and PackKit.Player into dist/.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Configuration Release
#
# Notes:
#   - Requires .NET SDK 8.0+ on PATH.
#   - Output layout: dist\PackKit.Builder\PackKit.Builder.exe / dist\PackKit.Player\PackKit.Player.exe
#   - Player.exe carries requireAdministrator in its app manifest (single UAC prompt at launch).

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "dist"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "dotnet CLI not found on PATH. Install .NET SDK 8.0 or newer."
}

$publishArgs = @(
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true"
)

$projects = @(
    @{ Csproj = "src\PackKit.Builder\PackKit.Builder.csproj"; Out = "PackKit.Builder" },
    @{ Csproj = "src\PackKit.Player\PackKit.Player.csproj";  Out = "PackKit.Player"  }
)

foreach ($project in $projects) {
    $csprojPath = Join-Path $repoRoot $project.Csproj
    $outputPath = Join-Path $OutputRoot $project.Out
    Write-Host "== Publishing $csprojPath -> $outputPath" -ForegroundColor Cyan
    & dotnet publish $csprojPath @publishArgs -o $outputPath
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed for $csprojPath (exit code $LASTEXITCODE)."
    }
}

Write-Host "== Publish finished. Outputs:" -ForegroundColor Green
foreach ($project in $projects) {
    $exePath = Join-Path (Join-Path $OutputRoot $project.Out) ($project.Out + ".exe")
    if (Test-Path $exePath) {
        $sizeMb = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
        Write-Host ("   {0}  ({1} MB)" -f $exePath, $sizeMb)
    }
    else {
        Write-Warning ("   missing: {0}" -f $exePath)
    }
}
