using CommunityToolkit.Mvvm.Messaging;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Data;
using MemoryTimeline.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Sort options for person lists.
/// </summary>
public enum PersonSortOption
{
    /// <summary>Alphabetically by name.</summary>
    Name,

    /// <summary>Most recently created first.</summary>
    RecentlyAdded,

    /// <summary>Highest linked-event count first.</summary>
    MostEvents,

    /// <summary>Most recently updated first.</summary>
    RecentlyUpdated
}

/// <summary>
/// How a person was matched by <see cref="IPersonService.FindBestMatchAsync"/>.
/// </summary>
public enum PersonMatchKind
{
    /// <summary>Exact case-insensitive name match.</summary>
    Exact,

    /// <summary>Exact case-insensitive nickname match.</summary>
    Nickname,

    /// <summary>Containment or small-edit-distance name match.</summary>
    Fuzzy,

    /// <summary>
    /// Exact case-insensitive alias-table match, resolved to the living
    /// person. Ranks between Nickname and Fuzzy in match priority.
    /// </summary>
    Alias
}

/// <summary>
/// A matched person together with how the match was made.
/// </summary>
public sealed class PersonMatch
{
    /// <summary>The matched person.</summary>
    public required PersonDto Person { get; init; }

    /// <summary>How the person was matched.</summary>
    public PersonMatchKind Kind { get; init; }
}

/// <summary>
/// A pair of persons that look like duplicates of each other.
/// </summary>
public sealed class PersonDuplicatePair
{
    /// <summary>First person of the pair.</summary>
    public required PersonDto First { get; init; }

    /// <summary>Second person of the pair.</summary>
    public required PersonDto Second { get; init; }

    /// <summary>Human-readable explanation of why the pair is suspicious.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// A suggested merge: two living persons who look like the same human, with a
/// human-readable reason. <see cref="First"/> is the suggested merge target
/// (keeper) and <see cref="Second"/> the suggested source, matching how the
/// People page's duplicates banner drives its merge dialog.
/// </summary>
public sealed class MergeCandidate
{
    /// <summary>Suggested merge target (keeper).</summary>
    public required PersonDto First { get; init; }

    /// <summary>Suggested merge source.</summary>
    public required PersonDto Second { get; init; }

    /// <summary>Human-readable explanation of why the pair should merge.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// A living person who shares events with the profiled person.
/// </summary>
/// <param name="PersonId">The co-occurring person's id.</param>
/// <param name="Name">Their display name.</param>
/// <param name="SharedEvents">How many events both people are linked to.</param>
public sealed record PersonCoOccurrence(string PersonId, string Name, int SharedEvents)
{
    /// <summary>"1 shared memory" / "N shared memories", for display.</summary>
    public string SharedEventsDisplay => SharedEvents == 1 ? "1 shared memory" : $"{SharedEvents} shared memories";
}

/// <summary>
/// A location of the profiled person's events, with how many of them happened there.
/// </summary>
/// <param name="Name">The location's name.</param>
/// <param name="Count">Number of the person's events linked to it.</param>
public sealed record PersonLocationCount(string Name, int Count)
{
    /// <summary>"1 memory" / "N memories", for display.</summary>
    public string CountDisplay => Count == 1 ? "1 memory" : $"{Count} memories";
}

/// <summary>
/// Aggregated profile of one person: identity, linked events, first/last
/// appearance, top co-occurring people, top shared locations, and aliases.
/// </summary>
public sealed class PersonProfile
{
    /// <summary>The person (with event count and first/last event dates).</summary>
    public required PersonDto Person { get; init; }

    /// <summary>Linked-event summaries, newest start date first.</summary>
    public List<PersonEventSummary> Events { get; init; } = new();

    /// <summary>Start date of the earliest linked event, or null.</summary>
    public DateTime? FirstSeen { get; init; }

    /// <summary>Start date of the latest linked event, or null.</summary>
    public DateTime? LastSeen { get; init; }

    /// <summary>Up to five living people sharing the most events, most shared first (ties by name).</summary>
    public List<PersonCoOccurrence> TopCoOccurring { get; init; } = new();

    /// <summary>Up to five locations of the person's events, most frequent first (ties by name).</summary>
    public List<PersonLocationCount> TopLocations { get; init; } = new();

    /// <summary>The person's aliases, ordered case-insensitively.</summary>
    public List<string> Aliases { get; init; } = new();
}

/// <summary>
/// Lightweight summary of an event linked to a person.
/// </summary>
public sealed class PersonEventSummary
{
    /// <summary>Event id.</summary>
    public string EventId { get; init; } = string.Empty;

    /// <summary>Event title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Event start date.</summary>
    public DateTime StartDate { get; init; }

    /// <summary>Event end date, if any.</summary>
    public DateTime? EndDate { get; init; }

    /// <summary>Event category.</summary>
    public string? Category { get; init; }

    /// <summary>Start date formatted as "MMM d, yyyy".</summary>
    public string StartDateDisplay => StartDate.ToString("MMM d, yyyy");
}

/// <summary>
/// Service interface for contact-book person business logic.
/// F9 deviation (deliberate): the spec's IPeopleService members were added to
/// the pre-existing IPersonService rather than introducing a parallel
/// interface; legacy member names are kept as delegating shims.
/// </summary>
public interface IPersonService
{
    /// <summary>Gets all persons with event counts, sorted per the options.</summary>
    Task<List<PersonDto>> GetAllPersonsAsync(PersonSortOption sortBy = PersonSortOption.Name, bool favoritesFirst = true);

    /// <summary>Searches persons by name, nickname, email, company, or relationship.</summary>
    Task<List<PersonDto>> SearchPersonsAsync(string searchTerm, PersonSortOption sortBy = PersonSortOption.Name);

    /// <summary>Gets one person by id (with event count and first/last event dates), or null.</summary>
    Task<PersonDto?> GetPersonAsync(string personId);

