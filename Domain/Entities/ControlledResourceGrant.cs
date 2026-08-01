namespace Domain.Entities;

/// <summary>
/// The single durable grant record for a founder-controlled resource. It is
/// intentionally participant-typed so a shared Entra user ID cannot inherit a
/// resource through a different Legend role.
/// </summary>
public sealed class ControlledResourceGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string UserId { get; set; } = string.Empty;

    public string ParticipantType { get; set; } = string.Empty;

    public string ResourceType { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public DateTime GrantedUtc { get; set; } = DateTime.UtcNow;

    public string GrantedByUserId { get; set; } = string.Empty;

    public DateTime? RevokedUtc { get; set; }

    public string? RevokedByUserId { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
