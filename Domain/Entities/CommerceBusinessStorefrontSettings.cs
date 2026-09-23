namespace Domain.Entities;

public sealed class CommerceBusinessStorefrontSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CommerceBusinessId { get; set; }
    public CommerceBusiness? CommerceBusiness { get; set; }

    public string BrandHeadline { get; set; } = string.Empty;
    public string BrandSubheadline { get; set; } = string.Empty;
    public string AccentColor { get; set; } = "#926950";
    public string LogoUrl { get; set; } = string.Empty;
    public string StorefrontStatus { get; set; } = "Draft";

    public string PublicFactsJson { get; set; } = "{}";
    public string WorkspacePreferencesJson { get; set; } = "{}";
    public string? ShortBio { get; set; }
    public bool BookingEnabled { get; set; }
    public string? BookingEmbedUrl { get; set; }
    public string? BookingFallbackUrl { get; set; }
    public string? BookingMailboxId { get; set; }
    public string? BookingCalendarEmail { get; set; }
    public string? GlobalStoreCheckoutUrl { get; set; }
    public DateTime? LegacyProfileImportedUtc { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
