# PackKit 装机包工具

把若干安装包打成一个**自解压成品 exe**（壳 + 尾部 zip 载荷 + footer 索引）的离线装机工具：制作端（Builder）负责挑选安装包、识别安装器类型、定安装顺序并打包；成品端（Player，即产出 exe）双击提权后多选、按顺序串行安装。

## 目录结构

```
PackKit/
├─ PackKit.sln                     # 解决方案（4 工程）
├─ docs/baseline.md                # 需求底表 / 收敛设计 / 验收基准（契约唯一源）
├─ src/
│  ├─ PackKit.Core/                # 共享契约库（net8.0 类库）
│  │  ├─ PackManifest.cs           #   清单模型（manifest.json，含 Groups 分组集合）
│  │  ├─ PackGroup.cs              #   分组模型（Id/Name）
│  │  ├─ PackGroupContract.cs      #   分组边界收口单源（悬空引用→未分组等）
│  │  ├─ PackItem.cs               #   清单条目模型（含 IconPng/GroupId）
│  │  ├─ InstallerType.cs          #   安装器类型枚举（nsis|inno|msi|unknown）
│  │  ├─ InstallerTypeDetector.cs  #   文件头启发式探测 + 默认静默参数
│  │  ├─ PayloadFormat.cs          #   成品布局常量与 footer 编解码（冻结契约）
│  │  ├─ PayloadFooter.cs          #   footer 解析结果
│  │  ├─ PayloadWriter.cs          #   打包器（manifest+包文件 → 成品 exe）
│  │  ├─ PayloadReader.cs          #   读取器（footer 校验/读清单/按需提取条目）
│  │  ├─ PayloadWriteProgress.cs   #   打包进度回调数据
│  │  ├─ PayloadPackageFile.cs     #   打包输入（条目+源文件路径）
│  │  ├─ ManifestJson.cs           #   清单 JSON 序列化边界（camelCase + 非空集合不变量）
│  │  └─ Internal/SubStream.cs     #   基础流只读切片视图（按偏移开 zip 用）
│  ├─ PackKit.Builder/             # 制作端（net8.0-windows WPF，WPF-UI 4.0.2 主题）
│  └─ PackKit.Player/              # 成品端壳（net8.0-windows WPF，app.manifest requireAdministrator）
├─ tools/
│  └─ PackKit.Harness/             # 实证探针（net8.0 控制台）：夹具/追加/读回/启动取证/类型探测
├─ scripts/
│  ├─ publish.ps1                  # 一键发布 Builder 与 Player 到 dist/（self-contained 单文件 win-x64）
│  ├─ gen-brand-icon.ps1           # 品牌图标 sp.ico 生成（幂等）
│  ├─ extract-icon-evidence.ps1    # 成品 exe 图标取证
│  └─ probe-builder-window-icon.ps1# 制作端窗口图标活体探针
└─ README.md
```

## 构建与发布

```bash
# 构建（交付级证据用全量重建）
dotnet build PackKit.sln -t:Rebuild

# 一键发布（产出 dist/PackKit.Builder/PackKit.Builder.exe 与 dist/PackKit.Player/PackKit.Player.exe）
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
```

发布命令等价于对两个 WinExe 工程逐个执行：

```bash
dotnet publish <工程.csproj> -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o <输出目录>
```

- 全自包含：目标机 **无需安装 .NET 运行时**（Win10/Win11 直接跑）。
- `PackKit.Player.exe` 内嵌 `app.manifest`（requireAdministrator）：双击弹一次 UAC，提权后安装器子进程继承管理员权限。

## 成品 exe 结构（冻结契约）

```
┌─────────────────────────────┐  偏移 0
│  壳 exe 字节                 │  Player 单文件自包含程序（其自带 bundle 清单不受尾部追加影响）
├─────────────────────────────┤  ← zipOffset（footer 记录）
│  zip 载荷字节                │
│   ├─ manifest.json          │  清单（camelCase JSON，见下）
│   └─ packages/{文件名}       │  各安装包条目（按需解压，非整包解压）
├─────────────────────────────┤  ← zipOffset + zipLength
│  footer（32 字节）           │
│   ├─ [0..16)   magic        │  "PACKKITv1" ASCII + \0 补齐 16 字节
│   ├─ [16..24)  zipOffset    │  int64 小端
│   └─ [24..32)  zipLength    │  int64 小端
└─────────────────────────────┘  文件尾
```

manifest.json 示例（camelCase；`installerType` 为字符串枚举 `nsis|inno|msi|unknown`）：

```json
{
  "formatVersion": 1,
  "title": "装机包标题",
  "groups": [
    { "id": "guid-字符串", "name": "分组名（如：开发）" }
  ],
  "items": [
    {
      "id": "guid-字符串",
      "name": "展示名称",
      "entryPath": "packages/setup.exe",
      "fileName": "setup.exe",
      "sizeBytes": 123456,
      "installerType": "nsis",
      "silentArgs": "/S",
      "iconPng": "base64 PNG 文本（可选；null 或缺省 = 无图标）",
      "groupId": "guid-字符串（可选；null 或缺省 = 未分组）"
    }
  ],
  "createdAt": "2026-10-08T00:00:00+08:00"
}
```

