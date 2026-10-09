using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using PackKit.Builder.Models;
using PackKit.Builder.Services;
using PackKit.Core;
using Wpf.Ui.Controls;

namespace PackKit.Builder.ViewModels;

/// <summary>
/// 主界面视图模型：承载安装包列表、成品设置与打包流程的全部界面状态与动作。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly string desktopDirectory;

    /// <summary>安装包条目集合的列表视图（承载分组范围过滤；与 Items 同源，保存与打包始终消费全量集合）。</summary>
    private readonly ICollectionView itemsView;

    private string title = string.Empty;

    private string iconPath = string.Empty;

    private string shellExePath = string.Empty;

    private string outputExePath = string.Empty;

    private PackageEntryViewModel? selectedItem;

    private PackGroup? selectedGroup;

    private bool isBusy;

    private bool isDirty;

    private double progressPercent;

    private string progressPercentText = "0%";

    private string progressStatus = string.Empty;

    private string summaryText = "共 0 项 · 0 B";

    private bool hasItems;

    private InfoBarSeverity feedbackSeverity = InfoBarSeverity.Informational;

    private string feedbackTitle = string.Empty;

    private string feedbackMessage = string.Empty;

    private bool isFeedbackOpen;

    private bool isSuccessFeedback;

    private string lastOutputPath = string.Empty;

    private bool outputAutoFilled = true;

    private CancellationTokenSource? packCancellationTokenSource;

    /// <summary>打包进度中壳字节段占整体进度权重。</summary>
    private const double ShellProgressWeight = 10;

    /// <summary>打包进度中安装包压缩段占整体进度权重。</summary>
    private const double PackageProgressWeight = 80;

    /// <summary>打包进度中成品文件生成与收尾段占整体进度权重。</summary>
    private const double FinalizeProgressWeight = 10;

    /// <summary>各安装包条目在打包过程中的累计已写字节（按 zip 条目名记录）。</summary>
    private readonly Dictionary<string, long> packageWrittenBytes = new(StringComparer.Ordinal);

    /// <summary>本次打包的安装包原始字节总量（进度权重分母）。</summary>
    private long totalPackageBytes;

    /// <summary>本次打包的壳模板字节长度（进度权重分母）。</summary>
    private long shellTotalBytes;

    /// <summary>
    /// 创建主视图模型并装配命令。
    /// </summary>
    public MainViewModel()
    {
        desktopDirectory = ResolveDesktopDirectory();
        Items = new ObservableCollection<PackageEntryViewModel>();
        Items.CollectionChanged += (_, _) => RefreshDerivedState();
        Groups = new ObservableCollection<PackGroup>();
        GroupMenuItems = new ObservableCollection<GroupMenuItemViewModel>();

        // 分组集合任何增删移动都直接映射为下拉面板行集合重建（行包装负责承载界面态）
        Groups.CollectionChanged += (_, _) => RebuildGroupMenuItems();

        // 列表过滤视图与集合同源建立：过滤语义 = 选中分组范围（null 表示「全部」）
        itemsView = CollectionViewSource.GetDefaultView(Items);
        itemsView.Filter = FiltersEntryBySelectedGroup;

        MoveUpCommand = new RelayCommand(_ => MoveSelected(-1), _ => CanMoveSelected(-1));
        MoveDownCommand = new RelayCommand(_ => MoveSelected(1), _ => CanMoveSelected(1));
        DeleteSelectedCommand = new RelayCommand(_ => DeleteSelected(), _ => SelectedItem is not null && !IsBusy);
        PackCommand = new RelayCommand(_ => _ = PackAsync(), _ => !IsBusy);
        OpenOutputFolderCommand = new RelayCommand(_ => OpenOutputFolder(), _ => !string.IsNullOrEmpty(lastOutputPath) && File.Exists(lastOutputPath));

        ApplyNewProjectDefaults();
    }

    /// <summary>上移选中条目命令。</summary>
    public ICommand MoveUpCommand { get; }

    /// <summary>下移选中条目命令。</summary>
    public ICommand MoveDownCommand { get; }

    /// <summary>删除选中条目命令。</summary>
    public ICommand DeleteSelectedCommand { get; }

    /// <summary>打包生成成品命令（唯一主操作，打包期间禁用防重复提交）。</summary>
    public ICommand PackCommand { get; }

    /// <summary>打开成品所在文件夹命令。</summary>
    public ICommand OpenOutputFolderCommand { get; }

    /// <summary>安装包条目有序集合。</summary>
    public ObservableCollection<PackageEntryViewModel> Items { get; }

    /// <summary>列表过滤视图（与 Items 同源；筛选语义由选中分组范围决定，DataGrid 绑定此视图）。</summary>
    public ICollectionView ItemsView => itemsView;

    /// <summary>软件分组有序集合（顺序即创建顺序，供分组下拉面板与打包组装消费）。</summary>
    public ObservableCollection<PackGroup> Groups { get; }

    /// <summary>分组下拉面板行集合（由 Groups 派生，「全部」项为界面固定首项不在本集合内）。</summary>
    public ObservableCollection<GroupMenuItemViewModel> GroupMenuItems { get; }

    /// <summary>成品标题（打包时写入清单，输出文件名默认随之生成）。</summary>
    public string Title
    {
        get => title;
        set
        {
            if (SetProperty(ref title, value))
            {
                MarkDirty();
                RefreshAutoOutput();
            }
        }
    }

    /// <summary>可选图标 .ico 路径（空表示不嵌入图标）。</summary>
    public string IconPath
    {
        get => iconPath;
        set
        {
            if (SetProperty(ref iconPath, value))
            {
                MarkDirty();
            }
        }
    }

    /// <summary>壳模板 exe 路径（成品端 Player 程序）。</summary>
    public string ShellExePath
    {
        get => shellExePath;
        set
        {
            if (SetProperty(ref shellExePath, value))
            {
                MarkDirty();
            }
        }
    }

    /// <summary>成品 exe 输出路径（用户手动修改后不再随标题自动生成）。</summary>
    public string OutputExePath
    {
        get => outputExePath;
        set
        {
            if (SetProperty(ref outputExePath, value))
            {
                outputAutoFilled = false;
                MarkDirty();
            }
        }
    }

    /// <summary>当前选中条目。</summary>
    public PackageEntryViewModel? SelectedItem
    {
        get => selectedItem;
        set => SetProperty(ref selectedItem, value);
    }

    /// <summary>
    /// 分组下拉当前选中的分组范围：null 表示「全部」（列表显示全部条目，新添加的安装包保持未分组）；
    /// 选中某分组时列表只显示该分组条目，新添加的安装包自动归入该分组。
    /// </summary>
    public PackGroup? SelectedGroup
    {
        get => selectedGroup;
        set
        {
            if (SetProperty(ref selectedGroup, value))
            {
                OnPropertyChanged(nameof(SelectedGroupDisplayText));
                OnPropertyChanged(nameof(IsAllGroupsFilter));
                UpdateGroupMenuSelection();
                RefreshItemsView();
            }
        }
    }

    /// <summary>分组下拉按钮展示文本（按钮文案即当前分组范围）。</summary>
    public string SelectedGroupDisplayText => SelectedGroup is { } selected ? $"分组：{selected.Name}" : "分组：全部";

    /// <summary>当前分组范围是否为「全部」（下拉面板「全部」项的选中标注）。</summary>
    public bool IsAllGroupsFilter => SelectedGroup is null;

    /// <summary>
    /// 最近一次批量添加中被跳过的文件数（每次 AddPackageFiles 收尾重置；供紧随其后的归组反馈把跳过信息并入文案，防止跳过警告被归组结果反馈覆盖）。
    /// </summary>
    public int LastAddSkippedCount { get; private set; }

    /// <summary>是否正在打包（打包期间禁用全部变更动作与打包按钮防重复提交）。</summary>
    public bool IsBusy
    {
        get => isBusy;
        private set => SetProperty(ref isBusy, value);
    }

    /// <summary>工程自上次保存后是否有未保存变更。</summary>
    public bool IsDirty
    {
        get => isDirty;
        private set => SetProperty(ref isDirty, value);
    }

    /// <summary>打包进度百分比（0~100）。</summary>
    public double ProgressPercent
    {
        get => progressPercent;
        private set
        {
            if (SetProperty(ref progressPercent, value))
            {
                ProgressPercentText = $"{Math.Round(value)}%";
            }
        }
    }

    /// <summary>打包进度百分比文本。</summary>
    public string ProgressPercentText
    {
        get => progressPercentText;
        private set => SetProperty(ref progressPercentText, value);
    }

    /// <summary>打包进度状态文本（当前阶段与当前条目）。</summary>
    public string ProgressStatus
    {
        get => progressStatus;
        private set => SetProperty(ref progressStatus, value);
    }

    /// <summary>底部合计文本（条目数与总大小）。</summary>
    public string SummaryText
    {
        get => summaryText;
        private set => SetProperty(ref summaryText, value);
    }

    /// <summary>列表是否非空（驱动空状态占位显隐）。</summary>
    public bool HasItems
    {
        get => hasItems;
        private set => SetProperty(ref hasItems, value);
    }

    /// <summary>反馈条严重级别。</summary>
    public InfoBarSeverity FeedbackSeverity
    {
        get => feedbackSeverity;
        private set => SetProperty(ref feedbackSeverity, value);
    }

    /// <summary>反馈条标题。</summary>
    public string FeedbackTitle
    {
        get => feedbackTitle;
        private set => SetProperty(ref feedbackTitle, value);
    }

    /// <summary>反馈条消息。</summary>
    public string FeedbackMessage
    {
        get => feedbackMessage;
        private set => SetProperty(ref feedbackMessage, value);
    }

    /// <summary>
    /// 反馈条是否展开（InfoBar 关闭按钮回写时需要公开 setter 支持 TwoWay 绑定）。
    /// </summary>
    public bool IsFeedbackOpen
    {
        get => isFeedbackOpen;
        set => SetProperty(ref isFeedbackOpen, value);
    }

    /// <summary>最近一次打包是否成功（驱动「打开所在文件夹」按钮显隐）。</summary>
    public bool IsSuccessFeedback
    {
        get => isSuccessFeedback;
        private set => SetProperty(ref isSuccessFeedback, value);
    }

    /// <summary>
    /// 应用条目编辑结果（名称、静默参数与所属分组由编辑对话框回写，类型只读不可改）。
    /// </summary>
    /// <param name="entryId">条目唯一标识。</param>
    /// <param name="newName">新的展示名称。</param>
    /// <param name="newSilentArgs">新的静默参数。</param>
    /// <param name="newGroupId">新的所属分组唯一标识；null 表示未分组。</param>
    /// <returns>条目存在并已应用时为 true。</returns>
    public bool ApplyEntryEdit(string entryId, string newName, string newSilentArgs, string? newGroupId)
    {
        foreach (var entry in Items)
        {
            if (!string.Equals(entry.Id, entryId, StringComparison.Ordinal))
            {
                continue;
            }

            entry.Name = newName.Trim();
            entry.SilentArgs = newSilentArgs.Trim();
            entry.GroupId = newGroupId;
            entry.GroupName = ResolveGroupName(newGroupId);
            MarkDirty();
            // 条目换组后按当前分组范围刷新过滤视图（条目可能移入或移出当前范围）
            RefreshItemsView();
            return true;
        }

        return false;
    }

    /// <summary>
    /// 判定分组名称是否已被占用（忽略大小写，分组同级不重名约束）。
    /// </summary>
    /// <param name="name">待检查的分组名称。</param>
    /// <returns>已被占用时为 true。</returns>
    public bool IsGroupNameTaken(string name)
    {
        foreach (var group in Groups)
        {
            if (string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 创建分组（追加到集合末尾，顺序即创建顺序；名称非空与不重名校验由调用方界面完成）。
    /// </summary>
    /// <param name="name">分组名称（自动去除首尾空白）。</param>
    /// <returns>创建完成的分组实例。</returns>
    public PackGroup CreateGroup(string name)
    {
        var group = new PackGroup
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = name.Trim()
        };
        Groups.Add(group);
        MarkDirty();
        return group;
    }

    /// <summary>
    /// 重命名分组并同步刷新其下条目的分组展示名。
    /// </summary>
    /// <param name="groupId">目标分组唯一标识。</param>
    /// <param name="newName">新分组名称（自动去除首尾空白）。</param>
    public void RenameGroup(string groupId, string newName)
    {
        PackGroup? target = null;
        foreach (var group in Groups)
        {
            if (string.Equals(group.Id, groupId, StringComparison.Ordinal))
            {
                target = group;
                break;
            }
        }

        if (target is null)
        {
            return;
        }

        target.Name = newName.Trim();
        SyncEntryGroupName(target);
        MarkDirty();
        // PackGroup 为 Core 冻结契约无属性通知：改名后需重建下拉面板行并刷新按钮文案与面板选中标注
        RebuildGroupMenuItems();
        OnPropertyChanged(nameof(SelectedGroupDisplayText));
    }

    /// <summary>
    /// 删除分组：其下安装包回落为未分组（不删包），删除确认提示由下拉面板在调用前给出；
    /// 被删分组正处于筛选范围时同步回落为「全部」。
    /// </summary>
    /// <param name="groupId">目标分组唯一标识。</param>
    public void DeleteGroup(string groupId)
    {
        if (selectedGroup is not null && string.Equals(selectedGroup.Id, groupId, StringComparison.Ordinal))
        {
            SelectedGroup = null;
        }

        for (var index = Groups.Count - 1; index >= 0; index--)
        {
            if (!string.Equals(Groups[index].Id, groupId, StringComparison.Ordinal))
            {
                continue;
            }

            Groups.RemoveAt(index);
            break;
        }

        foreach (var entry in Items)
        {
            if (string.Equals(entry.GroupId, groupId, StringComparison.Ordinal))
            {
                entry.GroupId = null;
                entry.GroupName = string.Empty;
            }
        }

        // 回落条目按当前分组范围刷新过滤视图（被删分组的条目可能移出当前范围）
        RefreshItemsView();
        MarkDirty();
    }

    /// <summary>
    /// 统计指定分组下的安装包条目数（删除分组确认提示用）。
    /// </summary>
    /// <param name="groupId">目标分组唯一标识。</param>
    /// <returns>该分组下的条目数。</returns>
    public int CountEntriesInGroup(string groupId)
    {
        var count = 0;
        foreach (var entry in Items)
        {
            if (string.Equals(entry.GroupId, groupId, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 批量添加安装包文件：入列时探测安装器类型并预填默认静默参数，同文件去重，类型受限与重复的文件跳过并向用户明示；
    /// 返回本次实际入列的条目，供调用方按归组双通道落位。
    /// </summary>
    /// <param name="filePaths">候选文件路径集合。</param>
    /// <returns>本次实际入列的条目集合（跳过的不含在内）。</returns>
    public IReadOnlyList<PackageEntryViewModel> AddPackageFiles(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var addedEntries = new List<PackageEntryViewModel>();
        var addedCount = 0;
        var skippedCount = 0;
        foreach (var filePath in filePaths)
        {
            var isSupportedExtension = filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || filePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
            if (!isSupportedExtension || !File.Exists(filePath))
            {
                skippedCount++;
                continue;
            }

            var fullPath = Path.GetFullPath(filePath);
            var isDuplicate = false;
            foreach (var existing in Items)
            {
                if (string.Equals(existing.SourceFilePath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    isDuplicate = true;
                    break;
                }
            }

            if (isDuplicate)
            {
                skippedCount++;
                continue;
            }

            InstallerType installerType;
            long sizeBytes;
            try
            {
                installerType = InstallerTypeDetector.Detect(fullPath);
                sizeBytes = new FileInfo(fullPath).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个文件读取失败只跳过该文件并在反馈条明示，不阻断其余文件入列
                skippedCount++;
                continue;
            }

            // exe 条目入列时抽取首个图标资源为 base64 PNG（.msi 与抽取失败按契约降级为 null，成品端显示占位字形）
            var iconPng = filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? IconExtractor.ExtractIconPngBase64(fullPath)
                : null;

            var addedEntry = new PackageEntryViewModel(
                Guid.NewGuid().ToString("D"),
                Path.GetFileNameWithoutExtension(fullPath),
                fullPath,
                installerType,
                InstallerTypeDetector.GetDefaultSilentArgs(installerType),
                sizeBytes,
                iconPng);
            Items.Add(addedEntry);
            addedEntries.Add(addedEntry);
            addedCount++;
        }

        LastAddSkippedCount = skippedCount;
        if (addedCount > 0)
        {
            MarkDirty();
        }

        if (skippedCount > 0)
        {
            ShowFeedback(InfoBarSeverity.Warning, "部分文件未添加", $"{skippedCount} 个文件因重复添加或不受支持而跳过。");
        }
        else if (addedCount > 0)
        {
            CloseFeedback();
        }

        return addedEntries;
    }

    /// <summary>
    /// 添加整个文件夹（取该文件夹顶层的 *.exe 与 *.msi，不递归子目录），返回本次实际入列的条目。
    /// </summary>
    /// <param name="folderPath">目标文件夹路径。</param>
    /// <returns>本次实际入列的条目集合。</returns>
    public IReadOnlyList<PackageEntryViewModel> AddPackagesFromFolder(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        if (!Directory.Exists(folderPath))
        {
            ShowFeedback(InfoBarSeverity.Error, "文件夹不存在", folderPath);
            return Array.Empty<PackageEntryViewModel>();
        }

        var packageFiles = new List<string>();
        foreach (var filePath in Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly))
        {
            var isPackageFile = filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || filePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
            if (isPackageFile)
            {
                packageFiles.Add(filePath);
            }
        }

        packageFiles.Sort(StringComparer.OrdinalIgnoreCase);
        return AddPackageFiles(packageFiles);
    }

    /// <summary>
    /// 把一批新添加的条目归入指定分组（归组双通道的落位点；null 表示保持未分组）。
    /// </summary>
    /// <param name="entries">待归组的条目集合。</param>
    /// <param name="groupId">目标分组唯一标识；null 表示未分组。</param>
    public void AssignEntriesToGroup(IReadOnlyList<PackageEntryViewModel> entries, string? groupId)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var groupName = ResolveGroupName(groupId);
        foreach (var entry in entries)
        {
            entry.GroupId = groupId;
            entry.GroupName = groupName;
        }

        // 新加条目以未分组态入列时会被分组过滤视图排除，归组后需刷新视图保证其在当前范围内可见
        RefreshItemsView();
        MarkDirty();
    }

    /// <summary>
    /// 从工程文件加载工程；解析失败向上抛出由调用方以可读方式呈现（界面层容错降级，不影响当前已加载内容）。
    /// </summary>
    /// <param name="filePath">工程文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public async Task LoadProjectFromFileAsync(string filePath, CancellationToken cancellationToken)
    {
        var project = await ProjectFileService.LoadAsync(filePath, cancellationToken);

        // 先关闭输出自动生成，再按工程文件原样恢复各字段，避免标题赋值触发默认输出覆盖工程值
        outputAutoFilled = false;
        title = project.Title;
        iconPath = project.IconPath;
        shellExePath = string.IsNullOrWhiteSpace(project.ShellExePath) ? ShellLocator.FindDefault() : project.ShellExePath;
        outputExePath = project.OutputExePath;

        Groups.Clear();
        foreach (var group in project.Groups)
        {
            Groups.Add(group);
        }

        Items.Clear();
        foreach (var projectItem in project.Items)
        {
            var installerType = Enum.TryParse<InstallerType>(projectItem.InstallerType, ignoreCase: true, out var parsedType)
                ? parsedType
                : InstallerType.Unknown;
            var sourceExists = File.Exists(projectItem.SourceFilePath);
            var sizeBytes = sourceExists ? new FileInfo(projectItem.SourceFilePath).Length : 0;
            var silentArgs = projectItem.SilentArgs;

            // 识别器升级后自动重探：旧工程识别为 unknown 的条目（源文件在场时）按新特征重新识别并补默认静默参数，
            // 用户已手填的参数不覆盖
            if (installerType == InstallerType.Unknown && sourceExists)
            {
                var redetectedType = InstallerTypeDetector.Detect(projectItem.SourceFilePath);
                if (redetectedType != InstallerType.Unknown)
                {
                    installerType = redetectedType;
                    silentArgs = InstallerTypeDetector.GetDefaultSilentArgs(redetectedType);
                }
            }

            Items.Add(new PackageEntryViewModel(
                projectItem.Id,
                projectItem.Name,
                projectItem.SourceFilePath,
                installerType,
                silentArgs,
                sizeBytes,
                projectItem.IconPng,
                projectItem.GroupId,
                ResolveGroupName(projectItem.GroupId)));
        }

        SelectedItem = null;
        SelectedGroup = null;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IconPath));
        OnPropertyChanged(nameof(ShellExePath));
        OnPropertyChanged(nameof(OutputExePath));
        MarkClean();
        ShowFeedback(InfoBarSeverity.Informational, "工程已打开", filePath);
    }

    /// <summary>
    /// 发起打包（校验 → 组装 → 写出，进度回调驱动进度条；期间界面进入忙态防重复提交）。
    /// </summary>
    /// <returns>工程实例。</returns>
    private BuilderProject BuildProject()
    {
        var project = new BuilderProject
        {
            Title = Title,
            IconPath = IconPath,
            ShellExePath = ShellExePath,
            OutputExePath = OutputExePath
        };

        foreach (var group in Groups)
        {
            project.Groups.Add(group);
        }

        foreach (var entry in Items)
        {
            project.Items.Add(new BuilderProjectItem
            {
                Id = entry.Id,
                Name = entry.Name,
                SourceFilePath = entry.SourceFilePath,
                InstallerType = entry.InstallerType.ToString(),
                SilentArgs = entry.SilentArgs,
                IconPng = entry.IconPng,
                GroupId = entry.GroupId
            });
        }

        return project;
    }

    /// <summary>工程自动保存文件路径（固定锚定数据目录，随数据目录迁移与备份）。</summary>
    public static string AutoProjectFilePath => Path.Combine(DataDirectoryService.CurrentRoot, "project.packkit.json");

    /// <summary>
    /// 工程随数据目录自动保存（静默：失败仅写日志，不中断主流程、不弹窗）。
    /// </summary>
    public void SaveProjectAuto()
    {
        try
        {
            ProjectFileService.Save(BuildProject(), AutoProjectFilePath);
            MarkClean();
        }
        catch (Exception ex)
        {
            AppLogger.Error("工程自动保存失败。", ex);
        }
    }

    /// <summary>
    /// 发起打包（校验 → 组装 → 写出，进度回调驱动进度条；期间界面进入忙态防重复提交）。
    /// </summary>
    /// <returns>异步任务。</returns>
    public async Task PackAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var request = BuildPackagingRequest();
        packCancellationTokenSource = new CancellationTokenSource();
        IsBusy = true;
        CloseFeedback();
        ResetProgress(request);

        try
        {
            var progress = new Progress<PayloadWriteProgress>(OnPayloadProgress);
            var outcome = await PackagingService.PackAsync(request, progress, packCancellationTokenSource.Token);
            lastOutputPath = outcome.OutputExePath;
            ProgressPercent = 100;
            ProgressStatus = "打包完成";
            IsSuccessFeedback = true;
            if (outcome.IconSkipReason is not null)
            {
                // 图标字段保留但当前壳形态实证不可嵌入：打包成功但以警告明示跳过原因，不静默假实现
                ShowFeedback(InfoBarSeverity.Warning, "打包完成（图标未嵌入）", $"{outcome.IconSkipReason}成品已输出：{outcome.OutputExePath}");
            }
            else
            {
                ShowFeedback(InfoBarSeverity.Success, "打包完成", $"成品已输出：{outcome.OutputExePath}");
            }
        }
        catch (OperationCanceledException)
        {
            ProgressStatus = "已取消";
            ShowFeedback(InfoBarSeverity.Warning, "打包已取消", "打包过程已取消，输出文件可能不完整，可重新打包覆盖。");
        }
        catch (Exception ex)
        {
            ProgressStatus = "打包失败";
            IsSuccessFeedback = false;
            ShowFeedback(InfoBarSeverity.Error, "打包失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
            packCancellationTokenSource.Dispose();
            packCancellationTokenSource = null;
        }
    }

    /// <summary>
    /// 取消进行中的打包（窗口关闭或用户主动取消时调用；仅在界面线程调用，与打包生命周期串行无竞态）。
    /// </summary>
    public void CancelPack()
    {
        packCancellationTokenSource?.Cancel();
    }

    /// <summary>
    /// 以可读方式向用户呈现错误（反馈条展示，不吞异常）。
    /// </summary>
    /// <param name="title">错误标题。</param>
    /// <param name="message">可读原因。</param>
    public void ShowError(string title, string message)
    {
        IsSuccessFeedback = false;
        ShowFeedback(InfoBarSeverity.Error, title, message);
    }

    /// <summary>
    /// 以成功级别展示反馈条（归组结果等正向反馈）。
    /// </summary>
    /// <param name="title">反馈标题。</param>
    /// <param name="message">反馈内容。</param>
    public void ShowSuccess(string title, string message)
    {
        ShowFeedback(InfoBarSeverity.Success, title, message);
    }

    /// <summary>
    /// 以提示级别展示反馈条（不构成成功或错误的操作结论）。
    /// </summary>
    /// <param name="title">反馈标题。</param>
    /// <param name="message">反馈内容。</param>
    public void ShowInformational(string title, string message)
    {
        ShowFeedback(InfoBarSeverity.Informational, title, message);
    }

    /// <summary>
    /// 展示反馈条。
    /// </summary>
    /// <param name="severity">严重级别。</param>
    /// <param name="title">标题。</param>
    /// <param name="message">消息。</param>
    private void ShowFeedback(InfoBarSeverity severity, string title, string message)
    {
        FeedbackSeverity = severity;
        FeedbackTitle = title;
        FeedbackMessage = message;
        IsSuccessFeedback = severity == InfoBarSeverity.Success;
        IsFeedbackOpen = true;
    }

    /// <summary>
    /// 收起反馈条。
    /// </summary>
    private void CloseFeedback()
    {
        IsFeedbackOpen = false;
        IsSuccessFeedback = false;
    }

    /// <summary>
    /// 标记工程出现未保存变更，并立即随数据目录自动保存（静默，失败仅落日志）。
    /// </summary>
    private void MarkDirty()
    {
        IsDirty = true;
        SaveProjectAuto();
    }

    /// <summary>
    /// 清除未保存变更标记（保存/打开/新建后调用）。
    /// </summary>
    private void MarkClean()
    {
        IsDirty = false;
    }

    /// <summary>
    /// 应用新建工程默认值：壳模板按约定定位，输出路径默认桌面「装机包.exe」。
    /// </summary>
    private void ApplyNewProjectDefaults()
    {
        shellExePath = ShellLocator.FindDefault();
        OnPropertyChanged(nameof(ShellExePath));
        outputAutoFilled = true;
        RefreshAutoOutput();
    }

    /// <summary>
    /// 按标题自动生成输出路径（仅在用户未手动修改输出路径时生效）。
    /// </summary>
    private void RefreshAutoOutput()
    {
        if (!outputAutoFilled)
        {
            return;
        }

        var fileTitle = SanitizeFileName(Title);
        if (string.IsNullOrWhiteSpace(fileTitle))
        {
            fileTitle = "装机包";
        }

        outputExePath = Path.Combine(desktopDirectory, $"{fileTitle}.exe");
        OnPropertyChanged(nameof(OutputExePath));
    }

    /// <summary>
    /// 把标题清洗为合法文件名（非法字符替换为连字符）。
    /// </summary>
    /// <param name="rawTitle">原始标题。</param>
    /// <returns>清洗后的文件名安全文本。</returns>
    private static string SanitizeFileName(string rawTitle)
    {
        var sanitized = rawTitle.Trim();
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalidChar, '-');
        }

        return sanitized;
    }

    /// <summary>
    /// 解析桌面目录（异常环境下回退到我的文档，保证默认输出路径总可用）。
    /// </summary>
    /// <returns>桌面目录路径。</returns>
    private static string ResolveDesktopDirectory()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop))
        {
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        return string.IsNullOrEmpty(desktop) ? "." : desktop;
    }

    /// <summary>
    /// 在集合增删移动后刷新派生状态（行序号、合计文本、空态标志）。
    /// </summary>
    private void RefreshDerivedState()
    {
        for (var rowIndex = 0; rowIndex < Items.Count; rowIndex++)
        {
            Items[rowIndex].UpdateIndex(rowIndex + 1);
        }

        var totalBytes = 0L;
        foreach (var entry in Items)
        {
            totalBytes += entry.SizeBytes;
        }

        SummaryText = $"共 {Items.Count} 项 · {PackageEntryViewModel.FormatSize(totalBytes)}";
        HasItems = Items.Count > 0;
    }

    /// <summary>
    /// 判定选中条目能否朝指定方向移动。
    /// </summary>
    /// <param name="offset">移动方向（-1 上移，1 下移）。</param>
    /// <returns>可移动时为 true。</returns>
    private bool CanMoveSelected(int offset)
    {
        if (SelectedItem is null || IsBusy)
        {
            return false;
        }

        var currentIndex = Items.IndexOf(SelectedItem);
        var targetIndex = currentIndex + offset;
        return targetIndex >= 0 && targetIndex < Items.Count;
    }

    /// <summary>
    /// 移动选中条目并保持其选中态。
    /// </summary>
    /// <param name="offset">移动方向（-1 上移，1 下移）。</param>
    private void MoveSelected(int offset)
    {
        if (SelectedItem is null || IsBusy)
        {
            return;
        }

        var currentIndex = Items.IndexOf(SelectedItem);
        var targetIndex = currentIndex + offset;
        if (targetIndex < 0 || targetIndex >= Items.Count)
        {
            return;
        }

        Items.Move(currentIndex, targetIndex);
        SelectedItem = Items[targetIndex];
        MarkDirty();
    }

    /// <summary>
    /// 删除选中条目。
    /// </summary>
    private void DeleteSelected()
    {
        if (SelectedItem is null || IsBusy)
        {
            return;
        }

        Items.Remove(SelectedItem);
        SelectedItem = null;
        MarkDirty();
    }

    /// <summary>
    /// 组装打包请求（展示名为空时回退到文件名，保证成品端清单行可辨识；分组集合与条目分组一并携带）。
    /// </summary>
    /// <returns>打包请求。</returns>
    private PackagingRequest BuildPackagingRequest()
    {
        var entries = new List<PackagingEntry>();
        foreach (var entry in Items)
        {
            var displayName = string.IsNullOrWhiteSpace(entry.Name) ? entry.FileName : entry.Name;
            entries.Add(new PackagingEntry(entry.Id, displayName, entry.SourceFilePath, entry.InstallerType, entry.SilentArgs, entry.IconPng, entry.GroupId));
        }

        return new PackagingRequest(Title.Trim(), IconPath, ShellExePath, OutputExePath, Groups.ToList(), entries);
    }

    /// <summary>
    /// 解析分组唯一标识对应的展示名（未命中或未分组返回空串，展示层按未分组呈现）。
    /// </summary>
    /// <param name="groupId">分组唯一标识；null 表示未分组。</param>
    /// <returns>分组展示名。</returns>
    private string ResolveGroupName(string? groupId)
    {
        foreach (var group in Groups)
        {
            if (string.Equals(group.Id, groupId, StringComparison.Ordinal))
            {
                return group.Name;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 把分组当前名称同步刷新到其下全部条目的展示名（重命名分组后调用）。
    /// </summary>
    /// <param name="group">已更名的分组。</param>
    private void SyncEntryGroupName(PackGroup group)
    {
        foreach (var entry in Items)
        {
            if (string.Equals(entry.GroupId, group.Id, StringComparison.Ordinal))
            {
                entry.GroupName = group.Name;
            }
        }
    }

    /// <summary>
    /// 按分组创建顺序重建下拉面板行集合（ Groups 增删移动与改名后调用），并同步行选中标注。
    /// </summary>
    private void RebuildGroupMenuItems()
    {
        GroupMenuItems.Clear();
        foreach (var group in Groups)
        {
            GroupMenuItems.Add(new GroupMenuItemViewModel(group));
        }

        UpdateGroupMenuSelection();
    }

    /// <summary>
    /// 把当前分组范围同步标注到下拉面板各行（选中标注以对勾图标冗余呈现）。
    /// </summary>
    private void UpdateGroupMenuSelection()
    {
        foreach (var row in GroupMenuItems)
        {
            row.IsSelected = SelectedGroup is { } selected
                && string.Equals(row.Group.Id, selected.Id, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 刷新列表过滤视图（分组范围切换、条目换组与分组删除后调用）。
    /// </summary>
    private void RefreshItemsView()
    {
        itemsView.Refresh();
    }

    /// <summary>
    /// 列表过滤谓词：未选分组范围（「全部」）时放行全部条目，选中分组时只放行该分组条目。
    /// </summary>
    /// <param name="item">待判定的列表条目。</param>
    /// <returns>该条目应显示在列表中时为 true。</returns>
    private bool FiltersEntryBySelectedGroup(object item)
    {
        if (SelectedGroup is null)
        {
            return true;
        }

        return item is PackageEntryViewModel entry
            && string.Equals(entry.GroupId, SelectedGroup.Id, StringComparison.Ordinal);
    }

    /// <summary>
    /// 打包开始前重置进度状态并预计算进度权重分母。
    /// </summary>
    /// <param name="request">本次打包请求。</param>
    private void ResetProgress(PackagingRequest request)
    {
        packageWrittenBytes.Clear();
        ProgressPercent = 0;
        ProgressStatus = "正在准备打包…";

        shellTotalBytes = 0;
        if (File.Exists(request.ShellExePath))
        {
            shellTotalBytes = new FileInfo(request.ShellExePath).Length;
        }

        totalPackageBytes = 0;
        foreach (var entry in request.Entries)
        {
            totalPackageBytes += File.Exists(entry.SourceFilePath) ? new FileInfo(entry.SourceFilePath).Length : 0;
        }
    }

    /// <summary>
    /// 打包进度回调：按阶段把 Core 结构化进度折算为整体百分比与状态文本。
    /// 壳字节段占 10%，安装包压缩段占 80%（按原始字节累计），成品文件生成与收尾段占 10%。
    /// </summary>
    /// <param name="payloadProgress">Core 打包进度数据。</param>
    private void OnPayloadProgress(PayloadWriteProgress payloadProgress)
    {
        switch (payloadProgress.Phase)
        {
            case PayloadWriteProgress.PhaseShell:
                ProgressPercent = shellTotalBytes > 0
                    ? ShellProgressWeight * payloadProgress.TotalBytesWritten / shellTotalBytes
                    : 0;
                ProgressStatus = "正在写入壳程序…";
                break;

            case PayloadWriteProgress.PhaseManifest:
                ProgressPercent = ShellProgressWeight;
                ProgressStatus = "正在写入清单…";
                break;

            case PayloadWriteProgress.PhasePackage:
                RecordPackageProgress(payloadProgress.CurrentEntryName, payloadProgress.CurrentEntryBytesWritten);
                ProgressStatus = $"正在压缩 {ExtractFileName(payloadProgress.CurrentEntryName)} …";
                break;

            case PayloadWriteProgress.PhasePayloadCopy:
                ProgressPercent = ShellProgressWeight + PackageProgressWeight
                    + (payloadProgress.CurrentEntryTotalBytes > 0
                        ? FinalizeProgressWeight * payloadProgress.CurrentEntryBytesWritten / payloadProgress.CurrentEntryTotalBytes
                        : 0);
                ProgressStatus = "正在生成成品文件…";
                break;

            case PayloadWriteProgress.PhaseFooter:
                ProgressPercent = 99.9;
                ProgressStatus = "正在完成…";
                break;
        }
    }

    /// <summary>
    /// 记录单个安装包条目的压缩进度并折算整体百分比。
    /// </summary>
    /// <param name="entryName">zip 条目名（packages/{文件名}）。</param>
    /// <param name="writtenBytes">该条目已压缩字节。</param>
    private void RecordPackageProgress(string? entryName, long writtenBytes)
    {
        if (string.IsNullOrEmpty(entryName))
        {
            return;
        }

        packageWrittenBytes[entryName] = Math.Max(packageWrittenBytes.TryGetValue(entryName, out var previous) ? previous : 0, writtenBytes);

        var writtenTotal = 0L;
        foreach (var written in packageWrittenBytes.Values)
        {
            writtenTotal += written;
        }

        ProgressPercent = totalPackageBytes > 0
            ? ShellProgressWeight + PackageProgressWeight * writtenTotal / totalPackageBytes
            : ShellProgressWeight;
    }

    /// <summary>
    /// 从 zip 条目名提取展示用的文件名。
    /// </summary>
    /// <param name="entryName">zip 条目名。</param>
    /// <returns>纯文件名；无法解析时原样返回。</returns>
    private static string ExtractFileName(string? entryName)
    {
        if (string.IsNullOrEmpty(entryName))
        {
            return string.Empty;
        }

        var separatorIndex = entryName.LastIndexOf('/');
        return separatorIndex >= 0 ? entryName[(separatorIndex + 1)..] : entryName;
    }

    /// <summary>
    /// 在资源管理器中定位最近一次打包的成品文件。
    /// </summary>
    private void OpenOutputFolder()
    {
        if (string.IsNullOrEmpty(lastOutputPath) || !File.Exists(lastOutputPath))
        {
            return;
        }

        var startInfo = new ProcessStartInfo("explorer.exe", $"/select,\"{lastOutputPath}\"")
        {
            UseShellExecute = true
        };
        Process.Start(startInfo);
    }
}
