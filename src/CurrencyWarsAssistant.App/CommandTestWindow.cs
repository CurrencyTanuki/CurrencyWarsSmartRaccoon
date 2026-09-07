using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.App;

/// <summary>
/// 指令测试台（--command-test 启动，特殊测试包专用）：只含「接收指令→执行→回显」一个功能，
/// 不含任何自动决策——决策层由远程 AI 代管，经文件通道下发指令。
/// <para>
/// 文件通道（均在 exe 同目录）：
/// - 指令测试-command.txt：AI 写入，一行一条指令；本窗口轮询读到后立即删除再执行（防重复）。
/// - 指令测试-result.txt：同步等待协议回执——每条指令<strong>接收时</strong>先写
///   「{指令} ⇢ 已接收，执行中…」，<strong>执行完毕</strong>再写终态行「{指令} ⇒ OK/失败：摘要」。
///   远程 AI 轮询结果文件、等到该指令的「⇒」终态行即视为执行完毕，期间短间隔轮询、
///   不设固定长休眠（2026-09-02 用户裁定）。
/// </para>
/// <para>
/// 指令语法（首 token 为指令号，其余按指令给语义参数）：
///   I1~I10                     识别（不带参数）
///   A1 角色名 前台|后台          部署上场
///   A4 前台|后台 槽位号          星徽装配（位置语义，拖物品栏星徽到该槽位角色）
///   A3 角色名                   出售备战席角色
///   A6 / A9                    买经验 / 弃局回主页
///   A10 id1,id2,...            按策略 ID 优先集选投资策略
///   M1 备战页ID 落地页ID        打完一场战斗推进到落地页
///   M3 / M4 / M5               祈愿应答 / 开聘用书 / 商店 Pass（M5 圣杯=1-3 N14 循环语义）
///   M7 [id1,id2,...]           选投资策略（缺省=写死优先级）
///   M8                         刷到命中→选中进局→1-1 备战席立刻停
///   STATUS                     查看状态（游戏窗口/识别会话/最新帧/目标模式）
///   START / STOP               启动 / 停止识别会话（实时采集）
///   GOAL 单人|全员              设置目标模式（影响 M3/M4 的决策语义）
/// 1.2.99 最终交付剥离：KEY/CLICK/SCREENSHOT 三条 AI 诊断指令已移除（用户令）。
/// 尚无底层实现的指令（A2/A4/A5/A7/A11/A12/A13/A14、M2/M6）返回失败事实并注明缺口。
/// </para>
/// </summary>
public sealed class CommandTestWindow : Window
{
    private static readonly string CommandFilePath =
        Path.Combine(AppContext.BaseDirectory, "指令测试-command.txt");
    private static readonly string ResultFilePath =
        Path.Combine(AppContext.BaseDirectory, "指令测试-result.txt");
    private static readonly string AbortFilePath =
        Path.Combine(AppContext.BaseDirectory, "指令测试-abort.txt");

    private readonly GrailRunStateHolder _stateHolder = new();
    private readonly PreparationBoardController _preparationBoard;
    private readonly RewardStageAutomationController _rewardStage;
    private readonly GrailRecognitionListener _listener;
    private readonly GrailOperationExecutor _executor;
    private readonly GrailCommandDispatcher _dispatcher;
    private readonly IInputController _input;
    private readonly GrailFlightRecorder _flightRecorder = new();
    private readonly IPhase2LiveCollectionService _collectionService;
    private readonly IGameWindowService _gameWindowService;
    private readonly GameDataCatalog _gameData;
    private readonly DispatcherTimer _timer = null!;
    private readonly TextBox _logBox = new()
    {
        IsReadOnly = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly TextBlock _statusText = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(8),
    };

    private CancellationTokenSource? _collectionCts;
    private Task? _collectionTask;
    private GrailUserGoal _goal = GrailUserGoal.Single;
    private bool _busy;
    private CancellationTokenSource? _activeCommandCts;
    /// <summary>结果文件写锁：急停监听线程与 UI 线程可能并发回写，防止交错/冲突丢行。</summary>
    private readonly object _resultFileLock = new();
    private bool _collectionMessageSubscribed;
    /// <summary>果断弃局看门狗（用户拍板 2026-09-03：操作不动就果断退出）——
    /// 同一指令连续失败计数；≥2 且页面不在健康备战态 → 自动弃局，绝不挂死。</summary>
    private string? _lastFailureKey;
    private DateTimeOffset _lastFailureAt = DateTimeOffset.MinValue;
    private int _sameFailureStreak;
    private string? _currentCommandKind;
    /// <summary>识别流自动复活（2026-09-04 用户提速令）：会话死亡/帧冻结 ≥45 秒时
    /// 自动 STOP/START 重连——识别流反复死亡是实测最大的隐性耗时源。节流 300 秒防会话重叠。</summary>
    private DateTimeOffset _lastStreamReviveAt = DateTimeOffset.MinValue;
    // 1.2.118 焦点豁免：最近一次检测到游戏不在前台的时刻（切回前台恢复缓冲的锚点）。
    private DateTimeOffset _lastNonForegroundAt = DateTimeOffset.MinValue;
    private bool _streamReviveInProgress;
    /// <summary>用户显式 STOP 标志（审查 90a8a0f4 P1）：置位后自动复活绝不拉起，START 时清除。</summary>
    private bool _streamStopRequested;
    /// <summary>决策层引擎（2026-09-04 用户令：决策层软件化）——DECIDE 启动/停止。</summary>
    private GrailDecisionEngine? _decisionEngine;
    private CancellationTokenSource? _decisionCts;
    private Task? _decisionTask;

    public CommandTestWindow(
        GameDataCatalog gameData,
        PreparationBoardController preparationBoard,
        RewardStageAutomationController rewardStage,
        WishTrialSelectionAutomation trialSelection,
        TrialRecruitSelectionAutomation trialRecruit,
        IRunAbandoner runAbandoner,
        IPhase2LiveCollectionService collectionService,
        IGameWindowService gameWindowService,
        OpeningRerollLoopCoordinator openingCoordinator,
        IInputController inputController,
        IGameCapture gameCapture)
    {
        _preparationBoard = preparationBoard;
        _rewardStage = rewardStage;
        _collectionService = collectionService;
        _gameWindowService = gameWindowService;
        _gameData = gameData;
        _input = inputController;
        _listener = new GrailRecognitionListener(collectionService);
        _executor = new GrailOperationExecutor(
            rewardStage, preparationBoard, trialSelection, trialRecruit, _stateHolder, gameData);
        var recognition = new GrailRecognitionCommands(
            _listener, _stateHolder, gameData, preparationBoard, rewardStage, trialSelection,
            gameCapture, gameWindowService);
        var operation = new GrailOperationCommands(
            _executor, rewardStage, preparationBoard, runAbandoner,
            runAbandoner as IGalaBondPopupHandler);
        var macro = new GrailMacroCommands(
            _executor, rewardStage, _stateHolder, _listener, openingCoordinator, runAbandoner);
        _dispatcher = new GrailCommandDispatcher(recognition, operation, macro);
        _recordingCapture = gameCapture;

        Title = "指令测试台（决策层由 AI 代管）";
        Width = 720;
        Height = 520;

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };
        buttonPanel.Children.Add(MakeButton("启动识别会话", () => _ = ExecuteLineAsync("START")));
        buttonPanel.Children.Add(MakeButton("停止识别会话", () => _ = ExecuteLineAsync("STOP")));
        buttonPanel.Children.Add(MakeButton("状态", () => _ = ExecuteLineAsync("STATUS")));

