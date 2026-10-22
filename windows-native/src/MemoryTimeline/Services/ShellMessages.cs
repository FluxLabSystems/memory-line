namespace MemoryTimeline.Services;

// Head-only messenger signals, published through
// CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.
//
// The cross-feature domain messages (EventCreatedMessage, EventUpdatedMessage,
// DraftsChangedMessage, ...) live in MemoryTimeline.Core/Services/Messages.cs
// because Core services raise them. The records below are raised only by the
// WinUI head, for shell-level concerns (navigation badges, the jump list), so
// they stay in the head rather than widening Core's message surface.

/// <summary>
/// Published after the recording queue's contents changed from the Queue page
/// (recording stopped, text pasted, audio imported, item removed, completed
/// items cleared) so the shell's Queue badge can re-count.
/// </summary>
public sealed record QueueChangedMessage;

/// <summary>
/// Published after pending events were approved or rejected on the Review page
/// so the shell's Review badge can re-count. Approval also raises Core's
/// <c>EventCreatedMessage</c>; rejection raises nothing else, which is why
/// this message exists.
/// </summary>
public sealed record PendingEventsChangedMessage;

/// <summary>
/// Published when the user opens an event — selecting it on the timeline or
/// arriving through a "view on timeline" / deep-link navigation — so the jump
/// list's "Recent Events" group can track it.
/// </summary>
public sealed record EventOpenedMessage(string EventId);