契约要点（制作端/成品端都必须遵守）：

- `entryPath` 恒为 `packages/{fileName}`（`PayloadFormat.BuildPackageEntryPath`）。
- `sizeBytes` 必须等于源文件长度（打包器强校验；读取端用它做损坏检测）。
- `fileName` 必须是纯文件名（禁止路径分隔符与目录穿越片段）。
- `silentArgs` 空串 = 无静默参数，安装时由用户人工点下一步。
- `installerType: unknown` 不猜测，默认静默参数为空串。
- `iconPng` 可选：条目列表行图标的 base64 PNG 文本（制作端入列时从 exe 图标资源抽取的 48×48 PNG）；只有「null = 无图标」与「非空 base64 文本」两种合法形态，空白文本在解析边界收口为 null；无图标时成品端列表行显示统一占位字形；旧清单/旧工程无该字段时反序列化为 null，加载兼容。
- `groups` 可选：软件分组集合（`PackManifest.Groups`，按创建顺序排列）；制作端「分组管理」维护，随清单持久化。缺省或空集合 = 无分组数据，成品端左栏整体隐藏（与旧成品行为一致）；集合内 `id`/`name` 空缺或 `id` 重复的分组在解析边界剔除（重复标识只认首个）。
- `groupId` 可选：条目所属分组引用（指向 `groups` 内分组 `id`）；null/缺省 = 未分组。悬空引用（指向不存在的分组）在解析边界收口为未分组，防止条目在任何分组视图都无法呈现；旧清单/旧工程无该字段时反序列化为 null（全部未分组）。
- MSI 由成品端经 `msiexec /i` 承载，Core 只提供参数 `/qn /norestart`。

安装器类型默认静默参数（`InstallerTypeDetector.GetDefaultSilentArgs`）：

| 类型 | 探测依据（文件头启发式） | 默认静默参数 |
|---|---|---|
| `msi` | OLE 复合文档头 `D0 CF 11 E0 A1 B1 1A E1` | `/qn /norestart` |
| `nsis` | 头部 64KB 内含 `Nullsoft` | `/S` |
| `inno` | 头部 64KB 内含 `Inno Setup` | `/VERYSILENT /NORESTART` |
| `unknown` | 以上都不命中（不猜测） | 空串（人工点下一步） |

## 实证结论（波次0 已取证，后续波次直接依赖）

- **单文件 exe 尾部追加可用**：self-contained 单文件 exe（PublishSingleFile + IncludeNativeLibrariesForSelfExtract）在尾部追加 zip+footer 后**进程可正常启动**（Harness 单文件版实测：追加后启动存活、并能在启动后自读回自身尾部载荷完成清单校验与按字节提取）。
- **追加版 Player 文件读回**：对追加后的 Player exe 文件本体，footer 校验、清单 17 字段逐一相等、条目按字节提取相等全部通过。
- **Player 本体启动**：因 `requireAdministrator` 在非提权自动化环境被拒（Win32 错误 740 需要提升），双击启动留待用户手动验收（见 baseline 未测项清单）。
- **ZipArchive 追加限制**（框架行为，源码级确认）：`ZipArchive` 写模式会把中央目录偏移写成输出流的**绝对偏移**——zip 必须写到从 0 开始的独立流（本库已用临时文件中转处理），禁止直接往成品中部写 zip。
- **NuGet 可用性**：本机全局包缓存已有 `WPF-UI (Lepoco) 4.0.2`；`net8.0-windows` WPF 工程 restore 成功（资产 `net8.0-windows7.0`，无 NU1701）。当前环境到 nuget.org / 镜像源的网络不可达（NU1900/NU1301），全新裸机首次还原需网络可达。

## Harness 用法（实证探针）

```bash
tools/PackKit.Harness/bin/Debug/net8.0/PackKit.Harness.exe <命令>
# makefixture <目录>                                生成测试夹具（两文件 + 清单）
# append <壳exe> <输出成品> <夹具目录>               壳 exe 追加 payload 产出成品
# readback <成品exe> <提取目录> <夹具目录>           读回清单逐字段比对 + 条目按字节比对
# launch <目标exe> <需存活秒数> [透传参数...]        启动目标 exe 取证存活
# wait <秒数>                                      空转指定秒数（配合 launch 用）
# detect <文件>                                    安装器类型探测
```

## 当前状态

波次0 地基已交付：解决方案骨架、Core 契约库、占位窗口、发布脚本、框架实证。制作端完整 UI/业务（添加包、排序、工程保存、打包流程）与成品端完整 UI/业务（清单展示、串行安装、重试）由后续版本实现。
