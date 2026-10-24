using Shared.Analytics;

namespace Infrastructure.Bookings;

// In-process deterministic backend contracts only. They are never model/tool
// schemas, MVC request models or serialized responses. No arbitrary customer IDs
// from a caller are accepted by the application booking authority.
public sealed record ReceptionistTrustedBookingTarget(MarketingOwnerScope Owner, Guid IntakeLinkId,
    string WorkstationLeadId, Guid? BookingAgentProfileId, ReceptionistBookingService Service,
    string CalendarConfigurationSha256, string CalendarTargetSha256, string ConfirmedFactsSha256, string InvitationEmail, string CustomerName);
public sealed record ReceptionistCalendarSlot(DateTime StartUtc, DateTime EndUtc, string StaffId, string TargetSha256);
public sealed record ReceptionistTrustedBookingCommit(ReceptionistTrustedBookingTarget Target,
    ReceptionistCalendarSlot Slot, bool CustomerEmailConsent);
public sealed record ReceptionistProviderBookingIdentity(string AppointmentId, string TargetSha256);

public interface IReceptionistBookingExecutionFence
{
    Task<bool> TryStartProviderAsync(CancellationToken ct);
    Task ProviderCreatedAsync(ReceptionistProviderBookingIdentity identity, CancellationToken ct);
}

/// <summary>Adapter to the existing CalendarController; no alternate scheduler or CRM writer.</summary>
public interface IReceptionistBookingGateway
{
    Task<IReadOnlyList<ReceptionistCalendarSlot>> SlotsAsync(ReceptionistTrustedBookingTarget target,
        DateOnly date, int minimumNoticeMinutes, int maximumAdvanceDays, CancellationToken ct);
    Task CreateAsync(ReceptionistTrustedBookingCommit command, IReceptionistBookingExecutionFence fence, CancellationToken ct);
    Task<ReceptionistCalendarSlot?> ReadOriginalSlotAsync(ReceptionistTrustedBookingTarget target,
        string originalAppointmentId, CancellationToken ct);
    Task<bool> VerifyAsync(ReceptionistTrustedBookingCommit command, ReceptionistProviderBookingIdentity identity, CancellationToken ct);
}

// Dialogue receives only slots plus opaque purpose handles and a bounded result.
// Internal identities/confirmed-facts receipts never enter model-facing text.
internal sealed record ReceptionistBookingOption(string Handle, DateTime StartUtc, DateTime EndUtc,
    string TimeZoneId, string PublicServiceLabel, bool Synthetic);
internal sealed record ReceptionistBookingResult(string State, bool Verified, bool Synthetic);
internal interface IReceptionistApplicationBooking
{
    Task<IReadOnlyList<ReceptionistBookingOption>> PrepareAsync(Infrastructure.Reception.ReceptionistCallContext call,
        Guid intakeActionId, string serviceKey, DateOnly date, Guid lease, CancellationToken ct);
    Task<ReceptionistBookingResult> CommitAsync(Infrastructure.Reception.ReceptionistCallContext call,
        string handle, bool appointmentConsent, bool emailInvitationConsent, string confirmationReceipt, Guid lease, CancellationToken ct);
}
