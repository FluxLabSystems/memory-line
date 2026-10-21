# People Feature — Cross-Agent Contracts

**Status:** Implementation contract (binding for the 2026-08 people feature build)
**Scope:** `windows-native/` only. All paths below are relative to `windows-native/src/`.

This document pins the exact contracts shared between parallel implementation
agents. Every agent implements its owned files **exactly** against these
signatures; deviations must be reported, not silently made. Files not owned by
an agent are read-only for that agent.

---

## 1. Feature summary

1. **Contact-book persons** — `Person` grows contact fields (nickname,
   relationship, email, phone, birthday, company, notes, photo, avatar color,
   favorite, first-met date).
2. **People page** — new nav tab (`Tag="People"`, page key `"People"`,
   `ContactsPage`) for managing contacts: search, sort, favorites,
   add/edit/delete, merge duplicates, per-person linked-event list.
3. **Events with persons everywhere** — event create/edit dialog gets a person
   picker (plus tags/location/era enrichment); search gets a person facet;
   event display surfaces people.
4. **Audio → person suggestions** — extraction emits per-person details; the
   Review queue shows *new person / known person / update details* suggestions
   with one-click apply.
5. **Drafts** — "Save as draft" in event, era, and person editors; a Drafts
   section (Pivot) on the Review page lists drafts and resumes them in the
   owning editor via navigation parameter `"draft:<draftId>"`.

## 2. File ownership map

| Agent | Owns (create/modify) |
|---|---|
| **A — Data** | `MemoryTimeline.Data/Models/Person.cs`, `MemoryTimeline.Data/Models/Draft.cs` (new), `MemoryTimeline.Data/AppDbContext.cs`, `MemoryTimeline.Data/SchemaUpgrader.cs`, `MemoryTimeline.Data/Repositories/IPersonRepository.cs`, `PersonRepository.cs`, `IDraftRepository.cs` (new), `DraftRepository.cs` (new) |
| **B — Core services** | `MemoryTimeline.Core/Services/IPersonService.cs` (new, interface + impl), `MemoryTimeline.Core/Services/IDraftService.cs` (new, interface + impl), `MemoryTimeline.Core/DTOs/PersonDto.cs` (new), `MemoryTimeline.Core/DTOs/DraftDto.cs` (new, incl. payload classes), `MemoryTimeline.Core/Services/Messages.cs` |
| **C — Extraction** | `MemoryTimeline.Core/Services/EventExtractionService.cs`, `IEventExtractionService.cs`, `ILlmService.cs`, `AnthropicLlmService.cs`, `MemoryTimeline.Core/DTOs/PersonSuggestionDto.cs` (new), `MemoryTimeline.Core/DTOs/PendingEventDto.cs` |
| **D — Contacts UI** | `MemoryTimeline/Views/ContactsPage.xaml` (new), `ContactsPage.xaml.cs` (new), `MemoryTimeline/ViewModels/ContactsViewModel.cs` (new), `MemoryTimeline/Converters/HexToBrushConverter.cs` (new) |
| **E — Review UI** | `MemoryTimeline/Views/ReviewPage.xaml`, `ReviewPage.xaml.cs`, `MemoryTimeline/ViewModels/ReviewViewModel.cs` |
| **F — Event CRUD UI** | `MemoryTimeline/Controls/TimelineControl.xaml`, `TimelineControl.xaml.cs`, `MemoryTimeline/ViewModels/TimelineViewModel.cs`, `MemoryTimeline/Views/TimelinePage.xaml.cs`, `MemoryTimeline.Core/DTOs/TimelineEventDto.cs`, `MemoryTimeline/Controls/EventBubble.xaml`, `EventBubble.xaml.cs` |
| **G — Eras + Search UI** | `MemoryTimeline/Views/ErasPage.xaml`, `ErasPage.xaml.cs`, `MemoryTimeline/ViewModels/ErasViewModel.cs`, `MemoryTimeline/Views/SearchPage.xaml`, `SearchPage.xaml.cs`, `MemoryTimeline/ViewModels/SearchViewModel.cs`, `MemoryTimeline.Core/Services/IAdvancedSearchService.cs`, `MemoryTimeline.Core/Models/SearchModels.cs` |
| **H — Tests** | New files only under `MemoryTimeline.Tests/`: `Services/PersonServiceTests.cs`, `Services/DraftServiceTests.cs`, `UnitTests/PersonSuggestionTests.cs`, `Integration/PeopleSchemaTests.cs` |
| **Integrator (orchestrator)** | `MemoryTimeline/App.xaml.cs` (DI), `MemoryTimeline/MainWindow.xaml`, `MainWindow.xaml.cs`, `MemoryTimeline/App.xaml`, root docs |

