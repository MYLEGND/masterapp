namespace Domain.Entities;

/// <summary>
/// An authored Daily Scripture passage for one Legend business date. The raw
/// passage is retained exactly as supplied; presentation-only verse formatting
/// is derived when the mobile client renders it.
/// </summary>
public sealed class DailyScriptureOverride
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateOnly DisplayDate { get; set; }

    public string Reference { get; set; } = string.Empty;

    public string Translation { get; set; } = string.Empty;

    public string PassageText { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public string CreatedByUserId { get; set; } = string.Empty;

    public string CreatedByParticipantType { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public string UpdatedByUserId { get; set; } = string.Empty;

    public string UpdatedByParticipantType { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
