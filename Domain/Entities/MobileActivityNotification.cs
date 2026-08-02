namespace Domain.Entities;

/// <summary>
/// A recipient-scoped mobile activity event. These events are intentionally
/// separate from conversations so administrative decisions never create an
/// inbox thread or expose the private Founder review queue.
/// </summary>
public sealed class MobileActivityNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string RecipientUserId { get; set; } = string.Empty;

    public string RecipientParticipantType { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// The originating controlled-resource request. One decision produces one
    /// activity event, enforced by a unique database index.
    /// </summary>
    public Guid? ControlledResourceRequestId { get; set; }

    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;
}