Nobody edits `App.xaml.cs`, `MainWindow.*`, or `App.xaml` except the
integrator. UI agents needing converters define **page-local** resources and
new converter classes with agent-unique names.

## 3. Data layer (Agent A)

### 3.1 `Person` entity — new columns (all additive)

Extend the existing `Person` class (table `people`) with:

```csharp
[MaxLength(100)]  [Column("nickname")]       public string? Nickname { get; set; }
[MaxLength(100)]  [Column("relationship")]   public string? Relationship { get; set; }
[MaxLength(320)]  [Column("email")]          public string? Email { get; set; }
[MaxLength(50)]   [Column("phone")]          public string? Phone { get; set; }
                  [Column("birthday")]       public DateTime? Birthday { get; set; }
[MaxLength(200)]  [Column("company")]        public string? Company { get; set; }
                  [Column("notes")]          public string? Notes { get; set; }
                  [Column("photo_path")]     public string? PhotoPath { get; set; }
[MaxLength(9)]    [Column("avatar_color")]   public string? AvatarColor { get; set; }
                  [Column("is_favorite")]    public bool IsFavorite { get; set; }
                  [Column("first_met_date")] public DateTime? FirstMetDate { get; set; }
                  [Column("updated_at")]     public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
```

Existing `PersonId`, `Name`, `CreatedAt`, `EventPeople` are unchanged.

### 3.2 `Draft` entity (new file `Models/Draft.cs`)

```csharp
[Table("drafts")]
public class Draft
{
    [Key] [Column("draft_id")]                 public string DraftId { get; set; } = Guid.NewGuid().ToString();
    [Required] [MaxLength(20)] [Column("draft_type")] public string DraftType { get; set; } = string.Empty;
    [Required] [MaxLength(500)] [Column("title")]     public string Title { get; set; } = string.Empty;
    [Required] [Column("payload_json")]        public string PayloadJson { get; set; } = "{}";
    [Column("created_at")]                     public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Column("updated_at")]                     public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class DraftTypes
{
    public const string Event = "event";
    public const string Era = "era";
    public const string Person = "person";
}
```

### 3.3 `AppDbContext`

- `public DbSet<Draft> Drafts { get; set; } = null!;`
- `OnModelCreating`: Draft — `HasKey(DraftId)`, `HasIndex(DraftType)`,
  `HasIndex(UpdatedAt)`. Person — add `entity.HasIndex(p => p.IsFavorite);`
  (keep existing unique Name index).
- `UpdateTimestamps()`: also stamp `Person.UpdatedAt` and `Draft.UpdatedAt`
  for modified entries.

### 3.4 `SchemaUpgrader` (drift repair for pre-existing DBs)

New table (via `EnsureTableAsync`):

```sql
CREATE TABLE IF NOT EXISTS "drafts" (
    "draft_id" TEXT NOT NULL CONSTRAINT "PK_drafts" PRIMARY KEY,
    "draft_type" TEXT NOT NULL,
    "title" TEXT NOT NULL,
    "payload_json" TEXT NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL
);
```
Indexes: `IX_drafts_draft_type` on `draft_type`, `IX_drafts_updated_at` on
`updated_at`.

