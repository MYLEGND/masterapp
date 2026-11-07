using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Reception;

public sealed partial class ReceptionistAdmissionService
{
    // This permits a conservative historical hold, never a claim of provider closure or final usage.
    // Guaranteed-bound and expired policies retain the existing reconciliation requirement.
    // A public binding still needs its separately accepted public activation and exact risk evidence.
    private static bool RetiredVoiceCarryPolicyCurrent(MarketingConnection connection, Guid configurationId,
        ReceptionistProviderBinding? binding, DateTime now) =>
        connection.ReceptionistActiveConfigurationId == configurationId &&
        binding is { Transport: "webrtc", BudgetPolicy.Mode: ReceptionistOwnerBudgetPolicy.SupportedMode } &&
        connection.ReceptionistProviderProjectKey == binding.ProviderProjectKey &&
        ReceptionistConfigurationCodec.BudgetPolicyCurrent(binding, connection.OwnerKey, connection.Id, configurationId, now);

    private static async Task<Guid[]> RetiredVoiceCarryCallIdsAsync(MasterAppDbContext read,
        MarketingConnection connection, Guid configurationId, ReceptionistProviderBinding? binding,
        Guid ownerAccountId, DateTime now, CancellationToken ct)
    {
        if (!RetiredVoiceCarryPolicyCurrent(connection, configurationId, binding, now)) return [];
        var retirements = RetirementActions(read);
        var calls = await read.Set<ReceptionistCall>().AsNoTracking().Where(x =>
            x.OwnerBudgetAccountId == ownerAccountId && x.MarketingConnectionId == connection.Id &&
            x.Provider == connection.Provider && x.ProviderProjectKey == binding!.ProviderProjectKey &&
            x.ControlledTest && x.ProviderSessionId != null && !x.VoiceUsageFinal &&
            x.ReservedVoiceSeconds > 0 && x.ObservedVoiceSeconds >= 0 && x.ObservedVoiceSeconds <= x.ReservedVoiceSeconds &&
            x.MustStopUtc <= now && (x.SidebandLeaseExpiresUtc == null || x.SidebandLeaseExpiresUtc <= now || x.SidebandObservationOnly) &&
            (x.VoiceAllocationUncertain || read.Set<ReceptionistBudgetPeriod>().Any(p => p.Id == x.OwnerBudgetPeriodId && p.EndUtc <= now)) &&
            retirements.Any(a => a.CallId == x.Id) && !read.Set<ReceptionistActionIntent>().Any(a => a.CallId == x.Id &&
                (a.State == "pending" || a.State == "uncertain") && a.CapabilityKey != StopCapability && a.CapabilityKey != "__runtime_stop"))
            .Take(4097).ToArrayAsync(ct);
        if (calls.Length > 4096) throw new InvalidOperationException("The scoped voice holds exceed the supported review bound.");
        var versionIds = calls.Select(x => x.ConfigurationVersionId).Distinct().ToArray();
        var versions = await read.Set<ReceptionistConfigurationVersion>().AsNoTracking().Where(x =>
            x.MarketingConnectionId == connection.Id && versionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var eligible = new List<Guid>();
        foreach (var call in calls)
        {
            if (!versions.TryGetValue(call.ConfigurationVersionId, out var version)) continue;
            var originalConfiguration = ReceptionistConfigurationCodec.Read(version.ConfigurationJson, version.ConfigurationSha256);
            var originalBinding = ReceptionistConfigurationCodec.ReadBinding(version.AcceptedBindingJson, originalConfiguration);
            if (originalBinding?.ControlledTestOnly == true && originalBinding.ProviderProjectKey == call.ProviderProjectKey)
                eligible.Add(call.Id);
        }
        return eligible.ToArray();
    }

    private sealed record BudgetAdmissionCheck(IReadOnlyList<string> Blockers, Guid[] CarriedCallIds,
        decimal CashHeld, decimal ConsumptionHeld, long VoiceHeld, int UnresolvedNonCallCharges);

    // Shared by the Founder observation, exact preview and transactional apply. No writes or provider I/O.
    private static async Task<BudgetAdmissionCheck> CheckBudgetAdmissionAsync(MasterAppDbContext read,
        ReceptionistBudgetAccount account, DateTime now, CancellationToken ct,
        Guid? reviewedConnectionId = null, Guid? reviewedConfigurationId = null)
    {
        var blockers = new List<string>();
        var period = await read.Set<ReceptionistBudgetPeriod>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.BudgetAccountId == account.Id && x.StartUtc <= now && x.EndUtc > now, ct);
        if (period?.OpeningReconciled != true) blockers.Add("current_opening_unreconciled");
        Guid[] carried = [];
        long voiceHeld = 0;
        ReceptionistBusinessConfiguration? currentConfiguration = null;
        if (account.Kind == "owner")
        {
            // An unscoped internal caller cannot use the reviewed historical-hold exception.
            if (reviewedConnectionId is { } connectionId && reviewedConfigurationId is { } configurationId)
            {
                var connection = await read.MarketingConnections.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.Id == connectionId && x.OwnerKey == account.ScopeKey && x.Provider == "openai" &&
                    x.ReceptionistActiveConfigurationId == configurationId, ct);
                var version = connection is null ? null : await read.Set<ReceptionistConfigurationVersion>().AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == configurationId && x.MarketingConnectionId == connection.Id, ct);
                if (connection is not null && version is not null && ReceptionistSql.Owner(connection).Key == account.ScopeKey)
                {
                    currentConfiguration = ReceptionistConfigurationCodec.Read(version.ConfigurationJson, version.ConfigurationSha256);
                    var binding = ReceptionistConfigurationCodec.ReadBinding(version.AcceptedBindingJson, currentConfiguration);
                    carried = await RetiredVoiceCarryCallIdsAsync(read, connection, configurationId, binding, account.Id, now, ct);
                }
            }
            var voice = await read.Set<ReceptionistCall>().AsNoTracking().Where(x => x.OwnerBudgetAccountId == account.Id &&
                (!x.VoiceUsageFinal || x.VoiceAllocationUncertain || x.ProviderClosedUtc == null)).Take(4097).ToArrayAsync(ct);
            if (voice.Length > 4096) throw new InvalidOperationException("The scoped voice holds exceed the supported review bound.");
            var originalPeriodIds = voice.Select(x => x.OwnerBudgetPeriodId).Distinct().ToArray();
            var priorPeriods = await read.Set<ReceptionistBudgetPeriod>().AsNoTracking().Where(x =>
                originalPeriodIds.Contains(x.Id) && x.EndUtc <= now).Select(x => x.Id).ToArrayAsync(ct);
            if (voice.Any(x => (!x.VoiceUsageFinal || x.VoiceAllocationUncertain) && x.ObservedVoiceSeconds > x.ReservedVoiceSeconds))
                blockers.Add("voice_reservation_exceeded");
            if (voice.Any(x => (x.VoiceAllocationUncertain || x.ProviderClosedUtc == null && priorPeriods.Contains(x.OwnerBudgetPeriodId)) &&
                !carried.Contains(x.Id))) blockers.Add("voice_allocation_unreconciled");
            voiceHeld = checked(voice.Where(x => !x.VoiceUsageFinal || x.VoiceAllocationUncertain)
                .Sum(x => carried.Contains(x.Id) ? x.ReservedVoiceSeconds : Math.Max(0, x.ReservedVoiceSeconds - x.ObservedVoiceSeconds)));
            if (carried.Length > 0 && period is not null && currentConfiguration is not null &&
                checked(period.VoiceSecondsCommitted + voiceHeld) > currentConfiguration.MonthlyVoiceSecondsLimit)
                blockers.Add("owner_voice_minutes_limit");
        }
        var charges = await read.Set<ReceptionistAccountCharge>().CountAsync(x =>
            (account.Kind == "owner" ? x.OwnerBudgetAccountId == account.Id : x.FundingBudgetAccountId == account.Id) &&
            (!x.CashFinal || !x.ConsumptionFinal), ct);
        if (charges != 0) blockers.Add("non_call_charges_unreconciled");
        var outstanding = await read.Set<ReceptionistCostReservation>().AsNoTracking().Where(x =>
            (account.Kind == "owner" ? x.OwnerBudgetAccountId == account.Id : x.FundingBudgetAccountId == account.Id) &&
            (!x.CashFinal || !x.ConsumptionFinal)).Take(4097).ToArrayAsync(ct);
        if (outstanding.Length > 4096) throw new InvalidOperationException("The scoped cost holds exceed the supported review bound.");
        if (outstanding.Any(x => !x.CashFinal && x.CashObserved > x.CashReserved ||
            !x.ConsumptionFinal && x.ConsumptionObserved > x.ConsumptionReserved)) blockers.Add("cost_reservation_exceeded");
        var cashHeld = outstanding.Sum(CashRemaining);
        var consumptionHeld = outstanding.Sum(ConsumptionRemaining);
        if (period is not null && (checked(period.CashCommitted + cashHeld) > period.CashLimit ||
            checked(period.ConsumptionCommitted + consumptionHeld) > period.ConsumptionLimit)) blockers.Add("accounting_ceiling_exceeded");
        return new(blockers, carried, cashHeld, consumptionHeld, voiceHeld, charges);
    }

    private static string BudgetAdmissionMessage(string reason) => reason switch
    {
        "current_opening_unreconciled" => "An accepted current accounting opening is required.",
        "voice_allocation_unreconciled" => "Unallocated monthly voice usage requires reconciliation or an exact current reviewed budget risk policy with eligible retired test calls and retained holds.",
        "voice_reservation_exceeded" => "Observed voice usage exceeds its reservation and must be reconciled before admission can resume.",
        "owner_voice_minutes_limit" => "Known voice usage and retained historical voice holds exceed the approved monthly voice allowance.",
        "non_call_charges_unreconciled" => "Unresolved non-call cash and consumption must be reconciled before admission can resume.",
        "cost_reservation_exceeded" => "An exceeded reservation requires verified final costs before admission can resume.",
        "accounting_ceiling_exceeded" => "Known charges and unresolved reservations exceed the approved accounting ceiling.",
        _ => "Accounting readiness could not be established."
    };

    internal void ValidateBudgetAdmissionPreview(ReceptionistOperatorPackage package, ReceptionistOperatorState state)
    {
        if (package.Operation != "set_budget_admission" || package.Enabled != true) return;
        // Guard already holds the target accounting lock. Reuse the facts read by that
        // exact context; a separate connection could wait on our own transaction.
        var blockers = state.BudgetAdmissionBlockers
            ?? throw new InvalidOperationException("The exact accounting enablement conditions must be observed first.");
        if (blockers.Count > 0) throw new InvalidOperationException(BudgetAdmissionMessage(blockers[0]));
    }

    internal async Task<ReceptionistAccountingPreparation> ReadAccountingPreparationAsync(string ownerKey,
        Guid configurationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerKey) || ownerKey.Length > 160 || configurationId == Guid.Empty)
            throw new ArgumentException("An exact current owner and configuration are required.");
        await using var read = new MasterAppDbContext(options);
        await using var transaction = await read.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var version = await read.Set<ReceptionistConfigurationVersion>().AsNoTracking().SingleAsync(x => x.Id == configurationId, ct);
        var connection = await read.MarketingConnections.AsNoTracking().SingleAsync(x => x.Id == version.MarketingConnectionId &&
            x.OwnerKey == ownerKey && (x.ReceptionistActiveConfigurationId == configurationId ||
                !read.Set<ReceptionistConfigurationVersion>().Any(v => v.MarketingConnectionId == x.Id && v.Version > version.Version)), ct);
        if (ReceptionistSql.Owner(connection).Key != ownerKey || connection.Provider != "openai")
            throw new InvalidOperationException("The canonical accounting owner does not match.");
        var config = ReceptionistConfigurationCodec.Read(version.ConfigurationJson, version.ConfigurationSha256);
        var binding = ReceptionistConfigurationCodec.ReadBinding(version.AcceptedBindingJson, config);
        if (binding is not null && connection.ReceptionistProviderProjectKey != binding.ProviderProjectKey)
            throw new InvalidOperationException("The current provider project does not match its accepted binding.");
        var fundingIds = binding?.Costs.Select(x => x.FundingBudgetAccountId).Distinct().ToArray() ?? [];
        if (fundingIds.Length > 63) throw new InvalidOperationException("The accounting scope exceeds the supported bound.");
        var accounts = await read.Set<ReceptionistBudgetAccount>().AsNoTracking().Where(x =>
            x.Kind == "owner" && x.ScopeKey == ownerKey || x.Kind == "funding" && fundingIds.Contains(x.Id)).OrderBy(x => x.Id).ToArrayAsync(ct);
        if (accounts.Any(x => x.Currency != config.Currency || x.Kind == "owner" && x.TimeZoneId != config.TimeZoneId) ||
            fundingIds.Any(id => accounts.All(x => x.Id != id))) throw new InvalidOperationException("The current accounting scope is inconsistent.");
        var now = clock.GetUtcNow().UtcDateTime;
        var observations = new List<ReceptionistAccountingAccountPreparation>();
        foreach (var account in accounts)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.FindSystemTimeZoneById(account.TimeZoneId));
            var month = ReceptionistConfigurationCodec.Month(account.TimeZoneId, local.Year, local.Month);
            var period = await read.Set<ReceptionistBudgetPeriod>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.BudgetAccountId == account.Id && x.MonthKey == month.Key, ct);
            var prior = await read.Set<ReceptionistBudgetPeriod>().AsNoTracking().Where(x => x.BudgetAccountId == account.Id &&
                x.OpeningReconciled && x.EndUtc <= now).OrderByDescending(x => x.EndUtc).FirstOrDefaultAsync(ct);
            var approved = prior is null ? null : new ReceptionistAccountingCeilings(prior.Id, prior.Revision,
                account.Kind == "owner" ? Math.Min(prior.CashLimit, config.MonthlyCashLimit) : prior.CashLimit,
                account.Kind == "owner" ? Math.Min(prior.ConsumptionLimit, config.MonthlyConsumptionLimit) : prior.ConsumptionLimit);
            var check = await CheckBudgetAdmissionAsync(read, account, now, ct, connection.Id, version.Id);
            observations.Add(new(new(account.Id, account.Revision, account.Kind, account.ScopeKey, account.Currency, account.TimeZoneId),
                account.AdmissionEnabled, month.Start, month.End, period is null ? null : new(period.Id, period.Revision,
                    period.StartUtc, period.EndUtc, period.OpeningReconciled, period.ReconciledThroughUtc,
                    period.CashLimit, period.ConsumptionLimit, period.CashCommitted, period.ConsumptionCommitted, period.VoiceSecondsCommitted),
                approved, check.CashHeld, check.ConsumptionHeld, check.VoiceHeld, check.CarriedCallIds.Length,
                check.UnresolvedNonCallCharges, check.Blockers.Select(BudgetAdmissionMessage).ToArray()));
        }
        var readiness = new List<string>();
        if (!connection.ReceptionistAdmissionEnabled) readiness.Add("Owner call permission is paused.");
        if (connection.ReceptionistActiveConfigurationId != version.Id) readiness.Add("This selected configuration is not the operating configuration.");
        if (binding is null) readiness.Add("This configuration has no accepted voice connection.");
        if (binding is not null && !ReceptionistConfigurationCodec.BudgetPolicyCurrent(binding, ownerKey, connection.Id, version.Id, now))
            readiness.Add("The exact current budget risk policy has expired or changed.");
        if (binding is not null && !ReceptionistConfigurationCodec.HasRequiredIntake(config, binding))
            readiness.Add("The required inquiry route is not accepted.");
        if (!ReceptionistConfigurationCodec.IsOpen(config, now)) readiness.Add("The current time is outside the configured reception hours.");
        if (binding?.ControlledTestOnly == true && await ActiveAcceptanceWindowAsync(read, connection.Id, version.Id, binding, now, ct) is null)
            readiness.Add("No active restricted test window is verified for this configuration.");
        if (!accounts.Any(x => x.Kind == "owner")) readiness.Add("The existing owner accounting account is unavailable.");
        return new(now, "observed_not_reviewed", connection.Id, ownerKey, connection.Provider,
            connection.Revision, version.Id, version.Version, version.ConfigurationSha256, connection.ReceptionistProviderProjectKey,
            connection.ReceptionistAdmissionEnabled, binding is not null, binding?.Transport, binding?.BudgetPolicy?.Mode,
            binding?.BudgetPolicy?.ExpiresUtc, RetiredVoiceCarryPolicyCurrent(connection, version.Id, binding, now),
            config.MonthlyVoiceSecondsLimit, readiness, observations);
    }
}
