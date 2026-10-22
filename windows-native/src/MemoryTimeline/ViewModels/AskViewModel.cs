using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;

namespace MemoryTimeline.ViewModels;

/// <summary>
/// ViewModel for the "Ask your timeline" page: a small conversation of
/// question/answer exchanges with clickable event citations.
/// </summary>
public partial class AskViewModel : ObservableObject, IDisposable
{
    private readonly IMemoryQueryService _memoryQueryService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IPersonRepository _personRepository;
    private readonly IEventRepository _eventRepository;
    private readonly ITagRepository _tagRepository;
    private readonly INavigationService _navigationService;
    private readonly ILogger<AskViewModel> _logger;

    // Captured on construction (UI thread) so background chunk delivery can
    // marshal observable mutations back onto the dispatcher.
    private readonly DispatcherQueue? _dispatcherQueue;

    private CancellationTokenSource? _cts;
    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<AskExchangeViewModel> _exchanges = new();

    [ObservableProperty]
    private string _questionText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>True when semantic retrieval is unavailable (no embedding key).</summary>
    [ObservableProperty]
    private bool _showDegradedBanner;

    /// <summary>True while the conversation is empty (shows example questions).</summary>
    [ObservableProperty]
    private bool _showExamples = true;

    [ObservableProperty]
    private string _exampleQuestion1 = "What did I do last summer?";

    [ObservableProperty]
    private string _exampleQuestion2 = "What happened in 2020?";

    [ObservableProperty]
    private string _exampleQuestion3 = "Show my travel memories";

    public AskViewModel(
        IMemoryQueryService memoryQueryService,
        IEmbeddingService embeddingService,
        IPersonRepository personRepository,
        IEventRepository eventRepository,
        ITagRepository tagRepository,
        INavigationService navigationService,
        ILogger<AskViewModel> logger)
    {
        _memoryQueryService = memoryQueryService;
        _embeddingService = embeddingService;
        _personRepository = personRepository;
        _eventRepository = eventRepository;
        _tagRepository = tagRepository;
        _navigationService = navigationService;
        _logger = logger;

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>
    /// Marshals the supplied action onto the UI dispatcher thread (background
    /// work must never mutate bound state directly).
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
    /// Initializes page state: the degraded-mode banner and (when the
    /// conversation is empty) example questions generated from real data.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            ShowDegradedBanner = !await _embeddingService.IsAvailableAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not determine embedding availability; assuming degraded mode");
            ShowDegradedBanner = true;
        }

