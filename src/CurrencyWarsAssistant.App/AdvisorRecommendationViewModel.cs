// AdvisorRecommendationViewModel.cs —— 主界面「攻略推荐（局势+阵容）」面板子 VM（Advisor 匹配器 v2.1 实装）。
// 模式对齐 SituationAnalysisViewModel：MainWindow 持有属性，XAML 以 ElementName=RootWindow 绑定。
// 快照获取路径与 SituationAnalysisViewModel 相同：记录中取识别流最新快照；空闲时对当前游戏窗口
// 做一次只读截图+识别（绝不操作游戏）；都没有则面板内给出明确提示而不是异常。
// 数据目录与 App 现有定位完全一致（App.xaml.cs 的 dataDirectory / ISituationScreenshotAnalyzer 注册）：
// 攻略册 = BaseDirectory/data/advisor/1.0.0/4.4/guides；dataRoot = BaseDirectory/data/4.4。
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.App;

/// <summary>购买推荐行（Buys 与门控项 Gated 共用模板；IsGated=true 走灰色样式）。</summary>
public sealed record AdvisorBuyRow(
    string CharacterId,
    string ScoreDisplay,
    string TierDisplay,
    string Note);

/// <summary>v1 MatchReport.Matches 行（攻略名/相似度/命中阶段/主匹配）。</summary>
public sealed record AdvisorGuideMatchRow(
    string Title,
    string SimilarityDisplay,
    string MatchedPhase,
    bool IsPrimary);

public sealed class AdvisorRecommendationViewModel : ObservableObject, IDisposable
{
    // 与 App.xaml.cs 现有 data 目录定位同款：发布后 data 随 BaseDirectory 分发。
    private static readonly string PlaybookDirectory = Path.Combine(
        AppContext.BaseDirectory, "data", "advisor", "1.0.0", "4.4", "guides");
    private static readonly string DataRoot = Path.Combine(
        AppContext.BaseDirectory, "data", "4.4");

    private readonly IPhase2LiveCollectionService _collector;
    private readonly ISituationScreenshotAnalyzer _analyzer;
    private readonly IGameWindowService _windowService;
    private readonly IGameCapture _capture;
    private readonly MainViewModel _mainViewModel;
    private readonly UiTaskEventSink _eventSink;
    private RunSnapshot? _latestSnapshot;
    private CancellationTokenSource? _activityCts;
    private bool _isBusy;
    private bool _hasResult;
    private bool _hasGatedBuys;
    private bool _hasDifficultyAdvisory;
    private string _status = "尚未生成：点击「生成攻略推荐」按当前识别快照计算（只读识别，不影响游戏）。";
    private string _snapshotSummary = "";
    private string _bestStageSummary = "";
    private string _errorMessage = "";
    private string _difficultyAdvisory = "";
    private bool _shutdownRequested;

