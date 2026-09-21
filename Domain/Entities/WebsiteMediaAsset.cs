namespace Domain.Entities;

public sealed class WebsiteMediaAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerKey { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
