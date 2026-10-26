using System.Globalization;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Reception;

public sealed partial class ReceptionistAdmissionService
{
    internal async Task<ReceptionistOperatorPreparation> ReadOperatorPreparationAsync(
        string expectedOwnerKey, Guid configurationId, CancellationToken ct)
    {
        await using var read = new MasterAppDbContext(options);
        // A coherent read across settings, immutable configuration and accounts.
        // This path performs no SaveChanges, provider calls, or acceptance writes.
        await using var transaction = await read.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var version = await read.Set<ReceptionistConfigurationVersion>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == configurationId, ct)
            ?? throw new InvalidOperationException("The requested current configuration is unavailable.");
        var connection = await read.MarketingConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == version.MarketingConnectionId && x.OwnerKey == expectedOwnerKey &&
                (x.ReceptionistActiveConfigurationId == configurationId || !read.Set<ReceptionistConfigurationVersion>().Any(v => v.MarketingConnectionId == x.Id && v.Version > version.Version)), ct)
            ?? throw new InvalidOperationException("The requested configuration is not current for this settings owner.");
        var owner = ReceptionistSql.Owner(connection);
        if (owner.Key != expectedOwnerKey || connection.Provider != "openai")
            throw new InvalidOperationException("The canonical owner or receptionist provider does not match.");
        var config = ReceptionistConfigurationCodec.Read(version.ConfigurationJson, version.ConfigurationSha256);
        var binding = ReceptionistConfigurationCodec.ReadBinding(version.AcceptedBindingJson, config);
        if (binding is not null && connection.ReceptionistProviderProjectKey != binding.ProviderProjectKey)
            throw new InvalidOperationException("The current binding does not match the connection provider project.");
        var fundingIds = binding?.Costs.Select(x => x.FundingBudgetAccountId).Distinct().ToArray() ?? [];
        if (fundingIds.Length > 64) throw new InvalidOperationException("The current funding scope exceeds its supported bound.");
        var accounts = await read.Set<ReceptionistBudgetAccount>().AsNoTracking()
            .Where(x => x.Kind == "owner" && x.ScopeKey == expectedOwnerKey || x.Kind == "funding" && fundingIds.Contains(x.Id))
            .OrderBy(x => x.Id).ToListAsync(ct);
        if (accounts.Any(x => x.Currency != config.Currency || x.Kind == "owner" && x.TimeZoneId != config.TimeZoneId) ||
            fundingIds.Any(id => accounts.All(x => x.Id != id)))
            throw new InvalidOperationException("The current accounting scope is inconsistent.");
        var historical = await read.Set<ReceptionistConfigurationVersion>().AsNoTracking()
            .Where(x => x.MarketingConnectionId == connection.Id && x.Version < version.Version && x.AcceptedBindingJson != null)
            .OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        ReceptionistPreparationHistory? prior = null;
        if (historical is not null)
        {
            var oldConfig = ReceptionistConfigurationCodec.Read(historical.ConfigurationJson, historical.ConfigurationSha256);
            var oldBinding = ReceptionistConfigurationCodec.ReadBinding(historical.AcceptedBindingJson, oldConfig);
            if (oldBinding is not null)
            {
                // The same owner's immutable historical binding supplies the relationship;
                // no caller-selected account IDs and no inheritance by the current draft.
                var oldFundingIds = oldBinding.Costs.Select(x => x.FundingBudgetAccountId).Distinct().ToArray();
                if (oldFundingIds.Length > 64) throw new InvalidOperationException("The historical funding scope exceeds its supported bound.");
                var oldFunding = await read.Set<ReceptionistBudgetAccount>().AsNoTracking()
                    .Where(x => oldFundingIds.Contains(x.Id)).OrderBy(x => x.Id).ToListAsync(ct);
                if (oldFunding.Count != oldFundingIds.Length || oldFunding.Any(x => x.Kind != "funding" || x.Currency != oldConfig.Currency))
                    throw new InvalidOperationException("The historical funding references are inconsistent.");
                var evidence = new ReceptionistHistoricalBindingEvidence("historical_not_current_acceptance",
                    oldBinding.ProviderProjectKey, oldBinding.Model, oldBinding.Voice, oldBinding.BackendMode,
                    oldBinding.Transport, oldBinding.ControlledTestOnly, oldBinding.MaximumSupportedCallSeconds,
                    oldBinding.CredentialReference, oldBinding.WebhookSecretReference, oldBinding.OperatorAcceptancePackageSha256,
                    [new("provider_setup", oldBinding.ProviderVerificationReceipt),
                     new("exclusive_project", oldBinding.ExclusiveProjectVerificationReceipt),
                     new("cost_bounds", oldBinding.CostVerificationReceipt),
                     new("disclosure", oldBinding.DisclosureConfigurationReceipt),
                     new("retention", oldBinding.RetentionConfigurationReceipt),
                     new("carrier_fallback", oldBinding.CarrierFallbackReceipt)],
                    oldBinding.Costs.Select(x => new ReceptionistPreparationCost(x.Key, x.FundingBudgetAccountId,
                        x.CashFixedMaximum, x.CashPerSecondMaximum, x.ConsumptionFixedMaximum, x.ConsumptionPerSecondMaximum)).ToArray(),
                    oldBinding.Capabilities.Select(x => new ReceptionistPreparationCapability(x.Key, x.RouteKey,
                        x.State, x.AcceptanceReceipt)).ToArray(),
                    oldFunding.Select(x => new ReceptionistPreparationAccount(x.Id, x.Revision, x.Kind,
                        x.ScopeKey, x.Currency, x.TimeZoneId)).ToArray(),
                    oldConfig.Currency == config.Currency && oldConfig.TimeZoneId == config.TimeZoneId &&
                        oldBinding.ProviderProjectKey == connection.ReceptionistProviderProjectKey);
                prior = new(historical.Id, historical.Version, historical.ConfigurationSha256,
                    historical.CreatedUtc, historical.CreatedByActorId, evidence);
            }
        }
        return new(clock.GetUtcNow().UtcDateTime, "observed_not_reviewed", connection.Id, connection.OwnerKey,
            connection.Provider, connection.Revision, connection.ReceptionistControlRevision, version.Id, version.Version,
            version.ConfigurationSha256, connection.ReceptionistProviderProjectKey, connection.ReceptionistAdmissionEnabled,
            binding is not null, accounts.Select(x => new ReceptionistPreparationAccount(x.Id, x.Revision, x.Kind,
                x.ScopeKey, x.Currency, x.TimeZoneId)).ToArray(), prior,
            ["This observation is not a reviewed operation, provider verification, or permission to enable calls.",
             binding is null ? "Current configuration has no accepted binding. Historical acceptance and funding are not inherited."
                : "Current binding metadata does not replace independently reviewed provider, capability and cost evidence.",
             "Prepare and review exact configuration evidence, then register its JSON and SHA256 through the existing operator package configuration.",
             "Historical record provenance is not a new review. Receipt references do not supply missing evidence hashes, observation dates, reviewer/expiry, or credential fingerprints. No credentials are decrypted or fingerprints derived here.",
             "No test window, call, admission extension or activation is created by this export."]);
    }

    internal async Task ApplyOperatorAccountingAsync(ReceptionistOperatorPackage package,
        ReceptionistOperatorState state, Func<MasterAppDbContext, Task> guard, CancellationToken ct)
    {
        switch (package.Operation)
        {
            case "open_budget_period":
                await OpenBudgetPeriodCoreAsync(package.Opening!, guard, ct);
                break;
            case "approve_budget_ceilings":
                await ApproveBudgetCeilingsCoreAsync(package.BudgetAccountId!.Value,
                    package.ExpectedAccountRevisions[package.BudgetAccountId.Value], package.CashLimit!.Value,
                    package.ConsumptionLimit!.Value, package.ReceiptReference, guard, ct);
                break;
            case "set_budget_admission":
                await SetBudgetAdmissionCoreAsync(package.BudgetAccountId!.Value,
                    package.ExpectedAccountRevisions[package.BudgetAccountId.Value], package.Enabled!.Value, guard, ct);
                break;
            case "reconcile_call_cost":
                await ReconcileCostCoreAsync(package.CallId!.Value, package.ComponentKey!, package.ReceiptReference,
                    package.CallCost!, guard, ct);
                break;
            case "reconcile_account_charge":
                await ReconcileAccountChargeCoreAsync(package.AccountCharge!, guard, ct);
                break;
            case "reconcile_dated_voice":
                await ReconcileDatedVoiceUsageCoreAsync(package.CallId!.Value, package.ReceiptReference,
                    package.VoiceAllocations!, package.FinalVoiceSeconds, guard, ct);
                break;
            case "record_provider_closure":
                await RecordProviderClosureAndUsageCoreAsync(package.CallId!.Value, package.ReceiptReference,
                    package.ObservedProviderClosedUtc!.Value, package.FinalVoiceSeconds, guard, ct);
                break;
            default:
                throw new ArgumentException("Unsupported accounting operation.");
        }
    }

    internal async Task<ReceptionistOperatorState> ReadOperatorStateAsync(ReceptionistOperatorPackage package,
        string packageSha256, CancellationToken ct)
    {
        await using var read = new MasterAppDbContext(options);
        return await ReadOperatorStateAsync(read, package, packageSha256, ct);
    }

    // Also called inside each canonical transaction after its existing dimension locks are held.
    // This is a projection and precondition check, not a second persistence/coordination owner.
    internal async Task<ReceptionistOperatorState> ReadOperatorStateAsync(MasterAppDbContext read,
        ReceptionistOperatorPackage package, string packageSha256, CancellationToken ct)
    {
        var connection = await read.MarketingConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == package.MarketingConnectionId, ct)
            ?? throw new InvalidOperationException("The scoped settings authority is absent.");
        var owner = ReceptionistSql.Owner(connection);
        if (owner.Key != package.ExpectedOwnerKey || connection.Provider != package.Provider)
            throw new InvalidOperationException("The reviewed settings owner or provider does not match.");
        var version = await read.Set<ReceptionistConfigurationVersion>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == package.ConfigurationId && x.MarketingConnectionId == connection.Id, ct)
            ?? throw new InvalidOperationException("The reviewed configuration does not belong to this settings authority.");
        if (version.ConfigurationSha256 != package.ConfigurationSha256)
            throw new InvalidOperationException("The reviewed configuration fingerprint changed.");
        var configuration = ReceptionistConfigurationCodec.Read(version.ConfigurationJson, version.ConfigurationSha256);
        var binding = ReceptionistConfigurationCodec.ReadBinding(version.AcceptedBindingJson, configuration);
        var ownerAccount = await read.Set<ReceptionistBudgetAccount>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Kind == "owner" && x.ScopeKey == owner.Key, ct);
        ReceptionistCall? call = null;
        var ids = new HashSet<Guid>();
        if (package.CallId is { } callId)
        {
            call = await read.Set<ReceptionistCall>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == callId, ct)
                ?? throw new InvalidOperationException("The reviewed call is absent.");
            if (call.MarketingConnectionId != connection.Id || call.ConfigurationVersionId != version.Id ||
                call.Provider != package.Provider || call.ProviderProjectKey != package.ProviderProjectKey ||
                ownerAccount?.Id != call.OwnerBudgetAccountId || binding?.ProviderProjectKey != call.ProviderProjectKey)
                throw new InvalidOperationException("The reviewed call does not match its immutable owner and provider binding.");
            ids.Add(call.OwnerBudgetAccountId);
            foreach (var id in await read.Set<ReceptionistCostReservation>().AsNoTracking()
                .Where(x => x.CallId == callId).Select(x => x.FundingBudgetAccountId).Distinct().ToArrayAsync(ct)) ids.Add(id);
        }
        else
        {
            if (connection.ReceptionistProviderProjectKey is { } claimed && claimed != package.ProviderProjectKey)
                throw new InvalidOperationException("The current exclusive provider project does not match the reviewed subject.");
            switch (package.Operation)
            {
                case "accept_binding":
                    ReceptionistConfigurationCodec.ValidateBinding(package.Binding!, configuration);
                    if (ownerAccount is not null) ids.Add(ownerAccount.Id);
                    foreach (var cost in package.Binding!.Costs) ids.Add(cost.FundingBudgetAccountId);
                    break;
                case "set_owner_admission":
                    if (ownerAccount is not null) ids.Add(ownerAccount.Id);
                    if (package.Enabled == true && (binding is null || connection.ReceptionistProviderProjectKey != binding.ProviderProjectKey ||
                        binding.ProviderProjectKey != package.ProviderProjectKey))
                        throw new InvalidOperationException("The accepted current project is required before enabling admission.");
                    break;
                case "open_budget_period":
                    var opening = package.Opening!;
                    if (opening.Kind is not ("owner" or "funding") || opening.Kind == "owner" &&
                        (opening.ScopeKey != owner.Key || opening.TimeZoneId != configuration.TimeZoneId) || opening.Currency != configuration.Currency)
                        throw new InvalidOperationException("The reviewed opening does not match its accounting subject.");
                    var existing = await read.Set<ReceptionistBudgetAccount>().AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Kind == opening.Kind && x.ScopeKey == opening.ScopeKey, ct);
                    if (existing is not null) ids.Add(existing.Id);
                    break;
                case "approve_budget_ceilings":
                case "set_budget_admission":
                    ids.Add(package.BudgetAccountId!.Value);
                    break;
                case "reconcile_account_charge":
                    if (ownerAccount?.Id != package.AccountCharge!.OwnerBudgetAccountId)
                        throw new InvalidOperationException("The reviewed charge does not belong to this owner.");
                    ids.Add(package.AccountCharge.OwnerBudgetAccountId); ids.Add(package.AccountCharge.FundingBudgetAccountId);
                    break;
                default:
                    throw new ArgumentException("Unsupported operator subject.");
            }
        }
        if (ids.Count > 64) throw new InvalidOperationException("The reviewed accounting scope exceeds its supported bound.");
        var accounts = await read.Set<ReceptionistBudgetAccount>().AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        if (accounts.Count != ids.Count || accounts.Any(x => x.Currency != configuration.Currency ||
            x.Kind == "owner" && (x.ScopeKey != owner.Key || x.TimeZoneId != configuration.TimeZoneId) || x.Kind is not ("owner" or "funding")))
            throw new InvalidOperationException("An accounting dimension does not match the reviewed owner or currency.");
        foreach (var account in accounts.Where(x => x.Kind == "funding"))
            if (!(package.Operation == "open_budget_period" && package.Opening!.Kind == "funding" && package.Opening.ScopeKey == account.ScopeKey) &&
                (!package.FundingScopes.TryGetValue(account.Id, out var scope) || scope != account.ScopeKey))
                throw new InvalidOperationException("The reviewed shared funding scope does not match.");
        if (package.FundingScopes.Keys.Except(accounts.Where(x => x.Kind == "funding").Select(x => x.Id)).Any())
            throw new InvalidOperationException("The package includes an unrelated funding authority.");

        var periods = await read.Set<ReceptionistBudgetPeriod>().AsNoTracking().Where(x => ids.Contains(x.BudgetAccountId)).Take(4097).ToListAsync(ct);
        var reservations = await read.Set<ReceptionistCostReservation>().AsNoTracking().Where(x =>
            (ids.Contains(x.OwnerBudgetAccountId) || ids.Contains(x.FundingBudgetAccountId)) && (!x.CashFinal || !x.ConsumptionFinal || x.CallId == package.CallId))
            .Take(4097).ToListAsync(ct);
        var charges = await read.Set<ReceptionistAccountCharge>().AsNoTracking().Where(x =>
            (ids.Contains(x.OwnerBudgetAccountId) || ids.Contains(x.FundingBudgetAccountId)) && (!x.CashFinal || !x.ConsumptionFinal))
            .Take(4097).ToListAsync(ct);
        var voice = await read.Set<ReceptionistCall>().AsNoTracking().Where(x => ids.Contains(x.OwnerBudgetAccountId) &&
            (!x.VoiceUsageFinal || x.VoiceAllocationUncertain)).Select(x => new
            { x.Id, x.OwnerBudgetAccountId, x.OwnerBudgetPeriodId, x.ReservedUtc, x.ProviderClosedUtc, x.ReservedVoiceSeconds, x.ObservedVoiceSeconds, x.VoiceUsageFinal, x.VoiceAllocationUncertain })
            .Take(4097).ToListAsync(ct);
        if (periods.Count > 4096 || reservations.Count > 4096 || charges.Count > 4096 || voice.Count > 4096)
            throw new InvalidOperationException("The scoped reconciliation exceeds the supported review bound.");
        var now = clock.GetUtcNow().UtcDateTime;
        ReceptionistActionIntent? bookingAction = null;
        if (package.Operation == "reconcile_booking_action")
        {
            bookingAction = await read.Set<ReceptionistActionIntent>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == package.BookingActionId && x.CallId == package.CallId, ct);
            if (call?.ControlledTest != false || bookingAction is null ||
                bookingAction.CapabilityKey != ReceptionistCapabilities.CanonicalBooking ||
                bookingAction.RouteKey != Bookings.ReceptionistBookingAuthority.RouteKey ||
                bookingAction.State is not ("pending" or "uncertain" or "verified" or "rejected") ||
                bookingAction.State == "rejected" && !IsZeroEffectBookingRejection(bookingAction))
                throw new InvalidOperationException("The exact original production booking attempt is required.");
        }
        var completed = await OperatorCompletionAsync(read, package, packageSha256, connection, configuration,
            accounts, periods, call, reservations, now, ct);
        var latestConfigurationId = await read.Set<ReceptionistConfigurationVersion>().Where(x => x.MarketingConnectionId == connection.Id)
            .OrderByDescending(x => x.Version).Select(x => x.Id).FirstAsync(ct);
        var stateHash = ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new
        {
            connection.Id, connection.OwnerKey, connection.Provider, connection.Revision,
            connection.ReceptionistActiveConfigurationId, connection.ReceptionistProviderProjectKey,
            connection.ReceptionistAdmissionEnabled, connection.ReceptionistControlRevision,
            ConfigurationId = version.Id, version.ConfigurationSha256, latestConfigurationId,
            CallRevision = call?.Revision,
            BookingActionRevision = bookingAction?.Revision,
            Accounts = accounts.OrderBy(x => x.Id), Periods = periods.OrderBy(x => x.Id),
            Reservations = reservations.OrderBy(x => x.Id), Charges = charges.OrderBy(x => x.Id), Voice = voice.OrderBy(x => x.Id),
            CurrentPeriods = periods.Where(x => x.StartUtc <= now && x.EndUtc > now).Select(x => x.Id).OrderBy(x => x)
        }));
        return new(owner, connection, version, configuration, binding, call, accounts, stateHash,
            completed.Reference, completed.Satisfied, OperatorFacts(package, connection, version, configuration, accounts, periods,
                reservations, charges, call, now, bookingAction)) { BookingActionRevision = bookingAction?.Revision, LatestConfigurationId = latestConfigurationId };
    }

    private async Task<(string? Reference, bool Satisfied)> OperatorCompletionAsync(MasterAppDbContext read,
        ReceptionistOperatorPackage package, string packageSha, MarketingConnection connection,
        ReceptionistBusinessConfiguration configuration, IReadOnlyList<ReceptionistBudgetAccount> accounts,
        IReadOnlyList<ReceptionistBudgetPeriod> periods, ReceptionistCall? call,
        IReadOnlyList<ReceptionistCostReservation> reservations, DateTime now, CancellationToken ct)
    {
        switch (package.Operation)
        {
            case "reconcile_booking_action":
                var booking = await read.Set<ReceptionistActionIntent>().AsNoTracking().SingleAsync(x =>
                    x.Id == package.BookingActionId && x.CallId == package.CallId, ct);
                return (Bookings.ReceptionistBookingAuthority.VerifiedAction(booking, configuration) || IsZeroEffectBookingRejection(booking))
                    ? ("booking-action:" + booking.Id.ToString("D"), true) : (null, false);
            case "accept_binding":
                // The private immutable binding pins the exact reviewed package. A historical replay
                // verifies that receipt; it never reactivates an old version after an owner edit.
                var versions = await read.Set<ReceptionistConfigurationVersion>().AsNoTracking().Where(x =>
                    x.MarketingConnectionId == connection.Id && x.ConfigurationSha256 == package.ConfigurationSha256 &&
                    x.AcceptedBindingJson != null && x.AcceptedBindingJson.Contains(packageSha)).Take(2).ToListAsync(ct);
                var match = versions.SingleOrDefault(x => ReceptionistConfigurationCodec.ReadBinding(x.AcceptedBindingJson, configuration)?.OperatorAcceptancePackageSha256 == packageSha);
                return (match is null ? null : "configuration:" + match.Id.ToString("D"), false);
            case "open_budget_period":
                var opening = package.Opening!;
                var month = ReceptionistConfigurationCodec.Month(opening.TimeZoneId, opening.Year, opening.Month);
                var account = accounts.SingleOrDefault();
                if (account is null) return (null, false);
                if (account.Kind != opening.Kind || account.ScopeKey != opening.ScopeKey || account.Currency != opening.Currency || account.TimeZoneId != opening.TimeZoneId)
                    throw new InvalidOperationException("The reviewed opening authority differs.");
                var period = periods.SingleOrDefault(x => x.BudgetAccountId == account.Id && x.MonthKey == month.Key);
                if (period is null) return (null, false);
                if (!period.OpeningReconciled || period.OpeningReceipt != opening.ReconciliationReceipt || period.ReconciledThroughUtc != opening.ReconciledThroughUtc ||
                    period.OpeningCashCeiling != opening.CashLimit || period.OpeningConsumptionCeiling != opening.ConsumptionLimit ||
                    period.OpeningCashCommitted != opening.KnownCashCommitted || period.OpeningConsumptionCommitted != opening.KnownConsumptionCommitted || period.OpeningVoiceSeconds != opening.KnownVoiceSeconds)
                    throw new InvalidOperationException("An existing immutable opening differs from the reviewed facts.");
                return ("period:" + period.Id.ToString("D"), false);
            case "approve_budget_ceilings":
                var approved = periods.SingleOrDefault(x => x.BudgetAccountId == package.BudgetAccountId && x.StartUtc <= now && x.EndUtc > now);
                return (approved?.CeilingApprovalReceipt == package.ReceiptReference && approved.CashLimit == package.CashLimit && approved.ConsumptionLimit == package.ConsumptionLimit
                    ? "period:" + approved.Id.ToString("D") : null, false);
            case "set_budget_admission":
                var budget = accounts.Single(x => x.Id == package.BudgetAccountId);
                return (budget.AdmissionEnabled == package.Enabled ? "account:" + budget.Id.ToString("D") + ":" + budget.Revision.ToString("D") : null, true);
            case "set_owner_admission":
                return (connection.ReceptionistActiveConfigurationId == package.ConfigurationId && connection.ReceptionistAdmissionEnabled == package.Enabled
                    ? "connection:" + connection.Id.ToString("D") + ":" + connection.Revision.ToString("D") : null, true);
        }

        string scope; string component; string payload; Guid? accountChargeId = null;
        if (package.Operation == "reconcile_account_charge")
        {
            var receipt = package.AccountCharge!;
            scope = FundingReceiptScope(receipt.FundingBudgetAccountId); component = receipt.ComponentKey;
            payload = ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(receipt));
            var charge = await read.Set<ReceptionistAccountCharge>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.FundingBudgetAccountId == receipt.FundingBudgetAccountId && x.ProviderChargeKey == receipt.ProviderChargeKey, ct);
            if (charge is not null && (charge.OwnerBudgetAccountId != receipt.OwnerBudgetAccountId || charge.ComponentKey != component))
                throw new InvalidOperationException("The provider charge belongs to different accounting facts.");
            accountChargeId = charge?.Id;
        }
        else
        {
            var callId = call!.Id;
            switch (package.Operation)
            {
                case "reconcile_call_cost":
                    var componentKey = package.ComponentKey!; var observation = package.CallCost!;
                    var reservation = reservations.SingleOrDefault(x => x.CallId == callId && x.ComponentKey == componentKey)
                        ?? throw new InvalidOperationException("The reviewed call component is absent.");
                    scope = FundingReceiptScope(reservation.FundingBudgetAccountId); component = componentKey;
                    payload = ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new { callId, componentKey, observation }));
                    break;
                case "reconcile_dated_voice":
                    var ordered = package.VoiceAllocations!.OrderBy(x => x.ServicePeriodEvidenceUtc).ToArray(); var finalTotal = package.FinalVoiceSeconds;
                    scope = VoiceReceiptScope(call); component = "voice_duration_allocated";
                    payload = ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new { callId, allocations = ordered, finalTotal }));
                    break;
                case "record_provider_closure":
                    var finalCumulativeSeconds = package.FinalVoiceSeconds;
                    scope = VoiceReceiptScope(call); component = "provider_closure";
                    payload = ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new { kind = "provider_closure", callId, finalCumulativeSeconds }));
                    break;
                default: throw new ArgumentException("Unsupported receipt operation.");
            }
        }
        var stored = await read.Set<ReceptionistUsageReceipt>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.ScopeKey == scope && x.ProviderReceiptKey == package.ReceiptReference, ct);
        if (stored is null) return (null, false);
        if (stored.PayloadSha256 != payload || stored.ComponentKey != component || stored.CallId != call?.Id || stored.AccountChargeId != accountChargeId)
            throw new InvalidOperationException("The receipt identity already belongs to different scoped facts.");
        return ("receipt:" + stored.Id.ToString("D"), false);
    }

    private static IReadOnlyList<ReceptionistOperatorFact> OperatorFacts(ReceptionistOperatorPackage package,
        MarketingConnection connection, ReceptionistConfigurationVersion version,
        ReceptionistBusinessConfiguration configuration,
        IReadOnlyList<ReceptionistBudgetAccount> accounts, IReadOnlyList<ReceptionistBudgetPeriod> periods,
        IReadOnlyList<ReceptionistCostReservation> reservations, IReadOnlyList<ReceptionistAccountCharge> charges,
        ReceptionistCall? call, DateTime now, ReceptionistActionIntent? originalBookingAction)
    {
        static string Amount(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);
        static string Date(DateTime? value) => value?.ToString("O", CultureInfo.InvariantCulture) ?? "No dated amount";
        var facts = new List<ReceptionistOperatorFact>
        {
            new("Evidence", package.Operation == "reconcile_booking_action"
                ? "Applying this reviewed operation reads the original calendar appointment and canonical saved appointment. It cannot create, resend, cancel or reschedule."
                : "Operator-reviewed evidence; this page does not independently query the provider."),
            new("Owner", connection.OwnerKey), new("Provider project", package.ProviderProjectKey),
            new("Configuration", version.Id.ToString("D")), new("Configuration fingerprint", version.ConfigurationSha256),
            new("Current owner admission", connection.ReceptionistAdmissionEnabled ? "Enabled" : "Disabled")
        };
        if (package.BookingActionId is { } bookingAction)
        {
            facts.Add(new("Original booking action", bookingAction.ToString("D")));
            facts.Add(new("Booking outcome", originalBookingAction is not null &&
                Bookings.ReceptionistBookingAuthority.VerifiedAction(originalBookingAction, configuration)
                ? "Appointment independently verified"
                : originalBookingAction is not null && IsZeroEffectBookingRejection(originalBookingAction)
                    ? "No appointment created; rejected before provider attempt"
                    : "Unresolved; no new booking or invitation is authorized"));
            facts.Add(new("Recovery boundary", "A proven unstarted action may be rejected. Uncertain provider effects stay fenced until exact readback; recovery cannot create or resend."));
        }
        foreach (var account in accounts.OrderBy(x => x.Id))
        {
            facts.Add(new("Accounting dimension", account.Kind + " / " + account.Id.ToString("D") + " / " + account.Currency + " / " + account.TimeZoneId));
            var period = periods.SingleOrDefault(x => x.BudgetAccountId == account.Id && x.StartUtc <= now && x.EndUtc > now);
            if (period is not null)
            {
                facts.Add(new("Current cash committed / ceiling", Amount(period.CashCommitted) + " / " + Amount(period.CashLimit)));
                facts.Add(new("Current consumption committed / ceiling", Amount(period.ConsumptionCommitted) + " / " + Amount(period.ConsumptionLimit)));
                facts.Add(new("Current voice seconds", period.VoiceSecondsCommitted.ToString(CultureInfo.InvariantCulture)));
            }
            var held = reservations.Where(x => account.Kind == "owner" ? x.OwnerBudgetAccountId == account.Id : x.FundingBudgetAccountId == account.Id).ToArray();
            facts.Add(new("Unresolved cash / consumption reservations", Amount(held.Sum(CashRemaining)) + " / " + Amount(held.Sum(ConsumptionRemaining))));
            facts.Add(new("Unresolved non-call charges", charges.Count(x => account.Kind == "owner" ? x.OwnerBudgetAccountId == account.Id : x.FundingBudgetAccountId == account.Id).ToString(CultureInfo.InvariantCulture)));
        }
        if (package.Binding is { } binding)
        {
            facts.Add(new("Accepted backend", binding.BackendMode + " / " + binding.Model + " / " + binding.Transport + " / " + binding.Voice));
            facts.Add(new("Accepted ingress route", binding.IngressKey));
            facts.Add(new("After binding acceptance", "Inactive; a separate reviewed admission command is required."));
            facts.Add(new("Provider acceptance scope", binding.ControlledTestOnly ? "Controlled tests only" : "Reviewed public activation evidence"));
            facts.Add(new("Configured call duration maximum", configuration.MaximumCallSeconds.ToString(CultureInfo.InvariantCulture) + " seconds"));
            foreach (var bound in binding.Costs)
            {
                facts.Add(new("Cost component / shared funding", bound.Key + " / " + bound.FundingBudgetAccountId.ToString("D")));
                facts.Add(new("Cash bound: fixed / per second / per call", Amount(bound.CashFixedMaximum) + " / " + Amount(bound.CashPerSecondMaximum) + " / " +
                    Amount(ReceptionistConfigurationCodec.Maximum(bound.CashFixedMaximum, bound.CashPerSecondMaximum, configuration.MaximumCallSeconds))));
                facts.Add(new("Consumption bound: fixed / per second / per call", Amount(bound.ConsumptionFixedMaximum) + " / " + Amount(bound.ConsumptionPerSecondMaximum) + " / " +
                    Amount(ReceptionistConfigurationCodec.Maximum(bound.ConsumptionFixedMaximum, bound.ConsumptionPerSecondMaximum, configuration.MaximumCallSeconds))));
            }
        }
        if (package.Opening is { } opening)
        {
            facts.Add(new("Opening month and calendar", opening.Year.ToString(CultureInfo.InvariantCulture) + "-" + opening.Month.ToString("00", CultureInfo.InvariantCulture) + " / " + opening.TimeZoneId));
            facts.Add(new("Opening cash / consumption", Amount(opening.KnownCashCommitted) + " / " + Amount(opening.KnownConsumptionCommitted)));
            facts.Add(new("Opening cash / consumption ceilings", Amount(opening.CashLimit) + " / " + Amount(opening.ConsumptionLimit)));
            facts.Add(new("Opening voice seconds", opening.KnownVoiceSeconds.ToString(CultureInfo.InvariantCulture)));
            facts.Add(new("Opening reconciliation cutoff", Date(opening.ReconciledThroughUtc)));
        }
        if (package.CashLimit is { } cash) facts.Add(new("Approved cash ceiling", Amount(cash)));
        if (package.ConsumptionLimit is { } consumption) facts.Add(new("Approved consumption ceiling", Amount(consumption)));
        if (package.Enabled is { } enabled) facts.Add(new("Requested admission", enabled ? "Enabled" : "Disabled"));
        if (call is not null)
        {
            facts.Add(new("Call", call.Id.ToString("D"))); facts.Add(new("Observed closure", Date(call.ProviderClosedUtc)));
            facts.Add(new("Voice accounting", call.VoiceAllocationUncertain ? "Held for dated allocation" : call.VoiceUsageFinal ? "Final" : "Reservation retained"));
        }
        if (package.CallCost is { } cost)
        {
            facts.Add(new("Dated cash amount", Amount(cost.CumulativeCashForPeriod) + " at " + Date(cost.CashDebitedUtc)));
            facts.Add(new("Dated consumption amount", Amount(cost.CumulativeConsumptionForPeriod) + " at " + Date(cost.ConsumptionIncurredUtc)));
            facts.Add(new("Final cash / consumption totals", (cost.FinalCashTotal is { } fc ? Amount(fc) : "Unresolved") + " / " + (cost.FinalConsumptionTotal is { } fs ? Amount(fs) : "Unresolved")));
        }
        if (package.AccountCharge is { } charge)
        {
            facts.Add(new("Charge cash amount", Amount(charge.CumulativeCash) + " at " + Date(charge.CashDebitedUtc)));
            facts.Add(new("Charge consumption amount", Amount(charge.CumulativeConsumption) + " at " + Date(charge.ConsumptionIncurredUtc)));
            facts.Add(new("Charge finality", "Cash " + charge.CashFinal + "; consumption " + charge.ConsumptionFinal));
        }
        foreach (var allocation in package.VoiceAllocations ?? [])
            facts.Add(new("Dated voice allocation", allocation.CumulativeSecondsForPeriod.ToString(CultureInfo.InvariantCulture) + " seconds / " + Date(allocation.ServicePeriodEvidenceUtc)));
        if (package.ObservedProviderClosedUtc is { } closed) facts.Add(new("Reviewed provider closure", Date(closed)));
        if (package.FinalVoiceSeconds is { } seconds) facts.Add(new("Final voice seconds", seconds.ToString(CultureInfo.InvariantCulture)));
        return facts;
    }
}

