using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using PackKit.Core;
using PackKit.Player.Infrastructure;
using PackKit.Player.ViewModels;
using Wpf.Ui.Controls;

namespace PackKit.Player;

/// <summary>
/// 成品端主窗口：读自身尾部清单 → 勾选/全选/搜索 → 按清单顺序串行安装 → 失败单重试 → 汇总呈现。
/// </summary>
public partial class MainWindow : FluentWindow
{
    /// <summary>提权徽标语义色：绿点=管理员模式、橙警示=未提权（对齐状态色语义规范）。</summary>
    private static readonly Brush ElevatedBadgeBrush = CreateFrozenBrush(0x0F, 0x7B, 0x0F);

    private static readonly Brush UnelevatedBadgeBrush = CreateFrozenBrush(0xBC, 0x4C, 0x00);

    private readonly ObservableCollection<InstallItemViewModel> visibleItems = new();
    private readonly List<InstallItemViewModel> allItems = new();
    private readonly List<GroupFilterViewModel> groupFilters = new();

    /// <summary>当前选中的分组筛选（null 表示未启用分组时的全部范围）。</summary>
    private GroupFilterViewModel? selectedGroupFilter;

    /// <summary>窗口生命周期取消源：窗口关闭即取消进行中的安装等待与解压。</summary>
    private readonly CancellationTokenSource shutdownCts = new();

    private string? exePath;
    private bool isInstalling;
    private bool isDisposed;

