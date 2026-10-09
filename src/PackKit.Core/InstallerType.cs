namespace PackKit.Core;

/// <summary>
/// 安装器类型（由文件头启发式探测得出；unknown 表示无法识别，按约定不得猜测）。
/// </summary>
public enum InstallerType
{
    /// <summary>无法识别的安装器类型：静默参数保持空串，安装时由用户人工点下一步完成。</summary>
    Unknown,

    /// <summary>Nullsoft Scriptable Install System（NSIS）安装包。</summary>
    Nsis,

    /// <summary>Inno Setup 安装包。</summary>
    Inno,

    /// <summary>Microsoft Windows Installer（MSI）安装包，由成品端经 msiexec /i 承载执行。</summary>
    Msi
}
