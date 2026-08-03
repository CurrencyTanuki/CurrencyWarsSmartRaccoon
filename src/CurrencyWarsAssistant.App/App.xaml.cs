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
        if (batchCommand is not null)
        {
            WriteBatchStartupProgress(batchCommand, "command-parsed");
        }

        base.OnStartup(e);
        if (!headlessCommand && !TryAcquireSingleInstance())
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
        services.AddSingleton<IGameCapture, WindowsGraphicsGameCapture>();
        services.AddSingleton<ITemplateMatcher, OpenCvTemplateMatcher>();
        services.AddSingleton<
            ICharacterCardRecognizer,
            OpenCvCharacterCardRecognizer>();
        services.AddSingleton<IGoldDigitRecognizer, OpenCvGoldDigitRecognizer>();
        services.AddSingleton<IPhase2IconRecognizer, OpenCvPhase2IconRecognizer>();
        services.AddSingleton<IGamePageClassifier, TemplateGamePageClassifier>();
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
            // 吞吐高，6 路并行安全且不互相阻塞。
            maximumConcurrency: 6));
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
        services.AddSingleton<UiTaskEventSink>();
        services.AddSingleton<ITaskEventSink>(
            provider => provider.GetRequiredService<UiTaskEventSink>());
        services.AddTransient<CurrencyWarsNavigationTask>();
        services.AddTransient<ICurrencyWarsOpeningNavigator>(
            provider => provider.GetRequiredService<CurrencyWarsNavigationTask>());
        services.AddTransient<OpeningRerollLoopCoordinator>();
        services.AddTransient<IOpeningRerollRunner, OpeningRerollRunnerAdapter>();
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
                pageClassifier: provider.GetRequiredService<IGamePageClassifier>(),
                enableRobustFallback: false);
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
        services.AddSingleton<
            IUnifiedRunLifecycleService,
            UnifiedRunLifecycleService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SituationAnalysisViewModel>();
        services.AddSingleton<MainWindow>();
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
        var mainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
        StartSingleInstanceActivationListener();
        await Dispatcher.Yield(DispatcherPriority.Loaded);
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
            .Select(character =>
            {
                var files = Directory.GetFiles(
                    templateDirectory,
                    $"{character.Id}__*.png");
                if (files.Length != 1)
                {
                    throw new InvalidDataException(
                        $"角色“{character.Name}”的大头像模板数量应为1，实际为{files.Length}。");
                }

                return new CharacterCardTemplateDefinition(
                    character.Id,
                    character.Name,
                    files[0]);
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
