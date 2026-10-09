using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PackKit.Core;

namespace PackKit.Player.ViewModels;

/// <summary>
/// 成品端清单行视图模型：承载单个安装包条目的勾选状态、安装状态与语义色呈现，供主窗口列表绑定。
/// </summary>
public sealed class InstallItemViewModel : INotifyPropertyChanged
{
    /// <summary>状态标签前景与背景语义色（灰=待执行、蓝=进行中、绿=成功、红=失败，对齐客户端状态色语义规范，浅色主题下对比度不低于 4.5:1）。</summary>
    private static readonly Brush PendingForeground = CreateFrozenBrush(0x61, 0x61, 0x61);

    private static readonly Brush PendingBackground = CreateFrozenBrush(0xF0, 0xF0, 0xF0);
    private static readonly Brush InstallingForeground = CreateFrozenBrush(0x0F, 0x6C, 0xBD);
    private static readonly Brush InstallingBackground = CreateFrozenBrush(0xEF, 0xF6, 0xFC);
    private static readonly Brush SucceededForeground = CreateFrozenBrush(0x0A, 0x6C, 0x0A);
    private static readonly Brush SucceededBackground = CreateFrozenBrush(0xE6, 0xF2, 0xE6);
    private static readonly Brush FailedForeground = CreateFrozenBrush(0xC4, 0x2B, 0x1C);
    private static readonly Brush FailedBackground = CreateFrozenBrush(0xFD, 0xF3, 0xF4);

    private InstallStatus status = InstallStatus.Pending;
    private string statusText = "待安装";
    private Brush statusForeground = PendingForeground;
    private Brush statusBackground = PendingBackground;
    private string failureReason = string.Empty;
    private bool isChecked = true;
    private bool isSelectable = true;

    /// <summary>
    /// 以清单条目初始化行视图模型（默认勾选，与安装主流程「勾选项」语义一致）。
    /// </summary>
    /// <param name="source">对应的清单条目（不可为空）。</param>
    public InstallItemViewModel(PackItem source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        SizeText = FormatSize(source.SizeBytes);
        Icon = DecodeIconPng(source.IconPng);
    }

    /// <summary>清单条目变更通知。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>对应的清单条目（安装执行时的数据源）。</summary>
    public PackItem Source { get; }

    /// <summary>列表行图标（清单 IconPng 的解码结果，已冻结可直接绑定）；null 时由行模板显示统一占位字形。</summary>
    public ImageSource? Icon { get; }

    /// <summary>展示名称（清单名称为空时回退为文件名，保证行内始终可读）。</summary>
    public string DisplayName
    {
        get { return string.IsNullOrWhiteSpace(Source.Name) ? Source.FileName : Source.Name; }
    }

    /// <summary>条目大小的人类可读文本（B/KB/MB/GB）。</summary>
    public string SizeText { get; }

    /// <summary>当前安装状态。</summary>
    public InstallStatus Status { get { return status; } }

    /// <summary>状态标签文本（待安装 / 安装中 / 完成 / 失败）。</summary>
    public string StatusText { get { return statusText; } }

    /// <summary>状态标签前景色（语义色）。</summary>
    public Brush StatusForeground { get { return statusForeground; } }

    /// <summary>状态标签背景色（语义色）。</summary>
    public Brush StatusBackground { get { return statusBackground; } }

    /// <summary>是否处于失败态（驱动行内重试按钮可见性）。</summary>
    public bool IsFailed { get { return status == InstallStatus.Failed; } }

    /// <summary>行级悬停详情（失败时给出原因，其余状态为空即不弹提示）。</summary>
    public string StatusDetail
    {
        get { return status == InstallStatus.Failed ? failureReason : string.Empty; }
    }

    /// <summary>勾选状态（默认勾选；过滤仅影响显示、不影响勾选）。</summary>
    public bool IsChecked
    {
        get { return isChecked; }
        set
        {
            if (isChecked == value)
            {
                return;
            }

            isChecked = value;
            OnPropertyChanged();
        }
    }

