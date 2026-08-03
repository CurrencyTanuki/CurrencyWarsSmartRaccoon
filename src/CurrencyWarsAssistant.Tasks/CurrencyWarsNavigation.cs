using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurrencyWarsAssistant.Tasks;

public sealed record StandardPoint(int X, int Y);

public enum CurrencyWarsGameMode
{
    Standard,
    Overclock
}

/// <summary>
/// 快速刷开局模式（设置页可选）：
/// Stable = 现版本完整验证流程；
/// Fast = 去掉阶段 4 的验证，OCR 只识别两次，其余按内部状态机推算；
/// Extreme = 备战席前三个角色无脑拖到前台，其余备战逻辑全部去掉。
/// </summary>
public enum FastRerollMode
{
    Stable,
    Fast,
    Extreme
}

public sealed class NavigationActionDefinition
{
    public required string Id { get; init; }
    public string DisplayName { get; init; } = "";
    public string Kind { get; init; } = "click";
    public StandardPoint? Point { get; init; }
    public StandardPoint? TargetPoint { get; init; }
    public int DurationMilliseconds { get; init; } = 500;
    public List<string> ExpectedPageIds { get; init; } = [];
    public int TimeoutMilliseconds { get; init; } = 12000;
    public CurrencyWarsGameMode? RequiredGameMode { get; init; }
}

public sealed class NavigationStepDefinition
{
    public required string PageId { get; init; }
    public bool Terminal { get; init; }
    public List<NavigationActionDefinition> Actions { get; init; } = [];
}

public sealed class CurrencyWarsNavigationConfig
{
    public int ReferenceWidth { get; init; } = 1920;
    public int ReferenceHeight { get; init; } = 1080;
    public int PollIntervalMilliseconds { get; init; } = 300;
    public int StableDetections { get; init; } = 2;
    public int InitialPageTimeoutMilliseconds { get; init; } = 10000;
    public int MaximumRuntimeMilliseconds { get; init; } = 120000;
    public string PreparationPageId { get; init; } = "preparation_1_1";
    public List<NavigationStepDefinition> Steps { get; init; } = [];

    public static CurrencyWarsNavigationConfig Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var json = File.ReadAllText(fullPath);
        var config = JsonSerializer.Deserialize<CurrencyWarsNavigationConfig>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                Converters = { new JsonStringEnumConverter() }
            }) ?? throw new InvalidDataException($"Invalid navigation config: {fullPath}");
        config.Validate();
        return config;
    }

    public void Validate()
    {
        if (ReferenceWidth <= 0 || ReferenceHeight <= 0)
        {
            throw new InvalidDataException("Navigation reference size must be positive.");
        }

        if (PollIntervalMilliseconds <= 0 ||
            StableDetections <= 0 ||
            InitialPageTimeoutMilliseconds <= 0 ||
            MaximumRuntimeMilliseconds <= 0)
        {
            throw new InvalidDataException("Navigation timing values must be positive.");
        }

        var pageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in Steps)
        {
            if (string.IsNullOrWhiteSpace(step.PageId) || !pageIds.Add(step.PageId))
            {
                throw new InvalidDataException(
                    $"Navigation page ID is empty or duplicated: {step.PageId}");
            }

            var actionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var action in step.Actions)
            {
                if (string.IsNullOrWhiteSpace(action.Id) || !actionIds.Add(action.Id))
                {
                    throw new InvalidDataException(
                        $"Action ID is empty or duplicated on {step.PageId}: {action.Id}");
                }

                var isClick = string.Equals(
                    action.Kind,
                    "click",
                    StringComparison.OrdinalIgnoreCase);
                var isDrag = string.Equals(
                    action.Kind,
                    "drag",
                    StringComparison.OrdinalIgnoreCase);
                var isEscape = string.Equals(
                    action.Kind,
                    "escape",
                    StringComparison.OrdinalIgnoreCase);
                var isAltClick = string.Equals(
                    action.Kind,
                    "altClick",
                    StringComparison.OrdinalIgnoreCase);
                if (!isClick && !isDrag && !isEscape && !isAltClick)
                {
                    throw new InvalidDataException(
                        $"Unsupported action kind: {action.Kind}");
                }

                if ((isClick || isDrag || isAltClick) &&
                    (action.Point is null ||
                     !Contains(action.Point)))
                {
                    throw new InvalidDataException(
                        $"Action point is missing or out of range: {action.Id}");
                }

                if (isDrag &&
                    (action.TargetPoint is null ||
                     !Contains(action.TargetPoint)))
                {
                    throw new InvalidDataException(
                        $"Drag target is missing or out of range: {action.Id}");
                }
            }
        }

        if (!pageIds.Contains(PreparationPageId))
        {
            throw new InvalidDataException(
                $"Preparation page has no navigation step: {PreparationPageId}");
        }
    }

    private bool Contains(StandardPoint point) =>
        point.X >= 0 &&
        point.Y >= 0 &&
        point.X < ReferenceWidth &&
        point.Y < ReferenceHeight;
}

public enum CurrencyWarsNavigationState
{
    LocatingWindow,
    WaitingForPage,
    Acting,
    OpeningRecognized,
    InvestmentEnvironmentFallbackSelected,
    ReachedPreparation,
    EnteredBattle,
    RecognitionIncomplete,
    UnknownPage,
    TimedOut,
    WindowUnavailable,
    InputBlocked,
    ActiveRunDetected,
    UnsupportedPage
}

public sealed record CurrencyWarsNavigationProgress(
    CurrencyWarsNavigationState State,
    string? PageId,
    string Message);

public sealed record CurrencyWarsNavigationResult(
    CurrencyWarsNavigationState FinalState,
    string? PageId,
    string Message)
{
    public EnemyOverviewReadResult? EnemyOverview { get; init; }
    public InvestmentEnvironmentReadResult? InvestmentEnvironments { get; init; }
    public string? SelectedInvestmentEnvironmentId { get; init; }

    public bool Succeeded =>
        FinalState is CurrencyWarsNavigationState.OpeningRecognized
            or CurrencyWarsNavigationState.ReachedPreparation
            or CurrencyWarsNavigationState.EnteredBattle;
}

