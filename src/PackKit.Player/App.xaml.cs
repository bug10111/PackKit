using System.Windows;
using System.Windows.Threading;
using PackKit.Core;
using PackKit.Player.Infrastructure;

namespace PackKit.Player;

/// <summary>
/// 成品端应用入口：装配主题库全局资源，并承载界面线程未处理异常的兜底呈现与留痕。
/// 工程纪律豁免口径：成品端为打包生成的一次性安装壳（双提权→多选安装→退出），无持久用户数据、
/// 无设置页形态，数据目录/设置页三入口/云同步备份纪律对其不成立；日志标准全额适用（装机现场排障取证面）。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 初始化应用并登记界面线程异常兜底（记录内存日志后以可读弹窗呈现，不静默吞掉）。
    /// </summary>
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    /// <summary>
    /// 应用启动：最先完成文件日志初始化（装机现场排障取证面），再继续既有启动时序。
    /// </summary>
    /// <param name="e">启动事件参数。</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLogger.Initialize("Player");
        AppLogger.Info("成品端启动。");
    }

    /// <summary>
    /// 界面线程未处理异常兜底：留痕后向用户给出可读错误，避免进程无提示退出。
    /// </summary>
    /// <param name="sender">事件来源（当前应用）。</param>
    /// <param name="e">异常事件数据。</param>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MemoryLog.Error("界面线程出现未处理异常。", e.Exception);
        MessageBox.Show(
            $"发生未处理的异常：{e.Exception.Message}",
            "装机包",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
