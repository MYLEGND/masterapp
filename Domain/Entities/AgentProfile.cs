namespace Domain.Entities;

/// <summary>
/// Stores per-agent profile data that needs to persist across client creations
/// (e.g., licensing info such as NPN) so the agent does not have to retype it.
/// </summary>
public class AgentProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string AgentUserId { get; set; } = ""; // canonical OID
    public string AgentUpn { get; set; } = "";    // email/UPN for self-healing
    public string? NormalizedEmail { get; set; }

    public string? FullName { get; set; }
    public string? Title { get; set; }
    public string? Npn { get; set; }
    public string? Phone { get; set; }
    public string? ShortBio { get; set; }
    public string? MetaPixelId { get; set; }
    public string? MetaCapiAccessToken { get; set; }
    public string? MetaTestEventCode { get; set; }
    public bool? BookingEnabled { get; set; }
    public string? MicrosoftBookingsEmbedUrl { get; set; }
    public string? FallbackBookingUrl { get; set; }
    public string? BookingPageIdOrMailbox { get; set; }
    public string? CalendarUserId { get; set; }
    public string? CalendarEmail { get; set; }
    public bool? PreferModalOnMobile { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime? DeactivatedUtc { get; set; }
    public string? DeactivationReason { get; set; }

    public int? DisplayOrder { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
