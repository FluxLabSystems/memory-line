using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Sync;
using MemoryTimeline.ViewModels;
using MemoryTimeline.Views;
using MemoryTimeline.Services;

namespace MemoryTimeline;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _mainWindow;
    private IHost? _host;

    // Redirected activations can arrive (on a background thread) before the main
    // window exists during the startup race; the latest action token is parked
    // here and drained by OnLaunched once the window is up.
    private static readonly object _activationSync = new();
    private static string? _pendingActivationAction;
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MemoryTimeline",
        "error.log"
    );

    /// <summary>
    /// Writes a message to the error log file.
    /// </summary>
    private static void WriteToLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch
        {
            // Ignore logging failures
        }
    }

    /// <summary>
    /// Writes an exception to the error log file.
    /// </summary>
    private static void LogException(string context, Exception ex)
    {
        var message = $"=== {context} ===\n{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";

        if (ex.InnerException != null)
        {
            message += $"\n\nInner Exception:\n{ex.InnerException.GetType().Name}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}";
        }

        WriteToLog(message);
    }

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        WriteToLog("App constructor starting...");

        try
        {
            InitializeComponent();
            WriteToLog("InitializeComponent completed");

            // Set up global exception handlers
            UnhandledException += App_UnhandledException;
            System.AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            // Configure dependency injection
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    // Database access goes through a context factory: every operation opens a
                    // short-lived AppDbContext, so no context is ever shared across features
                    // or threads (the audit's "captive DbContext" fix). Connection config
                    // lives in AppDbContext.OnConfiguring.
                    services.AddDbContextFactory<AppDbContext>();

                    // Repositories are stateless over the singleton factory.
                    services.AddSingleton<IEventRepository, EventRepository>();
                    services.AddSingleton<IEraRepository, EraRepository>();
                    services.AddSingleton<IEraCategoryRepository, EraCategoryRepository>();
                    services.AddSingleton<IMilestoneRepository, MilestoneRepository>();
                    services.AddSingleton<IRecordingQueueRepository, RecordingQueueRepository>();
                    services.AddSingleton<ITagRepository, TagRepository>();
                    services.AddSingleton<IPersonRepository, PersonRepository>();
                    services.AddSingleton<ILocationRepository, LocationRepository>();
                    services.AddSingleton<ICrossReferenceRepository, CrossReferenceRepository>();
                    services.AddSingleton<IEventEmbeddingRepository, EventEmbeddingRepository>();
                    services.AddSingleton<IAppSettingRepository, AppSettingRepository>();
                    services.AddSingleton<IPendingEventRepository, PendingEventRepository>();
                    services.AddSingleton<IDraftRepository, DraftRepository>();
                    services.AddSingleton<IEventMediaRepository, EventMediaRepository>();
                    services.AddSingleton<IRecallPromptRepository, RecallPromptRepository>();
                    services.AddSingleton<IEventRevisionRepository, EventRevisionRepository>();
                    services.AddSingleton<ICaptureRepository, CaptureRepository>();
                    services.AddSingleton<ISyncOutboxRepository, SyncOutboxRepository>();

                    // Register core services (stateless over the factory/repositories)
                    // Secrets at rest: SettingsService stores API keys and sync tokens
                    // through this port (user-scoped DPAPI on Windows); Core defaults to
                    // the passthrough when nothing is registered.
                    services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
                    services.AddSingleton<ISettingsService, SettingsService>();
                    // Revision history (F12): the writer is an optional ctor
                    // dependency of EventService/EventExtractionService/
                    // ImportService (registered => injected, absent => null);
                    // RevisionService restores prior versions.
                    services.AddSingleton<EventRevisionWriter>();
                    services.AddSingleton<IRevisionService, RevisionService>();
                    services.AddSingleton<IEventService, EventService>();
                    services.AddSingleton<ITimelineService, TimelineService>();
                    services.AddSingleton<IPersonService, PersonService>();
                    services.AddSingleton<IDraftService, DraftService>();
                    // Media attachments (F2): thumbnails are generated by the
                    // app-side WinRT implementation; MediaService itself is
                    // UI-free and lives in Core.
                    services.AddSingleton<IThumbnailGenerator, WindowsThumbnailGenerator>();
                    // Recording files (recorder output, synced artifacts, normalized
                    // WAVs): released when a queue row is removed, swept by Clear Cache.
                    services.AddSingleton<IRecordingStorage, RecordingStorage>();
                    services.AddSingleton<IMediaService, MediaService>();

                    // Phase 3: Audio & Queue services
                    services.AddSingleton<IAudioRecordingService, AudioRecordingService>();
                    services.AddSingleton<IAudioPlaybackService, AudioPlaybackService>();
                    services.AddSingleton<IQueueService, QueueService>();
                    // Whisper transcribes the recorded file locally; the Windows
                    // SpeechRecognizer cannot transcribe files (mic-only API).
                    services.AddSingleton<ISpeechToTextService, WhisperSpeechToTextService>();

                    // iOS companion Phase 0: capture ingestion (design §7.2). Every
                    // recording — local or from another device — enters the queue
                    // through this seam. The Phase 0 resolver only handles local
                    // file-path locators; Phase 1 swaps in a sync download client.
                    services.AddSingleton<IArtifactResolver, LocalFileArtifactResolver>();
                    // Design §8.5: ingestion normalizes any non-16 kHz-WAV source
                    // (an iPhone's AAC .m4a, an imported MP3) through this port
                    // before queueing it; Media Foundation does the transcoding.
                    services.AddSingleton<IAudioNormalizer, WindowsAudioNormalizer>();
                    services.AddSingleton<ICaptureIngestionService, CaptureIngestionService>();

                    // iOS companion Phase 1: sync client + background worker (design §7.4).
                    // One shared HttpClient for the sync endpoints; auth headers are set
                    // per request, so a single instance is safe app-wide.
                    services.AddSingleton(_ => new HttpClient());
                    services.AddSingleton<ISyncSettingsStore, SyncSettingsStore>();
                    services.AddSingleton<ISyncCursorStore, SettingsSyncCursorStore>();
                    services.AddSingleton<ISyncClient, SyncApiClient>();
                    services.AddSingleton<IArtifactTransferClient, ArtifactTransferClient>();
                    services.AddSingleton<IRemoteChangeApplier, RemoteChangeApplier>();
                    // Phase 3 status handoff. Both consumers (QueueService,
                    // CaptureIngestionService) take this as an optional
                    // constructor parameter defaulting to null, so WITHOUT this
                    // registration the app still starts and silently publishes
                    // no capture_status at all — the phone would never learn
                    // what happened to a capture after upload.
                    services.AddSingleton<ICaptureStatusPublisher, CaptureStatusPublisher>();
                    // Phase 3 timeline projection: the read-only archive feed a
                    // companion renders from, and the one write that comes back.
                    // RemoteChangeApplier takes the decision applier as an
                    // optional parameter, so without this registration a
                    // companion's approve/reject is silently skipped instead of
                    // failing the sync cycle.
                    services.AddSingleton<ITimelineProjectionPublisher, TimelineProjectionPublisher>();
                    services.AddSingleton<IPendingEventDecisionApplier, PendingEventDecisionApplier>();
                    services.AddSingleton<ILocalOutboxPublisher, LocalOutboxPublisher>();
                    services.AddSingleton<ISyncBackgroundWorker, SyncBackgroundWorker>();

                    // Phase 4: LLM & Event Extraction services.
                    // F11 pluggable providers: concrete LLM services are registered as
                    // themselves (AnthropicLlmService singleton; OpenAiCompatibleLlmService
                    // as an HttpClient typed client, i.e. transient with factory-managed
                    // HttpClient). The RoutingLlmService facade re-reads llm_provider on
                    // every call and resolves the matching concrete per call, so switching
                    // providers in Settings applies without a restart and HttpClient
                    // lifetimes stay healthy.
                    services.AddSingleton<AnthropicLlmService>();
                    services.AddHttpClient<OpenAiCompatibleLlmService>();
                    services.AddSingleton<ILlmUsageTracker, LlmUsageTracker>();
                    services.AddSingleton<ILlmService, RoutingLlmService>();
                    services.AddSingleton<IEventExtractionService, EventExtractionService>();

                    // Phase 5: RAG & Embedding services.
                    // F11 local-first embeddings: same routing shape as the LLM —
                    // OnnxEmbeddingService (local, no key, seeded default) is a singleton;
                    // OpenAIEmbeddingService stays a typed HttpClient; the router picks
                    // per call from embedding_provider.
                    services.AddHttpClient<OpenAIEmbeddingService>();
                    services.AddSingleton<OnnxEmbeddingService>();
                    services.AddSingleton<IEmbeddingService, RoutingEmbeddingService>();
                    services.AddSingleton<IRagService, RagService>();
                    // Ask your timeline: conversational retrieval over the archive
                    services.AddSingleton<IMemoryQueryService, MemoryQueryService>();
                    // On This Day / random-memory resurfacing (Home page + daily toast)
                    services.AddSingleton<IResurfacingService, ResurfacingService>();
                    // Narrative generation: readable prose chapters from timeline scopes
                    services.AddSingleton<INarrativeService, NarrativeService>();
                    // Guided recall: prompts generated from archive gaps (F6)
                    services.AddSingleton<IRecallPromptService, RecallPromptService>();
                    // Geographic data (F10): opt-in Nominatim geocoding as a
                    // typed HttpClient (same pattern as OpenAIEmbeddingService;
                    // a plain AddSingleton would bypass the HttpClient factory).
                    services.AddHttpClient<IGeocodingService, NominatimGeocodingService>();

                    // Phase 6: Export/Import & Windows Integration services
                    services.AddSingleton<IExportService, ExportService>();
                    services.AddSingleton<IImportService, ImportService>();
                    // User backups (.mtbak) + restore + scheduled backup (F12)
                    services.AddSingleton<IBackupService, BackupService>();
                    services.AddSingleton<INotificationService, Services.NotificationService>();
                    services.AddSingleton<IWindowsTimelineService, WindowsTimelineService>();
                    services.AddSingleton<IJumpListService, JumpListService>();

                    // Phase 7: Advanced Search, Analytics, & Audio Import services
                    services.AddSingleton<IAdvancedSearchService, AdvancedSearchService>();
                    services.AddSingleton<IAnalyticsService, AnalyticsService>();
                    services.AddSingleton<IAudioImportService, AudioImportService>();

                    // Register app services
                    services.AddSingleton<INavigationService, NavigationService>();
                    services.AddSingleton<IThemeService, ThemeService>();

                    // Register ViewModels
                    services.AddTransient<MainViewModel>();
                    services.AddTransient<HomeViewModel>();
                    services.AddSingleton<TimelineViewModel>(); // Singleton to preserve state across navigation
                    services.AddTransient<SettingsViewModel>();
                    services.AddTransient<QueueViewModel>();
                    services.AddTransient<ReviewViewModel>();
                    services.AddTransient<ConnectionsViewModel>();
                    services.AddTransient<SearchViewModel>();
                    services.AddTransient<AnalyticsViewModel>();
                    services.AddTransient<ErasViewModel>();
                    services.AddTransient<ContactsViewModel>();
                    services.AddTransient<MapViewModel>();
                    services.AddTransient<AskViewModel>();
                    services.AddTransient<NarrativeDialogViewModel>();

                    // Register Views. Pages are NOT registered: Frame.Navigate creates them
                    // via Activator, so page registrations were dead code; each page pulls
                    // its ViewModel from App.Current.Services in its constructor.
                    services.AddTransient<MainWindow>();
                })
                .Build();

            WriteToLog("Host built successfully");
        }
        catch (Exception ex)
        {
            LogException("App Constructor Failed", ex);
            throw; // Re-throw to let the app crash (but now we have a log)
        }
    }

    private async void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            WriteToLog("Main window closed - stopping host...");
            if (_host != null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
            }
            WriteToLog("Host stopped and disposed");
        }
        catch (Exception ex)
        {
            LogException("Host shutdown on window close failed", ex);
        }
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogException("Unhandled UI Exception", e.Exception);
        e.Handled = false; // Let it crash, but we have the log
    }

    private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogException("AppDomain Unhandled Exception", ex);
        }
        else
        {
            WriteToLog($"AppDomain Unhandled Exception (non-Exception): {e.ExceptionObject}");
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("Unobserved Task Exception", e.Exception);
    }

    /// <summary>
    /// Gets the current <see cref="App"/> instance in use
    /// </summary>
    public new static App Current => (App)Application.Current;

    /// <summary>
    /// Gets the <see cref="IServiceProvider"/> instance to resolve application services.
    /// </summary>
    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Host not initialized");

    /// <summary>
    /// Gets the main application window.
    /// </summary>
    public Window? Window => _mainWindow;

    /// <summary>
    /// Entry point for activations redirected from secondary instances
    /// (see Program.OnRedirectedActivation). Called on a background thread:
    /// marshals to the UI thread via the main window's DispatcherQueue, brings
    /// the window to the foreground, and routes the action token through the
    /// same path OnLaunched uses. If the window does not exist yet, the token
    /// is queued for OnLaunched to drain (a token-less activation is safely
    /// ignored — the window is about to appear anyway).
    /// </summary>
    internal static void OnRedirectedActivation(string? actionToken)
    {
        var app = Application.Current as App;
        var dispatcher = app?._mainWindow?.DispatcherQueue;

        if (app == null || dispatcher == null)
        {
            if (!string.IsNullOrEmpty(actionToken))
            {
                lock (_activationSync)
                {
                    _pendingActivationAction = actionToken;
                }
            }

            WriteToLog($"Redirected activation before window ready - queued action: {actionToken ?? "(none)"}");
            return;
        }

        var enqueued = dispatcher.TryEnqueue(() =>
        {
            try
            {
                app.BringMainWindowToForeground();

                if (!string.IsNullOrEmpty(actionToken))
                {
                    app.HandleActivationAction(actionToken);
                }
            }
            catch (Exception ex)
            {
                LogException("Redirected activation dispatch failed", ex);
            }
        });

        if (!enqueued)
        {
            WriteToLog("Redirected activation could not be enqueued to the UI thread (dispatcher shutting down)");
        }
    }

    /// <summary>
    /// Restores the main window if minimized and brings it to the foreground.
    /// Must run on the UI thread.
    /// </summary>
    private void BringMainWindowToForeground()
    {
        if (_mainWindow == null)
        {
            return;
        }

        try
        {
            if (_mainWindow.AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
                presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }
        }
        catch (Exception ex)
        {
            LogException("Restoring minimized window failed", ex);
        }

        _mainWindow.Activate();
    }

    /// <summary>
    /// Routes an activation token to the matching page: "action:" quick
    /// actions navigate to their page, and "event:{id}" deep links (Jump List
    /// recent events, toast taps) open the event on the timeline. Shared by
    /// OnLaunched (cold start), OnRedirectedActivation (activation redirected
    /// from a secondary instance), and toast invocation. Must run on the UI
    /// thread. Unknown tokens are ignored; navigation failures are logged,
    /// never thrown.
    /// </summary>
    internal void HandleActivationAction(string actionToken)
    {
        try
        {
            if (actionToken.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                var eventId = actionToken.Substring("event:".Length).Trim();
                if (!string.IsNullOrEmpty(eventId))
                {
                    // TimelinePage.OnNavigatedTo accepts an event id parameter
                    // and moves the viewport to that event.
                    var eventNavigationService = Services.GetRequiredService<INavigationService>();
                    eventNavigationService.NavigateTo("Timeline", eventId);
                }
                return;
            }

            var target = actionToken.ToLowerInvariant() switch
            {
                "action:new-event" or "action:view-timeline" => "Timeline",
                "action:start-recording" => "Queue",
                "action:search" => "Search",
                _ => null
            };

            if (target != null)
            {
                var navigationService = Services.GetRequiredService<INavigationService>();
                navigationService.NavigateTo(target);
            }
        }
        catch (Exception ex)
        {
            LogException("Activation action navigation failed", ex);
        }
    }

    /// <summary>
    /// Shows the daily "On This Day" toast: at most once per calendar day
    /// (guarded by the last_toast_date setting), only while daily_toast_enabled,
    /// and only when at least one memory matches today. The guard date is
    /// stamped BEFORE showing so a Show failure cannot cause repeat toasts.
    /// Local dates throughout - "today" is the day the user is living in.
    /// </summary>
    private async Task ShowDailyOnThisDayToastAsync()
    {
        var settingsService = Services.GetRequiredService<ISettingsService>();
        var enabledSetting = await settingsService.GetSettingAsync<string>(SettingKeys.DailyToastEnabled, "true");
        var lastToastDate = await settingsService.GetSettingAsync<string>(SettingKeys.LastToastDate, string.Empty);
        // Invariant culture: the stored guard value must be a stable Gregorian
        // yyyy-MM-dd regardless of the user's culture/calendar.
        var today = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        if (!ResurfacingService.ShouldShowToast(lastToastDate, today, enabledSetting))
        {
            return;
        }

        var resurfacingService = Services.GetRequiredService<IResurfacingService>();
        var memories = await resurfacingService.GetOnThisDayAsync();
        if (memories.Count == 0)
        {
            return;
        }

        // Stamp before showing: even if Show fails, we do not retry until tomorrow.
        await settingsService.SetSettingAsync(SettingKeys.LastToastDate, today);

        var top = memories[0];
        var yearsText = top.YearsAgo == 1 ? "1 year ago" : $"{top.YearsAgo} years ago";
        var notificationService = Services.GetRequiredService<INotificationService>();
        await notificationService.ShowEventNotificationAsync(
            "On This Day",
            $"{top.Title}, {yearsText}",
            top.EventId);
    }

    /// <summary>
    /// Runs a scheduled backup when one is due: backup_schedule is
    /// "daily"/"weekly", backup_destination is set, and last_backup_at is
    /// absent or older than the cadence (the pure decision lives in
    /// <see cref="BackupService.ShouldRunScheduledBackup"/>). On success the
    /// last_backup_at stamp is written in invariant round-trip ("o") format.
    /// The single backup_include_media setting governs both media and audio
    /// for scheduled backups.
    /// </summary>
    private async Task RunScheduledBackupIfDueAsync()
    {
        var settingsService = Services.GetRequiredService<ISettingsService>();

        var schedule = await settingsService.GetSettingAsync<string>(SettingKeys.BackupSchedule, "off");
        var destination = await settingsService.GetSettingAsync<string>(SettingKeys.BackupDestination, string.Empty);
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        DateTime? lastBackupAt = null;
        var lastBackupRaw = await settingsService.GetSettingAsync<string>(SettingKeys.LastBackupAt, string.Empty);
        if (DateTime.TryParse(
                lastBackupRaw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsedLastBackup))
        {
            lastBackupAt = parsedLastBackup.ToUniversalTime();
        }

        if (!BackupService.ShouldRunScheduledBackup(schedule, lastBackupAt, DateTime.UtcNow))
        {
            return;
        }

        var includeMediaSetting = await settingsService.GetSettingAsync<string>(SettingKeys.BackupIncludeMedia, "true");
        var includeMedia = !string.Equals(includeMediaSetting, "false", StringComparison.OrdinalIgnoreCase);

        var backupService = Services.GetRequiredService<IBackupService>();
        var backupPath = Path.Combine(destination, $"MemoryTimeline-{DateTime.Now:yyyyMMdd-HHmmss}.mtbak");
        var result = await backupService.CreateBackupAsync(
            backupPath,
            new BackupOptions { IncludeMedia = includeMedia, IncludeAudio = includeMedia });

        await settingsService.SetSettingAsync(
            SettingKeys.LastBackupAt,
            DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));

        WriteToLog($"Scheduled backup written to {result.FilePath} ({result.EventCount} events, {result.SizeBytes} bytes)");
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        WriteToLog("OnLaunched starting...");

        try
        {
            if (_host == null)
            {
                throw new InvalidOperationException("Host was not initialized in constructor");
            }

            WriteToLog("Starting host...");
            await _host.StartAsync();
            WriteToLog("Host started");

            // Ensure the database exists and repair schema drift on databases created
            // by older builds (SchemaUpgrader = EnsureCreated + idempotent DDL repairs).
            WriteToLog("Ensuring database schema...");
            var contextFactory = Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using (var dbContext = await contextFactory.CreateDbContextAsync())
            {
                var schemaLogger = Services.GetRequiredService<ILoggerFactory>().CreateLogger("SchemaUpgrader");
                await SchemaUpgrader.EnsureSchemaAsync(dbContext, schemaLogger);
            }
            WriteToLog("Database ready");

            // Start the sync background loop (iOS companion Phase 1) once the schema
            // is guaranteed. Safe no-op while sync is disabled or the device is not
            // paired; kept non-fatal because sync must never break app launch.
            try
            {
                var syncWorker = Services.GetRequiredService<ISyncBackgroundWorker>();
                syncWorker.Start();
                WriteToLog("Sync background worker started");
            }
            catch (Exception syncEx)
            {
                LogException("Sync background worker startup failed", syncEx);
            }

            WriteToLog("Creating MainWindow...");
            _mainWindow = Services.GetRequiredService<MainWindow>();

            // Dispose the host when the main window closes so singleton services get a
            // chance to release native resources (microphone, notification registration).
            _mainWindow.Closed += OnMainWindowClosed;

            WriteToLog("Activating MainWindow...");
            _mainWindow.Activate();
            WriteToLog("MainWindow activated");

            // Initialize theme
            WriteToLog("Initializing theme...");
            var themeService = Services.GetRequiredService<IThemeService>();
            await themeService.InitializeAsync();
            WriteToLog("Theme initialized - startup complete!");

            // Populate JumpList quick actions (no-ops where unsupported).
            try
            {
                var jumpListService = Services.GetRequiredService<IJumpListService>();
                await jumpListService.AddQuickActionsAsync();
            }
            catch (Exception jumpListEx)
            {
                LogException("JumpList initialization failed", jumpListEx);
            }

            // Honor a jump-list / command-line launch action captured by Program.Main.
            var launchAction = Program.LaunchAction;
            if (!string.IsNullOrEmpty(launchAction))
            {
                HandleActivationAction(launchAction);
            }

            // Drain any activation redirected here before the window existed
            // (queued by OnRedirectedActivation during the startup race).
            string? pendingAction;
            lock (_activationSync)
            {
                pendingAction = _pendingActivationAction;
                _pendingActivationAction = null;
            }
            if (!string.IsNullOrEmpty(pendingAction))
            {
                HandleActivationAction(pendingAction);
            }

            // Daily "On This Day" toast (at most once per calendar day). Its
            // own try/catch: a toast failure must never affect startup.
            try
            {
                await ShowDailyOnThisDayToastAsync();
            }
            catch (Exception toastEx)
            {
                LogException("On This Day launch toast failed", toastEx);
            }

            // Scheduled backup (F12): fire-and-forget after the schema is
            // ready - a slow or failing backup must never block or crash
            // startup. Runs only when a schedule is active, a destination is
            // configured, and the last backup is older than the cadence.
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunScheduledBackupIfDueAsync();
                }
                catch (Exception backupEx)
                {
                    LogException("Scheduled backup failed", backupEx);
                }
            });
        }
        catch (Exception ex)
        {
            LogException("OnLaunched Failed", ex);

            // Try to show an error dialog if possible
            try
            {
                // Create a minimal window with content for the dialog
                if (_mainWindow == null)
                {
                    _mainWindow = new Window();
                    _mainWindow.Content = new Grid();
                    _mainWindow.Activate();
                }

                if (_mainWindow.Content?.XamlRoot != null)
                {
                    var errorMessage = $"Failed to start Memory Timeline:\n\n{ex.Message}";
                    if (ex.InnerException != null)
                    {
                        errorMessage += $"\n\nInner: {ex.InnerException.Message}";
                    }
                    errorMessage += $"\n\nSee log file for details:\n{LogPath}";

                    var dialog = new ContentDialog
                    {
                        Title = "Startup Error",
                        Content = new ScrollViewer
                        {
                            Content = new TextBlock
                            {
                                Text = errorMessage,
                                TextWrapping = TextWrapping.Wrap,
                                IsTextSelectionEnabled = true
                            },
                            MaxHeight = 400
                        },
                        CloseButtonText = "Exit",
                        XamlRoot = _mainWindow.Content.XamlRoot
                    };

                    await dialog.ShowAsync();
                }
            }
            catch (Exception dialogEx)
            {
                LogException("Error Dialog Failed", dialogEx);
            }

            // Exit the application
            Environment.Exit(1);
        }
    }
}
