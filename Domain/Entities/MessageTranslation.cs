namespace Domain.Entities;

/// <summary>
/// A cached presentation derivative of one authoritative message body. The
/// source message is never overwritten or replaced by translated text.
/// </summary>
public sealed class MessageTranslation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid InternalMessageId { get; set; }

    public string TargetLanguage { get; set; } = string.Empty;

    public string TranslatedText { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public InternalMessage InternalMessage { get; set; } = null!;
}
