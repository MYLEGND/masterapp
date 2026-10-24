using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Reception;

public sealed partial class ReceptionistAdmissionService
{
    internal async Task<bool> MayPrepareBookingAsync(Guid callId, Guid lease, CancellationToken ct)
    {
        await using var db = new MasterAppDbContext(options);
        var call = await db.Set<ReceptionistCall>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == callId, ct);
        if (call is null || !OwnsLease(call, lease, clock.GetUtcNow().UtcDateTime) || !await AdmissionStillEnabledAsync(db, call, ct)) return false;
        var context = await ContextAsync(db, call, ct);
        return ReceptionistConfigurationCodec.BookingEnabled(context.Configuration, context.Binding, context.ControlledTest);
    }

    /// <summary>One durable attempt across concurrent workers/replay. Uncertain never authorizes a second POST.</summary>
    internal Task<bool> TryBeginBookingAsync(Guid callId, Guid actionId, string argumentsSha256,
        string confirmationReceipt, Guid lease, bool controlledTest, CancellationToken ct) =>
        WithCallAsync(callId, async (db, call) =>
        {
            var action = await db.Set<ReceptionistActionIntent>().SingleOrDefaultAsync(x => x.Id == actionId && x.CallId == callId, ct);
            if (action is null || action.State != "pending" || action.CapabilityKey != ReceptionistCapabilities.CanonicalBooking ||
                action.RouteKey != Bookings.ReceptionistBookingAuthority.RouteKey || action.ArgumentsSha256 != argumentsSha256 ||
                action.ConfirmationReceipt != confirmationReceipt || action.ExecutionLeaseToken != lease ||
                action.ExecutionFence != call.SidebandFence || call.ControlledTest != controlledTest ||
                !OwnsLease(call, lease, clock.GetUtcNow().UtcDateTime) || !await AdmissionStillEnabledAsync(db, call, ct)) return false;
            action.State = "uncertain"; action.VerificationReceipt = "booking-attempt:v1:" + action.AttemptId.ToString("N");
            action.UpdatedUtc = clock.GetUtcNow().UtcDateTime; action.Revision = Guid.NewGuid();
            return true;
        }, ct);

    internal Task<bool> RecordBookingProviderAsync(Guid callId, Guid actionId, string reference,
        string bindingReceipt, CancellationToken ct) => WithCallAsync(callId, async (db, call) =>
        {
            ReceptionistConfigurationCodec.Required(reference, 512, "original provider booking reference");
            ReceptionistConfigurationCodec.Required(bindingReceipt, 512, "original provider booking binding");
            var action = await db.Set<ReceptionistActionIntent>().SingleAsync(x => x.Id == actionId && x.CallId == call.Id, ct);
            if (action.CapabilityKey != ReceptionistCapabilities.CanonicalBooking || action.RouteKey != Bookings.ReceptionistBookingAuthority.RouteKey ||
                action.State != "uncertain" || call.ControlledTest)
                throw new InvalidOperationException("The provider identity requires the original uncertain production booking attempt.");
            if (action.ActualRecordReference is not null)
            {
                if (action.ActualRecordReference != reference || action.VerificationReceipt != bindingReceipt)
                    throw new InvalidOperationException("The original provider booking identity cannot be replaced.");
                return false;
            }
            action.ActualRecordReference = reference; action.VerificationReceipt = bindingReceipt;
            action.UpdatedUtc = clock.GetUtcNow().UtcDateTime; action.Revision = Guid.NewGuid();
            return true;
        }, ct);

    // A definite zero-effect failure may be followed by a newly offered and
    // confirmed slot. The same transaction races the provider-start CAS: only
    // pending can become rejected; uncertain is never made retryable.
    internal Task<bool> RejectUnstartedBookingAsync(Guid callId, Guid actionId, string hash, CancellationToken ct,
        Guid? expectedRevision = null, Func<MasterAppDbContext, Task>? guard = null) =>
        WithCallAsync(callId, async (db, call) =>
        {
            if (guard is not null) await guard(db);
            var action = await db.Set<ReceptionistActionIntent>().SingleAsync(x => x.Id == actionId && x.CallId == call.Id, ct);
            if (expectedRevision.HasValue && action.Revision != expectedRevision)
                throw new DbUpdateConcurrencyException("The unstarted booking action changed.");
            if (IsZeroEffectBookingRejection(action)) return true;
            if (action.State != "pending" || action.ArgumentsSha256 != hash || action.ActualRecordReference is not null ||
                action.CapabilityKey != ReceptionistCapabilities.CanonicalBooking || action.RouteKey != Bookings.ReceptionistBookingAuthority.RouteKey) return false;
            action.State = "rejected"; action.VerificationReceipt = BookingRejectionReceipt(action);
            action.UpdatedUtc = clock.GetUtcNow().UtcDateTime; action.Revision = Guid.NewGuid();
            return true;
        }, ct);

    internal static bool IsZeroEffectBookingRejection(ReceptionistActionIntent action) =>
        action.State == "rejected" && action.ActualRecordReference is null &&
        action.CapabilityKey == ReceptionistCapabilities.CanonicalBooking && action.RouteKey == Bookings.ReceptionistBookingAuthority.RouteKey &&
        action.VerificationReceipt == BookingRejectionReceipt(action);
    internal Task<bool> CompleteBookingReconciliationAsync(Guid callId, Guid actionId, Guid expectedRevision,
        string originalReference, string hash, string receipt, Func<MasterAppDbContext, Task>? guard, CancellationToken ct) =>
        WithCallAsync(callId, async (db, call) =>
        {
            if (guard is not null) await guard(db);
            var action = await db.Set<ReceptionistActionIntent>().SingleAsync(x => x.Id == actionId && x.CallId == call.Id, ct);
            if (action.Revision != expectedRevision || action.State is not ("uncertain" or "verified") ||
                action.ActualRecordReference != originalReference || action.ArgumentsSha256 != hash ||
                action.CapabilityKey != ReceptionistCapabilities.CanonicalBooking || action.RouteKey != Bookings.ReceptionistBookingAuthority.RouteKey)
                throw new DbUpdateConcurrencyException("The original booking evidence changed during readback.");
            if (action.State == "verified")
            {
                if (action.VerificationReceipt != receipt) throw new InvalidOperationException("The verified booking receipt conflicts.");
                return false;
            }
            action.State = "verified"; action.VerificationReceipt = receipt;
            action.UpdatedUtc = clock.GetUtcNow().UtcDateTime; action.Revision = Guid.NewGuid();
            return true;
        }, ct);
    private static string BookingRejectionReceipt(ReceptionistActionIntent action) => "booking-rejected-before-attempt:v1:" +
        ReceptionistConfigurationCodec.Hash($"{action.Id:N}|{action.ArgumentsSha256}|{action.ConfirmationReceipt}|{action.AttemptId:N}");
}
