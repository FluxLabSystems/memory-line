using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace MemoryTimeline.ViewModels;

/// <summary>
/// One selectable avatar color swatch in the person editor. A null
/// <see cref="Hex"/> means "Auto": the avatar color is derived from the
/// person's name (<see cref="PersonDto.EffectiveAvatarColor"/>).
/// </summary>
public sealed class AvatarColorOption
{
    /// <summary>The hex color ("#RRGGBB"), or null for the Auto option.</summary>
    public string? Hex { get; init; }

    /// <summary>True for the Auto (name-derived color) option.</summary>
    public bool IsAuto => Hex is null;

    /// <summary>Hex color rendered for the swatch; neutral gray for Auto.</summary>
    public string SwatchHex => Hex ?? "#808080";

    /// <summary>Tooltip text for the swatch.</summary>
    public string ToolTipText => IsAuto ? "Auto (color picked from the name)" : Hex!;
}

/// <summary>
/// ViewModel for the People (contact book) page: master list with live search,
/// sorting and a favorites filter; a detail pane with linked events; and
/// persistence hooks for the add/edit dialog, merge, delete, and drafts.
/// </summary>
public partial class ContactsViewModel : ObservableObject
{
    private readonly IPersonService _personService;
    private readonly IDraftService _draftService;
    private readonly INavigationService _navigationService;
    private readonly ILogger<ContactsViewModel> _logger;

    // Captured on construction (UI thread) so messenger callbacks arriving on
    // background threads can marshal observable mutations back to the UI.
    private readonly DispatcherQueue? _dispatcherQueue;

    /// <summary>Serializes refreshes so overlapping reloads cannot interleave.</summary>
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>Unfiltered person cache the visible list is projected from.</summary>
    private List<PersonDto> _allPersons = new();

    /// <summary>Key ("sourceId|targetId") of a dismissed duplicate pair, so it is not re-surfaced.</summary>
    private string? _dismissedDuplicateKey;

    #region Observable state

    [ObservableProperty]
    private ObservableCollection<PersonDto> _contacts = new();

    [ObservableProperty]
    private PersonDto? _selectedPerson;

    [ObservableProperty]
    private ObservableCollection<PersonEventSummary> _selectedPersonEvents = new();

    /// <summary>Aliases ("also known as") of the selected person.</summary>
    [ObservableProperty]
    private ObservableCollection<string> _selectedPersonAliases = new();

    /// <summary>Living people who share the most events with the selected person (profile aggregate).</summary>
    [ObservableProperty]
    private ObservableCollection<PersonCoOccurrence> _selectedPersonCoOccurring = new();

    /// <summary>Locations of the selected person's events, most frequent first (profile aggregate).</summary>
    [ObservableProperty]
    private ObservableCollection<PersonLocationCount> _selectedPersonTopLocations = new();

    /// <summary>Text of the add-alias box in the detail pane.</summary>
    [ObservableProperty]
    private string _newAliasText = string.Empty;

    /// <summary>Inline error under the alias editor (ci-duplicate alias, etc.).</summary>
    [ObservableProperty]
    private string _aliasErrorMessage = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private PersonSortOption _sortOption = PersonSortOption.Name;

    [ObservableProperty]
    private bool _favoritesOnly;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Non-empty when the last operation failed; drives the error InfoBar.</summary>
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>Non-empty after a successful action ("Draft saved"); drives the info InfoBar.</summary>
    [ObservableProperty]
    private string _infoMessage = string.Empty;

    /// <summary>Non-empty when potential duplicates were found; drives the duplicates InfoBar.</summary>
    [ObservableProperty]
    private string _duplicateMessage = string.Empty;

    /// <summary>Candidate merge targets for the merge dialog (everyone except the source).</summary>
    [ObservableProperty]
    private ObservableCollection<PersonDto> _mergeCandidates = new();

    #endregion

    #region Computed properties