New columns on `people` (via `EnsureColumnsAsync`; NOT NULL columns must carry
constant defaults — SQLite `ALTER TABLE ADD COLUMN` requirement):

```
nickname TEXT NULL · relationship TEXT NULL · email TEXT NULL · phone TEXT NULL
birthday TEXT NULL · company TEXT NULL · notes TEXT NULL · photo_path TEXT NULL
avatar_color TEXT NULL · is_favorite INTEGER NOT NULL DEFAULT 0
first_met_date TEXT NULL · updated_at TEXT NOT NULL DEFAULT '2025-01-21 00:00:00'
```

Plus `CREATE INDEX IF NOT EXISTS "IX_people_is_favorite" ON "people" ("is_favorite");`
(only when the `people` table exists).

### 3.5 Repositories

`IPersonRepository` additions:

```csharp
Task<IEnumerable<Person>> GetFavoritesAsync();
Task<Dictionary<string, int>> GetEventCountsAsync();               // personId -> event count
Task<Dictionary<string, (DateTime First, DateTime Last)>> GetEventDateRangesAsync(); // personId -> first/last event StartDate
```

`IDraftRepository` (new): `IRepository<Draft>` plus

```csharp
Task<IEnumerable<Draft>> GetByTypeAsync(string draftType);   // ordered UpdatedAt desc
Task<IEnumerable<Draft>> GetAllOrderedAsync();               // ordered UpdatedAt desc
```

Both repository impls follow the existing factory pattern
(`IDbContextFactory<AppDbContext>`, `await using` context per operation,
`AsNoTracking()` for reads, tracked-load-then-mutate for updates — see
`PersonRepository.cs` today).

## 4. Core DTOs + services (Agent B)

All DTOs live in `MemoryTimeline.Core.DTOs`, are
`partial class … : ObservableObject` with `[ObservableProperty]` fields
(follow `PendingEventDto.cs` style). **No `Microsoft.UI` references in the new
DTOs** — colors are hex strings; the app layer converts.

### 4.1 `PersonDto` (new file `DTOs/PersonDto.cs`)

Observable properties (backing-field names shown):

```
_personId (init Guid.NewGuid().ToString()), _name (""), _nickname, _relationship,
_email, _phone, _birthday (DateTime?), _company, _notes, _photoPath,
_avatarColor, _isFavorite (bool), _firstMetDate (DateTime?),
_createdAt (DateTime), _updatedAt (DateTime),
_eventCount (int), _firstEventDate (DateTime?), _lastEventDate (DateTime?),
_isSelected (bool)
```

Computed (plain get-only, with `partial void On…Changed` notifications where
the source is observable):

```csharp
public string DisplayName   // Name, or $"{Name} ({Nickname})" when nickname set
public string Initials      // up to 2 uppercase initials from Name words; "?" when empty
public string EffectiveAvatarColor // AvatarColor if set, else deterministic pick from AvatarPalette by stable hash of Name
public string EventCountDisplay    // "3 events" / "1 event" / "No events"
public string BirthdayDisplay      // "MMM d, yyyy" or ""
public bool HasBirthday, HasEmail, HasPhone, HasCompany, HasNotes, HasRelationship
public static readonly string[] AvatarPalette // 10 pleasant hex colors
public static PersonDto FromPerson(Person person, int eventCount = 0, DateTime? firstEventDate = null, DateTime? lastEventDate = null);
public Person ToPerson();                 // full copy incl. PersonId
public void CopyTo(Person person);        // copies editable fields (not PersonId/CreatedAt)
```

The deterministic hash must be **stable across processes** (do not use
`string.GetHashCode()`; sum chars or use a simple FNV-1a).

### 4.2 `DraftDto` + payloads (new file `DTOs/DraftDto.cs`)

