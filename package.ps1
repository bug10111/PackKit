# PackKit 一键打包脚本：把 Builder 与 Player 两个成品 exe 以单文件 win-x64 形态整备进交付目录
# （默认 out\PackKit），附 version.txt（git 提交号 + 打包模式），供对外交付分发。
#
# 用途：
#   交付打包脚本，与 scripts\publish.ps1 职责分离——publish.ps1 = dev 态 dist\ 壳模板供给
#   （Builder 打包成品 exe 时引用 dist\PackKit.Player\PackKit.Player.exe 作壳）；
#   package.ps1 = 交付形态整备（out\PackKit 平铺双入口 exe + version.txt + data\ 升级保全）。
#
# 参数：
#   -SelfContained   自包含发布开关，默认 $true（交付包目标机免装 .NET 运行时）；框架依赖模式（目标机
#                    需 .NET Desktop Runtime 8.0）传法：PowerShell 会话内 -SelfContained:$false，
#                    或 powershell -File 调用时 -SelfContained 0（或 false）
#   -OutputDir       交付输出目录，默认 out\PackKit
#
# data 保全语义：
#   目标输出目录已存在且含 data\ 子目录时，仅覆盖程序文件、原样保留 data\（用户数据/数据库/密钥库）：
#   脚本先把 data\ 同卷改名暂存，清整目录内其余文件后原位放回，并打印保全日志行；
#   打包中途失败时自动把 data\ 回滚原位再上抛错误。全新输出目录直接整备，不受影响。
#
# 外部运行时依赖：
#   构建机需 .NET SDK 8.0+（dotnet CLI 在 PATH）与 git（version.txt 溯源提交号）；
#   交付物 self-contained 模式下目标机零运行时依赖（Win10/Win11 直接运行），
#   framework-dependent 模式下目标机需 .NET Desktop Runtime 8.0；
#   本项目无 WebView2 等其它外部运行时依赖。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File package.ps1
#   powershell -ExecutionPolicy Bypass -File package.ps1 -SelfContained 0
#   powershell -ExecutionPolicy Bypass -File package.ps1 -OutputDir D:\Deliver\PackKit

[CmdletBinding()]
param(
    # 兼容两种调用边界：PowerShell 会话内传布尔 -SelfContained:$false；
    # powershell -File 调用时形参按字符串绑定，传 -SelfContained 0（或 false）为框架依赖
    [object]$SelfContained = $true,
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"

# 形参归一化：-File 调用时 $SelfContained 以字符串形态到达（"0"/"false" = 框架依赖），其余真值按自包含处理
$selfContainedEffective = $true
if ($SelfContained -is [bool]) {
    $selfContainedEffective = $SelfContained
}
elseif ($SelfContained -is [string]) {
    $selfContainedEffective = -not ($SelfContained -match '^(?i)(false|0)$')
}
else {
    $selfContainedEffective = [bool]$SelfContained
}

# 本脚本位于仓库根，$PSScriptRoot 即仓库根（scripts\publish.ps1 在子目录才需取父目录）
$repoRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "out\PackKit"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "dotnet CLI not found on PATH. Install .NET SDK 8.0 or newer."
}

# version.txt 必须可溯源到具体提交，git 提交号取不到直接失败，禁止静默写占位值
$gitCommit = (& git -C $repoRoot rev-parse --short HEAD 2>$null)
if ([string]::IsNullOrWhiteSpace($gitCommit)) {
    Write-Error "无法取得 git 提交号（git rev-parse --short HEAD 为空），打包中止。"
}
$gitCommit = $gitCommit.Trim()
$packMode = if ($selfContainedEffective) { "self-contained" } else { "framework-dependent" }

$projects = @(
    @{ Csproj = "src\PackKit.Builder\PackKit.Builder.csproj"; Exe = "PackKit.Builder.exe" },
    @{ Csproj = "src\PackKit.Player\PackKit.Player.csproj";  Exe = "PackKit.Player.exe"  }
)

$publishArgs = @(
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "$selfContainedEffective",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true"
)

# 发布到进程级临时目录，整备时只挑入口 exe 进交付目录（旧 pdb 等附属产物一律不进交付物）
$stageRoot = Join-Path $env:TEMP ("PackKit-stage-" + [guid]::NewGuid().ToString("N"))

# 升级保全：data\ 同卷改名暂存（原子操作），成功后原位放回、失败自动回滚
$preservedData = $null
try {
    if (Test-Path $OutputDir) {
        $existingData = Join-Path $OutputDir "data"
        if (Test-Path $existingData) {
            $preservedData = Join-Path $OutputDir "data.preserved"
            if (Test-Path $preservedData) {
                Remove-Item $preservedData -Recurse -Force
            }
            Rename-Item $existingData "data.preserved"
            Write-Host "== 升级保全：检测到既有 data\ 目录，已暂存（仅覆盖程序文件，用户数据不覆盖不丢失）" -ForegroundColor Yellow
        }
        # 清整目录内旧程序文件，暂存中的 data.preserved 不动
        Get-ChildItem -Path $OutputDir -Force |
            Where-Object { $_.Name -ne "data.preserved" } |
            Remove-Item -Recurse -Force
    }

    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

    foreach ($project in $projects) {
        $csprojPath = Join-Path $repoRoot $project.Csproj
        $stageDir = Join-Path $stageRoot ([IO.Path]::GetFileNameWithoutExtension($project.Exe))
        Write-Host "== Publishing $csprojPath -> stage ($packMode)" -ForegroundColor Cyan
        & dotnet publish $csprojPath @publishArgs -o $stageDir
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for $csprojPath (exit code $LASTEXITCODE)."
        }
        # 产物整备：只取入口 exe（单文件发布下 pdb 等附属产物不对外分发）
        Copy-Item (Join-Path $stageDir $project.Exe) $OutputDir -Force
    }

    # version.txt：交付包溯源信息（git 提交号 + 打包模式 + 打包时间）
    $versionLines = @(
        "commit: $gitCommit",
        "mode: $packMode",
        "packedAt: $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))"
    )
    Set-Content -Path (Join-Path $OutputDir "version.txt") -Value $versionLines -Encoding Ascii

    # 升级保全恢复：data\ 原位放回
    if ($preservedData) {
        Move-Item $preservedData (Join-Path $OutputDir "data")
        $preservedData = $null
        Write-Host "== 升级保全：data\ 已原位恢复" -ForegroundColor Green
    }
}
finally {
    # 失败兜底：程序文件整备完成前失败时，把暂存的 data\ 放回原位再上抛原始错误
    if ($preservedData -and (Test-Path $preservedData)) {
        if (-not (Test-Path $OutputDir)) {
            New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
        }
        if (-not (Test-Path (Join-Path $OutputDir "data"))) {
            Move-Item $preservedData (Join-Path $OutputDir "data")
            Write-Host "== 打包未完成，data\ 已回滚原位" -ForegroundColor Red
        }
    }
    if (Test-Path $stageRoot) {
        Remove-Item $stageRoot -Recurse -Force
    }
}

Write-Host "== Package finished. Outputs:" -ForegroundColor Green
foreach ($project in $projects) {
    $exePath = Join-Path $OutputDir $project.Exe
    if (Test-Path $exePath) {
        $sizeMb = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
        Write-Host ("   {0}  ({1} MB)" -f $exePath, $sizeMb)
    }
    else {
        Write-Warning ("   missing: {0}" -f $exePath)
    }
}
Write-Host ("   {0}  (commit {1} / {2})" -f (Join-Path $OutputDir "version.txt"), $gitCommit, $packMode)
