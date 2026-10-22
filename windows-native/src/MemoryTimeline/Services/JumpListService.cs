using CommunityToolkit.Mvvm.Messaging;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.StartScreen;

namespace MemoryTimeline.Services;

/// <summary>
/// Windows Jump List service implementation.
/// <para>
/// Two groups are maintained: "Quick Actions" (written once at launch by
/// <see cref="AddQuickActionsAsync"/>) and "Recent Events", which this service
/// keeps current on its own by listening to <see cref="EventCreatedMessage"/>
/// (Core) and <see cref="EventOpenedMessage"/> (head) on the messenger. Each
/// entry launches the app with an <c>event:{id}</c> argument that
/// <c>Program</c>/<c>App.HandleActivationAction</c> route to the timeline.
/// </para>
/// <para>
/// <b>Unpackaged caveat.</b> <c>Windows.UI.StartScreen.JumpList</c> needs
/// package identity. This app ships unpackaged (<c>WindowsPackageType=None</c>),
/// so <c>IsSupported</c>/<c>LoadCurrentAsync</c>/<c>SaveAsync</c> may report
/// unsupported or throw. The first failure is logged once at Warning level and
/// marks the Jump List unavailable for the rest of the session; every later
/// call becomes a silent no-op. Nothing here ever throws to a caller.
/// </para>
/// </summary>
public class JumpListService : IJumpListService
{
    private const string QuickActionsGroup = "Quick Actions";
    private const string RecentEventsGroup = "Recent Events";
    private const int MaxRecentEvents = 10;

    private readonly ILogger<JumpListService> _logger;
    private readonly IEventService _eventService;

    // Jump List writes are load-modify-save; serialize them so two messages
    // arriving together (create + open of the same event) cannot interleave.
    private readonly SemaphoreSlim _jumpListLock = new(1, 1);

    // Set after the first API failure; read without a lock (a benign race
    // only costs one extra logged attempt).
    private volatile bool _unavailable;

    public JumpListService(ILogger<JumpListService> logger, IEventService eventService)
    {
        _logger = logger;
        _eventService = eventService;

        // The service is a DI singleton resolved at launch (AddQuickActionsAsync),
        // so these registrations live for the app's lifetime.
        WeakReferenceMessenger.Default.Register<EventCreatedMessage>(this, static (recipient, message) =>
        {
            ((JumpListService)recipient).TrackEvent(message.EventId);
        });
        WeakReferenceMessenger.Default.Register<EventOpenedMessage>(this, static (recipient, message) =>
        {
            ((JumpListService)recipient).TrackEvent(message.EventId);
        });
    }