```csharp
public partial class DraftDto : ObservableObject
{
    // _draftId, _draftType, _title, _payloadJson, _createdAt, _updatedAt
    public string TypeDisplay { get; }   // "Event" | "Era" | "Person" (fallback: DraftType)
    public string TypeGlyph { get; }     // "\uE8EB" event (calendar), "\uE81C" era, "\uE77B" person
    public string UpdatedDisplay { get; } // "MMM d, yyyy h:mm tt"
    public static DraftDto FromDraft(Draft draft);
    public Draft ToDraft();
    public T? GetPayload<T>() where T : class;  // JSON deserialize, null on malformed (no throw)
}
```

Payload classes (same file, plain mutable classes, serialized with
`System.Text.Json` default options):

```csharp
public sealed class EventDraftPayload
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? Category { get; set; }
    public string? EraId { get; set; }
    public string? Location { get; set; }
    public List<string> TagNames { get; set; } = new();
    public List<string> PersonIds { get; set; } = new();   // existing persons
    public List<string> PersonNames { get; set; } = new(); // to-be-created persons
}

public sealed class EraDraftPayload
{
    public string Name { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? CategoryId { get; set; }
    public string? ColorCode { get; set; }
    public string? Description { get; set; }
    public string? Notes { get; set; }
    public List<string> Tags { get; set; } = new();
}

public sealed class PersonDraftPayload
{
    public string Name { get; set; } = string.Empty;
    public string? Nickname { get; set; }
    public string? Relationship { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public DateTime? Birthday { get; set; }
    public string? Company { get; set; }
    public string? Notes { get; set; }
    public string? AvatarColor { get; set; }
    public bool IsFavorite { get; set; }
    public DateTime? FirstMetDate { get; set; }
}
```

### 4.3 `IPersonService` (new file `Services/IPersonService.cs`, interface + `PersonService` impl in same file)

```csharp
public enum PersonSortOption { Name, RecentlyAdded, MostEvents, RecentlyUpdated }
public enum PersonMatchKind { Exact, Nickname, Fuzzy }

public sealed class PersonMatch
{
    public required PersonDto Person { get; init; }
    public PersonMatchKind Kind { get; init; }
}

public sealed class PersonDuplicatePair
{
    public required PersonDto First { get; init; }
    public required PersonDto Second { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class PersonEventSummary
{
    public string EventId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public DateTime StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public string? Category { get; init; }
    public string StartDateDisplay => StartDate.ToString("MMM d, yyyy");
}

public interface IPersonService
{
    Task<List<PersonDto>> GetAllPersonsAsync(PersonSortOption sortBy = PersonSortOption.Name, bool favoritesFirst = true);
    Task<List<PersonDto>> SearchPersonsAsync(string searchTerm, PersonSortOption sortBy = PersonSortOption.Name);
    Task<PersonDto?> GetPersonAsync(string personId);
    Task<PersonDto?> GetPersonByNameAsync(string name);          // case-insensitive exact
    Task<List<PersonDto>> GetPersonsForEventAsync(string eventId);
    Task<PersonDto> CreatePersonAsync(PersonDto person);         // throws InvalidOperationException on duplicate name
    Task<PersonDto> UpdatePersonAsync(PersonDto person);
    Task DeletePersonAsync(string personId);                     // junction rows cascade
    Task<bool> ToggleFavoriteAsync(string personId);             // returns new state
    Task<List<PersonEventSummary>> GetEventsForPersonAsync(string personId); // StartDate desc
    Task MergePersonsAsync(string sourcePersonId, string targetPersonId);
    Task<List<PersonDuplicatePair>> FindPotentialDuplicatesAsync();
    Task<PersonMatch?> FindBestMatchAsync(string name);
}
```

Implementation notes (binding):
- `PersonService(IDbContextFactory<AppDbContext> contextFactory, ILogger<PersonService> logger)` —
  query via short-lived contexts like `EventService` does; event
  counts/dates via grouped queries over `EventPeople`/`Events` (no N+1).
- All returned `PersonDto`s carry `EventCount`; `GetPersonAsync` also fills
  `FirstEventDate`/`LastEventDate`.
