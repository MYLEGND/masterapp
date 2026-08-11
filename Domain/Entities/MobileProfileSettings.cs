namespace Domain.Entities;

/// <summary>
/// Member-controlled presentation settings for the native Legend profile.
/// This deliberately sits outside AgentProfile and ClientProfile so a member's
/// mobile identity, public contact choices, and profile details never overwrite
/// data synchronized from the web applications or directory.
/// </summary>
public sealed class MobileProfileSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public string ParticipantType { get; set; } = string.Empty;

    public string? Username { get; set; }
    public string? NormalizedUsername { get; set; }
    /// Calendar month and count for member-initiated username changes. The first
    /// username reservation is not a change; subsequent changes are capped by the
    /// mobile profile authority at two per calendar month.
    public DateTime? UsernameChangeMonthUtc { get; set; }
    public int UsernameChangeCount { get; set; }
    public string? Bio { get; set; }
    public string? Website { get; set; }
    public string? Location { get; set; }

    // This is a member-entered mobile-profile address, never their authenticated
    // account email. It is returned to the owner for editing and only shown on a
    // profile when IsEmailVisible is enabled.
    public string? PublicEmail { get; set; }
    public bool IsEmailVisible { get; set; }

    // The account phone remains owned by the typed AgentProfile or ClientProfile.
    // This flag controls only whether that existing number is presented on the
    // mobile profile; it never creates a second copy of contact data.
    public bool IsPhoneVisible { get; set; }

    /// <summary>
    /// The server-validated BCP-47-style communication preference. It stays
    /// saved across an entitlement revocation but is usable only while the
    /// language-translation resource is granted.
    /// </summary>
    public string? PreferredCommunicationLanguage { get; set; }

    // Public is the default. A private profile keeps its public identity
    // discoverable, but its social content is limited to approved followers.
    public bool IsPrivate { get; set; }

    /// <summary>
    /// Explicit, member-controlled consent for future successful translated
    /// conversations to enter the governed Legend Connect learning pipeline.
    /// This is false by default: translation itself never implies consent to
    /// retain private conversation text as learning knowledge.
    /// </summary>
    public bool AllowsConsentedTranslationLearning { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