    /// <summary>Gets one person by case-insensitive exact name, or null.</summary>
    Task<PersonDto?> GetPersonByNameAsync(string name);

    /// <summary>Gets the persons linked to an event, ordered by name, with event counts.</summary>
    Task<List<PersonDto>> GetPersonsForEventAsync(string eventId);

    /// <summary>Creates a person. Throws <see cref="InvalidOperationException"/> when a person with the same name already exists.</summary>
    Task<PersonDto> CreatePersonAsync(PersonDto person);

    /// <summary>Updates a person's editable fields.</summary>
    Task<PersonDto> UpdatePersonAsync(PersonDto person);

    /// <summary>Deletes a person; event links cascade.</summary>
    Task DeletePersonAsync(string personId);

    /// <summary>Toggles the favorite flag and returns the new state.</summary>
    Task<bool> ToggleFavoriteAsync(string personId);

    /// <summary>Gets summaries of the events linked to a person, newest start date first.</summary>
    Task<List<PersonEventSummary>> GetEventsForPersonAsync(string personId);

    /// <summary>
    /// Legacy name for <see cref="MergeAsync"/>; kept for existing callers and
    /// delegates to it (tombstone semantics — the source is no longer deleted).
    /// </summary>
    Task MergePersonsAsync(string sourcePersonId, string targetPersonId);

    /// <summary>
    /// Merges the source person into the target person: repoints event links
    /// (dedup-safe), backfills empty contact fields, ORs the favorite flag,
    /// keeps the source's name/nickname as aliases of the target, and
    /// TOMBSTONES the source (<see cref="Data.Models.Person.MergedIntoId"/>)
    /// instead of deleting it. A tombstoned target is resolved through its
    /// merge chain first; merging an already-tombstoned source is an
    /// idempotent no-op; source == target (directly or via the chain) throws.
    /// </summary>
    Task MergeAsync(string sourcePersonId, string targetPersonId);

    /// <summary>
    /// Legacy shape of <see cref="SuggestMergesAsync"/>; kept for existing
    /// callers and delegates to it.
    /// </summary>
    Task<List<PersonDuplicatePair>> FindPotentialDuplicatesAsync();

    /// <summary>
    /// Suggests merges: near-duplicate name/nickname pairs PLUS alias
    /// collisions (a living person whose name matches another living person's
    /// alias), each with a human-readable reason.
    /// </summary>
    Task<List<MergeCandidate>> SuggestMergesAsync();

    /// <summary>
    /// Aggregated profile for a person (events, first/last appearance, top
    /// co-occurring people, top locations, aliases). A tombstoned id resolves
    /// through the merge chain to the surviving person. Null when not found.
    /// </summary>
    Task<PersonProfile?> GetProfileAsync(string personId);

    /// <summary>
    /// Adds an alias for a person. Aliases are unique case-insensitively
    /// across all aliases AND all living canonical names: an alias that
    /// already belongs to a different person, or equals another living
    /// person's name, throws <see cref="InvalidOperationException"/> with a
    /// user-facing message. Re-adding the person's own existing alias is an
    /// idempotent no-op.
    /// </summary>
    Task AddAliasAsync(string personId, string alias);

    /// <summary>Gets a person's aliases, ordered case-insensitively. A tombstoned id resolves through the merge chain.</summary>
    Task<List<string>> GetAliasesAsync(string personId);

    /// <summary>Removes an alias (case-insensitive match) from a person; a missing alias is a logged no-op.</summary>
    Task RemoveAliasAsync(string personId, string alias);

    /// <summary>Finds the best existing person for a free-text name, or null when nothing plausible matches.</summary>
    Task<PersonMatch?> FindBestMatchAsync(string name);
}

/// <summary>
/// Person service implementation. Uses short-lived contexts from the factory
/// per operation; event counts and date ranges come from a single grouped
/// query over the event_people junction (no N+1).
/// </summary>
public class PersonService : IPersonService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<PersonService> _logger;
    private readonly ITimelineProjectionPublisher? _projectionPublisher;

    public PersonService(
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<PersonService> logger,
        ITimelineProjectionPublisher? projectionPublisher = null)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        _projectionPublisher = projectionPublisher;
    }

    /// <inheritdoc />
    public async Task<List<PersonDto>> GetAllPersonsAsync(
        PersonSortOption sortBy = PersonSortOption.Name,
        bool favoritesFirst = true)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            // Tombstoned (merged-away) people never appear in lists.
            var people = await context.People.AsNoTracking()
                .Where(p => p.MergedIntoId == null)
                .ToListAsync();
            var stats = await LoadEventStatsAsync(context);

