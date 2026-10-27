using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Reception;

public sealed partial class ReceptionistAdmissionService
{
    private const string StopCapability = "__provider_stop";

    internal Task<IReadOnlyList<ReceptionistProviderWorkItem>> FindPendingProviderAcceptsAsync(int maximum, CancellationToken ct = default) =>
        FindProviderWorkAsync("reserved", maximum, ct);

    internal Task<IReadOnlyList<ReceptionistProviderWorkItem>> FindUncertainProviderCallsAsync(int maximum, CancellationToken ct = default) =>
        FindProviderWorkAsync("uncertain", maximum, ct);

    internal Task<IReadOnlyList<ReceptionistProviderWorkItem>> FindSidebandCandidatesAsync(int maximum, CancellationToken ct = default) =>
        FindProviderWorkAsync("accepted", maximum, ct);

    /// <summary>
    /// Finds unowned observations within the runtime's supported closing window. Older unresolved calls retain
    /// their reservations and reconciliation requirements without monopolizing automatic provider work batches.
    /// </summary>
    internal async Task<IReadOnlyList<ReceptionistProviderWorkItem>> FindObservationCandidatesAsync(
        int maximum, TimeSpan observationWindow, CancellationToken ct = default)
    {
        if (maximum is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (observationWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(observationWindow));
        var now = clock.GetUtcNow().UtcDateTime;
        var earliestDeadline = now.Subtract(observationWindow);
        await using var read = new MasterAppDbContext(options);
        var stopRequiredIds = CallsRequiringStop(read, now).Select(x => x.Id);
        return await read.Set<ReceptionistCall>().AsNoTracking().Where(call => call.ProviderSessionId != null && call.ProviderClosedUtc == null &&
            call.MustStopUtc >= earliestDeadline && (call.SidebandLeaseExpiresUtc == null || call.SidebandLeaseExpiresUtc <= now) &&
            (call.State == "uncertain" || stopRequiredIds.Contains(call.Id)))
            .OrderByDescending(x => x.MustStopUtc).ThenBy(x => x.Id).Take(maximum)
            .Select(x => new ReceptionistProviderWorkItem(x.Id, x.State, x.MustStopUtc)).ToListAsync(ct);
    }

    /// <summary>Only this claim grants the first accept attempt. The claim commits uncertainty before external I/O.</summary>
    internal async Task<ReceptionistProviderAcceptClaim> ClaimProviderAcceptAsync(Guid callId, string workerId, CancellationToken ct = default)
    {
        ReceptionistConfigurationCodec.Required(workerId, 100, nameof(workerId));
        return await WithCallAsync(callId, async (write, call) =>
        {
            if (call.ProviderSessionId is null || call.BrowserCreationId is not null || call.ProviderAcceptAttemptId is not null || call.State != "reserved" || call.ProviderClosedUtc is not null)
                return new ReceptionistProviderAcceptClaim(false, call.State, call.Id, call.ProviderAcceptAttemptId);
            if (clock.GetUtcNow().UtcDateTime >= call.MustStopUtc || !await AdmissionStillEnabledAsync(write, call, ct))
                return new ReceptionistProviderAcceptClaim(false, "admission_disabled_or_expired", call.Id, null);
            call.ProviderAcceptAttemptId = Guid.NewGuid(); call.ProviderAcceptWorkerId = workerId;
            call.ProviderAcceptAttemptedUtc = clock.GetUtcNow().UtcDateTime;
            call.State = "uncertain"; call.ProviderStateReceipt = "accept-attempt:" + call.ProviderAcceptAttemptId.Value.ToString("N");
            call.Revision = Guid.NewGuid();
            return new ReceptionistProviderAcceptClaim(true, call.State, call.Id, call.ProviderAcceptAttemptId);
        }, ct);
    }

    /// <summary>One fenced owner may interpret and act on a sideband session. Expired owners cannot renew or claim effects.</summary>
    internal async Task<ReceptionistSidebandLease?> AcquireSidebandLeaseAsync(Guid callId, string workerId,
        TimeSpan duration, CancellationToken ct = default)
    {
        ReceptionistConfigurationCodec.Required(workerId, 100, nameof(workerId));
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        return await WithCallAsync<ReceptionistSidebandLease?>(callId, async (write, call) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (call.ProviderSessionId is null || call.State != "accepted" || call.ProviderClosedUtc is not null || call.MustStopUtc <= now ||
                (call.SidebandLeaseExpiresUtc is { } expires && expires > now) || !await AdmissionStillEnabledAsync(write, call, ct)) return null;
            call.SidebandWorkerId = workerId; call.SidebandLeaseToken = Guid.NewGuid();
            call.SidebandFence = checked(call.SidebandFence + 1);
            call.SidebandObservationOnly = false;
            call.SidebandLeaseExpiresUtc = duration >= call.MustStopUtc - now ? call.MustStopUtc : now.Add(duration);
            call.Revision = Guid.NewGuid();
            return Lease(call);
        }, ct);
    }