    /// <summary>True when <see cref="ErrorMessage"/> is set.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>True when <see cref="InfoMessage"/> is set.</summary>
    public bool HasInfo => !string.IsNullOrEmpty(InfoMessage);

    /// <summary>True when <see cref="DuplicateMessage"/> is set.</summary>
    public bool HasDuplicates => !string.IsNullOrEmpty(DuplicateMessage);

    /// <summary>True when <see cref="AliasErrorMessage"/> is set.</summary>
    public bool HasAliasError => !string.IsNullOrEmpty(AliasErrorMessage);

    /// <summary>True when a person is selected but has no aliases yet.</summary>
    public bool HasNoAliases => SelectedPerson != null && SelectedPersonAliases.Count == 0;

    /// <summary>True when the selected person shares events with someone.</summary>
    public bool HasCoOccurring => SelectedPersonCoOccurring.Count > 0;

    /// <summary>True when the selected person's events have at least one location.</summary>
    public bool HasTopLocations => SelectedPersonTopLocations.Count > 0;

    /// <summary>True when the Connections card has anything to show.</summary>
    public bool HasConnections => HasCoOccurring || HasTopLocations;

    /// <summary>
    /// The first merge suggestion found, if any (for the Review action).
    /// First = suggested keeper/target, Second = suggested source.
    /// </summary>
    public MergeCandidate? FirstDuplicatePair { get; private set; }

    /// <summary>"No people yet" / "1 person" / "N people" for the page subtitle.</summary>
    public string ContactCountDisplay => _allPersons.Count switch
    {
        0 => "No people yet",
        1 => "1 person",
        _ => $"{_allPersons.Count} people"
    };

    /// <summary>True when there are no contacts at all (call-to-action empty state).</summary>
    public bool ShowNoContacts => !IsLoading && _allPersons.Count == 0;

    /// <summary>True when contacts exist but the current filter matches none.</summary>
    public bool ShowNoMatches => !IsLoading && _allPersons.Count > 0 && Contacts.Count == 0;

    /// <summary>True when a person is selected (shows the detail pane).</summary>
    public bool HasSelection => SelectedPerson != null;

    /// <summary>True when nothing is selected (shows the placeholder pane).</summary>
    public bool ShowNoSelection => SelectedPerson == null;

    /// <summary>True when the selected person has no linked events.</summary>
    public bool HasNoLinkedEvents => SelectedPerson != null && SelectedPersonEvents.Count == 0;

    /// <summary>Star glyph for the selected person's favorite toggle (filled when favorite).</summary>
    public string SelectedFavoriteGlyph =>
        SelectedPerson?.IsFavorite == true ? "\uE735" : "\uE734";

    /// <summary>Label for the selected person's favorite toggle.</summary>
    public string SelectedFavoriteLabel =>
        SelectedPerson?.IsFavorite == true ? "Unfavorite" : "Favorite";

    /// <summary>True when the selected person has a first-met date.</summary>
    public bool SelectedHasFirstMet => SelectedPerson?.FirstMetDate != null;

    /// <summary>
    /// True when the selected person has no contact details at all (shows the
    /// "no details yet" hint in the detail card).
    /// </summary>
    public bool SelectedHasNoDetails =>
        SelectedPerson != null &&
        !SelectedPerson.HasRelationship && !SelectedPerson.HasEmail &&
        !SelectedPerson.HasPhone && !SelectedPerson.HasBirthday &&
        !SelectedPerson.HasCompany && !SelectedPerson.HasNotes &&
        !SelectedHasFirstMet;

    /// <summary>Selected person's first-met date as "MMM d, yyyy", or empty.</summary>
    public string SelectedFirstMetDisplay =>
        SelectedPerson?.FirstMetDate?.ToString("MMM d, yyyy") ?? string.Empty;

    /// <summary>Selected person's earliest linked-event date, or an em dash.</summary>
    public string SelectedFirstEventDisplay =>
        SelectedPerson?.FirstEventDate?.ToString("MMM d, yyyy") ?? "—";

