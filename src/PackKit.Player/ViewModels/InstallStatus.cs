namespace PackKit.Player.ViewModels;

/// <summary>
/// 安装项在成品端的生命周期状态（驱动行状态标签、重试按钮可见性与底部汇总）。
/// </summary>
public enum InstallStatus
{
    /// <summary>待安装（尚未开始执行）。</summary>
    Pending,

    /// <summary>安装中（解压或安装器运行进行中）。</summary>
    Installing,

    /// <summary>完成（安装器退出码为 0）。</summary>
    Succeeded,

    /// <summary>失败（安装器退出码非 0，或解压 / 拉起安装器发生异常）。</summary>
    Failed
}
