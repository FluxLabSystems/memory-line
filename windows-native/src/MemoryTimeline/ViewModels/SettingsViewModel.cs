using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Services;
using MemoryTimeline.Sync;
using MemoryTimeline.SyncContracts;
using System.Collections.ObjectModel;
using System.Reflection;
using Windows.Storage.Pickers;
using Windows.Storage;
using WinRT.Interop;

namespace MemoryTimeline.ViewModels;

/// <summary>
/// ViewModel for settings page.
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IExportService _exportService;
    private readonly IImportService _importService;
    private readonly IEventService _eventService;
    private readonly ILlmUsageTracker _llmUsageTracker;
    private readonly IBackupService _backupService;
    private readonly IMediaService _mediaService;
    private readonly IAudioRecordingService _audioRecordingService;
    private readonly ISyncClient _syncClient;
    private readonly ISyncSettingsStore _syncSettingsStore;
    private readonly ISyncBackgroundWorker _syncBackgroundWorker;
    private readonly ILogger<SettingsViewModel> _logger;

    // Captured on construction (UI thread) so sync worker events arriving on
    // background threads can marshal UI mutations back onto the dispatcher.
    private readonly DispatcherQueue? _dispatcherQueue;
    private bool _disposed;

    // True while persisted sync values are being loaded into the observable
    // properties, so the persist-on-change handlers do not write them back.
    private bool _suppressSyncPersistence;

    // Embedding provider persisted at load time; a differing selection shows
    // the re-embed warning until "Re-embed all events" completes.
    private string _loadedEmbeddingProviderKey = EmbeddingProviderKeys.Local;
    private CancellationTokenSource? _reEmbedCts;

    // Suppresses persist-on-change while InitializeAsync loads stored values.
    private bool _isLoadingBackupSettings;

    [ObservableProperty]
    private string _selectedTheme = "System";

    [ObservableProperty]
    private string _selectedZoomLevel = "Month";

    [ObservableProperty]
    private string _llmProvider = "Anthropic";

    [ObservableProperty]
    private string _llmModel = SeededLlmModel;

    [ObservableProperty]
    private string _llmBaseUrl = string.Empty;

    [ObservableProperty]
    private string _selectedEmbeddingProvider = "Local (on-device)";

    [ObservableProperty]
    private bool _showReEmbedWarning;

    [ObservableProperty]
    private bool _isReEmbedding;

    [ObservableProperty]
    private string _reEmbedStatusMessage = string.Empty;

    [ObservableProperty]
    private string _llmUsageSummary = "No LLM calls this session";

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private string _embeddingApiKey = string.Empty;

    // ---- Audio capture ----

    /// <summary>
    /// Input devices offered in the Microphone picker. Index 0 is always the
    /// "System default" sentinel (empty DeviceId); the rest come from
    /// IAudioRecordingService.GetAvailableDevicesAsync.
    /// </summary>
    public ObservableCollection<AudioDeviceDto> AudioInputDevices { get; } = new();

    /// <summary>Selected microphone; persisted as audio_input_device_id on Save ("" = system default).</summary>
    [ObservableProperty]
    private AudioDeviceDto? _selectedAudioInputDevice;

    /// <summary>Caption under the picker: how many devices were found, or why the saved one is not selected.</summary>
    [ObservableProperty]
    private string _audioDeviceStatus = string.Empty;

    /// <summary>
    /// The one recording format the app captures in. It is deliberately not a
    /// setting: the local Whisper engine takes exactly 16 kHz, 16-bit, mono
    /// PCM WAV, and QueueViewModel.StartRecordingAsync records in that format.
    /// </summary>
    public string RecordingFormatSummary => "16 kHz, 16-bit, mono WAV";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private int _exportProgress;

    [ObservableProperty]
    private string _exportStatusMessage = string.Empty;

    [ObservableProperty]
    private string _importStatusMessage = string.Empty;

    // ---- Backup & restore (F12, Data Management card) ----

    [ObservableProperty]
    private string _backupDestination = string.Empty;

    [ObservableProperty]
    private string _selectedBackupSchedule = "Off";

    [ObservableProperty]
    private bool _includeMediaInBackup = true;

    // Session-only by design: the only persisted include toggle is
    // backup_include_media (which also governs audio for SCHEDULED backups);
    // this checkbox refines a manual "Back Up Now" run.
    [ObservableProperty]
    private bool _includeAudioInBackup = true;

    [ObservableProperty]
    private string _lastBackupDisplay = "Last backup: never";

    [ObservableProperty]
    private bool _isBackingUp;

    [ObservableProperty]
    private int _backupProgress;

    [ObservableProperty]
    private string _backupStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _isRestoring;

    public List<string> BackupScheduleOptions { get; } = new() { "Off", "Daily", "Weekly" };

    // About information
    public string AppName => "Memory Timeline";
    public string AppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
    public string BuildDate => File.GetLastWriteTime(Assembly.GetExecutingAssembly().Location).ToString("yyyy-MM-dd");

    // Seeded defaults for "Reset to Defaults", mirrored from
    // AppDbContext.SeedDefaultSettings (theme "light", default_zoom_level
    // "month", llm_provider "anthropic", llm_model "claude-sonnet-4-20250514",
    // llm_base_url "", embedding_provider "local", audio_input_device_id "").
    // Core does not expose the seed values as constants, so keep these in step
    // with that seed when it changes.
    private const string SeededThemeDisplay = "Light";
    private const string SeededZoomLevelDisplay = "Month";
    private const string SeededLlmProviderDisplay = "Anthropic";
    private const string SeededLlmModel = "claude-sonnet-4-20250514";
    private const string SeededEmbeddingProviderDisplay = "Local (on-device)";

    // Available options
    public List<string> ThemeOptions { get; } = new() { "System", "Light", "Dark", "Solarized Dark" };
    public List<string> ZoomLevelOptions { get; } = new() { "Year", "Month", "Week", "Day" };
    // Display values lowercase to the canonical stored keys on save
    // ("anthropic" / "openai-compatible" — see LlmProviderKeys). Only list
    // providers RoutingLlmService actually implements: persisting an
    // unsupported provider used to break all event extraction.
    public List<string> LlmProviderOptions { get; } = new() { "Anthropic", "OpenAI-compatible" };
    public List<string> EmbeddingProviderOptions { get; } = new() { "Local (on-device)", "OpenAI (cloud)" };

    /// <summary>
    /// Plain-language privacy summary: exactly what each provider sends where.
    /// </summary>
    public string PrivacySummary =>
        "Privacy: Anthropic (LLM) receives your transcripts and event text for extraction and narration. " +
        "OpenAI (embeddings) receives event titles and descriptions to compute similarity vectors. " +
        "Local (on-device) embeddings send nothing — no text leaves this machine. " +
        "An OpenAI-compatible endpoint sends text to whatever server the base URL points at (e.g. Ollama on localhost stays local).";

    public SettingsViewModel(
        ISettingsService settingsService,
        IThemeService themeService,
        IExportService exportService,
        IImportService importService,
        IEventService eventService,
        ILlmUsageTracker llmUsageTracker,
        IBackupService backupService,
        IMediaService mediaService,
        IAudioRecordingService audioRecordingService,
        ISyncClient syncClient,
        ISyncSettingsStore syncSettingsStore,
        ISyncBackgroundWorker syncBackgroundWorker,
        ILogger<SettingsViewModel> logger)
    {
        _settingsService = settingsService;
        _themeService = themeService;
        _exportService = exportService;
        _importService = importService;
        _eventService = eventService;
        _llmUsageTracker = llmUsageTracker;
        _backupService = backupService;
        _mediaService = mediaService;
        _audioRecordingService = audioRecordingService;
        _syncClient = syncClient;
        _syncSettingsStore = syncSettingsStore;
        _syncBackgroundWorker = syncBackgroundWorker;
        _logger = logger;

        // Capture the UI dispatcher while we are still on the UI thread.
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // NOTE: the sync worker is a DI singleton while this ViewModel is transient;
        // SettingsPage disposes the VM on navigation away so this subscription does
        // not root the ViewModel for the app lifetime.
        _syncBackgroundWorker.SyncStatusChanged += OnSyncStatusChanged;
    }

    /// <summary>
    /// Initializes the settings view.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            // Load current settings - convert stored theme to display format
            var storedTheme = await _settingsService.GetThemeAsync();
            SelectedTheme = storedTheme?.ToLowerInvariant() switch
            {
                "light" => "Light",
                "dark" => "Dark",
                "solarized-dark" => "Solarized Dark",
                _ => "System"
            };
            // Stored values are canonical lowercase ("month", "anthropic");
            // normalize to the Title-case display values used by the ComboBoxes
            // so the selection is not blank on load.
            var storedZoomLevel = await _settingsService.GetDefaultZoomLevelAsync();
            SelectedZoomLevel = ToDisplayOption(storedZoomLevel, ZoomLevelOptions, "Month");

            // Normalize aliases ("ollama", "openai_compatible") through the
            // canonical keys before mapping to a display option.
            var storedProvider = LlmProviderKeys.Normalize(await _settingsService.GetLlmProviderAsync());
            LlmProvider = storedProvider == LlmProviderKeys.OpenAiCompatible
                ? "OpenAI-compatible"
                : "Anthropic";

            LlmModel = await _settingsService.GetLlmModelAsync();
            LlmBaseUrl = await _settingsService.GetSettingAsync<string>(SettingKeys.LlmBaseUrl, string.Empty) ?? string.Empty;

            // Embedding provider ('local' is the seeded default; unknown stored
            // values normalize to it)
            _loadedEmbeddingProviderKey = EmbeddingProviderKeys.Normalize(
                await _settingsService.GetSettingAsync<string>(SettingKeys.EmbeddingProvider, EmbeddingProviderKeys.Local));
            SelectedEmbeddingProvider = _loadedEmbeddingProviderKey == EmbeddingProviderKeys.OpenAi
                ? "OpenAI (cloud)"
                : "Local (on-device)";
            ShowReEmbedWarning = false;

            UpdateLlmUsageSummary();

            // Microphone picker (the recording format itself is fixed; see
            // RecordingFormatSummary).
            await LoadAudioInputDevicesAsync();

            // Load API key (masked)
            var apiKey = await _settingsService.GetSettingAsync<string>(SettingKeys.AnthropicApiKey, string.Empty);
            ApiKey = !string.IsNullOrEmpty(apiKey) ? "••••••••" : string.Empty;

            // Load embedding API key (masked)
            var embeddingApiKey = await _settingsService.GetSettingAsync<string>(SettingKeys.EmbeddingApiKey, string.Empty);
            EmbeddingApiKey = !string.IsNullOrEmpty(embeddingApiKey) ? "••••••••" : string.Empty;

            // Load backup settings (F12). The loading flag suppresses the
            // persist-on-change handlers while stored values stream in.
            _isLoadingBackupSettings = true;
            try
            {
                BackupDestination = await _settingsService.GetSettingAsync<string>(
                    SettingKeys.BackupDestination, string.Empty) ?? string.Empty;

                var storedSchedule = await _settingsService.GetSettingAsync<string>(
                    SettingKeys.BackupSchedule, "off");
                SelectedBackupSchedule = ToDisplayOption(storedSchedule, BackupScheduleOptions, "Off");

                var includeMedia = await _settingsService.GetSettingAsync<string>(
                    SettingKeys.BackupIncludeMedia, "true");
                IncludeMediaInBackup = !string.Equals(includeMedia, "false", StringComparison.OrdinalIgnoreCase);

                await RefreshLastBackupDisplayAsync();
            }
            finally
            {
                _isLoadingBackupSettings = false;
            }

            // Load sync configuration and pairing state
            await LoadSyncSettingsAsync();

            StatusMessage = "Settings loaded";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading settings");
            StatusMessage = "Error loading settings";
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (IsSaving) return;

        try
        {
            IsSaving = true;
            StatusMessage = "Saving settings...";

            // Save theme - convert display format to storage format
            var themeToStore = SelectedTheme switch
            {
                "Light" => "light",
                "Dark" => "dark",
                "Solarized Dark" => "solarized-dark",
                _ => "system"
            };
            await _settingsService.SetThemeAsync(themeToStore);
            await _themeService.SetThemeAsync(SelectedTheme switch
            {
                "Light" => AppTheme.Light,
                "Dark" => AppTheme.Dark,
                "Solarized Dark" => AppTheme.SolarizedDark,
                _ => AppTheme.System
            });

            // Save zoom level (canonical lowercase to match readers/seeds, e.g. "month")
            await _settingsService.SetSettingAsync(SettingKeys.DefaultZoomLevel, SelectedZoomLevel.ToLowerInvariant());

            // Save LLM settings (canonical lowercase provider keys:
            // "anthropic" / "openai-compatible" — see LlmProviderKeys)
            await _settingsService.SetSettingAsync(SettingKeys.LlmProvider, LlmProvider.ToLowerInvariant());
            await _settingsService.SetSettingAsync(SettingKeys.LlmModel, LlmModel);
            await _settingsService.SetSettingAsync(SettingKeys.LlmBaseUrl, LlmBaseUrl?.Trim() ?? string.Empty);

            // Save embedding provider (canonical "local" / "openai"). The
            // re-embed warning intentionally stays visible after saving: the
            // stored embeddings are still from the previous provider until
            // "Re-embed all events" runs.
            await _settingsService.SetSettingAsync(SettingKeys.EmbeddingProvider, ToEmbeddingProviderKey(SelectedEmbeddingProvider));

            // Save the microphone choice ("" = system default). Read by
            // QueueViewModel.StartRecordingAsync on the next recording.
            await _settingsService.SetSettingAsync(
                SettingKeys.AudioInputDeviceId, SelectedAudioInputDevice?.DeviceId ?? string.Empty);

            // Save API key (only if changed)
            if (!string.IsNullOrEmpty(ApiKey) && ApiKey != "••••••••")
            {
                await _settingsService.SetSettingAsync(SettingKeys.AnthropicApiKey, ApiKey);
            }

            // Save embedding API key (only if changed)
            if (!string.IsNullOrEmpty(EmbeddingApiKey) && EmbeddingApiKey != "••••••••")
            {
                await _settingsService.SetSettingAsync(SettingKeys.EmbeddingApiKey, EmbeddingApiKey);
            }

            StatusMessage = "Settings saved successfully";
            UpdateLlmUsageSummary();
            _logger.LogInformation("Settings saved successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving settings");
            StatusMessage = "Error saving settings";
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>
    /// Resets every setting on this page to the database seed defaults (not
    /// an arbitrary "Dark"/old-model set) and removes both stored API keys.
    /// Save skips blank key fields on purpose (a blank box means "unchanged"),
    /// so the keys are cleared explicitly before saving.
    /// </summary>
    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        try
        {
            SelectedTheme = SeededThemeDisplay;
            SelectedZoomLevel = SeededZoomLevelDisplay;
            LlmProvider = SeededLlmProviderDisplay;
            LlmModel = SeededLlmModel;
            LlmBaseUrl = string.Empty;
            SelectedEmbeddingProvider = SeededEmbeddingProviderDisplay;
            SelectedAudioInputDevice = AudioInputDevices.FirstOrDefault();

            await ClearStoredApiKeyAsync();
            await ClearStoredEmbeddingApiKeyAsync();

            await SaveSettingsAsync();
            StatusMessage = "Settings reset to defaults (API keys removed)";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting settings");
            StatusMessage = $"Error resetting settings: {ex.Message}";
        }
    }

    /// <summary>
    /// "Clear key" next to the Anthropic key box: deletes the stored key.
    /// Needed because Save treats an empty key field as "leave unchanged".
    /// </summary>
    [RelayCommand]
    private async Task ClearApiKeyAsync()
    {
        try
        {
            await ClearStoredApiKeyAsync();
            StatusMessage = "Anthropic API key removed";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing the Anthropic API key");
            StatusMessage = $"Could not remove the API key: {ex.Message}";
        }
    }

    /// <summary>"Clear key" next to the embedding key box: blanks the stored OpenAI key.</summary>
    [RelayCommand]
    private async Task ClearEmbeddingApiKeyAsync()
    {
        try
        {
            await ClearStoredEmbeddingApiKeyAsync();
            StatusMessage = "Embedding API key removed";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing the embedding API key");
            StatusMessage = $"Could not remove the embedding API key: {ex.Message}";
        }
    }

    /// <summary>
    /// The Anthropic key row ("ApiKey") is not seeded, so removing the key
    /// means deleting the row - readers treat a missing row as "no key".
    /// </summary>
    private async Task ClearStoredApiKeyAsync()
    {
        await _settingsService.DeleteSettingAsync(SettingKeys.AnthropicApiKey);
        ApiKey = string.Empty;
    }

    /// <summary>
    /// embedding_api_key IS a seeded row (seed value ""), so it is blanked
    /// rather than deleted to keep the seeded key set intact; readers treat
    /// an empty value as "no key".
    /// </summary>
    private async Task ClearStoredEmbeddingApiKeyAsync()
    {
        await _settingsService.SetSettingAsync(SettingKeys.EmbeddingApiKey, string.Empty);
        EmbeddingApiKey = string.Empty;
    }

    /// <summary>
    /// Regenerates the embeddings of ALL events with the currently selected
    /// provider (persisting the selection first so the router uses it). The
    /// heavy lifting lives in Core (IEventService.ReEmbedAllAsync), which
    /// deletes stale-dimension rows up front and reports (done, total).
    /// </summary>
    [RelayCommand]
    private async Task ReEmbedAllAsync()
    {
        if (IsReEmbedding) return;

        try
        {
            IsReEmbedding = true;
            _reEmbedCts = new CancellationTokenSource();
            ReEmbedStatusMessage = "Preparing to re-embed...";

            // Persist the selected embedding provider so the routing service
            // generates the new vectors with it (settings apply live).
            await _settingsService.SetSettingAsync(
                SettingKeys.EmbeddingProvider, ToEmbeddingProviderKey(SelectedEmbeddingProvider));
            _loadedEmbeddingProviderKey = ToEmbeddingProviderKey(SelectedEmbeddingProvider);

            // Progress<T> marshals back to this (UI) synchronization context.
            var progress = new Progress<(int done, int total)>(p =>
                ReEmbedStatusMessage = $"Re-embedding events... {p.done}/{p.total}");

            var count = await _eventService.ReEmbedAllAsync(progress, _reEmbedCts.Token);

            ReEmbedStatusMessage = $"Re-embedded {count} event(s) with the current provider";
            StatusMessage = ReEmbedStatusMessage;
            ShowReEmbedWarning = false;
            _logger.LogInformation("Re-embedded {Count} events from Settings", count);
        }
        catch (OperationCanceledException)
        {
            ReEmbedStatusMessage = "Re-embed cancelled — run it again to restore similarity features";
            _logger.LogInformation("Re-embed cancelled from Settings");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error re-embedding events");
            ReEmbedStatusMessage = $"Re-embed failed: {ex.Message}";
            StatusMessage = "Error re-embedding events";
        }
        finally
        {
            IsReEmbedding = false;
            _reEmbedCts?.Dispose();
            _reEmbedCts = null;
        }
    }

    [RelayCommand]
    private void CancelReEmbed()
    {
        _reEmbedCts?.Cancel();
    }

    /// <summary>
    /// Re-enumerates audio input devices (e.g. after plugging in a headset),
    /// keeping the current selection when that device is still present.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAudioInputDevicesAsync()
    {
        await LoadAudioInputDevicesAsync();
    }

    /// <summary>
    /// Fills <see cref="AudioInputDevices"/> ("System default" first, then the
    /// enumerated capture devices) and selects the device saved under
    /// audio_input_device_id. A saved device that is no longer present falls
    /// back to the system default and says so in the status caption, so Save
    /// writes "" rather than a stale id. Enumeration failures are already
    /// caught inside the recording service (empty list); nothing here throws.
    /// </summary>
    private async Task LoadAudioInputDevicesAsync()
    {
        var storedDeviceId = (await _settingsService.GetSettingAsync<string>(
            SettingKeys.AudioInputDeviceId, string.Empty) ?? string.Empty).Trim();

        // A refresh keeps an unsaved in-UI selection; the first load uses the stored one.
        var wantedDeviceId = SelectedAudioInputDevice?.DeviceId ?? storedDeviceId;

        var devices = (await _audioRecordingService.GetAvailableDevicesAsync()).ToList();

        AudioInputDevices.Clear();
        AudioInputDevices.Add(new AudioDeviceDto
        {
            DeviceId = string.Empty,
            Name = "System default",
            IsDefault = true,
            IsEnabled = true
        });
        foreach (var device in devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            AudioInputDevices.Add(device);
        }

        var match = string.IsNullOrEmpty(wantedDeviceId)
            ? null
            : AudioInputDevices.FirstOrDefault(d => string.Equals(d.DeviceId, wantedDeviceId, StringComparison.OrdinalIgnoreCase));

        SelectedAudioInputDevice = match ?? AudioInputDevices[0];

        var countText = devices.Count == 1 ? "1 microphone found" : $"{devices.Count} microphones found";
        AudioDeviceStatus = match == null && !string.IsNullOrEmpty(wantedDeviceId)
            ? $"{countText}. The saved microphone is not connected; the system default will be used until you pick another one and save."
            : countText;
    }

    /// <summary>
    /// Shows the re-embed warning as soon as the selection differs from the
    /// provider whose embeddings are (or were) stored.
    /// </summary>
    partial void OnSelectedEmbeddingProviderChanged(string value)
    {
        ShowReEmbedWarning = ToEmbeddingProviderKey(value) != _loadedEmbeddingProviderKey;
    }

    /// <summary>Maps the display option to the canonical stored key.</summary>
    private static string ToEmbeddingProviderKey(string? displayOption)
    {
        return displayOption != null && displayOption.StartsWith("OpenAI", StringComparison.OrdinalIgnoreCase)
            ? EmbeddingProviderKeys.OpenAi
            : EmbeddingProviderKeys.Local;
    }

    private void UpdateLlmUsageSummary()
    {
        var calls = _llmUsageTracker.CallCount;
        LlmUsageSummary = calls == 0
            ? "No LLM calls this session"
            : $"{calls} LLM call(s) this session, ~{_llmUsageTracker.EstimatedTokens:N0} tokens";
    }

    /// <summary>
    /// Real cache/orphan cleanup (replaces the old placebo stub): deletes
    /// orphaned thumbnails, orphaned managed media files, and *.tmp leftovers
    /// in the media tree, reporting the ACTUAL bytes freed. Never claims
    /// success without doing the work.
    /// </summary>
    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        try
        {
            StatusMessage = "Scanning for orphaned files...";
            var result = await _mediaService.CleanupOrphansAsync();

            StatusMessage = result.FilesDeleted == 0
                ? "Nothing to clean"
                : $"Freed {FormatBytes(result.BytesFreed)} ({result.FilesDeleted} file(s): " +
                  $"{result.MediaFilesDeleted} media, {result.ThumbnailsDeleted} thumbnails, {result.TempFilesDeleted} temp)";
            _logger.LogInformation(
                "Clear Cache deleted {Files} file(s), freeing {Bytes} bytes", result.FilesDeleted, result.BytesFreed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing cache");
            StatusMessage = $"Error clearing cache: {ex.Message}";
        }
    }

    #region Backup & Restore Commands (F12)

    /// <summary>
    /// Manual backup: pick a .mtbak destination (any writable location,
    /// including network shares), then write the archive with progress.
    /// A successful manual backup also stamps last_backup_at, postponing the
    /// next scheduled run.
    /// </summary>
    [RelayCommand]
    private async Task BackUpNowAsync()
    {
        if (IsBackingUp) return;

        try
        {
            IsBackingUp = true;
            BackupProgress = 0;
            BackupStatusMessage = "Selecting backup location...";

            var savePicker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"MemoryTimeline-{DateTime.Now:yyyyMMdd-HHmmss}"
            };
            savePicker.FileTypeChoices.Add("Memory Timeline Backup", new List<string> { ".mtbak" });

            var hwnd = WindowNative.GetWindowHandle(App.Current.Window);
            InitializeWithWindow.Initialize(savePicker, hwnd);

            var file = await savePicker.PickSaveFileAsync();
            if (file == null)
            {
                BackupStatusMessage = "Backup cancelled";
                return;
            }

            BackupStatusMessage = "Backing up...";
            var progress = new Progress<int>(p => BackupProgress = p);
            var options = new BackupOptions
            {
                IncludeMedia = IncludeMediaInBackup,
                IncludeAudio = IncludeAudioInBackup
            };

            var result = await _backupService.CreateBackupAsync(file.Path, options, progress);

            await _settingsService.SetSettingAsync(
                SettingKeys.LastBackupAt,
                DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            await RefreshLastBackupDisplayAsync();

            BackupStatusMessage =
                $"Backup complete: {result.EventCount} events, {FormatBytes(result.SizeBytes)} — {result.FilePath}";
            _logger.LogInformation("Manual backup written to {Path}", result.FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual backup failed");
            BackupStatusMessage = $"Backup failed: {ex.Message}";
        }
        finally
        {
            IsBackingUp = false;
        }
    }

    /// <summary>
    /// Restore flow: pick a .mtbak, preview its manifest (zero writes), show
    /// an explicit confirmation dialog whose primary button is the confirm
    /// phrase "Replace current archive", then restore and recommend a restart.
    /// </summary>
    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        if (IsRestoring || IsBackingUp) return;

        try
        {
            BackupStatusMessage = "Selecting backup to restore...";

            var openPicker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            openPicker.FileTypeFilter.Add(".mtbak");

            var hwnd = WindowNative.GetWindowHandle(App.Current.Window);
            InitializeWithWindow.Initialize(openPicker, hwnd);

            var file = await openPicker.PickSingleFileAsync();
            if (file == null)
            {
                BackupStatusMessage = "Restore cancelled";
                return;
            }

            BackupStatusMessage = "Reading backup...";
            var preview = await _backupService.PreviewRestoreAsync(file.Path);

            var xamlRoot = App.Current.Window?.Content?.XamlRoot;
            if (xamlRoot == null)
            {
                BackupStatusMessage = "Restore failed: no window available for confirmation";
                return;
            }

            var detail =
                $"Backup created: {preview.CreatedAtUtc.ToLocalTime():g}\n" +
                $"Events: {preview.EventCount}\n" +
                $"Media files: {preview.MediaFileCount}\n" +
                $"Audio files: {preview.AudioFileCount}\n" +
                $"Size: {FormatBytes(preview.SizeBytes)}\n\n" +
                "Restoring REPLACES your current archive (database" +
                (preview.MediaFileCount > 0 || preview.AudioFileCount > 0 ? ", media and audio files" : "") +
                "). A safety backup of the current database is written first.";

            var confirmDialog = new ContentDialog
            {
                Title = "Restore from backup?",
                Content = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Replace current archive",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };

            var choice = await confirmDialog.ShowAsync();
            if (choice != ContentDialogResult.Primary)
            {
                BackupStatusMessage = "Restore cancelled";
                return;
            }

            IsRestoring = true;
            BackupStatusMessage = "Restoring archive... (safety backup, database, files)";

            await _backupService.RestoreAsync(file.Path, RestoreMode.Replace);

            BackupStatusMessage = "Restore complete — restart Memory Timeline to load the restored archive.";
            StatusMessage = "Restore complete — please restart the app";
            _logger.LogWarning("Archive restored from {Path}; restart recommended", file.Path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore failed");
            BackupStatusMessage = $"Restore failed: {ex.Message}";
        }
        finally
        {
            IsRestoring = false;
        }
    }

    /// <summary>Folder picker persisting backup_destination immediately.</summary>
    [RelayCommand]
    private async Task PickBackupDestinationAsync()
    {
        try
        {
            var folderPicker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            folderPicker.FileTypeFilter.Add("*");

            var hwnd = WindowNative.GetWindowHandle(App.Current.Window);
            InitializeWithWindow.Initialize(folderPicker, hwnd);

            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder == null)
            {
                return;
            }

            BackupDestination = folder.Path;
            await _settingsService.SetSettingAsync(SettingKeys.BackupDestination, folder.Path);
            BackupStatusMessage = $"Backup folder set to {folder.Path}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error picking backup destination");
            BackupStatusMessage = $"Could not set backup folder: {ex.Message}";
        }
    }

    /// <summary>Opens the configured backup folder in File Explorer.</summary>
    [RelayCommand]
    private async Task OpenBackupFolderAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(BackupDestination))
            {
                BackupStatusMessage = "No backup folder configured yet — choose one first";
                return;
            }

            if (!Directory.Exists(BackupDestination))
            {
                BackupStatusMessage = $"The backup folder no longer exists: {BackupDestination}";
                return;
            }

            var opened = await Windows.System.Launcher.LaunchFolderPathAsync(BackupDestination);
            if (!opened)
            {
                BackupStatusMessage = "Windows could not open the backup folder";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening backup folder");
            BackupStatusMessage = $"Could not open backup folder: {ex.Message}";
        }
    }

    /// <summary>Schedule changes persist immediately (canonical lowercase).</summary>
    partial void OnSelectedBackupScheduleChanged(string value)
    {
        if (_isLoadingBackupSettings) return;
        _ = PersistBackupSettingAsync(SettingKeys.BackupSchedule, (value ?? "Off").ToLowerInvariant());
    }

    /// <summary>Include-media changes persist immediately.</summary>
    partial void OnIncludeMediaInBackupChanged(bool value)
    {
        if (_isLoadingBackupSettings) return;
        _ = PersistBackupSettingAsync(SettingKeys.BackupIncludeMedia, value ? "true" : "false");
    }

    private async Task PersistBackupSettingAsync(string key, string value)
    {
        try
        {
            await _settingsService.SetSettingAsync(key, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error persisting setting {Key}", key);
            BackupStatusMessage = $"Could not save setting: {ex.Message}";
        }
    }

    private async Task RefreshLastBackupDisplayAsync()
    {
        var lastBackupRaw = await _settingsService.GetSettingAsync<string>(SettingKeys.LastBackupAt, string.Empty);
        if (DateTime.TryParse(
                lastBackupRaw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var lastBackup))
        {
            LastBackupDisplay = $"Last backup: {lastBackup.ToLocalTime():g}";
        }
        else
        {
            // Absent row = never backed up (last_backup_at is deliberately unseeded).
            LastBackupDisplay = "Last backup: never";
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    #endregion

    #region Export/Import Commands

    [RelayCommand]
    private async Task ExportToJsonAsync()
    {
        try
        {
            IsExporting = true;
            ExportStatusMessage = "Selecting export location...";

            var savePicker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"MemoryTimeline_Export_{DateTime.Now:yyyyMMdd_HHmmss}"
            };
            savePicker.FileTypeChoices.Add("JSON File", new List<string> { ".json" });

            // Get the main window handle for WinUI 3
            var hwnd = WindowNative.GetWindowHandle(App.Current.Window);
            InitializeWithWindow.Initialize(savePicker, hwnd);

            var file = await savePicker.PickSaveFileAsync();
            if (file == null)
            {
                ExportStatusMessage = "Export cancelled";
                return;
            }

            ExportStatusMessage = "Exporting to JSON...";
            var progress = new Progress<int>(p => ExportProgress = p);

            await _exportService.ExportToJsonAsync(file.Path, progress: progress);

            ExportStatusMessage = $"Export complete: {file.Path}";
            _logger.LogInformation("Exported timeline to JSON: {Path}", file.Path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting to JSON");
            ExportStatusMessage = $"Export error: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand]
    private async Task ExportToCsvAsync()
    {
        try
        {
            IsExporting = true;
            ExportStatusMessage = "Selecting export location...";

            var savePicker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"MemoryTimeline_Export_{DateTime.Now:yyyyMMdd_HHmmss}"
            };
            savePicker.FileTypeChoices.Add("CSV File", new List<string> { ".csv" });

            var hwnd = WindowNative.GetWindowHandle(App.Current.Window);
            InitializeWithWindow.Initialize(savePicker, hwnd);

            var file = await savePicker.PickSaveFileAsync();
            if (file == null)
            {
                ExportStatusMessage = "Export cancelled";
                return;
            }

            ExportStatusMessage = "Exporting to CSV...";
            var progress = new Progress<int>(p => ExportProgress = p);

            await _exportService.ExportToCsvAsync(file.Path, progress: progress);

            ExportStatusMessage = $"Export complete: {file.Path}";
            _logger.LogInformation("Exported timeline to CSV: {Path}", file.Path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting to CSV");
            ExportStatusMessage = $"Export error: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand]
    private async Task ExportToMarkdownAsync()
    {
        try
        {
            IsExporting = true;
            ExportStatusMessage = "Selecting export location...";

            var savePicker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"MemoryTimeline_Export_{DateTime.Now:yyyyMMdd_HHmmss}"
            };
            savePicker.FileTypeChoices.Add("Markdown File", new List<string> { ".md" });

            var hwnd = WindowNative.GetWindowHandle(App.Current.Window);
            InitializeWithWindow.Initialize(savePicker, hwnd);

            var file = await savePicker.PickSaveFileAsync();
            if (file == null)
            {
                ExportStatusMessage = "Export cancelled";
                return;
            }

            ExportStatusMessage = "Exporting to Markdown...";
            var progress = new Progress<int>(p => ExportProgress = p);

            await _exportService.ExportToMarkdownAsync(file.Path, progress: progress);

            ExportStatusMessage = $"Export complete: {file.Path}";
            _logger.LogInformation("Exported timeline to Markdown: {Path}", file.Path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting to Markdown");
            ExportStatusMessage = $"Export error: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand]
    private async Task ImportFromJsonAsync()
    {
        try
        {
            IsImporting = true;
            ImportStatusMessage = "Selecting file to import...";

            var openPicker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            openPicker.FileTypeFilter.Add(".json");

            var hwnd = WindowNative.GetWindowHandle(App.Current.Window);
            InitializeWithWindow.Initialize(openPicker, hwnd);

            var file = await openPicker.PickSingleFileAsync();
            if (file == null)
            {
                ImportStatusMessage = "Import cancelled";
                return;
            }

            ImportStatusMessage = "Validating import file...";
            var validation = await _importService.ValidateImportFileAsync(file.Path);

            if (!validation.IsValid)
            {
                ImportStatusMessage = $"Invalid file: {string.Join(", ", validation.Issues)}";
                return;
            }

            ImportStatusMessage = $"Importing {validation.EventCount} events...";
            var progress = new Progress<(int, string)>(p => ImportStatusMessage = p.Item2);

            var options = new ImportOptions
            {
                SkipDuplicates = true,
                UpdateExisting = false,
                CreateBackup = true
            };

            var result = await _importService.ImportFromJsonAsync(file.Path, options, progress);

            if (result.Success)
            {
                ImportStatusMessage = $"Import complete: {result.EventsImported} imported, {result.EventsSkipped} skipped";
                _logger.LogInformation("Imported {Count} events from JSON", result.EventsImported);
            }
            else
            {
                ImportStatusMessage = $"Import failed: {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing from JSON");
            ImportStatusMessage = $"Import error: {ex.Message}";
        }
        finally
        {
            IsImporting = false;
        }
    }

    #endregion

    #region Sync

    [ObservableProperty]
    private bool _syncEnabled;

    [ObservableProperty]
    private string _syncServerUrl = string.Empty;

    // One-time secret exchanged for device tokens during pairing. NEVER persisted
    // and never logged; cleared as soon as pairing succeeds.
    [ObservableProperty]
    private string _syncPairingCode = string.Empty;

    [ObservableProperty]
    private bool _syncAutoProcess;

    /// <summary>Derived pairing state: true when a device identity is stored.</summary>
    [ObservableProperty]
    private bool _isSyncPaired;

    [ObservableProperty]
    private string _syncDeviceDisplayName = string.Empty;

    [ObservableProperty]
    private string _syncStatusText = "Not paired";

    /// <summary>
    /// Loads persisted sync configuration and pairing state. Persist-on-change is
    /// suppressed while loading so reads never write values back. Exceptions bubble
    /// to <see cref="InitializeAsync"/>, which logs and surfaces the failure.
    /// </summary>
    private async Task LoadSyncSettingsAsync()
    {
        _suppressSyncPersistence = true;
        try
        {
            SyncEnabled = await _settingsService.GetSettingAsync<bool>(SettingKeys.SyncEnabled, false);
            SyncServerUrl = await _settingsService.GetSettingAsync<string>(SettingKeys.SyncServerUrl, string.Empty) ?? string.Empty;
            // Default must match SyncSettingsStore.IsAutoProcessEnabledAsync (true):
            // on a fresh install the pipeline auto-processes, so the toggle must
            // show On until the user persists a different choice.
            SyncAutoProcess = await _settingsService.GetSettingAsync<bool>(SettingKeys.SyncAutoProcess, true);
            SyncDeviceDisplayName = await _settingsService.GetSettingAsync<string>(SettingKeys.SyncDeviceDisplayName, string.Empty) ?? string.Empty;

            var deviceId = await _syncSettingsStore.GetDeviceIdAsync();
            IsSyncPaired = !string.IsNullOrEmpty(deviceId);
            SyncStatusText = !IsSyncPaired
                ? "Not paired"
                : string.IsNullOrEmpty(SyncDeviceDisplayName)
                    ? "Paired"
                    : $"Paired as {SyncDeviceDisplayName}";
        }
        finally
        {
            _suppressSyncPersistence = false;
        }
    }

    /// <summary>
    /// Registers this machine with the sync service using the entered server URL and
    /// one-time pairing code, then persists the returned identity and tokens.
    /// </summary>
    [RelayCommand]
    private async Task PairDeviceAsync()
    {
        var serverUrl = SyncServerUrl.Trim();
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri) ||
            (serverUri.Scheme != Uri.UriSchemeHttps && serverUri.Scheme != Uri.UriSchemeHttp))
        {
            SyncStatusText = "Enter a valid server URL (http:// or https://)";
            StatusMessage = "Sync pairing failed: invalid server URL";
            return;
        }

        if (string.IsNullOrWhiteSpace(SyncPairingCode))
        {
            SyncStatusText = "Enter the pairing code shown by the sync server";
            StatusMessage = "Sync pairing failed: missing pairing code";
            return;
        }

        try
        {
            SyncStatusText = "Pairing...";

            var registration = await _syncClient.RegisterDeviceAsync(serverUrl, new DeviceRegisterRequest
            {
                PairingCode = SyncPairingCode.Trim(),
                Platform = "windows",
                DisplayName = Environment.MachineName,
                AppVersion = AppVersion
            });

            await _syncSettingsStore.SaveRegistrationAsync(serverUrl, registration);
            await _settingsService.SetSettingAsync(SettingKeys.SyncDeviceDisplayName, Environment.MachineName);

            // The pairing code is single-use; drop it from the UI immediately.
            SyncPairingCode = string.Empty;
            SyncServerUrl = serverUrl;
            SyncDeviceDisplayName = Environment.MachineName;
            // SaveRegistrationAsync already persisted sync_enabled=true; reflect
            // it in the toggle without writing it back.
            _suppressSyncPersistence = true;
            try
            {
                SyncEnabled = true;
            }
            finally
            {
                _suppressSyncPersistence = false;
            }
            IsSyncPaired = true;
            SyncStatusText = $"Paired as {Environment.MachineName}";
            StatusMessage = "Sync device paired";
            _logger.LogInformation("Sync device paired successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync device pairing failed");
            SyncStatusText = $"Pairing failed: {ex.Message}";
            StatusMessage = "Sync pairing failed";
        }
    }

    /// <summary>
    /// Revokes this device on the server (best-effort — the server may be
    /// unreachable) and always clears the local registration.
    /// </summary>
    [RelayCommand]
    private async Task UnpairDeviceAsync()
    {
        try
        {
            await _syncClient.RevokeDeviceAsync();
        }
        catch (Exception ex)
        {
            // Best-effort: an unreachable/already-revoked server must not block a
            // local unpair. Logged and surfaced below via the status line.
            _logger.LogWarning(ex, "Sync device revoke failed; clearing local registration anyway");
        }

        try
        {
            await _syncSettingsStore.ClearRegistrationAsync();
            // ClearRegistrationAsync already persisted sync_enabled=false;
            // reflect it in the toggle without writing it back.
            _suppressSyncPersistence = true;
            try
            {
                SyncEnabled = false;
            }
            finally
            {
                _suppressSyncPersistence = false;
            }
            IsSyncPaired = false;
            SyncDeviceDisplayName = string.Empty;
            SyncStatusText = "Not paired";
            StatusMessage = "Sync device unpaired";
            _logger.LogInformation("Sync device unpaired");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clearing sync registration failed");
            SyncStatusText = $"Unpair failed: {ex.Message}";
            StatusMessage = "Sync unpair failed";
        }
    }

    /// <summary>Runs one sync cycle immediately and surfaces the outcome.</summary>
    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (!IsSyncPaired)
        {
            SyncStatusText = "Pair this device before syncing";
            return;
        }

        try
        {
            SyncStatusText = "Syncing...";
            var result = await _syncBackgroundWorker.SyncNowAsync();

            if (!string.IsNullOrEmpty(result.Error))
            {
                SyncStatusText = $"Sync failed: {result.Error}";
            }
            else if (!result.Ran)
            {
                SyncStatusText = "Sync did not run — enable sync and pair this device first";
            }
            else
            {
                SyncStatusText = $"Sync complete: {result.ChangesApplied} applied, {result.OutboxPublished} published";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual sync failed");
            SyncStatusText = $"Sync failed: {ex.Message}";
        }
    }

    partial void OnSyncEnabledChanged(bool value)
    {
        if (_suppressSyncPersistence) return;
        _ = PersistSyncSettingAsync(SettingKeys.SyncEnabled, value);
    }

    partial void OnSyncServerUrlChanged(string value)
    {
        if (_suppressSyncPersistence) return;
        _ = PersistSyncSettingAsync(SettingKeys.SyncServerUrl, value);
    }

    partial void OnSyncAutoProcessChanged(bool value)
    {
        if (_suppressSyncPersistence) return;
        _ = PersistSyncSettingAsync(SettingKeys.SyncAutoProcess, value);
    }

    /// <summary>
    /// Persists one sync setting immediately (sync settings apply on change, not on
    /// the page's Save button). Started fire-and-forget from property-changed
    /// handlers on the UI thread; failures are logged and surfaced in the status line.
    /// </summary>
    private async Task PersistSyncSettingAsync<T>(string key, T value)
    {
        try
        {
            await _settingsService.SetSettingAsync(key, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving sync setting {Key}", key);
            SyncStatusText = "Error saving sync settings";
        }
    }

    private void OnSyncStatusChanged(object? sender, SyncStatusChangedEventArgs e)
    {
        RunOnUi(() => SyncStatusText = e.StatusText);
    }

    /// <summary>
    /// Marshals the supplied action onto the UI dispatcher thread. Sync worker
    /// events arrive on background threads; mutating bound state off the UI thread
    /// corrupts/crashes the WinUI binding layer.
    /// </summary>
    private void RunOnUi(Action action)
    {
        if (_dispatcherQueue == null || _dispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => action());
        }
    }

    /// <summary>
    /// Releases the subscription to the singleton sync worker's status event so a
    /// navigated-away Settings page is not rooted for the app lifetime.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _syncBackgroundWorker.SyncStatusChanged -= OnSyncStatusChanged;
    }

    #endregion

    /// <summary>
    /// Maps a canonical (lowercase) stored value to the matching Title-case
    /// display option so ComboBox selection is never blank on load.
    /// A stored value that is not a supported option normalizes to the default,
    /// so an unsupported value can never round-trip back into settings on save.
    /// (LLM/embedding providers use their dedicated key normalizers instead.)
    /// </summary>
    private static string ToDisplayOption(string? storedValue, List<string> options, string defaultOption)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return defaultOption;
        }

        var match = options.FirstOrDefault(o =>
            string.Equals(o, storedValue, StringComparison.OrdinalIgnoreCase));

        return match ?? defaultOption;
    }

    partial void OnSelectedThemeChanged(string value)
    {
        StatusMessage = $"Theme changed to {value}";
    }

}
