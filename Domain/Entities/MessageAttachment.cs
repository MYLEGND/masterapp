namespace Domain.Entities;

public class MessageAttachment
{
    public Guid Id { get; set; }

    public Guid InternalMessageId { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    public string StoredFileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string StoragePath { get; set; } = string.Empty;

    public string ScanStatus { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public InternalMessage InternalMessage { get; set; } = null!;
}
