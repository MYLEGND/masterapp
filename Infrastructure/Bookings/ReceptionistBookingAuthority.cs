using System.Security.Cryptography;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Leads;
using Infrastructure.Reception;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Analytics;

namespace Infrastructure.Bookings;

/// <summary>Purpose/consent orchestration over canonical intake, action fences and the existing calendar engine.</summary>
internal sealed class ReceptionistBookingAuthority(DbContextOptions<MasterAppDbContext> options,
    ReceptionistAdmissionService admission, ReceptionistInquiryAuthority inquiries,
    IDataProtectionProvider protection, IServiceProvider services, TimeProvider clock) : IReceptionistApplicationBooking
{
    internal const string RouteKey = "canonical-new-caller-booking-v1";
    private const string ProviderPrefix = "booking-provider:v1:";
    private const string QaPrefix = "receptionist-qa-booking:";
    private readonly IDataProtector handles = protection.CreateProtector("LEGEND.Receptionist.BookingPurpose.v1");
    private readonly ReceptionistAdmissionService actionAuthority = admission;

    public async Task<IReadOnlyList<ReceptionistBookingOption>> PrepareAsync(ReceptionistCallContext supplied,
        Guid intakeActionId, string serviceKey, DateOnly date, Guid lease, CancellationToken ct)
    {
        var call = await CurrentAsync(supplied, lease, ct);
        var policy = call.Configuration.BookingPolicy!;
        var service = policy.Services.SingleOrDefault(x => x.ServiceKey == serviceKey)
            ?? throw new InvalidOperationException("This service has no approved appointment mapping.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(service.TimeZoneId);
        var now = clock.GetUtcNow().UtcDateTime;
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone));
        if (date < localToday || date > localToday.AddDays(policy.MaximumAdvanceDays))
            throw new InvalidOperationException("The requested date is outside the approved booking range.");
        await using var read = new MasterAppDbContext(options);
        var attempts = await read.Set<ReceptionistActionIntent>().AsNoTracking().Where(x => x.CallId == call.CallId &&
            x.CapabilityKey == ReceptionistCapabilities.CanonicalBooking).ToArrayAsync(ct);
        if (attempts.Any(x => !ReceptionistAdmissionService.IsZeroEffectBookingRejection(x)))
            throw new InvalidOperationException("An existing booking attempt must be reconciled before another appointment can be offered.");
        var hash = await ConfigurationHashAsync(read, call, ct);
        IReadOnlyList<ReceptionistCalendarSlot> slots;
        string factsHash;
        if (call.ControlledTest)
        {
            var intake = await inquiries.ReadSafeOutcomeAsync(call, intakeActionId, ct);
            if (intake?.State != "qa_outcome_verified") throw new InvalidOperationException("A verified synthetic intake outcome is required.");
            factsHash = await read.Set<ReceptionistActionIntent>().AsNoTracking().Where(x => x.Id == intakeActionId && x.CallId == call.CallId)
                .Select(x => x.ArgumentsSha256).SingleAsync(ct);
            // Explicit synthetic fixture, not a live availability claim. No gateway,
            // connection import, token acquisition or provider call in controlled QA.
            var wall = date.ToDateTime(new TimeOnly(12, 0));
            if (zone.IsInvalidTime(wall) || zone.IsAmbiguousTime(wall)) return [];
            var start = TimeZoneInfo.ConvertTimeToUtc(wall, zone);
            if (start <= now.AddMinutes(policy.MinimumNoticeMinutes)) return [];
            slots = [new(start, start.AddMinutes(service.DurationMinutes), "synthetic-staff", "synthetic-calendar")];
        }
        else
        {
            var target = await TargetAsync(call, intakeActionId, service, ct);
            factsHash = target.ConfirmedFactsSha256;
            slots = await services.GetRequiredService<IReceptionistBookingGateway>().SlotsAsync(target, date,
                policy.MinimumNoticeMinutes, policy.MaximumAdvanceDays, ct, start => NoticeAllows(start, now, call.Configuration));
        }
        var label = JsonSerializer.Deserialize<ReceptionistApplicationServiceDefinition[]>(call.Configuration.ServiceDefinitionsJson,
            ReceptionistConfigurationCodec.Json)!.Single(x => x.ServiceKey == serviceKey).DisplayName;
        // Canonical SQL UTC columns are materialized with Unspecified DateTime.Kind.
        // Preserve their UTC meaning when issuing the protected, round-tripped handle.
        var expires = new[] { now.AddMinutes(5), DateTime.SpecifyKind(call.MustStopUtc, DateTimeKind.Utc) }.Min();
        return slots.Where(slot => NoticeAllows(slot.StartUtc, now, call.Configuration)).Take(3).Select(slot =>
        {
            RequireSlot(slot, service, now, policy, call.Configuration);
            var purpose = new Purpose(1, call.CallId, call.Owner.Key, call.ConfigurationId, hash, intakeActionId,
                serviceKey, factsHash, slot, service.TimeZoneId, policy.ExpectedCalendarConfigurationSha256,
                now, expires, call.ControlledTest, "email", attempts.Length + 1);
            return new ReceptionistBookingOption(handles.Protect(JsonSerializer.Serialize(purpose, ReceptionistConfigurationCodec.Json)),
                slot.StartUtc, slot.EndUtc, service.TimeZoneId, label, call.ControlledTest);
        }).ToArray();
    }

    public async Task<ReceptionistBookingResult> CommitAsync(ReceptionistCallContext supplied, string handle,
        bool appointmentConsent, bool emailInvitationConsent, string confirmationReceipt, Guid lease, CancellationToken ct)
    {
        if (!appointmentConsent || !emailInvitationConsent)
            return new("explicit_appointment_and_email_consent_required", false, supplied.ControlledTest);
        var call = await CurrentAsync(supplied, lease, ct);
        var purpose = ReadPurpose(handle);
        await ValidatePurposeAsync(call, purpose, ct);
        var hash = IntentHash(call, purpose.ConfigurationSha256, purpose.IntakeActionId, purpose.FactsSha256,
            call.Configuration.BookingPolicy!.Services.Single(x => x.ServiceKey == purpose.ServiceKey), purpose.Slot);
        var service = call.Configuration.BookingPolicy!.Services.Single(x => x.ServiceKey == purpose.ServiceKey);
        ReceptionistTrustedBookingTarget? target = null;
        IReceptionistBookingGateway? gateway = null;
        if (!call.ControlledTest)
        {
            // Resolve read-only prerequisites before a durable claim, so a missing
            // adapter or changed intake cannot strand a pending effect.
            try
            {
                target = await TargetAsync(call, purpose.IntakeActionId, service, ct);
                if (target.ConfirmedFactsSha256 != purpose.FactsSha256) return new("intake_changed", false, false);
                gateway = services.GetRequiredService<IReceptionistBookingGateway>();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { return new("booking_rejected_before_attempt", false, false); }
        }
        var claim = await actionAuthority.ClaimActionAsync(call.CallId, "caller-booking-" + purpose.Sequence, ReceptionistCapabilities.CanonicalBooking,
            hash, confirmationReceipt, lease, ct);
        if (claim.ActionId is not { } actionId || claim.RouteKey != RouteKey) return new("booking_not_authorized", false, call.ControlledTest);
        var qaReference = QaPrefix + actionId.ToString("N");
        if (call.ControlledTest)
        {
            var scope = new ReceptionistQaScope(call.Owner.Key, call.CallId, call.ConfigurationId,
                call.Provider, call.ProviderProjectKey, call.ProviderSessionId);
            var qaReceipt = QaReceipt(scope, actionId, hash, confirmationReceipt);
            try
            {
                await actionAuthority.RecordSyntheticBookingOutcomeAsync(call.CallId, actionId, hash,
                    confirmationReceipt, qaReference, qaReceipt, ct);
            }
            catch (Exception error) when (error is DbUpdateException or System.Data.Common.DbException or TimeoutException or IOException) { }
            await using var fresh = new MasterAppDbContext(options);
            var row = await fresh.Set<ReceptionistActionIntent>().AsNoTracking().SingleAsync(x => x.Id == actionId && x.CallId == call.CallId, ct);
            var verified = VerifiedQaAction(scope, row) &&
                row.ArgumentsSha256 == hash && row.ConfirmationReceipt == confirmationReceipt;
            return new(verified ? "qa_booking_outcome_verified" : "booking_uncertain", verified, true);
        }
        var command = new ReceptionistTrustedBookingCommit(target!, purpose.Slot, true);
        try
        {
            if (claim.MayExecute)
                await gateway!.CreateAsync(command, new Fence(this, call, purpose, actionId, hash, confirmationReceipt, lease), ct);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // No exception/provider body reaches the model. Only the recorded original
            // provider identity may be reconciled; absence never authorizes another POST.
        }
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if ((claim.MayExecute && await actionAuthority.RejectUnstartedBookingAsync(call.CallId, actionId, hash, completion.Token)) || claim.State == "rejected")
        {
            await using var rejectionRead = new MasterAppDbContext(options);
            var rejected = await rejectionRead.Set<ReceptionistActionIntent>().AsNoTracking().SingleAsync(x => x.Id == actionId && x.CallId == call.CallId, completion.Token);
            if (ReceptionistAdmissionService.IsZeroEffectBookingRejection(rejected)) return new("booking_rejected_before_attempt", false, false);
        }
        return ct.IsCancellationRequested ? new("booking_uncertain", false, false) : await ReconcileAsync(call, command, actionId, hash, gateway!, ct);
    }

    private async Task<ReceptionistBookingResult> ReconcileAsync(ReceptionistCallContext call,
        ReceptionistTrustedBookingCommit command, Guid actionId, string hash, IReceptionistBookingGateway gateway, CancellationToken ct)
    {
        await using var read = new MasterAppDbContext(options);
        var action = await read.Set<ReceptionistActionIntent>().AsNoTracking().SingleAsync(x => x.Id == actionId && x.CallId == call.CallId, ct);
        if (action.ArgumentsSha256 != hash || action.RouteKey != RouteKey || action.CapabilityKey != ReceptionistCapabilities.CanonicalBooking ||
            action.ActualRecordReference?.StartsWith(ProviderPrefix, StringComparison.Ordinal) != true)
            return new("booking_uncertain", false, false);
        var identity = new ReceptionistProviderBookingIdentity(action.ActualRecordReference[ProviderPrefix.Length..], command.Slot.TargetSha256);
        var bindingReceipt = BindingReceipt(actionId, hash, identity);
        if (action.VerificationReceipt != bindingReceipt && action.VerificationReceipt != "booking-readback:v1:" + bindingReceipt["booking-binding:v1:".Length..])
            return new("booking_uncertain", false, false);
        try
        {
            if (!await gateway.VerifyAsync(command, identity, ct)) return new("booking_uncertain", false, false);
            var receipt = "booking-readback:v1:" + bindingReceipt["booking-binding:v1:".Length..];
            await actionAuthority.RecordActionOutcomeAsync(actionId, "verified", action.ActualRecordReference, receipt, ct);
            // Reopen durable outcome after verification; no tracked object is success evidence.
            await using var fresh = new MasterAppDbContext(options);
            var committed = await fresh.Set<ReceptionistActionIntent>().AsNoTracking().AnyAsync(x => x.Id == actionId && x.State == "verified" &&
                x.ActualRecordReference == action.ActualRecordReference && x.VerificationReceipt == receipt, ct);
            return new(committed ? "booking_verified" : "booking_uncertain", committed, false);
        }
        catch (Exception error) when (error is not OperationCanceledException) { return new("booking_uncertain", false, false); }
    }

    private async Task<ReceptionistCallContext> CurrentAsync(ReceptionistCallContext supplied, Guid lease, CancellationToken ct)
    {
        var current = await actionAuthority.GetCallContextAsync(supplied.CallId, ct) ?? throw new InvalidOperationException("The call is unavailable.");
        if (current.Owner != supplied.Owner || current.ConfigurationId != supplied.ConfigurationId || current.ControlledTest != supplied.ControlledTest ||
            current.Provider != supplied.Provider || current.ProviderSessionId != supplied.ProviderSessionId || current.ProviderProjectKey != supplied.ProviderProjectKey ||
            !await actionAuthority.MayPrepareBookingAsync(current.CallId, lease, ct))
            throw new InvalidOperationException("The current call does not authorize booking.");
        var policy = current.Configuration.BookingPolicy!;
        if (policy.QaOnly && !current.ControlledTest || !policy.AllowConsentedCustomerEmail)
            throw new InvalidOperationException("This booking mode is not approved.");
        return current;
    }

    private async Task ValidatePurposeAsync(ReceptionistCallContext call, Purpose purpose, CancellationToken ct)
    {
        await using var read = new MasterAppDbContext(options);
        var now = clock.GetUtcNow().UtcDateTime;
        var policy = call.Configuration.BookingPolicy!;
        var service = policy.Services.SingleOrDefault(x => x.ServiceKey == purpose.ServiceKey);
        if (purpose.Version != 1 || purpose.CallId != call.CallId || purpose.OwnerKey != call.Owner.Key ||
            purpose.ConfigurationId != call.ConfigurationId || purpose.ConfigurationSha256 != await ConfigurationHashAsync(read, call, ct) ||
            purpose.Qa != call.ControlledTest || purpose.Channel != "email" || purpose.Sequence is < 1 or > 128 ||
            purpose.IssuedUtc.Kind != DateTimeKind.Utc || purpose.ExpiresUtc.Kind != DateTimeKind.Utc ||
            purpose.IssuedUtc > now || purpose.ExpiresUtc <= now ||
            purpose.ExpiresUtc > call.MustStopUtc || purpose.ExpiresUtc - purpose.IssuedUtc > TimeSpan.FromMinutes(5) ||
            purpose.CalendarConfigurationSha256 != policy.ExpectedCalendarConfigurationSha256 ||
            service is null || purpose.TimeZoneId != service.TimeZoneId)
            throw new InvalidOperationException("The booking purpose is stale or outside this call's scope.");
        RequireSlot(purpose.Slot, service, now, policy, call.Configuration);
    }

    private async Task<ReceptionistTrustedBookingTarget> TargetAsync(ReceptionistCallContext call, Guid intakeAction,
        ReceptionistBookingService service, CancellationToken ct)
    {
        var verified = await inquiries.ReadBackAsync(call.Owner, call.CallId, intakeAction, ct);
        if (verified is null || verified.ConfirmedFacts.ServiceKey != service.ServiceKey ||
            string.IsNullOrWhiteSpace(verified.ConfirmedFacts.Email) ||
            string.IsNullOrWhiteSpace(verified.ConfirmedFacts.CallerName ?? verified.ConfirmedFacts.FirstName))
            throw new InvalidOperationException("Booking needs this call's confirmed service, name and invitation email.");
        return new(call.Owner, verified.IntakeLinkId, verified.CrmRecordReference,
            call.Configuration.BookingPolicy!.BookingAgentProfileId, service,
            call.Configuration.BookingPolicy.ExpectedCalendarConfigurationSha256, call.Configuration.BookingPolicy.ExpectedCalendarTargetSha256, verified.FactsSha256,
            verified.ConfirmedFacts.Email!, verified.ConfirmedFacts.CallerName ??
                string.Join(' ', new[] { verified.ConfirmedFacts.FirstName, verified.ConfirmedFacts.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))));
    }

    private static Task<string> ConfigurationHashAsync(MasterAppDbContext db, ReceptionistCallContext call, CancellationToken ct) =>
        db.Set<ReceptionistConfigurationVersion>().AsNoTracking().Where(x => x.Id == call.ConfigurationId)
            .Select(x => x.ConfigurationSha256).SingleAsync(ct);

    private Purpose ReadPurpose(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle) || handle.Length > 8192) throw new InvalidOperationException("A bounded purpose handle is required.");
        try { return JsonSerializer.Deserialize<Purpose>(handles.Unprotect(handle), ReceptionistConfigurationCodec.Json)
            ?? throw new InvalidOperationException("The purpose handle is absent."); }
        catch (Exception error) when (error is CryptographicException or JsonException) { throw new InvalidOperationException("The purpose handle is invalid."); }
    }
    private static void RequireSlot(ReceptionistCalendarSlot slot, ReceptionistBookingService service, DateTime now, ReceptionistBookingPolicy policy, ReceptionistBusinessConfiguration configuration)
    {
        if (slot.StartUtc.Kind != DateTimeKind.Utc || slot.EndUtc.Kind != DateTimeKind.Utc ||
            !NoticeAllows(slot.StartUtc, now, configuration) || slot.EndUtc - slot.StartUtc != TimeSpan.FromMinutes(service.DurationMinutes) ||
            string.IsNullOrWhiteSpace(slot.StaffId) || string.IsNullOrWhiteSpace(slot.TargetSha256))
            throw new InvalidOperationException("The offered appointment is outside its approved bounds.");
    }
    // Rechecked at commitment as well as when offering slots. Closed weekdays do not
    // count as notice; use wall time so DST cannot turn a business day into 24 hours.
    internal static bool NoticeAllows(DateTime startUtc, DateTime nowUtc, ReceptionistBusinessConfiguration configuration)
    {
        var policy = configuration.BookingPolicy!;
        if (startUtc.Kind != DateTimeKind.Utc || nowUtc.Kind != DateTimeKind.Utc ||
            startUtc <= nowUtc.AddMinutes(policy.MinimumNoticeMinutes)) return false;
        if (policy.MinimumNoticeBusinessDays == 0) return true;
        if (policy.MinimumNoticeBusinessDays is < 0 or > 30) return false;
        var open = configuration.Hours.Select(x => x.Day).Where(day => Enum.IsDefined(day)).ToHashSet();
        if (open.Count == 0) return false;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(configuration.TimeZoneId);
        var cutoff = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        var start = TimeZoneInfo.ConvertTimeFromUtc(startUtc, zone);
        for (var remaining = policy.MinimumNoticeBusinessDays; remaining > 0;)
        {
            cutoff = cutoff.AddDays(1);
            if (open.Contains(cutoff.DayOfWeek)) remaining--;
        }
        return open.Contains(start.DayOfWeek) && start >= cutoff;
    }
    private static string BindingReceipt(Guid actionId, string hash, ReceptionistProviderBookingIdentity identity) =>
        "booking-binding:v1:" + ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new { actionId, hash, identity }));
    internal static bool VerifiedAction(ReceptionistActionIntent action, ReceptionistBusinessConfiguration configuration)
    {
        if (action.State != "verified" || action.RouteKey != RouteKey || action.CapabilityKey != ReceptionistCapabilities.CanonicalBooking ||
            action.ActualRecordReference?.StartsWith(ProviderPrefix, StringComparison.Ordinal) != true || configuration.BookingPolicy is null) return false;
        var bound = BindingReceipt(action.Id, action.ArgumentsSha256, new(action.ActualRecordReference[ProviderPrefix.Length..],
            configuration.BookingPolicy.ExpectedCalendarTargetSha256));
        return action.VerificationReceipt == "booking-readback:v1:" + bound["booking-binding:v1:".Length..];
    }
    internal static bool VerifiedQaAction(ReceptionistQaScope scope, ReceptionistActionIntent action) =>
        action.CallId == scope.CallId && action.State == "verified" && action.CapabilityKey == ReceptionistCapabilities.CanonicalBooking &&
        action.RouteKey == RouteKey && action.ExecutionLeaseToken is not null && action.ExecutionFence > 0 &&
        action.ActualRecordReference == QaPrefix + action.Id.ToString("N") &&
        action.VerificationReceipt == QaReceipt(scope, action.Id, action.ArgumentsSha256, action.ConfirmationReceipt);
    private static string QaReceipt(ReceptionistQaScope scope, Guid actionId, string hash, string confirmation) =>
        "qa-booking-outcome:v1:" + ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new
        {
            kind = "synthetic_booking_outcome", scope.Key, scope.CallId, scope.ConfigurationId,
            scope.Provider, scope.ProviderProjectKey, scope.ProviderSessionId, actionId, hash, confirmation,
            genuineRecords = 0, invitations = 0
        }, ReceptionistConfigurationCodec.Json));
    private static string IntentHash(ReceptionistCallContext call, string configurationSha256, Guid intakeActionId,
        string factsSha256, ReceptionistBookingService service, ReceptionistCalendarSlot slot) =>
        ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new
        {
            purpose = "legend-receptionist-booking-intent-v1", call.CallId, owner = call.Owner.Key, call.ConfigurationId,
            configurationSha256, intakeActionId, factsSha256, service, slot, call.ControlledTest,
            calendarConfigurationSha256 = call.Configuration.BookingPolicy!.ExpectedCalendarConfigurationSha256,
            calendarTargetSha256 = call.Configuration.BookingPolicy.ExpectedCalendarTargetSha256,
            appointmentConsent = true, invitationChannel = "email", invitationConsent = true
        }, ReceptionistConfigurationCodec.Json));

    // The operator supplies only a reviewed action identity and expected revision.
    // Recovery cannot claim or start an effect. Handle expiry/admission applies to
    // creation, not to verifying the exact original provider identity after closure.
    internal async Task<ReceptionistBookingResult> ReconcileRecordedAsync(MarketingOwnerScope owner, Guid actionId,
        Guid expectedRevision, Func<MasterAppDbContext, Task>? guard = null, CancellationToken ct = default)
    {
        await using var db = new MasterAppDbContext(options);
        var action = await db.Set<ReceptionistActionIntent>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == actionId, ct)
            ?? throw new InvalidOperationException("The reviewed booking action is unavailable.");
        var call = await actionAuthority.GetCallContextAsync(action.CallId, ct)
            ?? throw new InvalidOperationException("The original booking call is unavailable.");
        if (call.Owner != owner || call.ControlledTest || action.Revision != expectedRevision ||
            action.CapabilityKey != ReceptionistCapabilities.CanonicalBooking || action.RouteKey != RouteKey ||
            action.State is not ("pending" or "uncertain" or "verified" or "rejected") || action.ExecutionLeaseToken is null || action.ExecutionFence < 1 ||
            string.IsNullOrWhiteSpace(action.ConfirmationReceipt))
            throw new InvalidOperationException("The reviewed booking scope or revision changed.");
        if (action.State == "pending")
        {
            var rejected = await actionAuthority.RejectUnstartedBookingAsync(call.CallId, action.Id, action.ArgumentsSha256, ct,
                expectedRevision, guard);
            return new(rejected ? "booking_rejected_before_attempt" : "booking_uncertain", false, false);
        }
        if (ReceptionistAdmissionService.IsZeroEffectBookingRejection(action)) return new("booking_rejected_before_attempt", false, false);
        if (action.ActualRecordReference?.StartsWith(ProviderPrefix, StringComparison.Ordinal) != true)
            return new("provider_identity_missing", false, false);
        var providerId = action.ActualRecordReference[ProviderPrefix.Length..];
        var pinnedTarget = call.Configuration.BookingPolicy?.ExpectedCalendarTargetSha256;
        if (pinnedTarget is null) return new("reconciliation_evidence_mismatch", false, false);
        var originalBound = BindingReceipt(action.Id, action.ArgumentsSha256, new(providerId, pinnedTarget));
        if (action.VerificationReceipt != originalBound && action.VerificationReceipt != "booking-readback:v1:" + originalBound["booking-binding:v1:".Length..])
            return new("reconciliation_evidence_mismatch", false, false);
        var intakeIds = await db.Set<ReceptionistActionIntent>().AsNoTracking().Where(x => x.CallId == call.CallId &&
            x.CapabilityKey == ReceptionistCapabilities.CanonicalIntake && x.State == "verified").Select(x => x.Id).Take(2).ToArrayAsync(ct);
        if (intakeIds.Length != 1) return new("canonical_intake_ambiguous", false, false);
        var verified = await inquiries.ReadBackAsync(owner, call.CallId, intakeIds[0], ct);
        var service = call.Configuration.BookingPolicy?.Services.SingleOrDefault(x => x.ServiceKey == verified?.ConfirmedFacts.ServiceKey);
        if (verified is null || service is null) return new("canonical_intake_unavailable", false, false);
        var target = await TargetAsync(call, intakeIds[0], service, ct);
        var gateway = services.GetRequiredService<IReceptionistBookingGateway>();
        try
        {
            var slot = await gateway.ReadOriginalSlotAsync(target, providerId, ct);
            if (slot is null) return new("provider_readback_unavailable", false, false);
            var hash = IntentHash(call, await ConfigurationHashAsync(db, call, ct), intakeIds[0], verified.FactsSha256, service, slot);
            var identity = new ReceptionistProviderBookingIdentity(providerId, slot.TargetSha256);
            var bound = BindingReceipt(action.Id, hash, identity);
            var receipt = "booking-readback:v1:" + bound["booking-binding:v1:".Length..];
            if (hash != action.ArgumentsSha256 || (action.VerificationReceipt != bound && action.VerificationReceipt != receipt))
                return new("reconciliation_evidence_mismatch", false, false);
            if (!await gateway.VerifyAsync(new(target, slot, true), identity, ct))
                return new("booking_readback_incomplete", false, false);
            await actionAuthority.CompleteBookingReconciliationAsync(call.CallId, action.Id, expectedRevision,
                action.ActualRecordReference, hash, receipt, guard, ct);
            await using var fresh = new MasterAppDbContext(options);
            var complete = await fresh.Set<ReceptionistActionIntent>().AsNoTracking().AnyAsync(x => x.Id == action.Id &&
                x.CallId == call.CallId && x.ArgumentsSha256 == hash && x.State == "verified" &&
                x.ActualRecordReference == action.ActualRecordReference && x.VerificationReceipt == receipt, ct);
            return new(complete ? "booking_verified" : "booking_uncertain", complete, false);
        }
        catch (DbUpdateConcurrencyException) { throw; }
        catch (ReceptionistOperatorReplayException) { throw; }
        catch (Exception error) when (error is not OperationCanceledException)
        { return new("provider_readback_unavailable", false, false); }
    }
    private sealed record Purpose(int Version, Guid CallId, string OwnerKey, Guid ConfigurationId, string ConfigurationSha256,
        Guid IntakeActionId, string ServiceKey, string FactsSha256, ReceptionistCalendarSlot Slot, string TimeZoneId,
        string CalendarConfigurationSha256, DateTime IssuedUtc, DateTime ExpiresUtc, bool Qa, string Channel, int Sequence);
    private sealed class Fence(ReceptionistBookingAuthority authority, ReceptionistCallContext call, Purpose purpose,
        Guid actionId, string hash, string receipt, Guid lease) : IReceptionistBookingExecutionFence
    {
        public async Task<bool> TryStartProviderAsync(CancellationToken ct)
        {
            _ = await authority.CurrentAsync(call, lease, ct);
            await authority.ValidatePurposeAsync(call, purpose, ct);
            var service = call.Configuration.BookingPolicy!.Services.Single(x => x.ServiceKey == purpose.ServiceKey);
            var target = await authority.TargetAsync(call, purpose.IntakeActionId, service, ct);
            return target.ConfirmedFactsSha256 == purpose.FactsSha256 &&
                await authority.actionAuthority.TryBeginBookingAsync(call.CallId, actionId, hash, receipt, lease, false, ct);
        }
        public async Task ProviderCreatedAsync(ReceptionistProviderBookingIdentity identity, CancellationToken ct)
        {
            // Once the provider returned an identity, preserve it even if the caller
            // interrupted. This is completion accounting, never another provider effect.
            using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await authority.actionAuthority.RecordBookingProviderAsync(call.CallId, actionId, ProviderPrefix + identity.AppointmentId,
                BindingReceipt(actionId, hash, identity), finish.Token);
        }
    }
}
