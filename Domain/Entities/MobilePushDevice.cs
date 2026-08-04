namespace Domain.Entities;

/// <summary>
/// A typed account's APNs registration. A token belongs to one selected Legend
/// role at a time so agent and client notification state cannot cross paths.
/// </summary>
public sealed class MobilePushDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string UserId { get; set; } = string.Empty;

    public string ParticipantType { get; set; } = string.Empty;

    public string DeviceToken { get; set; } = string.Empty;

    public string TokenHash { get; set; } = string.Empty;

    public string Environment { get; set; } = "production";

    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastSeenUtc { get; set; }

    public DateTime? InvalidatedUtc { get; set; }
}