    public AdvisorRecommendationViewModel(
        IPhase2LiveCollectionService collector,
        ISituationScreenshotAnalyzer analyzer,
        IGameWindowService windowService,
        IGameCapture capture,
        MainViewModel mainViewModel,
        UiTaskEventSink eventSink)
    {
        _collector = collector;
        _analyzer = analyzer;
        _windowService = windowService;
        _capture = capture;
        _mainViewModel = mainViewModel;
        _eventSink = eventSink;
        _collector.Updated += OnCollectorUpdated;

        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => !_shutdownRequested);
    }

    public AsyncRelayCommand GenerateCommand { get; }

    public ObservableCollection<AdvisorBuyRow> Buys { get; } = [];
    public ObservableCollection<AdvisorBuyRow> GatedBuys { get; } = [];
    public ObservableCollection<string> TopStageMatches { get; } = [];
    public ObservableCollection<string> ActivatedGuides { get; } = [];
    public ObservableCollection<string> UniversalNow { get; } = [];
    public ObservableCollection<string> Caveats { get; } = [];
    public ObservableCollection<AdvisorGuideMatchRow> GuideMatches { get; } = [];
    public ObservableCollection<string> MatchCaveats { get; } = [];

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>面板结果区是否可见（只在成功生成后展开）。</summary>
    public bool HasResult
    {
        get => _hasResult;
        private set => SetProperty(ref _hasResult, value);
    }

    /// <summary>门控项分组是否可见（无门控项时整组隐藏）。</summary>
    public bool HasGatedBuys
    {
        get => _hasGatedBuys;
        private set => SetProperty(ref _hasGatedBuys, value);
    }

    /// <summary>难度 advisory 是否有值（无值时整行隐藏）。</summary>
    public bool HasDifficultyAdvisory
    {
        get => _hasDifficultyAdvisory;
        private set => SetProperty(ref _hasDifficultyAdvisory, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>当前快照摘要（时间/上场备战/等级/金币/血量）——诚实展示数据新鲜度。</summary>
    public string SnapshotSummary
    {
        get => _snapshotSummary;
        private set => SetProperty(ref _snapshotSummary, value);
    }

    public string BestStageSummary
    {
        get => _bestStageSummary;
        private set => SetProperty(ref _bestStageSummary, value);
    }

    /// <summary>面板内错误文案（AdvisorPipeline 抛异常时显示，不崩程序）。</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string DifficultyAdvisory
    {
        get => _difficultyAdvisory;
        private set => SetProperty(ref _difficultyAdvisory, value);
    }

    /// <summary>主窗口关闭流程调用：取消进行中的识别/生成（与 SituationAnalysisViewModel 同款）。</summary>
    public void RequestShutdownStop()
    {
        _shutdownRequested = true;
        _activityCts?.Cancel();
    }

    public void Dispose()
    {
        _collector.Updated -= OnCollectorUpdated;
        RequestShutdownStop();
        _activityCts?.Dispose();
        _activityCts = null;
    }

    private void OnCollectorUpdated(object? sender, LiveCollectionUpdate update) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (update.Analysis is { } analysis)
            {
                StoreSnapshot(analysis.Snapshot);
            }
        });

    private async Task GenerateAsync()
    {
        ErrorMessage = "";
        HasResult = false;
        IsBusy = true;
        Status = "正在获取当前识别快照……";
        try
        {
            var snapshot = await ResolveSnapshotAsync();
            if (snapshot is null)
            {
                Status = "尚无可用识别快照：请先「开始记录」，或在上方选择游戏窗口后重试（只读识别，不影响游戏）。";
                return;
            }

            SnapshotSummary = DescribeSnapshot(snapshot);
            if (!HasLineupObservation(snapshot))
            {
                Status = "识别快照未就绪：其中没有上场/备战阵容观测（当前画面可能不是备战或商店页）。" +
                         "请进入对局的备战界面后再生成。";
                return;
            }

            Status = "正在生成攻略推荐（v2.1 匹配器）……";
            var result = await Task.Run(
                () => AdvisorPipeline.Run(snapshot, PlaybookDirectory, DataRoot));
            Apply(snapshot, result);
            Status = "攻略推荐已生成；以上按快照时间的数据计算。";
        }
        catch (OperationCanceledException)
        {
            Status = "生成攻略推荐已取消。";
        }
        catch (Exception exception)
        {
            // 错误落在面板内，不崩程序。
            ErrorMessage = $"生成攻略推荐失败：{exception.Message}";
            Status = "生成攻略推荐失败（原因见上方错误提示）。";
            Publish(
                TaskEventLevel.Warning,
                "AdvisorRecommendationFailed",
                $"攻略推荐面板异常={exception.GetType().Name}；消息={exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>快照获取（照抄 SituationAnalysisViewModel 路径）：记录/自动化进行中只读识别流最新快照
    /// （避免与识别管线并发分析）；空闲时对当前窗口做一次只读截图+识别；都没有则回落最近一次快照。</summary>
    private async Task<RunSnapshot?> ResolveSnapshotAsync()
    {
        if (_mainViewModel.IsRunning ||
            _mainViewModel.IsPassiveCollectionRunning ||
            _mainViewModel.IsGrailRunActive)
        {
            return _latestSnapshot;
        }

        var window = ResolveGameWindow();
        if (window is not null)
        {
            _activityCts?.Dispose();
            _activityCts = new CancellationTokenSource();
            var token = _activityCts.Token;
            var frame = await _capture.CaptureAsync(window, token);
            var analysis = await _analyzer.AnalyzeAsync(
                frame,
                "advisor-panel:live",
                new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
                token);
            StoreSnapshot(analysis.Snapshot);
            return analysis.Snapshot;
        }

        return _latestSnapshot;
    }

    private GameWindowInfo? ResolveGameWindow()
    {
        var selected = _mainViewModel.SelectedWindow;
        var refreshed = selected is null
            ? null
            : _windowService.Refresh(selected.Handle);
        if (refreshed is not null)
        {
            return refreshed;
        }

        _mainViewModel.RefreshWindows();
        selected = _mainViewModel.SelectedWindow;
        return selected is null
            ? null
            : _windowService.Refresh(selected.Handle);
    }

    private void StoreSnapshot(RunSnapshot snapshot)
    {
        _latestSnapshot = snapshot;
        if (!HasResult && !HasLineupObservation(snapshot))
        {
            return;
        }

        SnapshotSummary = DescribeSnapshot(snapshot);
    }

    /// <summary>阵容观测是否就绪：上场/备战/阵容任一 Known 即可（AdvisorPipeline 的阵容输入）。</summary>
    private static bool HasLineupObservation(RunSnapshot snapshot) =>
        snapshot.BoardCharacterIds is { Status: ObservationStatus.Known } ||
        snapshot.BenchCharacterIds is { Status: ObservationStatus.Known } ||
        snapshot.LineupIds is { Status: ObservationStatus.Known };

    private void Apply(RunSnapshot snapshot, AdvisorPipeline.PipelineResult result)
    {
        var v2 = result.V2;
        var match = result.Match;

        // 1) 最佳阶段匹配 + TopStageMatches 前 3（榜首即最佳，列表保留排名视角）。
        var best = v2.BestStageMatch;
        BestStageSummary = string.IsNullOrWhiteSpace(best.LineupId)
            ? "没有与当前阵容有交集的阶段匹配（阵容观测为空或全部不命中）。"
            : $"阵容 {best.LineupId} · {best.Stage} 阶段 · Jaccard {best.Jaccard:P1}";
        ReplaceAll(TopStageMatches, v2.TopStageMatches
            .Where(x => !string.IsNullOrWhiteSpace(x.LineupId))
            .Take(3)
            .Select(x => $"{x.LineupId} · {x.Stage} · Jaccard {x.Jaccard:P1}"));

        // 2) 已激活攻略（N0 门控命中）。
        ReplaceAll(ActivatedGuides, v2.ActivatedGuides.Count > 0
            ? v2.ActivatedGuides
            : ["（无——当前没有 N0 门控命中的攻略）"]);

        // 3) 购买推荐：Score 降序（AdvisorPipeline/MatcherV2 已排序，这里防御性再排）。
        ReplaceAll(Buys, v2.Buys
            .OrderByDescending(x => x.Score)
            .Select(x => new AdvisorBuyRow(
                x.CharacterId,
                x.Score.ToString("F3"),
                x.Tier,
                x.GateNote)));

        // 4) 门控项（费用不可达/专家）：单独一组灰色显示，带 GateNote 原因。
        ReplaceAll(GatedBuys, v2.Gated
            .OrderByDescending(x => x.Score)
            .Select(x => new AdvisorBuyRow(
                x.CharacterId,
                x.Score.ToString("F3"),
                x.Tier,
                x.GateNote)));
        HasGatedBuys = GatedBuys.Count > 0;

        // 5) 通用拐价值线。
        ReplaceAll(UniversalNow, v2.UniversalNow.Count > 0
            ? v2.UniversalNow
            : ["（无）"]);

        // 6) 难度 advisory（有值才显示；数据待 A820 实测终验，仅参考）。
        DifficultyAdvisory = v2.DifficultyAdvisory ?? "";
        HasDifficultyAdvisory = !string.IsNullOrWhiteSpace(DifficultyAdvisory);

        // 7) Caveats 全部显示（诚实纪律：v2.1 与 v1 匹配器各自的局限都要见字）。
        ReplaceAll(Caveats, v2.Caveats.Count > 0
            ? v2.Caveats
            : ["（无）"]);
        ReplaceAll(MatchCaveats, match.Caveats.Count > 0
            ? match.Caveats
            : ["（无）"]);

        // v1 MatchReport.Matches：攻略名/相似度/命中阶段/主匹配。
        ReplaceAll(GuideMatches, match.Matches
            .OrderByDescending(x => x.Similarity)
            .Select(x => new AdvisorGuideMatchRow(
                string.IsNullOrWhiteSpace(x.Title) ? x.GuideId : x.Title,
                x.Similarity.ToString("P1"),
                x.MatchedPhase,
                x.IsPrimary)));

        HasResult = true;
    }

    private static void ReplaceAll<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private static string DescribeSnapshot(RunSnapshot snapshot)
    {
        static string Count(Observation<IReadOnlyList<string>>? o) =>
            o is { Status: ObservationStatus.Known, Value: not null }
                ? o.Value.Count.ToString()
                : "?";

        static string Number(Observation<int>? o) =>
            o is { Status: ObservationStatus.Known } known
                ? known.Value.ToString()
                : "未知";

        return $"快照 {snapshot.AsOf.ToLocalTime():HH:mm:ss} · " +
               $"上场 {Count(snapshot.BoardCharacterIds)} / 备战 {Count(snapshot.BenchCharacterIds)} · " +
               $"等级 {Number(snapshot.StoreLevel)} · 金币 {Number(snapshot.Economy)} · " +
               $"血量 {Number(snapshot.Health)}";
    }

    private void Publish(TaskEventLevel level, string code, string message) =>
        _eventSink.Publish(new TaskEvent(DateTimeOffset.Now, level, code, message));
}
