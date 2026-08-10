namespace Domain.Entities;

/// <summary>
/// A typed account's platform delivery registration. A token belongs to one
/// selected Legend role at a time so agent and client notification state cannot
/// cross paths. Notification creation remains server-owned and provider-neutral.
/// </summary>
public sealed class MobilePushDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string UserId { get; set; } = string.Empty;

    public string ParticipantType { get; set; } = string.Empty;

    public string DeviceToken { get; set; } = string.Empty;

    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Transport only: <c>apns</c> or <c>fcm</c>.</summary>
    public string Provider { get; set; } = MobilePushProviders.Apns;

    /// <summary>
    /// APNs environment (sandbox/production); <c>not-applicable</c> for FCM.
    /// Kept on the shared row so existing APNs registrations remain unchanged.
    /// </summary>
    public string Environment { get; set; } = "production";

    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastSeenUtc { get; set; }

    public DateTime? InvalidatedUtc { get; set; }
}

public static class MobilePushProviders
{
    public const string Apns = "apns";
    public const string Fcm = "fcm";
}