- `MergePersonsAsync`: one transaction — repoint `event_people` rows from
  source to target (skip rows that would duplicate the composite PK), copy any
  contact fields the target lacks (null/empty on target, present on source),
  delete source row, send `PersonsMergedMessage`.
- `FindBestMatchAsync`: exact name (OrdinalIgnoreCase) → `Exact`; exact
  nickname → `Nickname`; else normalized-containment or Levenshtein distance
  ≤ 2 on names → `Fuzzy`; null when nothing plausible.
- `FindPotentialDuplicatesAsync`: pairs with equal normalized names
  (trim/casefold), nickname==name matches, or distance ≤ 1; `Reason` is
  human-readable.
- Messages: send `PersonCreatedMessage`/`PersonUpdatedMessage`/
  `PersonDeletedMessage`/`PersonsMergedMessage` after successful writes,
  wrapped in try/catch like `EventExtractionService` does for
  `EventCreatedMessage`.

### 4.4 `IDraftService` (new file `Services/IDraftService.cs`, interface + `DraftService` impl)

```csharp
public interface IDraftService
{
    Task<DraftDto> SaveDraftAsync(string draftType, string title, string payloadJson, string? draftId = null); // upsert; new id when draftId null/unknown
    Task<List<DraftDto>> GetDraftsAsync(string? draftType = null);  // UpdatedAt desc
    Task<DraftDto?> GetDraftAsync(string draftId);
    Task DeleteDraftAsync(string draftId);
    Task<int> GetDraftCountAsync();
}
```

`DraftService(IDraftRepository draftRepository, ILogger<DraftService> logger)`.
Sends `DraftsChangedMessage` after successful save/delete. Empty titles are
stored as `"Untitled"` + type display.

### 4.5 `Messages.cs` additions (keep `EventCreatedMessage` untouched)

```csharp
public sealed record PersonCreatedMessage(string PersonId);
public sealed record PersonUpdatedMessage(string PersonId);
public sealed record PersonDeletedMessage(string PersonId);
public sealed record PersonsMergedMessage(string SourcePersonId, string TargetPersonId);
public sealed record DraftsChangedMessage;
public sealed record EventUpdatedMessage(string EventId);
```

## 5. Extraction + review pipeline (Agent C)

### 5.1 `ILlmService.cs`

Add alongside `ExtractedEvent`:

```csharp
public class ExtractedPersonDetail
{
    public string Name { get; set; } = string.Empty;
    public string? Relationship { get; set; }
    public string? Details { get; set; }
}
```

`ExtractedEvent` gains `public List<ExtractedPersonDetail>? PeopleDetails { get; set; }`
(the flat `People` list stays for compatibility).

### 5.2 `AnthropicLlmService.cs`

- Extraction prompt: request `peopleDetails` array (`name`, `relationship`,
  `details`) in the JSON schema alongside `people`; instruct that `people`
  stays the flat name list.
- When `ExtractionContext.KnownPeople` is non-empty, include a "Known people
  (use these canonical spellings when they match): …" line in the prompt.

### 5.3 `PersonSuggestionDto` (new file `DTOs/PersonSuggestionDto.cs`)

```csharp
public enum PersonSuggestionKind { NewPerson, KnownPerson, UpdateDetails }

public partial class PersonSuggestionDto : ObservableObject
{
    // _pendingEventId, _name, _matchedPersonId (string?), _matchedPersonName (string?),
    // _kind (PersonSuggestionKind), _suggestedRelationship (string?),
    // _suggestedDetails (string?), _isApplied (bool)
    public string KindDisplay { get; }  // "New person" | "Known" | "Update details"
    public string KindGlyph { get; }    // "\uE8FA" new person, "\uE73E" check, "\uE70F" edit
    public string Summary { get; }      // e.g. "Will be added as a new contact" / "Matches existing contact 'X'" / "Adds relationship 'sister' to 'X'"
    public bool CanApply => !IsApplied && Kind != PersonSuggestionKind.KnownPerson;
}
```

