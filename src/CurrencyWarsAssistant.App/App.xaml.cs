using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using CurrencyWarsAssistant.Workflow;
using Microsoft.Extensions.DependencyInjection;

namespace CurrencyWarsAssistant.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName =
        @"Local\CurrencyWarsSmartRaccoon.App.0C912196";
    private const string SingleInstanceActivationName =
        @"Local\CurrencyWarsSmartRaccoon.Activate.0C912196";
    private ServiceProvider? _services;

    // R1（1.2.71）：启动期（闪屏未关）致命错误必须可见化——历史上被吞成"僵尸闪屏"，
    // 单实例互斥量还阻断后续启动，用户看到"怎么点都没反应"（2026-08-08 实锤）。
    private bool _startupPhaseCompleted;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _singleInstanceActivation;
    private CancellationTokenSource? _singleInstanceListenerCancellation;
    private Task? _singleInstanceListenerTask;
    private bool _ownsSingleInstanceMutex;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        var batchCommand = Phase2BatchCommand.Parse(e.Args);
        var datasetCaptureCommand = Phase2DatasetCaptureCommand.Parse(e.Args);
        var headlessCommand = batchCommand is not null ||
                              datasetCaptureCommand is not null;
        // 指令测试台模式（测试包专用）：--command-test 启动，只显示三按钮测试窗口，
        // 一切动作仅由指令文件驱动，启动后不自动做任何事。
        var commandTestMode = Array.Exists(
            e.Args,
            argument => string.Equals(argument, "--command-test", StringComparison.OrdinalIgnoreCase));
        if (batchCommand is not null)
        {
            WriteBatchStartupProgress(batchCommand, "command-parsed");
        }

        base.OnStartup(e);
        // 1.2.72（运维解锁）：command-test（测试台）跳过单实例——它与主程序共存是
        // 合法需求，且实测场景需要它绕过僵尸实例占用的互斥量（10656 教训）。
        if (!headlessCommand && !commandTestMode && !TryAcquireSingleInstance())
        {
            Shutdown(0);
            return;
        }

        if (headlessCommand)
        {
            // The property setter requires WPF startup to be initialized, but it
            // still has to run before the first await yields back to the dispatcher.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (batchCommand is not null)
            {
                WriteBatchStartupProgress(batchCommand, "wpf-started");
            }
        }
        var startupStopwatch = Stopwatch.StartNew();
        StartupWindow? startupWindow = null;
        if (!headlessCommand)
        {
            startupWindow = new StartupWindow();
            MainWindow = startupWindow;
            startupWindow.Show();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }

        var shellElapsed = startupStopwatch.Elapsed;

        if (datasetCaptureCommand is not null)
        {
            try
            {
                using var capture = new WindowsGraphicsGameCapture();
                var captureService = new Phase2DatasetCaptureService(
                    new GameWindowService(),
                    capture);
                await captureService.CaptureAsync(
                    datasetCaptureCommand,
                    CancellationToken.None);
                Shutdown(0);
            }
            catch (Exception exception)
            {
                try
                {
                    Directory.CreateDirectory(
                        datasetCaptureCommand.OutputDirectory);
                    await File.WriteAllTextAsync(
                        Path.Combine(
                            datasetCaptureCommand.OutputDirectory,
                            "capture-error.txt"),
                        $"{DateTimeOffset.Now:O}{Environment.NewLine}" +
                        $"{exception.GetType().Name}: {exception.Message}");
                }
                catch
                {
                    // Preserve the original capture failure as the exit reason.
                }

                Shutdown(1);
            }

            return;
        }

        var configDirectory = Path.Combine(AppContext.BaseDirectory, "config");
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data", "4.4");
        var gameDataTask = Task.Run(() =>
            GameDataCatalogLoader.Load(dataDirectory));
        var navigationTask = Task.Run(() =>
            ConfigurationLoader.LoadNavigation(
                Path.Combine(configDirectory, "navigation-flow.json")));
        var pageRecognitionTask = Task.Run(() =>
            GamePageRecognitionConfig.Load(Path.Combine(
                configDirectory,
                "page-recognition.1920x1080.json")));
        var goldDigitTemplatesTask = Task.Run(() =>
            LoadGoldDigitTemplates(dataDirectory));
        var phase2IconTemplatesTask = Task.Run(() =>
            Phase2IconTemplateCatalog.Load(dataDirectory));
        var communityTask = Task.Run(() =>
            CommunityContactOptions.Load(
                Path.Combine(configDirectory, "community.json")));
        var gameData = await gameDataTask;
        var characterTemplatesTask = Task.Run(() =>
            LoadCharacterCardTemplates(dataDirectory, gameData));
        var loadedConfiguration = (
            GameData: gameData,
            Navigation: await navigationTask,
            PageRecognition: await pageRecognitionTask,
            CharacterTemplates: await characterTemplatesTask,
            GoldDigitTemplates: await goldDigitTemplatesTask,
            Phase2IconTemplates: await phase2IconTemplatesTask,
            Community: await communityTask);
        if (batchCommand is not null)
        {
            WriteBatchStartupProgress(batchCommand, "configuration-loaded");
        }
        var configurationElapsed = startupStopwatch.Elapsed - shellElapsed;

        var services = new ServiceCollection();
        services.AddSingleton(loadedConfiguration.GameData);
        services.AddSingleton(loadedConfiguration.Navigation);
        services.AddSingleton(loadedConfiguration.PageRecognition.Pages);
        services.AddSingleton(loadedConfiguration.CharacterTemplates);
        services.AddSingleton(loadedConfiguration.GoldDigitTemplates);
        services.AddSingleton(loadedConfiguration.Phase2IconTemplates);
        services.AddSingleton(loadedConfiguration.Community);
        services.AddSingleton<IGameWindowService, GameWindowService>();
        // 单例 IGameCapture 由记录器（Phase2LiveCollectionService）与刷开局链路共用。
        // 2026-08-05 曾尝试给记录器配独立实例（"recorder-capture"）避免饿死，但
        // WindowsGraphicsGameCapture 双实例并行对同一窗口不稳定（0.2.829 实测
        // 1-4 战斗页后记录器卡死，checkpoint 无更新）——已回滚。
        // 正确的根治方案是统一识别流（一个捕获器+一个识别器，数据分发），见
        // docs/PENDING_USER_FIXES.md 第 1 项步骤 3/4。
        services.AddSingleton<IGameCapture, WindowsGraphicsGameCapture>();
        services.AddSingleton<ITemplateMatcher, OpenCvTemplateMatcher>();
        services.AddSingleton<
            ICharacterCardRecognizer>(
            _ => new OpenCvCharacterCardRecognizer(
                candidateLimit: 32,
                // 区分度门放宽角色（用户 2026-08-07）：白厄/飞霄/开拓者等
                // 银灰发系相似角色，与用户实机模板 runner-up 分差小
                //（0.013-0.047），全局 0.04 会判 Uncertain——仅这些角色
                // 放宽到 0.02，其他角色保持 0.04 保守。
                lenientLeadOverCharacterIds:
                [
                    "currency_wars_character_40", // 白厄
                    "currency_wars_character_56", // 飞霄
                    "currency_wars_character_72", // 阿格莱雅（实测与开拓者混淆）
                    "currency_wars_character_trailblazer", // 开拓者
                ],
                // 变费角色置信度门槛放宽（用户 2026-08-07）：银狼 3/4/5 费
                // 背景色不同，匹配分天然偏低（detail 5/5/15 下 0.465）——
                // 仅银狼放宽到 0.46，其他角色保持 0.55。
                lenientConfidenceCharacterIds:
                [
                    "currency_wars_character_05", // 银狼LV.999（变费）
                    // 2026-08-15 实机：符玄后台 0.535 卡 0.55 阈值 0.015
                    //（卡面多变体，与银狼同类问题）。
                    "currency_wars_character_23", // 符玄
                ]));
        services.AddSingleton<IHorizontalSpecialUnitRecognizer>(
            _ => new OpenCvHorizontalSpecialUnitRecognizer(Path.Combine(
                AppContext.BaseDirectory,
                "data",
                "4.4",
                "character-card-templates",
                "special_unit_peipei__horizontal-face.png")));
        services.AddSingleton<IGoldDigitRecognizer, OpenCvGoldDigitRecognizer>();
        services.AddSingleton<IPhase2IconRecognizer, OpenCvPhase2IconRecognizer>();
        services.AddSingleton<IGamePageClassifier, TemplateGamePageClassifier>();
        // 自动化组件（奖励关/备战/祈愿）用限定页面子集的轻量分类器：全量 38 页全探太慢，
        // 单次分类成本约 1/2.5，热路径（出战轮询/商店稳定读取/部署验证/弹框检测）累计省数分钟
        services.AddSingleton<IAutomationPageClassifier>(serviceProvider =>
            AutomationPageIds.Create(
                serviceProvider.GetRequiredService<ITemplateMatcher>(),
                serviceProvider.GetRequiredService<IReadOnlyList<GamePageDefinition>>()));
        services.AddSingleton<IPhase2FastPageClassifier>(provider =>
            new Phase2FastPageClassifier(
                provider.GetRequiredService<ITemplateMatcher>(),
                provider.GetRequiredService<
                    IReadOnlyList<GamePageDefinition>>()));
        services.AddSingleton<IOfflineOcr, WindowsOfflineOcr>();
        services.AddSingleton(_ => new PpOcrOfflineOcr(
            Path.Combine(
                AppContext.BaseDirectory,
                "data",
                "ocr",
                "rapidocr",
                "PP-OCRv6_rec_small.onnx"),
            // 每个 lane 一个独立 InferenceSession：同一 session 绝不并发 Run
            // （ORT CPU EP 并发 Run 是 coreclr c0000005 崩溃根因）。GPU（DirectML）
            // 吞吐高，8 路并行安全且不互相阻塞（用户 i5-13400F 12 线程，2026-08-06
            // 调优：6→8 提升并行识别吞吐）。
            maximumConcurrency: 8));
        services.AddSingleton<Phase2OfflineOcrSet>(provider =>
        {
            var primary = provider.GetRequiredService<PpOcrOfflineOcr>();
            return new Phase2OfflineOcrSet(
                new ConfidenceFallbackOfflineOcr(
                    primary,
                    new WindowsOfflineOcr(
                        "zh-Hans",
                        OfflineOcrRecognitionMode.Fast,
                        maximumConcurrency: 4)),
                new ConfidenceFallbackOfflineOcr(
                    primary,
                    new WindowsOfflineOcr(
                        "en-US",
                        OfflineOcrRecognitionMode.Fast,
                        maximumConcurrency: 4)));
        });
        services.AddSingleton<Phase2RecognitionWarmUpService>();
        services.AddSingleton<IOcrOpeningPageReader, OcrOpeningPageReader>();
        services.AddSingleton<IGameForegroundGuard, GameForegroundGuard>();
        services.AddSingleton<IPassiveRecoveryMonitor, PassiveRecoveryMonitor>();
        services.AddSingleton<IInputController, Win32InputController>();
        services.AddTransient<UnknownPageEscapeRecovery>();
        services.AddSingleton<OpeningFilterEvaluator>();
        services.AddSingleton<InitialRewardFormationPlanner>();
        services.AddSingleton<PreparationBenchSalePlanner>();
        services.AddSingleton<RewardShopReader>();
        services.AddSingleton<RewardShopPurchasePlanner>();
        services.AddSingleton<InvestmentStrategyPageReader>();
        services.AddSingleton<RewardVisualDetector>();
        services.AddSingleton<WishTrialSelectionAutomation>();
        services.AddSingleton<TrialRecruitSelectionAutomation>();
        services.AddTransient<PreparationBoardController>();
        services.AddTransient<IPreparationBoardController>(provider =>
            provider.GetRequiredService<PreparationBoardController>());
        services.AddTransient<IPreparationBoardCompletionController>(provider =>
            provider.GetRequiredService<PreparationBoardController>());
        services.AddTransient<
            IRewardStageAutomationController,
            RewardStageAutomationController>();
        services.AddTransient<IRejectedOpeningRecovery, CurrencyWarsRejectedOpeningRecovery>();
        services.AddTransient<IAbandonSettlementRecovery, CurrencyWarsRejectedOpeningRecovery>();
        services.AddTransient<IRunAbandoner, CurrencyWarsRejectedOpeningRecovery>();
        services.AddSingleton<UiTaskEventSink>();
        services.AddSingleton<ITaskEventSink>(
            provider => provider.GetRequiredService<UiTaskEventSink>());
        services.AddTransient<CurrencyWarsNavigationTask>();
        services.AddTransient<ICurrencyWarsOpeningNavigator>(
            provider => provider.GetRequiredService<CurrencyWarsNavigationTask>());
        services.AddTransient<OpeningRerollLoopCoordinator>();
        services.AddTransient<IOpeningRerollRunner, OpeningRerollRunnerAdapter>();
        // 「1-3 三星五费」整局循环：把 实时快照源 → 快照组装 → 决策协调器 → 生产执行器 → 开局重刷 组装成可 DI 注入的单个入口。
        // note: RewardStageAutomationController 具体类单独注册一次，供 FateGrailProductionExecutor 使用（接口映射不足以按具体类解析）。
        services.AddTransient<RewardStageAutomationController>();
        // note: FateGrailRunLoop 依赖运行时窗口句柄，不在 DI 无参组装；
        // 由 MainWindow 按钮点击时用 IServiceProvider 现场组装（见 MainWindow）。
        services.AddSingleton<IPhase1AutomationService, Phase1AutomationService>();
        services.AddSingleton<GuideRepository>();
        services.AddSingleton<AdvisorEngine>();
        services.AddSingleton<Phase2OperationalScreenshotAnalyzer>(provider =>
        {
            var phase2Ocr = provider.GetRequiredService<Phase2OfflineOcrSet>();
            return new Phase2OperationalScreenshotAnalyzer(
                provider.GetRequiredService<ICharacterCardRecognizer>(),
                provider.GetRequiredService<
                    IReadOnlyList<CharacterCardTemplateDefinition>>(),
                provider.GetRequiredService<IPhase2IconRecognizer>(),
                provider.GetRequiredService<
                    IReadOnlyList<Phase2IconTemplateDefinition>>(),
                phase2Ocr.Text,
                provider.GetRequiredService<GameDataCatalog>(),
                phase2Ocr.Numeric,
                storeLevelOcr: provider.GetRequiredService<PpOcrOfflineOcr>(),
                pageClassifier: provider.GetRequiredService<IGamePageClassifier>(),
                enableRobustFallback: false,
                horizontalSpecialUnitRecognizer: provider.GetRequiredService<
                    IHorizontalSpecialUnitRecognizer>());
        });
        services.AddSingleton<Phase2BatchImageAnalysisService>();
        services.AddSingleton<IHistoricalDashboardProjection,
            HistoricalDashboardProjection>();
        services.AddSingleton<IChallengeSummaryReportGenerator,
            ChallengeSummaryReportGenerator>();
        services.AddSingleton(_ => new LocalRunStore(Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            ProductIdentity.UserDataDirectoryName,
            "runs")));
        services.AddSingleton<ISituationScreenshotAnalyzer>(provider =>
        {
            var phase2Ocr = provider.GetRequiredService<Phase2OfflineOcrSet>();
            return new CurrencyWarsSituationScreenshotAnalyzer(
                provider.GetRequiredService<IGamePageClassifier>(),
                provider.GetRequiredService<ICharacterCardRecognizer>(),
                provider.GetRequiredService<
                    IReadOnlyList<CharacterCardTemplateDefinition>>(),
                provider.GetRequiredService<IGoldDigitRecognizer>(),
                provider.GetRequiredService<
                    IReadOnlyList<GoldDigitTemplateDefinition>>(),
                provider.GetRequiredService<IOcrOpeningPageReader>(),
                provider.GetRequiredService<RewardShopReader>(),
                provider.GetRequiredService<IOfflineOcr>(),
                provider.GetRequiredService<GameDataCatalog>(),
                provider.GetRequiredService<GuideRepository>(),
                provider.GetRequiredService<AdvisorEngine>(),
                Path.Combine(
                    AppContext.BaseDirectory,
                    "data",
                    "advisor",
                    "1.0.0",
                    "4.4",
                    "guides"),
                provider.GetRequiredService<
                    Phase2OperationalScreenshotAnalyzer>(),
                numericOcr: phase2Ocr.Numeric,
                phase2IconTemplates: provider.GetRequiredService<
                    IReadOnlyList<Phase2IconTemplateDefinition>>());
        });
        services.AddTransient<
            IPhase2LiveCollectionService,
            Phase2LiveCollectionService>();
        // 统一识别流（一个摄像机+一个识别器，数据分发）——记录员启动对局时
        // Attach(pipeline)，刷开局等下游通过 feed 共享同一份识别结果。
        services.AddSingleton<IPhase2RecognitionFeed, Phase2RecognitionFeed>();
        services.AddSingleton<
            IUnifiedRunLifecycleService,
            UnifiedRunLifecycleService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SituationAnalysisViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<CommandTestWindow>();
        _services = services.BuildServiceProvider();

        if (batchCommand is not null)
        {
            try
            {
                WriteBatchStartupProgress(batchCommand, "batch-analysis-starting");
                var batchService = _services.GetRequiredService<
                    Phase2BatchImageAnalysisService>();
                WriteBatchStartupProgress(batchCommand, "batch-service-resolved");
                WriteBatchStartupProgress(
                    batchCommand,
                    "recognition-warmup-starting");
                await _services.GetRequiredService<
                        Phase2RecognitionWarmUpService>()
                    .WarmUpAsync(CancellationToken.None);
                WriteBatchStartupProgress(
                    batchCommand,
                    "recognition-warmup-completed");
                await batchService.AnalyzeDirectoryAsync(
                    batchCommand.SourceDirectory,
                    batchCommand.OutputDirectory,
                    CancellationToken.None,
                    batchCommand.ContinuousSequence,
                    batchCommand.WriteAnnotations);
                WriteBatchStartupProgress(batchCommand, "batch-analysis-completed");
                Shutdown(0);
            }
            catch (Exception exception)
            {
                WriteBatchStartupProgress(
                    batchCommand,
                    $"batch-analysis-failed\t{exception.GetType().Name}: " +
                    exception.Message);
                Shutdown(1);
            }

            return;
        }

        var serviceRegistrationElapsed =
            startupStopwatch.Elapsed - shellElapsed - configurationElapsed;
        if (commandTestMode)
        {
            var testWindow = _services.GetRequiredService<CommandTestWindow>();
            MainWindow = testWindow;
            testWindow.Show();
            StartSingleInstanceActivationListener();
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            _startupPhaseCompleted = true;
            startupWindow!.Close();
            _ = ObserveRecognitionWarmUpAsync(
                _services.GetRequiredService<Phase2RecognitionWarmUpService>(),
                _services.GetRequiredService<UiTaskEventSink>());
            return;
        }

        var mainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
        StartSingleInstanceActivationListener();
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        _startupPhaseCompleted = true;
        startupWindow!.Close();
        _ = ObserveRecognitionWarmUpAsync(
            _services.GetRequiredService<Phase2RecognitionWarmUpService>(),
            _services.GetRequiredService<UiTaskEventSink>());
        var mainWindowElapsed =
            startupStopwatch.Elapsed -
            shellElapsed -
            configurationElapsed -
            serviceRegistrationElapsed;
        startupStopwatch.Stop();
        var processElapsed = DateTime.Now -
                             Process.GetCurrentProcess().StartTime;
        _services.GetRequiredService<UiTaskEventSink>().Publish(
            new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Information,
                "StartupCompleted",
                $"应用主窗口已显示：进程启动至今 " +
                $"{processElapsed.TotalSeconds:F2} 秒；" +
                $"WPF 初始化 {startupStopwatch.Elapsed.TotalSeconds:F2} 秒" +
                $"（启动窗 {shellElapsed.TotalSeconds:F2} 秒、" +
                $"数据 {configurationElapsed.TotalSeconds:F2} 秒、" +
                $"服务注册 {serviceRegistrationElapsed.TotalSeconds:F2} 秒、" +
                $"主窗构建 {mainWindowElapsed.TotalSeconds:F2} 秒）。"));
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                var notice = new StartupNoticeWindow(
                    loadedConfiguration.Community)
                {
                    Owner = mainWindow
                };
                notice.Show();
            },
            DispatcherPriority.ApplicationIdle);
    }

    private bool TryAcquireSingleInstance()
    {
        _singleInstanceActivation = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            SingleInstanceActivationName);
        _singleInstanceMutex = new Mutex(false, SingleInstanceMutexName);
        try
        {
            _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsSingleInstanceMutex = true;
        }

        if (_ownsSingleInstanceMutex)
        {
            return true;
        }

        _singleInstanceActivation.Set();
        _singleInstanceActivation.Dispose();
        _singleInstanceActivation = null;
        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        return false;
    }

    private void StartSingleInstanceActivationListener()
    {
        if (_singleInstanceActivation is null ||
            _singleInstanceListenerCancellation is not null)
        {
            return;
        }

        _singleInstanceListenerCancellation = new CancellationTokenSource();
        var cancellation = _singleInstanceListenerCancellation.Token;
        var activation = _singleInstanceActivation;
        _singleInstanceListenerTask = Task.Run(() =>
        {
            var handles = new WaitHandle[]
            {
                activation,
                cancellation.WaitHandle
            };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                _ = Dispatcher.BeginInvoke(ActivateMainWindow);
            }
        });
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is not Window window)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
    }

    private static async Task ObserveRecognitionWarmUpAsync(
        Phase2RecognitionWarmUpService warmUp,
        UiTaskEventSink events)
    {
        try
        {
            await warmUp.WarmUpAsync(CancellationToken.None)
                .ConfigureAwait(false);
            events.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Information,
                "Phase2RecognitionWarmUpCompleted",
                $"第二阶段识别资源预热完成，用时 " +
                $"{warmUp.Elapsed?.TotalSeconds:F2} 秒。"));
        }
        catch (Exception exception)
        {
            events.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Warning,
                "Phase2RecognitionWarmUpFailed",
                "第二阶段识别资源预热失败；实时识别仍会按原有有界降级路径继续：" +
                exception.Message));
        }
    }

    private static void WriteBatchStartupProgress(
        Phase2BatchCommand command,
        string stage)
    {
        try
        {
            Directory.CreateDirectory(command.OutputDirectory);
            File.AppendAllText(
                Path.Combine(command.OutputDirectory, "batch-startup.log"),
                $"{DateTimeOffset.Now:O}\t{stage}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostic progress must never prevent the batch analysis itself.
        }
    }

    private static IReadOnlyList<CharacterCardTemplateDefinition>
        LoadCharacterCardTemplates(
            string dataDirectory,
            GameDataCatalog gameData)
    {
        var templateDirectory = Path.Combine(
            dataDirectory,
            "character-card-templates");
        if (!Directory.Exists(templateDirectory))
        {
            throw new DirectoryNotFoundException(
                $"角色卡牌模板目录不存在：{templateDirectory}");
        }

        var characterTemplates = gameData.CurrencyWarsCharacters
            .SelectMany(character =>
            {
                // 同角色可有多张卡面模板（如开拓者记忆/欢愉、银狼变费 3/4/5 费
                // 背景色不同）——全部加载，匹配时取最高分（用户 2026-08-07
                // 提供实机截图制作变体模板）。
                var files = Directory.GetFiles(
                    templateDirectory,
                    $"{character.Id}__*.png");
                if (files.Length == 0)
                {
                    throw new InvalidDataException(
                        $"角色“{character.Name}”没有大头像模板。");
                }

                return files.Select(file =>
                    new CharacterCardTemplateDefinition(
                        character.Id,
                        character.Name,
                        file));
            })
            .ToList();
        var specialOccupiedFile = Path.Combine(
            templateDirectory,
            "bench_special_privilege_armament_box.png");
        if (!File.Exists(specialOccupiedFile))
        {
            throw new FileNotFoundException(
                "特权武装箱备战席模板不存在。",
                specialOccupiedFile);
        }

        characterTemplates.Add(new CharacterCardTemplateDefinition(
            "bench_special_privilege_armament_box",
            "特权武装箱",
            specialOccupiedFile,
            CharacterCardTemplateKind.SpecialOccupied));
        // 特殊单位模板（2026-08-08）：佩佩等特殊单位占用后台格子、无星级、
        // 不匹配角色模板。从实机素材（user_ref_000032 后台最右）裁剪生成。
        var peipeiFile = Path.Combine(
            templateDirectory,
            "special_unit_peipei__default.png");
        if (File.Exists(peipeiFile))
        {
            characterTemplates.Add(new CharacterCardTemplateDefinition(
                "special_unit_peipei",
                "佩佩",
                peipeiFile,
                CharacterCardTemplateKind.SpecialOccupied));
        }

        // 2026-08-15 素材包补齐：狸猫 13 张 + 叽米 3 张后台特殊单位牌面。
        // 命名约定 special_unit_tanuki_NNN__default.png /
        // special_unit_owlbert_NNN__default.png（与佩佩同目录同约定）。
        foreach (var file in Directory.GetFiles(
                     templateDirectory,
                     "special_unit_*__default.png"))
        {
            var stem = Path.GetFileName(file);
            if (stem.StartsWith("special_unit_peipei", StringComparison.Ordinal))
            {
                continue;
            }

            var id = stem[..stem.IndexOf("__", StringComparison.Ordinal)];
            var displayName = id switch
            {
                "special_unit_tanuki_101" => "步狸人",
                "special_unit_tanuki_102" => "狸职狸狸",
                "special_unit_tanuki_103" => "狸财经狸",
                "special_unit_tanuki_104" => "普狸策",
                "special_unit_tanuki_105" => "幻太子",
                "special_unit_tanuki_106" => "尤狸安",
                "special_unit_tanuki_107" => "佛狸",
                "special_unit_tanuki_108" => "狸小龙",
                "special_unit_tanuki_109" => "狸小虎",
                "special_unit_tanuki_110" => "比狸比狸",
                "special_unit_tanuki_111" => "Gemi狸",
                "special_unit_tanuki_112" => "Gemi狸",
                "special_unit_tanuki_113" => "胡构狸",
                "special_unit_owlbert_901" => "金币大佬叽米",
                "special_unit_owlbert_902" => "星徽大佬叽米",
                "special_unit_owlbert_903" => "环保大佬叽米",
                _ => id
            };
            characterTemplates.Add(new CharacterCardTemplateDefinition(
                id,
                displayName,
                file,
                CharacterCardTemplateKind.SpecialOccupied));
        }

        return characterTemplates;
    }

    private static IReadOnlyList<GoldDigitTemplateDefinition>
        LoadGoldDigitTemplates(string dataDirectory)
    {
        var templateDirectory = Path.Combine(
            dataDirectory,
            "gold-digit-templates");
        return new[] { 3, 7 }
            .Select(digit =>
            {
                var file = Path.Combine(
                    templateDirectory,
                    $"digit_{digit}.png");
                if (!File.Exists(file))
                {
                    throw new FileNotFoundException(
                        $"金币数字 {digit} 的视觉模板不存在。",
                        file);
                }

                return new GoldDigitTemplateDefinition(digit, file);
            })
            .ToArray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceListenerCancellation?.Cancel();
        try
        {
            _singleInstanceListenerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The process is already exiting; activation-listener failures are
            // non-fatal and must not block cleanup.
        }
        _singleInstanceListenerCancellation?.Dispose();
        _singleInstanceActivation?.Dispose();
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        TryReportRecoverableException(e.Exception);
        if (!_startupPhaseCompleted)
        {
            // R1（1.2.71）：启动期异常不再无声吞掉——可见报错后退出进程并释放
            // 单实例互斥量，绝不留"僵尸闪屏"阻断后续启动。
            e.Handled = true;
            try
            {
                MessageBox.Show(
                    "启动失败：" + e.Exception.Message + Environment.NewLine +
                    Environment.NewLine +
                    "详细日志：%LOCALAPPDATA%/CurrencyWarsSmartRaccoon/logs/unhandled-errors.log" +
                    Environment.NewLine + "点击确定退出程序。",
                    "货币战争智能狸 启动错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // 连弹窗都失败（极罕见）——直接退出保底。
            }

            Environment.Exit(1);
            return;
        }

        e.Handled = true;
    }

    private void OnDomainUnhandledException(
        object sender,
        UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            TryWriteFallbackCrashReport(exception);
        }
    }

    private void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        TryReportRecoverableException(e.Exception);
        e.SetObserved();
    }

    private void TryReportRecoverableException(Exception exception)
    {
        try
        {
            var viewModel = _services?.GetService<MainViewModel>();
            if (viewModel is not null)
            {
                viewModel.ReportUnhandledException(exception);
                return;
            }
        }
        catch
        {
            // Fall through to the independent text report.
        }

        TryWriteFallbackCrashReport(exception);
    }

    private void TryWriteFallbackCrashReport(Exception exception)
    {
        try
        {
            var logFile = _services?
                .GetService<UiTaskEventSink>()
                ?.LogFilePath;
            var logDirectory = !string.IsNullOrWhiteSpace(logFile)
                ? Path.GetDirectoryName(logFile)!
                : Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    ProductIdentity.UserDataDirectoryName,
                    "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "unhandled-errors.log"),
                $"[{DateTimeOffset.Now:O}]{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Nothing else is safe to do from a last-chance exception handler.
        }
    }
}
