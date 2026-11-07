using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Accounts;
using Fraylo.Application.Analytics;
using Fraylo.Application.FacebookAnalytics;
using Fraylo.Application.Assets;
using Fraylo.Application.Content;
using Fraylo.Application.Documents;
using Fraylo.Application.Facebook;
using Fraylo.Application.Jobs;
using Fraylo.Application.Projects;
using Fraylo.Application.Prompts;
using Fraylo.Application.Providers;
using Fraylo.Application.Publishing;
using Fraylo.Application.Scenes;
using Fraylo.Application.Settings;
using Fraylo.Application.Workflows;
using Fraylo.Browser;
using Fraylo.Browser.Analytics;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Models;
using Fraylo.Browser.Publishing;
using Fraylo.Contracts.Browser;
using Fraylo.Contracts.Jobs;
using Fraylo.Desktop.Bridge;
using Fraylo.Desktop.Bridge.Handlers;
using Fraylo.Desktop.Hosting;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Interceptors;
using Fraylo.Infrastructure.Settings;
using Fraylo.Infrastructure.Storage;

namespace Fraylo.Desktop;

public partial class MainWindow : Window
{
    private readonly SemaphoreSlim _startupLock = new(1, 1);
    private CancellationTokenSource? _startupCts;
    private ProviderAccountHandlers? _providerAccountHandlers;
    private PromptHandlers? _promptHandlers;
    private ProjectHandlers? _projectHandlers;
    private SceneHandlers? _sceneHandlers;
    private VideoWorkflowHandlers? _videoWorkflowHandlers;
    private ProviderHandlers? _providerHandlers;
    private WorkflowHandlers? _workflowHandlers;
    private AnalyticsHandlers? _analyticsHandlers;
    private PublishingHandlers? _publishingHandlers;
    private FacebookHandlers? _facebookHandlers;
    private FacebookAnalyticsHandlers? _facebookAnalyticsHandlers;
    private AnalyticsAnalysisHandlers? _analyticsAnalysisHandlers;
    private FacebookAnalyticsService? _facebookAnalyticsService;
    private CancellationTokenSource? _facebookQueueCts;
    private Task? _facebookQueueTask;
    private UpdateHandlers? _updateHandlers;
    private BrowserHandlers? _browserHandlers;
    private SettingsHandlers? _settingsHandlers;
    private AssetHandlers? _assetHandlers;
    private DocumentHandlers? _documentHandlers;
    private JobHandlers? _jobHandlers;
    private JobScheduler? _jobScheduler;
    private Action<JobStateChangedPayload>? _jobStateChangedHandler;
    private Action<JobProgressUpdatedPayload>? _jobProgressUpdatedHandler;
    private LoginCoordinator? _loginCoordinator;
    private ProfileRegistry? _profileRegistry;
    private TabPool? _tabPool;
    private HostBrowserGatekeeper? _browserGatekeeper;
    private BrowserSessionCheckpointCoordinator? _sessionCheckpointCoordinator;
    private Func<BrowserProfileStateChangedPayload, Task>? _profileStateChangedHandler;
    private bool _isClosed;

    public DesktopRuntimePaths RuntimePaths { get; }

    public IHostThemeService ThemeService { get; }

    public IBridgeDispatcher DispatcherInstance { get; }

    public WebView2HostManager HostManager { get; }

    public BridgeMessageRouter? BridgeRouter => HostManager.BridgeRouter;

    public MainWindow()
        : this(App.CurrentRuntimePaths ?? DesktopRuntimePaths.FromCommandLineArgs())
    {
    }

