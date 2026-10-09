using System.Windows;
using System.Windows.Threading;
using PackKit.Player.Infrastructure;

namespace PackKit.Player;

/// <summary>
/// 成品端应用入口：装配主题库全局资源，并承载界面线程未处理异常的兜底呈现与留痕。
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