        var root = new DockPanel();
        DockPanel.SetDock(buttonPanel, Dock.Top);
        root.Children.Add(buttonPanel);
        DockPanel.SetDock(_statusText, Dock.Top);
        root.Children.Add(_statusText);
        root.Children.Add(_logBox);
        Content = root;

        Loaded += (_, _) =>
        {
            QuarantineLeftoverCommandFile();
            AppendLog(
                $"指令文件：{CommandFilePath}{Environment.NewLine}结果文件：{ResultFilePath}{Environment.NewLine}" +
                "把指令写进指令文件即可（一行一条）。先「启动识别会话」再发识别类指令。");
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            // 关窗必须同时中断执行中的指令（如 M8 的整局循环）与识别会话——
            // 2026-09-02 事故：只停识别会话时，协调器任务在关窗后仍持续
            // 自愈弃局/重开导航一个多小时（事件日志 01:20~02:05 可证）。
            _activeCommandCts?.Cancel();
            _decisionCts?.Cancel();
            _collectionCts?.Cancel();
            _listener.Unsubscribe();
            if (_collectionMessageSubscribed)
            {
                _collectionService.Updated -= OnCollectionMessage;
                _collectionMessageSubscribed = false;
            }
        };

        QuarantineLeftoverCommandFile();
        DeleteStaleExitFileAtStartup();
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _timer.Tick += async (_, _) => await PollCommandFileAsync();
        _timer.Start();
        _ = Task.Run(AbortWatcherLoopAsync);
    }

    /// <summary>
    /// 启动残留指令隔离（2026-09-02 事故修复）：轮询器开窗 700ms 后就会读取并执行
    /// 指令文件——上次会话残留的指令（如 M8）会让软件在无人下令的情况下立即操作
    /// 游戏。因此窗口加载时若发现指令文件已存在，一律改名为 .残留.bak 隔离，
    /// 绝不执行；需要执行请重新写入新指令。
    /// </summary>
    private void QuarantineLeftoverCommandFile()
    {
        try
        {
            if (!File.Exists(CommandFilePath))
            {
                return;
            }

            var quarantinePath = $"{CommandFilePath}.{DateTimeOffset.Now:yyyyMMdd-HHmmss}.残留.bak";
            File.Move(CommandFilePath, quarantinePath);
            AppendLog(
                $"⚠ 检测到启动前残留的指令文件，已隔离为 {Path.GetFileName(quarantinePath)}，不会执行其中任何指令。");
            AppendResult("启动隔离", ok: true, summary: $"残留指令文件已改名为 {Path.GetFileName(quarantinePath)}，未执行");
        }
        catch (Exception)
        {
            // 改名失败（占用/权限等）时保守起见删除，宁可丢弃残留指令也绝不执行；
            // 隔离本身绝不能把异常抛回构造函数导致窗口无法打开。
            try
            {
                File.Delete(CommandFilePath);
                AppendLog("⚠ 检测到启动前残留的指令文件且无法隔离，已直接删除，不会执行其中任何指令。");
                AppendResult("启动隔离", ok: true, summary: "残留指令文件无法改名，已删除，未执行");
            }
            catch (Exception)
            {
                // 删除也失败：轮询器仍会读到，只能记录并警示（极端占用场景）。
                AppendLog("⚠ 检测到启动前残留的指令文件，且隔离/删除均失败——请人工检查指令文件。");
            }
        }
    }

    /// <summary>
    /// 启动 exit.txt 卫生（1.2.87）：exit.txt 是"停已运行实例"通道，消费方（运行中的
    /// 实例）会自删后关窗。启动时若它仍存在=写它的时候没有实例在跑（停了个空），
    /// 留着会让本实例首个轮询 tick 误自退——这正是"后开实例秒退"问题的根源之一。
    /// 单实例互斥（1.2.87）保证此刻无其他实例在跑，删除是安全的。
    /// </summary>
    private void DeleteStaleExitFileAtStartup()
    {
        try
        {
            var exitPath = Path.Combine(AppContext.BaseDirectory, "指令测试-exit.txt");
            if (File.Exists(exitPath))
            {
                File.Delete(exitPath);
                AppendLog("启动时发现残留的 exit.txt（无实例消费过），已删除以防误自退。");
            }
        }
        catch (Exception)
        {
            // 卫生清理失败不阻断启动；若确是运行中的停机指令，下一轮询 tick 仍会正常消费。
        }
    }

    /// <summary>
    /// 急停通道（独立于 _busy 轮询，任何时刻可用）：写入 指令测试-abort.txt 即中断
    /// 当前执行中的指令（M8 卡住时的软件内解法，无需关闭窗口）。
    /// </summary>
    private async Task AbortWatcherLoopAsync()
    {
        while (true)
        {
            await Task.Delay(500);
            try
            {
                if (!File.Exists(AbortFilePath))
                {
                    continue;
                }

                File.Delete(AbortFilePath);
                var cts = _activeCommandCts;
                if (cts is null)
                {
                    AppendResult("ABORT", ok: true, summary: "当前没有执行中的指令");
                    continue;
                }

                AppendLog("收到急停：正在中断当前指令…");
                AppendResult("ABORT", ok: true, summary: "已请求中断当前指令");
                // 1.2.87 增量审查 P2：abort 恰落在指令收尾窗口时，命令侧已把 CTS
                // 置空并 Dispose（using var）——此时 Cancel 抛 ObjectDisposedException
                // 只是"指令恰好已结束"，绝不能让它病死监听循环（死后所有后续急停
                // 会被静默吞掉、abort 文件已消费且无回执）。引用已换新=指令已结束。
                if (!ReferenceEquals(_activeCommandCts, cts))
                {
                    AppendResult("ABORT", ok: true, summary: "指令已在此前结束，无需中断");
                    continue;
                }

                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    AppendResult("ABORT", ok: true, summary: "指令已在此前结束，无需中断");
                }
            }
            catch (IOException)
            {
                // 文件被写入方占用时下轮再取
            }
            catch (Exception exception)
            {
                // 1.2.87 增量审查 P2：急停通道兜底——任何未预期异常都要留可见痕迹
                // 并继续轮询，监听循环绝不允许无声死亡（本任务无观察者）。
                try
                {
                    AppendResult("ABORT", ok: false, summary: $"急停监听异常（下轮继续）：{exception.Message}");
                }
                catch
                {
                    // 回执也写失败时仍继续轮询，保住通道活性。
                }
            }
        }
    }

    private Button MakeButton(string text, Action onClick)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
        button.Click += (_, _) => onClick();
        return button;
    }

    // ---- 指令轮询与执行 ----

    /// <summary>
    /// 识别流自动复活（审查 90a8a0f4 修复版）：会话死亡→直接重启；会话在跑但 ≥45 秒
    /// 无流活动→STOP/START 重连。节流 300 秒。约束：
    /// ①显式 STOP 后绝不复活（W1：_streamStopRequested）；
    /// ②决策层运行期间照常复活（引擎 I10 依赖识别流）但节流内不重叠；
    /// ③capture 为单例共享会话，revive 是重启同一会话而非制造第二会话；
    /// ④失焦豁免（1.2.118）：分析自动暂停=等待不是卡死，失焦期 stale 不触发。
    /// 判据 1.2.118 修订：LatestAnalysis.AsOf 帧龄→流活动脉冲（弹框抑制期假性
    /// 帧龄不再误报，审计 #5）。
    /// </summary>
    private void CheckStreamHealth()
    {
        if (_streamStopRequested
            || _streamReviveInProgress
            || (DateTimeOffset.Now - _lastStreamReviveAt).TotalSeconds < 300)
        {
            return;
        }

        // M8/M1 执行期间不复活（审查 90a8a0f4 P1：豁免不得被遗留失败键废掉）：
        // 长指令导航有自己的捕获，重启识别会话只会制造帧抖动。
        if (_busy && _currentCommandKind is "M8" or "M1")
        {
            return;
        }

        var lastPulseAt = _listener.LastUpdateAt ?? _listener.LatestAnalysis?.Snapshot.AsOf;
        var stale = lastPulseAt is null
            || DateTimeOffset.Now - lastPulseAt.Value > TimeSpan.FromSeconds(45);
        // 焦点豁免（1.2.118）：失焦=分析自动暂停（设计行为，等待不是卡死）——
        // 暂停期脉冲停走是预期，stale 不成立；dead（采集任务退出）不受豁免。
        if (stale && !IsGameForegroundWithResumeGrace())
        {
            stale = false;
        }
        var dead = _collectionTask is null || _collectionTask.IsCompleted;
        if (!dead && !stale)
        {
            return;
        }

        _streamReviveInProgress = true;
        _lastStreamReviveAt = DateTimeOffset.Now; // 节流戳在触发点置位（09:1x 实测：漏置位=复活热循环）
        var reason = dead ? "识别流已死亡" : "识别流冻结（45 秒无流活动）";
        AppendLog($"⚠ {reason}，自动重启识别会话。诊断: {FormatStreamDiagnostics()}");
        _ = Task.Run(RunStreamReviveCoreAsync);
    }

    /// <summary>
    /// 冻结判定的焦点豁免（1.2.118，用户令「焦点判断逻辑得搞好」）：返回 false=豁免
    /// （不判冻结），true=游戏在前台且已过恢复缓冲、脉冲判据有效。两种豁免场景：
    /// ①失焦期=分析自动暂停（设计行为，「等待不是卡死」，GrailRunLoop 帧停流同款
    /// 口径）——M8 在途中途失焦/前台 4 号位推理期失焦都不得触发 revive；
    /// ②切回前台后的恢复缓冲（60s，覆盖管线预热窗 20-40s，审计 #4）——若无缓冲，
    /// 「把游戏切回前台」这个动作本身会立即触发幻影重启（失焦期脉冲停走、300s 节流
    /// 已过、切回第一 tick 即判冻结）。缓冲走完脉冲仍停=切回后管线真没起来，照报。
    /// 窗口找不到/检测异常=按失焦豁免（周期重试，漏报一轮无害；误报 revive 才有害；
    /// 游戏被关闭由 dead 判据兜底）。DateTimeOffset 跨线程裸写与 _lastStreamReviveAt
    /// 同口径（最坏=一次错误豁免，下轮自愈）。
    /// </summary>
    private bool IsGameForegroundWithResumeGrace()
    {
        try
        {
            var window = FindGameWindow();
            if (window is not null && _gameWindowService.IsForeground(window))
            {
                return DateTimeOffset.Now - _lastNonForegroundAt >= TimeSpan.FromSeconds(60);
            }
        }
        catch
        {
            // 检测失败走失焦豁免路径
        }

        _lastNonForegroundAt = DateTimeOffset.Now;
        return false;
    }

    /// <summary>
    /// 引擎侧流活性探测（1.2.89，M8 在途看门狗用）：判据 1.2.118 修订（审计 #5）——
    /// 原口径 LatestAnalysis.AsOf 帧龄被坑50 弹框抑制冻结（弹框在屏期 LatestAnalysis
    /// 停在弹框前旧帧），导致弹框期必然误报冻结（通宵 6 次幻影重启根因）。现口径=
    /// 流活动脉冲（LastUpdateAt，任何 Updated 事件含心跳/弹框帧都刷新），阈值 30s
    /// ——事件流停走即真挂死（服务层帧流看门狗 60s 兜底的同款死亡），引擎据此请求
    /// 救援重启。焦点豁免见 IsGameForegroundWithResumeGrace。
    /// 可从引擎后台线程调用：Win32 窗口枚举（毫秒级），无 Dispatcher 依赖。
    /// </summary>
    private bool IsEngineStreamStale()
    {
        if (!IsGameForegroundWithResumeGrace())
        {
            return false;
        }

        var lastPulseAt = _listener.LastUpdateAt ?? _listener.LatestAnalysis?.Snapshot.AsOf;
        return lastPulseAt is null
            || DateTimeOffset.Now - lastPulseAt.Value > TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// 识别流复活核心（1.2.88 自 CheckStreamHealth 提取）：停旧采集任务→重启同一会话。
    /// 启发式路径（CheckStreamHealth）与决策层救援路径（ForceStreamRevive）共用；
    /// 调用方负责置位 _streamReviveInProgress，本方法 finally 释放。
    /// </summary>
    private async Task RunStreamReviveCoreAsync()
    {
        try
        {
            var running = _collectionTask;
            if (running is not null)
            {
                _collectionCts?.Cancel();
                _listener.Unsubscribe();
                if (_collectionMessageSubscribed)
                {
                    _collectionService.Updated -= OnCollectionMessage;
                    _collectionMessageSubscribed = false;
                }

                try { await running.WaitAsync(TimeSpan.FromSeconds(30)); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }

                // W2 修复：只清自己看到的旧引用——期间若手动 START 已挂新任务，绝不清掉。
                if (ReferenceEquals(_collectionTask, running))
                {
                    _collectionTask = null;
                }

                await Task.Delay(1500);
            }

            if (_streamStopRequested)
            {
                return; // 复活过程中用户显式 STOP：尊重，不再拉起
            }

            if (_collectionTask is null || _collectionTask.IsCompleted)
            {
                BeginInvokeIfAlive(() => StartCollectionAsync());
            }
        }
        finally
        {
            _streamReviveInProgress = false;
        }
    }

    /// <summary>
    /// 决策层发起的识别流救援重启（1.2.88，命中局实锤：长尾 60s &lt; 启发式节流 300s，
    /// 好局在等待自愈时被弃）。引擎在追帧长尾入口经委托调用本方法，立即 STOP/START
    /// 识别会话（蓝图 X3 药方），绕过 300s 启发式节流但共享单飞标志与显式 STOP 尊重。
    /// 活性前置（审查 P3，1.2.118 判据修订）：仅当游戏在前台且流活动停走 &gt;20s
    /// （真冻结）才重启——弹框抑制期的假性帧龄（审计 #5 误报根因）与失焦暂停期
    /// （设计行为，2026-09-07 用户令）都不触发，对齐启发式
    /// "不必要重启只制造帧抖动"原则。
    /// 可从引擎后台线程调用：经 BeginInvokeIfAlive 摊回 UI 线程，核心在 Task.Run 执行。
    /// </summary>
    private void ForceStreamRevive(string reason)
    {
        if (_streamReviveInProgress || _streamStopRequested)
        {
            return;
        }

        BeginInvokeIfAlive(() =>
        {
            // 活性前置（审查 P3；1.2.118 判据修订）：仅当游戏在前台（含恢复缓冲，
            // 见 IsGameForegroundWithResumeGrace）且流活动停走 >20s（真冻结）才重启
            // ——弹框抑制期的假性帧龄（审计 #5）与失焦暂停/切回恢复期（设计行为）
            // 都不是重启理由；失焦期 revive 毫无意义（重启后照样暂停，还在 M8 在途
            // 制造帧抖动）。健康流遇到持续页面门禁拒绝（事件在流动）不重启。
            if (!IsGameForegroundWithResumeGrace()
                || _streamReviveInProgress
                || _streamStopRequested
                || _collectionCts is null)
            {
                return;
            }

            var lastPulseAt = _listener.LastUpdateAt ?? _listener.LatestAnalysis?.Snapshot.AsOf;
            var stale = lastPulseAt is null
                || DateTimeOffset.Now - lastPulseAt.Value > TimeSpan.FromSeconds(20);
            if (!stale)
            {
                // P3-6（审查 1.2.118）：拒绝必须留痕——审计 #5 的"回执 OK 但没重启"
                // 排查困局即源于静默 return。此处触发频率低（引擎仅脉冲停走时才请求），
                // 不会刷屏；外层焦点豁免/单飞拒绝保持静默（失焦期高频，日志会刷屏）。
                AppendLog("决策层救援请求被活性前置拒绝（脉冲新鲜，非真冻结）——不重启。");
                return;
            }

            _streamReviveInProgress = true;
            _lastStreamReviveAt = DateTimeOffset.Now;
            AppendLog($"⚠ 决策层请求重启识别会话（{reason} 救援，绕过 300s 节流）。诊断: {FormatStreamDiagnostics()}");
            _ = Task.Run(RunStreamReviveCoreAsync);
        });
    }

    private async Task PollCommandFileAsync()
    {
        CheckStreamHealth();
        if (_busy)
        {
            return;
        }

        // 遥控自退通道（2026-09-02 用户授权最大自主权）：写入 指令测试-exit.txt 即干净关窗。
        // 用途=版本更替：AI 关旧窗→覆盖文件→经计划任务免 UAC 拉起新版，全程无需用户点 ×。
        // 仅空闲时消费（忙时先 abort 再 exit），避免打断执行中的任务。
        try
        {
            var exitPath = Path.Combine(AppContext.BaseDirectory, "指令测试-exit.txt");
            if (File.Exists(exitPath))
            {
                File.Delete(exitPath);
                AppendLog("收到遥控退出指令，窗口即将关闭（供 AI 覆盖新版本后经计划任务重新拉起）。");
                _timer?.Stop();
                Application.Current.Shutdown();
                return;
            }
        }
        catch
        {
            // 自退检测失败不影响正常轮询
        }

        string[] lines;
        try
        {
            if (!File.Exists(CommandFilePath))
            {
                RefreshStatus();
                return;
            }

            lines = File.ReadAllLines(CommandFilePath);
            File.Delete(CommandFilePath);
        }
        catch (IOException)
        {
            return; // 写入方尚未写完，下个 tick 再取
        }

        _busy = true;
        try
        {
            foreach (var line in lines)
            {
                await ExecuteLineAsync(line);
            }
        }
        finally
        {
            _busy = false;
            _currentCommandKind = null;
            RefreshStatus();
        }
    }

    // 1.2.99 最终交付剥离（用户 2026-09-05 令）：KEY/CLICK/SCREENSHOT 三条 AI 诊断指令
    // 已从交付版移除——原始输入/截屏通道对最终用户无用且增大攻击面；语义指令集
    // （I/A/M+STATUS/START/STOP/GOAL/DECIDE）不受影响。需要取证时用识别流帧（START+I1）。
    // （_input 注入=有意保留：DI 构造签名稳定，App.xaml.cs 的注册为产品在用。）

    private async Task ExecuteLineAsync(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            return;
        }

        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _currentCommandKind = tokens[0].ToUpperInvariant();
        AppendLog($"▶ {line}");
        try
        {
            switch (tokens[0].ToUpperInvariant())
            {
                case "STATUS":
                    AppendLog(BuildStatusText());
                    AppendResult("STATUS", ok: true, summary: BuildStatusText());
                    return;
                case "START":
                    StartCollectionAsync();
                    return;
                case "STOP":
                    await StopCollectionAsync();
                    return;
                case "GOAL":
                    SetGoal(tokens);
                    return;
                case "DECIDE":
                    HandleDecide(tokens);
                    return;
            }

            var flightStopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (TryParseCommand(tokens, out var command, out var parseError) is false)
            {
                AppendLog($"✗ 解析失败：{parseError}");
                AppendResult(line, ok: false, summary: parseError ?? "解析失败");
                _flightRecorder.Record(line, ok: false, parseError ?? "解析失败", 0, "parse_error");
                _sameFailureStreak = 0;
                _lastFailureKey = null;
                _lastFailureAt = DateTimeOffset.MinValue;
                return;
            }

            var window = FindGameWindow();
            if (window is null)
            {
                AppendLog("✗ 未找到可自动化的游戏窗口。");
                AppendResult(line, ok: false, summary: "未找到可自动化的游戏窗口");
                _flightRecorder.Record(line, ok: false, "未找到可自动化的游戏窗口", 0, "no_window");
                _sameFailureStreak = 0;
                _lastFailureKey = null;
                _lastFailureAt = DateTimeOffset.MinValue;
                return;
            }

            // 审查 90a8a0f4 P1：决策层运行期间拒绝游戏操作类指令（防并发互抢窗口/计数器）。
            if (_decisionTask is { IsCompleted: false }
                && tokens[0].ToUpperInvariant() is not ("STATUS" or "DECIDE" or "START" or "STOP" or "GOAL"))
            {
                AppendResult(line, ok: false, summary: "决策层运行中，拒绝并发指令（先 DECIDE 停止）");
                _flightRecorder.Record(line, ok: false, "决策层运行中拒绝并发指令", 0, "rejected");
                return;
            }

            var context = new GrailCommandContext(window.Handle, "preparation_generic", _goal);

            // 同步等待协议（2026-09-02 用户裁定）：接收即回执，远程 AI 轮询结果文件
            // 等到该指令的「⇒」终态行才算执行完——禁止 AI 固定长休眠呆等。
            AppendReceipt(line);

            // 每条指令挂独立急停令牌：写入 指令测试-abort.txt 即可中断长指令（如 M8）。
            using var commandCts = new CancellationTokenSource();
            _activeCommandCts = commandCts;
            GrailCommandResult result;
            try
            {
                result = await _dispatcher.DispatchAsync(command!, context, commandCts.Token);
            }
            catch (OperationCanceledException)
            {
                AppendResult(line, ok: false, summary: "已急停中断；游戏当前状态用 I1 查看，用下一条指令接续");
                _flightRecorder.Record(
                    line, ok: false, "已急停中断", flightStopwatch.ElapsedMilliseconds, "aborted");
                _sameFailureStreak = 0;
                _lastFailureKey = null;
                _lastFailureAt = DateTimeOffset.MinValue;
                return;
            }
            finally
            {
                _activeCommandCts = null;
            }

            flightStopwatch.Stop();
            var summary = result.Error is null ? FormatPayload(result.Payload) : result.Error;
            AppendResult(line, ok: result.Error is null, summary);
            AppendLog(result.Error is null ? $"✔ OK {summary}" : $"✗ 失败 {result.Error}");
            // 黑匣子（handoff 六.2）：每条指令的下发/结果/耗时旁路落盘，实机验证以日志为准。
            // 先落盘再刷新快照（审查 P3）：刷新失败不得把成功指令污染成 exception。
            _flightRecorder.Record(line, result.Error is null, summary, flightStopwatch.ElapsedMilliseconds);

            // 果断弃局看门狗（用户拍板 2026-09-03：操作不动就果断退出）：同一指令连续失败 ≥2 次
            // 且页面不在健康备战态（识别不到/非备战页/陈旧）→ 自动弃局，防止被识别表外的
            // 阻塞模态或异常状态挂死。页面健康时绝不触发（复位计数）。
            if (result.Error is not null)
            {
                var failureKey = line;
                var sameAsLast = _lastFailureKey == failureKey
                    && DateTimeOffset.Now - _lastFailureAt < TimeSpan.FromSeconds(120);
                _sameFailureStreak = sameAsLast ? _sameFailureStreak + 1 : 1;
                _lastFailureKey = failureKey;
                _lastFailureAt = DateTimeOffset.Now;
                var decisionRunning = _decisionTask is { IsCompleted: false };
                if (_sameFailureStreak >= 2)
                {
                    _sameFailureStreak = 0;
                    // 审查 90a8a0f4 P1：决策层运行期间绝不 AUTO-A9（会弃掉引擎正在打的局）。
                    if (decisionRunning)
                    {
                        AppendLog("⚠ 连续失败但决策层运行中——跳过 AUTO-A9（弃局由决策层自理）。");
                        return;
                    }

                    var analysisNow = _listener.LatestAnalysis;
                    var pageNow = analysisNow?.Snapshot.PageId.Value;
                    var pageFresh = analysisNow?.Snapshot.AsOf is { } at
                        && DateTimeOffset.Now - at <= TimeSpan.FromSeconds(15);
                    var healthyPreparation = pageFresh
                        && pageNow is not null
                        && pageNow.StartsWith("preparation_", StringComparison.OrdinalIgnoreCase);
                    if (!healthyPreparation)
                    {
                        AppendLog("⚠ 连续 2 次同一指令失败且页面不在健康备战态——检测到操作不动，果断弃局退出。");
                        var abandon = await _dispatcher.DispatchAsync(
                            new GrailCommand(GrailCommandKind.A9), context, commandCts.Token);
                        var abandonSummary = abandon.Error is null
                            ? FormatPayload(abandon.Payload)
                            : abandon.Error;
                        AppendResult("AUTO-A9", ok: abandon.Error is null,
                            summary: $"果断弃局（连续失败触发）：{abandonSummary}");
                        _flightRecorder.Record("AUTO-A9", abandon.Error is null,
                            abandonSummary ?? string.Empty, 0, "auto_exit");
                    }
                }
            }
            else
            {
                _sameFailureStreak = 0;
                _lastFailureKey = null;
                _lastFailureAt = DateTimeOffset.MinValue;
            }

            try
            {
                RefreshLatestSnapshot();
            }
            catch
            {
                // 快照刷新失败不影响本条指令的回执与黑匣子（下一条指令前置 I10 会再水合）。
            }
        }
        catch (Exception exception)
        {
            AppendLog($"✗ 异常 {exception.GetType().Name}: {exception.Message}");
            AppendResult(line, ok: false, summary: $"异常 {exception.GetType().Name}: {exception.Message}");
            _flightRecorder.Record(
                line,
                ok: false,
                $"异常 {exception.GetType().Name}: {exception.Message}",
                -1,
                "exception");
        }
    }

    // ---- 指令解析 ----

    private static bool TryParseCommand(string[] tokens, out GrailCommand? command, out string? error)
    {
        command = null;
        error = null;
        if (!Enum.TryParse(tokens[0], ignoreCase: true, out GrailCommandKind kind)
            || !Enum.IsDefined(kind))
        {
            error = $"未知指令 {tokens[0]}。";
            return false;
        }

        switch (kind)
        {
            case GrailCommandKind.A1:
                if (tokens.Length < 2)
                {
                    error = "用法：A1 角色名 前台|后台 [槽位号1基]（槽位可省=按占用序；显式槽位可精确落位/有意互换）。";
                    return false;
                }

                command = new GrailCommand(kind, ParseDeployArgs(tokens, out error));
                return true;
            case GrailCommandKind.A4:
                if (tokens.Length < 3
                    || !int.TryParse(tokens[2], out var a4Slot)
                    || a4Slot < 1
                    || a4Slot > (tokens[1].StartsWith("前", StringComparison.Ordinal) ? 4 : 6))
                {
                    error = "用法：A4 前台|后台 槽位号(前台1-4/后台1-6)——位置语义（角色名形式已废除，2026-09-03 定稿）。";
                    return false;
                }

                command = new GrailCommand(kind, new GrailPositionArgs(
                    tokens[1].StartsWith("前", StringComparison.Ordinal)
                        ? PreparationLane.Front
                        : PreparationLane.Back,
                    a4Slot - 1));
                return true;
            case GrailCommandKind.A2:
                if (tokens.Length < 3
                    || !int.TryParse(tokens[2], out var a2Slot)
                    || a2Slot < 1
                    || a2Slot > (tokens[1].StartsWith("前", StringComparison.Ordinal) ? 4 : 6))
                {
                    error = "用法：A2 前台|后台 槽位号(前台1-4/后台1-6)。";
                    return false;
                }

                command = new GrailCommand(kind, new GrailPositionArgs(
                    tokens[1].StartsWith("前", StringComparison.Ordinal)
                        ? PreparationLane.Front
                        : PreparationLane.Back,
                    a2Slot - 1));
                return true;
            case GrailCommandKind.A3:
                if (tokens.Length >= 2 && int.TryParse(tokens[1], out var a3Slot) && a3Slot is >= 1 and <= 9)
                {
                    command = new GrailCommand(kind, new GrailBenchSlotArgs(a3Slot - 1));
                    return true;
                }

                if (tokens.Length >= 2)
                {
                    command = new GrailCommand(kind, new GrailCharacterArgs(tokens[1]));
                    return true;
                }

                error = "用法：A3 槽位号(1-9) 或 A3 角色名。";
                return false;
            case GrailCommandKind.A5:
                if (tokens.Length < 2)
                {
                    command = new GrailCommand(kind, new GrailCharacterArgs(string.Empty));
                    return true;
                }

                command = new GrailCommand(kind, new GrailCharacterArgs(tokens[1]));
                return true;
            case GrailCommandKind.A10 or GrailCommandKind.M7 when tokens.Length >= 2:
                var ids = tokens[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                command = new GrailCommand(kind, new GrailStrategyArgs(
                    new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase)));
                return true;
            case GrailCommandKind.M5 when tokens.Length >= 2:
                if (!tokens[1].StartsWith("圣", StringComparison.Ordinal))
                {
                    error = "用法：M5 [圣杯]。带'圣杯'=1-3 N14 循环语义（无目标刷新、买到才关店、真实空槽上场）；不带=1-1/1-2 单轮语义。";
                    return false;
                }

                command = new GrailCommand(kind, new GrailShopPassArgs(GrailLoopMode: true));
                return true;
            case GrailCommandKind.M1:
                if (tokens.Length < 3)
                {
                    error = "用法：M1 备战页ID 落地页ID（如 M1 preparation_generic reward_shop）";
                    return false;
                }

                command = new GrailCommand(kind, new GrailBattleArgs(tokens[1], tokens[2]));
                return true;
            case GrailCommandKind.A15:
                // A15 [幸运星|小刀|轮滑鞋|手枪]（缺省=幸运星）
                command = new GrailCommand(kind, new GrailCharacterArgs(tokens.Length >= 2 ? tokens[1] : "幸运星"));
                return true;
            case GrailCommandKind.M8:
                // M8 语义（2026-09-02 用户拍板停靠点）：刷到命中→选中进局→1-1 备战席立刻停。
                command = new GrailCommand(kind);
                return true;
            default:
                command = new GrailCommand(kind);
                return true;
        }
    }

    /// <summary>A1 参数解析：角色名 前台|后台 [槽位号（1 基，前台 1-4/后台 1-6）]。槽位可省=按占用序。</summary>
    private static GrailDeployArgs ParseDeployArgs(string[] tokens, out string? error)
    {
        error = null;
        var lane = tokens.Length >= 3
            && (tokens[2].StartsWith("前", StringComparison.Ordinal)
                || tokens[2].StartsWith("front", StringComparison.OrdinalIgnoreCase))
            ? PreparationLane.Front
            : PreparationLane.Back;
        int? targetSlot = null;
        if (tokens.Length >= 4)
        {
            if (!int.TryParse(tokens[3], out var slotNumber) || slotNumber < 1
                || slotNumber > (lane == PreparationLane.Front ? 4 : 6))
            {
                error = $"槽位号 {tokens[3]} 无效（{(lane == PreparationLane.Front ? "前台 1-4" : "后台 1-6")}）。";
                return new GrailDeployArgs(tokens[1], lane);
            }

            targetSlot = slotNumber - 1;
        }

        return new GrailDeployArgs(tokens[1], lane, targetSlot);
    }

    private void SetGoal(string[] tokens)
    {
        if (tokens.Length >= 2 && tokens[1].StartsWith("全", StringComparison.Ordinal))
        {
            _goal = GrailUserGoal.All;
        }
        else
        {
            _goal = GrailUserGoal.Single;
        }

        _executor.Goal = _goal;
        var goalSummary = $"目标模式={(_goal == GrailUserGoal.All ? "全员" : "单人")}（影响 M3/M4 决策语义）";
        AppendLog(goalSummary + "。");
        AppendResult("GOAL", ok: true, summary: goalSummary);
    }

    /// <summary>DECIDE 开始|停止：启动/停止决策层引擎（整局自主：M8→1-1→1-2→1-3→终局/重开）。</summary>
    private GrailRollingRecorder? _decideRecorder;
    private readonly IGameCapture _recordingCapture;
    private static readonly string DecideRecordingDirectory =
        Path.Combine(AppContext.BaseDirectory, "Recordings");

    private void HandleDecide(string[] tokens)
    {
        var action = tokens.Length >= 2 ? tokens[1] : "开始";
        if (action.StartsWith("停", StringComparison.Ordinal) || action.StartsWith("S", StringComparison.OrdinalIgnoreCase))
        {
            if (_decisionTask is null || _decisionTask.IsCompleted)
            {
                AppendResult("DECIDE", ok: false, summary: "决策层未在运行");
                return;
            }

            _decisionCts?.Cancel();
            AppendResult("DECIDE", ok: true, summary: "已请求停止决策层");
            return;
        }

        if (_decisionTask is not null && !_decisionTask.IsCompleted)
        {
            AppendResult("DECIDE", ok: false, summary: "决策层已在运行（先 DECIDE 停止）");
            return;
        }

        var window = FindGameWindow();
        if (window is null)
        {
            AppendResult("DECIDE", ok: false, summary: "未找到游戏窗口");
            return;
        }

        var handle = window.Handle;
        _decisionCts = new CancellationTokenSource();
        // 1.2.113（用户令 DECIDE 自动录制）：整段决策层录制为单个 MP4（低画质 15fps
        // 省 CPU），ffmpeg 缺失时静默跳过不阻断决策。录毕存 Recordings\decide-*。
        GrailRollingRecorder? recorder = null;
        try
        {
            var ffmpeg = FfmpegLocator.Locate();
            if (ffmpeg.Found)
            {
                recorder = new GrailRollingRecorder(
                    _recordingCapture,
                    window,
                    FateGrailRecordingQuality.FromLevel(FateGrailRecordingQualityLevel.Low),
                    ffmpeg.ExecutablePath!,
                    Path.Combine(Path.GetTempPath(), "GrailRecordingTemp"));
            }
            else
            {
                AppendLog("未找到 ffmpeg——本轮 DECIDE 不录制（仅日志+证据帧监督）。");
            }
        }
        catch
        {
            recorder = null; // 录制装配失败绝不阻断决策
        }

        _decideRecorder = recorder;
        var board = _preparationBoard;
        _decisionEngine = new GrailDecisionEngine(
            _dispatcher, _stateHolder, _executor, _gameData,
            emit: text =>
            {
                AppendLog(text);
                AppendResult("决策层", ok: true, summary: text);
            },
            genericClick: (handle, x, y, token) =>
                board.GrailClickReferencePointAsync(handle, x, y, token),
            pressInteractKey: (handle, token) =>
                board.GrailPressInteractKeyAsync(handle, token),
            retreatFromBattleView: (handle, token) =>
                _rewardStage.RetreatFromBattleViewAsync(handle, token),
            requestStreamRevive: reason =>
                ForceStreamRevive(reason),
            isStreamStale: IsEngineStreamStale);
        var cts = _decisionCts;
        _decisionTask = Task.Run(async () =>
        {
            try
            {
                if (recorder is not null)
                {
                    await recorder.StartAsync(
                        $"decide-{DateTime.Now:yyyyMMdd-HHmmss}", cts.Token);
                }

                await _decisionEngine.RunAsync(handle, _goal, cts.Token);
                AppendResult("DECIDE", ok: true, summary: "决策层已结束");
            }
            catch (OperationCanceledException)
            {
                AppendResult("DECIDE", ok: true, summary: "决策层已停止");
            }
            catch (Exception exception)
            {
                AppendResult("DECIDE", ok: false, summary: $"决策层异常 {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                if (recorder is not null)
                {
                    try
                    {
                        await recorder.FinishAsync(true, DecideRecordingDirectory, CancellationToken.None);
                        AppendLog("▶ DECIDE 录制已保存到 Recordings。");
                    }
                    catch
                    {
                        // 录制收尾失败不影响决策层终态。
                    }

                    recorder.Dispose();
                    _decideRecorder = null;
                }
            }
        });
        AppendLog("▶ 决策层引擎已启动（自主整局）。");
        AppendResult("DECIDE", ok: true, summary: "决策层已启动");
    }

    // ---- 识别会话 ----

    private void StartCollectionAsync()
    {
        if (_collectionTask is not null && !_collectionTask.IsCompleted)
        {
            AppendLog("识别会话已在运行。");
            AppendResult("START", ok: false, summary: "识别会话已在运行");
            return;
        }

        var window = FindGameWindow();
        if (window is null)
        {
            AppendLog("✗ 未找到可自动化的游戏窗口，无法启动识别。");
            AppendResult("START", ok: false, summary: "未找到游戏窗口");
            return;
        }

        _collectionCts = new CancellationTokenSource();
        var runId = $"cmdtest-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        _collectionTask = _collectionService.RunAsync(
            window.Handle,
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
            new LiveCollectionStartOptions(
                runId,
                RunEntryMode.AutomaticReroll,
                DeleteScreenshotsOnCompletion: true),
            _collectionCts.Token);
        _streamStopRequested = false;
        _listener.Subscribe();
        // 识别流的失败/里程碑消息必须可见（2026-09-02 事故：监听器只消费带帧的
        // Updated，"采集失败/看门狗暂停"等纯文本消息全部丢弃，识别流死了没人知道）。
        // 幂等守卫：会话自行死亡后再 START 会重复走这里，重复订阅会导致消息翻倍。
        if (!_collectionMessageSubscribed)
        {
            _collectionService.Updated += OnCollectionMessage;
            _collectionMessageSubscribed = true;
        }
        AppendLog($"识别会话已启动（{runId}）。等首帧约数秒，可发 I1 试读。");
        AppendResult("START", ok: true, summary: runId);
    }

    private void OnCollectionMessage(object? sender, LiveCollectionUpdate update)
    {
        if (string.IsNullOrEmpty(update.Message) || (!update.IsError && !update.IsMilestone))
        {
            return;
        }

        AppendLog($"[识别流] {update.Message}");
        if (update.IsError)
        {
            AppendResult("识别流", ok: false, summary: update.Message);
        }
    }

    private async Task StopCollectionAsync()
    {
        if (_collectionTask is null || _collectionTask.IsCompleted)
        {
            AppendLog("识别会话未在运行。");
            AppendResult("STOP", ok: false, summary: "识别会话未在运行");
            return;
        }

        _streamStopRequested = true;
        _collectionCts!.Cancel();
        _listener.Unsubscribe();
        if (_collectionMessageSubscribed)
        {
            _collectionService.Updated -= OnCollectionMessage;
            _collectionMessageSubscribed = false;
        }
        try
        {
            await _collectionTask;
        }
        catch (OperationCanceledException)
        {
        }

        _collectionTask = null;
        AppendLog("识别会话已停止。");
        AppendResult("STOP", ok: true, summary: "已停止");
    }

    // ---- 状态与回显 ----

    private GameWindowInfo? FindGameWindow() =>
        _gameWindowService.FindCandidates().FirstOrDefault(window => window.IsReadyForAutomation);

    /// <summary>每条指令后用 I10 组装刷新执行器快照（纯计算），让 M5 的同名去重用上最新已购名单。</summary>
    private void RefreshLatestSnapshot()
    {
        if (_collectionTask is null || _collectionTask.IsCompleted)
        {
            return;
        }

        var analysis = _listener.LatestAnalysis;
        if (analysis is null
            || analysis.OperationalState is null
            || analysis.Snapshot is not { } snapshot
            || snapshot.PageId.Value is not { } framePageId
            || !framePageId.StartsWith("preparation_", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var snapshotAssembled = GrailSnapshotAssembler.Assemble(
            analysis.OperationalState,
            snapshot,
            _gameData,
            _stateHolder,
            _goal,
            DateTimeOffset.Now,
            TimeSpan.FromSeconds(15));
        _executor.LatestSnapshot = snapshotAssembled;
    }

    /// <summary>
    /// 1.2.96 诊断（纯观测）：两侧流统计摘要，供冻结病理判读——
    /// 截图循环间隔逐次抬升=渐慢；连续失败突增=断崖；会话重建计数=重启后即死甄别；
    /// WGC 帧计数停滞而循环仍在请求=捕获/游戏侧不产帧。
    /// </summary>
    private string FormatStreamDiagnostics()
    {
        var loop = _collectionService.ActivePipelineLoopStatistics;
        var stream = _collectionService.ActiveCaptureStreamStats;
        if (loop is null && stream is null)
        {
            return "无（识别会话未启动）";
        }

        var parts = new List<string>();
        if (loop is not null)
        {
            parts.Add(
                $"截图循环 成功{loop.Successes}/失败{loop.Failures}/连续失败{loop.ConsecutiveFailures}"
                + $" 间隔 Last/Min/Avg/Max={loop.LastIntervalMs:F0}/{loop.MinIntervalMs:F0}/{loop.AverageIntervalMs:F0}/{loop.MaxIntervalMs:F0}ms"
                + (loop.LastSuccessAt is { } at
                    ? $" 最后成功={at.ToLocalTime():HH:mm:ss}"
                    : string.Empty));
        }

        if (stream is not null)
        {
            parts.Add(
                $"捕获层 帧{stream.FrameArrivals}/成功{stream.CaptureSuccesses}/超时{stream.CaptureTimeouts}"
                + $" 会话{stream.SessionCreations}(重建{stream.SessionRebuilds})"
                + (stream.LastFrameArrivedAt is { } fa
                    ? $" 最后WGC帧={fa.ToLocalTime():HH:mm:ss}"
                    : string.Empty));
        }

        return string.Join("；", parts);
    }

    private string BuildStatusText()
    {
        var window = FindGameWindow();
        var analysis = _listener.LatestAnalysis;
        var collectionRunning = _collectionTask is not null && !_collectionTask.IsCompleted;
        var latestFrame = analysis is null
            ? "无"
            : $"{analysis.Snapshot.PageId.Value} @{analysis.Snapshot.AsOf.ToLocalTime():HH:mm:ss}" +
              (DateTimeOffset.Now - analysis.Snapshot.AsOf > TimeSpan.FromSeconds(15)
                  ? "（⚠ 陈旧）"
                  : string.Empty);
        return $"游戏窗口={(window is null ? "未找到" : $"{window.Title} ({window.Handle})")}；"
            + $"识别会话={(collectionRunning ? "运行中" : "停止")}；"
            + $"最新帧={latestFrame}；"
            + $"祈愿弹框={(_listener.IsWishDialogOpen ? "在屏" : "无")}；"
            + $"目标={(_goal == GrailUserGoal.All ? "全员" : "单人")}；"
            + $"流诊断={FormatStreamDiagnostics()}。";
    }

    private void RefreshStatus() => BeginInvokeIfAlive(() => _statusText.Text = BuildStatusText());

    private void AppendLog(string text)
    {
        BeginInvokeIfAlive(() =>
        {
            _logBox.AppendText($"[{DateTimeOffset.Now:HH:mm:ss}] {text}{Environment.NewLine}");
            _logBox.ScrollToEnd();
        });
    }

    /// <summary>接收回执（⇢ 行）：指令已被取走并开始执行；终态另有「⇒」行。</summary>
    private void AppendReceipt(string command)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss}] {command} ⇢ 已接收，执行中…{Environment.NewLine}";
        try
        {
            lock (_resultFileLock)
            {
                File.AppendAllText(ResultFilePath, line);
            }
        }
        catch (Exception)
        {
            // 结果文件被远端读取占用/权限问题时丢弃本次回写（终态行会再写）。
        }

        BeginInvokeIfAlive(() =>
        {
            _logBox.AppendText(line);
            _logBox.ScrollToEnd();
        });
    }

    private void AppendResult(string command, bool ok, string summary)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss}] {command} ⇒ {(ok ? "OK" : "失败")}：{summary}{Environment.NewLine}";
        try
        {
            lock (_resultFileLock)
            {
                File.AppendAllText(ResultFilePath, line);
            }
        }
        catch (Exception)
        {
            // 结果文件被远端读取占用/权限问题时丢弃本次回写（下一条指令会再写）；
            // 这里绝不能再抛——AppendResult 也可能运行在异常兜底 catch 内。
        }

        BeginInvokeIfAlive(() =>
        {
            _logBox.AppendText(line);
            _logBox.ScrollToEnd();
        });
    }

    /// <summary>
    /// 关窗取消执行中的指令后，收尾回显可能落在 Dispatcher 已关停之后——
    /// 此时 BeginInvoke 会抛 TaskCanceledException 并把取消本身变成"异常"，
    /// 必须吞掉（文件结果已在 AppendResult 里先行落盘）。
    /// </summary>
    private void BeginInvokeIfAlive(Action action)
    {
        try
        {
            Dispatcher.BeginInvoke(action);
        }
        catch (TaskCanceledException)
        {
        }
    }

    private static string FormatPayload(object? payload) => payload switch
    {
        null => string.Empty,
        GrailPageFact page => page.CapturedAt is null
            ? $"页面={page.PageId ?? "未知"} 祈愿弹框={(page.WishDialogOpen ? "在屏" : "无")}（⚠ 无帧时间）"
            : page.IsStale
                ? $"页面={page.PageId ?? "未知"}（⚠ 陈旧帧 {page.CapturedAt.Value.ToLocalTime():HH:mm:ss}，已 {(DateTimeOffset.Now - page.CapturedAt.Value).TotalSeconds:F0} 秒无新帧，识别流疑冻结，勿当现状）"
                : $"页面={page.PageId ?? "未知"} 祈愿弹框={(page.WishDialogOpen ? "在屏" : "无")}（帧龄 {(DateTimeOffset.Now - page.CapturedAt.Value).TotalSeconds:F0} 秒）",
        IReadOnlyList<GrailCharacterFact> characters when characters.Count > 0 =>
            string.Join("、", characters.Select(item =>
                $"{item.Name ?? "?"}({item.Cost?.ToString() ?? "?"}费){item.Slot}")),
        IReadOnlyList<GrailCharacterFact> => "（空）",
        GrailMeterFact meter => $"数值={meter.Value?.ToString() ?? "未知"}（缓存时间 {meter.CapturedAt:HH:mm:ss}）",
        GrailBadgeFact badge => $"星徽 总计={badge.TotalObtained} 未携带={badge.Uncarried}",
        GrailTierFact tier => $"羁绊计数={tier.BondMemberCount} 成员=[{string.Join("、", tier.DeployedBondMembers)}]",
        WishTrialSelectionInfo wish => $"左={wish.LeftName ?? "?"}/{wish.LeftReward ?? "?"} 右={wish.RightName ?? "?"}/{wish.RightReward ?? "?"}",
        GrailWishOutcomeFact wishOutcome => $"已应答={wishOutcome.Responded} 累计祈愿={wishOutcome.WishesResponded} 书=得{wishOutcome.LettersObtained}/开{wishOutcome.LettersOpened} 奇迹={wishOutcome.MiracleSelected} 釜={wishOutcome.CauldronSelected}",
        GrailLettersFact letters => $"聘用书 得={letters.Obtained} 已开={letters.Opened}",
        GrailShopPassFact shop => $"买到={(shop.BoughtCharacterNames is { Count: > 0 } ? string.Join("、", shop.BoughtCharacterNames) : "无")} 金币={shop.GoldAfter?.ToString() ?? "未知"}"
            + (shop.ShelfCharacterNames is { Count: > 0 }
                ? $" 货架=[{string.Join("、", shop.ShelfCharacterNames)}]"
                : string.Empty),
        GrailOpeningFact opening => $"成功={opening.Succeeded} 命中环境={opening.MatchedEnvironmentName ?? "—"}：{opening.Message}",
        GrailCharacterFact character => $"{character.Name}[{character.Slot}]（{character.Cost?.ToString() ?? "?"}费）",
        RewardStageAutomationResult stage => $"{stage.Status}：{stage.Message}",
        RejectedOpeningRecoveryResult recovery => recovery.ToString(),
        bool flag => flag ? "是" : "否",
        string text => text,
        GrailRunSnapshot snapshot =>
            $"快照：血={snapshot.TeamHealth?.ToString() ?? "?"} 金={snapshot.Gold} 人口={snapshot.Population} "
            + $"羁绊={snapshot.BondMemberCount} 祈愿={snapshot.WishesResponded} 已购=[{string.Join("、", snapshot.OwnedCharacterNames)}]"
            + $" 场上=[{string.Join("、", snapshot.DeployedCharacterDetails)}]"
            + $" 备战席=[{string.Join("、", snapshot.BenchCharacterDetails)}]"
            + (string.IsNullOrEmpty(snapshot.AnomalyNotes) ? "" : $" ⚠{snapshot.AnomalyNotes.Trim()}"),
        _ => payload.ToString() ?? string.Empty,
    };
}