    public MainWindow(DesktopRuntimePaths runtimePaths)
    {
        RuntimePaths = runtimePaths ?? throw new ArgumentNullException(nameof(runtimePaths));

        InitializeComponent();
        Opacity = 0;

        // Tối ưu kích thước ban đầu theo màn hình người dùng để ứng dụng rộng rãi, thoáng đãng
        AdjustInitialWindowSize();

        DispatcherInstance = new BridgeDispatcher();
        ThemeService = new HostThemeService(new ThemePreferenceStore(RuntimePaths.DatabasePath));
        HostManager = new WebView2HostManager(ThemeService, DispatcherInstance, RuntimePaths.WebView2ProfileDirectory);

        // Áp dụng theme tức thời để đồng bộ giao diện cửa sổ và overlay
        ApplyEffectiveTheme(ThemeService.CurrentEffectiveTheme);

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    public void ApplyEffectiveTheme(string effectiveTheme)
    {
        ThemeService.ApplyTheme(effectiveTheme, this, WebViewControl);

        bool isDark = string.Equals(ThemeService.CurrentEffectiveTheme, "dark", StringComparison.OrdinalIgnoreCase);
        ErrorOverlay.Background = new SolidColorBrush(isDark
            ? Color.FromRgb(0x0B, 0x12, 0x20)
            : Color.FromRgb(0xF4, 0xF6, 0xFB));
        ErrorTitleText.Foreground = new SolidColorBrush(isDark
            ? Color.FromRgb(0xF4, 0xF7, 0xFC)
            : Color.FromRgb(0x0F, 0x17, 0x2A));
        ErrorMessageText.Foreground = new SolidColorBrush(isDark
            ? Color.FromRgb(0xA9, 0xB8, 0xCD)
            : Color.FromRgb(0x47, 0x55, 0x69));
    }

    private void AdjustInitialWindowSize()
    {
        var workArea = SystemParameters.WorkArea;
        if (workArea.Width > 0 && workArea.Height > 0)
        {
            // Tối ưu kích thước ban đầu: chiếm khoảng 85% diện tích màn hình làm việc
            // Giúp ứng dụng rộng rãi, thoáng đãng trên cả màn hình 2K, 3K lẫn Full HD
            double targetWidth = Math.Min(1440, Math.Max(1280, workArea.Width * 0.75));
            double targetHeight = Math.Min(1020, Math.Max(880, workArea.Height * 0.85));

            // Bảo đảm không vượt quá diện tích làm việc của màn hình nhỏ
            targetWidth = Math.Min(targetWidth, workArea.Width);
            targetHeight = Math.Min(targetHeight, workArea.Height);

            Width = Math.Max(MinWidth, targetWidth);
            Height = Math.Max(MinHeight, targetHeight);
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeStartupAsync();
    }

    private async Task InitializeStartupAsync()
    {
        if (_isClosed)
        {
            return;
        }

        // Hủy yêu cầu khởi động đang dở (nếu có từ lần nhấn trước)
        _startupCts?.Cancel();
        _startupCts?.Dispose();
        _startupCts = new CancellationTokenSource();
        var ct = _startupCts.Token;

        try
        {
            await _startupLock.WaitAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_isClosed || ct.IsCancellationRequested)
            {
                return;
            }

            ErrorOverlay.Visibility = Visibility.Collapsed;

            // 1. Dọn dẹp handlers cũ nếu đang thử lại (retry) để tránh nhân đôi listener
            CleanupG2Handlers();

            // 2. Khởi tạo bất đồng bộ SQLite Database theo final-schema.sql (T04)
            var bootstrapper = new SqliteDatabaseBootstrapper();
            var bootstrapResult = await bootstrapper.BootstrapAsync(
                databasePath: RuntimePaths.DatabasePath,
                cancellationToken: ct);

            // Kiểm tra trạng thái đóng cửa sổ hoặc hủy bỏ sau await bootstrap
            if (_isClosed || ct.IsCancellationRequested)
            {
                return;
            }

            if (!bootstrapResult.IsBootstrapSuccessful)
            {
                throw new InvalidOperationException("Khởi tạo cơ sở dữ liệu không thành công.");
            }

            await ThemeService.InitializePreferenceAsync(ct);
            if (_isClosed || ct.IsCancellationRequested)
            {
                return;
            }

            ApplyEffectiveTheme(ThemeService.CurrentEffectiveTheme);

            // 3. ĐĂNG KÝ G2 HANDLERS CHỈ SAU KHI BOOTSTRAP THÀNH CÔNG VÀ CHƯA BỊ HỦY
            // Nối account, prompt và Project-first handlers.
            var connectionString = DatabasePathHelper.BuildConnectionString(bootstrapResult.DatabasePath);
            var dbOptions = new DbContextOptionsBuilder<FrayloDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(new SqlitePragmaInterceptor())
                .Options;

            var tabPool = new TabPool();
            _tabPool = tabPool;
            var profileRegistry = new ProfileRegistry(() => new FrayloDbContext(dbOptions), null, tabPool);
            await profileRegistry.ReconcileCheckpointsAtStartupAsync(ct);

            var profileOwner = new ProfileOwner(pid => profileRegistry.GetUserDataDir(pid, "AI"));
            var processController = new PlaywrightBrowserController(profileRegistry, profileOwner);

            // Vòng đời browser: Không tự khởi chạy runtime/browser khi mở app hoặc load danh sách tài khoản.
            // Chỉ khởi chạy browser automation khi đã reserve được job đủ điều kiện.
            var sessionProbe = new SessionProbe(profileRegistry, processController);

            int GetMaxOpenBrowsers()
            {
                try
                {
                    using var db = new FrayloDbContext(dbOptions);
                    var setting = db.AppSettings.AsNoTracking().FirstOrDefault(s => s.Key == "scheduler.max_open_browsers");
                    if (setting != null && int.TryParse(setting.Value, out var val) && val > 0)
                    {
                        return val;
                    }
                }
                catch { }
                return 4;
            }

            int GetOpenBrowserCount()
            {
                return _browserGatekeeper?.GetActiveOrBusyProfileIds().Count ?? 0;
            }

            BrowserSessionCheckpointCoordinator? sessionCheckpointCoordinator = null;
            var loginCoordinator = new LoginCoordinator(
                profileRegistry,
                processController,
                sessionProbe,
                getMaxOpenBrowsers: GetMaxOpenBrowsers,
                getOpenBrowserCount: GetOpenBrowserCount,
                canReplaceHeadlessContext: profileId =>
                    tabPool.GetActiveJobCount(profileId) == 0 &&
                    sessionCheckpointCoordinator?.HasUncertainOrActiveJobs(profileId) != true);
            _profileRegistry = profileRegistry;
            _loginCoordinator = loginCoordinator;

            sessionCheckpointCoordinator = new BrowserSessionCheckpointCoordinator(
                profileRegistry,
                loginCoordinator,
                sessionProbe,
                processController,
                tabPool,
                () => new FrayloDbContext(dbOptions));
            _sessionCheckpointCoordinator = sessionCheckpointCoordinator;

            var browserGatekeeper = new HostBrowserGatekeeper(profileRegistry, loginCoordinator, tabPool, processController, sessionCheckpointCoordinator, new JobActivityLog(dbOptions),
                isBrowserVisible: providerKey =>
                {
                    try
                    {
                        using var db = new FrayloDbContext(dbOptions);
                        var value = db.AppSettings.AsNoTracking().Where(s => s.Key == SettingsService.KeyVisibleBrowserProviders).Select(s => s.Value).FirstOrDefault();
                        return providerKey != null && SettingsService.ParseVisibleBrowserProviders(value).Contains(providerKey, StringComparer.OrdinalIgnoreCase);
                    }
                    catch { return false; }
                });
            _browserGatekeeper = browserGatekeeper;

            _profileStateChangedHandler = async payload =>
            {
                if (BridgeRouter != null)
                {
                    await BridgeRouter.PostEventAsync("browser.profile_state_changed", payload).ConfigureAwait(false);
                }

                // Khi profile hoàn tất login hoặc unpause về Hidden/Stopped/Idle: đánh thức scheduler tiếp tục dispatch
                if (_jobScheduler != null &&
                    (string.Equals(payload.VisibilityState, BrowserVisibilityState.Hidden, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(payload.OperationalState, BrowserOperationalState.Stopped, StringComparison.OrdinalIgnoreCase)))
                {
                    _jobScheduler.NotifyWakeup(new JobWakeupSignal("browser.profile_state_changed", ProfileId: payload.ProfileId));
                }
            };
            profileRegistry.ProfileStateChanged += _profileStateChangedHandler;

            var providerAccountService = new ProviderAccountService(dbOptions, sessionProbe, async (pid, ct) =>
            {
                using var operation = FacebookGroupBrowser.AcquireProfileOperation(pid);
                await processController.CloseContextAsync(pid, ct);
                await profileRegistry.DeleteUserDataAsync(pid, ct);
            });
            _providerAccountHandlers = new ProviderAccountHandlers(providerAccountService);
            _providerAccountHandlers.Register(DispatcherInstance);

            _promptHandlers = new PromptHandlers(dbOptions);
            _promptHandlers.Register(DispatcherInstance);

            var projectService = new ProjectService(dbOptions);
            _projectHandlers = new ProjectHandlers(projectService);
            _projectHandlers.Register(DispatcherInstance);

            var sceneService = new SceneService(dbOptions);
            _sceneHandlers = new SceneHandlers(sceneService);
            _sceneHandlers.Register(DispatcherInstance);

            var browserHandlers = new BrowserHandlers(profileRegistry, loginCoordinator, sessionProbe);
            _browserHandlers = browserHandlers;
            _browserHandlers.Register(DispatcherInstance);

            var rootRegistry = new StorageRootRegistry(dbOptions);
            await rootRegistry.EnsureDefaultRootsAsync(ct).ConfigureAwait(false);
            var fileStore = new AssetFileStore();
            var settingsService = new SettingsService(dbOptions, rootRegistry);
            var assetService = new AssetService(dbOptions, rootRegistry, fileStore);
            var documentExportService = new DocumentExportService(dbOptions, rootRegistry, fileStore);

            _settingsHandlers = new SettingsHandlers(settingsService);
            _settingsHandlers.Register(DispatcherInstance);

            _assetHandlers = new AssetHandlers(assetService);
            _assetHandlers.Register(DispatcherInstance);

            _documentHandlers = new DocumentHandlers(documentExportService);
            _documentHandlers.Register(DispatcherInstance);

            var providerRegistry = new ProviderRegistry();
            providerRegistry.RegisterAdapter(new ChatGptWebAdapter(new ChatGptPlaywrightAutomation(processController, profileRegistry, dbOptions, rootRegistry, assetService)));
            providerRegistry.RegisterAdapter(new GeminiWebAdapter(new GeminiPlaywrightAutomation(processController, profileRegistry, dbOptions, rootRegistry, assetService)));
            var museAiAutomation = new MuseAiPlaywrightAutomation(processController, browserGatekeeper, profileRegistry, dbOptions, rootRegistry, assetService);
            providerRegistry.RegisterAdapter(new MuseAiVideoAdapter(museAiAutomation));
            var providerHandlers = new ProviderHandlers(providerRegistry, providerAccountService);
            _providerHandlers = providerHandlers;
            _providerHandlers.Register(DispatcherInstance);

            var workflowDefService = new WorkflowDefinitionService(dbOptions);
            var workflowHandlers = new WorkflowHandlers(workflowDefService);
            _workflowHandlers = workflowHandlers;
            _workflowHandlers.Register(DispatcherInstance);

            var snapshotFactory = new JobSnapshotFactory(settingsService, dbOptions: dbOptions);
            var reservation = new JobReservation(dbOptions);
            var executor = new ProductionJobExecutor(providerRegistry);
            var scheduler = new JobScheduler(dbOptions, reservation, executor, browserGatekeeper);
            var jobService = new JobService(dbOptions, snapshotFactory, scheduler);
            var analysisService = new AnalyticsAnalysisService(dbOptions, providerRegistry, jobService, scheduler);
            _analyticsAnalysisHandlers = new AnalyticsAnalysisHandlers(analysisService);
            _analyticsAnalysisHandlers.Register(DispatcherInstance);

            var analyticsService = new AnalyticsService(dbOptions, analysis: analysisService);
            var tiktokCrawler = new TikTokAnalyticsSyncService(
                dbOptions,
                profileRegistry,
                processController,
                loginCoordinator,
                sessionProbe,
                analyticsService);
            _analyticsHandlers = new AnalyticsHandlers(analyticsService, tiktokCrawler);
            _analyticsHandlers.Register(DispatcherInstance);

            var publishingService = new TikTokPublishingService(dbOptions, rootRegistry, profileRegistry,
                new TikTokVideoUploader(processController, sessionProbe));
            _publishingHandlers = new PublishingHandlers(publishingService);
            _publishingHandlers.Register(DispatcherInstance);
            var rewriteService = new Fraylo.Application.Facebook.FacebookContentRewriteService(dbOptions, providerRegistry, jobService, scheduler);
            await rewriteService.HoldUnsentOnStartupAsync(ct);
            var facebookService = new FacebookPublishingService(dbOptions, rootRegistry, profileRegistry,
                new FacebookGroupBrowser(processController), processController, rewrite: rewriteService);
            _facebookHandlers = new FacebookHandlers(facebookService, rewriteService);
            _facebookHandlers.Register(DispatcherInstance);
            _facebookAnalyticsService = new FacebookAnalyticsService(dbOptions, profileRegistry, processController, new FacebookInsightsBrowser(processController), analysisService);
            await _facebookAnalyticsService.RecoverInterruptedAsync(ct);
            _facebookAnalyticsHandlers = new FacebookAnalyticsHandlers(_facebookAnalyticsService);
            _facebookAnalyticsHandlers.Register(DispatcherInstance);
            var facebookQueueCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _facebookQueueCts = facebookQueueCts;
            _facebookQueueTask = Task.Run(async () =>
            {
                try { await facebookService.RunQueueAsync(facebookQueueCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (facebookQueueCts.IsCancellationRequested) { }
                catch (Exception ex) { Trace.TraceError("Facebook queue stopped: {0}", ex.GetType().Name); }
            });

            _updateHandlers = new UpdateHandlers(new AppUpdateService(() => Dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown())));
            _updateHandlers.Register(DispatcherInstance);

            var workflowService = new ProjectWorkflowService(dbOptions, jobService);
            var museAiOutputRecovery = new MuseAiOutputRecoveryService(dbOptions, museAiAutomation, jobService, workflowService);

            _videoWorkflowHandlers = new VideoWorkflowHandlers(workflowService);
            _videoWorkflowHandlers.Register(DispatcherInstance);

            await analysisService.HoldUnsentOnStartupAsync(ct).ConfigureAwait(false);
            await jobService.RunCrashRecoverySweepAsync(ct).ConfigureAwait(false);
            await workflowService.RunWorkflowApplySweepAsync(ct).ConfigureAwait(false);
            await workflowService.RunWorkflowContinuationSweepAsync(ct).ConfigureAwait(false);
            await workflowService.RunAutoRenderRecoverySweepAsync(ct).ConfigureAwait(false);

            // Đối soát tệp chạy nền: thư viện lớn không được làm chậm việc mở app.
            _ = Task.Run(async () =>
            {
                try
                {
                    var report = await assetService.ReconcileFilesAsync().ConfigureAwait(false);
                    System.Diagnostics.Trace.TraceInformation($"Đối soát tài nguyên: dọn {report.StagingFilesRemoved} tệp staging, {report.OrphanRelativePaths.Count} tệp không có metadata.");
                }
                catch (Exception)
                {
                    // Đối soát chỉ phục vụ theo dõi; lần khởi động sau sẽ thử lại.
                }
            });

            _jobScheduler = scheduler;
            _jobStateChangedHandler = payload =>
            {
                _ = PublishJobStateAfterWorkflowAsync(workflowService, payload);
            };
            scheduler.OnJobStateChanged += _jobStateChangedHandler;

            _jobProgressUpdatedHandler = payload =>
            {
                if (BridgeRouter != null)
                {
                    _ = BridgeRouter.PostEventAsync("job.progress_updated", payload);
                }
            };
            scheduler.OnJobProgressUpdated += _jobProgressUpdatedHandler;

            scheduler.Start();

            _jobHandlers = new JobHandlers(jobService, async envelope =>
            {
                if (BridgeRouter != null)
                {
                    await BridgeRouter.PostEventAsync(envelope.EventName, envelope.Payload).ConfigureAwait(false);
                }
            }, museAiOutputRecovery);
            _jobHandlers.Register(DispatcherInstance);

            // 4. Khởi tạo WebView2 Host Manager và điều hướng UI
            await HostManager.InitializeAsync(WebViewControl, this, assetService);

            // Kiểm tra trạng thái sau await host initialization
            if (_isClosed || ct.IsCancellationRequested)
            {
                CleanupG2Handlers();
                HostManager.Dispose();
                return;
            }

            Opacity = 1;
        }
        catch (OperationCanceledException)
        {
            // Bỏ qua khi cửa sổ bị đóng hoặc tác vụ khởi động bị hủy
        }
        catch (FileNotFoundException)
        {
            if (_isClosed || ct.IsCancellationRequested)
            {
                return;
            }

            CleanupG2Handlers();

            ErrorMessageText.Text = "Không tìm thấy tệp giao diện Web cục bộ.\nVui lòng đảm bảo bản đóng gói ứng dụng đầy đủ và nhấn 'Thử lại'.";
            ApplyEffectiveTheme(ThemeService.CurrentEffectiveTheme);
            Opacity = 1;
            ErrorOverlay.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is SqliteException || ex is DbUpdateException || (ex is InvalidOperationException inv && inv.Message.Contains("cơ sở dữ liệu", StringComparison.OrdinalIgnoreCase)))
        {
            if (_isClosed || ct.IsCancellationRequested)
            {
                return;
            }

            CleanupG2Handlers();
            Debug.WriteLine($"[MainWindow] Lỗi cơ sở dữ liệu: {ex}");

            ErrorMessageText.Text = $"Không thể khởi tạo cơ sở dữ liệu ứng dụng ({ex.Message}).\nVui lòng kiểm tra quyền truy cập hệ thống và nhấn 'Thử lại'.";
            ApplyEffectiveTheme(ThemeService.CurrentEffectiveTheme);
            Opacity = 1;
            ErrorOverlay.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (_isClosed || ct.IsCancellationRequested)
            {
                return;
            }

            CleanupG2Handlers();
            Debug.WriteLine($"[MainWindow] Lỗi khởi tạo ứng dụng: {ex}");

            ErrorMessageText.Text = $"Khởi tạo ứng dụng thất bại: {ex.Message}\nVui lòng kiểm tra môi trường hệ thống và nhấn 'Thử lại'.";
            ApplyEffectiveTheme(ThemeService.CurrentEffectiveTheme);
            Opacity = 1;
            ErrorOverlay.Visibility = Visibility.Visible;
        }
        finally
        {
            _startupLock.Release();
        }
    }

    private async Task PublishJobStateAfterWorkflowAsync(ProjectWorkflowService workflowService, JobStateChangedPayload payload)
    {
        try
        {
            if (payload.ProjectId is not null && string.Equals(payload.NewState, JobState.Succeeded, StringComparison.OrdinalIgnoreCase))
            {
                await workflowService.ApplyJobOutputAsync(payload.JobId).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Workflow] Output apply failed for job {payload.JobId}: {ex}");
        }
        // UI chỉ đọc lại Project sau khi kết quả đã được lưu vào workflow.
        if (BridgeRouter != null)
        {
            await BridgeRouter.PostEventAsync("job.state_changed", payload).ConfigureAwait(false);
        }
    }

    private async Task StopFacebookQueueAsync()
    {
        var cts = _facebookQueueCts;
        var task = _facebookQueueTask;
        _facebookQueueCts = null;
        _facebookQueueTask = null;
        if (cts == null) return;
        await cts.CancelAsync().ConfigureAwait(false);
        try { if (task != null) await task.ConfigureAwait(false); }
        finally { cts.Dispose(); }
    }
    private void CleanupG2Handlers()
    {
        _ = StopFacebookQueueAsync();
        _facebookHandlers?.Unregister(DispatcherInstance);
        _facebookHandlers = null;
        if (_profileRegistry != null && _profileStateChangedHandler != null)
        {
            _profileRegistry.ProfileStateChanged -= _profileStateChangedHandler;
            _profileStateChangedHandler = null;
        }

        if (_browserHandlers != null)
        {
            _browserHandlers.Unregister(DispatcherInstance);
            _browserHandlers = null;
        }

        _loginCoordinator?.Dispose();
        _loginCoordinator = null;
        _sessionCheckpointCoordinator?.Dispose();
        _sessionCheckpointCoordinator = null;
        _profileRegistry = null;

        if (_providerAccountHandlers != null)
        {
            _providerAccountHandlers.Unregister(DispatcherInstance);
            _providerAccountHandlers = null;
        }
        if (_promptHandlers != null)
        {
            _promptHandlers.Unregister(DispatcherInstance);
            _promptHandlers = null;
        }
        if (_projectHandlers != null)
        {
            _projectHandlers.Unregister(DispatcherInstance);
            _projectHandlers = null;
        }
        if (_sceneHandlers != null)
        {
            _sceneHandlers.Unregister(DispatcherInstance);
            _sceneHandlers = null;
        }
        _videoWorkflowHandlers = null;
        if (_settingsHandlers != null)
        {
            _settingsHandlers.Unregister(DispatcherInstance);
            _settingsHandlers = null;
        }
        if (_assetHandlers != null)
        {
            _assetHandlers.Unregister(DispatcherInstance);
            _assetHandlers = null;
        }
        if (_documentHandlers != null)
        {
            _documentHandlers.Unregister(DispatcherInstance);
            _documentHandlers = null;
        }
        if (_providerHandlers != null)
        {
            _providerHandlers.Unregister(DispatcherInstance);
            _providerHandlers = null;
        }
        if (_workflowHandlers != null)
        {
            _workflowHandlers.Unregister(DispatcherInstance);
            _workflowHandlers = null;
        }
        if (_analyticsHandlers != null)
        {
            _analyticsHandlers.Unregister(DispatcherInstance);
            _analyticsHandlers = null;
        }
        _analyticsAnalysisHandlers?.Unregister(DispatcherInstance);
        _analyticsAnalysisHandlers = null;
        _facebookAnalyticsHandlers?.Unregister(DispatcherInstance);
        _facebookAnalyticsHandlers = null;
        if (_facebookAnalyticsService != null)
        {
            _ = _facebookAnalyticsService.DisposeAsync().AsTask();
            _facebookAnalyticsService = null;
        }
        if (_publishingHandlers != null)
        {
            _publishingHandlers.Unregister(DispatcherInstance);
            _publishingHandlers = null;
        }
        if (_updateHandlers != null)
        {
            _updateHandlers.Unregister(DispatcherInstance);
            _updateHandlers = null;
        }

        if (_browserGatekeeper != null)
        {
            _browserGatekeeper.Dispose();
            _browserGatekeeper = null;
        }

        var schedulerToDispose = _jobScheduler;
        _jobScheduler = null;
        if (schedulerToDispose != null)
        {
            if (_jobStateChangedHandler != null)
            {
                schedulerToDispose.OnJobStateChanged -= _jobStateChangedHandler;
                _jobStateChangedHandler = null;
            }
            if (_jobProgressUpdatedHandler != null)
            {
                schedulerToDispose.OnJobProgressUpdated -= _jobProgressUpdatedHandler;
                _jobProgressUpdatedHandler = null;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await schedulerToDispose.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Bỏ qua lỗi dispose khi shutdown nền
                }
            });
        }

        if (_jobHandlers != null)
        {
            _jobHandlers.Unregister(DispatcherInstance);
            _jobHandlers = null;
        }
    }

    private async Task CleanupG2HandlersAsync()
    {
        await StopFacebookQueueAsync().ConfigureAwait(true);
        _analyticsAnalysisHandlers?.Unregister(DispatcherInstance);
        _analyticsAnalysisHandlers = null;
        _facebookAnalyticsHandlers?.Unregister(DispatcherInstance);
        _facebookAnalyticsHandlers = null;
        if (_facebookAnalyticsService != null)
        {
            await _facebookAnalyticsService.DisposeAsync();
            _facebookAnalyticsService = null;
        }
        _facebookHandlers?.Unregister(DispatcherInstance);
        _facebookHandlers = null;
        if (_providerAccountHandlers != null)
        {
            _providerAccountHandlers.Unregister(DispatcherInstance);
            _providerAccountHandlers = null;
        }
        if (_promptHandlers != null)
        {
            _promptHandlers.Unregister(DispatcherInstance);
            _promptHandlers = null;
        }
        if (_projectHandlers != null)
        {
            _projectHandlers.Unregister(DispatcherInstance);
            _projectHandlers = null;
        }
        if (_browserHandlers != null)
        {
            _browserHandlers.Unregister(DispatcherInstance);
            _browserHandlers = null;
        }
        if (_sceneHandlers != null)
        {
            _sceneHandlers.Unregister(DispatcherInstance);
            _sceneHandlers = null;
        }
        _videoWorkflowHandlers = null;
        if (_settingsHandlers != null)
        {
            _settingsHandlers.Unregister(DispatcherInstance);
            _settingsHandlers = null;
        }
        if (_assetHandlers != null)
        {
            _assetHandlers.Unregister(DispatcherInstance);
            _assetHandlers = null;
        }
        if (_documentHandlers != null)
        {
            _documentHandlers.Unregister(DispatcherInstance);
            _documentHandlers = null;
        }
        if (_providerHandlers != null)
        {
            _providerHandlers.Unregister(DispatcherInstance);
            _providerHandlers = null;
        }
        if (_workflowHandlers != null)
        {
            _workflowHandlers.Unregister(DispatcherInstance);
            _workflowHandlers = null;
        }
        if (_analyticsHandlers != null)
        {
            _analyticsHandlers.Unregister(DispatcherInstance);
            _analyticsHandlers = null;
        }
        if (_publishingHandlers != null)
        {
            _publishingHandlers.Unregister(DispatcherInstance);
            _publishingHandlers = null;
        }
        if (_updateHandlers != null)
        {
            _updateHandlers.Unregister(DispatcherInstance);
            _updateHandlers = null;
        }

        if (_browserGatekeeper != null)
        {
            _browserGatekeeper.Dispose();
            _browserGatekeeper = null;
        }

        if (_jobScheduler != null)
        {
            if (_jobStateChangedHandler != null)
            {
                _jobScheduler.OnJobStateChanged -= _jobStateChangedHandler;
                _jobStateChangedHandler = null;
            }
            if (_jobProgressUpdatedHandler != null)
            {
                _jobScheduler.OnJobProgressUpdated -= _jobProgressUpdatedHandler;
                _jobProgressUpdatedHandler = null;
            }
            await _jobScheduler.DisposeAsync().ConfigureAwait(false);
            _jobScheduler = null;
        }

        if (_jobHandlers != null)
        {
            _jobHandlers.Unregister(DispatcherInstance);
            _jobHandlers = null;
        }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        await CleanupG2HandlersAsync().ConfigureAwait(true);
        await InitializeStartupAsync();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;

        _startupCts?.Cancel();
        _startupCts?.Dispose();
        _startupCts = null;

        CleanupG2Handlers();

        HostManager.Dispose();
        ThemeService.Dispose();
        // Không gọi _startupLock.Dispose() tại đây để tránh ObjectDisposedException
        // nếu InitializeStartupAsync đang chạy và khối finally sẽ gọi _startupLock.Release().
        // SemaphoreSlim sẽ được bộ thu gom rác (GC) dọn dẹp an toàn theo vòng đời đối tượng.
    }
}