        if (Exchanges.Count == 0)
        {
            ShowExamples = true;
            await LoadExampleQuestionsAsync();
        }
    }

    /// <summary>
    /// Builds three example questions from the archive: the most-linked
    /// person, the earliest event year, and the most-used tag. Falls back to
    /// generic examples on an empty database.
    /// </summary>
    private async Task LoadExampleQuestionsAsync()
    {
        var example1 = "What did I do last summer?";
        var example2 = "What happened in 2020?";
        var example3 = "Show my travel memories";

        try
        {
            var counts = await _personRepository.GetEventCountsAsync();
            if (counts.Count > 0)
            {
                var topPersonId = counts.OrderByDescending(kv => kv.Value).First().Key;
                var person = await _personRepository.GetByIdAsync(topPersonId);
                if (person != null && !string.IsNullOrWhiteSpace(person.Name))
                {
                    example1 = $"When did I first meet {person.Name}?";
                }
            }

            var events = (await _eventRepository.GetAllAsync()).ToList();
            if (events.Count > 0)
            {
                var earliestYear = events.Min(e => e.StartDate).Year;
                example2 = $"What happened in {earliestYear}?";
            }

            var tags = (await _tagRepository.GetFrequentlyUsedAsync(1)).ToList();
            if (tags.Count > 0)
            {
                example3 = $"Show my {tags[0].TagName} memories";
            }
        }
        catch (Exception ex)
        {
            // Example generation is best-effort; the generic fallbacks apply.
            _logger.LogWarning(ex, "Failed to build example questions from archive data; using generic examples");
        }

        ExampleQuestion1 = example1;
        ExampleQuestion2 = example2;
        ExampleQuestion3 = example3;
    }

    private bool CanAsk() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        AskCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Submits the current question: runs the retrieval + answer pipeline on a
    /// background task and streams the answer into the newest exchange.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAsk))]
    private async Task AskAsync()
    {
        var question = QuestionText?.Trim();
        if (string.IsNullOrWhiteSpace(question) || IsBusy)
        {
            return;
        }

        ErrorMessage = null;
        QuestionText = string.Empty;
        ShowExamples = false;

        var exchange = new AskExchangeViewModel { Question = question };
        Exchanges.Add(exchange);

        IsBusy = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            // The pipeline (LLM calls + cosine ranking over stored embeddings)
            // runs off the UI thread; all UI mutations go through RunOnUi.
            // The answer streams from the provider as it is generated: each
            // reported chunk is display text (citation markers already
            // rewritten to [n]), appended as it arrives. Progress<T> posts to
            // the UI thread's SynchronizationContext, and RunOnUi guards the
            // mutation either way.
            var partialAnswer = new Progress<string>(chunk => RunOnUi(() => exchange.Answer += chunk));
            var answer = await Task.Run(() => _memoryQueryService.AskAsync(question, null, partialAnswer, ct), ct);

            RunOnUi(() =>
            {
                // The returned answer is authoritative (final whitespace tidy);
                // replace the streamed text with it so both always agree.
                exchange.Answer = answer.Answer;

                foreach (var citation in answer.Citations)
                {
                    var chip = new AskCitationDto
                    {
                        EventId = citation.EventId,
                        // Precision-honest display: a Year-precision memory reads
                        // "1998 — ..." here, never a fabricated exact day.
                        Label = $"{DateDisplay.FormatPrecise(citation.StartDate, citation.DatePrecision)} — {citation.Title}"
                    };
                    chip.OpenCommand = new RelayCommand(() => OpenCitation(chip.EventId));
                    exchange.Citations.Add(chip);
                }

                exchange.HasCitations = exchange.Citations.Count > 0;
            });
        }
        catch (OperationCanceledException)
        {
            RunOnUi(() => exchange.Answer = string.IsNullOrEmpty(exchange.Answer)
                ? "(cancelled)"
                : exchange.Answer + " (cancelled)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ask pipeline failed for question: {Question}", question);
            RunOnUi(() =>
            {
                ErrorMessage = ex.Message;
                if (string.IsNullOrEmpty(exchange.Answer))
                {
                    exchange.Answer = "(failed)";
                }
            });
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            RunOnUi(() => IsBusy = false);
        }
    }

    /// <summary>
    /// Submits one of the example questions.
    /// </summary>
    [RelayCommand]
    private async Task AskExampleAsync(string? question)
    {
        if (string.IsNullOrWhiteSpace(question) || IsBusy)
        {
            return;
        }

        QuestionText = question;
        await AskAsync();
    }

    private bool CanStop() => IsBusy;

    /// <summary>
    /// Cancels the in-flight question; the exchange is marked "(cancelled)"
    /// and the page stays usable.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The pipeline finished between the check and the cancel; nothing to stop.
        }
    }

    /// <summary>
    /// Navigates to the Settings page (from the degraded-mode banner).
    /// </summary>
    [RelayCommand]
    private void OpenSettings()
    {
        _navigationService.NavigateTo("Settings");
    }

    /// <summary>
    /// Opens a cited event on the timeline.
    /// </summary>
    private void OpenCitation(string eventId)
    {
        // "Timeline" is the key registered in MainWindow.RegisterPages;
        // TimelinePage.OnNavigatedTo expects the event id string.
        _navigationService.NavigateTo("Timeline", eventId);
    }

    /// <summary>
    /// Cancels any in-flight ask. Called from AskPage.OnNavigatedFrom so a
    /// navigated-away page does not keep streaming in the background.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already completed and disposed; nothing to cancel.
        }
    }
}

/// <summary>
/// One question/answer exchange in the Ask conversation.
/// </summary>
public partial class AskExchangeViewModel : ObservableObject
{
    /// <summary>The question as asked (immutable once created).</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>The answer text; grows as streaming chunks append.</summary>
    [ObservableProperty]
    private string _answer = string.Empty;

    /// <summary>True once citations have been attached.</summary>
    [ObservableProperty]
    private bool _hasCitations;

    /// <summary>Citation chips (populated after the answer completes).</summary>
    public ObservableCollection<AskCitationDto> Citations { get; } = new();
}

/// <summary>
/// A clickable citation chip ("14 Mar 2019 — Title").
/// </summary>
public class AskCitationDto
{
    public string EventId { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>Set by the ViewModel before the chip is added to the collection.</summary>
    public IRelayCommand? OpenCommand { get; set; }
}
