using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Analytics;

namespace Infrastructure.Reception;

/// <summary>
/// Durable admission and action coordination under MarketingConnection ownership.
/// Provider I/O must occur after these transactions commit; an uncertain effect is never retried blindly.
/// </summary>
public sealed partial class ReceptionistAdmissionService(DbContextOptions<MasterAppDbContext> options, TimeProvider clock,
    IServiceProvider? services = null)
{
    /// <summary>
    /// Caller must first resolve the existing owner/settings authority. This observation has no
    /// writes, locks, provider I/O or admission effect. Actual calls recheck inside ReserveAsync.
    /// Only the control-plane controller may request its local runtime/window observation.
    /// </summary>
    public async Task<ReceptionistReadinessProjection> GetSetupReadinessAsync(ReceptionistConfigurationSnapshot snapshot,
        bool observeControlPlaneRuntime = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var observed = clock.GetUtcNow();
        ReceptionistReadinessProjection Unavailable(string reason) => new() { ObservedUtc = observed, AdmissionReason = reason };
        await using var read = new MasterAppDbContext(options);
        read.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var provider = MarketingDestinationKeys.Normalize(snapshot.Provider);
        var connection = await read.MarketingConnections.SingleOrDefaultAsync(
            x => x.OwnerKey == snapshot.Owner.Key && x.Provider == provider, ct);
        if (connection is null)
            return snapshot.ConnectionRevision == Guid.Empty && snapshot.ConfigurationId is null
                ? new() { ObservedUtc = observed, OwnerPermissionEnabled = false, BindingScope = "unaccepted",
                    AdmissionReason = "configuration_or_binding_unavailable", CallReadiness = "Not ready" }
                : Unavailable("snapshot_changed");
        // Never follow a caller-supplied configuration id across a tenant, or combine stale settings with live facts.
        if (ReceptionistSql.Owner(connection) != snapshot.Owner || connection.Revision != snapshot.ConnectionRevision ||
            connection.ReceptionistActiveConfigurationId != snapshot.ConfigurationId)
            return Unavailable("snapshot_changed");
        if (connection.ReceptionistActiveConfigurationId is null)
            return new() { ObservedUtc = observed, OwnerPermissionEnabled = connection.ReceptionistAdmissionEnabled,
                BindingScope = "unaccepted", AdmissionReason = "configuration_or_binding_unavailable", CallReadiness = "Not ready" };
        var version = await read.Set<ReceptionistConfigurationVersion>().SingleOrDefaultAsync(
            x => x.MarketingConnectionId == connection.Id && x.Id == connection.ReceptionistActiveConfigurationId, ct);
        if (version is null || version.ConfigurationSha256 != snapshot.ConfigurationSha256 || version.Version != snapshot.ConfigurationVersion)
            return Unavailable("configuration_or_binding_unavailable");
        ReceptionistBusinessConfiguration configuration;
        ReceptionistProviderBinding? binding;
        try
        {
            configuration = ReceptionistConfigurationCodec.Read(version.ConfigurationJson, version.ConfigurationSha256);
            binding = ReceptionistConfigurationCodec.ReadBinding(version.AcceptedBindingJson, configuration);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { return Unavailable("configuration_or_binding_unavailable"); }
        var bound = binding is not null && connection.ReceptionistIngressKey == binding.IngressKey &&
            connection.ReceptionistProviderProjectKey == binding.ProviderProjectKey;
        var ownerAccount = await read.Set<ReceptionistBudgetAccount>().SingleOrDefaultAsync(
            x => x.Kind == "owner" && x.ScopeKey == snapshot.Owner.Key, ct);
        var accountIds = ownerAccount is null || !bound ? [] : binding!.Costs.Select(x => x.FundingBudgetAccountId)
            .Append(ownerAccount.Id).Distinct().ToArray();
        if (accountIds.Length > 0 && await read.Set<ReceptionistBudgetAccount>().CountAsync(x => accountIds.Contains(x.Id), ct) != accountIds.Length)
            accountIds = [];
        var reason = "configuration_or_binding_unavailable";
        if (bound)
            reason = (await EvaluateAdmissionAsync(read, connection, configuration, binding!, ownerAccount, accountIds,
                binding!.ControlledTestOnly, observed.UtcDateTime, ct)).Reason ?? "eligible_at_observation";
        var openCallIds = await read.Set<ReceptionistCall>().Where(
            x => x.MarketingConnectionId == connection.Id && x.ProviderClosedUtc == null).Select(x => x.Id).ToArrayAsync(ct);
        var openCalls = openCallIds.Length;
        var stopCalls = await CallsRequiringStop(read, observed.UtcDateTime)
            .CountAsync(x => x.MarketingConnectionId == connection.Id && openCallIds.Contains(x.Id), ct);
        // Runtime is registered only in the existing control-plane host. A business-host flag is never evidence for it.
        var runtime = observeControlPlaneRuntime ? services?.GetService<ReceptionistRuntimeAuthority>() : null;
        var enabled = runtime?.Enabled;
        var window = !bound ? "unknown" : !binding!.ControlledTestOnly ? "not_applicable" : runtime is null ? "unknown"
            : runtime.IsControlledTest(snapshot.Owner, binding) ? "valid_application_window" : "not_verified";
        var closing = enabled is null ? (int?)null : enabled == false ? openCalls : stopCalls;
        // Revision drift invalidates the combined projection, rather than reusing stale affirmative facts.
        if (!await read.MarketingConnections.AnyAsync(x => x.Id == connection.Id && x.OwnerKey == snapshot.Owner.Key &&
            x.Provider == provider && x.Revision == connection.Revision &&
            x.ReceptionistControlRevision == connection.ReceptionistControlRevision &&
            x.ReceptionistActiveConfigurationId == version.Id, ct))
            return Unavailable("snapshot_changed");
        return new()
        {
            ObservedUtc = observed, OperatingConfigurationVersion = bound ? version.Version : null, OwnerPermissionEnabled = connection.ReceptionistAdmissionEnabled,
            CanAllowOwnerPermission = bound && ReceptionistConfigurationCodec.HasRequiredIntake(configuration, binding!) &&
                ReceptionistSetupForms.CanAllowOwnerPermission(snapshot),
            RuntimeState = enabled is null ? "unknown" : enabled.Value ? "enabled" : "disabled",
            BindingScope = !bound ? "unaccepted" : binding!.ControlledTestOnly ? "controlled_test_only" : "public",
            ApplicationWindowState = window, AdmissionReason = reason, OpenCalls = openCalls,
            OpenCallsWithoutStopCondition = closing is { } count ? openCalls - count : null, AwaitingStopOrClosure = closing,
            CallReadiness = enabled == false || reason != "eligible_at_observation" || window == "not_verified" ? "Not ready" : "Unverified"
            // Current carrier routing and correlated human audio acceptance remain unverified. No schema/receipt is fabricated.
        };
    }

    /// <summary>Resolves the private server route before signature validation. Caller fields do not select an owner or secret.</summary>
    internal async Task<ReceptionistIngressBinding?> GetIngressBindingAsync(string provider, string ingressKey, CancellationToken ct = default)
    {
        provider = MarketingDestinationKeys.Normalize(provider);
        ReceptionistConfigurationCodec.Required(ingressKey, 200, nameof(ingressKey));
        await using var read = new MasterAppDbContext(options);
        var connection = await read.MarketingConnections.AsNoTracking().SingleOrDefaultAsync(
            x => x.Provider == provider && x.ReceptionistIngressKey == ingressKey, ct);
        if (connection?.ReceptionistActiveConfigurationId is not { } configurationId) return null;
        var owner = ReceptionistSql.Owner(connection);
        var version = await read.Set<ReceptionistConfigurationVersion>().AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == configurationId && x.MarketingConnectionId == connection.Id, ct);
        if (version is null) return null;
        var configuration = ReceptionistConfigurationCodec.Read(version.ConfigurationJson, version.ConfigurationSha256);
        var binding = ReceptionistConfigurationCodec.ReadBinding(version.AcceptedBindingJson, configuration);
        if (binding is null || binding.IngressKey != ingressKey || connection.ReceptionistProviderProjectKey != binding.ProviderProjectKey) return null;
        return new(provider, connection.Id, owner, version.Id, configuration, binding);
    }

    internal async Task<ReceptionistAdmission> ReserveAsync(TrustedReceptionistIngress ingress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ingress);
        ingress = ingress with { Provider = MarketingDestinationKeys.Normalize(ingress.Provider) };
        ReceptionistConfigurationCodec.Required(ingress.IngressKey, 200, nameof(ingress.IngressKey));
        ReceptionistConfigurationCodec.Required(ingress.ProviderProjectKey, 200, nameof(ingress.ProviderProjectKey));
        ReceptionistConfigurationCodec.Required(ingress.ProviderSessionId, 200, nameof(ingress.ProviderSessionId));
        ReceptionistConfigurationCodec.Required(ingress.EventId, 200, nameof(ingress.EventId));
        ReceptionistConfigurationCodec.Required(ingress.AuthenticatedOwnerKey, 160, nameof(ingress.AuthenticatedOwnerKey));
        if (ingress.AuthenticatedConnectionId == Guid.Empty || ingress.AuthenticatedConfigurationId == Guid.Empty)
            throw new ArgumentException("The exact server binding used to authenticate the ingress is required.");
        var now = clock.GetUtcNow().UtcDateTime;
        await using var read = new MasterAppDbContext(options);
        var connection = await read.MarketingConnections.AsNoTracking().SingleOrDefaultAsync(
            x => x.Provider == ingress.Provider && x.ReceptionistIngressKey == ingress.IngressKey, ct);
        ReceptionistConfigurationVersion? initialVersion = connection?.ReceptionistActiveConfigurationId is { } versionId
            ? await read.Set<ReceptionistConfigurationVersion>().AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == versionId && x.MarketingConnectionId == connection.Id, ct) : null;
        ReceptionistBusinessConfiguration? configuration = initialVersion is null ? null :
            ReceptionistConfigurationCodec.Read(initialVersion.ConfigurationJson, initialVersion.ConfigurationSha256);
        var binding = configuration is null ? null : ReceptionistConfigurationCodec.ReadBinding(initialVersion!.AcceptedBindingJson, configuration);
        var ownerAccount = connection is null ? null : await read.Set<ReceptionistBudgetAccount>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Kind == "owner" && x.ScopeKey == connection.OwnerKey, ct);
        var accountIds = ownerAccount is null || binding is null ? [] : binding.Costs.Select(x => x.FundingBudgetAccountId)
            .Append(ownerAccount.Id).Distinct().ToArray();
        if (accountIds.Length > 0 && await read.Set<ReceptionistBudgetAccount>().CountAsync(x => accountIds.Contains(x.Id), ct) != accountIds.Length)
            accountIds = [];

        try
        {
            return await ReceptionistSql.TransactionAsync(options, accountIds, async write =>
            {
                now = clock.GetUtcNow().UtcDateTime;
                var current = await AuthenticatedConnectionAsync(write, ingress, ct);
                // Signature verification does not survive a route transfer, owner change or immutable binding rotation.
                // Check this before replay so an authenticated event cannot reveal or reserve another owner's call.
                if (current is null || connection?.Id != current.Id || initialVersion?.Id != ingress.AuthenticatedConfigurationId)
                    return new(false, false, "authenticated_binding_changed", null);
                if (configuration is null || binding is null || binding.Transport != "sip") return new(false, false, "configuration_or_binding_unavailable", null);
                if (binding.ProviderProjectKey != ingress.ProviderProjectKey || binding.IngressKey != ingress.IngressKey)
                    return new(false, false, "provider_binding_mismatch", null);
                var replay = await SessionDecisionAsync(write, ingress, ct);
                if (replay is not null) return await ReplayAsync(write, replay, ingress, now, ct);
                await AssertEventAvailableAsync(write, ingress, null, ct);
                ReceptionistAdmission Deny(string reason) => SaveDecision(write, ingress, now, reason, null);
                var evaluation = await EvaluateAdmissionAsync(write, current, configuration, binding, ownerAccount,
                    accountIds, ingress.ControlledTest, now, ct);
                // Only the existing reservation transaction may persist boundary holds. Readiness never does.
                foreach (var boundaryCall in evaluation.BoundaryCalls) await HoldVoiceAllocationAsync(write, boundaryCall, ct);
                if (evaluation.Reason is { } reason) return Deny(reason);
                var call = AddReservation(write, current, initialVersion.Id, configuration, binding,
                    ownerAccount!.Id, evaluation, ingress.ProviderSessionId, ingress.ControlledTest, now);
                return SaveDecision(write, ingress, now, "reserved", call);
            }, ct);
        }
        catch (DbUpdateException conflict)
        {
            // Concurrent deliveries may race before they resolve an owner. Only an already committed identical
            // session decision can explain a uniqueness failure; otherwise surface the database error.
            return await ReceptionistSql.TransactionAsync(options, accountIds, async write =>
            {
                if (await AuthenticatedConnectionAsync(write, ingress, ct) is null)
                    return new(false, false, "authenticated_binding_changed", null);
                var existing = await SessionDecisionAsync(write, ingress, ct);
                if (existing is null) throw new DbUpdateException("No identical committed provider decision explains this database conflict.", conflict);
                return await ReplayAsync(write, existing, ingress, clock.GetUtcNow().UtcDateTime, ct, persistEvent: false);
            }, ct);
        }
    }

    // The reservation transaction and the read-only setup observation share the same ordered checks.
    // The returned plan is private and ephemeral; setup cannot authorize or reserve a future call.
    private static async Task<AdmissionEvaluation> EvaluateAdmissionAsync(MasterAppDbContext read,
        MarketingConnection current, ReceptionistBusinessConfiguration configuration, ReceptionistProviderBinding binding,
        ReceptionistBudgetAccount? ownerAccount, Guid[] accountIds, bool controlledTest, DateTime now, CancellationToken ct)
    {
        if (ownerAccount is null || accountIds.Length == 0) return new("accounting_unavailable");
        if (!current.ReceptionistAdmissionEnabled) return new("admission_disabled");
        if (binding.ControlledTestOnly && !controlledTest) return new("controlled_acceptance_only");
        if (!ReceptionistConfigurationCodec.HasRequiredIntake(configuration, binding)) return new("canonical_intake_unaccepted");
        if (!ReceptionistConfigurationCodec.IsOpen(configuration, now)) return new("outside_reception_hours");
        var accounts = await read.Set<ReceptionistBudgetAccount>().Where(x => accountIds.Contains(x.Id)).ToListAsync(ct);
        if (accounts.Count != accountIds.Length || accounts.Any(x => !x.AdmissionEnabled || x.Currency != configuration.Currency) ||
            accounts.Any(x => x.Id == ownerAccount.Id ? x.Kind != "owner" || x.ScopeKey != current.OwnerKey : x.Kind != "funding"))
            return new("accounting_disabled_or_mismatched");
        var boundaryCalls = await read.Set<ReceptionistCall>().Where(x => x.OwnerBudgetAccountId == ownerAccount.Id &&
            x.ProviderClosedUtc == null && !x.VoiceUsageFinal && read.Set<ReceptionistBudgetPeriod>().Any(period =>
                period.Id == x.OwnerBudgetPeriodId && period.EndUtc <= now)).ToListAsync(ct);
        if (boundaryCalls.Count > 0)
        {
            return new("voice_allocation_unreconciled") { BoundaryCalls = boundaryCalls };
        }
        if (await read.Set<ReceptionistCall>().AnyAsync(x => x.OwnerBudgetAccountId == ownerAccount.Id && x.VoiceAllocationUncertain, ct))
            return new("voice_allocation_unreconciled");
        var periods = await read.Set<ReceptionistBudgetPeriod>().Where(x => accountIds.Contains(x.BudgetAccountId) &&
            x.StartUtc <= now && x.EndUtc > now).ToListAsync(ct);
        if (periods.Count != accountIds.Length || periods.Any(x => !x.OpeningReconciled) ||
            periods.Select(x => x.BudgetAccountId).Distinct().Count() != accountIds.Length)
            return new("unreconciled_accounting_period");
        var ownerPeriod = periods.Single(x => x.BudgetAccountId == ownerAccount.Id);
        var costReservations = binding.Costs.Select(component => new ReceptionistCostReservation
        {
            OwnerBudgetAccountId = ownerAccount.Id, OwnerBudgetPeriodId = ownerPeriod.Id,
            FundingBudgetAccountId = component.FundingBudgetAccountId,
            FundingBudgetPeriodId = periods.Single(x => x.BudgetAccountId == component.FundingBudgetAccountId).Id,
            FundingControlRevision = accounts.Single(x => x.Id == component.FundingBudgetAccountId).ControlRevision,
            ComponentKey = component.Key,
            CashReserved = ReceptionistConfigurationCodec.Maximum(component.CashFixedMaximum, component.CashPerSecondMaximum, configuration.MaximumCallSeconds),
            ConsumptionReserved = ReceptionistConfigurationCodec.Maximum(component.ConsumptionFixedMaximum, component.ConsumptionPerSecondMaximum, configuration.MaximumCallSeconds)
        }).ToArray();
        var outstanding = await read.Set<ReceptionistCostReservation>().AsNoTracking()
            .Where(x => (x.OwnerBudgetAccountId == ownerAccount.Id || accountIds.Contains(x.FundingBudgetAccountId)) && (!x.CashFinal || !x.ConsumptionFinal))
            .ToListAsync(ct);
        foreach (var period in periods)
        {
            var isOwner = period.BudgetAccountId == ownerAccount.Id;
            var outstandingForAccount = outstanding.Where(x => isOwner ? x.OwnerBudgetAccountId == ownerAccount.Id : x.FundingBudgetAccountId == period.BudgetAccountId).ToArray();
            var proposedForAccount = costReservations.Where(x => isOwner || x.FundingBudgetAccountId == period.BudgetAccountId).ToArray();
            var cashLimit = isOwner ? Math.Min(period.CashLimit, configuration.MonthlyCashLimit) : period.CashLimit;
            var consumptionLimit = isOwner ? Math.Min(period.ConsumptionLimit, configuration.MonthlyConsumptionLimit) : period.ConsumptionLimit;
            // Old unpriced reservations continue to encumber the new period. Rollover and rebinding never reset them.
            if (checked(period.CashCommitted + outstandingForAccount.Sum(CashRemaining) + proposedForAccount.Sum(x => x.CashReserved)) > cashLimit)
                return new(isOwner ? "owner_cash_limit" : "funding_cash_limit");
            if (checked(period.ConsumptionCommitted + outstandingForAccount.Sum(ConsumptionRemaining) + proposedForAccount.Sum(x => x.ConsumptionReserved)) > consumptionLimit)
                return new(isOwner ? "owner_consumption_limit" : "funding_consumption_limit");
        }
        var unfinishedVoice = await read.Set<ReceptionistCall>().AsNoTracking()
            .Where(x => x.OwnerBudgetAccountId == ownerAccount.Id && !x.VoiceUsageFinal)
            .Select(x => new { x.ReservedVoiceSeconds, x.ObservedVoiceSeconds }).ToListAsync(ct);
        if (checked(ownerPeriod.VoiceSecondsCommitted + unfinishedVoice.Sum(x => Math.Max(0, x.ReservedVoiceSeconds - x.ObservedVoiceSeconds)) +
            configuration.MaximumCallSeconds) > configuration.MonthlyVoiceSecondsLimit) return new("owner_voice_minutes_limit");
        if (await read.Set<ReceptionistCall>().CountAsync(x => x.OwnerBudgetAccountId == ownerAccount.Id && x.ProviderClosedUtc == null, ct) >=
            configuration.MaximumConcurrentCalls) return new("owner_concurrency_limit");
        var since = now.AddSeconds(-configuration.AdmissionWindowSeconds);
        if (await read.Set<ReceptionistCall>().CountAsync(x => x.OwnerBudgetAccountId == ownerAccount.Id && x.ReservedUtc >= since, ct) >=
            configuration.MaximumCallsPerWindow) return new("owner_admission_rate_limit");
        var deadline = new[] { now.AddSeconds(configuration.MaximumCallSeconds), periods.Min(x => x.EndUtc),
            ReceptionistConfigurationCodec.CurrentWindowEnd(configuration, now) }.Min();
        if (deadline <= now) return new("reception_window_ended");
        return new(null) { Accounts = accounts, OwnerPeriod = ownerPeriod, Costs = costReservations, Deadline = deadline };
    }

    private static ReceptionistCall AddReservation(MasterAppDbContext write, MarketingConnection connection,
        Guid configurationId, ReceptionistBusinessConfiguration configuration, ReceptionistProviderBinding binding,
        Guid ownerAccountId, AdmissionEvaluation evaluation, string? providerSessionId, bool controlledTest, DateTime now)
    {
        var call = new ReceptionistCall
        {
            MarketingConnectionId = connection.Id, ConfigurationVersionId = configurationId,
            OwnerBudgetAccountId = ownerAccountId, OwnerBudgetPeriodId = evaluation.OwnerPeriod!.Id,
            ConnectionControlRevision = connection.ReceptionistControlRevision,
            OwnerControlRevision = evaluation.Accounts.Single(x => x.Id == ownerAccountId).ControlRevision,
            Provider = connection.Provider, IngressKey = binding.IngressKey,
            ProviderProjectKey = binding.ProviderProjectKey, ProviderSessionId = providerSessionId,
            ControlledTest = controlledTest, ReservedUtc = now, MustStopUtc = evaluation.Deadline,
            ReservedVoiceSeconds = configuration.MaximumCallSeconds
        };
        write.Add(call);
        foreach (var reservation in evaluation.Costs) { reservation.CallId = call.Id; write.Add(reservation); }
        return call;
    }

    private sealed record AdmissionEvaluation(string? Reason)
    {
        internal IReadOnlyList<ReceptionistCall> BoundaryCalls { get; init; } = [];
        internal IReadOnlyList<ReceptionistBudgetAccount> Accounts { get; init; } = [];
        internal ReceptionistBudgetPeriod? OwnerPeriod { get; init; }
        internal IReadOnlyList<ReceptionistCostReservation> Costs { get; init; } = [];
        internal DateTime Deadline { get; init; }
    }

    internal async Task<ReceptionistCallContext?> GetCallContextAsync(Guid callId, CancellationToken ct = default)
    {
        await using var read = new MasterAppDbContext(options);
        var call = await read.Set<ReceptionistCall>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == callId, ct);
        if (call?.ProviderSessionId is null) return null;
        return await ContextAsync(read, call, ct);
    }

    internal async Task RecordProviderStateAsync(Guid callId, string state, string receipt,
        DateTime? providerClosedUtc = null, CancellationToken ct = default)
    {
        if (state is not ("accepted" or "uncertain" or "closed" or "rejected")) throw new ArgumentException("Unsupported provider state.");
        ReceptionistConfigurationCodec.Required(receipt, 512, nameof(receipt));
        var terminal = state is "closed" or "rejected";
        if (terminal != providerClosedUtc.HasValue || (providerClosedUtc.HasValue && providerClosedUtc.Value.Kind != DateTimeKind.Utc))
            throw new ArgumentException("A verified terminal state requires its UTC provider closure time.");
        await WithCallAsync(callId, async (write, call) =>
        {
            if (call.ProviderSessionId is null) throw new InvalidOperationException("Provider state requires a bound identity.");
            if (providerClosedUtc is { } closure && (closure < call.ReservedUtc || closure > clock.GetUtcNow().UtcDateTime))
                throw new ArgumentException("The verified provider closure must fall between reservation and the current UTC time.");
            if (call.ProviderClosedUtc is not null)
            {
                if (!terminal) return true; // A delayed accepted event cannot reopen a closed call.
                if (call.State != state) throw new InvalidOperationException("Provider terminal states conflict; reconcile before changing evidence.");
                return true;
            }
            if (state == "rejected" && call.State == "accepted")
                throw new InvalidOperationException("An accepted call requires a closure receipt.");
            call.State = state; call.ProviderStateReceipt = receipt;
            call.ProviderClosedUtc = providerClosedUtc; call.Revision = Guid.NewGuid();
            if (providerClosedUtc is { } closed)
            {
                var periodEnd = await write.Set<ReceptionistBudgetPeriod>().Where(x => x.Id == call.OwnerBudgetPeriodId)
                    .Select(x => x.EndUtc).SingleAsync(ct);
                if (closed > periodEnd) await HoldVoiceAllocationAsync(write, call, ct);
            }
            // Closure releases concurrency only. Usage and money remain reserved until separately reconciled.
            return true;
        }, ct);
    }

    internal async Task<ReceptionistActionClaim> ClaimActionAsync(Guid callId, string logicalActionKey,
        string capabilityKey, string argumentsSha256, string confirmationReceipt, Guid sidebandLeaseToken, CancellationToken ct = default)
    {
        ReceptionistConfigurationCodec.Identifier(logicalActionKey, 160, nameof(logicalActionKey));
        ReceptionistConfigurationCodec.Identifier(capabilityKey, 100, nameof(capabilityKey));
        RequireSha256(argumentsSha256);
        ReceptionistConfigurationCodec.Required(confirmationReceipt, 512, nameof(confirmationReceipt));
        return await WithCallAsync(callId, async (write, call) =>
        {
            var existing = await write.Set<ReceptionistActionIntent>().SingleOrDefaultAsync(x => x.CallId == callId && x.LogicalActionKey == logicalActionKey, ct);
            if (existing is not null)
            {
                if (existing.CapabilityKey != capabilityKey || existing.ArgumentsSha256 != argumentsSha256 || existing.ConfirmationReceipt != confirmationReceipt)
                    throw new InvalidOperationException("A logical action cannot be replayed with different confirmed facts.");
                return new ReceptionistActionClaim(false, existing.State, existing.Id, existing.RouteKey, existing.ActualRecordReference, existing.VerificationReceipt, existing.AttemptId);
            }
            var context = await ContextAsync(write, call, ct);
            var now = clock.GetUtcNow().UtcDateTime;
            if (!OwnsLease(call, sidebandLeaseToken, now) || !await AdmissionStillEnabledAsync(write, call, ct))
                return new ReceptionistActionClaim(false, "call_not_active", null);
            var capability = context.Binding.Capabilities.SingleOrDefault(x => x.Key == capabilityKey);
            if (!context.Configuration.RequestedCapabilities.Contains(capabilityKey, StringComparer.Ordinal) ||
                capability?.State != ReceptionistCapabilityStates.Accepted)
                return new ReceptionistActionClaim(false, "capability_unaccepted", null);
            var action = new ReceptionistActionIntent
            {
                CallId = callId, LogicalActionKey = logicalActionKey, CapabilityKey = capabilityKey,
                RouteKey = capability.RouteKey, ArgumentsSha256 = argumentsSha256, ConfirmationReceipt = confirmationReceipt,
                ExecutionLeaseToken = sidebandLeaseToken, ExecutionFence = call.SidebandFence, WorkerId = call.SidebandWorkerId,
                CreatedUtc = now, UpdatedUtc = now
            };
            write.Add(action);
            return new ReceptionistActionClaim(true, action.State, action.Id, action.RouteKey, AttemptId: action.AttemptId);
        }, ct);
    }

    internal Task<bool> RecordSyntheticIntakeOutcomeAsync(Guid callId, Guid actionId, string hash,
        string confirmation, string reference, string receipt, CancellationToken ct) =>
        RecordSyntheticOutcomeAsync(callId, actionId, ReceptionistCapabilities.CanonicalIntake,
            Leads.ReceptionistInquiryAuthority.RouteKey, hash, confirmation, reference, receipt, ct);

    internal Task<bool> RecordSyntheticBookingOutcomeAsync(Guid callId, Guid actionId, string hash,
        string confirmation, string reference, string receipt, CancellationToken ct) =>
        RecordSyntheticOutcomeAsync(callId, actionId, ReceptionistCapabilities.CanonicalBooking,
            Bookings.ReceptionistBookingAuthority.RouteKey, hash, confirmation, reference, receipt, ct);

    private Task<bool> RecordSyntheticOutcomeAsync(Guid callId, Guid actionId, string capability, string route,
        string hash, string confirmation, string reference, string receipt, CancellationToken ct) => WithCallAsync(callId, async (write, call) =>
    {
        var action = await write.Set<ReceptionistActionIntent>().SingleAsync(x => x.Id == actionId && x.CallId == call.Id, ct);
        if (!call.ControlledTest || action.CapabilityKey != capability ||
            action.RouteKey != route || action.ArgumentsSha256 != hash ||
            action.ConfirmationReceipt != confirmation)
            throw new InvalidOperationException("The synthetic outcome does not match its claimed scope.");
        if (action.State == "verified" && action.ActualRecordReference == reference && action.VerificationReceipt == receipt) return false;
        if (action.State != "pending" || action.ExecutionLeaseToken is not { } lease ||
            action.ExecutionFence != call.SidebandFence || !OwnsLease(call, lease, clock.GetUtcNow().UtcDateTime) ||
            !await AdmissionStillEnabledAsync(write, call, ct))
            throw new InvalidOperationException("The synthetic outcome no longer owns an active confirmed action.");
        action.State = "verified"; action.ActualRecordReference = reference; action.VerificationReceipt = receipt;
        action.UpdatedUtc = clock.GetUtcNow().UtcDateTime; action.Revision = Guid.NewGuid();
        return true;
    }, ct);

    internal async Task<bool> RecordActionOutcomeAsync(Guid actionId, string state, string? actualRecordReference,
        string verificationReceipt, CancellationToken ct = default)
    {
        if (state is not ("verified" or "uncertain" or "rejected")) throw new ArgumentException("Unsupported action state.");
        ReceptionistConfigurationCodec.Required(verificationReceipt, 512, nameof(verificationReceipt));
        if (state == "verified") ReceptionistConfigurationCodec.Required(actualRecordReference, 512, nameof(actualRecordReference));
        else if (actualRecordReference is not null) ReceptionistConfigurationCodec.Required(actualRecordReference, 512, nameof(actualRecordReference));
        await using var read = new MasterAppDbContext(options);
        var callId = await read.Set<ReceptionistActionIntent>().Where(x => x.Id == actionId).Select(x => x.CallId).SingleAsync(ct);
        return await WithCallAsync(callId, async (write, call) =>
        {
            var action = await write.Set<ReceptionistActionIntent>().SingleAsync(x => x.Id == actionId && x.CallId == call.Id, ct);
            if (state == "verified" && action.CapabilityKey == StopCapability && call.ProviderClosedUtc is null)
                throw new InvalidOperationException("A stop action is successful only after verified provider closure.");
            if (action.State is "verified" or "rejected")
            {
                if (action.State != state || action.ActualRecordReference != actualRecordReference)
                    throw new InvalidOperationException("A terminal action outcome cannot be overwritten by a conflicting receipt.");
                return false;
            }
            action.State = state; action.ActualRecordReference = actualRecordReference;
            action.VerificationReceipt = verificationReceipt; action.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
            action.Revision = Guid.NewGuid();
            return true;
        }, ct);
    }

    internal async Task<IReadOnlyList<ReceptionistStopRequest>> FindStopRequestsAsync(int maximum, CancellationToken ct = default)
    {
        if (maximum is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximum));
        var now = clock.GetUtcNow().UtcDateTime;
        await using var read = new MasterAppDbContext(options);
        var calls = await CallsRequiringStop(read, now).Where(call =>
                !read.Set<ReceptionistActionIntent>().Any(action => action.CallId == call.Id && action.CapabilityKey == StopCapability &&
                    !(action.ConfirmationReceipt == BrowserCleanupReceipt && action.State == "rejected" && action.VerificationReceipt == BrowserCleanupReleased)))
            .OrderBy(x => x.MustStopUtc).ThenBy(x => x.Id).Take(maximum).ToListAsync(ct);
        return calls.Select(call => new ReceptionistStopRequest(call.Id, call.Provider, call.ProviderProjectKey, call.ProviderSessionId!,
            call.ConfigurationVersionId, call.MustStopUtc <= now ? "deadline" : "kill_switch")).ToArray();
    }

    private static IQueryable<ReceptionistCall> CallsRequiringStop(MasterAppDbContext read, DateTime now) =>
        read.Set<ReceptionistCall>().AsNoTracking().Where(call => call.ProviderSessionId != null && call.ProviderClosedUtc == null &&
            (call.MustStopUtc <= now || read.MarketingConnections.Any(connection => connection.Id == call.MarketingConnectionId &&
                (!connection.ReceptionistAdmissionEnabled || connection.ReceptionistControlRevision != call.ConnectionControlRevision ||
                    connection.ReceptionistProviderProjectKey != call.ProviderProjectKey)) ||
             read.Set<ReceptionistBudgetAccount>().Any(account => account.Id == call.OwnerBudgetAccountId &&
                (!account.AdmissionEnabled || account.ControlRevision != call.OwnerControlRevision)) ||
             read.Set<ReceptionistCostReservation>().Any(cost => cost.CallId == call.Id &&
                 read.Set<ReceptionistBudgetAccount>().Any(account => account.Id == cost.FundingBudgetAccountId &&
                     (!account.AdmissionEnabled || account.ControlRevision != cost.FundingControlRevision)))));

    private async Task<T> WithCallAsync<T>(Guid callId, Func<MasterAppDbContext, ReceptionistCall, Task<T>> operation, CancellationToken ct)
    {
        await using var read = new MasterAppDbContext(options);
        var owner = await read.Set<ReceptionistCall>().Where(x => x.Id == callId).Select(x => x.OwnerBudgetAccountId).SingleAsync(ct);
        var funding = await read.Set<ReceptionistCostReservation>().Where(x => x.CallId == callId).Select(x => x.FundingBudgetAccountId).Distinct().ToArrayAsync(ct);
        return await ReceptionistSql.TransactionAsync(options, funding.Append(owner).ToArray(), async write =>
            await operation(write, await write.Set<ReceptionistCall>().SingleAsync(x => x.Id == callId, ct)), ct);
    }

    private static async Task<ReceptionistCallContext> ContextAsync(MasterAppDbContext read, ReceptionistCall call, CancellationToken ct)
    {
        if (call.ProviderSessionId is null) throw new InvalidOperationException("Provider identity is not yet bound.");
        var connection = await read.MarketingConnections.AsNoTracking().SingleAsync(x => x.Id == call.MarketingConnectionId, ct);
        var version = await read.Set<ReceptionistConfigurationVersion>().AsNoTracking().SingleAsync(
            x => x.Id == call.ConfigurationVersionId && x.MarketingConnectionId == connection.Id, ct);
        var owner = ReceptionistSql.Owner(connection);
        var account = await read.Set<ReceptionistBudgetAccount>().AsNoTracking().SingleAsync(x => x.Id == call.OwnerBudgetAccountId, ct);
        if (account.Kind != "owner" || account.ScopeKey != owner.Key) throw new InvalidOperationException("Call accounting ownership is inconsistent.");
        var configuration = ReceptionistConfigurationCodec.Read(version.ConfigurationJson, version.ConfigurationSha256);
        var binding = ReceptionistConfigurationCodec.ReadBinding(version.AcceptedBindingJson, configuration)
            ?? throw new InvalidOperationException("The pinned provider binding is absent.");
        if (binding.IngressKey != call.IngressKey || binding.ProviderProjectKey != call.ProviderProjectKey || connection.Provider != call.Provider)
            throw new InvalidOperationException("The pinned provider identity is inconsistent.");
        // Preserve the original immutable session context for stopping and observation after current controls drift.
        // Dialogue and new business effects additionally require the live project claim in AdmissionStillEnabledAsync.
        return new(call.Id, owner, call.Provider, call.ProviderProjectKey, call.ProviderSessionId, version.Id,
            configuration, binding, call.MustStopUtc, call.State, call.ControlledTest);
    }

    private static async Task<bool> AdmissionStillEnabledAsync(MasterAppDbContext read, ReceptionistCall call, CancellationToken ct) =>
        await read.MarketingConnections.AnyAsync(x => x.Id == call.MarketingConnectionId && x.ReceptionistAdmissionEnabled &&
            x.ReceptionistControlRevision == call.ConnectionControlRevision && x.ReceptionistProviderProjectKey == call.ProviderProjectKey, ct) &&
        await read.Set<ReceptionistBudgetAccount>().AnyAsync(x => x.Id == call.OwnerBudgetAccountId && x.AdmissionEnabled && x.ControlRevision == call.OwnerControlRevision, ct) &&
        !await read.Set<ReceptionistCostReservation>().AnyAsync(cost => cost.CallId == call.Id &&
            read.Set<ReceptionistBudgetAccount>().Any(account => account.Id == cost.FundingBudgetAccountId &&
                (!account.AdmissionEnabled || account.ControlRevision != cost.FundingControlRevision)), ct);

    private static Task<ReceptionistSessionDecision?> SessionDecisionAsync(MasterAppDbContext read, TrustedReceptionistIngress ingress, CancellationToken ct) =>
        read.Set<ReceptionistSessionDecision>().AsNoTracking().SingleOrDefaultAsync(x => x.Provider == ingress.Provider &&
            x.ProviderProjectKey == ingress.ProviderProjectKey && x.ProviderSessionId == ingress.ProviderSessionId, ct);

    private static async Task<MarketingConnection?> AuthenticatedConnectionAsync(
        MasterAppDbContext read, TrustedReceptionistIngress ingress, CancellationToken ct)
    {
        var connection = await read.MarketingConnections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == ingress.AuthenticatedConnectionId, ct);
        if (connection is null || connection.Provider != ingress.Provider || connection.OwnerKey != ingress.AuthenticatedOwnerKey ||
            connection.ReceptionistIngressKey != ingress.IngressKey || connection.ReceptionistActiveConfigurationId != ingress.AuthenticatedConfigurationId ||
            connection.ReceptionistProviderProjectKey != ingress.ProviderProjectKey)
            return null;
        _ = ReceptionistSql.Owner(connection);
        return connection;
    }

    private static async Task AssertEventAvailableAsync(MasterAppDbContext read, TrustedReceptionistIngress ingress, Guid? decisionId, CancellationToken ct)
    {
        var existing = await read.Set<ReceptionistIngressEvent>().AsNoTracking().SingleOrDefaultAsync(x => x.Provider == ingress.Provider &&
            x.ProviderProjectKey == ingress.ProviderProjectKey && x.EventId == ingress.EventId, ct);
        if (existing is not null && existing.DecisionId != decisionId)
            throw new InvalidOperationException("The provider event identity is already bound to another session decision.");
    }

    private static ReceptionistAdmission SaveDecision(MasterAppDbContext write, TrustedReceptionistIngress ingress, DateTime now, string reason, ReceptionistCall? call)
    {
        var decision = new ReceptionistSessionDecision
        {
            Provider = ingress.Provider, ProviderProjectKey = ingress.ProviderProjectKey, ProviderSessionId = ingress.ProviderSessionId,
            IngressKey = ingress.IngressKey, Outcome = call is null ? "denied" : "reserved", Reason = reason,
            CallId = call?.Id, DecidedUtc = now
        };
        write.Add(decision);
        write.Add(new ReceptionistIngressEvent { DecisionId = decision.Id, Provider = ingress.Provider,
            ProviderProjectKey = ingress.ProviderProjectKey, EventId = ingress.EventId, ReceivedUtc = now });
        // The webhook may acknowledge this durable decision immediately. Only ClaimProviderAcceptAsync grants external I/O.
        return new(call is not null, false, reason, call?.Id, call?.ConfigurationVersionId, call?.MustStopUtc);
    }

    private static async Task<ReceptionistAdmission> ReplayAsync(MasterAppDbContext write, ReceptionistSessionDecision decision,
        TrustedReceptionistIngress ingress, DateTime now, CancellationToken ct, bool persistEvent = true)
    {
        if (decision.IngressKey != ingress.IngressKey) return new(false, false, "session_binding_mismatch", null);
        var call = decision.CallId is { } id ? await write.Set<ReceptionistCall>().AsNoTracking().SingleAsync(x => x.Id == id, ct) : null;
        if (call is not null && (call.MarketingConnectionId != ingress.AuthenticatedConnectionId ||
            call.ConfigurationVersionId != ingress.AuthenticatedConfigurationId))
            return new(false, false, "session_binding_mismatch", null);
        await AssertEventAvailableAsync(write, ingress, decision.Id, ct);
        if (persistEvent && !await write.Set<ReceptionistIngressEvent>().AnyAsync(x => x.Provider == ingress.Provider &&
            x.ProviderProjectKey == ingress.ProviderProjectKey && x.EventId == ingress.EventId, ct))
            write.Add(new ReceptionistIngressEvent { DecisionId = decision.Id, Provider = ingress.Provider,
                ProviderProjectKey = ingress.ProviderProjectKey, EventId = ingress.EventId, ReceivedUtc = now });
        return new(call is not null, false, decision.Reason, call?.Id, call?.ConfigurationVersionId, call?.MustStopUtc);
    }

    private static decimal CashRemaining(ReceptionistCostReservation value) => value.CashFinal ? 0m : Math.Max(0m, value.CashReserved - value.CashObserved);
    private static decimal ConsumptionRemaining(ReceptionistCostReservation value) => value.ConsumptionFinal ? 0m : Math.Max(0m, value.ConsumptionReserved - value.ConsumptionObserved);
    private static void RequireSha256(string value)
    {
        if (value is not { Length: 64 } || value.Any(ch => !(ch is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("An exact lowercase SHA-256 fingerprint is required.");
    }
}
