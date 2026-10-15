namespace Domain.Entities;

public sealed class OpenAiProductFeedProjection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommerceBusinessId { get; set; }
    public Guid CommerceProductId { get; set; }
    public string Provider { get; set; } = "openai";
    public string? ProviderFeedId { get; set; }
    public string? ProviderProductId { get; set; }
    public string Status { get; set; } = "ready";
    public string CanonicalFingerprint { get; set; } = string.Empty;
    public string? LastError { get; set; }
    public DateTime? LastPublishedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public Guid Revision { get; set; } = Guid.NewGuid();
}