    /// <summary>Selected person's latest linked-event date, or an em dash.</summary>
    public string SelectedLastEventDisplay =>
        SelectedPerson?.LastEventDate?.ToString("MMM d, yyyy") ?? "—";

    /// <summary>When the selected person was added, e.g. "Added Mar 3, 2026".</summary>
    public string SelectedAddedDisplay =>
        SelectedPerson == null
            ? string.Empty
            : $"Added {SelectedPerson.CreatedAt.ToLocalTime():MMM d, yyyy}";

    /// <summary>
    /// Swatches for the editor's avatar color picker: an Auto option followed
    /// by <see cref="PersonDto.AvatarPalette"/>.
    /// </summary>
    public IReadOnlyList<AvatarColorOption> AvatarColorOptions { get; }

    #endregion

    public ContactsViewModel(
        IPersonService personService,
        IDraftService draftService,
        INavigationService navigationService,
        ILogger<ContactsViewModel> logger)
    {
        _personService = personService;
        _draftService = draftService;
        _navigationService = navigationService;
        _logger = logger;

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        var options = new List<AvatarColorOption> { new() { Hex = null } };
        options.AddRange(PersonDto.AvatarPalette.Select(hex => new AvatarColorOption { Hex = hex }));
        AvatarColorOptions = options;

        // Refresh when people change anywhere in the app (review approvals,
        // event editors creating people, etc.), not just from this page.
        WeakReferenceMessenger.Default.Register<PersonCreatedMessage>(this, static (recipient, _) =>
            ((ContactsViewModel)recipient).OnPeopleChangedElsewhere());
        WeakReferenceMessenger.Default.Register<PersonUpdatedMessage>(this, static (recipient, _) =>
            ((ContactsViewModel)recipient).OnPeopleChangedElsewhere());
        WeakReferenceMessenger.Default.Register<PersonDeletedMessage>(this, static (recipient, _) =>
            ((ContactsViewModel)recipient).OnPeopleChangedElsewhere());
        WeakReferenceMessenger.Default.Register<PersonsMergedMessage>(this, static (recipient, _) =>
            ((ContactsViewModel)recipient).OnPeopleChangedElsewhere());
    }

    #region Load / refresh / filter

    /// <summary>
    /// Initial load: fetches all persons and scans for potential duplicates.
    /// </summary>
    public async Task InitializeAsync()
    {
        await RefreshCoreAsync(preserveSelection: true);
        await ScanForDuplicatesAsync();
    }

    /// <summary>Reloads the person list, preserving the current selection.</summary>
    [RelayCommand]
    private Task RefreshAsync() => RefreshCoreAsync(preserveSelection: true);