            var dtos = people.Select(p => ToDto(p, stats));
            return SortPersons(dtos, sortBy, favoritesFirst);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all persons");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<List<PersonDto>> SearchPersonsAsync(
        string searchTerm,
        PersonSortOption sortBy = PersonSortOption.Name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                return await GetAllPersonsAsync(sortBy);
            }

            await using var context = await _contextFactory.CreateDbContextAsync();

            // Tombstoned (merged-away) people never appear in search results.
            var people = await context.People.AsNoTracking()
                .Where(p => p.MergedIntoId == null)
                .ToListAsync();
            var stats = await LoadEventStatsAsync(context);

            var term = searchTerm.Trim();
            var matches = people
                .Where(p =>
                    ContainsIgnoreCase(p.Name, term) ||
                    ContainsIgnoreCase(p.Nickname, term) ||
                    ContainsIgnoreCase(p.Email, term) ||
                    ContainsIgnoreCase(p.Company, term) ||
                    ContainsIgnoreCase(p.Relationship, term))
                .Select(p => ToDto(p, stats));

            return SortPersons(matches, sortBy, favoritesFirst: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching persons: {SearchTerm}", searchTerm);
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A tombstoned id resolves through the merge chain to the surviving
    /// person, so callers holding pre-merge ids keep working. Null only when
    /// the id (or its chain) leads nowhere.
    /// </remarks>
    public async Task<PersonDto?> GetPersonAsync(string personId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var person = await ResolveLivingPersonAsync(context, personId);
            if (person == null || person.MergedIntoId != null)
            {
                return null;
            }

            var stats = await LoadEventStatsAsync(context, person.PersonId);
            return ToDto(person, stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting person: {PersonId}", personId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<PersonDto?> GetPersonByNameAsync(string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            await using var context = await _contextFactory.CreateDbContextAsync();

            var lowered = name.Trim().ToLowerInvariant();
            var person = await context.People.AsNoTracking()
                .FirstOrDefaultAsync(p => p.MergedIntoId == null && p.Name.ToLower() == lowered);
            if (person == null)
            {
                // The name may belong to a tombstone (merged-away person);
                // resolve through the chain so callers that hit the duplicate
                // guard on create can still find the surviving contact. A
                // tombstone itself is never returned.
                var tombstone = await context.People.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Name.ToLower() == lowered);
                if (tombstone == null)
                {
                    return null;
                }

                person = await ResolveLivingPersonAsync(context, tombstone.PersonId);
                if (person == null || person.MergedIntoId != null)
                {
                    return null;
                }
            }

            var stats = await LoadEventStatsAsync(context, person.PersonId);
            return ToDto(person, stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting person by name: {Name}", name);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<List<PersonDto>> GetPersonsForEventAsync(string eventId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var people = await context.EventPeople.AsNoTracking()
                .Where(ep => ep.EventId == eventId)
                .Select(ep => ep.Person)
                .Where(p => p.MergedIntoId == null) // merges repoint links, but never surface a stray tombstone
                .OrderBy(p => p.Name)
                .ToListAsync();
            if (people.Count == 0)
            {
                return new List<PersonDto>();
            }

            var stats = await LoadEventStatsAsync(context);
            return people.Select(p => ToDto(p, stats)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting persons for event: {EventId}", eventId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<PersonDto> CreatePersonAsync(PersonDto person)
    {
        try
        {
            var trimmedName = person.Name.Trim();
            if (trimmedName.Length == 0)
            {
                throw new ArgumentException("Person name is required", nameof(person));
            }

            await using var context = await _contextFactory.CreateDbContextAsync();

            // The duplicate check deliberately includes tombstones: a
            // merged-away person keeps its row (and its slot in the NOCASE
            // unique name index), so allowing the insert would fail with a
            // raw constraint violation instead of this clear message.
            var lowered = trimmedName.ToLowerInvariant();
            var duplicate = await context.People.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Name.ToLower() == lowered);
            if (duplicate != null)
            {
                throw new InvalidOperationException(duplicate.MergedIntoId == null
                    ? $"A person named '{trimmedName}' already exists."
                    : $"'{trimmedName}' was merged into another contact; use that contact instead.");
            }

            var entity = person.ToPerson();
            entity.Name = trimmedName;
            if (string.IsNullOrWhiteSpace(entity.PersonId))
            {
                entity.PersonId = Guid.NewGuid().ToString();
            }
            entity.CreatedAt = DateTime.UtcNow;
            entity.UpdatedAt = DateTime.UtcNow;

            context.People.Add(entity);
            await context.SaveChangesAsync();

            _logger.LogInformation("Person created: {PersonId} - {Name}", entity.PersonId, entity.Name);
            SendMessage(new PersonCreatedMessage(entity.PersonId));
            await PublishPersonProjectionAsync(entity.PersonId);

            return PersonDto.FromPerson(entity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating person: {Name}", person.Name);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<PersonDto> UpdatePersonAsync(PersonDto person)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var entity = await context.People
                .FirstOrDefaultAsync(p => p.PersonId == person.PersonId);
            if (entity == null)
            {
                throw new InvalidOperationException($"Person not found: {person.PersonId}");
            }

            // Renaming onto another person's name would trip the NOCASE
            // unique index as a raw constraint violation; reject it with a
            // user-facing message first.
            var newLowered = person.Name.Trim().ToLowerInvariant();
            if (newLowered.Length > 0 &&
                await context.People.AsNoTracking().AnyAsync(p =>
                    p.PersonId != entity.PersonId && p.Name.ToLower() == newLowered))
            {
                throw new InvalidOperationException($"A person named '{person.Name.Trim()}' already exists.");
            }

            person.CopyTo(entity);
            entity.Name = entity.Name.Trim();
            entity.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();

            _logger.LogInformation("Person updated: {PersonId} - {Name}", entity.PersonId, entity.Name);
            SendMessage(new PersonUpdatedMessage(entity.PersonId));
            await PublishPersonProjectionAsync(entity.PersonId);

            var stats = await LoadEventStatsAsync(context, entity.PersonId);
            return ToDto(entity, stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating person: {PersonId}", person.PersonId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DeletePersonAsync(string personId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var entity = await context.People.FirstOrDefaultAsync(p => p.PersonId == personId);
            if (entity == null)
            {
                throw new InvalidOperationException($"Person not found: {personId}");
            }

            context.People.Remove(entity);

            // Also delete tombstones whose merge chain ends at this person:
            // they are empty shells (their links were repointed at merge
            // time), and leaving them would strand unresolvable chains.
            var removedIds = new HashSet<string>(StringComparer.Ordinal) { personId };
            var frontier = new List<string> { personId };
            while (frontier.Count > 0)
            {
                var shells = await context.People
                    .Where(p => p.MergedIntoId != null && frontier.Contains(p.MergedIntoId))
                    .ToListAsync();
                frontier = new List<string>();
                foreach (var shell in shells)
                {
                    if (removedIds.Add(shell.PersonId))
                    {
                        context.People.Remove(shell);
                        frontier.Add(shell.PersonId);
                    }
                }
            }

            await context.SaveChangesAsync();

            _logger.LogInformation("Person deleted: {PersonId}", personId);
            SendMessage(new PersonDeletedMessage(personId));

            // Tombstone every row the delete actually removed, not just the id
            // the caller named. The merged-away shells were each published as
            // an upsert when they were merged (that is how a companion follows
            // an old event's person id to the survivor), so a companion still
            // holds them; leaving them un-tombstoned would strand it on the
            // same unresolvable chains this method just cleaned up locally.
            foreach (var removedId in removedIds)
            {
                await PublishPersonDeletionAsync(removedId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting person: {PersonId}", personId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ToggleFavoriteAsync(string personId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var entity = await context.People.FirstOrDefaultAsync(p => p.PersonId == personId);
            if (entity == null)
            {
                throw new InvalidOperationException($"Person not found: {personId}");
            }

            entity.IsFavorite = !entity.IsFavorite;
            entity.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();

            _logger.LogInformation(
                "Person favorite toggled: {PersonId} -> {IsFavorite}", personId, entity.IsFavorite);
            SendMessage(new PersonUpdatedMessage(personId));
            await PublishPersonProjectionAsync(personId);

            return entity.IsFavorite;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling favorite for person: {PersonId}", personId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<List<PersonEventSummary>> GetEventsForPersonAsync(string personId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            // A tombstoned id resolves to the surviving person (whose links
            // include everything repointed by the merge).
            var living = await ResolveLivingPersonAsync(context, personId);
            var resolvedId = living?.PersonId ?? personId;

            var events = await context.EventPeople.AsNoTracking()
                .Where(ep => ep.PersonId == resolvedId)
                .Select(ep => new
                {
                    ep.Event.EventId,
                    ep.Event.Title,
                    ep.Event.StartDate,
                    ep.Event.EndDate,
                    ep.Event.Category
                })
                .OrderByDescending(e => e.StartDate)
                .ToListAsync();

            return events
                .Select(e => new PersonEventSummary
                {
                    EventId = e.EventId,
                    Title = e.Title,
                    StartDate = e.StartDate,
                    EndDate = e.EndDate,
                    Category = e.Category
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting events for person: {PersonId}", personId);
            throw;
        }
    }

    /// <inheritdoc />
    public Task MergePersonsAsync(string sourcePersonId, string targetPersonId)
        => MergeAsync(sourcePersonId, targetPersonId);

    /// <inheritdoc />
    public async Task MergeAsync(string sourcePersonId, string targetPersonId)
    {
        try
        {
            if (string.Equals(sourcePersonId, targetPersonId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Cannot merge a person into itself.");
            }

            await using var context = await _contextFactory.CreateDbContextAsync();
            await using var transaction = await context.Database.BeginTransactionAsync();

            var source = await context.People.FirstOrDefaultAsync(p => p.PersonId == sourcePersonId);
            if (source == null)
            {
                throw new InvalidOperationException($"Person not found: {sourcePersonId}");
            }

            // Merging an already-tombstoned source is an idempotent no-op:
            // its links, aliases, and contact data moved when it was merged.
            if (source.MergedIntoId != null)
            {
                _logger.LogInformation(
                    "MergeAsync: source {SourcePersonId} is already merged (into {MergedIntoId}); no-op.",
                    sourcePersonId, source.MergedIntoId);
                return;
            }

            var target = await context.People.FirstOrDefaultAsync(p => p.PersonId == targetPersonId);
            if (target == null)
            {
                throw new InvalidOperationException($"Person not found: {targetPersonId}");
            }

            // Merging INTO a tombstone follows its merge chain to the living
            // person first. The visited set terminates a (should-be
            // impossible) tombstone cycle instead of looping forever.
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (target.MergedIntoId != null)
            {
                if (!visited.Add(target.PersonId))
                {
                    throw new InvalidOperationException(
                        "Merge chain contains a cycle; cannot resolve a living target.");
                }

                var nextId = target.MergedIntoId;
                target = await context.People.FirstOrDefaultAsync(p => p.PersonId == nextId)
                    ?? throw new InvalidOperationException($"Merge chain is broken: person not found: {nextId}");
            }

            if (string.Equals(target.PersonId, source.PersonId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Cannot merge a person into itself (the target's merge chain resolves back to the source).");
            }

            // Repoint junction rows from source to target. The composite PK
            // (EventId, PersonId) cannot be updated in place, so rows are
            // removed and re-added; rows that would duplicate an existing
            // target link are simply dropped.
            var sourceLinks = await context.EventPeople
                .Where(ep => ep.PersonId == source.PersonId)
                .ToListAsync();
            var targetEventIds = (await context.EventPeople.AsNoTracking()
                    .Where(ep => ep.PersonId == target.PersonId)
                    .Select(ep => ep.EventId)
                    .ToListAsync())
                .ToHashSet(StringComparer.Ordinal);

            foreach (var link in sourceLinks)
            {
                context.EventPeople.Remove(link);
                if (!targetEventIds.Contains(link.EventId))
                {
                    context.EventPeople.Add(new EventPerson
                    {
                        EventId = link.EventId,
                        PersonId = target.PersonId,
                        CreatedAt = link.CreatedAt
                    });
                    targetEventIds.Add(link.EventId);
                }
            }

            // Move the source's aliases to the target. The alias table is
            // globally NOCASE-unique, so a plain FK move can never collide.
            var sourceAliases = await context.PersonAliases
                .Where(a => a.PersonId == source.PersonId)
                .ToListAsync();
            foreach (var alias in sourceAliases)
            {
                alias.PersonId = target.PersonId;
            }

            // Backfill contact fields the target lacks with the source's values.
            target.Nickname = Backfill(target.Nickname, source.Nickname);
            target.Relationship = Backfill(target.Relationship, source.Relationship);
            target.Email = Backfill(target.Email, source.Email);
            target.Phone = Backfill(target.Phone, source.Phone);
            target.Birthday ??= source.Birthday;
            target.Company = Backfill(target.Company, source.Company);
            target.Notes = Backfill(target.Notes, source.Notes);
            target.PhotoPath = Backfill(target.PhotoPath, source.PhotoPath);
            target.AvatarColor = Backfill(target.AvatarColor, source.AvatarColor);
            target.FirstMetDate ??= source.FirstMetDate;
            if (source.IsFavorite)
            {
                target.IsFavorite = true;
            }
            target.UpdatedAt = DateTime.UtcNow;

            // Keep the source's name and nickname as aliases of the target so
            // future mentions ("Mike" in a transcript) still resolve here.
            await AddMergeAliasAsync(context, target.PersonId, source.PersonId, source.Name);
            if (!string.IsNullOrWhiteSpace(source.Nickname))
            {
                await AddMergeAliasAsync(context, target.PersonId, source.PersonId, source.Nickname);
            }

            // TOMBSTONE the source instead of deleting it: old references
            // (navigation parameters, exports, stale UI) resolve through
            // MergedIntoId to the surviving person.
            source.MergedIntoId = target.PersonId;
            source.UpdatedAt = DateTime.UtcNow;

            await context.SaveChangesAsync();
            await transaction.CommitAsync();

            _logger.LogInformation(
                "Persons merged: {SourcePersonId} -> {TargetPersonId} (source tombstoned)",
                source.PersonId, target.PersonId);
            SendMessage(new PersonsMergedMessage(source.PersonId, target.PersonId));

            // A merge publishes an UPSERT for the merged-away source, never a
            // deletion — which looks wrong until you notice that the archive
            // does not delete it either. The source row survives carrying
            // MergedIntoId, and a projected event that was written before the
            // merge still lists the old person id on purpose (event payloads
            // are not rewritten). If the source were tombstoned here, every one
            // of those events would render a dangling person on the phone;
            // published as an upsert, the companion resolves the id and follows
            // the pointer to the survivor exactly as Windows does.
            //
            // The target is republished because the merge moved event links and
            // backfilled contact fields onto it, so the EventCount, nickname and
            // favorite flag the projection carries have all just changed.
            await PublishPersonProjectionAsync(source.PersonId);
            await PublishPersonProjectionAsync(target.PersonId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Error merging person {SourcePersonId} into {TargetPersonId}",
                sourcePersonId, targetPersonId);
            throw;
        }
    }

    /// <summary>
    /// Adds one merge-produced alias (the source's name or nickname) to the
    /// target, silently skipping collisions: an existing alias with the same
    /// spelling (the table is NOCASE-unique) or another living person's
    /// canonical name. The source is excluded from the living-name check
    /// because it is tombstoned in the same pending SaveChanges.
    /// </summary>
    private async Task AddMergeAliasAsync(
        AppDbContext context,
        string targetPersonId,
        string sourcePersonId,
        string aliasValue)
    {
        var trimmed = aliasValue.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var lowered = trimmed.ToLowerInvariant();

        var aliasTaken =
            context.PersonAliases.Local.Any(a => a.Alias.ToLowerInvariant() == lowered) ||
            await context.PersonAliases.AsNoTracking().AnyAsync(a => a.Alias.ToLower() == lowered);
        if (aliasTaken)
        {
            _logger.LogInformation(
                "MergeAsync: skipping alias '{Alias}' for {TargetPersonId} (spelling already recorded).",
                trimmed, targetPersonId);
            return;
        }

        var nameTaken = await context.People.AsNoTracking().AnyAsync(p =>
            p.MergedIntoId == null &&
            p.PersonId != targetPersonId &&
            p.PersonId != sourcePersonId &&
            p.Name.ToLower() == lowered);
        if (nameTaken)
        {
            _logger.LogInformation(
                "MergeAsync: skipping alias '{Alias}' for {TargetPersonId} (matches another living person's name).",
                trimmed, targetPersonId);
            return;
        }

        context.PersonAliases.Add(new PersonAlias
        {
            PersonId = targetPersonId,
            Alias = trimmed
        });
    }

    /// <inheritdoc />
    public async Task<List<PersonDuplicatePair>> FindPotentialDuplicatesAsync()
    {
        // Legacy shape: delegate to the alias-aware superset so both entry
        // points always agree on what counts as a duplicate.
        var candidates = await SuggestMergesAsync();
        return candidates
            .Select(c => new PersonDuplicatePair { First = c.First, Second = c.Second, Reason = c.Reason })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<List<MergeCandidate>> SuggestMergesAsync()
    {
        try
        {
            var people = await GetAllPersonsAsync(PersonSortOption.Name, favoritesFirst: false);
            var candidates = new List<MergeCandidate>();
            var seenPairs = new HashSet<string>(StringComparer.Ordinal);

            // 1. Near-duplicate names/nicknames (the pre-alias heuristics).
            for (var i = 0; i < people.Count; i++)
            {
                for (var j = i + 1; j < people.Count; j++)
                {
                    var a = people[i];
                    var b = people[j];
                    var reason = GetDuplicateReason(a, b);
                    if (reason != null && seenPairs.Add(PairKey(a.PersonId, b.PersonId)))
                    {
                        candidates.Add(new MergeCandidate { First = a, Second = b, Reason = reason });
                    }
                }
            }

            // 2. Alias collisions: a living person whose canonical name
            //    matches another living person's alias — both records very
            //    likely describe the same human. The alias owner is First
            //    (suggested keeper), the namesake Second (suggested source).
            await using var context = await _contextFactory.CreateDbContextAsync();
            var aliasRows = await context.PersonAliases.AsNoTracking()
                .Select(a => new { a.PersonId, a.Alias })
                .ToListAsync();

            var byId = people.ToDictionary(p => p.PersonId, StringComparer.Ordinal);
            foreach (var aliasRow in aliasRows)
            {
                if (!byId.TryGetValue(aliasRow.PersonId, out var owner))
                {
                    continue; // owner tombstoned or missing
                }

                var namesake = people.FirstOrDefault(p =>
                    p.PersonId != owner.PersonId &&
                    string.Equals(p.Name.Trim(), aliasRow.Alias.Trim(), StringComparison.OrdinalIgnoreCase));
                if (namesake == null)
                {
                    continue;
                }

                if (seenPairs.Add(PairKey(owner.PersonId, namesake.PersonId)))
                {
                    candidates.Add(new MergeCandidate
                    {
                        First = owner,
                        Second = namesake,
                        Reason = $"'{namesake.Name}' matches '{aliasRow.Alias}', an alias of '{owner.Name}'"
                    });
                }
            }

            return candidates;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error suggesting person merges");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<PersonMatch?> FindBestMatchAsync(string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var people = await GetAllPersonsAsync(PersonSortOption.Name, favoritesFirst: false);
            var trimmed = name.Trim();
            var normalized = Normalize(trimmed);

            // 1. Exact name match (case-insensitive).
            var exact = people.FirstOrDefault(p =>
                string.Equals(p.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return new PersonMatch { Person = exact, Kind = PersonMatchKind.Exact };
            }

            // 2. Exact nickname match (case-insensitive).
            var byNickname = people.FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(p.Nickname) &&
                string.Equals(p.Nickname.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
            if (byNickname != null)
            {
                return new PersonMatch { Person = byNickname, Kind = PersonMatchKind.Nickname };
            }

            // 3. Exact alias-table match (case-insensitive), resolved through
            //    the merge chain to the living person. This is what lets
            //    extraction resolve "Bob" to "Robert" instead of creating a
            //    duplicate contact.
            await using (var context = await _contextFactory.CreateDbContextAsync())
            {
                var loweredName = trimmed.ToLowerInvariant();
                var aliasOwnerId = await context.PersonAliases.AsNoTracking()
                    .Where(a => a.Alias.ToLower() == loweredName)
                    .Select(a => a.PersonId)
                    .FirstOrDefaultAsync();
                if (aliasOwnerId != null)
                {
                    var living = await ResolveLivingPersonAsync(context, aliasOwnerId);
                    if (living != null && living.MergedIntoId == null)
                    {
                        var stats = await LoadEventStatsAsync(context, living.PersonId);
                        return new PersonMatch { Person = ToDto(living, stats), Kind = PersonMatchKind.Alias };
                    }
                }
            }

            // 4. Fuzzy: normalized containment or Levenshtein distance <= 2.
            PersonDto? best = null;
            var bestScore = int.MaxValue;
            foreach (var candidate in people)
            {
                var candidateNorm = Normalize(candidate.Name);
                if (candidateNorm.Length == 0)
                {
                    continue;
                }

                int score;
                if (normalized.Length >= 2 &&
                    (candidateNorm.Contains(normalized, StringComparison.Ordinal) ||
                     normalized.Contains(candidateNorm, StringComparison.Ordinal)))
                {
                    // Containment beats any edit-distance match.
                    score = 0;
                }
                else
                {
                    var distance = LevenshteinDistance(normalized, candidateNorm);
                    if (distance > 2)
                    {
                        continue;
                    }
                    score = distance;
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return best == null ? null : new PersonMatch { Person = best, Kind = PersonMatchKind.Fuzzy };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding best person match for: {Name}", name);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<PersonProfile?> GetProfileAsync(string personId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var person = await ResolveLivingPersonAsync(context, personId);
            if (person == null || person.MergedIntoId != null)
            {
                return null;
            }

            var stats = await LoadEventStatsAsync(context, person.PersonId);
            var dto = ToDto(person, stats);

            var eventRows = await context.EventPeople.AsNoTracking()
                .Where(ep => ep.PersonId == person.PersonId)
                .Select(ep => new
                {
                    ep.Event.EventId,
                    ep.Event.Title,
                    ep.Event.StartDate,
                    ep.Event.EndDate,
                    ep.Event.Category
                })
                .OrderByDescending(e => e.StartDate)
                .ToListAsync();

            var events = eventRows
                .Select(e => new PersonEventSummary
                {
                    EventId = e.EventId,
                    Title = e.Title,
                    StartDate = e.StartDate,
                    EndDate = e.EndDate,
                    Category = e.Category
                })
                .ToList();

            var eventIds = eventRows.Select(e => e.EventId).ToList();
            DateTime? firstSeen = eventRows.Count == 0 ? null : eventRows.Min(e => e.StartDate);
            DateTime? lastSeen = eventRows.Count == 0 ? null : eventRows.Max(e => e.StartDate);

            var topCoOccurring = new List<PersonCoOccurrence>();
            var topLocations = new List<PersonLocationCount>();

            if (eventIds.Count > 0)
            {
                // Top co-occurring people (living only), by shared event count.
                var coCounts = await context.EventPeople.AsNoTracking()
                    .Where(ep => eventIds.Contains(ep.EventId) && ep.PersonId != person.PersonId)
                    .GroupBy(ep => ep.PersonId)
                    .Select(g => new { PersonId = g.Key, Count = g.Count() })
                    .ToListAsync();
                var coIds = coCounts.Select(c => c.PersonId).ToList();
                var coPeople = await context.People.AsNoTracking()
                    .Where(p => coIds.Contains(p.PersonId) && p.MergedIntoId == null)
                    .Select(p => new { p.PersonId, p.Name })
                    .ToListAsync();
                topCoOccurring = coCounts
                    .Join(coPeople, c => c.PersonId, p => p.PersonId,
                        (c, p) => new PersonCoOccurrence(p.PersonId, p.Name, c.Count))
                    .OrderByDescending(x => x.SharedEvents)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToList();

                // Top locations of the person's events, by frequency.
                var locationCounts = await context.EventLocations.AsNoTracking()
                    .Where(el => eventIds.Contains(el.EventId))
                    .GroupBy(el => el.LocationId)
                    .Select(g => new { LocationId = g.Key, Count = g.Count() })
                    .ToListAsync();
                var locationIds = locationCounts.Select(l => l.LocationId).ToList();
                var locations = await context.Locations.AsNoTracking()
                    .Where(l => locationIds.Contains(l.LocationId))
                    .Select(l => new { l.LocationId, l.Name })
                    .ToListAsync();
                topLocations = locationCounts
                    .Join(locations, c => c.LocationId, l => l.LocationId,
                        (c, l) => new PersonLocationCount(l.Name, c.Count))
                    .OrderByDescending(x => x.Count)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToList();
            }

            var aliases = await context.PersonAliases.AsNoTracking()
                .Where(a => a.PersonId == person.PersonId)
                .Select(a => a.Alias)
                .ToListAsync();

            return new PersonProfile
            {
                Person = dto,
                Events = events,
                FirstSeen = firstSeen,
                LastSeen = lastSeen,
                TopCoOccurring = topCoOccurring,
                TopLocations = topLocations,
                Aliases = aliases.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building profile for person: {PersonId}", personId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task AddAliasAsync(string personId, string alias)
    {
        try
        {
            var trimmed = (alias ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                throw new ArgumentException("Alias text is required", nameof(alias));
            }
            if (trimmed.Length > 200)
            {
                throw new ArgumentException("Alias is too long (200 characters max)", nameof(alias));
            }

            await using var context = await _contextFactory.CreateDbContextAsync();

            var person = await ResolveLivingPersonAsync(context, personId);
            if (person == null || person.MergedIntoId != null)
            {
                throw new InvalidOperationException($"Person not found: {personId}");
            }

            var lowered = trimmed.ToLowerInvariant();

            // Case-insensitive uniqueness across ALL aliases (mirrors the
            // NOCASE unique index, but fails with a clear message).
            var existingAlias = await context.PersonAliases.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Alias.ToLower() == lowered);
            if (existingAlias != null)
            {
                if (existingAlias.PersonId == person.PersonId)
                {
                    return; // idempotent: this person already has the alias
                }

                var ownerName = await context.People.AsNoTracking()
                    .Where(p => p.PersonId == existingAlias.PersonId)
                    .Select(p => p.Name)
                    .FirstOrDefaultAsync();
                throw new InvalidOperationException(
                    $"'{trimmed}' is already an alias of '{ownerName ?? "another person"}'.");
            }

            // ... and across living canonical names (an alias equal to
            // another person's name would silently absorb their mentions).
            var namesake = await context.People.AsNoTracking()
                .FirstOrDefaultAsync(p =>
                    p.MergedIntoId == null &&
                    p.PersonId != person.PersonId &&
                    p.Name.ToLower() == lowered);
            if (namesake != null)
            {
                throw new InvalidOperationException(
                    $"'{trimmed}' is already the name of '{namesake.Name}'. Merge the two people instead of adding an alias.");
            }

            context.PersonAliases.Add(new PersonAlias
            {
                PersonId = person.PersonId,
                Alias = trimmed
            });
            await context.SaveChangesAsync();

            _logger.LogInformation("Alias '{Alias}' added for person {PersonId}", trimmed, person.PersonId);
            SendMessage(new PersonUpdatedMessage(person.PersonId));
            await PublishPersonProjectionAsync(person.PersonId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding alias '{Alias}' for person {PersonId}", alias, personId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<List<string>> GetAliasesAsync(string personId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var person = await ResolveLivingPersonAsync(context, personId);
            if (person == null)
            {
                return new List<string>();
            }

            var aliases = await context.PersonAliases.AsNoTracking()
                .Where(a => a.PersonId == person.PersonId)
                .Select(a => a.Alias)
                .ToListAsync();

            return aliases.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting aliases for person {PersonId}", personId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task RemoveAliasAsync(string personId, string alias)
    {
        try
        {
            var lowered = (alias ?? string.Empty).Trim().ToLowerInvariant();
            if (lowered.Length == 0)
            {
                return;
            }

            await using var context = await _contextFactory.CreateDbContextAsync();

            var row = await context.PersonAliases
                .FirstOrDefaultAsync(a => a.PersonId == personId && a.Alias.ToLower() == lowered);
            if (row == null)
            {
                _logger.LogInformation(
                    "RemoveAliasAsync: alias '{Alias}' not found on person {PersonId}; no-op.", alias, personId);
                return;
            }

            context.PersonAliases.Remove(row);
            await context.SaveChangesAsync();

            _logger.LogInformation("Alias '{Alias}' removed from person {PersonId}", alias, personId);
            SendMessage(new PersonUpdatedMessage(personId));
            await PublishPersonProjectionAsync(personId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing alias '{Alias}' from person {PersonId}", alias, personId);
            throw;
        }
    }

    // Private helpers

    /// <summary>
    /// Resolves a person id through the merge-tombstone chain to the living
    /// person. Returns null when the id (or the chain) leads nowhere; the
    /// visited set terminates a malformed cycle, in which case the last
    /// tombstone reached is returned (callers check MergedIntoId).
    /// </summary>
    private static async Task<Person?> ResolveLivingPersonAsync(AppDbContext context, string personId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = await context.People.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PersonId == personId);

        while (current != null && current.MergedIntoId != null && visited.Add(current.PersonId))
        {
            var nextId = current.MergedIntoId;
            var next = await context.People.AsNoTracking()
                .FirstOrDefaultAsync(p => p.PersonId == nextId);
            if (next == null)
            {
                // Broken chain (target deleted outside the service): treat
                // the id as unresolvable rather than surfacing a tombstone.
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>Order-independent key for a pair of person ids.</summary>
    private static string PairKey(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

    /// <summary>
    /// Loads per-person event statistics (count plus first/last event start
    /// date) in a single grouped join query over event_people and events.
    /// </summary>
    private static async Task<Dictionary<string, (int Count, DateTime First, DateTime Last)>> LoadEventStatsAsync(
        AppDbContext context,
        string? personId = null)
    {
        var links = context.EventPeople.AsNoTracking();
        if (personId != null)
        {
            links = links.Where(ep => ep.PersonId == personId);
        }

        var stats = await links
            .Join(
                context.Events.AsNoTracking(),
                ep => ep.EventId,
                e => e.EventId,
                (ep, e) => new { ep.PersonId, e.StartDate })
            .GroupBy(x => x.PersonId)
            .Select(g => new
            {
                PersonId = g.Key,
                Count = g.Count(),
                First = g.Min(x => x.StartDate),
                Last = g.Max(x => x.StartDate)
            })
            .ToListAsync();

        return stats.ToDictionary(
            s => s.PersonId,
            s => (s.Count, s.First, s.Last),
            StringComparer.Ordinal);
    }

    /// <summary>Maps an entity to a DTO, filling event statistics from the lookup.</summary>
    private static PersonDto ToDto(
        Person person,
        Dictionary<string, (int Count, DateTime First, DateTime Last)> stats)
    {
        return stats.TryGetValue(person.PersonId, out var s)
            ? PersonDto.FromPerson(person, s.Count, s.First, s.Last)
            : PersonDto.FromPerson(person);
    }

    /// <summary>Sorts persons by the requested option, favorites first when asked.</summary>
    private static List<PersonDto> SortPersons(
        IEnumerable<PersonDto> people,
        PersonSortOption sortBy,
        bool favoritesFirst)
    {
        var ordered = favoritesFirst
            ? people.OrderByDescending(p => p.IsFavorite)
            : people.OrderBy(_ => 0);

        return sortBy switch
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

    /// <summary>
    /// Returns a human-readable reason when the two persons look like
    /// duplicates, or null when they do not.
    /// </summary>
    private static string? GetDuplicateReason(PersonDto a, PersonDto b)
    {
        var nameA = Normalize(a.Name);
        var nameB = Normalize(b.Name);
        if (nameA.Length == 0 || nameB.Length == 0)
        {
            return null;
        }

        if (nameA == nameB)
        {
            return $"Names match: '{a.Name}' and '{b.Name}'";
        }

        var nicknameA = Normalize(a.Nickname ?? string.Empty);
        var nicknameB = Normalize(b.Nickname ?? string.Empty);
        if (nicknameA.Length > 0 && nicknameA == nameB)
        {
            return $"Nickname '{a.Nickname}' of '{a.Name}' matches name '{b.Name}'";
        }
        if (nicknameB.Length > 0 && nicknameB == nameA)
        {
            return $"Nickname '{b.Nickname}' of '{b.Name}' matches name '{a.Name}'";
        }

        if (LevenshteinDistance(nameA, nameB) <= 1)
        {
            return $"Names are very similar: '{a.Name}' and '{b.Name}'";
        }

        return null;
    }

    /// <summary>Trims, lowercases, and collapses inner whitespace for comparisons.</summary>
    private static string Normalize(string value)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', words).ToLowerInvariant();
    }

    /// <summary>Case-insensitive substring check that tolerates null haystacks.</summary>
    private static bool ContainsIgnoreCase(string? haystack, string needle)
    {
        return !string.IsNullOrEmpty(haystack) &&
               haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the source value when the target value is null or whitespace.</summary>
    private static string? Backfill(string? targetValue, string? sourceValue)
    {
        return string.IsNullOrWhiteSpace(targetValue) && !string.IsNullOrWhiteSpace(sourceValue)
            ? sourceValue
            : targetValue;
    }

    /// <summary>
    /// Standard two-row Levenshtein edit distance between two strings.
    /// </summary>
    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }
        if (b.Length == 0)
        {
            return a.Length;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitutionCost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>
    /// Projects a person onto the sync feed so companion devices see a contact
    /// the user just changed (design §19 Phase 3).
    ///
    /// <para>Every call site runs AFTER the archive write has committed, and a
    /// publish failure is logged and swallowed: the sync outbox being
    /// unavailable must never turn a contact that is already saved on disk into
    /// an error the user sees, or worse, roll one back. The feed self-heals —
    /// the next mutation of the same person republishes it in full.</para>
    ///
    /// <para>Call this after every mutation without judging whether the change
    /// was worth sending. The publisher drops a projection byte-identical to
    /// the one it last published for the same person, so an "uninteresting"
    /// publish costs a comparison; a skipped one costs a phone showing stale
    /// data until something else happens to touch the row.</para>
    /// </summary>
    private async Task PublishPersonProjectionAsync(string personId)
    {
        if (_projectionPublisher == null)
        {
            return;
        }

        try
        {
            await _projectionPublisher.PublishPersonAsync(personId);
        }
        catch (Exception publishEx)
        {
            _logger.LogWarning(
                publishEx, "Failed to publish person projection for {PersonId}", personId);
        }
    }

    /// <summary>
    /// Tombstones a person on the sync feed so companions drop their copy.
    /// Reserved for real deletions — a merged-away person keeps its row and is
    /// published through <see cref="PublishPersonProjectionAsync"/> instead.
    /// Failures are logged and swallowed for the same reason as there.
    /// </summary>
    private async Task PublishPersonDeletionAsync(string personId)
    {
        if (_projectionPublisher == null)
        {
            return;
        }

        try
        {
            await _projectionPublisher.PublishDeletedAsync(TimelineProjectionEntity.Person, personId);
        }
        catch (Exception publishEx)
        {
            _logger.LogWarning(
                publishEx, "Failed to publish person deletion for {PersonId}", personId);
        }
    }

    /// <summary>
    /// Publishes a messenger notification; a subscriber failure must not turn
    /// a successful write into an error.
    /// </summary>
    private void SendMessage<TMessage>(TMessage message) where TMessage : class
    {
        try
        {
            WeakReferenceMessenger.Default.Send(message);
        }
        catch (Exception messengerEx)
        {
            _logger.LogWarning(
                messengerEx, "Error publishing {MessageType}", typeof(TMessage).Name);
        }
    }
}
