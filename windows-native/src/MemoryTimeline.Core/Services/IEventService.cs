using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MemoryTimeline.Data;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Service interface for event-related business logic.
/// </summary>
public interface IEventService
{
    /// <summary>
    /// Whether created and approved events are embedded in the background
    /// (<c>auto_generate_embeddings</c>). Explicit re-embedding ignores it.
    /// </summary>
    Task<bool> IsAutoEmbeddingEnabledAsync();

    // CRUD operations
    Task<Event> CreateEventAsync(Event eventData);
    Task<Event?> GetEventByIdAsync(string eventId);
    Task<Event?> GetEventWithDetailsAsync(string eventId);
    Task<IEnumerable<Event>> GetAllEventsAsync();
    Task<Event> UpdateEventAsync(Event eventData);
    Task DeleteEventAsync(string eventId);

    // Query operations
    Task<IEnumerable<Event>> GetEventsByDateRangeAsync(DateTime startDate, DateTime endDate);
    Task<IEnumerable<Event>> GetEventsByCategoryAsync(string category);
    Task<IEnumerable<Event>> GetEventsByEraAsync(string eraId);
    Task<IEnumerable<Event>> SearchEventsAsync(string searchTerm);
    Task<IEnumerable<Event>> GetRecentEventsAsync(int count);

    // Pagination
    Task<(IEnumerable<Event> Events, int TotalCount)> GetPagedEventsAsync(
        int pageNumber,
        int pageSize,
        DateTime? startDate = null,
        DateTime? endDate = null,
        string? category = null);

    // Tags
    Task AddTagToEventAsync(string eventId, string tagId, bool isManual = true, double confidence = 1.0);
    Task RemoveTagFromEventAsync(string eventId, string tagId);
    Task<IEnumerable<Tag>> GetEventTagsAsync(string eventId);

    // People
    Task AddPersonToEventAsync(string eventId, string personId);
    Task RemovePersonFromEventAsync(string eventId, string personId);
    Task<IEnumerable<Person>> GetEventPeopleAsync(string eventId);

    // Locations
    Task AddLocationToEventAsync(string eventId, string locationId);
    Task RemoveLocationFromEventAsync(string eventId, string locationId);
    Task<IEnumerable<Location>> GetEventLocationsAsync(string eventId);

    // Statistics
    Task<int> GetTotalEventCountAsync();
    Task<Dictionary<string, int>> GetEventCountByCategoryAsync();

    // Embeddings
    Task<bool> HasEmbeddingAsync(string eventId);
    Task GenerateEmbeddingAsync(string eventId);