(Notify `CanApply` from `OnIsAppliedChanged`/`OnKindChanged`.)

### 5.4 `IEventExtractionService` additions

```csharp
/// <summary>Computes create/update suggestions for the people mentioned in a pending event.</summary>
Task<List<PersonSuggestionDto>> GetPersonSuggestionsAsync(string pendingEventId);

/// <summary>Applies one suggestion (creates the person or merges suggested details). Returns the updated suggestion (IsApplied=true).</summary>
Task<PersonSuggestionDto> ApplyPersonSuggestionAsync(PersonSuggestionDto suggestion);
```

### 5.5 `EventExtractionService` changes

- Constructor gains `IPersonService personService` (registered by integrator).
- `BuildExtractionContextAsync`: populate `context.KnownPeople` with up to 100
  person display names via `IPersonService.GetAllPersonsAsync(PersonSortOption.MostEvents)`
  (best-effort; keep the existing catch-and-continue shape). **Done** — the
  TODO that used to sit here is resolved; `EventExtractionService` now calls
  `GetAllPersonsAsync(PersonSortOption.MostEvents)` for the known-people list.
- `GetPersonSuggestionsAsync`: deserialize `ExtractedData`; source names from
  `PeopleDetails` (fallback `People`); for each distinct name run
  `IPersonService.FindBestMatchAsync`:
  - no match → `NewPerson` (carry suggested relationship/details);
  - match, and the suggestion has relationship/details the matched person
    lacks → `UpdateDetails`;
  - match otherwise → `KnownPerson`.
- `ApplyPersonSuggestionAsync`: `NewPerson` → `CreatePersonAsync` with
  Name/Relationship(+Details→Notes); `UpdateDetails` → fill only missing
  fields on the matched person via `UpdatePersonAsync`; `KnownPerson` → no-op.
  Always return with `IsApplied = true`. Duplicate-create races resolve to the
  existing person (catch the duplicate `InvalidOperationException`, re-match).
- `ApprovePendingEventAsync` / `MapExtractedMetadataAsync`: when creating a
  brand-new `Person` during approve, if a matching `ExtractedPersonDetail`
  exists, populate `Relationship` and `Notes` (details) on the new row.
  Matching stays name-based (OrdinalIgnoreCase).
- `PendingEventDto`: add read-only `List<string> PeopleNames` populated in
  `FromPendingEvent` by deserializing ExtractedData (`PeopleDetails` ??
  `People`, distinct, trimmed; empty list on malformed) + `bool HasPeople`,
  `string PeopleDisplay => string.Join(", ", PeopleNames)`.

## 6. Shared UI contracts (Agents D–G + integrator)

### 6.1 Navigation

- Page key `"People"` → `ContactsPage` (integrator registers; nav item
  `Content="People" Tag="People"`, FontIcon glyph `&#xE716;`).
- **Draft resume parameter:** `Frame` navigation parameter string
  `"draft:<draftId>"`.
  - `TimelinePage.OnNavigatedTo` (F): parameter starting `"draft:"` → tell
    `TimelineViewModel` to open the event editor prefilled from that draft;
    otherwise existing eventId behavior.
  - `ErasPage.OnNavigatedTo` (G) and `ContactsPage.OnNavigatedTo` (D): same
    pattern for era/person drafts.
  - A plain (non-`draft:`) string parameter to `ContactsPage` is a personId to
    select/show in the detail pane.
- Review's Drafts tab resumes via
  `INavigationService.NavigateTo(pageKeyFor(draftType), $"draft:{draftId}")`
  where event→`"Timeline"`, era→`"Eras"`, person→`"People"`.
- Person chips on events/search results may navigate with
  `NavigateTo("People", personId)`.

### 6.2 Save-as-draft editor behavior (all editors)

