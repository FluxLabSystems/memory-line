using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MemoryTimeline.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MemoryTimeline.Views;

public sealed partial class QueuePage : Page
{
    /// <summary>
    /// Soft warning threshold for pasted text (characters). Longer inputs are
    /// still allowed - they are just slow/expensive to extract.
    /// </summary>
    private const int LongTextWarningThreshold = 20000;

    /// <summary>
    /// Navigation parameter sent by the Jump List "Start Recording" quick
    /// action (App.HandleActivationAction): starts capturing on arrival.
    /// </summary>
    public const string StartRecordingParameter = "record";

    private bool _isPasteDialogOpen;

    /// <summary>
    /// True while the paste dialog is answering the recall card's prompt:
    /// the label is the prompt question and submit routes through
    /// <see cref="QueueViewModel.EnqueueRecallAnswerAsync"/>.
    /// </summary>
    private bool _isRecallAnswerMode;

    public QueueViewModel ViewModel { get; }

    public QueuePage()
    {
        InitializeComponent();
        ViewModel = App.Current.Services.GetRequiredService<QueueViewModel>();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Ctrl+Shift+V (MainWindow accelerator) navigates here with the "paste"
        // parameter so the paste-capture dialog opens on arrival.
        var parameter = e.Parameter as string;
        var openPasteDialog = string.Equals(parameter, "paste", StringComparison.OrdinalIgnoreCase);

        // The Jump List "Start Recording" quick action navigates here with the
        // "record" parameter so capturing starts as soon as the page is up.
        var startRecording = string.Equals(parameter, StartRecordingParameter, StringComparison.OrdinalIgnoreCase);

        // The Home page's recall card navigates here with "recall:<promptId>"
        // so the queue's recall card focuses that prompt.
        string? focusRecallPromptId = null;
        if (parameter != null &&
            parameter.StartsWith("recall:", StringComparison.OrdinalIgnoreCase))
        {
            focusRecallPromptId = parameter["recall:".Length..];
        }

        await ViewModel.InitializeAsync();

        if (!string.IsNullOrEmpty(focusRecallPromptId))
        {
            ViewModel.FocusRecallPrompt(focusRecallPromptId);
        }

        if (openPasteDialog)
        {
            OpenPasteDialogWhenReady();
        }

        if (startRecording)
        {
            if (ViewModel.CanStartRecording)
            {
                // StartRecordingAsync reports its own failures in the status line.
                await ViewModel.StartRecordingCommand.ExecuteAsync(null);
            }
            else
            {
                // A session is already open (recording or paused); do not stack a second one.
                ViewModel.StatusText = "A recording is already in progress";
            }
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // QueueViewModel is a transient VM that subscribes to singleton audio-service
        // events and runs a recording timer; dispose it on navigation away so the
        // subscriptions/timer are released and the VM is not leaked for the app lifetime.
        ViewModel.Dispose();
    }

    private void PasteTextButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ShowPasteDialogAsync();
    }