    /// <summary>
    /// Regenerates embeddings for ALL events with the CURRENTLY configured
    /// embedding provider. Rows stored with a different dimension than the
    /// current provider produces are deleted up front, so a cancelled run can
    /// never leave a cross-dimension mix that corrupts similarity math.
    /// Reports (done, total) progress and honors cancellation between events.
    /// </summary>
    /// <returns>The number of events re-embedded.</returns>
    Task<int> ReEmbedAllAsync(IProgress<(int done, int total)>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// Event service implementation with business logic.
/// </summary>
public class EventService : IEventService
{
    private readonly IEventRepository _eventRepository;
    private readonly IEmbeddingService? _embeddingService;
    private readonly IMediaService? _mediaService;
    private readonly EventRevisionWriter? _revisionWriter;
    private readonly ITimelineProjectionPublisher? _projectionPublisher;
    private readonly ISettingsService? _settingsService;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<EventService> _logger;

    /// <summary>
    /// Creates the service. The trailing optional parameters are resolved by
    /// DI when registered and left null otherwise; <paramref name="settingsService"/>
    /// gates the automatic background embedding on <c>auto_generate_embeddings</c>
    /// (absent service or row: embeddings are generated, the seeded default).
    /// </summary>
    public EventService(
        IEventRepository eventRepository,
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<EventService> logger,
        IEmbeddingService? embeddingService = null,
        IMediaService? mediaService = null,
        EventRevisionWriter? revisionWriter = null,
        ITimelineProjectionPublisher? projectionPublisher = null,
        ISettingsService? settingsService = null)
    {
        _eventRepository = eventRepository;
        _contextFactory = contextFactory;
        _logger = logger;
        _embeddingService = embeddingService;
        _mediaService = mediaService;
        _revisionWriter = revisionWriter;
        _projectionPublisher = projectionPublisher;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Whether events should be embedded automatically on creation and
    /// approval (<c>auto_generate_embeddings</c>, seeded true). Explicit
    /// requests — the Connections backfill, "Re-embed all events" — ignore it.
    /// </summary>
    public async Task<bool> IsAutoEmbeddingEnabledAsync()
    {
        if (_settingsService == null)
        {
            return true;
        }

        return await _settingsService.GetSettingAsync(SettingKeys.AutoGenerateEmbeddings, true);
    }

    // CRUD operations

    public async Task<Event> CreateEventAsync(Event eventData)
    {
        try
        {
            eventData.Category = NormalizeCategory(eventData.Category);
            ValidateEvent(eventData);

            eventData.EventId = Guid.NewGuid().ToString();
            eventData.CreatedAt = DateTime.UtcNow;
            eventData.UpdatedAt = DateTime.UtcNow;

            var createdEvent = await _eventRepository.AddAsync(eventData);
            _logger.LogInformation("Event created: {EventId} - {Title}", createdEvent.EventId, createdEvent.Title);

            // Revision history (F12): record the creation-time state. Junctions
            // are always empty at this moment (tags/people/locations attach
            // afterwards via the association APIs). Gated on the
            // revision_history_enabled setting inside the writer; never throws.
            if (_revisionWriter != null)
            {
                await _revisionWriter.TryWriteSnapshotAsync(
                    EventRevisionWriter.BuildSnapshot(
                        createdEvent, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                    RevisionKind.Created);
            }

            // Notify subscribers (e.g. TimelineViewModel) that a new event exists.
            // A subscriber failure must not turn a successful create into an error.
            try
            {
                WeakReferenceMessenger.Default.Send(new EventCreatedMessage(createdEvent.EventId, createdEvent.StartDate));
            }
            catch (Exception messengerEx)
            {
                _logger.LogWarning(messengerEx, "Error publishing EventCreatedMessage for event {EventId}", createdEvent.EventId);
            }

            // Companion devices render a timeline they do not own, so a new
            // memory has to reach the sync feed as well as the local views
            // (design §19 Phase 3). Junctions are still empty here; the
            // association APIs below publish again once they attach.
            await PublishEventProjectionAsync(createdEvent.EventId);

            // Generate embedding asynchronously (fire and forget), unless the
            // user turned automatic embedding off. The background task creates
            // its own DbContext from the factory.
            if (await IsAutoEmbeddingEnabledAsync())
            {
                _ = Task.Run(async () => await GenerateEmbeddingForEventInBackgroundAsync(createdEvent));
            }
            else
            {
                _logger.LogDebug("Automatic embedding is disabled; event {EventId} is not embedded", createdEvent.EventId);
            }

            return createdEvent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating event: {Title}", eventData.Title);
            throw;
        }
    }

    public async Task<Event?> GetEventByIdAsync(string eventId)
    {
        try
        {
            return await _eventRepository.GetByIdAsync(eventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting event by ID: {EventId}", eventId);
            throw;
        }
    }

    public async Task<Event?> GetEventWithDetailsAsync(string eventId)
    {
        try
        {
            return await _eventRepository.GetByIdWithIncludesAsync(eventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting event with details: {EventId}", eventId);
            throw;
        }
    }

    public async Task<IEnumerable<Event>> GetAllEventsAsync()
    {
        try
        {
            return await _eventRepository.GetAllAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all events");
            throw;
        }
    }

    public async Task<Event> UpdateEventAsync(Event eventData)
    {
        try
        {
            eventData.Category = NormalizeCategory(eventData.Category);
            ValidateEvent(eventData);

            var existingEvent = await _eventRepository.GetByIdAsync(eventData.EventId);
            if (existingEvent == null)
            {
                throw new InvalidOperationException($"Event not found: {eventData.EventId}");
            }

            // Revision history (F12): capture the PRE-edit state (scalars +
            // junction names, loaded fresh) BEFORE the update overwrites it,
            // plus a field-by-field changed-fields csv. Failures here are
            // logged and must never fail the update itself.
            EventSnapshot? preEditSnapshot = null;
            string? changedFieldsCsv = null;
            if (_revisionWriter != null)
            {
                try
                {
                    if (await _revisionWriter.IsEnabledAsync())
                    {
                        preEditSnapshot = await _revisionWriter.BuildSnapshotFromDatabaseAsync(eventData.EventId);
                        if (preEditSnapshot != null)
                        {
                            changedFieldsCsv = EventRevisionWriter.ComputeChangedFieldsCsv(preEditSnapshot, eventData);
                        }
                    }
                }
                catch (Exception revisionEx)
                {
                    preEditSnapshot = null;
                    _logger.LogWarning(revisionEx,
                        "Could not capture the pre-edit revision snapshot for event {EventId}; the update proceeds without one.",
                        eventData.EventId);
                }
            }

            eventData.UpdatedAt = DateTime.UtcNow;
            await _eventRepository.UpdateAsync(eventData);

            // The revision is written only after the update succeeded, so a
            // failed save never leaves a phantom Edited revision.
            if (_revisionWriter != null && preEditSnapshot != null)
            {
                await _revisionWriter.TryWriteSnapshotAsync(preEditSnapshot, RevisionKind.Edited, changedFieldsCsv);
            }

            _logger.LogInformation("Event updated: {EventId} - {Title}", eventData.EventId, eventData.Title);

            await PublishEventProjectionAsync(eventData.EventId);

            return eventData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating event: {EventId}", eventData.EventId);
            throw;
        }
    }

    public async Task DeleteEventAsync(string eventId)
    {
        try
        {
            var eventToDelete = await _eventRepository.GetByIdAsync(eventId);
            if (eventToDelete == null)
            {
                throw new InvalidOperationException($"Event not found: {eventId}");
            }

            // Media files live outside the database: capture the event's media
            // rows BEFORE the delete (the FK cascade removes the rows but
            // cannot touch the files), then clean the files up afterwards.
            // Lookup failure must not block the delete itself.
            IReadOnlyList<EventMedia> mediaToClean = Array.Empty<EventMedia>();
            if (_mediaService != null)
            {
                try
                {
                    mediaToClean = await _mediaService.GetForEventAsync(eventId);
                }
                catch (Exception mediaEx)
                {
                    _logger.LogWarning(mediaEx,
                        "Could not enumerate media for event {EventId} before delete; managed files may be orphaned.",
                        eventId);
                }
            }

            // The linked people are captured BEFORE the delete for the same
            // reason the media rows are, and it is even less recoverable: both
            // of event_people's foreign keys are configured ON DELETE CASCADE
            // (AppDbContext), and the repository removes a DETACHED root, so
            // nothing loads the junction into the change tracker and SQLite
            // does the cascade itself. Read it afterwards and it is already
            // empty — the affected counts would silently never be republished,
            // which is exactly the kind of miss that shows up as nothing at all.
            // Skipped when no publisher is registered: the ids would have no
            // reader, and a delete should not pay for a query nobody wants.
            IReadOnlyList<string> linkedPersonIds = Array.Empty<string>();
            if (_projectionPublisher != null)
            {
                try
                {
                    await using var context = await _contextFactory.CreateDbContextAsync();
                    linkedPersonIds = await context.EventPeople
                        .AsNoTracking()
                        .Where(ep => ep.EventId == eventId)
                        .Select(ep => ep.PersonId)
                        .ToListAsync();
                }
                catch (Exception peopleEx)
                {
                    _logger.LogWarning(peopleEx,
                        "Could not enumerate the people linked to event {EventId} before delete; their event counts stay stale on companions until something else touches them.",
                        eventId);
                }
            }

            await _eventRepository.DeleteAsync(eventToDelete);
            _logger.LogInformation("Event deleted: {EventId}", eventId);

            if (_mediaService != null && mediaToClean.Count > 0)
            {
                // Best-effort physical cleanup; DeleteFiles logs any failures.
                _mediaService.DeleteFiles(mediaToClean);
            }

            // Notify subscribers (e.g. TimelineViewModel) that the event is gone.
            // A subscriber failure must not turn a successful delete into an error.
            try
            {
                WeakReferenceMessenger.Default.Send(new EventDeletedMessage(eventId));
            }
            catch (Exception messengerEx)
            {
                _logger.LogWarning(messengerEx, "Error publishing EventDeletedMessage for event {EventId}", eventId);
            }

            // A tombstone, not an upsert: the row is already gone, so
            // PublishEventAsync would find nothing to project and the companion
            // would keep showing a memory the user deleted.
            await PublishEventDeletionAsync(eventId);

            // The cascade dropped one junction row per person, so every one of
            // them is now showing a memory too many. Published AFTER the
            // tombstone because outbox delivery is ordered: a batch that
            // truncates should take the deleted memory away before any count
            // moves. A count that drops while the event is still listed is a
            // contradiction the companion can see in its own data; a count that
            // lags the deletion is the same staleness that existed a moment ago.
            foreach (var personId in linkedPersonIds)
            {
                await PublishPersonProjectionAsync(personId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting event: {EventId}", eventId);
            throw;
        }
    }

    // Query operations

    public async Task<IEnumerable<Event>> GetEventsByDateRangeAsync(DateTime startDate, DateTime endDate)
    {
        try
        {
            return await _eventRepository.GetByDateRangeAsync(startDate, endDate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting events by date range");
            throw;
        }
    }

    public async Task<IEnumerable<Event>> GetEventsByCategoryAsync(string category)
    {
        try
        {
            return await _eventRepository.GetByCategoryAsync(category);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting events by category: {Category}", category);
            throw;
        }
    }

    public async Task<IEnumerable<Event>> GetEventsByEraAsync(string eraId)
    {
        try
        {
            return await _eventRepository.GetByEraAsync(eraId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting events by era: {EraId}", eraId);
            throw;
        }
    }

    public async Task<IEnumerable<Event>> SearchEventsAsync(string searchTerm)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                return Enumerable.Empty<Event>();
            }

            return await _eventRepository.SearchAsync(searchTerm);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching events: {SearchTerm}", searchTerm);
            throw;
        }
    }

    public async Task<IEnumerable<Event>> GetRecentEventsAsync(int count)
    {
        try
        {
            var allEvents = await _eventRepository.GetAllAsync();
            return allEvents
                .OrderByDescending(e => e.CreatedAt)
                .Take(count)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recent events");
            throw;
        }
    }

    // Pagination

    public async Task<(IEnumerable<Event> Events, int TotalCount)> GetPagedEventsAsync(
        int pageNumber,
        int pageSize,
        DateTime? startDate = null,
        DateTime? endDate = null,
        string? category = null)
    {
        try
        {
            if (pageNumber < 1) pageNumber = 1;
            if (pageSize < 1) pageSize = 50;
            if (pageSize > 1000) pageSize = 1000;

            return await _eventRepository.GetPagedAsync(pageNumber, pageSize, startDate, endDate, category);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting paged events");
            throw;
        }
    }

    // Tags

    public async Task AddTagToEventAsync(string eventId, string tagId, bool isManual = true, double confidence = 1.0)
    {
        try
        {
            // Work directly against a fresh context: adding a junction row through a
            // detached Event graph + repository Update marks the new row Modified,
            // which issues an UPDATE affecting 0 rows and throws DbUpdateConcurrencyException.
            await using var context = await _contextFactory.CreateDbContextAsync();

            if (!await context.Events.AnyAsync(e => e.EventId == eventId))
            {
                throw new InvalidOperationException($"Event not found: {eventId}");
            }

            if (!await context.Tags.AnyAsync(t => t.TagId == tagId))
            {
                throw new InvalidOperationException($"Tag not found: {tagId}");
            }

            if (await context.EventTags.AnyAsync(et => et.EventId == eventId && et.TagId == tagId))
            {
                _logger.LogDebug("Tag {TagId} is already associated with event {EventId}", tagId, eventId);
                return;
            }

            context.EventTags.Add(new EventTag
            {
                EventId = eventId,
                TagId = tagId,
                IsManual = isManual,
                ConfidenceScore = confidence,
                CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();

            _logger.LogInformation("Tag {TagId} added to event {EventId}", tagId, eventId);

            // Tags are denormalised into the event projection, so linking one is
            // an event change as far as a phone is concerned. The already-linked
            // early return above writes nothing and therefore publishes nothing.
            await PublishEventProjectionAsync(eventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding tag to event: {EventId}", eventId);
            throw;
        }
    }

    public async Task RemoveTagFromEventAsync(string eventId, string tagId)
    {
        try
        {
            // Delete the junction row directly; removing it from a detached Event
            // graph never issued a DELETE (the repository Update only marked the
            // root entity Modified), so removals silently no-oped.
            await using var context = await _contextFactory.CreateDbContextAsync();

            if (!await context.Events.AnyAsync(e => e.EventId == eventId))
            {
                throw new InvalidOperationException($"Event not found: {eventId}");
            }

            // Composite key order matches AppDbContext: (EventId, TagId)
            var eventTag = await context.EventTags.FindAsync(eventId, tagId);
            if (eventTag != null)
            {
                context.EventTags.Remove(eventTag);
                await context.SaveChangesAsync();
                _logger.LogInformation("Tag {TagId} removed from event {EventId}", tagId, eventId);

                await PublishEventProjectionAsync(eventId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing tag from event: {EventId}", eventId);
            throw;
        }
    }

    public async Task<IEnumerable<Tag>> GetEventTagsAsync(string eventId)
    {
        try
        {
            var eventEntity = await _eventRepository.GetByIdWithIncludesAsync(eventId);
            if (eventEntity == null)
            {
                return Enumerable.Empty<Tag>();
            }

            return eventEntity.EventTags.Select(et => et.Tag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tags for event: {EventId}", eventId);
            throw;
        }
    }

    // People

    public async Task AddPersonToEventAsync(string eventId, string personId)
    {
        try
        {
            // See AddTagToEventAsync: junction rows are written directly with a
            // fresh context instead of through a detached Event graph.
            await using var context = await _contextFactory.CreateDbContextAsync();

            if (!await context.Events.AnyAsync(e => e.EventId == eventId))
            {
                throw new InvalidOperationException($"Event not found: {eventId}");
            }

            if (!await context.People.AnyAsync(p => p.PersonId == personId))
            {
                throw new InvalidOperationException($"Person not found: {personId}");
            }

            if (await context.EventPeople.AnyAsync(ep => ep.EventId == eventId && ep.PersonId == personId))
            {
                _logger.LogDebug("Person {PersonId} is already associated with event {EventId}", personId, eventId);
                return;
            }

            context.EventPeople.Add(new EventPerson
            {
                EventId = eventId,
                PersonId = personId,
                CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();

            _logger.LogInformation("Person {PersonId} added to event {EventId}", personId, eventId);

            // The event projection carries its people inline; who was there is
            // half of what a timeline entry says.
            await PublishEventProjectionAsync(eventId);

            // ...and the person's own projection carries the count derived from
            // the junction row just written, so the event alone is only half the
            // change. The early return above writes nothing and publishes
            // nothing, which is why this costs exactly one extra outbox row per
            // link that actually moved.
            await PublishPersonProjectionAsync(personId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding person to event: {EventId}", eventId);
            throw;
        }
    }

    public async Task RemovePersonFromEventAsync(string eventId, string personId)
    {
        try
        {
            // See RemoveTagFromEventAsync: delete the junction row directly.
            await using var context = await _contextFactory.CreateDbContextAsync();

            if (!await context.Events.AnyAsync(e => e.EventId == eventId))
            {
                throw new InvalidOperationException($"Event not found: {eventId}");
            }

            // Composite key order matches AppDbContext: (EventId, PersonId)
            var eventPerson = await context.EventPeople.FindAsync(eventId, personId);
            if (eventPerson != null)
            {
                context.EventPeople.Remove(eventPerson);
                await context.SaveChangesAsync();
                _logger.LogInformation("Person {PersonId} removed from event {EventId}", personId, eventId);

                await PublishEventProjectionAsync(eventId);
                await PublishPersonProjectionAsync(personId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing person from event: {EventId}", eventId);
            throw;
        }
    }

    public async Task<IEnumerable<Person>> GetEventPeopleAsync(string eventId)
    {
        try
        {
            var eventEntity = await _eventRepository.GetByIdWithIncludesAsync(eventId);
            if (eventEntity == null)
            {
                return Enumerable.Empty<Person>();
            }

            return eventEntity.EventPeople.Select(ep => ep.Person);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting people for event: {EventId}", eventId);
            throw;
        }
    }

    // Locations

    public async Task AddLocationToEventAsync(string eventId, string locationId)
    {
        try
        {
            // See AddTagToEventAsync: junction rows are written directly with a
            // fresh context instead of through a detached Event graph.
            await using var context = await _contextFactory.CreateDbContextAsync();

            if (!await context.Events.AnyAsync(e => e.EventId == eventId))
            {
                throw new InvalidOperationException($"Event not found: {eventId}");
            }

            if (!await context.Locations.AnyAsync(l => l.LocationId == locationId))
            {
                throw new InvalidOperationException($"Location not found: {locationId}");
            }

            if (await context.EventLocations.AnyAsync(el => el.EventId == eventId && el.LocationId == locationId))
            {
                _logger.LogDebug("Location {LocationId} is already associated with event {EventId}", locationId, eventId);
                return;
            }

            context.EventLocations.Add(new EventLocation
            {
                EventId = eventId,
                LocationId = locationId,
                CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();

            _logger.LogInformation("Location {LocationId} added to event {EventId}", locationId, eventId);

            // Locations supply both the place names and the event's single map
            // pin, so a link change can move where a companion draws the memory.
            await PublishEventProjectionAsync(eventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding location to event: {EventId}", eventId);
            throw;
        }
    }

    public async Task RemoveLocationFromEventAsync(string eventId, string locationId)
    {
        try
        {
            // See RemoveTagFromEventAsync: delete the junction row directly.
            await using var context = await _contextFactory.CreateDbContextAsync();

            if (!await context.Events.AnyAsync(e => e.EventId == eventId))
            {
                throw new InvalidOperationException($"Event not found: {eventId}");
            }

            // Composite key order matches AppDbContext: (EventId, LocationId)
            var eventLocation = await context.EventLocations.FindAsync(eventId, locationId);
            if (eventLocation != null)
            {
                context.EventLocations.Remove(eventLocation);
                await context.SaveChangesAsync();
                _logger.LogInformation("Location {LocationId} removed from event {EventId}", locationId, eventId);

                await PublishEventProjectionAsync(eventId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing location from event: {EventId}", eventId);
            throw;
        }
    }

    public async Task<IEnumerable<Location>> GetEventLocationsAsync(string eventId)
    {
        try
        {
            var eventEntity = await _eventRepository.GetByIdWithIncludesAsync(eventId);
            if (eventEntity == null)
            {
                return Enumerable.Empty<Location>();
            }

            return eventEntity.EventLocations.Select(el => el.Location);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting locations for event: {EventId}", eventId);
            throw;
        }
    }

    // Statistics

    public async Task<int> GetTotalEventCountAsync()
    {
        try
        {
            return await _eventRepository.CountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting total event count");
            throw;
        }
    }

    public async Task<Dictionary<string, int>> GetEventCountByCategoryAsync()
    {
        try
        {
            var events = await _eventRepository.GetAllAsync();
            return events
                .Where(e => !string.IsNullOrEmpty(e.Category))
                .GroupBy(e => e.Category!)
                .ToDictionary(g => g.Key, g => g.Count());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting event count by category");
            throw;
        }
    }

    // Private helper methods

    private void ValidateEvent(Event eventData)
    {
        if (string.IsNullOrWhiteSpace(eventData.Title))
        {
            throw new ArgumentException("Event title is required", nameof(eventData.Title));
        }

        if (eventData.Title.Length > 500)
        {
            throw new ArgumentException("Event title cannot exceed 500 characters", nameof(eventData.Title));
        }

        if (eventData.EndDate.HasValue && eventData.EndDate < eventData.StartDate)
        {
            throw new ArgumentException("End date cannot be before start date", nameof(eventData.EndDate));
        }

        if (!string.IsNullOrEmpty(eventData.Category) &&
            !EventCategory.AllCategories.Contains(eventData.Category, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Invalid category: '{eventData.Category}'. Valid categories are: {string.Join(", ", EventCategory.AllCategories)}",
                nameof(eventData.Category));
        }
    }

    /// <summary>
    /// Maps a category to its canonical (lowercase) form from <see cref="EventCategory.AllCategories"/>,
    /// matching case-insensitively (e.g. "Other" -> "other"). Unknown values are returned
    /// unchanged so <see cref="ValidateEvent"/> can reject them with a clear message.
    /// </summary>
    private static string? NormalizeCategory(string? category)
    {
        if (string.IsNullOrEmpty(category))
        {
            return category;
        }

        return EventCategory.AllCategories.FirstOrDefault(
            c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase)) ?? category;
    }

    /// <summary>
    /// Projects the event onto the sync feed so a companion device sees the
    /// change it just missed (design §19 Phase 3). Every caller invokes this
    /// AFTER its archive write has committed, for two reasons: the publisher
    /// re-reads the event to build the payload, so it must see the new state;
    /// and a projection is a convenience while the archive is the memory itself
    /// — an outbox that is full, locked or misconfigured must never turn a
    /// successful save into a failed one, hence the swallow-and-warn.
    ///
    /// No caller-side "is this worth publishing?" test: the publisher already
    /// drops a projection identical to the last one it sent for the event, and
    /// that comparison is far more reliable than a guess made from the outside.
    /// </summary>
    private async Task PublishEventProjectionAsync(string eventId)
    {
        if (_projectionPublisher == null)
        {
            return;
        }

        try
        {
            await _projectionPublisher.PublishEventAsync(eventId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish the timeline projection for event {EventId}", eventId);
        }
    }

    /// <summary>
    /// Projects a person whose linked-event count this service just changed.
    ///
    /// <para>A person projection's <c>EventCount</c> is derived from the
    /// event_people junction, and this service — not PersonService — is its
    /// main writer. Publishing only the event therefore leaves a companion
    /// reading "Dana — 4 memories" indefinitely after Dana was added to a
    /// fifth. Only the paths that actually write the junction call this: a
    /// title edit or a tag link moves nobody's count, so an event with ten
    /// people still costs one outbox row for an ordinary edit.</para>
    ///
    /// <para>Always an upsert, never a tombstone, even for a merged-away
    /// person: that row survives carrying its MergedIntoId, which is how a
    /// companion resolves the old id an earlier event projection still names.
    /// A person who was genuinely deleted is tombstoned by PersonService, and
    /// the publisher no-ops on the missing row rather than resurrecting it as
    /// an upsert from here.</para>
    ///
    /// <para>Same swallow-and-warn as
    /// <see cref="PublishEventProjectionAsync"/>, and for the same reason: the
    /// junction write has already committed.</para>
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish the person projection for {PersonId}", personId);
        }
    }

    /// <summary>
    /// Tombstones a deleted event on the sync feed. Separate from
    /// <see cref="PublishEventProjectionAsync"/> because the row is gone by the
    /// time we know to publish: only the id survives, and only a delete
    /// operation makes a companion drop its copy.
    /// </summary>
    private async Task PublishEventDeletionAsync(string eventId)
    {
        if (_projectionPublisher == null)
        {
            return;
        }

        try
        {
            await _projectionPublisher.PublishDeletedAsync(TimelineProjectionEntity.Event, eventId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish the timeline deletion for event {EventId}", eventId);
        }
    }

    /// <summary>
    /// Fire-and-forget wrapper around <see cref="GenerateEmbeddingForEventAsync"/>:
    /// never throws (embedding generation is non-critical for event creation),
    /// but logs failures at Warning so they are visible.
    /// </summary>
    private async Task GenerateEmbeddingForEventInBackgroundAsync(Event eventData)
    {
        if (_embeddingService == null)
        {
            _logger.LogDebug("Embedding service not available, skipping embedding generation for event {EventId}", eventData.EventId);
            return;
        }

        try
        {
            await GenerateEmbeddingForEventAsync(eventData);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background embedding generation failed for event {EventId}", eventData.EventId);
        }
    }

    /// <summary>
    /// Generates and persists an embedding for the given event using its own
    /// short-lived DbContext from the factory. Throws on failure.
    /// </summary>
    private async Task GenerateEmbeddingForEventAsync(Event eventData)
    {
        if (_embeddingService == null)
        {
            throw new InvalidOperationException("Embedding service is not configured");
        }

        _logger.LogInformation("Generating embedding for event {EventId}", eventData.EventId);

        // Create text representation of event
        var text = ComposeEmbeddingText(eventData);

        // Generate embedding
        var embedding = await _embeddingService.GenerateEmbeddingAsync(text);

        // Persist the ACTIVE provider's identity (IEmbeddingService.ProviderKey,
        // e.g. "openai" / "local") instead of the old hard-coded "openai" string,
        // so the dimension/provider guards in RagService can trust stored rows.
        // ProviderKey (not ModelName) is the provenance field: it stays stable
        // across model-name changes within a provider.
        var providerKey = _embeddingService.ProviderKey;

        var eventEmbedding = new EventEmbedding
        {
            EmbeddingId = Guid.NewGuid().ToString(),
            EventId = eventData.EventId,
            EmbeddingVector = System.Text.Json.JsonSerializer.Serialize(embedding),
            EmbeddingProvider = string.IsNullOrWhiteSpace(providerKey) ? "unknown" : providerKey,
            EmbeddingModel = _embeddingService.ModelName,
            EmbeddingDimension = embedding.Length,
            CreatedAt = DateTime.UtcNow
        };

        // Save embedding to database using a dedicated context (safe from any thread).
        // Replace-then-add in ONE SaveChanges: event_id carries a unique index, so
        // re-embedding an event must remove any previous row (possibly from a
        // different provider) instead of violating the index.
        await using var context = await _contextFactory.CreateDbContextAsync();
        var existingRows = await context.EventEmbeddings
            .Where(ee => ee.EventId == eventData.EventId)
            .ToListAsync();
        if (existingRows.Count > 0)
        {
            context.EventEmbeddings.RemoveRange(existingRows);
        }
        context.EventEmbeddings.Add(eventEmbedding);
        await context.SaveChangesAsync();

        _logger.LogInformation("Embedding generated and saved for event {EventId}", eventData.EventId);
    }

    /// <summary>
    /// The text an event is embedded from: title, or "title. description".
    /// Shared by the per-event embedding path and the re-embed dimension probe
    /// so both always embed identical input.
    /// </summary>
    private static string ComposeEmbeddingText(Event eventData)
        => string.IsNullOrWhiteSpace(eventData.Description)
            ? eventData.Title
            : $"{eventData.Title}. {eventData.Description}";

    // Embeddings

    public async Task<bool> HasEmbeddingAsync(string eventId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.EventEmbeddings
                .AsNoTracking()
                .AnyAsync(ee => ee.EventId == eventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for embedding: {EventId}", eventId);
            return false;
        }
    }

    /// <summary>
    /// Backfill entry point: generates and persists an embedding for an existing event.
    /// Throws on failure so callers (e.g. the Connections backfill UI) can report errors.
    /// </summary>
    public async Task GenerateEmbeddingAsync(string eventId)
    {
        try
        {
            var eventData = await GetEventByIdAsync(eventId);
            if (eventData == null)
            {
                throw new InvalidOperationException($"Event not found: {eventId}");
            }

            await GenerateEmbeddingForEventAsync(eventData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating embedding for event: {EventId}", eventId);
            throw;
        }
    }

    /// <summary>
    /// Regenerates embeddings for every event with the CURRENT provider (used by
    /// the Settings "Re-embed all events" action after a provider switch).
    /// The current dimension is resolved from an actual probe embedding (never
    /// the router's lagging EmbeddingDimension display property), stale-dimension
    /// rows are deleted before anything is persisted so a cancelled run never
    /// leaves a cross-dimension mix, and each event is then re-embedded one at a
    /// time (the per-event upsert replaces same-dimension rows too).
    /// </summary>
    public async Task<int> ReEmbedAllAsync(IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        if (_embeddingService == null)
        {
            throw new InvalidOperationException("Embedding service is not configured");
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var events = (await _eventRepository.GetAllAsync()).ToList();
            var total = events.Count;
            var done = 0;
            progress?.Report((0, total));

            if (total == 0)
            {
                _logger.LogInformation("Re-embed requested but the archive has no events");
                return 0;
            }

            // Resolve the ACTIVE provider's true dimension by generating a
            // probe embedding (nothing is persisted). The routing facade's
            // EmbeddingDimension display property lags behind a just-persisted
            // provider switch (it reflects the provider of the LAST embedding
            // call), so keying the stale delete on it would delete nothing
            // right after a switch — and a cancelled run could then leave the
            // cross-dimension mix this method promises to prevent. The probe
            // also fails fast (bad key, missing model) BEFORE any row is
            // deleted, and its actual vector length is authoritative for any
            // provider. Cost: the first event's text is embedded twice.
            var probeEmbedding = await _embeddingService.GenerateEmbeddingAsync(
                ComposeEmbeddingText(events[0]));
            var currentDimension = probeEmbedding.Length;

            await using (var context = await _contextFactory.CreateDbContextAsync(ct))
            {
                var staleRows = await context.EventEmbeddings
                    .Where(ee => ee.EmbeddingDimension != currentDimension)
                    .ToListAsync(ct);
                if (staleRows.Count > 0)
                {
                    context.EventEmbeddings.RemoveRange(staleRows);
                    await context.SaveChangesAsync(ct);
                    _logger.LogInformation(
                        "Deleted {Count} stale embedding row(s) (dimension != {Dimension}) before re-embedding",
                        staleRows.Count, currentDimension);
                }
            }

            foreach (var eventData in events)
            {
                ct.ThrowIfCancellationRequested();
                await GenerateEmbeddingForEventAsync(eventData);
                done++;
                progress?.Report((done, total));
            }

            _logger.LogInformation(
                "Re-embedded {Done}/{Total} events with provider '{Provider}' ({Model})",
                done, total, _embeddingService.ProviderKey, _embeddingService.ModelName);
            return done;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Re-embed of all events was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error re-embedding all events");
            throw;
        }
    }
}