    /// <summary>
    /// Reloads the person cache from the service and re-projects the visible
    /// list. Serialized so messenger-triggered and user-triggered refreshes
    /// cannot interleave.
    /// </summary>
    private async Task RefreshCoreAsync(bool preserveSelection)
    {
        await _refreshLock.WaitAsync();
        try
        {
            IsLoading = true;
            var selectedId = preserveSelection ? SelectedPerson?.PersonId : null;

            _allPersons = await _personService.GetAllPersonsAsync(SortOption);
            ApplyFilter(selectedId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading people");
            ErrorMessage = "Couldn't load people. Please try again.";
        }
        finally
        {
            IsLoading = false;
            NotifyListStateChanged();
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Projects the cached persons through the favorites filter, the live
    /// search text, and the sort option into <see cref="Contacts"/>, then
    /// restores the selection by id when still visible.
    /// </summary>
    private void ApplyFilter(string? reselectPersonId = null)
    {
        reselectPersonId ??= SelectedPerson?.PersonId;

        IEnumerable<PersonDto> projected = _allPersons;

        if (FavoritesOnly)
        {
            projected = projected.Where(p => p.IsFavorite);
        }

        var term = SearchText.Trim();
        if (term.Length > 0)
        {
            projected = projected.Where(p => MatchesSearch(p, term));
        }

        Contacts = new ObservableCollection<PersonDto>(SortPersons(projected));

        if (reselectPersonId != null)
        {
            SelectedPerson = Contacts.FirstOrDefault(p => p.PersonId == reselectPersonId);
        }

        NotifyListStateChanged();
    }

    /// <summary>Search over the same fields the service searches.</summary>
    private static bool MatchesSearch(PersonDto person, string term)
    {
        return ContainsIgnoreCase(person.Name, term) ||
               ContainsIgnoreCase(person.Nickname, term) ||
               ContainsIgnoreCase(person.Email, term) ||
               ContainsIgnoreCase(person.Company, term) ||
               ContainsIgnoreCase(person.Relationship, term);
    }

    private static bool ContainsIgnoreCase(string? haystack, string needle)
    {
        return !string.IsNullOrEmpty(haystack) &&
               haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Favorites first, then by the active sort option.</summary>
    private List<PersonDto> SortPersons(IEnumerable<PersonDto> people)
    {
        var ordered = people.OrderByDescending(p => p.IsFavorite);

        return SortOption switch
        {
            PersonSortOption.RecentlyAdded => ordered
                .ThenByDescending(p => p.CreatedAt)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            PersonSortOption.MostEvents => ordered
                .ThenByDescending(p => p.EventCount)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            PersonSortOption.RecentlyUpdated => ordered
                .ThenByDescending(p => p.UpdatedAt)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => ordered
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSortOptionChanged(PersonSortOption value) => ApplyFilter();

    partial void OnFavoritesOnlyChanged(bool value) => ApplyFilter();

    partial void OnSelectedPersonChanged(PersonDto? value)
    {
        NotifySelectionChanged();
        AliasErrorMessage = string.Empty;
        NewAliasText = string.Empty;
        _ = LoadSelectedPersonEventsAsync();
        _ = LoadSelectedPersonAliasesAsync();
        _ = LoadSelectedPersonConnectionsAsync();
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnAliasErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasAliasError));

    partial void OnInfoMessageChanged(string value) => OnPropertyChanged(nameof(HasInfo));

    partial void OnDuplicateMessageChanged(string value) => OnPropertyChanged(nameof(HasDuplicates));

    private void NotifyListStateChanged()
    {
        OnPropertyChanged(nameof(ShowNoContacts));
        OnPropertyChanged(nameof(ShowNoMatches));
        OnPropertyChanged(nameof(ContactCountDisplay));
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ShowNoSelection));
        OnPropertyChanged(nameof(HasNoLinkedEvents));
        OnPropertyChanged(nameof(HasNoAliases));
        OnPropertyChanged(nameof(SelectedFavoriteGlyph));
        OnPropertyChanged(nameof(SelectedFavoriteLabel));
        OnPropertyChanged(nameof(SelectedHasFirstMet));
        OnPropertyChanged(nameof(SelectedHasNoDetails));
        OnPropertyChanged(nameof(SelectedFirstMetDisplay));
        OnPropertyChanged(nameof(SelectedFirstEventDisplay));
        OnPropertyChanged(nameof(SelectedLastEventDisplay));
        OnPropertyChanged(nameof(SelectedAddedDisplay));
    }

    /// <summary>
    /// Marshals a messenger notification (possibly from a background thread)
    /// onto the UI dispatcher and refreshes the list.
    /// </summary>
    private void OnPeopleChangedElsewhere()
    {
        RunOnUi(() => _ = RefreshCoreAsync(preserveSelection: true));
    }

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

    #endregion

    #region Selection detail

    /// <summary>
    /// Selects a person by id in the detail pane (used for navigation
    /// parameters and post-save focus). Clears the search/favorites filter
    /// when the person is hidden by it.
    /// </summary>
    public async Task SelectPersonByIdAsync(string personId)
    {
        if (_allPersons.Count == 0)
        {
            await RefreshCoreAsync(preserveSelection: false);
        }

        if (!_allPersons.Any(p => p.PersonId == personId))
        {
            // The id may be a merge tombstone (e.g. a stale deep link);
            // GetPersonAsync resolves the chain to the surviving person.
            try
            {
                var resolved = await _personService.GetPersonAsync(personId);
                if (resolved != null)
                {
                    personId = resolved.PersonId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve person id {PersonId} for selection", personId);
            }
        }

        var visible = Contacts.FirstOrDefault(p => p.PersonId == personId);
        if (visible == null && _allPersons.Any(p => p.PersonId == personId))
        {
            // The person exists but the current filter hides it; reset filters
            // so the requested person can actually be shown and selected.
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            _favoritesOnly = false;
            OnPropertyChanged(nameof(FavoritesOnly));
            ApplyFilter(personId);
            visible = Contacts.FirstOrDefault(p => p.PersonId == personId);
        }

        SelectedPerson = visible;
    }

    /// <summary>Loads the linked-event summaries for the selected person.</summary>
    private async Task LoadSelectedPersonEventsAsync()
    {
        var person = SelectedPerson;
        SelectedPersonEvents = new ObservableCollection<PersonEventSummary>();
        OnPropertyChanged(nameof(HasNoLinkedEvents));

        if (person == null)
        {
            return;
        }

        try
        {
            var events = await _personService.GetEventsForPersonAsync(person.PersonId);
            if (!ReferenceEquals(SelectedPerson, person))
            {
                return; // Selection moved on while loading.
            }

            SelectedPersonEvents = new ObservableCollection<PersonEventSummary>(events);
            OnPropertyChanged(nameof(HasNoLinkedEvents));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading events for person {PersonId}", person.PersonId);
            ErrorMessage = "Couldn't load this person's events.";
        }
    }

    /// <summary>Loads the aliases ("also known as") for the selected person.</summary>
    private async Task LoadSelectedPersonAliasesAsync()
    {
        var person = SelectedPerson;
        SelectedPersonAliases = new ObservableCollection<string>();
        OnPropertyChanged(nameof(HasNoAliases));

        if (person == null)
        {
            return;
        }

        try
        {
            var aliases = await _personService.GetAliasesAsync(person.PersonId);
            if (!ReferenceEquals(SelectedPerson, person))
            {
                return; // Selection moved on while loading.
            }

            SelectedPersonAliases = new ObservableCollection<string>(aliases);
            OnPropertyChanged(nameof(HasNoAliases));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading aliases for person {PersonId}", person.PersonId);
            ErrorMessage = "Couldn't load this person's aliases.";
        }
    }

    /// <summary>
    /// Loads the Connections card for the selected person: the top
    /// co-occurring people and top locations that
    /// <see cref="IPersonService.GetProfileAsync"/> aggregates.
    /// </summary>
    private async Task LoadSelectedPersonConnectionsAsync()
    {
        var person = SelectedPerson;
        SelectedPersonCoOccurring = new ObservableCollection<PersonCoOccurrence>();
        SelectedPersonTopLocations = new ObservableCollection<PersonLocationCount>();
        NotifyConnectionsChanged();

        if (person == null)
        {
            return;
        }

        try
        {
            var profile = await _personService.GetProfileAsync(person.PersonId);
            if (!ReferenceEquals(SelectedPerson, person))
            {
                return; // Selection moved on while loading.
            }

            if (profile != null)
            {
                SelectedPersonCoOccurring = new ObservableCollection<PersonCoOccurrence>(profile.TopCoOccurring);
                SelectedPersonTopLocations = new ObservableCollection<PersonLocationCount>(profile.TopLocations);
            }

            NotifyConnectionsChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading connections for person {PersonId}", person.PersonId);
            ErrorMessage = "Couldn't load this person's connections.";
        }
    }

    private void NotifyConnectionsChanged()
    {
        OnPropertyChanged(nameof(HasCoOccurring));
        OnPropertyChanged(nameof(HasTopLocations));
        OnPropertyChanged(nameof(HasConnections));
    }

    /// <summary>
    /// Adds <see cref="NewAliasText"/> as an alias of the selected person.
    /// Business-rule failures (alias already taken, matches another living
    /// person's name) surface inline via <see cref="AliasErrorMessage"/>.
    /// </summary>
    [RelayCommand]
    private async Task AddAliasAsync()
    {
        var person = SelectedPerson;
        var alias = NewAliasText.Trim();
        if (person == null || alias.Length == 0)
        {
            return;
        }

        try
        {
            await _personService.AddAliasAsync(person.PersonId, alias);
            NewAliasText = string.Empty;
            AliasErrorMessage = string.Empty;
            await LoadSelectedPersonAliasesAsync();
        }
        catch (InvalidOperationException ex)
        {
            // ci-duplicate alias / living-person name collision; user-facing message.
            AliasErrorMessage = ex.Message;
        }
        catch (ArgumentException ex)
        {
            AliasErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding alias '{Alias}' for person {PersonId}", alias, person.PersonId);
            AliasErrorMessage = "Couldn't add the alias. Please try again.";
        }
    }

    /// <summary>Removes an alias chip from the selected person.</summary>
    [RelayCommand]
    private async Task RemoveAliasAsync(string alias)
    {
        var person = SelectedPerson;
        if (person == null || string.IsNullOrWhiteSpace(alias))
        {
            return;
        }

        try
        {
            await _personService.RemoveAliasAsync(person.PersonId, alias);
            AliasErrorMessage = string.Empty;
            await LoadSelectedPersonAliasesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing alias '{Alias}' from person {PersonId}", alias, person.PersonId);
            AliasErrorMessage = "Couldn't remove the alias. Please try again.";
        }
    }

    /// <summary>Toggles the selected person's favorite flag.</summary>
    [RelayCommand]
    private async Task ToggleSelectedFavoriteAsync()
    {
        var person = SelectedPerson;
        if (person == null)
        {
            return;
        }

        try
        {
            var newState = await _personService.ToggleFavoriteAsync(person.PersonId);
            person.IsFavorite = newState; // Immediate star feedback; messenger refresh follows.
            NotifySelectionChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling favorite for {PersonId}", person.PersonId);
            ErrorMessage = "Couldn't update the favorite flag.";
        }
    }

    /// <summary>Navigates to the timeline focused on the given event.</summary>
    public void OpenEvent(string eventId)
    {
        try
        {
            _navigationService.NavigateTo("Timeline", eventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error navigating to event {EventId}", eventId);
        }
    }

    #endregion

    #region Create / update / delete / merge

    /// <summary>
    /// Creates or updates a person from the editor dialog. Returns null on
    /// success, or a user-facing validation/error message (shown inline in the
    /// dialog) on failure.
    /// </summary>
    public async Task<string?> SavePersonAsync(PersonDto person, bool isNew)
    {
        try
        {
            var saved = isNew
                ? await _personService.CreatePersonAsync(person)
                : await _personService.UpdatePersonAsync(person);

            await RefreshCoreAsync(preserveSelection: false);
            SelectedPerson = Contacts.FirstOrDefault(p => p.PersonId == saved.PersonId);
            InfoMessage = isNew ? $"Added '{saved.Name}'." : $"Saved '{saved.Name}'.";
            await ScanForDuplicatesAsync();
            return null;
        }
        catch (InvalidOperationException ex)
        {
            // Duplicate name and similar business-rule failures; message is user-facing.
            return ex.Message;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving person '{Name}'", person.Name);
            return "Something went wrong while saving. Please try again.";
        }
    }

    /// <summary>Deletes the selected person (event links cascade).</summary>
    public async Task DeleteSelectedPersonAsync()
    {
        var person = SelectedPerson;
        if (person == null)
        {
            return;
        }

        try
        {
            await _personService.DeletePersonAsync(person.PersonId);
            SelectedPerson = null;
            await RefreshCoreAsync(preserveSelection: false);
            InfoMessage = $"Deleted '{person.Name}'.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting person {PersonId}", person.PersonId);
            ErrorMessage = $"Couldn't delete '{person.Name}'.";
        }
    }

    /// <summary>
    /// Fills <see cref="MergeCandidates"/> with every person except the merge
    /// source, favorites and name order preserved.
    /// </summary>
    public void PrepareMergeCandidates(PersonDto source)
    {
        MergeCandidates = new ObservableCollection<PersonDto>(
            SortPersons(_allPersons.Where(p => p.PersonId != source.PersonId)));
    }

    /// <summary>
    /// Merges the source person into the target person, then selects the
    /// surviving target.
    /// </summary>
    public async Task MergePersonsAsync(string sourcePersonId, string targetPersonId)
    {
        try
        {
            await _personService.MergePersonsAsync(sourcePersonId, targetPersonId);
            await RefreshCoreAsync(preserveSelection: false);
            SelectedPerson = Contacts.FirstOrDefault(p => p.PersonId == targetPersonId);
            DuplicateMessage = string.Empty;
            FirstDuplicatePair = null;
            await ScanForDuplicatesAsync();
            InfoMessage = "People merged.";
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Error merging person {SourceId} into {TargetId}", sourcePersonId, targetPersonId);
            ErrorMessage = "Couldn't merge these people. Please try again.";
        }
    }

    #endregion

    #region Duplicates

    /// <summary>
    /// Best-effort scan for merge suggestions (near-duplicate names plus
    /// alias collisions); surfaces the first suggestion — with the service's
    /// human-readable reason — via the dismissible duplicates InfoBar.
    /// </summary>
    public async Task ScanForDuplicatesAsync()
    {
        try
        {
            var candidates = await _personService.SuggestMergesAsync();
            var candidate = candidates.FirstOrDefault(c => DuplicateKey(c) != _dismissedDuplicateKey);

            FirstDuplicatePair = candidate;
            DuplicateMessage = candidate == null
                ? string.Empty
                : $"{candidate.Reason} — review to merge them.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Duplicate scan failed (non-fatal)");
        }
    }

    /// <summary>Dismisses the duplicates InfoBar and stops re-surfacing that pair.</summary>
    public void DismissDuplicates()
    {
        if (FirstDuplicatePair != null)
        {
            _dismissedDuplicateKey = DuplicateKey(FirstDuplicatePair);
        }

        FirstDuplicatePair = null;
        DuplicateMessage = string.Empty;
    }

    private static string DuplicateKey(MergeCandidate pair) =>
        $"{pair.First.PersonId}|{pair.Second.PersonId}";

    #endregion

    #region Drafts

    /// <summary>Loads a draft for resuming, or null (with an error InfoBar) on failure.</summary>
    public async Task<DraftDto?> GetDraftAsync(string draftId)
    {
        try
        {
            return await _draftService.GetDraftAsync(draftId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading draft {DraftId}", draftId);
            ErrorMessage = "Couldn't load the draft.";
            return null;
        }
    }

    /// <summary>
    /// Upserts a person draft from the editor. Returns true on success.
    /// </summary>
    public async Task<bool> SavePersonDraftAsync(string title, string payloadJson, string? draftId)
    {
        try
        {
            var saved = await _draftService.SaveDraftAsync(
                Data.Models.DraftTypes.Person, title, payloadJson, draftId);
            InfoMessage = $"Draft '{saved.Title}' saved. Resume it from the Review page.";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving person draft '{Title}'", title);
            ErrorMessage = "Couldn't save the draft. Please try again.";
            return false;
        }
    }

    /// <summary>
    /// Deletes a resumed draft after its person was actually saved. Best-effort:
    /// failure is logged but never surfaced (the person save already succeeded).
    /// </summary>
    public async Task DeletePersonDraftAsync(string draftId)
    {
        try
        {
            await _draftService.DeleteDraftAsync(draftId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error deleting resumed draft {DraftId}", draftId);
        }
    }

    #endregion
}