    internal async Task<ReceptionistSidebandLease?> RenewSidebandLeaseAsync(Guid callId, string workerId,
        Guid leaseToken, TimeSpan duration, CancellationToken ct = default)
    {
        ReceptionistConfigurationCodec.Required(workerId, 100, nameof(workerId));
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        return await WithCallAsync<ReceptionistSidebandLease?>(callId, async (write, call) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (!OwnsLease(call, leaseToken, now) || call.SidebandWorkerId != workerId || !await AdmissionStillEnabledAsync(write, call, ct)) return null;
            call.SidebandLeaseExpiresUtc = duration >= call.MustStopUtc - now ? call.MustStopUtc : now.Add(duration);
            call.Revision = Guid.NewGuid();
            return Lease(call);
        }, ct);
    }

    /// <summary>The runtime must fence every sideband send and close its socket when this check/renewal fails.</summary>
    internal async Task<bool> ValidateSidebandLeaseAsync(Guid callId, Guid leaseToken, CancellationToken ct = default)
    {
        await using var read = new MasterAppDbContext(options);
        var call = await read.Set<ReceptionistCall>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == callId, ct);
        return call is not null && OwnsLease(call, leaseToken, clock.GetUtcNow().UtcDateTime) && await AdmissionStillEnabledAsync(read, call, ct);
    }

    /// <summary>Observation shares the dialogue fence but cannot grant a business effect or outbound dialogue.</summary>
    internal async Task<ReceptionistSidebandLease?> AcquireObservationLeaseAsync(Guid callId, string workerId,
        TimeSpan duration, CancellationToken ct = default)
    {
        ReceptionistConfigurationCodec.Required(workerId, 100, nameof(workerId));
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        return await WithCallAsync<ReceptionistSidebandLease?>(callId, async (write, call) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (call.ProviderSessionId is null || call.ProviderClosedUtc is not null || (call.SidebandLeaseExpiresUtc is { } expires && expires > now) ||
                (call.State == "reserved" && now < call.MustStopUtc && await AdmissionStillEnabledAsync(write, call, ct))) return null;
            call.SidebandWorkerId = workerId; call.SidebandLeaseToken = Guid.NewGuid();
            call.SidebandFence = checked(call.SidebandFence + 1); call.SidebandObservationOnly = true;
            call.SidebandLeaseExpiresUtc = now.Add(duration); call.Revision = Guid.NewGuid();
            return Lease(call);
        }, ct);
    }

    /// <summary>Keeps the existing receive socket at a deadline/kill while forbidding dialogue and business actions.</summary>
    internal async Task<ReceptionistSidebandLease?> ConvertSidebandToObservationAsync(Guid callId, string workerId,
        Guid leaseToken, TimeSpan duration, CancellationToken ct = default)
    {
        ReceptionistConfigurationCodec.Required(workerId, 100, nameof(workerId));
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        return await WithCallAsync<ReceptionistSidebandLease?>(callId, async (write, call) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (leaseToken == Guid.Empty || call.ProviderClosedUtc is not null || call.SidebandWorkerId != workerId || call.SidebandLeaseToken != leaseToken) return null;
            if (call.SidebandLeaseExpiresUtc <= now && (call.SidebandObservationOnly ||
                (now < call.MustStopUtc && await AdmissionStillEnabledAsync(write, call, ct)))) return null;
            if (!call.SidebandObservationOnly) call.SidebandFence = checked(call.SidebandFence + 1);
            call.SidebandObservationOnly = true; call.SidebandLeaseExpiresUtc = now.Add(duration); call.Revision = Guid.NewGuid();
            return Lease(call);
        }, ct);
    }

    internal async Task<ReceptionistSidebandLease?> RenewObservationLeaseAsync(Guid callId, string workerId,
        Guid leaseToken, TimeSpan duration, CancellationToken ct = default)
    {
        ReceptionistConfigurationCodec.Required(workerId, 100, nameof(workerId));
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        return await WithCallAsync<ReceptionistSidebandLease?>(callId, (write, call) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (!OwnsObservationLease(call, leaseToken, now) || call.SidebandWorkerId != workerId) return Task.FromResult<ReceptionistSidebandLease?>(null);
            call.SidebandLeaseExpiresUtc = now.Add(duration); call.Revision = Guid.NewGuid();
            return Task.FromResult<ReceptionistSidebandLease?>(Lease(call));
        }, ct);
    }

    internal async Task<bool> ValidateObservationLeaseAsync(Guid callId, Guid leaseToken, CancellationToken ct = default)
    {
        await using var read = new MasterAppDbContext(options);
        var call = await read.Set<ReceptionistCall>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == callId, ct);
        return call is not null && OwnsObservationLease(call, leaseToken, clock.GetUtcNow().UtcDateTime);
    }

    /// <summary>Promotion needs actual accepted state plus current admission; the former observation token cannot send.</summary>
    internal async Task<ReceptionistSidebandLease?> PromoteObservationLeaseAsync(Guid callId, string workerId,
        Guid leaseToken, TimeSpan duration, CancellationToken ct = default)
    {
        ReceptionistConfigurationCodec.Required(workerId, 100, nameof(workerId));
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        return await WithCallAsync<ReceptionistSidebandLease?>(callId, async (write, call) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (!OwnsObservationLease(call, leaseToken, now) || call.SidebandWorkerId != workerId || call.State != "accepted" ||
                call.MustStopUtc <= now || !await AdmissionStillEnabledAsync(write, call, ct)) return null;
            call.SidebandObservationOnly = false; call.SidebandLeaseToken = Guid.NewGuid(); call.SidebandFence = checked(call.SidebandFence + 1);
            call.SidebandLeaseExpiresUtc = duration >= call.MustStopUtc - now ? call.MustStopUtc : now.Add(duration);
            call.Revision = Guid.NewGuid();
            return Lease(call);
        }, ct);
    }

    /// <summary>Stop is a provider control action. A retry needs a newer verified observation that the call remains active.</summary>
    internal async Task<ReceptionistActionClaim> ClaimProviderStopAsync(Guid callId, string workerId,
        ReceptionistVerifiedProviderObservation? observation = null, CancellationToken ct = default)
    {
        ReceptionistConfigurationCodec.Required(workerId, 100, nameof(workerId));
        if (observation is not null)
        {
            ReceptionistConfigurationCodec.Required(observation.Receipt, 512, nameof(observation.Receipt));
            if (observation.ObservedUtc.Kind != DateTimeKind.Utc || observation.ObservedUtc > clock.GetUtcNow().UtcDateTime)
                throw new ArgumentException("A verified current UTC provider observation is required.");
        }
        return await WithCallAsync(callId, async (write, call) =>
        {
            if (call.ProviderSessionId is null) return new ReceptionistActionClaim(false, "provider_identity_unbound", null);
            if (call.ProviderClosedUtc is not null) return new ReceptionistActionClaim(false, "provider_closed", null);
            var now = clock.GetUtcNow().UtcDateTime;
            var reason = now >= call.MustStopUtc ? "deadline" : !await AdmissionStillEnabledAsync(write, call, ct) ? "kill_switch" : null;
            if (reason is null) return new ReceptionistActionClaim(false, "stop_not_required", null);
            var previous = await write.Set<ReceptionistActionIntent>().Where(x => x.CallId == callId && x.CapabilityKey == StopCapability)
                .OrderByDescending(x => x.AttemptNumber).FirstOrDefaultAsync(ct);
            var unusedBrowserCleanup = previous?.ConfirmationReceipt == BrowserCleanupReceipt &&
                previous.State == "rejected" && previous.VerificationReceipt == BrowserCleanupReleased;
            if (previous?.ConfirmationReceipt == BrowserCleanupReceipt && !unusedBrowserCleanup &&
                (now < previous.CreatedUtc.Add(BrowserCleanupLifetime) || observation is null ||
                    observation.ObservedUtc < previous.CreatedUtc.Add(BrowserCleanupLifetime)))
                return new ReceptionistActionClaim(false, "browser_cleanup_owned", previous.Id);
            if (previous is not null && !unusedBrowserCleanup && (previous.State == "verified" || observation is null || !observation.Active ||
                observation.ObservedUtc <= previous.UpdatedUtc || observation.Receipt == previous.ConfirmationReceipt || observation.Receipt == previous.VerificationReceipt))
                return new ReceptionistActionClaim(false, previous.State, previous.Id, previous.RouteKey,
                    previous.ActualRecordReference, previous.VerificationReceipt, previous.AttemptId);
            var attempt = checked((previous?.AttemptNumber ?? 0) + 1);
            var action = new ReceptionistActionIntent
            {
                CallId = callId, LogicalActionKey = "provider_stop/" + attempt, CapabilityKey = StopCapability, RouteKey = "provider.stop",
                ArgumentsSha256 = ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new { call.Id, call.Provider, call.ProviderProjectKey, call.ProviderSessionId })),
                ConfirmationReceipt = observation?.Receipt ?? reason + ":" + now.ToString("O"), WorkerId = workerId,
                AttemptNumber = attempt, CreatedUtc = now, UpdatedUtc = now
            };
            write.Add(action);
            return new ReceptionistActionClaim(true, action.State, action.Id, action.RouteKey, AttemptId: action.AttemptId);
        }, ct);
    }

    private async Task<IReadOnlyList<ReceptionistProviderWorkItem>> FindProviderWorkAsync(string state, int maximum, CancellationToken ct)
    {
        if (maximum is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximum));
        var now = clock.GetUtcNow().UtcDateTime;
        await using var read = new MasterAppDbContext(options);
        return await read.Set<ReceptionistCall>().AsNoTracking().Where(call => call.ProviderSessionId != null && call.State == state && call.ProviderClosedUtc == null &&
            call.MustStopUtc > now && (state != "reserved" || call.ProviderAcceptAttemptId == null) &&
            (call.SidebandLeaseExpiresUtc == null || call.SidebandLeaseExpiresUtc <= now))
            .OrderBy(x => x.ReservedUtc).ThenBy(x => x.Id).Take(maximum)
            .Select(x => new ReceptionistProviderWorkItem(x.Id, x.State, x.MustStopUtc)).ToListAsync(ct);
    }

    private static bool OwnsLease(ReceptionistCall call, Guid leaseToken, DateTime now) =>
        call.ProviderSessionId != null && leaseToken != Guid.Empty && call.SidebandLeaseToken == leaseToken && call.SidebandLeaseExpiresUtc > now &&
        !call.SidebandObservationOnly && call.State == "accepted" && call.ProviderClosedUtc is null && call.MustStopUtc > now;

    private static bool OwnsObservationLease(ReceptionistCall call, Guid leaseToken, DateTime now) =>
        call.ProviderSessionId != null && leaseToken != Guid.Empty && call.SidebandLeaseToken == leaseToken && call.SidebandLeaseExpiresUtc > now &&
        call.SidebandObservationOnly && call.ProviderClosedUtc is null;

    private static ReceptionistSidebandLease Lease(ReceptionistCall call) =>
        new(call.Id, call.SidebandWorkerId!, call.SidebandLeaseToken!.Value, call.SidebandFence, call.SidebandLeaseExpiresUtc!.Value, call.SidebandObservationOnly);
}