    /// <summary>
    /// Fire-and-forget entry point for the messenger handlers: loads the event
    /// and pushes it to the top of "Recent Events". Never throws into the
    /// publisher (which may be a UI command or a Core service).
    /// </summary>
    private void TrackEvent(string eventId)
    {
        if (_unavailable || string.IsNullOrEmpty(eventId))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var evt = await _eventService.GetEventByIdAsync(eventId);
                if (evt == null)
                {
                    _logger.LogDebug("Event {EventId} not found; not added to the Jump List", eventId);
                    return;
                }

                await AddEventToJumpListAsync(evt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not track event {EventId} in the Jump List", eventId);
            }
        });
    }

    /// <summary>
    /// Loads the current Jump List, or returns null (after logging once) when
    /// the API is unsupported or unavailable in this process.
    /// </summary>
    private async Task<JumpList?> TryLoadJumpListAsync(string operation)
    {
        if (_unavailable)
        {
            return null;
        }

        try
        {
            if (!JumpList.IsSupported())
            {
                MarkUnavailable(operation, null);
                return null;
            }

            return await JumpList.LoadCurrentAsync();
        }
        catch (Exception ex)
        {
            MarkUnavailable(operation, ex);
            return null;
        }
    }

    private void MarkUnavailable(string operation, Exception? ex)
    {
        if (_unavailable)
        {
            return;
        }

        _unavailable = true;

        // One warning for the session: an unpackaged install has no package
        // identity, which is the expected cause. Later calls are silent no-ops.
        _logger.LogWarning(ex,
            "Jump List is unavailable in this process ({Operation}); this is expected for an unpackaged " +
            "(WindowsPackageType=None) install, which lacks the package identity the JumpList API needs. " +
            "Jump List updates are disabled for the rest of this session.",
            operation);
    }

    /// <summary>
    /// Updates the Jump List with recent events.
    /// </summary>
    public async Task UpdateRecentEventsAsync(IEnumerable<Event> events)
    {
        await _jumpListLock.WaitAsync();
        try
        {
            var jumpList = await TryLoadJumpListAsync("update recent events");
            if (jumpList == null)
            {
                return;
            }

            _logger.LogInformation("Updating Jump List with recent events");
            jumpList.SystemGroupKind = JumpListSystemGroupKind.None;

            // Clear existing recent items (but keep quick actions)
            var itemsToRemove = jumpList.Items
                .Where(item => item.GroupName == RecentEventsGroup)
                .ToList();

            foreach (var item in itemsToRemove)
            {
                jumpList.Items.Remove(item);
            }

            var added = 0;
            foreach (var evt in events.Take(MaxRecentEvents))
            {
                jumpList.Items.Add(CreateEventItem(evt));
                added++;
            }

            await jumpList.SaveAsync();

            _logger.LogInformation("Jump List updated with {Count} recent events", added);
        }
        catch (Exception ex)
        {
            // Load succeeded but save failed: treat like any other API failure.
            MarkUnavailable("update recent events", ex);
        }
        finally
        {
            _jumpListLock.Release();
        }
    }

    /// <summary>
    /// Adds quick action items to the Jump List.
    /// </summary>
    public async Task AddQuickActionsAsync()
    {
        await _jumpListLock.WaitAsync();
        try
        {
            var jumpList = await TryLoadJumpListAsync("add quick actions");
            if (jumpList == null)
            {
                return;
            }

            _logger.LogInformation("Adding quick actions to Jump List");
            jumpList.SystemGroupKind = JumpListSystemGroupKind.None;

            // Clear existing quick actions
            var itemsToRemove = jumpList.Items
                .Where(item => item.GroupName == QuickActionsGroup)
                .ToList();

            foreach (var item in itemsToRemove)
            {
                jumpList.Items.Remove(item);
            }

            // Add quick actions. The tokens are routed by App.HandleActivationAction:
            // new-event opens the add-event dialog, start-recording starts capturing.
            var quickActions = new[]
            {
                ("action:new-event", "New Event", "Create a new timeline event"),
                ("action:start-recording", "Start Recording", "Begin audio recording"),
                ("action:view-timeline", "View Timeline", "Open timeline view"),
                ("action:search", "Search", "Search events")
            };

            foreach (var (args, title, description) in quickActions)
            {
                var item = JumpListItem.CreateWithArguments(args, title);
                item.GroupName = QuickActionsGroup;
                item.Description = description;

                jumpList.Items.Add(item);
            }

            await jumpList.SaveAsync();

            _logger.LogInformation("Quick actions added to Jump List");
        }
        catch (Exception ex)
        {
            MarkUnavailable("add quick actions", ex);
        }
        finally
        {
            _jumpListLock.Release();
        }
    }

    /// <summary>
    /// Clears all Jump List items.
    /// </summary>
    public async Task ClearJumpListAsync()
    {
        await _jumpListLock.WaitAsync();
        try
        {
            var jumpList = await TryLoadJumpListAsync("clear");
            if (jumpList == null)
            {
                return;
            }

            _logger.LogInformation("Clearing Jump List");
            jumpList.Items.Clear();
            await jumpList.SaveAsync();

            _logger.LogInformation("Jump List cleared");
        }
        catch (Exception ex)
        {
            MarkUnavailable("clear", ex);
        }
        finally
        {
            _jumpListLock.Release();
        }
    }

    /// <summary>
    /// Adds a specific event to the top of the "Recent Events" group (moving it
    /// there if already present) and trims the group to ten entries.
    /// </summary>
    public async Task AddEventToJumpListAsync(Event evt)
    {
        await _jumpListLock.WaitAsync();
        try
        {
            var jumpList = await TryLoadJumpListAsync("add event");
            if (jumpList == null)
            {
                return;
            }

            _logger.LogInformation("Adding event to Jump List: {EventId}", evt.EventId);

            var arguments = $"event:{evt.EventId}";
            var existingItem = jumpList.Items.FirstOrDefault(item => item.Arguments == arguments);
            if (existingItem != null)
            {
                jumpList.Items.Remove(existingItem);
            }

            jumpList.Items.Insert(0, CreateEventItem(evt)); // Add at top

            // Limit to MaxRecentEvents recent events
            var recentItems = jumpList.Items
                .Where(i => i.GroupName == RecentEventsGroup)
                .ToList();

            if (recentItems.Count > MaxRecentEvents)
            {
                foreach (var oldItem in recentItems.Skip(MaxRecentEvents))
                {
                    jumpList.Items.Remove(oldItem);
                }
            }

            await jumpList.SaveAsync();

            _logger.LogInformation("Event added to Jump List");
        }
        catch (Exception ex)
        {
            MarkUnavailable("add event", ex);
        }
        finally
        {
            _jumpListLock.Release();
        }
    }

    private static JumpListItem CreateEventItem(Event evt)
    {
        var item = JumpListItem.CreateWithArguments($"event:{evt.EventId}", evt.Title);
        item.GroupName = RecentEventsGroup;
        item.Description = evt.Description ?? string.Empty;
        // Logo intentionally unset: the unpackaged app has no ms-appx:// assets.
        return item;
    }
}
