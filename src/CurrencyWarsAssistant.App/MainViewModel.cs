using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using CurrencyWarsAssistant.Workflow;

namespace CurrencyWarsAssistant.App;

public sealed record GameModeOption(
    CurrencyWarsGameMode Value,
    string DisplayName,
    string Description)
{
    public override string ToString() => DisplayName;
}

public sealed record FastRerollModeOption(
    FastRerollMode Value,
    string DisplayName,
    string Description)
{
    public override string ToString() => DisplayName;
}

public sealed record GameSourceOption(
    GameSourcePreference Value,
    string DisplayName,
    string Description)
{
    public override string ToString() => DisplayName;
}

public sealed record BenchSaleModeOption(
    PreparationBenchSaleMode Value,
    string DisplayName,
    string Description)
{
    public override string ToString() => DisplayName;
}

public sealed record OverlayLogLine(string Text, Brush Foreground);

public sealed class MainViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions UserSettingsJsonOptions =
        new()
        {
            WriteIndented = true
        };
    private static readonly Brush InformationLogBrush =
        CreateFrozenBrush(0xF5, 0xF8, 0xFC);
    private static readonly Brush WarningLogBrush =
        CreateFrozenBrush(0xFD, 0xD8, 0x35);
    private static readonly Brush FaultLogBrush =
        CreateFrozenBrush(0xFB, 0x71, 0x71);
    private readonly IGameWindowService _windowService;
    private readonly IGameCapture _capture;
    private readonly IPhase1AutomationService _automation;
    private readonly GameDataCatalog _gameData;
    private readonly UiTaskEventSink _eventSink;
    private readonly LocalRunStore _runStore;
    private readonly IUnifiedRunLifecycleService _unifiedRunLifecycle;
    private readonly IHistoricalDashboardProjection? _historicalDashboard;
    private readonly ISituationScreenshotAnalyzer? _situationAnalyzer;
    private readonly IChallengeSummaryReportGenerator? _summaryReportGenerator;
    private readonly HistoricalDetailPresentationBuilder _detailPresentation;
    private GameWindowInfo? _selectedWindow;
    private ImageSource? _preview;
    private string _status = "等待连接游戏窗口";
    private bool _isRunning;
    private CancellationTokenSource? _passiveCollectionCancellation;
    private CancellationTokenSource? _runCancellation;
    private readonly object _activeOperationSync = new();
    private Task? _activeOperationTask;
    private long _activeOperationGeneration;
    private bool _shutdownRequested;
    private string _newCombinationName = "";
    private AppFilterSelectionMode _newCombinationCondition =
        AppFilterSelectionMode.Forbidden;
    private string _newCombinationInvestmentEnvironments = "";
    private string _newCombinationCompetitors = "";
    private string _newCombinationEnemyAffixes = "";
    private CurrencyWarsGameMode _selectedGameMode = CurrencyWarsGameMode.Standard;
    private GameSourcePreference _selectedGameSource =
        GameSourcePreference.Automatic;
    private string? _lastWindowProcessName;
    private string? _lastWindowTitle;
    private NormalizedRect? _savedGameArea;
    private GameWindowSourceKind? _savedGameAreaSource;
    private bool _enableUnknownPageEscapeRecovery = true;
    private bool _showFilterDescriptions;
    private bool _isLogOverlayClickThrough = true;
    private double _logOverlayOpacity = 0.52;
    private bool _showLogOverlay = true;
    private bool _enableDiagnosticLogging = true;
    private bool _deleteScreenshotsAfterRunCompletion;
    private bool _enableRewardStageAutomation = true;
    private bool _enableEarlyStrongFormationPurchase = true;
    private bool _enableGalaxyScholarRewardStrategy;
    private FastRerollMode _fastRerollMode = FastRerollMode.Stable;
    private PreparationBenchSaleMode _benchSaleMode =
        PreparationBenchSaleMode.InterestThreshold;
    private int _benchSaleInterestThreshold = 10;
    private string _openingFilterSettingsSource = "defaults";
    private string _dashboardDamageScaleLabel = "线性";
    private string _dashboardTheoryScaleLabel = "线性";
    private string _dashboardSummaryText = "等待节点结算";
    private string _detailedHistorySummary = "当前没有正在记录的对局。查看历史存档：点右上角【历史对局】按钮";
    private HistoricalDetailNodeViewModel? _selectedDetailedHistoryNode;
    private readonly Dictionary<FilterItemViewModel, AppFilterSelectionMode>
        _runningOpeningFilterModes = [];

    public MainViewModel(
        IGameWindowService windowService,
        IGameCapture capture,
        IPhase1AutomationService automation,
        GameDataCatalog gameData,
        UiTaskEventSink eventSink,
        LocalRunStore runStore,
        IUnifiedRunLifecycleService unifiedRunLifecycle,
        IHistoricalDashboardProjection? historicalDashboard = null,
        CommunityContactOptions? community = null,
        ISituationScreenshotAnalyzer? situationAnalyzer = null,
        IChallengeSummaryReportGenerator? summaryReportGenerator = null)
    {
        _windowService = windowService;
        _capture = capture;
        _automation = automation;
        _gameData = gameData;
        _eventSink = eventSink;
        _runStore = runStore;
        _unifiedRunLifecycle = unifiedRunLifecycle;
        _historicalDashboard = historicalDashboard;
        _situationAnalyzer = situationAnalyzer;
        _summaryReportGenerator = summaryReportGenerator;
        _detailPresentation = new HistoricalDetailPresentationBuilder(gameData);
        Community = community ?? new CommunityContactOptions();

        InvestmentEnvironmentFilters = new FilterGroupViewModel(
            gameData.InvestmentEnvironments.Select(item =>
                new FilterItemViewModel(
                    item.Id,
                    item.Name,
                    item.Effect,
                    supportsForbidden: false)));
        CompetitorFilters = new FilterGroupViewModel(
            gameData.Competitors.Select(item =>
                new FilterItemViewModel(
                    item.Id,
                    item.Name,
                    "竞争对手阵营",
                    supportsForbidden: true)));
        EnemyAffixFilters = new FilterGroupViewModel(
            gameData.EnemyAffixes.Select(item =>
                new FilterItemViewModel(
                    item.Id,
                    item.Name,
                    $"T{item.Tier} · {item.Effect}",
                    supportsForbidden: true)));
        var orderedStrategies = gameData.InvestmentStrategies
            .OrderByDescending(item =>
                InvestmentStrategyVersionCatalog.GetNewestFirstRank(item.Id))
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        InvestmentStrategyFilters = new FilterGroupViewModel(
            orderedStrategies
                .Where(item => item.AvailablePlanes.Contains(1))
                .Select(item => CreateInvestmentStrategyItem(
                    item,
                    isSelectable: true)));
        var selectableStrategiesById =
            InvestmentStrategyFilters.Items.ToDictionary(
                item => item.Id,
                StringComparer.OrdinalIgnoreCase);
        PrismaticInvestmentStrategies = CreateInvestmentStrategyRarityGroup(
            "棱彩",
            orderedStrategies,
            selectableStrategiesById);
        GoldInvestmentStrategies = CreateInvestmentStrategyRarityGroup(
            "金色",
            orderedStrategies,
            selectableStrategiesById);
        SilverInvestmentStrategies = CreateInvestmentStrategyRarityGroup(
            "银色",
            orderedStrategies,
            selectableStrategiesById);
        CharacterAutomationRules = new CharacterAutomationGroupViewModel(
            gameData.CurrencyWarsCharacters
                .OrderBy(item => item.Costs.Min())
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new CharacterAutomationItemViewModel(
                    item.Id,
                    item.Name,
                    item.Position,
                    item.Costs,
                    item.BondNames,
                    isRetained: false)));
        LoadUserSettings();
        _eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            TaskEventLevel.Information,
            "OpeningFilterSelectionsLoaded",
            $"来源={_openingFilterSettingsSource}；投资环境={FilterSelectionSettings.Capture(InvestmentEnvironmentFilters.Items).Count}；" +
            $"敌人阵营={FilterSelectionSettings.Capture(CompetitorFilters.Items).Count}；" +
            $"负面词条={FilterSelectionSettings.Capture(EnemyAffixFilters.Items).Count}"));
        SubscribeOpeningFilterGuards();
        CombinationRuleOptions =
        [
            .. gameData.InvestmentEnvironments.Select(item =>
                new CombinationRuleOption(
                    AppCombinationItemKind.InvestmentEnvironment,
                    item.Id,
                    item.Name)),
            .. gameData.Competitors.Select(item =>
                new CombinationRuleOption(
                    AppCombinationItemKind.Competitor,
                    item.Id,
                    item.Name)),
            .. gameData.EnemyAffixes.Select(item =>
                new CombinationRuleOption(
                    AppCombinationItemKind.EnemyAffix,
                    item.Id,
                    item.Name))
        ];

        if (!SpecialCombinations.Any(item => string.Equals(
                item.Id,
                "death_dragon_plus_extra_strike",
                StringComparison.OrdinalIgnoreCase)))
        {
            SpecialCombinations.Add(new SpecialCombinationViewModel(
                "灰手生命科技 + 额外打击",
                AppFilterSelectionMode.Forbidden,
                "不限制",
                "灰手生命科技",
                "额外打击",
                true));
        }

        RefreshWindowsCommand = new RelayCommand(
            RefreshWindows,
            () => !IsRunning && !IsPassiveCollectionRunning);
        CaptureCommand = new AsyncRelayCommand(
            CaptureAsync,
            () => SelectedWindow?.IsReadyForAutomation == true &&
                  !IsRunning &&
                  !IsPassiveCollectionRunning);
        ObserveCommand = new AsyncRelayCommand(
            () => TrackActiveOperationAsync(RecognizeCurrentWindowAsync),
            CanStart);
        AutoRerollCommand = new AsyncRelayCommand(
            () => TrackActiveOperationAsync(() => RunFilterAsync(false)),
            CanStart);
        StopCommand = new RelayCommand(
            Stop,
            () => IsRunning || IsPassiveCollectionRunning);
        AddCombinationCommand = new RelayCommand(AddCombination);
        DeleteCombinationCommand = new RelayCommand<SpecialCombinationViewModel>(
            DeleteCombination,
            combination => !combination.IsBuiltIn);
        RefreshIncompleteRunsCommand = new AsyncRelayCommand(
            RefreshIncompleteRunsAsync,
            () => !IsRunning && !IsPassiveCollectionRunning);
        ContinueIncompleteRunCommand = new RelayCommand<IncompleteRunViewModel>(
            RequestResume,
            run => run is not null &&
                   !IsRunning &&
                   !IsPassiveCollectionRunning);
        SettleIncompleteRunCommand = new RelayCommand<IncompleteRunViewModel>(
            run => _ = SettleIncompleteRunAsync(run),
            run => run is not null &&
                   !IsRunning &&
                   !IsPassiveCollectionRunning);
        DeleteIncompleteRunCommand = new RelayCommand<IncompleteRunViewModel>(
            run => _ = DeleteIncompleteRunAsync(run),
            run => run is not null &&
                   !IsRunning &&
                   !IsPassiveCollectionRunning);

        _automation.StatusChanged += OnAutomationStatusChanged;
        _automation.OpeningProgressChanged += OnOpeningProgressChanged;
        _unifiedRunLifecycle.Updated += OnUnifiedRunLifecycleUpdated;
        eventSink.EventPublished += OnTaskEvent;
        if (_historicalDashboard is not null)
        {
            _historicalDashboard.Changed += OnHistoricalDashboardChanged;
            ApplyHistoricalDashboard(_historicalDashboard.Current);
        }
        LogFilePath = eventSink.LogFilePath;
        eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            TaskEventLevel.Information,
            "SessionStarted",
            $"测试会话日志：{LogFilePath}；" +
            $"管理员权限：{IsRunningAsAdministrator()}"));
        RefreshWindows();
    }

    public ObservableCollection<GameWindowInfo> Windows { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<OverlayLogLine> OverlayLogLines { get; } = [];
    public ObservableCollection<HistoricalDashboardRow> DashboardRows { get; } = [];
    public ObservableCollection<HistoricalDetailNodeViewModel>
        DetailedHistoryNodes { get; } = [];
    public ObservableCollection<IncompleteRunViewModel> IncompleteRuns { get; } = [];
    public CommunityContactOptions Community { get; }

    public string IncompleteRunsSummary => IncompleteRuns.Count == 0
        ? "没有未完成记录"
        : $"{IncompleteRuns.Count} 个未完成记录";

    public event EventHandler<RunResumeRequestedEventArgs>? ResumeRequested;

    public string DashboardDamageScaleLabel
    {
        get => _dashboardDamageScaleLabel;
        private set => SetProperty(ref _dashboardDamageScaleLabel, value);
    }

    public string DashboardTheoryScaleLabel
    {
        get => _dashboardTheoryScaleLabel;
        private set => SetProperty(ref _dashboardTheoryScaleLabel, value);
    }

    public string DashboardSummaryText
    {
        get => _dashboardSummaryText;
        private set => SetProperty(ref _dashboardSummaryText, value);
    }

    public string DetailedHistorySummary
    {
        get => _detailedHistorySummary;
        private set => SetProperty(ref _detailedHistorySummary, value);
    }

    public HistoricalDetailNodeViewModel? SelectedDetailedHistoryNode
    {
        get => _selectedDetailedHistoryNode;
        set => SetProperty(ref _selectedDetailedHistoryNode, value);
    }
    public ObservableCollection<CompletedRunViewModel> CompletedRuns { get; } = [];

    /// <summary>
    /// 当前实时对局的节点原始数据（含快照/备战状态/战斗），
    /// 供实时对局报告生成使用（未在记录时为空）。
    /// </summary>
    public IReadOnlyList<HistoricalNodeDetailEntry> RealtimeNodeEntries =>
        _historicalDashboard?.Current.DetailNodes ?? [];
    private CompletedRunViewModel? _selectedCompletedRun;
    public CompletedRunViewModel? SelectedCompletedRun
    {
        get => _selectedCompletedRun;
        set
        {
            if (SetProperty(ref _selectedCompletedRun, value))
            {
                // 切换存档时节点下拉回到第一个节点。
                NodeSelectionIndex = 0;
            }
        }
    }

    private int _nodeSelectionIndex;
    public int NodeSelectionIndex
    {
        get => _nodeSelectionIndex;
        set => SetProperty(ref _nodeSelectionIndex, value);
    }
    public ObservableCollection<RerollProfileViewModel> RerollProfiles
    {
        get;
    } = [];
    public ObservableCollection<SpecialCombinationViewModel> SpecialCombinations { get; } = [];
    public IReadOnlyList<CombinationRuleOption> CombinationRuleOptions { get; }
    public FilterGroupViewModel InvestmentEnvironmentFilters { get; }
    public FilterGroupViewModel CompetitorFilters { get; }
    public FilterGroupViewModel EnemyAffixFilters { get; }
    public FilterGroupViewModel InvestmentStrategyFilters { get; }
    public InvestmentStrategyRarityViewModel PrismaticInvestmentStrategies
    {
        get;
    }
    public InvestmentStrategyRarityViewModel GoldInvestmentStrategies
    {
        get;
    }
    public InvestmentStrategyRarityViewModel SilverInvestmentStrategies
    {
        get;
    }
    public CharacterAutomationGroupViewModel CharacterAutomationRules { get; }
    public IReadOnlyList<FilterModeOption> CombinationConditionOptions =>
        FilterModeOptions.CombinationConditions;
    public IReadOnlyList<GameModeOption> GameModeOptions { get; } =
    [
        new(
            CurrencyWarsGameMode.Standard,
            "标准博弈",
            "默认模式，开局包含两层奖励关"),
        new(
            CurrencyWarsGameMode.Overclock,
            "超频博弈",
            "超频模式，开局只包含一层奖励关")
    ];
    public IReadOnlyList<GameSourceOption> GameSourceOptions { get; } =
    [
        new(
            GameSourcePreference.Automatic,
            "自动",
            "优先查找本地客户端，并识别可能运行云游戏的浏览器"),
        new(
            GameSourcePreference.LocalClient,
            "本地客户端",
            "只显示《崩坏：星穹铁道》Windows客户端"),
        new(
            GameSourcePreference.CloudBrowser,
            "云游戏浏览器",
            "显示可用于“云·星穹铁道”的浏览器窗口"),
        new(
            GameSourcePreference.AnyWindow,
            "手动选择窗口",
            "显示所有可用顶层窗口，由用户手动指定")
    ];
    public IReadOnlyList<BenchSaleModeOption> BenchSaleModeOptions { get; } =
    [
        new(
            PreparationBenchSaleMode.SellAll,
            "无视金币价值，出售全部可卖角色",
            "保留已上场角色、手动勾选保留角色，以及已开启的三仙舟/DOT 自动保留角色；其余备战席角色全部出售。"),
        new(
            PreparationBenchSaleMode.InterestThreshold,
            "达到利息档时出售",
            "计算全部可卖角色的价值；只有当前金币加出售价值能达到所选 10/20 金币档时才出售。"),
        new(
            PreparationBenchSaleMode.None,
            "完全关闭，不出售（默认）",
            "布阵后保留备战席全部剩余角色，不执行任何出售输入。")
    ];
    public IReadOnlyList<int> BenchSaleInterestThresholdOptions { get; } =
        [10, 20];
    public string LogFilePath { get; }

    private static InvestmentStrategyRarityViewModel
        CreateInvestmentStrategyRarityGroup(
            string rarity,
            IReadOnlyList<InvestmentStrategyData> orderedStrategies,
            IReadOnlyDictionary<string, FilterItemViewModel>
                selectableStrategiesById)
    {
        var matching = orderedStrategies
            .Where(item =>
                string.Equals(
                    item.Rarity,
                    rarity,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return new InvestmentStrategyRarityViewModel(
            rarity,
            matching
                .Where(item => item.AvailablePlanes.Contains(1))
                .Select(item => selectableStrategiesById[item.Id]),
            matching
                .Where(item => !item.AvailablePlanes.Contains(1))
                .Select(item => CreateInvestmentStrategyItem(
                    item,
                    isSelectable: false)));
    }

    private static FilterItemViewModel CreateInvestmentStrategyItem(
        InvestmentStrategyData item,
        bool isSelectable)
    {
        var introducedVersion =
            InvestmentStrategyVersionCatalog.GetIntroducedVersion(item.Id);
        var versionLabel = introducedVersion is null
            ? ""
            : $"{introducedVersion} 新增 · ";
        return new FilterItemViewModel(
            item.Id,
            item.Name,
            $"{versionLabel}{item.Rarity} · {item.Effect}",
            supportsForbidden: false,
            isSelectable,
            isSelectable
                ? ""
                : "第一位面不会出现，无法刷取");
    }

    public GameWindowInfo? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (SetProperty(ref _selectedWindow, value))
            {
                if (value is not null)
                {
                    _lastWindowProcessName = value.ProcessName;
                    _lastWindowTitle = value.Title;
                    if (value.HostClientAreaOverride is { } host)
                    {
                        _savedGameArea = host.Width > 0 && host.Height > 0
                            ? new NormalizedRect(
                                (value.ClientArea.X - host.X) /
                                (double)host.Width,
                                (value.ClientArea.Y - host.Y) /
                                (double)host.Height,
                                value.ClientArea.Width / (double)host.Width,
                                value.ClientArea.Height / (double)host.Height)
                            : null;
                        _savedGameAreaSource = value.SourceKind;
                    }
                    else
                    {
                        _savedGameArea = null;
                        _savedGameAreaSource = null;
                    }
                }

                NotifyCommands();
            }
        }
    }

    public ImageSource? Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    public CurrencyWarsGameMode SelectedGameMode
    {
        get => _selectedGameMode;
        set => SetProperty(ref _selectedGameMode, value);
    }

    public GameSourcePreference SelectedGameSource
    {
        get => _selectedGameSource;
        set
        {
            if (SetProperty(ref _selectedGameSource, value))
            {
                RefreshWindows();
            }
        }
    }

    public void ApplyGameAreaBinding(GameWindowInfo binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        for (var index = 0; index < Windows.Count; index++)
        {
            if (Windows[index].Handle == binding.Handle)
            {
                Windows[index] = binding;
                break;
            }
        }

        SelectedWindow = binding;
        Status = binding.IsReadyForAutomation
            ? $"游戏画面已定位：{binding.SourceDisplayName} " +
              $"{binding.ClientArea.Width}×{binding.ClientArea.Height}"
            : binding.BindingMessage;
    }

    public bool BringWindowToForeground(GameWindowInfo window) =>
        _windowService.BringToForeground(window);

    public GameWindowInfo? BindGameArea(
        nint handle,
        PixelRect gameArea,
        GameWindowSourceKind sourceKind)
    {
        var binding = _windowService.BindGameArea(
            handle,
            gameArea,
            sourceKind);
        if (binding is not null)
        {
            ApplyGameAreaBinding(binding);
        }

        return binding;
    }

    public bool EnableUnknownPageEscapeRecovery
    {
        get => _enableUnknownPageEscapeRecovery;
        set => SetProperty(ref _enableUnknownPageEscapeRecovery, value);
    }

    public bool ShowFilterDescriptions
    {
        get => _showFilterDescriptions;
        set => SetProperty(ref _showFilterDescriptions, value);
    }

    public bool IsLogOverlayClickThrough
    {
        get => _isLogOverlayClickThrough;
        set
        {
            if (SetProperty(ref _isLogOverlayClickThrough, value))
            {
                OnPropertyChanged(nameof(LogOverlayClickThroughLabel));
            }
        }
    }

    public bool CanConfigureLogOverlayClickThrough => true;

    public string LogOverlayClickThroughLabel =>
        IsLogOverlayClickThrough
            ? "日志鼠标穿透：开"
            : "日志鼠标穿透：关";

    public double LogOverlayOpacity
    {
        get => _logOverlayOpacity;
        set => SetProperty(
            ref _logOverlayOpacity,
            Math.Clamp(value, 0.25, 1.0));
    }

    public bool ShowLogOverlay
    {
        get => _showLogOverlay;
        set => SetProperty(ref _showLogOverlay, value);
    }

    public bool EnableDiagnosticLogging
    {
        get => _enableDiagnosticLogging;
        set
        {
            if (SetProperty(ref _enableDiagnosticLogging, value))
            {
                _eventSink.DiagnosticLoggingEnabled = value;
            }
        }
    }

    public bool DeleteScreenshotsAfterRunCompletion
    {
        get => _deleteScreenshotsAfterRunCompletion;
        set => SetProperty(ref _deleteScreenshotsAfterRunCompletion, value);
    }

    public bool EnableRewardStageAutomation
    {
        get => _enableRewardStageAutomation;
        set => SetProperty(ref _enableRewardStageAutomation, value);
    }

    public bool EnableEarlyStrongFormationPurchase
    {
        get => _enableEarlyStrongFormationPurchase;
        set => SetProperty(ref _enableEarlyStrongFormationPurchase, value);
    }

    public bool EnableGalaxyScholarRewardStrategy
    {
        get => _enableGalaxyScholarRewardStrategy;
        set => SetProperty(ref _enableGalaxyScholarRewardStrategy, value);
    }

    /// <summary>
    /// 快速刷开局模式：稳定版（完整验证）/ 快速版（去验证）/ 极速版（无脑部署）。
    /// </summary>
    public FastRerollMode FastRerollMode
    {
        get => _fastRerollMode;
        set => SetProperty(ref _fastRerollMode, value);
    }

    public IReadOnlyList<FastRerollModeOption> FastRerollModeOptions { get; } =
    [
        new(FastRerollMode.Stable, "稳定版", "完整识别与验证流程（当前版本行为）"),
        new(FastRerollMode.Fast, "快速版", "去掉备战页全部验证，OCR 仅识别两次，其余按内部状态机推算"),
        new(FastRerollMode.Extreme, "极速版", "备战席前三个角色无脑拖到前台，其余备战逻辑全部去掉")
    ];

    public PreparationBenchSaleMode BenchSaleMode
    {
        get => _benchSaleMode;
        set
        {
            if (SetProperty(ref _benchSaleMode, value))
            {
                OnPropertyChanged(nameof(IsInterestBenchSaleSelected));
            }
        }
    }

    public bool IsInterestBenchSaleSelected =>
        BenchSaleMode == PreparationBenchSaleMode.InterestThreshold;

    public int BenchSaleInterestThreshold
    {
        get => _benchSaleInterestThreshold;
        set => SetProperty(
            ref _benchSaleInterestThreshold,
            value is 10 or 20 ? value : 10);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                if (value)
                {
                    CaptureRunningOpeningFilterModes();
                }
                else
                {
                    _runningOpeningFilterModes.Clear();
                }

                OnPropertyChanged(nameof(CanEditOpeningFilters));
                NotifyCommands();
            }
        }
    }

    public bool CanEditOpeningFilters =>
        !IsRunning && !IsPassiveCollectionRunning;

    public bool IsPassiveCollectionRunning =>
        _passiveCollectionCancellation is not null;

    public void BeginPassiveCollection(CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        if (_passiveCollectionCancellation is not null)
        {
            throw new InvalidOperationException("即时识别已经在运行。");
        }

        _passiveCollectionCancellation = cancellation;
        OnPropertyChanged(nameof(IsPassiveCollectionRunning));
        OnPropertyChanged(nameof(CanEditOpeningFilters));
        NotifyCommands();
        AssistanceActivated?.Invoke(this, EventArgs.Empty);
    }

    public void EndPassiveCollection(CancellationTokenSource cancellation)
    {
        if (!ReferenceEquals(_passiveCollectionCancellation, cancellation))
        {
            return;
        }

        _passiveCollectionCancellation = null;
        OnPropertyChanged(nameof(IsPassiveCollectionRunning));
        OnPropertyChanged(nameof(CanEditOpeningFilters));
        NotifyCommands();
    }

    private void SubscribeOpeningFilterGuards()
    {
        foreach (var item in InvestmentEnvironmentFilters.Items
                     .Concat(CompetitorFilters.Items)
                     .Concat(EnemyAffixFilters.Items))
        {
            item.PropertyChanged += OnOpeningFilterItemPropertyChanged;
        }
    }

    private void CaptureRunningOpeningFilterModes()
    {
        _runningOpeningFilterModes.Clear();
        foreach (var item in InvestmentEnvironmentFilters.Items
                     .Concat(CompetitorFilters.Items)
                     .Concat(EnemyAffixFilters.Items))
        {
            _runningOpeningFilterModes[item] = item.SelectionMode;
        }
    }

    private void OnOpeningFilterItemPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (!IsRunning ||
            eventArgs.PropertyName != nameof(FilterItemViewModel.SelectionMode) ||
            sender is not FilterItemViewModel item ||
            !_runningOpeningFilterModes.TryGetValue(item, out var acceptedMode) ||
            item.SelectionMode == acceptedMode)
        {
            return;
        }

        var rejectedMode = item.SelectionMode;
        item.SelectionMode = acceptedMode;
        _eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            TaskEventLevel.Warning,
            "OpeningFilterMutationRejectedWhileRunning",
            $"运行期间拒绝筛选变更：{item.Id}，{acceptedMode}->{rejectedMode}"));
    }

    public string NewCombinationName
    {
        get => _newCombinationName;
        set => SetProperty(ref _newCombinationName, value);
    }

    public AppFilterSelectionMode NewCombinationCondition
    {
        get => _newCombinationCondition;
        set => SetProperty(ref _newCombinationCondition, value);
    }

    public string NewCombinationInvestmentEnvironments
    {
        get => _newCombinationInvestmentEnvironments;
        set => SetProperty(ref _newCombinationInvestmentEnvironments, value);
    }

    public string NewCombinationCompetitors
    {
        get => _newCombinationCompetitors;
        set => SetProperty(ref _newCombinationCompetitors, value);
    }

    public string NewCombinationEnemyAffixes
    {
        get => _newCombinationEnemyAffixes;
        set => SetProperty(ref _newCombinationEnemyAffixes, value);
    }

    public RelayCommand RefreshWindowsCommand { get; }
    public AsyncRelayCommand CaptureCommand { get; }
    public AsyncRelayCommand ObserveCommand { get; }
    public AsyncRelayCommand AutoRerollCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand AddCombinationCommand { get; }
    public RelayCommand<SpecialCombinationViewModel> DeleteCombinationCommand { get; }
    public AsyncRelayCommand RefreshIncompleteRunsCommand { get; }
    public RelayCommand<IncompleteRunViewModel> ContinueIncompleteRunCommand { get; }
    public RelayCommand<IncompleteRunViewModel> SettleIncompleteRunCommand { get; }
    public RelayCommand<IncompleteRunViewModel> DeleteIncompleteRunCommand { get; }

    public event EventHandler? AssistanceActivated;

    public void RequestStop() => Stop();

    public void RequestShutdownStop()
    {
        lock (_activeOperationSync)
        {
            _shutdownRequested = true;
        }

        _runCancellation?.Cancel();
        _passiveCollectionCancellation?.Cancel();
        NotifyCommands();
    }

    public async Task<bool> WaitForIdleAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Task? activeTask;
        lock (_activeOperationSync)
        {
            activeTask = _activeOperationTask;
        }

        if (activeTask is null || activeTask.IsCompleted)
        {
            return true;
        }

        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await activeTask.WaitAsync(timeoutCancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
            when (timeoutCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            // A completed faulted task no longer owns process lifetime. Its
            // command body is responsible for publishing the original error.
            return true;
        }
    }

    private Task TrackActiveOperationAsync(Func<Task> operation)
    {
        lock (_activeOperationSync)
        {
            if (_shutdownRequested)
            {
                return Task.CompletedTask;
            }

            if (_activeOperationTask is { IsCompleted: false })
            {
                return _activeOperationTask;
            }

            var generation = ++_activeOperationGeneration;
            _activeOperationTask = TrackActiveOperationCoreAsync(
                operation,
                generation);
            return _activeOperationTask;
        }
    }

    private async Task TrackActiveOperationCoreAsync(
        Func<Task> operation,
        long generation)
    {
        try
        {
            await operation();
        }
        finally
        {
            lock (_activeOperationSync)
            {
                if (_activeOperationGeneration == generation)
                {
                    _activeOperationTask = null;
                }
            }
        }
    }

    public void ReportUiStatus(string status) => Status = status;

    public async Task RefreshCompletedRunsAsync()
    {
        try
        {
            var records = await _runStore
                .ListCompletedRunsAsync(CancellationToken.None)
                .ConfigureAwait(true);

            void Apply()
            {
                CompletedRuns.Clear();
                foreach (var record in records)
                {
                    var nodes = record.Nodes
                        .Select((node, index) => _detailPresentation.Build(
                            new HistoricalNodeDetailEntry(
                                record.RunId,
                                node.NodeId,
                                node.FinalPreparationSnapshot,
                                node.FinalPreparationState,
                                node.FinalPreparationState,
                                node.FinalBattle,
                                null,
                                record.CompletedAt,
                                node.PreparationAnalysisFile,
                                node.FinalBattleFile),
                            HistoricalDetailPresentationBuilder.BuildCompletedEconomy(
                                record.Nodes,
                                index)))
                        .ToArray();
                    var identity = _detailPresentation.BuildIdentity(
                        record.IdentityEvidence);
                    var archive = _detailPresentation.BuildArchiveMetadata(record);
                    var (damageLine, goldLine, healthLine) = BuildChartLines(record.Nodes);
                    var nodeCards = _detailPresentation.BuildNodeCards(record.Nodes);
                    CompletedRuns.Add(new CompletedRunViewModel(
                        record.RunId,
                        $"{record.CompletedAt:MM-dd HH:mm} · " +
                        $"{nodes.Length} 节点 · 结束于 {record.CompletionNodeId}",
                        record.CompletedAt,
                        archive.Fields,
                        identity.Fields,
                        nodes,
                        damageLine,
                        goldLine,
                        healthLine,
                        nodeCards));
                }

                // 打开历史对局窗口时默认选中第一个存档，右侧直接展示内容。
                if (SelectedCompletedRun is null)
                {
                    SelectedCompletedRun = CompletedRuns.FirstOrDefault();
                }
            }

            if (System.Windows.Application.Current.Dispatcher
                .CheckAccess())
            {
                Apply();
            }
            else
            {
                await System.Windows.Application.Current.Dispatcher
                    .InvokeAsync(Apply);
            }
        }
        catch (Exception exception)
        {
            Status = $"历史对局加载失败：{exception.Message}";
        }
    }

    /// <summary>
    /// 没有正在记录的对局时，把最近一个存档的对局节点加载进
    /// "对局历史详细信息"窗口，避免窗口空白（用户可再点右上角
    /// 【历史对局】查看全部存档）。
    /// </summary>
    public void LoadLatestArchiveIntoDetailedHistory()
    {
        if (DetailedHistoryNodes.Count > 0 || CompletedRuns.Count == 0)
        {
            return;
        }

        var archive = CompletedRuns[0];
        DetailedHistoryNodes.Clear();
        foreach (var node in archive.Nodes)
        {
            DetailedHistoryNodes.Add(node);
        }

        SelectedDetailedHistoryNode = DetailedHistoryNodes.FirstOrDefault();
        DetailedHistorySummary =
            $"正在浏览历史存档（{archive.Nodes.Count} 个节点，结束于 {archive.Nodes.LastOrDefault()?.NodeId ?? "未知"}）。" +
            "实时对局未在记录；查看全部存档请点右上角【历史对局】。";
    }

    /// <summary>
    /// 把存档节点的伤害/金币/血量数值归一化成折线点串，
    /// 供历史对局窗口的"节点数值趋势"图使用（缺失值用上一有效值延续）。
    /// </summary>
    private static (string Damage, string Gold, string Health) BuildChartLines(
        IReadOnlyList<CompletedRunNodeRecord> nodes)
    {
        const double width = 560;
        const double height = 96;
        const double topPad = 6;

        var damage = nodes.Select(n => n.FinalBattle?.TotalDamage).ToArray();
        var gold = nodes.Select(n => n.FinalPreparationSnapshot?.Economy?.Value).ToArray();
        var health = nodes.Select(n => n.FinalPreparationSnapshot?.Health?.Value).ToArray();

        static string BuildLine<T>(IReadOnlyList<T?> values)
            where T : struct
        {
            var max = values.Where(v => v.HasValue)
                .Select(v => Convert.ToDouble(v!.Value))
                .DefaultIfEmpty(1d)
                .Max();
            var points = new List<string>(values.Count);
            double? last = null;
            for (var i = 0; i < values.Count; i++)
            {
                if (values[i] is { } value)
                {
                    last = Convert.ToDouble(value);
                }

                var x = values.Count <= 1 ? 0d : (double)i / (values.Count - 1) * width;
                var y = last is { } v
                    ? topPad + (1 - v / max) * (height - topPad * 2)
                    : height / 2;
                points.Add($"{x:0.##},{y:0.##}");
            }

            return string.Join(" ", points);
        }

        return (BuildLine(damage), BuildLine(gold), BuildLine(health));
    }

    public async Task RefreshIncompleteRunsAsync()
    {
        try
        {
            var summaries = await _runStore
                .ListIncompleteRunsAsync(CancellationToken.None)
                .ConfigureAwait(true);

            void Apply()
            {
                IncompleteRuns.Clear();
                foreach (var summary in summaries)
                {
                    IncompleteRuns.Add(new IncompleteRunViewModel(summary));
                }

                OnPropertyChanged(nameof(IncompleteRunsSummary));
                ContinueIncompleteRunCommand.NotifyCanExecuteChanged();
                SettleIncompleteRunCommand.NotifyCanExecuteChanged();
                DeleteIncompleteRunCommand.NotifyCanExecuteChanged();
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(Apply);
            }
            else
            {
                Apply();
            }
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            PublishApplicationWarning(
                "IncompleteRunDiscoveryFailed",
                $"未完成对局列表读取失败：{exception.Message}。原始记录未被修改。");
        }
    }

    private void RequestResume(IncompleteRunViewModel? run)
    {
        if (run is null)
        {
            return;
        }

        ResumeRequested?.Invoke(this, new RunResumeRequestedEventArgs(run.Summary));
    }

    /// <summary>
    /// 现场生成最新已完成对局的挑战总结报告（HTML），返回文件路径。
    /// 供主界面"挑战总结（实验版）"入口在报告未生成时调用。
    /// </summary>
    public async Task<string?> TryGenerateLatestChallengeReportAsync()
    {
        try
        {
            var records = await _runStore
                .ListCompletedRunsAsync(CancellationToken.None)
                .ConfigureAwait(true);
            var latest = records
                .OrderByDescending(item => item.CompletedAt)
                .FirstOrDefault();
            if (latest is null || _summaryReportGenerator is null)
            {
                return null;
            }

            return await _summaryReportGenerator.GenerateAsync(
                    _runStore.GetRunDirectory(latest.RunId),
                    latest,
                    CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
            when (exception is IOException or
                  UnauthorizedAccessException or
                  InvalidDataException or
                  JsonException)
        {
            PublishApplicationWarning(
                "ChallengeReportGenerationFailed",
                $"挑战总结报告现场生成失败：{exception.Message}。");
            return null;
        }
    }

    private async Task SettleIncompleteRunAsync(IncompleteRunViewModel run)
    {
        var confirmation = MessageBox.Show(
            $"确认结算这条未完成对局吗？\n\n开始时间：{run.StartedAtDisplay}\n" +
            $"最后节点：{run.LastNodeDisplay}\n\n未记录的数据会保留为缺失值。",
            "结算未完成对局",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var checkpoint = run.Summary.Checkpoint;
            var nodeId = string.IsNullOrWhiteSpace(
                    checkpoint.LastConfirmedNodeId)
                ? "unknown"
                : checkpoint.LastConfirmedNodeId;
            var archive = await _runStore.CompleteRunAsync(
                    run.RunId,
                    DateTimeOffset.UtcNow,
                    "manual_settlement",
                    nodeId,
                    null,
                    "手动结算（数据可能不完整）",
                    CancellationToken.None)
                .ConfigureAwait(true);
            if (_summaryReportGenerator is not null)
            {
                try
                {
                    await _summaryReportGenerator.GenerateAsync(
                            _runStore.GetRunDirectory(run.RunId),
                            archive,
                            CancellationToken.None)
                        .ConfigureAwait(true);
                }
                catch (Exception exception)
                    when (exception is IOException or
                          UnauthorizedAccessException or
                          InvalidDataException or
                          JsonException)
                {
                    PublishApplicationWarning(
                        "ManualSettlementReportFailed",
                        $"对局已经结算，但总结报告生成失败：{exception.Message}");
                }
            }

            Status = $"已结算对局：{run.StartedAtDisplay}";
            await RefreshIncompleteRunsAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
            when (exception is IOException or
                  UnauthorizedAccessException or
                  InvalidOperationException or
                  InvalidDataException or
                  JsonException)
        {
            PublishApplicationWarning(
                "IncompleteRunSettlementFailed",
                $"未完成对局结算失败：{exception.Message}");
        }
    }

    private async Task DeleteIncompleteRunAsync(IncompleteRunViewModel run)
    {
        var confirmation = MessageBox.Show(
            $"确认永久删除这条未完成对局吗？\n\n开始时间：{run.StartedAtDisplay}\n" +
            $"最后节点：{run.LastNodeDisplay}\n\n此操作不可恢复。",
            "删除未完成对局",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _runStore.DeleteIncompleteRunAsync(
                    run.RunId,
                    CancellationToken.None)
                .ConfigureAwait(true);
            Status = $"已删除未完成对局：{run.StartedAtDisplay}";
            await RefreshIncompleteRunsAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
            when (exception is IOException or
                  UnauthorizedAccessException or
                  InvalidOperationException)
        {
            PublishApplicationWarning(
                "IncompleteRunDeletionFailed",
                $"未完成对局删除失败：{exception.Message}");
        }
    }

    public RerollProfileEditorViewModel CreateRerollProfileEditor(
        RerollProfileViewModel? existing = null) =>
        new(_gameData, existing);

    public void AddOrReplaceRerollProfile(RerollProfileViewModel profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var existing = RerollProfiles.FirstOrDefault(item =>
            string.Equals(
                item.Id,
                profile.Id,
                StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            RerollProfiles.Add(profile);
            Status = $"已添加刷取方案：{profile.Name}";
            return;
        }

        var index = RerollProfiles.IndexOf(existing);
        RerollProfiles[index] = profile;
        Status = $"已更新刷取方案：{profile.Name}";
    }

    public void DeleteRerollProfile(RerollProfileViewModel profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (RerollProfiles.Remove(profile))
        {
            Status = $"已删除刷取方案：{profile.Name}";
        }
    }

    public void SaveUserSettings()
    {
        try
        {
            var path = GetUserSettingsPath();
            Directory.CreateDirectory(
                Path.GetDirectoryName(path)!);
            var settings = new UserSettingsSnapshot(
                ShowLogOverlay,
                EnableUnknownPageEscapeRecovery,
                EnableRewardStageAutomation,
                EnableEarlyStrongFormationPurchase,
                IsLogOverlayClickThrough,
                LogOverlayOpacity,
                SelectedGameMode,
                CharacterAutomationRules.Items
                    .Where(item => item.IsRetained)
                    .Select(item => item.Id)
                    .ToArray(),
                CharacterAutomationRules.Items
                    .Where(item => item.IsAutoPurchased)
                    .Select(item => item.Id)
                    .ToArray(),
                InvestmentStrategyFilters.Items
                    .Where(item =>
                        item.SelectionMode ==
                        AppFilterSelectionMode.Required)
                    .Select(item => item.Id)
                    .ToArray(),
                RerollProfiles.Select(ToSnapshot).ToArray(),
                BenchSaleMode,
                BenchSaleInterestThreshold,
                EnableGalaxyScholarRewardStrategy,
                FilterSelectionSettings.Capture(
                    InvestmentEnvironmentFilters.Items),
                FilterSelectionSettings.Capture(CompetitorFilters.Items),
                FilterSelectionSettings.Capture(EnemyAffixFilters.Items),
                SpecialCombinationSettings.Capture(SpecialCombinations),
                EnableDiagnosticLogging,
                GameSource: SelectedGameSource,
                LastWindowProcessName: SelectedWindow?.ProcessName,
                LastWindowTitle: SelectedWindow?.Title,
                LastGameArea: _savedGameArea,
                LastGameAreaSource: _savedGameAreaSource,
                DeleteScreenshotsAfterRunCompletion:
                    DeleteScreenshotsAfterRunCompletion,
                FastReroll: FastRerollMode);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    settings,
                    UserSettingsJsonOptions));
            _eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Information,
                "UserSettingsSaved",
                $"用户设置已保存：{path}"));
        }
        catch (Exception exception)
        {
            PublishApplicationWarning(
                "UserSettingsSaveFailed",
                $"用户设置保存失败：{exception.Message}");
        }
    }

    public void ReportUnhandledException(Exception exception)
    {
        _runCancellation?.Cancel();
        Status = $"界面异常已拦截，自动操作已停止：{exception.Message}";
        PublishApplicationError("UnhandledUiException", exception);
    }

    public bool AddProhibitedCombination(
        CombinationRuleOption first,
        CombinationRuleOption second)
    {
        if (first.Kind == second.Kind &&
            string.Equals(first.Id, second.Id, StringComparison.OrdinalIgnoreCase))
        {
            Status = "组合规则的两个条件不能相同。";
            return false;
        }

        var selected = new[] { first, second };
        static string NamesFor(
            IEnumerable<CombinationRuleOption> options,
            AppCombinationItemKind kind) =>
            string.Join(
                "、",
                options
                    .Where(item => item.Kind == kind)
                    .Select(item => item.Name)
                    .DefaultIfEmpty("不限制"));

        var name = $"{first.Name} + {second.Name}";
        SpecialCombinations.Add(new SpecialCombinationViewModel(
            name,
            AppFilterSelectionMode.Forbidden,
            NamesFor(selected, AppCombinationItemKind.InvestmentEnvironment),
            NamesFor(selected, AppCombinationItemKind.Competitor),
            NamesFor(selected, AppCombinationItemKind.EnemyAffix),
            false));
        Status = $"已添加禁止共存组合：{name}";
        return true;
    }

    private bool CanStart() =>
        !_shutdownRequested &&
        SelectedWindow?.IsReadyForAutomation == true &&
        !IsRunning &&
        !IsPassiveCollectionRunning;

    private void LoadUserSettings()
    {
        var path = GetUserSettingsPath();
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<UserSettingsSnapshot>(
                File.ReadAllText(path));
            if (settings is null)
            {
                return;
            }

            _openingFilterSettingsSource = "user-settings";

            ShowLogOverlay = settings.ShowLogOverlay;
            EnableDiagnosticLogging = settings.EnableDiagnosticLogging;
            DeleteScreenshotsAfterRunCompletion =
                settings.DeleteScreenshotsAfterRunCompletion;
            EnableUnknownPageEscapeRecovery =
                settings.EnableUnknownPageEscapeRecovery;
            EnableRewardStageAutomation =
                settings.EnableRewardStageAutomation;
            EnableEarlyStrongFormationPurchase =
                settings.EnableEarlyStrongFormationPurchase;
            EnableGalaxyScholarRewardStrategy =
                settings.EnableGalaxyScholarRewardStrategy;
            FastRerollMode = Enum.IsDefined(settings.FastReroll)
                ? settings.FastReroll
                : FastRerollMode.Stable;
            BenchSaleMode = Enum.IsDefined(settings.BenchSaleMode)
                ? settings.BenchSaleMode
                : PreparationBenchSaleMode.None;
            BenchSaleInterestThreshold =
                settings.BenchSaleInterestThreshold is 10 or 20
                    ? settings.BenchSaleInterestThreshold
                    : 10;
            IsLogOverlayClickThrough =
                settings.IsLogOverlayClickThrough;
            LogOverlayOpacity = settings.LogOverlayOpacity;
            SelectedGameMode = Enum.IsDefined(settings.GameMode)
                ? settings.GameMode
                : CurrencyWarsGameMode.Standard;
            _selectedGameSource = Enum.IsDefined(settings.GameSource)
                ? settings.GameSource
                : GameSourcePreference.Automatic;
            _lastWindowProcessName = settings.LastWindowProcessName;
            _lastWindowTitle = settings.LastWindowTitle;
            _savedGameArea = settings.LastGameArea;
            _savedGameAreaSource = settings.LastGameAreaSource;
            var retained = (settings.RetainedCharacterIds ?? []).ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            var autoPurchase =
                (settings.AutoPurchaseCharacterIds ?? []).ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            var legacyImplicitRetention = _gameData.CurrencyWarsCharacters
                .Where(item => InitialRewardFormationPlanner
                    .DefaultEligibleCharacterNames.Contains(item.Name))
                .Select(item => item.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (retained.SetEquals(legacyImplicitRetention))
            {
                retained.Clear();
                PublishApplicationWarning(
                    "LegacyImplicitCharacterRetentionCleared",
                    "检测到旧版本把默认奖励关出战名单误存成了用户保留名单；" +
                    "已迁移为空的自定义保留名单。三仙舟+2DOT 独立策略不受影响。");
            }

            foreach (var character in CharacterAutomationRules.Items)
            {
                character.IsRetained = retained.Contains(character.Id);
                character.IsAutoPurchased =
                    autoPurchase.Contains(character.Id);
            }

            var preferredStrategies =
                (settings.PreferredInvestmentStrategyIds ?? []).ToHashSet(
                    StringComparer.OrdinalIgnoreCase);
            foreach (var strategy in InvestmentStrategyFilters.Items)
            {
                strategy.SelectionMode =
                    preferredStrategies.Contains(strategy.Id)
                        ? AppFilterSelectionMode.Required
                        : AppFilterSelectionMode.Unrestricted;
            }

            FilterSelectionSettings.Apply(
                settings.InvestmentEnvironmentFilters,
                InvestmentEnvironmentFilters.Items);
            FilterSelectionSettings.Apply(
                settings.CompetitorFilters,
                CompetitorFilters.Items);
            FilterSelectionSettings.Apply(
                settings.EnemyAffixFilters,
                EnemyAffixFilters.Items);
            if (settings.InvestmentEnvironmentFilters is null ||
                settings.CompetitorFilters is null ||
                settings.EnemyAffixFilters is null)
            {
                PublishApplicationWarning(
                    "LegacyOpeningFilterSelectionsMissing",
                    "当前设置文件来自未保存开局筛选项的旧版本；" +
                    "请重新勾选一次投资环境、敌方阵营和负面词条。" +
                    "新版保存后，关闭程序、换包和重启都会保留这些选择。");
            }

            RerollProfiles.Clear();
            foreach (var profile in settings.RerollProfiles ?? [])
            {
                RerollProfiles.Add(FromSnapshot(profile));
            }

            if (settings.SpecialCombinations is not null)
            {
                SpecialCombinations.Clear();
                foreach (var combination in SpecialCombinationSettings.Restore(
                             settings.SpecialCombinations))
                {
                    SpecialCombinations.Add(combination);
                }
            }
        }
        catch (Exception exception)
        {
            PublishApplicationWarning(
                "UserSettingsLoadFailed",
                $"用户设置读取失败，已使用默认值：{exception.Message}");
        }
    }

    private static string GetUserSettingsPath() =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            ProductIdentity.UserDataDirectoryName,
            "user-settings.json");

    private void AddCombination()
    {
        var name = NewCombinationName.Trim();
        var investments = NewCombinationInvestmentEnvironments.Trim();
        var competitors = NewCombinationCompetitors.Trim();
        var affixes = NewCombinationEnemyAffixes.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Status = "自定义组合需要填写名称。";
            return;
        }

        if (string.IsNullOrWhiteSpace(investments) &&
            string.IsNullOrWhiteSpace(competitors) &&
            string.IsNullOrWhiteSpace(affixes))
        {
            Status = "自定义组合至少需要填写一类名称。";
            return;
        }

        SpecialCombinations.Add(new SpecialCombinationViewModel(
            name,
            NewCombinationCondition,
            string.IsNullOrWhiteSpace(investments) ? "不限制" : investments,
            string.IsNullOrWhiteSpace(competitors) ? "不限制" : competitors,
            string.IsNullOrWhiteSpace(affixes) ? "不限制" : affixes,
            false));
        NewCombinationName = "";
        NewCombinationInvestmentEnvironments = "";
        NewCombinationCompetitors = "";
        NewCombinationEnemyAffixes = "";
        Status = $"已添加自定义组合：{name}";
    }

    private void DeleteCombination(SpecialCombinationViewModel combination)
    {
        if (!combination.IsBuiltIn && SpecialCombinations.Remove(combination))
        {
            Status = $"已删除自定义组合：{combination.Name}";
        }
    }

    public void RefreshWindows()
    {
        var previousHandle = SelectedWindow?.Handle;
        Windows.Clear();
        foreach (var candidate in _windowService.FindCandidates(
                     SelectedGameSource))
        {
            Windows.Add(TryRestoreSavedGameArea(candidate));
        }

        SelectedWindow =
            Windows.FirstOrDefault(window => window.Handle == previousHandle) ??
            Windows.FirstOrDefault(window =>
                string.Equals(
                    window.ProcessName,
                    _lastWindowProcessName,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    window.Title,
                    _lastWindowTitle,
                    StringComparison.Ordinal)) ??
            Windows.FirstOrDefault();
        Status = SelectedWindow is null
            ? "未发现符合当前来源设置的游戏窗口"
            : SelectedWindow.IsReadyForAutomation
                ? $"已发现：{SelectedWindow}"
                : $"已选择：{SelectedWindow.Title}；" +
                  $"{SelectedWindow.BindingMessage}";
        _eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            SelectedWindow is null
                ? TaskEventLevel.Warning
                : TaskEventLevel.Information,
            "WindowDiscovery",
            Status));
    }

    private GameWindowInfo TryRestoreSavedGameArea(GameWindowInfo candidate)
    {
        if (_savedGameArea is not { } savedArea ||
            _savedGameAreaSource is not { } source ||
            !string.Equals(
                candidate.ProcessName,
                _lastWindowProcessName,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                candidate.Title,
                _lastWindowTitle,
                StringComparison.Ordinal))
        {
            return candidate;
        }

        var host = candidate.HostClientArea;
        var relative = savedArea.ToPixels(host.Width, host.Height);
        var screenArea = relative with
        {
            X = host.X + relative.X,
            Y = host.Y + relative.Y
        };
        return _windowService.BindGameArea(
                   candidate.Handle,
                   screenArea,
                   source) ??
               candidate;
    }

    private async Task CaptureAsync()
    {
        var window = SelectedWindow;
        if (window is null)
        {
            return;
        }

        try
        {
            var refreshed = _windowService.Refresh(window.Handle)
                ?? throw new InvalidOperationException("游戏窗口已失效。");
            var frame = await _capture.CaptureAsync(refreshed, CancellationToken.None);
            Preview = frame.ToBitmapSource();
            Status = $"截图成功：{frame.Width}×{frame.Height}";
            _eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Information,
                "CaptureSucceeded",
                Status));
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            PublishApplicationError("CaptureFailed", exception);
        }
    }

    private async Task RunFilterAsync(bool singleRound)
    {
        var window = SelectedWindow;
        if (window is null)
        {
            return;
        }

        _runCancellation = new CancellationTokenSource();
        IsRunning = true;
        Status = "已接收开始指令，正在启动刷开局……";
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.Render);
        AssistanceActivated?.Invoke(this, EventArgs.Empty);
        var unifiedLifecycleStarted = false;
        var unifiedLifecycleClosed = false;
        try
        {
            if (!singleRound)
            {
                _unifiedRunLifecycle.BeginAutomaticReroll(
                    window.Handle,
                    _runCancellation.Token);
                unifiedLifecycleStarted = true;
            }

            var filters = BuildFilterSet();
            _eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Information,
                "FilterConfiguration",
                DescribeFilters(filters)));
            var configuration = Phase1RunConfiguration.Create(
                window.Handle,
                filters,
                new OpeningRerollLoopOptions
                {
                    MaximumRounds = singleRound ? 1 : null,
                    MaximumRuntime = singleRound
                        ? TimeSpan.FromMinutes(3)
                        : null,
                    DeployMatchedOpening = !singleRound,
                    CompleteRewardStages =
                        !singleRound && EnableRewardStageAutomation,
                    BenchSaleMode = BenchSaleMode,
                    BenchSaleInterestThreshold =
                        BenchSaleInterestThreshold,
                    RewardStage = new RewardStageAutomationOptions
                    {
                        EnableEarlyStrongFormationPurchase =
                            EnableEarlyStrongFormationPurchase,
                        EnableGalaxyScholarRewardStrategy =
                            EnableGalaxyScholarRewardStrategy,
                        AutoPurchaseCharacterNames =
                            CharacterAutomationRules.Items
                                .Where(item => item.IsAutoPurchased)
                                .Select(item => item.Name)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase),
                        RetainedCharacterNames =
                            CharacterAutomationRules.Items
                                .Where(item => item.IsRetained)
                                .Select(item => item.Name)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase),
                        PreferredInvestmentStrategyIds =
                            InvestmentStrategyFilters.Items
                                .Where(item =>
                                    item.SelectionMode ==
                                    AppFilterSelectionMode.Required)
                                .Select(item => item.Id)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    },
                    EnableUnknownPageEscapeRecovery =
                        EnableUnknownPageEscapeRecovery,
                    GameMode = SelectedGameMode,
                    FastReroll = FastRerollMode
                });
            var result = await _automation.RunAsync(
                configuration,
                _runCancellation.Token);
            Status = FormatLoopResult(result);
            if (unifiedLifecycleStarted)
            {
                if (result.Succeeded)
                {
                    Status = $"{Status} 已自动接入正式对局记录。";
                    _ = await _unifiedRunLifecycle.ContinueMatchedRunAsync(
                        _runCancellation.Token);
                }
                else
                {
                    await _unifiedRunLifecycle.EndWithoutMatchAsync();
                }

                unifiedLifecycleClosed = true;
            }
        }
        catch (OperationCanceledException)
        {
            Status = "任务已停止。";
            PublishApplicationWarning("FilterRunCancelled", Status);
        }
        catch (Exception exception)
        {
            Status = $"任务异常：{exception.Message}";
            PublishApplicationError("FilterRunFailed", exception);
        }
        finally
        {
            if (unifiedLifecycleStarted && !unifiedLifecycleClosed)
            {
                await _unifiedRunLifecycle.EndWithoutMatchAsync();
            }

            IsRunning = false;
            _runCancellation.Dispose();
            _runCancellation = null;
        }
    }

    private async Task RecognizeCurrentWindowAsync()
    {
        var window = SelectedWindow;
        if (window is null || _situationAnalyzer is null)
        {
            Status = "识别器尚未就绪。";
            return;
        }

        _runCancellation = new CancellationTokenSource();
        IsRunning = true;
        try
        {
            var refreshed = _windowService.Refresh(window.Handle)
                ?? throw new InvalidOperationException("游戏窗口已失效。");
            var frame = await _capture.CaptureAsync(
                refreshed,
                _runCancellation.Token);
            var analysis = await _situationAnalyzer.AnalyzeAsync(
                frame,
                "recognition-test",
                new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
                _runCancellation.Token);
            var page = analysis.Snapshot.PageId.Status == ObservationStatus.Known
                ? analysis.Snapshot.PageId.Value ?? "未知页面"
                : "未知页面";
            var confidence = analysis.Snapshot.PageId.Confidence;
            var unknown = analysis.UnknownFields.Count == 0
                ? "无"
                : string.Join("、", analysis.UnknownFields);
            Status =
                $"识别测试完成：页面={page}，置信度={confidence:P0}，未知项={unknown}";
            _eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Information,
                "RecognitionTestCompleted",
                Status));
        }
        catch (OperationCanceledException)
        {
            Status = "识别测试已停止。";
        }
        catch (Exception exception)
        {
            Status = $"识别测试失败：{exception.Message}";
            PublishApplicationError("RecognitionTestFailed", exception);
        }
        finally
        {
            IsRunning = false;
            _runCancellation.Dispose();
            _runCancellation = null;
        }
    }

    private void OnOpeningProgressChanged(
        object? sender,
        OpeningRerollLoopProgress progress) =>
        _unifiedRunLifecycle.ObserveOpeningProgress(progress);

    private void OnUnifiedRunLifecycleUpdated(
        object? sender,
        UnifiedRunLifecycleUpdate update)
    {
        void Apply()
        {
            Status = update.Message;
            if (update.IsError || update.IsMilestone)
            {
                _eventSink.Publish(new TaskEvent(
                    DateTimeOffset.Now,
                    update.IsError
                        ? TaskEventLevel.Warning
                        : TaskEventLevel.Information,
                    update.IsError
                        ? "UnifiedRunLifecycleWarning"
                        : "UnifiedRunLifecycleMilestone",
                    update.Message));
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(Apply);
        }
        else
        {
            Apply();
        }
    }

    private void Stop()
    {
        _eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            TaskEventLevel.Warning,
            "EmergencyStopRequested",
            "用户请求停止当前任务。"));
        _runCancellation?.Cancel();
        _passiveCollectionCancellation?.Cancel();
    }

    private void OnTaskEvent(object? sender, TaskEvent taskEvent)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var levelTag = taskEvent.Level switch
            {
                TaskEventLevel.Debug => "DBG",
                TaskEventLevel.Information => "INF",
                TaskEventLevel.Warning => "WRN",
                TaskEventLevel.Error => "FLT",
                _ => "LOG"
            };
            AddLog(
                $"{taskEvent.Timestamp:HH:mm:ss} " +
                $"[{levelTag}] {UserLogMessageFormatter.Format(taskEvent)}",
                taskEvent.Level);
            if (string.Equals(
                    taskEvent.Code,
                    "GameFocusPaused",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    taskEvent.Code,
                    "GameFocusResumed",
                    StringComparison.OrdinalIgnoreCase))
            {
                Status = taskEvent.Message;
            }
        });
    }

    private void OnHistoricalDashboardChanged(
        object? sender,
        HistoricalDashboardSnapshot snapshot)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyHistoricalDashboard(snapshot);
            return;
        }

        _ = dispatcher.BeginInvoke(() => ApplyHistoricalDashboard(snapshot));
    }

    private void ApplyHistoricalDashboard(HistoricalDashboardSnapshot snapshot)
    {
        var selectedDetailNodeId = SelectedDetailedHistoryNode?.NodeId;
        DashboardRows.Clear();
        var damageValues = snapshot.Nodes.Select(node => node.FinalDamage).ToArray();
        for (var index = 0; index < snapshot.Nodes.Count; index++)
        {
            var node = snapshot.Nodes[index];
            DashboardRows.Add(new HistoricalDashboardRow(
                node.NodeId,
                FormatDamage(node.FinalDamage),
                FormatOptionalInteger(node.RemainingActionValue),
                FormatSignedInteger(node.GoldDeltaSincePreviousNode),
                node.GoldReward is null ? "—" : $"+{node.GoldReward}",
                node.IsComplete ? "完整" : "残缺",
                node.ClearStatus switch
                {
                    NodeClearStatus.Perfect => "✓",
                    NodeClearStatus.NotPerfect => "✕",
                    _ => "?"
                },
                node.ClearStatus switch
                {
                    NodeClearStatus.Perfect => "Perfect",
                    NodeClearStatus.NotPerfect => "Failed",
                    _ => "Unknown"
                },
                node.HealthDelta switch
                {
                    > 0 => $"+{node.HealthDelta}",
                    0 => "±0",
                    < 0 => node.HealthDelta.Value.ToString(),
                    _ when node.HealthDepleted => "↓?",
                    _ => "—"
                },
                node.HealthDelta switch
                {
                    > 0 => "Positive",
                    0 => "Neutral",
                    < 0 => "Negative",
                    _ when node.HealthDepleted => "Negative",
                    _ => "Unknown"
                },
                HistoricalDashboardProjection.NormalizeDamage(
                    node.FinalDamage,
                    damageValues,
                    snapshot.DamageScale),
                index == snapshot.Nodes.Count - 1,
                node.FinalDamage,
                node.RemainingActionValue,
                node.AbsoluteGold,
                FormatOptionalInteger(node.AbsoluteGold),
                node.TheoreticalDamage,
                FormatDamage(node.TheoreticalDamage),
                node.IsRewardNode));
        }

        DetailedHistoryNodes.Clear();
        foreach (var detail in snapshot.DetailNodes)
        {
            var dashboard = snapshot.Nodes.FirstOrDefault(node =>
                string.Equals(
                    node.NodeId,
                    detail.NodeId,
                    StringComparison.OrdinalIgnoreCase));
            DetailedHistoryNodes.Add(_detailPresentation.Build(
                detail,
                dashboard is null
                    ? null
                    : HistoricalDetailEconomyProjection.FromDashboard(dashboard)));
        }

        SelectedDetailedHistoryNode = DetailedHistoryNodes.FirstOrDefault(item =>
                string.Equals(
                    item.NodeId,
                    selectedDetailNodeId,
                    StringComparison.OrdinalIgnoreCase)) ??
            DetailedHistoryNodes.LastOrDefault();
        DetailedHistorySummary = snapshot.RunId is null
            ? "当前没有正在记录的对局。查看历史存档：点右上角【历史对局】按钮"
            : $"运行 {snapshot.RunId} · 已收集 {DetailedHistoryNodes.Count} 个节点 · 实时更新";

        DashboardDamageScaleLabel = snapshot.DamageScale ==
                                    HistoricalDamageScale.Logarithmic
            ? "对数"
            : "线性";
        DashboardTheoryScaleLabel = snapshot.TheoryScale ==
                                    HistoricalDamageScale.Logarithmic
            ? "对数"
            : "线性";
        if (snapshot.Nodes.Count == 0)
        {
            DashboardSummaryText = "等待节点结算";
            return;
        }

        var latest = snapshot.Nodes[^1];
        DashboardSummaryText = latest.GoldReward is null
            ? $"已记录 {snapshot.Nodes.Count} 个节点 · 最近 {latest.NodeId}"
            : $"已记录 {snapshot.Nodes.Count} 个节点 · 最近 {latest.NodeId} · 奖励 +{latest.GoldReward}";
    }

    private static string FormatDamage(long? value)
    {
        if (value is null)
        {
            return "—";
        }

        if (value >= 100_000_000)
        {
            return $"{value.Value / 100_000_000d:0.##}亿";
        }

        return value >= 10_000
            ? $"{value.Value / 10_000d:0.0}万"
            : value.Value.ToString("N0");
    }

    private static string FormatOptionalInteger(int? value) =>
        value?.ToString() ?? "—";

    private static string FormatSignedInteger(int? value) =>
        value is null
            ? "—"
            : value > 0
                ? $"+{value}"
                : value.Value.ToString();

    private void OnRerollLoopProgressChanged(
        object? sender,
        OpeningRerollLoopProgress progress)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Status = $"[第 {progress.Round} 轮 / {progress.State}] {progress.Message}";
        });
    }

    private void OnAutomationStatusChanged(
        object? sender,
        Phase1WorkflowStatus status)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Status = $"[{status.State}] {status.Message}";
        });
    }

    private OpeningFilterSet BuildFilterSet() =>
        new()
        {
            InvestmentEnvironments = BuildItemFilters(
                InvestmentEnvironmentFilters.Items),
            Competitors = BuildItemFilters(CompetitorFilters.Items),
            EnemyModifiers = BuildItemFilters(EnemyAffixFilters.Items),
            Profiles = RerollProfiles
                .Select(BuildProfileFilter)
                .ToArray(),
            Combinations = SpecialCombinations
                .DistinctBy(
                    item => item.Id,
                    StringComparer.OrdinalIgnoreCase)
                .Select(BuildCombinationFilter)
                .ToArray()
        };

    private static OpeningFilterProfile BuildProfileFilter(
        RerollProfileViewModel profile) =>
        new()
        {
            Id = profile.Id,
            DisplayName = profile.Name,
            IsEnabled = profile.IsEnabled,
            AcceptedInvestmentEnvironmentIds =
                profile.AcceptedInvestmentEnvironmentIds,
            PreferredInvestmentStrategyIds =
                profile.PreferredInvestmentStrategyIds,
            RequiredCompetitorIds = profile.RequiredCompetitorIds,
            RejectedCompetitorIds = profile.RejectedCompetitorIds,
            RequiredEnemyModifierIds = profile.RequiredEnemyAffixIds,
            RejectedEnemyModifierIds = profile.RejectedEnemyAffixIds
        };

    private static IReadOnlyList<OpeningItemFilter> BuildItemFilters(
        IEnumerable<FilterItemViewModel> items) =>
        items
            .Where(item =>
                item.SelectionMode != AppFilterSelectionMode.Unrestricted)
            .Select(item => new OpeningItemFilter(
                item.Id,
                item.Name,
                ToGameFilterState(item.SelectionMode)))
            .ToArray();

    private OpeningCombinationFilter BuildCombinationFilter(
        SpecialCombinationViewModel combination)
    {
        if (combination.Condition == AppFilterSelectionMode.Unrestricted)
        {
            return new OpeningCombinationFilter
            {
                Id = combination.Id,
                DisplayName = combination.Name,
                State = OpeningFilterState.Ignore
            };
        }

        if (combination.IsBuiltIn &&
            string.Equals(
                combination.Name,
                "灰手生命科技 + 额外打击",
                StringComparison.OrdinalIgnoreCase))
        {
            return new OpeningCombinationFilter
            {
                Id = combination.Id,
                DisplayName = combination.Name,
                State = ToGameFilterState(combination.Condition),
                CompetitorIds = ["competitor_12"],
                EnemyModifierIds = ["enemy_affix_t2_16"]
            };
        }

        return new OpeningCombinationFilter
        {
            Id = combination.Id,
            DisplayName = combination.Name,
            State = ToGameFilterState(combination.Condition),
            InvestmentEnvironmentIds = ResolveNames(
                combination.InvestmentEnvironments,
                _gameData.InvestmentEnvironments,
                item => item.Id,
                item => item.Name,
                "投资环境"),
            CompetitorIds = ResolveNames(
                combination.Competitors,
                _gameData.Competitors,
                item => item.Id,
                item => item.Name,
                "敌人阵营"),
            EnemyModifierIds = ResolveNames(
                combination.EnemyAffixes,
                _gameData.EnemyAffixes,
                item => item.Id,
                item => item.Name,
                "敌人负面词条")
        };
    }

    private static IReadOnlyList<string> ResolveNames<T>(
        string text,
        IEnumerable<T> catalog,
        Func<T, string> idSelector,
        Func<T, string> nameSelector,
        string categoryName)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            string.Equals(text.Trim(), "不限制", StringComparison.Ordinal))
        {
            return [];
        }

        var values = catalog.ToArray();
        var resolved = new List<string>();
        foreach (var token in text.Split(
                     ['、', ',', '，', ';', '；', '\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            var match = values.FirstOrDefault(item =>
                string.Equals(nameSelector(item), token, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(idSelector(item), token, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new InvalidOperationException(
                    $"自定义组合中存在未知{categoryName}：{token}");
            }

            resolved.Add(idSelector(match));
        }

        return resolved.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static OpeningFilterState ToGameFilterState(
        AppFilterSelectionMode mode) =>
        mode switch
        {
            AppFilterSelectionMode.Unrestricted => OpeningFilterState.Ignore,
            AppFilterSelectionMode.Required => OpeningFilterState.Require,
            AppFilterSelectionMode.Forbidden => OpeningFilterState.Reject,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

    private static string FormatLoopResult(OpeningRerollLoopResult result)
    {
        if (result.FinalState == OpeningRerollLoopState.Matched)
        {
            return result.Message;
        }

        if (result.FinalState == OpeningRerollLoopState.WaitingForRecovery)
        {
            var reasons = result.Evaluation is null
                ? string.Empty
                : string.Join("；", result.Evaluation.Reasons);
            return $"当前开局不符合条件：{reasons}。等待补充“两步返回主界面”操作。";
        }

        if (result.Evaluation is not null &&
            result.Evaluation.Reasons.Count > 0)
        {
            return $"{result.Message} 条件结果：{string.Join("；", result.Evaluation.Reasons)}";
        }

        return result.Message;
    }

    private static string DescribeFilters(OpeningFilterSet filters)
    {
        static string DescribeItems(IEnumerable<OpeningItemFilter> items) =>
            string.Join(
                "、",
                items.Select(item =>
                    $"{item.DisplayName}={item.State}").DefaultIfEmpty("无"));

        var combinations = string.Join(
            "、",
            filters.Combinations
                .Where(item => item.State != OpeningFilterState.Ignore)
                .Select(item => $"{item.DisplayName}={item.State}")
                .DefaultIfEmpty("无"));
        var profiles = string.Join(
            "、",
            filters.Profiles
                .Where(item => item.IsEnabled)
                .Select(item => item.DisplayName)
                .DefaultIfEmpty("无"));
        return
            $"投资环境[{DescribeItems(filters.InvestmentEnvironments)}]；" +
            $"敌人阵营[{DescribeItems(filters.Competitors)}]；" +
            $"负面词条[{DescribeItems(filters.EnemyModifiers)}]；" +
            $"刷取方案[{profiles}]；" +
            $"特殊组合[{combinations}]";
    }

    private RerollProfileViewModel FromSnapshot(
        RerollProfileSnapshot snapshot) =>
        new(
            snapshot.Id,
            snapshot.Name,
            snapshot.IsEnabled,
            snapshot.AcceptedInvestmentEnvironmentIds ?? [],
            ResolveDisplayNames(
                snapshot.AcceptedInvestmentEnvironmentIds,
                _gameData.InvestmentEnvironments,
                item => item.Id,
                item => item.Name),
            snapshot.PreferredInvestmentStrategyIds ?? [],
            ResolveDisplayNames(
                snapshot.PreferredInvestmentStrategyIds,
                _gameData.InvestmentStrategies,
                item => item.Id,
                item => $"{item.Name} · {item.Rarity}"),
            snapshot.RequiredCompetitorIds ?? [],
            ResolveDisplayNames(
                snapshot.RequiredCompetitorIds,
                _gameData.Competitors,
                item => item.Id,
                item => item.Name),
            snapshot.RejectedCompetitorIds ?? [],
            ResolveDisplayNames(
                snapshot.RejectedCompetitorIds,
                _gameData.Competitors,
                item => item.Id,
                item => item.Name),
            snapshot.RequiredEnemyAffixIds ?? [],
            ResolveDisplayNames(
                snapshot.RequiredEnemyAffixIds,
                _gameData.EnemyAffixes,
                item => item.Id,
                item => item.Name),
            snapshot.RejectedEnemyAffixIds ?? [],
            ResolveDisplayNames(
                snapshot.RejectedEnemyAffixIds,
                _gameData.EnemyAffixes,
                item => item.Id,
                item => item.Name));

    private static RerollProfileSnapshot ToSnapshot(
        RerollProfileViewModel profile) =>
        new(
            profile.Id,
            profile.Name,
            profile.IsEnabled,
            profile.AcceptedInvestmentEnvironmentIds,
            profile.PreferredInvestmentStrategyIds,
            profile.RequiredCompetitorIds,
            profile.RejectedCompetitorIds,
            profile.RequiredEnemyAffixIds,
            profile.RejectedEnemyAffixIds);

    private static IReadOnlyList<string> ResolveDisplayNames<T>(
        IReadOnlyList<string>? ids,
        IEnumerable<T> catalog,
        Func<T, string> idSelector,
        Func<T, string> nameSelector)
    {
        var namesById = catalog.ToDictionary(
            idSelector,
            nameSelector,
            StringComparer.OrdinalIgnoreCase);
        return (ids ?? [])
            .Select(id => namesById.TryGetValue(id, out var name) ? name : id)
            .ToArray();
    }

    private void AddLog(string message, TaskEventLevel level)
    {
        LogLines.Add(message);
        while (LogLines.Count > 200)
        {
            LogLines.RemoveAt(0);
        }

        var foreground = level switch
        {
            TaskEventLevel.Warning => WarningLogBrush,
            TaskEventLevel.Error => FaultLogBrush,
            _ => InformationLogBrush
        };
        OverlayLogLines.Add(new OverlayLogLine(message, foreground));
        while (OverlayLogLines.Count > 7)
        {
            OverlayLogLines.RemoveAt(0);
        }
    }

    private static Brush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private void PublishApplicationError(string code, Exception exception) =>
        _eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            TaskEventLevel.Error,
            code,
            exception.ToString()));

    private void PublishApplicationWarning(string code, string message) =>
        _eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            TaskEventLevel.Warning,
            code,
            message));

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string GetGameModeDisplayName(CurrencyWarsGameMode gameMode) =>
        gameMode switch
        {
            CurrencyWarsGameMode.Standard => "标准博弈",
            CurrencyWarsGameMode.Overclock => "超频博弈",
            _ => gameMode.ToString()
        };

    private void NotifyCommands()
    {
        RefreshWindowsCommand.NotifyCanExecuteChanged();
        CaptureCommand.NotifyCanExecuteChanged();
        ObserveCommand.NotifyCanExecuteChanged();
        AutoRerollCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RefreshIncompleteRunsCommand.NotifyCanExecuteChanged();
        ContinueIncompleteRunCommand.NotifyCanExecuteChanged();
        SettleIncompleteRunCommand.NotifyCanExecuteChanged();
        DeleteIncompleteRunCommand.NotifyCanExecuteChanged();
    }

    private sealed record UserSettingsSnapshot(
        bool ShowLogOverlay,
        bool EnableUnknownPageEscapeRecovery,
        bool EnableRewardStageAutomation,
        bool EnableEarlyStrongFormationPurchase,
        bool IsLogOverlayClickThrough,
        double LogOverlayOpacity,
        CurrencyWarsGameMode GameMode,
        IReadOnlyList<string> RetainedCharacterIds,
        IReadOnlyList<string> AutoPurchaseCharacterIds,
        IReadOnlyList<string> PreferredInvestmentStrategyIds,
        IReadOnlyList<RerollProfileSnapshot>? RerollProfiles = null,
        PreparationBenchSaleMode BenchSaleMode =
            PreparationBenchSaleMode.InterestThreshold,
        int BenchSaleInterestThreshold = 10,
        bool EnableGalaxyScholarRewardStrategy = false,
        IReadOnlyList<FilterSelectionSnapshot>?
            InvestmentEnvironmentFilters = null,
        IReadOnlyList<FilterSelectionSnapshot>? CompetitorFilters = null,
        IReadOnlyList<FilterSelectionSnapshot>? EnemyAffixFilters = null,
        IReadOnlyList<SpecialCombinationSetting>? SpecialCombinations = null,
        bool EnableDiagnosticLogging = true,
        GameSourcePreference GameSource = GameSourcePreference.Automatic,
        string? LastWindowProcessName = null,
        string? LastWindowTitle = null,
        NormalizedRect? LastGameArea = null,
        GameWindowSourceKind? LastGameAreaSource = null,
        bool DeleteScreenshotsAfterRunCompletion = false,
        FastRerollMode FastReroll = FastRerollMode.Stable);

    private sealed record RerollProfileSnapshot(
        string Id,
        string Name,
        bool IsEnabled,
        IReadOnlyList<string>? AcceptedInvestmentEnvironmentIds,
        IReadOnlyList<string>? PreferredInvestmentStrategyIds,
        IReadOnlyList<string>? RequiredCompetitorIds,
        IReadOnlyList<string>? RejectedCompetitorIds,
        IReadOnlyList<string>? RequiredEnemyAffixIds,
        IReadOnlyList<string>? RejectedEnemyAffixIds);
}