- Editors (event dialog in TimelineControl, era dialog in ErasPage, person
  dialog in ContactsPage) get a **"Save as Draft"** `SecondaryButton` (or
  equivalent) that serializes the corresponding payload class
  (§4.2) with `System.Text.Json.JsonSerializer.Serialize(...)` and calls
  `IDraftService.SaveDraftAsync(DraftTypes.X, title, json, currentDraftId)`.
- When an editor was opened from a draft, keep the `draftId`; saving the real
  entity **deletes the draft** on success (`DeleteDraftAsync`).
- Draft titles: the entity's title/name, else `"Untitled event/era/person"`.

### 6.3 ViewModels

- Resolve services via constructor injection (registered in DI by
  integrator): `IPersonService`, `IDraftService`, `IEventExtractionService`,
  etc. Pages fetch VMs with `App.Current.Services.GetRequiredService<T>()`
  in the constructor (existing pattern).
- Marshal service callbacks/messenger handlers to the UI thread via
  `DispatcherQueue` as existing VMs do.
- `WeakReferenceMessenger.Default` for cross-feature refresh
  (`PersonCreated/Updated/Deleted/Merged`, `DraftsChangedMessage`,
  `EventCreatedMessage`, `EventUpdatedMessage`).

### 6.4 `TimelineEventDto` additions (Agent F; G may bind, not edit)

```csharp
[ObservableProperty] private List<string> _peopleNames = new();
public bool HasPeople => PeopleNames.Count > 0;
public string PeopleDisplay => string.Join(", ", PeopleNames);
// partial void OnPeopleNamesChanged(...) notifies HasPeople + PeopleDisplay
```

`FromEvent` populates `PeopleNames` from `e.EventPeople` when the navigation
is loaded (`ep.Person?.Name`, non-null, ordered by name).

### 6.5 XAML conventions

- WinUI 3 + `x:Bind` (Page/Control property `ViewModel`), `ThemeResource`
  brushes only, converters from `App.xaml` (NullToVisibility,
  BoolToVisibility, InvertedBoolToVisibility, BoolNegation, NullToFalse,
  DateFormat, ZeroToVisibility) or **page-local** new converters.
- `ContentDialog.XamlRoot` must be set from the hosting page before
  `ShowAsync` (existing pattern).
- **No new NuGet packages.** Person chips = `ItemsControl`/`ItemsRepeater`
  with chip-styled `Button`s + `AutoSuggestBox`; do not assume
  `TokenizingTextBox` works.
- Segoe Fluent glyphs (use `\uXXXX` in C#, `&#xXXXX;` in XAML):
  people `E716`, person `E77B`, add `E710`, favorite outline `E734` /
  filled `E735`, edit `E70F`, delete `E74D`, drafts `E70B`, save `E74E`,
  mail `E715`, phone `E717`, calendar `E787`, company `E821`,
  check `E73E`, add-friend `E8FA`, merge/link `E71B`.

## 7. DI registrations (integrator applies)

```csharp
services.AddSingleton<IDraftRepository, DraftRepository>();
services.AddSingleton<IPersonService, PersonService>();
services.AddSingleton<IDraftService, DraftService>();
services.AddTransient<ContactsViewModel>();
```

`MainWindow`: nav item People (after Review), `RegisterPage("People", typeof(ContactsPage))`.

## 8. Conventions checklist (all agents)

- File-scoped namespaces, 4-space indent, `Nullable enable`, C# 12, XML doc
  comments on public members, `ILogger<T>` logging, no `.Result`/`.Wait()`.
- EF: `await using var context = await _contextFactory.CreateDbContextAsync();`
  per operation; `AsNoTracking()` for reads; load-tracked-then-mutate for
  updates (never `Update()` on detached graphs with junctions).
- Time: store `DateTime.UtcNow` timestamps; display via local formatting.
- Do **not** run `git commit`; the integrator commits.
- Do **not** modify files outside your ownership row (§2).
- Report every deviation from this contract in your final summary.