    private void TypeRecallAnswerButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ShowPasteDialogAsync(recallAnswerMode: true);
    }

    /// <summary>
    /// "Import audio…": multi-select file picker (unpackaged-app interop: the
    /// picker must be initialized with the window handle, as the other pickers
    /// do), then the ViewModel imports each file through the capture-ingestion
    /// pipeline and the outcome lands in the InfoBar.
    /// </summary>
    private async void ImportAudioButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var openPicker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.MusicLibrary
            };
            foreach (var extension in QueueViewModel.ImportableAudioExtensions)
            {
                openPicker.FileTypeFilter.Add(extension);
            }

            var hwnd = WindowNative.GetWindowHandle(App.Current.Window);
            InitializeWithWindow.Initialize(openPicker, hwnd);

            var files = await openPicker.PickMultipleFilesAsync();
            var paths = files?
                .Select(f => f.Path)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList() ?? new List<string>();
            if (paths.Count == 0)
            {
                return;
            }

            var outcome = await ViewModel.ImportAudioFilesAsync(paths);
            ShowImportOutcome(outcome);
        }
        catch (Exception ex)
        {
            // Picker/infrastructure failure (the VM handles import failures itself).
            ShowImportOutcome(new AudioImportOutcome("Import failed", ex.Message, 0, 1));
        }
    }

    /// <summary>
    /// Maps an import outcome onto the page's InfoBar: success when everything
    /// imported, warning when some files were skipped/failed, error when nothing
    /// entered the queue.
    /// </summary>
    private void ShowImportOutcome(AudioImportOutcome outcome)
    {
        ImportInfoBar.Severity = outcome.ImportedCount == 0
            ? InfoBarSeverity.Error
            : outcome.HasProblems
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
        ImportInfoBar.Title = outcome.Title;
        ImportInfoBar.Message = outcome.Message;
        ImportInfoBar.IsOpen = true;
    }

    /// <summary>
    /// Opens the paste dialog once the page is loaded (a ContentDialog needs a
    /// live XamlRoot), deferring the show through the DispatcherQueue so the
    /// layout pass has completed - mirrors TimelinePage's draft-dialog pattern.
    /// </summary>
    private void OpenPasteDialogWhenReady()
    {
        void ShowDeferred()
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
            {
                await ShowPasteDialogAsync();
            });
        }

        if (IsLoaded)
        {
            ShowDeferred();
            return;
        }

        RoutedEventHandler? loadedHandler = null;
        loadedHandler = (_, _) =>
        {
            Loaded -= loadedHandler;
            ShowDeferred();
        };
        Loaded += loadedHandler;
    }

    private async Task ShowPasteDialogAsync(bool recallAnswerMode = false)
    {
        // ContentDialog throws if shown while another instance is open.
        if (_isPasteDialogOpen)
        {
            return;
        }

        try
        {
            _isPasteDialogOpen = true;
            _isRecallAnswerMode = recallAnswerMode;

            // Fresh dialog state per open. In recall-answer mode the label IS
            // the prompt question (stamped server-side by the recall service),
            // so the box shows it read-only instead of accepting a custom one.
            PasteTextBox.Text = string.Empty;
            PasteLabelBox.Text = recallAnswerMode ? ViewModel.RecallQuestion ?? string.Empty : string.Empty;
            PasteLabelBox.IsEnabled = !recallAnswerMode;
            PasteTextDialog.Title = recallAnswerMode ? "Answer the prompt" : "Paste Text";
            PasteDialogErrorBar.IsOpen = false;
            UpdatePasteCharCount();

            PasteTextDialog.XamlRoot = XamlRoot;
            await PasteTextDialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // Surface the failure on the page status bar (the VM logs enqueue
            // errors itself; this covers dialog-infrastructure failures).
            ViewModel.StatusText = $"Could not open the paste dialog: {ex.Message}";
        }
        finally
        {
            _isPasteDialogOpen = false;
            _isRecallAnswerMode = false;
        }
    }

    private void PasteTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePasteCharCount();
    }

    private void UpdatePasteCharCount()
    {
        var length = PasteTextBox.Text?.Length ?? 0;
        PasteCharCountText.Text = length == 1 ? "1 character" : $"{length:N0} characters";
        PasteLengthWarningBar.IsOpen = length > LongTextWarningThreshold;
    }

    private async void PasteTextDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            // Recall answers route through the recall service (labels the queue
            // row with the question and marks the prompt Answered); plain
            // pastes keep the original enqueue path.
            var errorMessage = _isRecallAnswerMode
                ? await ViewModel.EnqueueRecallAnswerAsync(PasteTextBox.Text)
                : await ViewModel.EnqueuePastedTextAsync(PasteTextBox.Text, PasteLabelBox.Text);
            if (errorMessage != null)
            {
                // Keep the dialog open (and the pasted text intact) and show
                // the reason in the in-dialog InfoBar. The VM already logged it.
                args.Cancel = true;
                PasteDialogErrorBar.Message = errorMessage;
                PasteDialogErrorBar.IsOpen = true;
            }
        }
        finally
        {
            deferral.Complete();
        }
    }
}