internal sealed record ReceptionistOperatorState(MarketingOwnerScope Owner, MarketingConnection Connection,
    ReceptionistConfigurationVersion Version, ReceptionistBusinessConfiguration Configuration,
    ReceptionistProviderBinding? PinnedBinding, ReceptionistCall? Call, IReadOnlyList<ReceptionistBudgetAccount> Accounts,
    string StateSha256, string? CompletedReference, bool SatisfiedWithoutReceipt, IReadOnlyList<ReceptionistOperatorFact> Facts)
{
    internal Guid? BookingActionRevision { get; init; }
    internal Guid LatestConfigurationId { get; init; }
    internal void RequirePackageRevisions(ReceptionistOperatorPackage package)
    {
        if (Connection.Revision != package.ExpectedConnectionRevision ||
            Accounts.Count != package.ExpectedAccountRevisions.Count ||
            Accounts.Any(x => !package.ExpectedAccountRevisions.TryGetValue(x.Id, out var revision) || x.Revision != revision) ||
            Call is not null && Call.Revision != package.ExpectedCallRevision ||
            package.Operation == "reconcile_booking_action" && BookingActionRevision != package.ExpectedBookingActionRevision ||
            package.Operation == "accept_binding" && LatestConfigurationId != Version.Id ||
            package.Operation == "set_owner_admission" && Connection.ReceptionistActiveConfigurationId != Version.Id)
            throw new DbUpdateConcurrencyException("The reviewed canonical revisions changed. A fresh scoped review is required.");
    }
}