    /// <summary>
    /// 初始化主窗口并装配列表数据源与加载事件。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        ItemsList.ItemsSource = visibleItems;
        Loaded += MainWindow_Loaded;
    }

    /// <summary>
    /// 窗口加载完成：先渲染窗口框架，再异步读自身尾部清单填充界面。
    /// </summary>
    /// <param name="sender">事件来源（本窗口）。</param>
    /// <param name="e">路由事件参数。</param>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeAsync();
    }

    /// <summary>
    /// 启动加载链路：检测提权态 → 读自身尾部清单 → 填充列表或降级为占位态（无 payload / 清单损坏均可见可诊断，不崩溃）。
    /// </summary>
    private async Task InitializeAsync()
    {
        UpdateElevationBadge();

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            MemoryLog.Error("无法取得当前进程可执行文件路径，进入占位模式。", new InvalidOperationException("Environment.ProcessPath 为空。"));
            ShowPlaceholder();
            return;
        }

        exePath = processPath;
        PackManifest manifest;
        try
        {
            manifest = await PayloadReader.ReadManifestAsync(exePath, shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭打断了清单读取：留痕后直接返回，窗口正在关闭无需呈现占位
            MemoryLog.Info("清单读取在窗口关闭后被取消。");
            return;
        }
        catch (Exception exception)
        {
            // 界面层读回失败容错降级：裸壳（无 footer）与清单损坏都在此收口，
            // 占位标题给出可执行动作，详情行透出底层可读错误，异常本身留痕不吞
            MemoryLog.Error("读取成品清单失败，进入占位模式。", exception);
            EmptyStateDetail.Text = $"详细信息：{exception.Message}";
            ShowPlaceholder();
            return;
        }

        PopulateFromManifest(manifest);
    }

    /// <summary>
    /// 以清单填充列表：窗口标题取清单 Title，逐条建立行视图模型并订阅勾选联动；
    /// 清单携带分组数据时显示左栏分组筛选（默认选中「全部」），无分组数据整体隐藏与旧成品一致。
    /// </summary>
    /// <param name="manifest">读回的成品清单。</param>
    private void PopulateFromManifest(PackManifest manifest)
    {
        if (manifest.Items.Count == 0)
        {
            EmptyStateTitle.Text = "暂无数据";
            EmptyStateDetail.Text = "成品清单未包含任何软件包，请用装机包制作工具重新打包。";
            ShowPlaceholder();
            return;
        }

        TitleBar.Title = manifest.Title;
        Title = manifest.Title;

        foreach (var packItem in manifest.Items)
        {
            var itemViewModel = new InstallItemViewModel(packItem);
            itemViewModel.PropertyChanged += ItemViewModel_PropertyChanged;
            allItems.Add(itemViewModel);
        }

        BuildGroupFilters(manifest);
        if (HasGroupData(manifest))
        {
            GroupList.ItemsSource = groupFilters;
            GroupPanel.Visibility = Visibility.Visible;
            GroupList.SelectedItem = groupFilters[0];
        }

        RefreshVisibleItems();
        UpdateInstallButton();
        StatusLine.Text = "就绪";
    }

    /// <summary>
    /// 判定清单是否携带分组数据（分组集合非空，或存在条目声明了所属分组）；
    /// 清单反序列化边界已把悬空分组引用收口为未分组，两条件实际同源，此处按契约字面双查兜底。
    /// </summary>
    /// <param name="manifest">读回的成品清单。</param>
    /// <returns>携带分组数据时为 true。</returns>
    private static bool HasGroupData(PackManifest manifest)
    {
        if (manifest.Groups.Count > 0)
        {
            return true;
        }

        foreach (var packItem in manifest.Items)
        {
            if (packItem.GroupId is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 构建左栏筛选选项：「全部」恒为首项，其后分组按清单创建顺序（展示名称与计数），存在未分组条目时追加「未分组」。
    /// </summary>
    /// <param name="manifest">读回的成品清单。</param>
    private void BuildGroupFilters(PackManifest manifest)
    {
        groupFilters.Clear();
        groupFilters.Add(new GroupFilterViewModel(GroupFilterKind.All, null, "全部", allItems.Count));
        foreach (var group in manifest.Groups)
        {
            var count = 0;
            foreach (var item in allItems)
            {
                if (string.Equals(item.Source.GroupId, group.Id, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            groupFilters.Add(new GroupFilterViewModel(GroupFilterKind.SpecificGroup, group.Id, group.Name, count));
        }

        var ungroupedCount = 0;
        foreach (var item in allItems)
        {
            if (item.Source.GroupId is null)
            {
                ungroupedCount++;
            }
        }

        if (ungroupedCount > 0)
        {
            groupFilters.Add(new GroupFilterViewModel(GroupFilterKind.Ungrouped, null, "未分组", ungroupedCount));
        }
    }

    /// <summary>
    /// 左栏选中项切换：切换当前分组范围并刷新可见行（勾选状态保留在行模型上，跨切换不丢失）。
    /// </summary>
    /// <param name="sender">事件来源（左栏列表）。</param>
    /// <param name="e">事件参数。</param>
    private void GroupList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupFilterViewModel filter)
        {
            return;
        }

        selectedGroupFilter = filter;
        RefreshVisibleItems();
    }

    /// <summary>
    /// 行视图模型勾选变更联动：刷新全选框三态与主 CTA 文案/可用性。
    /// </summary>
    /// <param name="sender">事件来源（行视图模型）。</param>
    /// <param name="e">属性变更参数。</param>
    private void ItemViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(InstallItemViewModel.IsChecked), StringComparison.Ordinal))
        {
            return;
        }

        UpdateSelectAllState();
        UpdateInstallButton();
    }

    /// <summary>
    /// 搜索框文本变更：按名称实时过滤可见行（仅影响显示，勾选状态保留在行模型上）。
    /// </summary>
    /// <param name="sender">事件来源（搜索框）。</param>
    /// <param name="e">事件参数。</param>
    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RefreshVisibleItems();
    }

    /// <summary>
    /// 全选框点击：全不选 → 全选；全选 / 部分选中 → 全不选（作用于当前可见行，与过滤显示解耦）。
    /// </summary>
    /// <param name="sender">事件来源（全选框）。</param>
    /// <param name="e">事件参数。</param>
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var visibleSnapshot = visibleItems.ToList();
        if (visibleSnapshot.Count == 0)
        {
            // 空可见行时全选无作用对象：仅把全选框复位为聚合态，避免点击自身的切换态残留
            UpdateSelectAllState();
            return;
        }

        var shouldCheckAll = visibleSnapshot.Any(item => !item.IsChecked);
        foreach (var item in visibleSnapshot)
        {
            item.IsChecked = shouldCheckAll;
        }
    }

    /// <summary>
    /// 主 CTA 点击：对勾选项按清单顺序串行执行安装批次。
    /// </summary>
    /// <param name="sender">事件来源（安装按钮）。</param>
    /// <param name="e">事件参数。</param>
    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetCheckedItemsInManifestOrder();
        if (isInstalling || targets.Count == 0)
        {
            return;
        }

        await RunExclusiveAsync(async token =>
        {
            foreach (var target in targets)
            {
                await InstallItemAsync(target, token);
            }
        });
    }

    /// <summary>
    /// 失败行重试点击：仅重装该项（批次执行期间忽略）。
    /// </summary>
    /// <param name="sender">事件来源（重试按钮，DataContext 为行视图模型）。</param>
    /// <param name="e">事件参数。</param>
    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (isInstalling || sender is not FrameworkElement { DataContext: InstallItemViewModel target })
        {
            return;
        }

        await RunExclusiveAsync(async token =>
        {
            await InstallItemAsync(target, token);
        });
    }

    /// <summary>
    /// 独占执行包装：安装期间锁定交互（全选/搜索/勾选/主 CTA），结束或取消后恢复并刷新汇总行。
    /// </summary>
    /// <param name="action">批次动作（参数为批次级取消令牌）。</param>
    private async Task RunExclusiveAsync(Func<CancellationToken, Task> action)
    {
        isInstalling = true;
        SetInteractionLock(true);
        UpdateInstallButton();
        using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownCts.Token);
        try
        {
            await action(batchCts.Token);
            StatusLine.Text = BuildSummaryText();
        }
        catch (OperationCanceledException)
        {
            MemoryLog.Info("安装流程被取消（窗口关闭或批次终止）。");
            StatusLine.Text = "安装已取消。";
        }
        catch (Exception exception)
        {
            // 批次级兜底：留痕后以可读文案呈现，不让异常静默丢失
            MemoryLog.Error("安装批次发生未预期异常。", exception);
            StatusLine.Text = $"安装批次异常中止：{exception.Message}";
        }
        finally
        {
            isInstalling = false;
            SetInteractionLock(false);
            UpdateInstallButton();
        }
    }

    /// <summary>
    /// 单项安装：解压到临时目录 → 拉起安装器并等待退出 → 按退出码定状态 → 尽力清理临时目录。
    /// 单项失败只标记该项并继续批次，不中断后续项。
    /// </summary>
    /// <param name="target">目标行视图模型。</param>
    /// <param name="token">取消令牌。</param>
    private async Task InstallItemAsync(InstallItemViewModel target, CancellationToken token)
    {
        target.ApplyStatus(InstallStatus.Installing);
        StatusLine.Text = $"正在安装: {target.DisplayName} …";

        // 临时目录：%TEMP%\PackKit\{Guid}\，用完即清理（占用时留下并记日志）
        var extractDirectory = Path.Combine(Path.GetTempPath(), "PackKit", Guid.NewGuid().ToString("N"));
        try
        {
            var extractedPath = await PayloadReader.ExtractItemAsync(exePath!, target.Source, extractDirectory, token);
            var exitCode = await RunInstallerAsync(target.Source, extractedPath, token);
            if (exitCode == 0)
            {
                target.ApplyStatus(InstallStatus.Succeeded);
                MemoryLog.Info($"「{target.DisplayName}」安装完成（退出码 0）。");
                StatusLine.Text = $"✓ {target.DisplayName} 安装完成";
                return;
            }

            target.ApplyStatus(InstallStatus.Failed, $"安装器退出码 {exitCode}");
            MemoryLog.Info($"「{target.DisplayName}」安装失败（退出码 {exitCode}）。");
            StatusLine.Text = $"✗ {target.DisplayName} 安装失败（安装器退出码 {exitCode}）";
        }
        catch (OperationCanceledException)
        {
            // 取消不是安装器失败：状态回退为待安装后向上传递，由批次层统一呈现
            target.ApplyStatus(InstallStatus.Pending);
            throw;
        }
        catch (Exception exception)
        {
            MemoryLog.Error($"「{target.DisplayName}」安装失败。", exception);
            target.ApplyStatus(InstallStatus.Failed, exception.Message);
            StatusLine.Text = $"✗ {target.DisplayName} 安装失败：{exception.Message}";
        }
        finally
        {
            CleanupExtractDirectory(extractDirectory);
        }
    }

    /// <summary>
    /// 拉起单个安装器并等待退出：MSI 统一经 msiexec /i 承载；其余按壳类型直启（静默参数为空时由人工点下一步）。
    /// </summary>
    /// <param name="item">清单条目。</param>
    /// <param name="extractedPath">解压出的安装包文件路径。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>安装器退出码。</returns>
    private static async Task<int> RunInstallerAsync(PackItem item, string extractedPath, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(item, extractedPath)
        };

        var started = process.Start();
        if (!started)
        {
            throw new InvalidOperationException($"安装器「{item.FileName}」启动失败。");
        }

        await process.WaitForExitAsync(token);
        return process.ExitCode;
    }

    /// <summary>
    /// 按安装器类型构造启动信息（MSI 走 msiexec /i + 静默参数；其余 UseShellExecute 直启）。
    /// </summary>
    /// <param name="item">清单条目。</param>
    /// <param name="extractedPath">解压出的安装包文件路径。</param>
    /// <returns>进程启动信息。</returns>
    private static ProcessStartInfo CreateStartInfo(PackItem item, string extractedPath)
    {
        if (item.InstallerType == InstallerType.Msi)
        {
            var arguments = $"/i \"{extractedPath}\"";
            if (!string.IsNullOrWhiteSpace(item.SilentArgs))
            {
                arguments += $" {item.SilentArgs}";
            }

            return new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = arguments,
                UseShellExecute = false
            };
        }

        return new ProcessStartInfo
        {
            FileName = extractedPath,
            Arguments = item.SilentArgs,
            UseShellExecute = true
        };
    }

    /// <summary>
    /// 尽力清理单项临时目录：删除失败（如安装器仍占用）时保留现场并记日志，不抛出。
    /// </summary>
    /// <param name="directory">临时目录。</param>
    private static void CleanupExtractDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception)
        {
            MemoryLog.Error($"临时目录 {directory} 清理失败，已保留待手动或系统清理。", exception);
        }
    }

    /// <summary>
    /// 按当前分组范围与名称过滤可见行（分组精确匹配 + 名称包含匹配、忽略大小写）；过滤只影响显示，勾选状态保留。
    /// </summary>
    private void RefreshVisibleItems()
    {
        var query = SearchBox.Text.Trim();
        visibleItems.Clear();
        foreach (var item in allItems)
        {
            if (!MatchesGroupFilter(item))
            {
                continue;
            }

            if (item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                visibleItems.Add(item);
            }
        }

        UpdateSelectAllState();
    }

    /// <summary>
    /// 判定行是否落在当前分组筛选范围（全部 / 指定分组精确匹配 / 未分组）。
    /// </summary>
    /// <param name="item">待判定的行视图模型。</param>
    /// <returns>落在当前范围时为 true。</returns>
    private bool MatchesGroupFilter(InstallItemViewModel item)
    {
        if (selectedGroupFilter is null || selectedGroupFilter.Kind == GroupFilterKind.All)
        {
            return true;
        }

        if (selectedGroupFilter.Kind == GroupFilterKind.Ungrouped)
        {
            return item.Source.GroupId is null;
        }

        return string.Equals(item.Source.GroupId, selectedGroupFilter.GroupId, StringComparison.Ordinal);
    }

    /// <summary>
    /// 刷新全选框三态：全勾选=true、全不勾=false、部分勾选=中间态（仅统计当前可见行）。
    /// </summary>
    private void UpdateSelectAllState()
    {
        if (visibleItems.Count == 0)
        {
            SelectAllCheckBox.IsChecked = false;
            return;
        }

        var checkedCount = visibleItems.Count(item => item.IsChecked);
        if (checkedCount == visibleItems.Count)
        {
            SelectAllCheckBox.IsChecked = true;
            return;
        }

        SelectAllCheckBox.IsChecked = checkedCount == 0 ? false : null;
    }

    /// <summary>
    /// 取勾选项（保持清单顺序，即安装执行顺序）。
    /// </summary>
    /// <returns>勾选中的行视图模型集合。</returns>
    private List<InstallItemViewModel> GetCheckedItemsInManifestOrder()
    {
        return allItems.Where(item => item.IsChecked).ToList();
    }

    /// <summary>
    /// 刷新主 CTA：文案携带勾选数，N=0 或安装中禁用。
    /// </summary>
    private void UpdateInstallButton()
    {
        var checkedCount = allItems.Count(item => item.IsChecked);
        InstallButton.Content = $"安装勾选项（{checkedCount}）";
        InstallButton.IsEnabled = !isInstalling && checkedCount > 0;
    }

    /// <summary>
    /// 构建汇总文案：完成 X / 失败 Y（有失败时提示可单独重试）。
    /// </summary>
    /// <returns>汇总文案（无任何已判定项时为「就绪」）。</returns>
    private string BuildSummaryText()
    {
        var succeededCount = allItems.Count(item => item.Status == InstallStatus.Succeeded);
        var failedCount = allItems.Count(item => item.Status == InstallStatus.Failed);
        if (succeededCount == 0 && failedCount == 0)
        {
            return "就绪";
        }

        var summary = $"完成 {succeededCount} / 失败 {failedCount}";
        if (failedCount > 0)
        {
            summary += "，可对失败项单独重试";
        }

        return summary;
    }

    /// <summary>
    /// 切换安装期交互锁：全选框 / 搜索框 / 分组左栏 / 行勾选框统一禁用与恢复。
    /// </summary>
    /// <param name="locked">是否锁定。</param>
    private void SetInteractionLock(bool locked)
    {
        SelectAllCheckBox.IsEnabled = !locked;
        SearchBox.IsEnabled = !locked;
        GroupList.IsEnabled = !locked;
        foreach (var item in allItems)
        {
            item.IsSelectable = !locked;
        }
    }

    /// <summary>
    /// 降级为占位态：隐藏分组左栏与清单区交互件，呈现占位标题与详情（裸壳 / 清单损坏 / 清单为空共用）。
    /// </summary>
    private void ShowPlaceholder()
    {
        GroupPanel.Visibility = Visibility.Collapsed;
        ToolbarPanel.Visibility = Visibility.Collapsed;
        ListPanel.Visibility = Visibility.Collapsed;
        InstallButton.Visibility = Visibility.Collapsed;
        StatusLine.Visibility = Visibility.Collapsed;
        EmptyStatePanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 按 Windows 身份实际态刷新提权徽标（● 管理员模式 / ⚠ 未提权）。
    /// </summary>
    private void UpdateElevationBadge()
    {
        bool isElevated;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var principal = new WindowsPrincipal(identity);
            isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        if (isElevated)
        {
            ElevationDot.Text = "● ";
            ElevationDot.Foreground = ElevatedBadgeBrush;
            ElevationText.Text = "管理员模式";
            return;
        }

        ElevationDot.Text = "⚠ ";
        ElevationDot.Foreground = UnelevatedBadgeBrush;
        ElevationText.Text = "未提权";
    }

    /// <summary>
    /// 窗口关闭：取消未完成的安装等待（安装器子进程不受影响，继续独立运行）。
    /// </summary>
    /// <param name="e">关闭事件参数。</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        shutdownCts.Cancel();
        MemoryLog.Info("窗口关闭，已请求取消未完成的安装等待。");
        base.OnClosing(e);
    }

    /// <summary>
    /// 窗口关闭完成：释放生命周期取消源。
    /// </summary>
    /// <param name="e">事件参数。</param>
    protected override void OnClosed(EventArgs e)
    {
        if (!isDisposed)
        {
            isDisposed = true;
            shutdownCts.Dispose();
        }

        base.OnClosed(e);
    }

    /// <summary>
    /// 创建冻结的纯色画刷（徽标语义色共享实例）。
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