public sealed class CurrencyWarsNavigationOptions
{
    public bool StopAfterOpeningRecognition { get; init; }
    public bool StopAtPreparation { get; init; } = true;
    public bool EnableUnknownPageEscapeRecovery { get; init; } = true;
    /// <summary>
    /// 从货币战争主界面到"开始本局"之间的固定按钮快速连点路径：
    /// 按钮位置与页面顺序固定，点击后不再等待 2 帧稳定识别，
    /// 只用单帧确认页面已切换，显著减少导航耗时。
    /// 敌人概览页保留稳定识别（必须确认敌人信息后再点确认）；
    /// 位面进度页使用单帧确认（一出就点继续）。
    /// </summary>
    public bool FastPathFromHome { get; init; }
    public CurrencyWarsGameMode GameMode { get; init; } = CurrencyWarsGameMode.Standard;
    /// <summary>
    /// 快速刷开局模式：Stable 走完整流程；Fast/Extreme 走盲点连点快速路径。
    /// </summary>
    public FastRerollMode FastReroll { get; init; } = FastRerollMode.Stable;
    public IReadOnlySet<string> PreferredInvestmentEnvironmentIds { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public interface ICurrencyWarsOpeningNavigator
{
    Task<CurrencyWarsNavigationResult> RunAsync(
        nint windowHandle,
        CurrencyWarsNavigationOptions options,
        CancellationToken cancellationToken);
}

public sealed class CurrencyWarsNavigationTask(
    IGameWindowService windowService,
    IGameCapture capture,
    IGamePageClassifier classifier,
    IOcrOpeningPageReader openingPageReader,
    IOfflineOcr offlineOcr,
    UnknownPageEscapeRecovery unknownPageRecovery,
    IInputController input,
    IGameForegroundGuard foregroundGuard,
    CurrencyWarsNavigationConfig config,
    ITaskEventSink eventSink) : ICurrencyWarsOpeningNavigator
{
    private const string BlueSeaInvestmentEnvironmentId =
        "investment_environment_068";
    private static readonly TimeSpan FastPathStepDelay =
        TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan FastPathPollInterval =
        TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan FastPathPageSwitchTimeout =
        TimeSpan.FromSeconds(8);
    private static readonly StandardPoint FastStartRunPoint =
        new(1690, 967);
    private static readonly StandardPoint InvestmentRefreshPoint =
        new(676, 984);
    private static readonly StandardPoint BlueSeaRewardCardPoint =
        new(960, 530);
    private static readonly StandardPoint BlueSeaRewardConfirmPoint =
        new(960, 984);
    private static readonly PixelRect GuideShellTitleRegion =
        new(0, 0, 500, 150);
    private static readonly PixelRect GuideActivityTitleRegion =
        new(180, 180, 700, 300);
    private static readonly IReadOnlyList<StandardPoint>
        InvestmentOptionPoints =
        [
            new(439, 530),
            new(960, 530),
            new(1481, 530)
        ];
    private readonly IReadOnlyDictionary<string, NavigationStepDefinition> _steps =
        config.Steps.ToDictionary(step => step.PageId, StringComparer.OrdinalIgnoreCase);
    private EnemyOverviewReadResult? _enemyOverview;
    private InvestmentEnvironmentReadResult? _investmentEnvironments;
    private string? _selectedInvestmentEnvironmentId;
    private bool _investmentEnvironmentFallbackRequired;
    private GameWindowInfo? _lastKnownWindow;
    private TimeSpan _pauseBaseline;

    private DateTimeOffset ActiveUtcNow =>
        DateTimeOffset.UtcNow -
        (foregroundGuard.TotalPausedDuration - _pauseBaseline);

    public event EventHandler<CurrencyWarsNavigationProgress>? ProgressChanged;

    public Task<CurrencyWarsNavigationResult> RunAsync(
        nint windowHandle,
        CancellationToken cancellationToken) =>
        RunAsync(windowHandle, new CurrencyWarsNavigationOptions(), cancellationToken);

    public async Task<CurrencyWarsNavigationResult> RunAsync(
        nint windowHandle,
        CurrencyWarsNavigationOptions options,
        CancellationToken cancellationToken)
    {
        _enemyOverview = null;
        _investmentEnvironments = null;
        _selectedInvestmentEnvironmentId = null;
        _investmentEnvironmentFallbackRequired = false;
        _pauseBaseline = foregroundGuard.TotalPausedDuration;
        var deadline = ActiveUtcNow +
                       TimeSpan.FromMilliseconds(config.MaximumRuntimeMilliseconds);
        Publish(CurrencyWarsNavigationState.LocatingWindow, null, "正在确认并聚焦游戏窗口。");

        var initialWindow = windowService.Refresh(windowHandle);
        if (initialWindow is null)
        {
            return Result(
                CurrencyWarsNavigationState.WindowUnavailable,
                null,
                "游戏窗口不存在、已最小化或客户区无效。");
        }
        _lastKnownWindow = initialWindow;

        if (!windowService.IsForeground(initialWindow))
        {
            if (!windowService.BringToForeground(initialWindow))
            {
                return Result(
                    CurrencyWarsNavigationState.InputBlocked,
                    null,
                    "无法将游戏窗口切换到前台；未执行截图或点击。");
            }

            // 0-1：取消切换窗口后的 200ms 等待——直接刷新验证前台（用户方案）。
            initialWindow = windowService.Refresh(windowHandle);
            if (initialWindow is null || !windowService.IsForeground(initialWindow))
            {
                return Result(
                    CurrencyWarsNavigationState.InputBlocked,
                    null,
                    "已请求切换游戏窗口，但未确认其获得前台焦点；未执行点击。");
            }
            _lastKnownWindow = initialWindow;

            Publish(
                CurrencyWarsNavigationState.LocatingWindow,
                null,
                "游戏窗口已获得前台焦点，开始识别当前页面。");
        }

        var current = await WaitForStablePageWithRecoveryAsync(
            windowHandle,
            expectedPageIds: null,
            TimeSpan.FromMilliseconds(config.InitialPageTimeoutMilliseconds),
            options.EnableUnknownPageEscapeRecovery,
            cancellationToken);
        if (current is null)
        {
            return Result(
                CurrencyWarsNavigationState.UnknownPage,
                null,
                "在安全等待时间内未能稳定识别当前页面；未执行任何点击。");
        }

        while (ActiveUtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(
                    current.PageId,
                    "rank_difficulty_in_progress",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Result(
                    CurrencyWarsNavigationState.ActiveRunDetected,
                    current.PageId,
                    "检测到已有进行中的货币战争对局；为保护用户进度，未执行任何操作。");
            }

            if (!_steps.TryGetValue(current.PageId, out var step))
            {
                return Result(
                    CurrencyWarsNavigationState.UnsupportedPage,
                    current.PageId,
                    $"已识别“{current.DisplayName}”，但没有可执行的导航步骤；已安全停止。");
            }

            if (options.FastPathFromHome &&
                options.GameMode == CurrencyWarsGameMode.Standard &&
                string.Equals(
                    current.PageId,
                    "currency_wars_home",
                    StringComparison.OrdinalIgnoreCase))
            {
                Publish(
                    CurrencyWarsNavigationState.Acting,
                    current.PageId,
                    "启用快速点击路径：货币战争主界面→开始本局，" +
                    "固定按钮连点并用单帧确认页面切换。");
                var fast = await FastPathFromHomeAsync(
                    windowHandle,
                    options,
                    cancellationToken);
                if (fast is null)
                {
                    return Result(
                        CurrencyWarsNavigationState.TimedOut,
                        current.PageId,
                        "快速点击路径未能在预期时间内到达敌人概览页；已安全停止。");
                }

                current = fast;
                continue;
            }

            Publish(
                CurrencyWarsNavigationState.WaitingForPage,
                current.PageId,
                $"已稳定识别：{current.DisplayName}（{current.Confidence:P1}）。");

            var recognition = await ReadOpeningDataIfNeededAsync(
                windowHandle,
                current.PageId,
                options,
                cancellationToken);
            if (!recognition.Succeeded)
            {
                return Result(
                    CurrencyWarsNavigationState.RecognitionIncomplete,
                    current.PageId,
                    recognition.Message);
            }

            if (options.StopAfterOpeningRecognition &&
                string.Equals(
                    current.PageId,
                    "investment_environment",
                    StringComparison.OrdinalIgnoreCase) &&
                !_investmentEnvironmentFallbackRequired)
            {
                return Result(
                    CurrencyWarsNavigationState.OpeningRecognized,
                    current.PageId,
                    "敌人信息与三个投资环境均已完整识别，当前停留在投资环境页面。");
            }

            if (step.Terminal &&
                (options.StopAtPreparation ||
                 !string.Equals(
                     current.PageId,
                     config.PreparationPageId,
                     StringComparison.OrdinalIgnoreCase)))
            {
                return _investmentEnvironmentFallbackRequired
                    ? Result(
                        CurrencyWarsNavigationState
                            .InvestmentEnvironmentFallbackSelected,
                        current.PageId,
                        "投资环境识别不完整，已降级选择一个候选并进入 1-1；" +
                        "本轮必须按未命中处理并安全重开。")
                    : Result(
                        CurrencyWarsNavigationState.ReachedPreparation,
                        current.PageId,
                        "已到达首次备战页面 1-1。");
            }

            if (step.Actions.Count == 0)
            {
                return Result(
                    CurrencyWarsNavigationState.UnsupportedPage,
                    current.PageId,
                    $"页面“{current.DisplayName}”未配置动作；已安全停止。");
            }

            var restartFromRecoveredPage = false;
            foreach (var action in step.Actions)
            {
                if (action.RequiredGameMode is { } requiredGameMode &&
                    requiredGameMode != options.GameMode)
                {
                    continue;
                }

                if (ActiveUtcNow >= deadline)
                {
                    return Result(
                        CurrencyWarsNavigationState.TimedOut,
                        current.PageId,
                        "导航达到最长运行时间，已安全停止。");
                }

                var effectiveAction = ResolveInvestmentSelectionAction(
                    current.PageId,
                    action,
                    options.PreferredInvestmentEnvironmentIds);
                if (effectiveAction is null)
                {
                    return Result(
                        CurrencyWarsNavigationState.RecognitionIncomplete,
                        current.PageId,
                        "当前三个投资环境中没有命中用户可接受列表；未选择随机候选。");
                }

                Publish(
                    CurrencyWarsNavigationState.Acting,
                    current.PageId,
                    $"准备执行：{effectiveAction.DisplayName}");
                var actionResult = await ExecuteActionWithRetryAsync(
                    windowHandle,
                    effectiveAction,
                    cancellationToken);
                if (!actionResult.Succeeded)
                {
                    return Result(
                        CurrencyWarsNavigationState.InputBlocked,
                        current.PageId,
                        actionResult.Message);
                }

                Publish(
                    CurrencyWarsNavigationState.Acting,
                    current.PageId,
                    actionResult.Message);

                if (effectiveAction.ExpectedPageIds.Count == 0)
                {
                    continue;
                }

                if (options.FastPathFromHome &&
                    options.GameMode == CurrencyWarsGameMode.Standard &&
                    string.Equals(
                        effectiveAction.Id,
                        "enemy_overview_next",
                        StringComparison.OrdinalIgnoreCase))
                {
                    // 快速刷开局：敌人概览识别完成后——
                    // ① 先点击"确认/下一页"(1514,985) 进入后续页面；
                    // ② 再盲点连点同一位置 (1514,985) 3 秒——不切换坐标：
                    //    (960,720) 在敌人概览页是空白，点不到"下一页"，
                    //    会卡在敌人概览页进不去（用户实测确认）。
                    // ③ 等待投资环境页出现。
                    Publish(
                        CurrencyWarsNavigationState.Acting,
                        current.PageId,
                        "快速刷开局：敌人概览已确认，点击下一页进入后续页面。");
                    var confirmResult = await ExecuteActionWithRetryAsync(
                        windowHandle,
                        effectiveAction,
                        cancellationToken);
                    if (!confirmResult.Succeeded)
                    {
                        return Result(
                            CurrencyWarsNavigationState.InputBlocked,
                            current.PageId,
                            "快速刷开局点击敌人概览下一页失败；已安全停止。");
                    }

                    Publish(
                        CurrencyWarsNavigationState.Acting,
                        current.PageId,
                        "快速刷开局：已点击下一页，持续连点同一位置 3 秒。");
                    if (effectiveAction.Point is not { } nextPoint)
                    {
                        return Result(
                            CurrencyWarsNavigationState.UnsupportedPage,
                            current.PageId,
                            "敌人概览下一页动作未配置点击坐标；已安全停止。");
                    }

                    if (!await BlindClickAsync(
                            windowHandle,
                            nextPoint,
                            TimeSpan.FromSeconds(3),
                            TimeSpan.FromMilliseconds(50),
                            cancellationToken))
                    {
                        return Result(
                            CurrencyWarsNavigationState.InputBlocked,
                            current.PageId,
                            "快速刷开局盲点连点下一页位置失败；已安全停止。");
                    }

                    current = await WaitForStablePageWithRecoveryAsync(
                        windowHandle,
                        ["investment_environment"],
                        TimeSpan.FromSeconds(20),
                        options.EnableUnknownPageEscapeRecovery,
                        cancellationToken);
                    if (current is null)
                    {
                        return Result(
                            CurrencyWarsNavigationState.TimedOut,
                            step.PageId,
                            "快速刷开局连点后未识别到投资环境页；已安全停止。");
                    }

                    continue;
                }

                if (options.FastPathFromHome &&
                    options.GameMode == CurrencyWarsGameMode.Standard &&
                    string.Equals(
                        effectiveAction.Id,
                        "select_first_investment",
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(
                        _selectedInvestmentEnvironmentId,
                        BlueSeaInvestmentEnvironmentId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    // 快速刷开局：点选投资环境后不等页面稳定——
                    // 0.05s 后再点一次（确保选中），延迟 50ms 后直接点确认。
                    Publish(
                        CurrencyWarsNavigationState.Acting,
                        current.PageId,
                        "快速刷开局：点选投资环境，0.05s 后复点，50ms 后直接确认。");
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(50),
                        cancellationToken);
                    if (!await BlindClickOnceAsync(
                            windowHandle,
                            effectiveAction.Point,
                            cancellationToken))
                    {
                        return Result(
                            CurrencyWarsNavigationState.InputBlocked,
                            current.PageId,
                            "快速刷开局复点投资环境失败；已安全停止。");
                    }

                    await Task.Delay(
                        TimeSpan.FromMilliseconds(50),
                        cancellationToken);

                    // 直接从当前步骤配置取"确认投资环境"动作并点击。
                    var confirmAction = step.Actions.FirstOrDefault(
                        candidate => string.Equals(
                            candidate.Id,
                            "confirm_investment",
                            StringComparison.OrdinalIgnoreCase));
                    if (confirmAction is null)
                    {
                        return Result(
                            CurrencyWarsNavigationState.UnsupportedPage,
                            current.PageId,
                            "投资环境页未配置确认动作；已安全停止。");
                    }

                    var confirmResult = await ExecuteActionWithRetryAsync(
                        windowHandle,
                        confirmAction,
                        cancellationToken);
                    if (!confirmResult.Succeeded)
                    {
                        return Result(
                            CurrencyWarsNavigationState.InputBlocked,
                            current.PageId,
                            confirmResult.Message);
                    }

                    current = await WaitForStablePageWithRecoveryAsync(
                        windowHandle,
                        confirmAction.ExpectedPageIds,
                        TimeSpan.FromSeconds(20),
                        options.EnableUnknownPageEscapeRecovery,
                        cancellationToken);
                    if (current is null)
                    {
                        return Result(
                            CurrencyWarsNavigationState.TimedOut,
                            step.PageId,
                            "快速刷开局确认投资环境后未识别到备战页；已安全停止。");
                    }

                    continue;
                }


                current = IsBlueSeaConfirmation(effectiveAction)
                    ? await CompleteBlueSeaExtraRewardAsync(
                        windowHandle,
                        effectiveAction.ExpectedPageIds,
                        cancellationToken)
                    : options.FastPathFromHome &&
                      options.GameMode == CurrencyWarsGameMode.Standard &&
                      FastPathUsesSingleFrameConfirmation(effectiveAction)
                        ? await FastWaitForPageAsync(
                            windowHandle,
                            effectiveAction.ExpectedPageIds,
                            TimeSpan.FromMilliseconds(
                                effectiveAction.TimeoutMilliseconds),
                            cancellationToken)
                        : await WaitForStablePageWithRecoveryAsync(
                            windowHandle,
                            effectiveAction.ExpectedPageIds,
                            TimeSpan.FromMilliseconds(
                                effectiveAction.TimeoutMilliseconds),
                            options.EnableUnknownPageEscapeRecovery,
                            cancellationToken);
                if (current is null)
                {
                    return Result(
                        CurrencyWarsNavigationState.TimedOut,
                        step.PageId,
                        $"执行“{effectiveAction.DisplayName}”后未出现预期页面；已安全停止。");
                }

                if (!effectiveAction.ExpectedPageIds.Contains(
                        current.PageId,
                        StringComparer.OrdinalIgnoreCase))
                {
                    Publish(
                        CurrencyWarsNavigationState.WaitingForPage,
                        current.PageId,
                        $"Esc 恢复后识别到“{current.DisplayName}”，" +
                        "将从该已知页面重新进入状态机。");
                    restartFromRecoveredPage = true;
                    break;
                }
            }

            if (restartFromRecoveredPage)
            {
                continue;
            }

            if (step.Terminal)
            {
                return _investmentEnvironmentFallbackRequired
                    ? Result(
                        CurrencyWarsNavigationState
                            .InvestmentEnvironmentFallbackSelected,
                        current.PageId,
                        "投资环境识别不完整，已降级选择一个候选并进入后续页面；" +
                        "本轮必须按未命中处理并安全重开。")
                    : Result(
                        CurrencyWarsNavigationState.EnteredBattle,
                        current.PageId,
                        "初始布阵动作已执行，并已点击出战。");
            }
        }

        return Result(
            CurrencyWarsNavigationState.TimedOut,
            current.PageId,
            "导航达到最长运行时间，已安全停止。");
    }

    private async Task<ActionResult> ExecuteActionAsync(
        GameWindowInfo window,
        NavigationActionDefinition action,
        CancellationToken cancellationToken)
    {
        var policy = new ActionPolicy
        {
            // 外部程序（如开发助手）可能持续移动光标：点击必须零延迟、
            // 不做光标到达验证，在光标被抢走前的窗口期内完成点击。
            VerifyPointerArrivalBeforeClick = false,
            PointerSettleDelay = TimeSpan.Zero,
            AfterActionDelay = TimeSpan.Zero
        };
        if (string.Equals(action.Kind, "escape", StringComparison.OrdinalIgnoreCase))
        {
            return await input.PressKeyAsync(
                window,
                InputKey.Escape,
                policy,
                cancellationToken);
        }

        if (string.Equals(action.Kind, "altClick", StringComparison.OrdinalIgnoreCase) &&
            action.Point is not null)
        {
            var altClickPoint = MapStandardPoint(window, action.Point);
            return await input.ClickWithModifierAsync(
                new ClickTarget(
                    action.Id,
                    action.DisplayName,
                    window,
                    BoundsAround(window, altClickPoint)),
                InputKey.LeftAlt,
                policy,
                cancellationToken);
        }

        if (!string.Equals(action.Kind, "click", StringComparison.OrdinalIgnoreCase) ||
            action.Point is null)
        {
            if (string.Equals(action.Kind, "drag", StringComparison.OrdinalIgnoreCase) &&
                action.Point is not null &&
                action.TargetPoint is not null)
            {
                var sourcePoint = MapStandardPoint(window, action.Point);
                var targetPoint = MapStandardPoint(window, action.TargetPoint);
                var sourceBounds = BoundsAround(window, sourcePoint);
                return await input.DragAsync(
                    new ClickTarget(action.Id, action.DisplayName, window, sourceBounds),
                    targetPoint,
                    TimeSpan.FromMilliseconds(action.DurationMilliseconds),
                    policy,
                    cancellationToken);
            }

            return ActionResult.Failure(
                $"动作“{action.DisplayName}”配置无效；未执行输入。");
        }

        var point = MapStandardPoint(window, action.Point);
        return await input.ClickAsync(
            new ClickTarget(action.Id, action.DisplayName, window, BoundsAround(window, point)),
            policy,
            cancellationToken);
    }

    private async Task<ActionResult> ExecuteActionWithRetryAsync(
        nint windowHandle,
        NavigationActionDefinition action,
        CancellationToken cancellationToken)
    {
        ActionResult? latest = null;
        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            var window = await WaitForForegroundWindowAsync(
                windowHandle,
                cancellationToken);
            if (window is null)
            {
                return ActionResult.Failure(
                    $"执行“{action.DisplayName}”前游戏窗口已失效。");
            }

            latest = await ExecuteActionAsync(
                window,
                action,
                cancellationToken);
            if (latest.Succeeded)
            {
                return latest;
            }

            Publish(
                CurrencyWarsNavigationState.Acting,
                null,
                $"“{action.DisplayName}”第 {attempt}/{maximumAttempts} 次输入失败：" +
                $"{latest.Message}；" +
                (attempt < maximumAttempts ? "准备重试。" : "已达到重试上限。"));
            if (attempt < maximumAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            }
        }

        return latest ??
               ActionResult.Failure($"“{action.DisplayName}”未能执行。");
    }

    private async Task<ActionResult> ReadOpeningDataIfNeededAsync(
        nint windowHandle,
        string pageId,
        CurrencyWarsNavigationOptions options,
        CancellationToken cancellationToken)
    {
        var isEnemyPage = string.Equals(
            pageId,
            "enemy_overview",
            StringComparison.OrdinalIgnoreCase);
        var isInvestmentPage = string.Equals(
            pageId,
            "investment_environment",
            StringComparison.OrdinalIgnoreCase);
        if ((!isEnemyPage || _enemyOverview is not null) &&
            (!isInvestmentPage || _investmentEnvironments is not null))
        {
            return ActionResult.Success("无需重复读取开局信息。");
        }

        if (isEnemyPage)
        {
            var stable = await ReadStableEnemyOverviewAsync(
                windowHandle,
                cancellationToken);
            if (stable.Result is null)
            {
                return ActionResult.Failure(
                    "敌人页面未取得任何可用识别帧，无法确认当前页面内容。");
            }

            _enemyOverview = stable.Result;
            if (!stable.Succeeded)
            {
                PublishFallback(
                    "EnemyOverviewRecognitionDegraded",
                    "敌人信息未能全部稳定识别；未知阵营/负面词条按未命中用户黑名单处理，" +
                    "仅使用已连续确认的项目继续本轮。");
            }

            Publish(
                CurrencyWarsNavigationState.WaitingForPage,
                pageId,
                $"敌人阵营：{FormatItems(_enemyOverview.Competitors)}；" +
                $"负面词条：{FormatItems(_enemyOverview.EnemyModifiers)}");
            return ActionResult.Success(
                stable.Succeeded
                    ? "敌人信息已连续两次稳定识别。"
                    : "敌人信息已按已确认项目降级继续。");
        }

        var stableInvestments = await ReadStableInvestmentEnvironmentsAsync(
            windowHandle,
            cancellationToken);
        if (stableInvestments.Result is null)
        {
            return ActionResult.Failure(
                "投资环境页面未取得任何可用识别帧，无法确认候选区域。");
        }

        _investmentEnvironments = stableInvestments.Result;
        if (!stableInvestments.Succeeded)
        {
            _investmentEnvironmentFallbackRequired = true;
            PublishFallback(
                "InvestmentEnvironmentRecognitionDegraded",
                "投资环境未能完整稳定识别；将优先选择一个已识别候选，" +
                "没有已识别候选时选择第一槽。进入 1-1 后本轮强制按未命中重开。");
        }

        Publish(
            CurrencyWarsNavigationState.WaitingForPage,
            pageId,
            $"投资环境：{FormatItems(_investmentEnvironments.Options)}");

        var preferredIds = options.PreferredInvestmentEnvironmentIds;
        if (_investmentEnvironmentFallbackRequired)
        {
            return ActionResult.Success(
                "投资环境识别已进入任选一项后强制重开的降级路径。");
        }

        if (preferredIds.Count > 0 &&
            !_investmentEnvironments.InvestmentEnvironments.Any(
                item => preferredIds.Contains(item.Id)))
        {
            Publish(
                CurrencyWarsNavigationState.Acting,
                pageId,
                "首轮三个投资环境均未命中偏好，准备使用一次免费刷新。");
            var window = await WaitForForegroundWindowAsync(
                windowHandle,
                cancellationToken);
            if (window is null)
            {
                return ActionResult.Failure(
                    "刷新投资环境前游戏窗口已失效。");
            }

            var refreshPoint = MapStandardPoint(
                window,
                InvestmentRefreshPoint);
            var refresh = await input.ClickAsync(
                new ClickTarget(
                    "refresh_investment_options",
                    "刷新投资环境候选",
                    window,
                    BoundsAround(window, refreshPoint)),
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.FromMilliseconds(250)
                },
                cancellationToken);
            if (!refresh.Succeeded)
            {
                return refresh;
            }

            var previousIds = _investmentEnvironments
                .InvestmentEnvironments
                .Select(item => item.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var refreshedInvestments =
                await ReadStableInvestmentEnvironmentsAsync(
                    windowHandle,
                    cancellationToken,
                    previousIds);
            if (refreshedInvestments.Result is null)
            {
                return ActionResult.Failure(
                    "点击刷新后未取得任何可用投资环境识别帧。");
            }

            _investmentEnvironments = refreshedInvestments.Result;
            if (!refreshedInvestments.Succeeded)
            {
                _investmentEnvironmentFallbackRequired = true;
                PublishFallback(
                    "InvestmentEnvironmentRefreshRecognitionDegraded",
                    "刷新后的投资环境仍未能完整稳定识别；将任选一个候选进入 1-1，" +
                    "随后把本轮按未命中安全重开。");
                return ActionResult.Success(
                    "刷新后识别已进入任选一项并强制重开的降级路径。");
            }

            Publish(
                CurrencyWarsNavigationState.WaitingForPage,
                pageId,
                $"刷新后的投资环境：{FormatItems(_investmentEnvironments.Options)}");
        }

        if (!options.StopAfterOpeningRecognition &&
            preferredIds.Count > 0 &&
            !_investmentEnvironments.InvestmentEnvironments.Any(
                item => preferredIds.Contains(item.Id)))
        {
            return ActionResult.Failure(
                "免费刷新后仍没有命中用户可接受的投资环境；未选择随机候选。");
        }

        return ActionResult.Success("投资环境已连续两次稳定识别。");
    }

    private async Task<PageClassificationResult?> FastPathFromHomeAsync(
        nint windowHandle,
        CurrencyWarsNavigationOptions options,
        CancellationToken cancellationToken)
    {
        // 快速刷开局（标准博弈）：主界面 → 开始本局 之间盲点连点。
        // 用户方案：点"开始货币战争"后，每 0.05s 连点"开始本局"(1690,967)
        // 持续 4 秒（不识别中间页面）；停止后等敌人概览稳定出现（保留识别）。
        if (!_steps.TryGetValue(
                "currency_wars_home",
                out var homeStep) ||
            !_steps.TryGetValue(
                "rank_difficulty",
                out var rankStep))
        {
            return null;
        }

        var startAction = homeStep.Actions.FirstOrDefault(
            action => string.Equals(
                action.Id,
                "start_currency_wars",
                StringComparison.OrdinalIgnoreCase));
        var startRunAction = rankStep.Actions.FirstOrDefault(
            action => string.Equals(
                action.Id,
                "start_run",
                StringComparison.OrdinalIgnoreCase));
        if (startAction is null || startRunAction is null)
        {
            return null;
        }

        Publish(
            CurrencyWarsNavigationState.Acting,
            "currency_wars_home",
            "快速刷开局：点击开始货币战争后，盲点连点开始本局 4 秒（标准博弈）。");

        // 第 1 步：点击"开始货币战争"。
        var startResult = await ExecuteActionWithRetryAsync(
            windowHandle,
            startAction,
            cancellationToken);
        if (!startResult.Succeeded)
        {
            return null;
        }

        // 第 2 步：盲点连点"开始本局"位置 4 秒（每 50ms 一次，共 80 次）。
        // 模式选择/职级难度页面动画期间该按钮位置不变，直接覆盖。
        if (!await BlindClickAsync(
                windowHandle,
                FastStartRunPoint,
                TimeSpan.FromSeconds(4),
                TimeSpan.FromMilliseconds(50),
                cancellationToken))
        {
            return null;
        }

        // 第 3 步：等待敌人概览稳定出现（入场动画很长，保留稳定识别）。
        var enemyOverview = await WaitForStablePageWithRecoveryAsync(
            windowHandle,
            startRunAction.ExpectedPageIds,
            TimeSpan.FromSeconds(20),
            options.EnableUnknownPageEscapeRecovery,
            cancellationToken);
        if (enemyOverview is null)
        {
            return null;
        }

        Publish(
            CurrencyWarsNavigationState.WaitingForPage,
            enemyOverview.PageId,
            "快速刷开局已到达敌人概览页；识别敌人信息后再点确认。");
        return enemyOverview;
    }

    /// <summary>
    /// 盲点连点：按固定间隔重复点击固定位置，持续指定时长。
    /// 用于页面动画期间按钮位置不变的场景（开始本局/位面进度继续）。
    /// </summary>
    private async Task<bool> BlindClickAsync(
        nint windowHandle,
        StandardPoint point,
        TimeSpan duration,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        var deadline = ActiveUtcNow + duration;
        while (ActiveUtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = await WaitForForegroundWindowAsync(
                windowHandle,
                cancellationToken);
            if (window is null)
            {
                return false;
            }

            var clickPoint = MapStandardPoint(window, point);
            var action = await input.ClickAsync(
                new ClickTarget(
                    "fast_click_" + point.X + "_" + point.Y,
                    "快速刷开局盲点点击",
                    window,
                    BoundsAround(window, clickPoint)),
                new ActionPolicy
                {
                    VerifyPointerArrivalBeforeClick = false,
                    PointerSettleDelay = TimeSpan.Zero,
                    AfterActionDelay = TimeSpan.Zero
                },
                cancellationToken);
            if (!action.Succeeded)
            {
                return false;
            }

            await Task.Delay(interval, cancellationToken);
        }

        return true;
    }

    private async Task<bool> BlindClickOnceAsync(
        nint windowHandle,
        StandardPoint? point,
        CancellationToken cancellationToken)
    {
        if (point is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var window = await WaitForForegroundWindowAsync(
            windowHandle,
            cancellationToken);
        if (window is null)
        {
            return false;
        }

        var clickPoint = MapStandardPoint(window, point);
        var action = await input.ClickAsync(
            new ClickTarget(
                "fast_click_once_" + point.X + "_" + point.Y,
                "快速刷开局单次点击",
                window,
                BoundsAround(window, clickPoint)),
            new ActionPolicy
            {
                VerifyPointerArrivalBeforeClick = false,
                PointerSettleDelay = TimeSpan.Zero,
                AfterActionDelay = TimeSpan.Zero
            },
            cancellationToken);
        return action.Succeeded;
    }

private async Task<PageClassificationResult?> FastWaitForPageAsync(
        nint windowHandle,
        IReadOnlyCollection<string>? expectedPageIds,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = ActiveUtcNow + timeout;
        while (ActiveUtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = await WaitForForegroundWindowAsync(
                windowHandle,
                cancellationToken);
            if (window is null)
            {
                return null;
            }

            var frame = await capture.CaptureAsync(window, cancellationToken);
            var detected = classifier.Classify(frame);
            if (detected is not null &&
                (expectedPageIds is null ||
                 expectedPageIds.Contains(
                     detected.PageId,
                     StringComparer.OrdinalIgnoreCase)))
            {
                return detected;
            }

            await Task.Delay(FastPathPollInterval, cancellationToken);
        }

        return null;
    }

    private static bool FastPathUsesSingleFrameConfirmation(
        NavigationActionDefinition action) =>
        // 敌人概览确认后等待位面进度页：一出就点继续，动画继续播放。
        // 位面进度页不需要稳定识别（无随机内容）。
        string.Equals(
            action.Id,
            "enemy_overview_next",
            StringComparison.OrdinalIgnoreCase);

    private NavigationActionDefinition? ResolveInvestmentSelectionAction(
        string pageId,
        NavigationActionDefinition action,
        IReadOnlySet<string> preferredIds)
    {
        if (!string.Equals(
                pageId,
                "investment_environment",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                action.Id,
                "select_first_investment",
                StringComparison.OrdinalIgnoreCase))
        {
            return action;
        }

        if (_investmentEnvironments is null)
        {
            return null;
        }

        var selected = _investmentEnvironmentFallbackRequired
            ? _investmentEnvironments.Options
                .Where(item => item.Item is not null)
                .OrderBy(item => item.Slot)
                .FirstOrDefault() ??
              _investmentEnvironments.Options
                  .OrderBy(item => item.Slot)
                  .FirstOrDefault()
            : InvestmentEnvironmentSelection.FindPreferredOption(
                _investmentEnvironments,
                preferredIds);
        if (selected is null ||
            (!_investmentEnvironmentFallbackRequired && selected.Item is null) ||
            selected.Slot < 0 ||
            selected.Slot >= InvestmentOptionPoints.Count)
        {
            return null;
        }

        _selectedInvestmentEnvironmentId = selected.Item?.Id;
        return new NavigationActionDefinition
        {
            Id = $"select_investment_slot_{selected.Slot + 1}",
            DisplayName = _investmentEnvironmentFallbackRequired
                ? $"降级选择投资环境槽位 {selected.Slot + 1}（本轮强制重开）"
                : $"选择投资环境：{selected.Item!.DisplayName}",
            Kind = action.Kind,
            Point = InvestmentOptionPoints[selected.Slot],
            TargetPoint = action.TargetPoint,
            DurationMilliseconds = action.DurationMilliseconds,
            ExpectedPageIds = [.. action.ExpectedPageIds],
            TimeoutMilliseconds = action.TimeoutMilliseconds,
            RequiredGameMode = action.RequiredGameMode
        };
    }

    private bool IsBlueSeaConfirmation(NavigationActionDefinition action) =>
        string.Equals(
            _selectedInvestmentEnvironmentId,
            BlueSeaInvestmentEnvironmentId,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            action.Id,
            "confirm_investment",
            StringComparison.OrdinalIgnoreCase);

    private async Task<PageClassificationResult?>
        CompleteBlueSeaExtraRewardAsync(
            nint windowHandle,
            IReadOnlyCollection<string> expectedPageIds,
            CancellationToken cancellationToken)
    {
        Publish(
            CurrencyWarsNavigationState.WaitingForPage,
            "blue_sea_extra_reward",
            "已选择“蓝海”，正在等待随机投资策略奖励页或直接进入备战页。");

        var preparation = await WaitForStablePageAsync(
            windowHandle,
            expectedPageIds,
            TimeSpan.FromSeconds(2),
            cancellationToken);
        if (preparation is not null)
        {
            return preparation;
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var window = await WaitForForegroundWindowAsync(
                windowHandle,
                cancellationToken);
            if (window is null)
            {
                return null;
            }

            var cardPoint = MapStandardPoint(
                window,
                BlueSeaRewardCardPoint);
            Publish(
                CurrencyWarsNavigationState.Acting,
                "blue_sea_extra_reward",
                $"蓝海额外奖励页：第 {attempt}/3 次尝试，先选择中央投资策略卡牌。");
            var selection = await input.ClickAsync(
                new ClickTarget(
                    "select_blue_sea_random_investment_strategy",
                    "选择蓝海随机获得的投资策略",
                    window,
                    BoundsAround(window, cardPoint)),
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.FromMilliseconds(250)
                },
                cancellationToken);
            if (!selection.Succeeded)
            {
                continue;
            }

            window = await WaitForForegroundWindowAsync(
                windowHandle,
                cancellationToken);
            if (window is null)
            {
                return null;
            }

            var confirmPoint = MapStandardPoint(
                window,
                BlueSeaRewardConfirmPoint);
            Publish(
                CurrencyWarsNavigationState.Acting,
                "blue_sea_extra_reward",
                $"蓝海额外奖励页：第 {attempt}/3 次尝试，卡牌已点击，继续点击确定。");
            var confirmation = await input.ClickAsync(
                new ClickTarget(
                    "confirm_blue_sea_extra_reward",
                    "确认蓝海随机投资策略奖励",
                    window,
                    BoundsAround(window, confirmPoint)),
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.FromMilliseconds(300)
                },
                cancellationToken);
            if (!confirmation.Succeeded)
            {
                continue;
            }

            preparation = await WaitForStablePageAsync(
                windowHandle,
                expectedPageIds,
                TimeSpan.FromSeconds(5),
                cancellationToken);
            if (preparation is not null)
            {
                return preparation;
            }
        }

        return null;
    }

    private async Task<(EnemyOverviewReadResult? Result, bool Succeeded)>
        ReadStableEnemyOverviewAsync(
            nint windowHandle,
            CancellationToken cancellationToken)
    {
        var deadline = ActiveUtcNow + TimeSpan.FromSeconds(30);
        EnemyOverviewReadResult? latest = null;
        var competitorVotes = new OpeningRecognitionAccumulator(3);
        var modifierVotes = new OpeningRecognitionAccumulator(4);
        var attempt = 0;
        var repeatedIncompleteSignatureCount = 0;
        string? previousIncompleteSignature = null;
        while (ActiveUtcNow < deadline)
        {
            attempt++;
            cancellationToken.ThrowIfCancellationRequested();
            var window = await WaitForForegroundWindowAsync(
                windowHandle,
                cancellationToken);
            if (window is null)
            {
                return (latest, false);
            }

            var frame = await capture.CaptureAsync(window, cancellationToken);
            latest = await openingPageReader.ReadEnemyOverviewAsync(
                frame,
                cancellationToken);
            var incompleteSignature = string.Join(
                "|",
                latest.Competitors
                    .Concat(latest.EnemyModifiers)
                    .OrderBy(item => item.Slot)
                    .Select(item =>
                        item.Item?.Id ??
                        GameDataNameMatcher.Normalize(item.RawText)));
            if (!latest.IsComplete &&
                string.Equals(
                    incompleteSignature,
                    previousIncompleteSignature,
                    StringComparison.Ordinal))
            {
                repeatedIncompleteSignatureCount++;
            }
            else
            {
                repeatedIncompleteSignatureCount = 1;
                previousIncompleteSignature = incompleteSignature;
            }

            competitorVotes.Observe(latest.Competitors);
            modifierVotes.Observe(latest.EnemyModifiers);
            if (competitorVotes.TryBuild(out var stableCompetitors) &&
                modifierVotes.TryBuild(out var stableModifiers))
            {
                return (
                    new EnemyOverviewReadResult(
                        stableCompetitors,
                        stableModifiers),
                    true);
            }

            Publish(
                CurrencyWarsNavigationState.WaitingForPage,
                "enemy_overview",
                $"敌人信息第 {attempt} 次识别：阵营已稳定 " +
                $"{competitorVotes.ConfirmedSlotCount}/3，词条已稳定 " +
                $"{modifierVotes.ConfirmedSlotCount}/4；本帧阵营 " +
                $"{FormatItems(latest.Competitors)}；本帧词条 " +
                $"{FormatItems(latest.EnemyModifiers)}");
            if (!latest.IsComplete &&
                repeatedIncompleteSignatureCount >= 6)
            {
                Publish(
                    CurrencyWarsNavigationState.WaitingForPage,
                    "enemy_overview_static_failure",
                    "连续 6 帧得到完全相同的不完整文字，画面与备用预处理结果均未变化；" +
                    "停止无效 OCR 重复并使用已稳定槽位降级继续。");
                return (
                    new EnemyOverviewReadResult(
                        competitorVotes.BuildBestEffort(latest.Competitors),
                        modifierVotes.BuildBestEffort(latest.EnemyModifiers)),
                    false);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return latest is null
            ? (null, false)
            : (
                new EnemyOverviewReadResult(
                    competitorVotes.BuildBestEffort(latest.Competitors),
                    modifierVotes.BuildBestEffort(latest.EnemyModifiers)),
                false);
    }

    private async Task<(
        InvestmentEnvironmentReadResult? Result,
        bool Succeeded)> ReadStableInvestmentEnvironmentsAsync(
            nint windowHandle,
            CancellationToken cancellationToken,
            IReadOnlySet<string>? excludedOptionIds = null)
    {
        var deadline = ActiveUtcNow + TimeSpan.FromSeconds(30);
        InvestmentEnvironmentReadResult? latest = null;
        var votes = new OpeningRecognitionAccumulator(3);
        var optionsChanged = excludedOptionIds is null;
        var attempt = 0;
        var repeatedIncompleteSignatureCount = 0;
        string? previousIncompleteSignature = null;
        while (ActiveUtcNow < deadline)
        {
            attempt++;
            cancellationToken.ThrowIfCancellationRequested();
            var window = await WaitForForegroundWindowAsync(
                windowHandle,
                cancellationToken);
            if (window is null)
            {
                return (latest, false);
            }

            var frame = await capture.CaptureAsync(window, cancellationToken);
            latest = await openingPageReader.ReadInvestmentEnvironmentsAsync(
                frame,
                cancellationToken);
            var incompleteSignature = string.Join(
                "|",
                latest.Options.Select(option =>
                    $"{option.Slot}:{option.Item?.Id ?? "?"}:" +
                    $"{GameDataNameMatcher.Normalize(option.RawText)}"));
            if (!latest.IsComplete &&
                string.Equals(
                    previousIncompleteSignature,
                    incompleteSignature,
                    StringComparison.Ordinal))
            {
                repeatedIncompleteSignatureCount++;
            }
            else
            {
                previousIncompleteSignature = incompleteSignature;
                repeatedIncompleteSignatureCount =
                    latest.IsComplete ? 0 : 1;
            }
            if (!optionsChanged)
            {
                if (!latest.IsComplete)
                {
                    votes.Observe(latest.Options);
                }

                var currentIds = latest.InvestmentEnvironments
                    .Select(item => item.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!latest.IsComplete ||
                    excludedOptionIds!.SetEquals(currentIds))
                {
                    Publish(
                        CurrencyWarsNavigationState.WaitingForPage,
                        "investment_environment",
                        $"投资环境第 {attempt} 次识别仍是刷新前候选或动画未结束，继续等待。");
                    if (!latest.IsComplete &&
                        repeatedIncompleteSignatureCount >= 6)
                    {
                        Publish(
                            CurrencyWarsNavigationState.WaitingForPage,
                            "investment_environment_refresh_static_failure",
                            "刷新后连续 6 帧保持相同的不完整结果；" +
                            "停止无效 OCR 重复并进入任选一项后强制重开的降级路径。",
                            TaskEventLevel.Warning);
                        return (
                            new InvestmentEnvironmentReadResult(
                                votes.BuildBestEffort(latest.Options)),
                            false);
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                    continue;
                }

                optionsChanged = true;
                Publish(
                    CurrencyWarsNavigationState.WaitingForPage,
                    "investment_environment",
                    "已确认刷新后的候选发生变化，开始累计稳定识别结果。");
            }

            votes.Observe(latest.Options);
            if (votes.TryBuild(out var stableOptions))
            {
                return (
                    new InvestmentEnvironmentReadResult(stableOptions),
                    true);
            }

            Publish(
                CurrencyWarsNavigationState.WaitingForPage,
                "investment_environment",
                $"投资环境第 {attempt} 次识别：已稳定 " +
                $"{votes.ConfirmedSlotCount}/3；本帧 " +
                FormatItems(latest.Options));
            if (!latest.IsComplete &&
                repeatedIncompleteSignatureCount >= 6)
            {
                Publish(
                    CurrencyWarsNavigationState.WaitingForPage,
                    "investment_environment_static_failure",
                    "连续 6 帧得到完全相同的不完整结果；标题、扩大区域和说明反查均已尝试，" +
                    "停止无效 OCR 重复并进入任选一项后强制重开的降级路径。");
                return (
                    new InvestmentEnvironmentReadResult(
                        votes.BuildBestEffort(latest.Options)),
                    false);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return latest is null
            ? (null, false)
            : (
                new InvestmentEnvironmentReadResult(
                    votes.BuildBestEffort(latest.Options)),
                false);
    }

    private static string FormatItems(IEnumerable<RecognizedOpeningItem> items) =>
        string.Join(
            "、",
            items.Select(item =>
                item.Item is null
                    ? $"槽位{item.Slot + 1}=未识别({item.RawText})"
                    : item.Item.DisplayName));

    private static PixelRect BoundsAround(GameWindowInfo window, PixelPoint point)
    {
        const int size = 4;
        return new PixelRect(
            Math.Clamp(point.X - size / 2, 0, Math.Max(0, window.ClientArea.Width - size)),
            Math.Clamp(point.Y - size / 2, 0, Math.Max(0, window.ClientArea.Height - size)),
            size,
            size);
    }

    private PixelPoint MapStandardPoint(GameWindowInfo window, StandardPoint point)
    {
        if (config.ReferenceWidth <= 0 || config.ReferenceHeight <= 0)
        {
            throw new InvalidOperationException("导航参考分辨率必须为正数。");
        }

        return new PixelPoint(
            (int)Math.Round(point.X * window.ClientArea.Width / (double)config.ReferenceWidth),
            (int)Math.Round(point.Y * window.ClientArea.Height / (double)config.ReferenceHeight));
    }

    private async Task<PageClassificationResult?> WaitForStablePageAsync(
        nint windowHandle,
        IReadOnlyCollection<string>? expectedPageIds,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = ActiveUtcNow + timeout;
        string? lastPageId = null;
        var stableCount = 0;
        var pollAttempt = 0;

        while (ActiveUtcNow < deadline)
        {
            pollAttempt++;
            cancellationToken.ThrowIfCancellationRequested();
            var window = await WaitForForegroundWindowAsync(
                windowHandle,
                cancellationToken);
            if (window is null)
            {
                return null;
            }

            var frame = await capture.CaptureAsync(window, cancellationToken);
            var detected = classifier.Classify(frame);
            if (detected is null ||
                (expectedPageIds is not null &&
                 !expectedPageIds.Contains(
                     detected.PageId,
                     StringComparer.OrdinalIgnoreCase)))
            {
                detected = await TryClassifyGuidePageByOcrAsync(
                    frame,
                    expectedPageIds,
                    cancellationToken);
            }

            if (detected is null &&
                pollAttempt % 5 == 0 &&
                expectedPageIds is not null &&
                classifier is IGamePageClassifierDiagnostics diagnostics)
            {
                var expectedScores = diagnostics.LastDiagnostics
                    .Where(item => expectedPageIds.Contains(
                        item.PageId,
                        StringComparer.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.Confidence)
                    .Take(4)
                    .Select(item =>
                        $"{item.PageId}/{item.AnchorId}=" +
                        $"{item.Confidence:P1}（阈值 {item.Threshold:P0}）")
                    .ToArray();
                if (expectedScores.Length > 0)
                {
                    Publish(
                        CurrencyWarsNavigationState.WaitingForPage,
                        null,
                        "预期页面模板尚未通过：" +
                        string.Join("；", expectedScores));
                }
            }

            var expected = detected is not null &&
                           (expectedPageIds is null ||
                            expectedPageIds.Contains(
                                detected.PageId,
                                StringComparer.OrdinalIgnoreCase));
            if (!expected)
            {
                lastPageId = null;
                stableCount = 0;
            }
            else if (string.Equals(
                         lastPageId,
                         detected!.PageId,
                         StringComparison.OrdinalIgnoreCase))
            {
                stableCount++;
            }
            else
            {
                lastPageId = detected!.PageId;
                stableCount = 1;
            }

            if (detected is not null &&
                stableCount >= Math.Max(1, config.StableDetections))
            {
                return detected;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(config.PollIntervalMilliseconds),
                cancellationToken);
        }

        return null;
    }

    private async Task<PageClassificationResult?>
        TryClassifyGuidePageByOcrAsync(
            CaptureFrame frame,
            IReadOnlyCollection<string>? expectedPageIds,
            CancellationToken cancellationToken)
    {
        if (expectedPageIds is null || !offlineOcr.IsAvailable)
        {
            return null;
        }

        var expectsGuideShell = expectedPageIds.Contains(
            "guide_shell",
            StringComparer.OrdinalIgnoreCase);
        var expectsCurrencyWars = expectedPageIds.Contains(
            "guide_currency_wars",
            StringComparer.OrdinalIgnoreCase);
        if (!expectsGuideShell && !expectsCurrencyWars)
        {
            return null;
        }

        var shellText = await offlineOcr.RecognizeAsync(
            frame,
            ScaleReferenceRegion(
                GuideShellTitleRegion,
                frame.Width,
                frame.Height),
            cancellationToken);
        var shellConfidence = BestOcrTextConfidence(
            shellText,
            "星际和平指南");
        if (shellConfidence < 0.72)
        {
            return null;
        }

        if (expectsCurrencyWars)
        {
            var activityText = await offlineOcr.RecognizeAsync(
                frame,
                ScaleReferenceRegion(
                    GuideActivityTitleRegion,
                    frame.Width,
                    frame.Height),
                cancellationToken);
            var activityConfidence = BestOcrTextConfidence(
                activityText,
                "货币战争");
            if (activityConfidence >= 0.72)
            {
                return new PageClassificationResult(
                    "guide_currency_wars",
                    "星际和平指南-货币战争",
                    (shellConfidence + activityConfidence) / 2,
                    []);
            }
        }

        return expectsGuideShell
            ? new PageClassificationResult(
                "guide_shell",
                "星际和平指南",
                shellConfidence,
                [])
            : null;
    }

    private static double BestOcrTextConfidence(
        OcrTextResult recognized,
        string expectedText) =>
        recognized.Lines
            .Prepend(recognized.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => GameDataNameMatcher.Similarity(
                text,
                expectedText))
            .DefaultIfEmpty(0)
            .Max();

    private PixelRect ScaleReferenceRegion(
        PixelRect source,
        int targetWidth,
        int targetHeight)
    {
        var horizontalScale =
            targetWidth / (double)config.ReferenceWidth;
        var verticalScale =
            targetHeight / (double)config.ReferenceHeight;
        return new PixelRect(
            (int)Math.Round(source.X * horizontalScale),
            (int)Math.Round(source.Y * verticalScale),
            (int)Math.Round(source.Width * horizontalScale),
            (int)Math.Round(source.Height * verticalScale));
    }

    private async Task<GameWindowInfo?> WaitForForegroundWindowAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        var window = windowService.Refresh(windowHandle) ?? _lastKnownWindow;
        if (window is null)
        {
            return null;
        }

        var foreground = await foregroundGuard.WaitUntilForegroundAsync(
            window,
            cancellationToken);
        _lastKnownWindow = foreground;
        return foreground;
    }

    private async Task<PageClassificationResult?>
        WaitForStablePageWithRecoveryAsync(
            nint windowHandle,
            IReadOnlyCollection<string>? expectedPageIds,
            TimeSpan timeout,
            bool enableUnknownPageRecovery,
            CancellationToken cancellationToken)
    {
        var detected = await WaitForStablePageAsync(
            windowHandle,
            expectedPageIds,
            timeout,
            cancellationToken);
        if (detected is not null || !enableUnknownPageRecovery)
        {
            return detected;
        }

        // A known but unexpected page is already a valid state-machine node.
        // Do not press Escape in that case; return it and let the caller route
        // through the matching step.
        detected = await WaitForStablePageAsync(
            windowHandle,
            expectedPageIds: null,
            TimeSpan.FromSeconds(1),
            cancellationToken);
        if (detected is not null)
        {
            return detected;
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Publish(
                CurrencyWarsNavigationState.WaitingForPage,
                null,
                $"连续未识别到已知页面，正在执行第 {attempt}/3 次 Esc 恢复。");
            var recovery = await unknownPageRecovery.RecoverAsync(
                windowHandle,
                cancellationToken);
            if (!recovery.Succeeded)
            {
                return null;
            }

            detected = await WaitForStablePageAsync(
                windowHandle,
                expectedPageIds: null,
                TimeSpan.FromSeconds(5),
                cancellationToken);
            if (detected is not null)
            {
                Publish(
                    CurrencyWarsNavigationState.WaitingForPage,
                    detected.PageId,
                    $"Esc 后重新识别到：{detected.DisplayName} " +
                    $"（{detected.Confidence:P1}）。");
                return detected;
            }
        }

        return null;
    }

    private void Publish(
        CurrencyWarsNavigationState state,
        string? pageId,
        string message,
        TaskEventLevel level = TaskEventLevel.Information)
    {
        eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            level,
            state.ToString(),
            message));
        ProgressChanged?.Invoke(
            this,
            new CurrencyWarsNavigationProgress(state, pageId, message));
    }

    private void PublishFallback(string code, string message)
    {
        eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            TaskEventLevel.Warning,
            code,
            message));
        ProgressChanged?.Invoke(
            this,
            new CurrencyWarsNavigationProgress(
                CurrencyWarsNavigationState.WaitingForPage,
                code,
                message));
    }

    private CurrencyWarsNavigationResult Result(
        CurrencyWarsNavigationState state,
        string? pageId,
        string message)
    {
        Publish(state, pageId, message);
        return new CurrencyWarsNavigationResult(state, pageId, message)
        {
            EnemyOverview = _enemyOverview,
            InvestmentEnvironments = _investmentEnvironments,
            SelectedInvestmentEnvironmentId =
                _selectedInvestmentEnvironmentId
        };
    }
}