    /// <summary>行内勾选框可用性（安装批次执行期间统一锁定，保证批次清单稳定）。</summary>
    public bool IsSelectable
    {
        get { return isSelectable; }
        set
        {
            if (isSelectable == value)
            {
                return;
            }

            isSelectable = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 迁移安装状态并刷新状态标签呈现（状态文本 + 语义色 + 重试可见性 + 悬停详情）。
    /// </summary>
    /// <param name="value">目标状态。</param>
    /// <param name="reason">失败原因（仅失败态消费，其余状态传空）。</param>
    public void ApplyStatus(InstallStatus value, string reason = "")
    {
        status = value;
        failureReason = reason ?? string.Empty;

        switch (status)
        {
            case InstallStatus.Installing:
                statusText = "安装中";
                statusForeground = InstallingForeground;
                statusBackground = InstallingBackground;
                break;
            case InstallStatus.Succeeded:
                statusText = "完成";
                statusForeground = SucceededForeground;
                statusBackground = SucceededBackground;
                break;
            case InstallStatus.Failed:
                statusText = "失败";
                statusForeground = FailedForeground;
                statusBackground = FailedBackground;
                break;
            default:
                statusText = "待安装";
                statusForeground = PendingForeground;
                statusBackground = PendingBackground;
                break;
        }

        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusForeground));
        OnPropertyChanged(nameof(StatusBackground));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(StatusDetail));
    }

    /// <summary>
    /// 触发属性变更通知（经 UI 线程调度器投递，保证跨线程变更时绑定更新安全）。
    /// </summary>
    /// <param name="propertyName">变更的属性名（缺省取调用方成员名）。</param>
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        var application = Application.Current;
        var dispatcher = application?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
            return;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// 把字节数格式化为人类可读大小文本（KB/MB/GB 保留一位小数，去除无意义的尾零）。
    /// </summary>
    /// <param name="sizeBytes">字节长度。</param>
    /// <returns>形如「1.6 MB」的大小文本。</returns>
    private static string FormatSize(long sizeBytes)
    {
        const double BytesPerUnit = 1024;

        // 逐级换算到合适档位：字节 / KB / MB / GB
        if (sizeBytes < BytesPerUnit)
        {
            return $"{sizeBytes} B";
        }

        var sizeInKb = sizeBytes / BytesPerUnit;
        if (sizeInKb < BytesPerUnit)
        {
            return $"{sizeInKb:0.#} KB";
        }

        var sizeInMb = sizeInKb / BytesPerUnit;
        if (sizeInMb < BytesPerUnit)
        {
            return $"{sizeInMb:0.#} MB";
        }

        var sizeInGb = sizeInMb / BytesPerUnit;
        return $"{sizeInGb:0.#} GB";
    }

    /// <summary>
    /// 把清单条目携带的 base64 PNG 图标解码为可直接绑定的位图。
    /// 空白文本按契约视为无图标；损坏或非 PNG 数据按契约降级为 null（行模板显示统一占位字形），不阻断清单加载。
    /// </summary>
    /// <param name="iconPngBase64">清单条目的 base64 PNG 文本。</param>
    /// <returns>已冻结的位图；无图标或数据不可解码时为 null。</returns>
    private static ImageSource? DecodeIconPng(string? iconPngBase64)
    {
        if (string.IsNullOrWhiteSpace(iconPngBase64))
        {
            return null;
        }

        try
        {
            var pngBytes = Convert.FromBase64String(iconPngBase64);
            var image = new BitmapImage();
            using var pngStream = new MemoryStream(pngBytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = pngStream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (
            exception is FormatException
                or IOException
                or NotSupportedException
                or OutOfMemoryException)
        {
            // base64 非法（FormatException，含 WPF 解码器的 FileFormatException）、流失败或 WIC 编解码失败均按契约降级为占位呈现
            return null;
        }
    }

    /// <summary>
    /// 创建冻结的纯色画刷（跨行共享，避免重复分配）。
    /// </summary>
    /// <param name="red">红色分量。</param>
    /// <param name="green">绿色分量。</param>
    /// <param name="blue">蓝色分量。</param>
    /// <returns>已冻结的纯色画刷。</returns>
    private static Brush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
