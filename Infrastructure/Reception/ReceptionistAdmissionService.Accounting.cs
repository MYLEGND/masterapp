using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Reception;

public sealed partial class ReceptionistAdmissionService
{
    /// <summary>Explicit reconciled opening. It does not fund a provider or enable admission.</summary>
    internal Task<Guid> OpenBudgetPeriodAsync(ReceptionistBudgetOpening opening, CancellationToken ct = default) =>
        OpenBudgetPeriodCoreAsync(opening, null, ct);

    private async Task<Guid> OpenBudgetPeriodCoreAsync(ReceptionistBudgetOpening opening,
        Func<MasterAppDbContext, Task>? guard, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(opening);
        if (opening.Kind is not ("owner" or "funding")) throw new ArgumentException("Unsupported accounting dimension.");
        ReceptionistConfigurationCodec.Required(opening.ScopeKey, 200, nameof(opening.ScopeKey));
        ReceptionistConfigurationCodec.Currency(opening.Currency);
        ReceptionistConfigurationCodec.Required(opening.ReconciliationReceipt, 512, nameof(opening.ReconciliationReceipt));
        var month = ReceptionistConfigurationCodec.Month(opening.TimeZoneId, opening.Year, opening.Month);
        ReceptionistConfigurationCodec.Money(opening.CashLimit); ReceptionistConfigurationCodec.Money(opening.ConsumptionLimit);
        ReceptionistConfigurationCodec.Money(opening.KnownCashCommitted); ReceptionistConfigurationCodec.Money(opening.KnownConsumptionCommitted);
        if (opening.KnownVoiceSeconds < 0 || opening.ReconciledThroughUtc.Kind != DateTimeKind.Utc ||
            opening.ReconciledThroughUtc < month.Start || opening.ReconciledThroughUtc >= month.End ||
            opening.ReconciledThroughUtc > clock.GetUtcNow().UtcDateTime)
            throw new ArgumentException("A current, explicit UTC accounting reconciliation cutoff is required.");
        await using var read = new MasterAppDbContext(options);
        var existingId = await read.Set<ReceptionistBudgetAccount>().Where(x => x.Kind == opening.Kind && x.ScopeKey == opening.ScopeKey)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        return await ReceptionistSql.TransactionAsync(options, existingId is { } id ? [id] : [], async write =>
        {
            if (guard is not null) await guard(write);
            if (opening.Kind == "owner")
            {
                var connections = await write.MarketingConnections.AsNoTracking().Where(x => x.OwnerKey == opening.ScopeKey).ToListAsync(ct);
                if (connections.Count == 0 || connections.Any(x => ReceptionistSql.Owner(x).Key != opening.ScopeKey))
                    throw new InvalidOperationException("A canonical existing owner connection is required.");
            }
            var account = await write.Set<ReceptionistBudgetAccount>().SingleOrDefaultAsync(x => x.Kind == opening.Kind && x.ScopeKey == opening.ScopeKey, ct);
            if (account is null)
            {
                account = new ReceptionistBudgetAccount { Kind = opening.Kind, ScopeKey = opening.ScopeKey,
                    Currency = opening.Currency, TimeZoneId = opening.TimeZoneId };
                write.Add(account);
            }
            else if (account.Currency != opening.Currency || account.TimeZoneId != opening.TimeZoneId)
                throw new InvalidOperationException("Accounting currency and calendar cannot be rebound to reset an allowance.");
            var period = await write.Set<ReceptionistBudgetPeriod>().SingleOrDefaultAsync(x => x.BudgetAccountId == account.Id && x.MonthKey == month.Key, ct);
            if (period is not null)
            {
                if (period.OpeningReceipt != opening.ReconciliationReceipt || period.ReconciledThroughUtc != opening.ReconciledThroughUtc ||
                    period.OpeningCashCeiling != opening.CashLimit || period.OpeningConsumptionCeiling != opening.ConsumptionLimit ||
                    period.OpeningCashCommitted != opening.KnownCashCommitted || period.OpeningConsumptionCommitted != opening.KnownConsumptionCommitted ||
                    period.OpeningVoiceSeconds != opening.KnownVoiceSeconds)
                    throw new InvalidOperationException("An existing accounting opening cannot be overwritten or reset.");
                return account.Id;
            }
            write.Add(new ReceptionistBudgetPeriod
            {
                BudgetAccountId = account.Id, MonthKey = month.Key, StartUtc = month.Start, EndUtc = month.End,
                CashLimit = opening.CashLimit, ConsumptionLimit = opening.ConsumptionLimit,
                CashCommitted = opening.KnownCashCommitted, ConsumptionCommitted = opening.KnownConsumptionCommitted,
                VoiceSecondsCommitted = opening.KnownVoiceSeconds, OpeningReconciled = true,
                OpeningReceipt = opening.ReconciliationReceipt, ReconciledThroughUtc = opening.ReconciledThroughUtc,
                OpeningCashCommitted = opening.KnownCashCommitted, OpeningConsumptionCommitted = opening.KnownConsumptionCommitted,
                OpeningCashCeiling = opening.CashLimit, OpeningConsumptionCeiling = opening.ConsumptionLimit,
                OpeningVoiceSeconds = opening.KnownVoiceSeconds
            });
            if (opening.KnownCashCommitted > opening.CashLimit || opening.KnownConsumptionCommitted > opening.ConsumptionLimit)
            {
                account.AdmissionEnabled = false;
                account.ControlRevision = Guid.NewGuid();
                account.Revision = Guid.NewGuid();
            }
            return account.Id;
        }, ct);
    }

    internal async Task<ReceptionistBudgetAccount?> GetBudgetAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var read = new MasterAppDbContext(options);
        return await read.Set<ReceptionistBudgetAccount>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountId, ct);
    }

    internal Task SetBudgetAdmissionAsync(Guid accountId, Guid expectedRevision, bool enabled, CancellationToken ct = default) =>
        SetBudgetAdmissionCoreAsync(accountId, expectedRevision, enabled, null, ct);

    private async Task SetBudgetAdmissionCoreAsync(Guid accountId, Guid expectedRevision, bool enabled,
        Func<MasterAppDbContext, Task>? guard, CancellationToken ct)
    {
        await ReceptionistSql.TransactionAsync(options, [accountId], async write =>
        {
            if (guard is not null) await guard(write);
            var account = await write.Set<ReceptionistBudgetAccount>().SingleAsync(x => x.Id == accountId, ct);
            if (account.Revision != expectedRevision) throw new DbUpdateConcurrencyException("Accounting acceptance changed. Reload it first.");
            if (enabled)
            {
                var now = clock.GetUtcNow().UtcDateTime;
                var period = await write.Set<ReceptionistBudgetPeriod>().SingleOrDefaultAsync(x => x.BudgetAccountId == accountId && x.StartUtc <= now && x.EndUtc > now, ct);
                if (period?.OpeningReconciled != true) throw new InvalidOperationException("An accepted current accounting opening is required.");
                if (account.Kind == "owner" && await write.Set<ReceptionistCall>().AnyAsync(x => x.OwnerBudgetAccountId == accountId &&
                    (x.VoiceAllocationUncertain || (x.ProviderClosedUtc == null && write.Set<ReceptionistBudgetPeriod>().Any(p => p.Id == x.OwnerBudgetPeriodId && p.EndUtc <= now))), ct))
                    throw new InvalidOperationException("Unallocated monthly voice usage must be reconciled before admission can resume.");
                if (await write.Set<ReceptionistAccountCharge>().AnyAsync(x =>
                    (account.Kind == "owner" ? x.OwnerBudgetAccountId == accountId : x.FundingBudgetAccountId == accountId) && (!x.CashFinal || !x.ConsumptionFinal), ct))
                    throw new InvalidOperationException("Unresolved non-call cash and consumption must be reconciled before admission can resume.");
                var outstanding = await write.Set<ReceptionistCostReservation>().AsNoTracking().Where(x =>
                    (account.Kind == "owner" ? x.OwnerBudgetAccountId == accountId : x.FundingBudgetAccountId == accountId) && (!x.CashFinal || !x.ConsumptionFinal)).ToListAsync(ct);
                if (outstanding.Any(x => (!x.CashFinal && x.CashObserved > x.CashReserved) || (!x.ConsumptionFinal && x.ConsumptionObserved > x.ConsumptionReserved)))
                    throw new InvalidOperationException("An exceeded reservation requires verified final costs before admission can resume.");
                if (checked(period.CashCommitted + outstanding.Sum(CashRemaining)) > period.CashLimit ||
                    checked(period.ConsumptionCommitted + outstanding.Sum(ConsumptionRemaining)) > period.ConsumptionLimit)
                    throw new InvalidOperationException("Known charges and unresolved reservations exceed the approved accounting ceiling.");
            }
            if (account.AdmissionEnabled != enabled) account.ControlRevision = Guid.NewGuid();
            account.AdmissionEnabled = enabled; account.Revision = Guid.NewGuid();
            return true;
        }, ct);
    }

    /// <summary>Approved ceilings change without replacing opening facts, usage or unresolved reservations.</summary>
    internal Task ApproveBudgetCeilingsAsync(Guid accountId, Guid expectedRevision, decimal cashLimit,
        decimal consumptionLimit, string approvalReceipt, CancellationToken ct = default) =>
        ApproveBudgetCeilingsCoreAsync(accountId, expectedRevision, cashLimit, consumptionLimit, approvalReceipt, null, ct);

    private async Task ApproveBudgetCeilingsCoreAsync(Guid accountId, Guid expectedRevision, decimal cashLimit,
        decimal consumptionLimit, string approvalReceipt, Func<MasterAppDbContext, Task>? guard, CancellationToken ct)
    {
        ReceptionistConfigurationCodec.Money(cashLimit); ReceptionistConfigurationCodec.Money(consumptionLimit);
        ReceptionistConfigurationCodec.Required(approvalReceipt, 512, nameof(approvalReceipt));
        await ReceptionistSql.TransactionAsync(options, [accountId], async write =>
        {
            if (guard is not null) await guard(write);
            var account = await write.Set<ReceptionistBudgetAccount>().SingleAsync(x => x.Id == accountId, ct);
            if (account.Revision != expectedRevision) throw new DbUpdateConcurrencyException("Accounting approval changed. Reload before approving ceilings.");
            var now = clock.GetUtcNow().UtcDateTime;
            var period = await write.Set<ReceptionistBudgetPeriod>().SingleOrDefaultAsync(x => x.BudgetAccountId == accountId && x.StartUtc <= now && x.EndUtc > now, ct);
            if (period?.OpeningReconciled != true) throw new InvalidOperationException("A reconciled current accounting opening is required.");
            var outstanding = await write.Set<ReceptionistCostReservation>().AsNoTracking().Where(x =>
                (account.Kind == "owner" ? x.OwnerBudgetAccountId == accountId : x.FundingBudgetAccountId == accountId) && (!x.CashFinal || !x.ConsumptionFinal)).ToListAsync(ct);
            period.CashLimit = cashLimit; period.ConsumptionLimit = consumptionLimit;
            period.CeilingApprovalReceipt = approvalReceipt; period.Revision = Guid.NewGuid();
            account.Revision = Guid.NewGuid();
            if (checked(period.CashCommitted + outstanding.Sum(CashRemaining)) > cashLimit ||
                checked(period.ConsumptionCommitted + outstanding.Sum(ConsumptionRemaining)) > consumptionLimit)
            { account.AdmissionEnabled = false; account.ControlRevision = Guid.NewGuid(); }
            return true;
        }, ct);
    }

    /// <summary>Dated cumulative component postings. Cash follows its debit date, independently of service consumption.</summary>
    internal Task ReconcileCostAsync(Guid callId, string componentKey, string providerReceiptKey,
        ReceptionistDatedCostObservation observation, CancellationToken ct = default) =>
        ReconcileCostCoreAsync(callId, componentKey, providerReceiptKey, observation, null, ct);

    private async Task ReconcileCostCoreAsync(Guid callId, string componentKey, string providerReceiptKey,
        ReceptionistDatedCostObservation observation, Func<MasterAppDbContext, Task>? guard, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ReceptionistConfigurationCodec.Identifier(componentKey, 100, nameof(componentKey));
        ReceptionistConfigurationCodec.Required(providerReceiptKey, 200, nameof(providerReceiptKey));
        ValidateObservationDate(observation.CashDebitedUtc, observation.CumulativeCashForPeriod);
        ValidateObservationDate(observation.ConsumptionIncurredUtc, observation.CumulativeConsumptionForPeriod);
        if (observation.FinalCashTotal is { } finalCash) ReceptionistConfigurationCodec.Money(finalCash);
        if (observation.FinalConsumptionTotal is { } finalConsumption) ReceptionistConfigurationCodec.Money(finalConsumption);
        var payload = ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new { callId, componentKey, observation }));
        await WithCallAsync(callId, async (write, call) =>
        {
            if (guard is not null) await guard(write);
            var reservation = await write.Set<ReceptionistCostReservation>().SingleAsync(x => x.CallId == callId && x.ComponentKey == componentKey, ct);
            var scope = FundingReceiptScope(reservation.FundingBudgetAccountId);
            if (await ReceiptExistsAsync(write, scope, providerReceiptKey, payload, callId, null, ct)) return true;
            if ((observation.FinalCashTotal.HasValue || observation.FinalConsumptionTotal.HasValue) && call.ProviderClosedUtc is null)
                throw new InvalidOperationException("A final call-cost receipt requires verified provider closure.");
            var cashDelta = await ApplyDatedPostingAsync(write, reservation, "cash", observation.CashDebitedUtc,
                observation.CumulativeCashForPeriod, observation.FinalCashTotal.HasValue, ct);
            var consumptionDelta = await ApplyDatedPostingAsync(write, reservation, "consumption", observation.ConsumptionIncurredUtc,
                observation.CumulativeConsumptionForPeriod, observation.FinalConsumptionTotal.HasValue, ct);
            reservation.CashObserved = ReceptionistConfigurationCodec.Money(checked(reservation.CashObserved + cashDelta));
            reservation.ConsumptionObserved = ReceptionistConfigurationCodec.Money(checked(reservation.ConsumptionObserved + consumptionDelta));
            if ((observation.FinalCashTotal is { } cashTotal && cashTotal != reservation.CashObserved) ||
                (observation.FinalConsumptionTotal is { } consumptionTotal && consumptionTotal != reservation.ConsumptionObserved))
                throw new InvalidOperationException("A final total must equal all dated component postings before releasing its reservation.");
            var invalidatedMaximum = reservation.CashObserved > reservation.CashReserved || reservation.ConsumptionObserved > reservation.ConsumptionReserved ||
                (reservation.CashFinal && cashDelta > 0m) || (reservation.ConsumptionFinal && consumptionDelta > 0m);
            reservation.CashFinal = observation.FinalCashTotal.HasValue || (reservation.CashFinal && cashDelta == 0m);
            reservation.ConsumptionFinal = observation.FinalConsumptionTotal.HasValue || (reservation.ConsumptionFinal && consumptionDelta == 0m);
            reservation.Revision = Guid.NewGuid();
            write.Add(new ReceptionistUsageReceipt { CallId = callId, ScopeKey = scope, ComponentKey = componentKey,
                ProviderReceiptKey = providerReceiptKey, PayloadSha256 = payload, RecordedUtc = clock.GetUtcNow().UtcDateTime });
            if (invalidatedMaximum)
                await DisableAccountsAsync(write, [reservation.OwnerBudgetAccountId, reservation.FundingBudgetAccountId], ct);
            return true;
        }, ct);
    }

    private void ValidateObservationDate(DateTime? effectiveUtc, decimal amount)
    {
        ReceptionistConfigurationCodec.Money(amount);
        if ((amount > 0m && effectiveUtc is null) || effectiveUtc is { } date && (date.Kind != DateTimeKind.Utc || date > clock.GetUtcNow().UtcDateTime))
            throw new ArgumentException("Positive costs require their actual UTC cash debit or service-consumption date.");
    }

    private static async Task<decimal> ApplyDatedPostingAsync(MasterAppDbContext write, ReceptionistCostReservation reservation,
        string kind, DateTime? effectiveUtc, decimal cumulativeAmount, bool final, CancellationToken ct)
    {
        if (effectiveUtc is null) return 0m; // A verified zero total has no invented debit timestamp.
        var periods = await write.Set<ReceptionistBudgetPeriod>().Where(x =>
            (x.BudgetAccountId == reservation.OwnerBudgetAccountId || x.BudgetAccountId == reservation.FundingBudgetAccountId) &&
            x.StartUtc <= effectiveUtc.Value && x.EndUtc > effectiveUtc.Value).ToListAsync(ct);
        if (periods.Count != 2 || periods.Any(x => !x.OpeningReconciled))
            throw new InvalidOperationException("Both actual-date accounting periods require reconciled openings.");
        var ownerPeriod = periods.Single(x => x.BudgetAccountId == reservation.OwnerBudgetAccountId);
        var fundingPeriod = periods.Single(x => x.BudgetAccountId == reservation.FundingBudgetAccountId);
        var posting = await write.Set<ReceptionistCostPosting>().SingleOrDefaultAsync(x => x.CostReservationId == reservation.Id && x.Kind == kind &&
            x.OwnerBudgetPeriodId == ownerPeriod.Id && x.FundingBudgetPeriodId == fundingPeriod.Id, ct);
        if (posting is not null && final && cumulativeAmount < posting.CumulativeAmount)
            throw new InvalidOperationException("A stale lower period total cannot finalize or release a reservation.");
        var delta = Math.Max(0m, cumulativeAmount - (posting?.CumulativeAmount ?? 0m));
        if (delta > 0m && periods.Any(x => effectiveUtc.Value <= x.ReconciledThroughUtc))
            throw new InvalidOperationException("The receipt overlaps its opening cutoff; reconcile its inclusion before posting it again.");
        if (posting is null)
        {
            posting = new ReceptionistCostPosting
            {
                CostReservationId = reservation.Id, OwnerBudgetAccountId = reservation.OwnerBudgetAccountId,
                OwnerBudgetPeriodId = ownerPeriod.Id, FundingBudgetAccountId = reservation.FundingBudgetAccountId,
                FundingBudgetPeriodId = fundingPeriod.Id, Kind = kind, FirstEffectiveUtc = effectiveUtc.Value
            };
            write.Add(posting);
        }
        posting.CumulativeAmount = Math.Max(posting.CumulativeAmount, cumulativeAmount); posting.Revision = Guid.NewGuid();
        foreach (var period in periods)
        {
            if (kind == "cash") period.CashCommitted = ReceptionistConfigurationCodec.Money(checked(period.CashCommitted + delta));
            else period.ConsumptionCommitted = ReceptionistConfigurationCodec.Money(checked(period.ConsumptionCommitted + delta));
            period.Revision = Guid.NewGuid();
        }
        return delta;
    }

    /// <summary>
    /// Commits authenticated terminal evidence and its optional final duration in one transaction. The observation
    /// time is a conservative service upper bound; it cannot establish an allocation across monthly boundaries.
    /// </summary>
    internal Task<ReceptionistClosureReconciliation> RecordProviderClosureAndUsageAsync(Guid callId,
        string receipt, DateTime observedClosedUtc, long? finalCumulativeSeconds, CancellationToken ct = default) =>
        RecordProviderClosureAndUsageCoreAsync(callId, receipt, observedClosedUtc, finalCumulativeSeconds, null, ct);

    private async Task<ReceptionistClosureReconciliation> RecordProviderClosureAndUsageCoreAsync(Guid callId,
        string receipt, DateTime observedClosedUtc, long? finalCumulativeSeconds,
        Func<MasterAppDbContext, Task>? guard, CancellationToken ct)
    {
        ReceptionistConfigurationCodec.Required(receipt, 200, nameof(receipt));
        if (observedClosedUtc.Kind != DateTimeKind.Utc || finalCumulativeSeconds < 0)
            throw new ArgumentException("A UTC closure observation and nonnegative optional final duration are required.");
        // The first receipt's observation time is retained. Redelivery time is not a different provider payload.
        var payload = ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new { kind = "provider_closure", callId, finalCumulativeSeconds }));
        return await WithCallAsync(callId, async (write, call) =>
        {
            if (guard is not null) await guard(write);
            if (observedClosedUtc < call.ReservedUtc || observedClosedUtc > clock.GetUtcNow().UtcDateTime)
                throw new ArgumentException("The closure observation must fall between reservation and the current UTC time.");
            var scope = VoiceReceiptScope(call);
            if (await ReceiptExistsAsync(write, scope, receipt, payload, callId, null, ct))
                return new ReceptionistClosureReconciliation(callId, call.VoiceUsageFinal, call.VoiceAllocationUncertain);
            if (call.ProviderClosedUtc is not null && call.State != "closed")
                throw new InvalidOperationException("Provider terminal states conflict; reconcile before changing evidence.");
            if (finalCumulativeSeconds < call.ObservedVoiceSeconds)
                throw new InvalidOperationException("A stale lower final duration cannot replace observed usage or release reserved minutes.");
            if (call.ProviderClosedUtc is null)
            {
                call.State = "closed"; call.ProviderStateReceipt = receipt; call.ProviderClosedUtc = observedClosedUtc;
                call.Revision = Guid.NewGuid();
            }
            var period = await write.Set<ReceptionistBudgetPeriod>().SingleAsync(x =>
                x.Id == call.OwnerBudgetPeriodId && x.BudgetAccountId == call.OwnerBudgetAccountId, ct);
            var spansBoundary = call.ProviderClosedUtc > period.EndUtc;
            if (finalCumulativeSeconds is { } seconds)
            {
                if (spansBoundary)
                {
                    // Preserve a previously verified dated allocation only when this aggregate agrees exactly.
                    if (!call.VoiceUsageFinal || call.VoiceAllocationUncertain || seconds != call.ObservedVoiceSeconds)
                    {
                        call.ObservedVoiceSeconds = Math.Max(call.ObservedVoiceSeconds, seconds);
                        await HoldVoiceAllocationAsync(write, call, ct);
                    }
                }
                else
                    await ApplyVoicePostingsAsync(write, call, [new(call.ReservedUtc, seconds)], seconds, ct);
            }
            else if (spansBoundary && !call.VoiceUsageFinal)
                await HoldVoiceAllocationAsync(write, call, ct);
            write.Add(new ReceptionistUsageReceipt { CallId = callId, ScopeKey = scope, ComponentKey = "provider_closure",
                ProviderReceiptKey = receipt, PayloadSha256 = payload, RecordedUtc = clock.GetUtcNow().UtcDateTime });
            return new ReceptionistClosureReconciliation(callId, call.VoiceUsageFinal, call.VoiceAllocationUncertain);
        }, ct);
    }

    internal async Task ReconcileVoiceUsageAsync(Guid callId, string providerReceiptKey, long cumulativeSeconds,
        bool final, CancellationToken ct = default)
    {
        ReceptionistConfigurationCodec.Required(providerReceiptKey, 200, nameof(providerReceiptKey));
        if (cumulativeSeconds < 0) throw new ArgumentOutOfRangeException(nameof(cumulativeSeconds));
        var payload = ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new { callId, cumulativeSeconds, final }));
        var allocated = await WithCallAsync(callId, async (write, call) =>
        {
            var scope = VoiceReceiptScope(call);
            if (await ReceiptExistsAsync(write, scope, providerReceiptKey, payload, callId, null, ct)) return !call.VoiceAllocationUncertain;
            if (final && call.ProviderClosedUtc is null) throw new InvalidOperationException("Final voice usage requires verified provider closure.");
            if (final && cumulativeSeconds < call.ObservedVoiceSeconds) throw new InvalidOperationException("A stale lower duration cannot release reserved minutes.");
            var period = await write.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.Id == call.OwnerBudgetPeriodId && x.BudgetAccountId == call.OwnerBudgetAccountId, ct);
            var spansOrMaySpanBoundary = call.ProviderClosedUtc is { } closed ? closed > period.EndUtc : clock.GetUtcNow().UtcDateTime >= period.EndUtc;
            if (spansOrMaySpanBoundary)
            {
                call.ObservedVoiceSeconds = Math.Max(call.ObservedVoiceSeconds, cumulativeSeconds);
                await HoldVoiceAllocationAsync(write, call, ct);
                write.Add(new ReceptionistUsageReceipt { CallId = callId, ScopeKey = scope, ComponentKey = "voice_duration_unallocated",
                    ProviderReceiptKey = providerReceiptKey, PayloadSha256 = payload, RecordedUtc = clock.GetUtcNow().UtcDateTime });
                return false;
            }
            // The verified service interval is wholly in this period. This marker selects its calendar;
            // it does not assert that the provider accepted or began media exactly at reservation time.
            await ApplyVoicePostingsAsync(write, call, [new(call.ReservedUtc, cumulativeSeconds)], final ? cumulativeSeconds : null, ct);
            write.Add(new ReceptionistUsageReceipt { CallId = callId, ScopeKey = scope, ComponentKey = "voice_duration",
                ProviderReceiptKey = providerReceiptKey, PayloadSha256 = payload, RecordedUtc = clock.GetUtcNow().UtcDateTime });
            return true;
        }, ct);
        // The hold and unallocated receipt commit before the refusal is surfaced to a caller.
        if (!allocated) throw new InvalidOperationException("Monthly voice allocation is uncertain; admission is durably paused until verified dated usage is reconciled.");
    }

    internal Task ReconcileDatedVoiceUsageAsync(Guid callId, string providerReceiptKey,
        IReadOnlyList<ReceptionistDatedVoiceUsage> allocations, long? finalTotal = null, CancellationToken ct = default) =>
        ReconcileDatedVoiceUsageCoreAsync(callId, providerReceiptKey, allocations, finalTotal, null, ct);

    private async Task ReconcileDatedVoiceUsageCoreAsync(Guid callId, string providerReceiptKey,
        IReadOnlyList<ReceptionistDatedVoiceUsage> allocations, long? finalTotal,
        Func<MasterAppDbContext, Task>? guard, CancellationToken ct)
    {
        ReceptionistConfigurationCodec.Required(providerReceiptKey, 200, nameof(providerReceiptKey));
        if (allocations is null || allocations.Count is < 1 or > 256 || allocations.Any(x => x is null ||
            x.ServicePeriodEvidenceUtc.Kind != DateTimeKind.Utc || x.CumulativeSecondsForPeriod < 0 || x.ServicePeriodEvidenceUtc > clock.GetUtcNow().UtcDateTime) || finalTotal < 0)
            throw new ArgumentException("Explicit verified UTC service-period allocations and nonnegative seconds are required.");
        var ordered = allocations.OrderBy(x => x.ServicePeriodEvidenceUtc).ToArray();
        var payload = ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new { callId, allocations = ordered, finalTotal }));
        await WithCallAsync(callId, async (write, call) =>
        {
            if (guard is not null) await guard(write);
            var scope = VoiceReceiptScope(call);
            if (await ReceiptExistsAsync(write, scope, providerReceiptKey, payload, callId, null, ct)) return true;
            await ApplyVoicePostingsAsync(write, call, ordered, finalTotal, ct);
            write.Add(new ReceptionistUsageReceipt { CallId = callId, ScopeKey = scope, ComponentKey = "voice_duration_allocated",
                ProviderReceiptKey = providerReceiptKey, PayloadSha256 = payload, RecordedUtc = clock.GetUtcNow().UtcDateTime });
            return true;
        }, ct);
    }

    private async Task ApplyVoicePostingsAsync(MasterAppDbContext write, ReceptionistCall call,
        IReadOnlyList<ReceptionistDatedVoiceUsage> allocations, long? finalTotal, CancellationToken ct)
    {
        if (finalTotal.HasValue && call.ProviderClosedUtc is null)
            throw new InvalidOperationException("Final voice allocation requires verified provider closure.");
        var periods = await write.Set<ReceptionistBudgetPeriod>().Where(x => x.BudgetAccountId == call.OwnerBudgetAccountId).ToListAsync(ct);
        var postings = await write.Set<ReceptionistVoiceUsagePosting>().Where(x => x.CallId == call.Id).ToListAsync(ct);
        var includedPeriods = new HashSet<Guid>();
        var serviceUpperBound = call.ProviderClosedUtc ?? clock.GetUtcNow().UtcDateTime;
        foreach (var allocation in allocations)
        {
            if (allocation.ServicePeriodEvidenceUtc < call.ReservedUtc || allocation.ServicePeriodEvidenceUtc > serviceUpperBound)
                throw new InvalidOperationException("A duration allocation must belong to the actual call service interval.");
            var period = periods.SingleOrDefault(x => x.StartUtc <= allocation.ServicePeriodEvidenceUtc && x.EndUtc > allocation.ServicePeriodEvidenceUtc);
            if (period?.OpeningReconciled != true || !includedPeriods.Add(period.Id))
                throw new InvalidOperationException("Each reconciled owner period requires a single cumulative voice allocation.");
            var posting = postings.SingleOrDefault(x => x.OwnerBudgetPeriodId == period.Id);
            if (finalTotal.HasValue && posting is not null && allocation.CumulativeSecondsForPeriod < posting.CumulativeSeconds)
                throw new InvalidOperationException("A stale lower period duration cannot finalize reserved minutes.");
            var delta = Math.Max(0, allocation.CumulativeSecondsForPeriod - (posting?.CumulativeSeconds ?? 0));
            if (delta > 0 && allocation.ServicePeriodEvidenceUtc <= period.ReconciledThroughUtc)
                throw new InvalidOperationException("Voice allocation overlaps its reconciled opening; establish its inclusion before posting it again.");
            if (posting is null)
            {
                posting = new ReceptionistVoiceUsagePosting { CallId = call.Id, OwnerBudgetAccountId = call.OwnerBudgetAccountId,
                    OwnerBudgetPeriodId = period.Id, ServicePeriodEvidenceUtc = allocation.ServicePeriodEvidenceUtc };
                postings.Add(posting); write.Add(posting);
            }
            posting.CumulativeSeconds = Math.Max(posting.CumulativeSeconds, allocation.CumulativeSecondsForPeriod); posting.Revision = Guid.NewGuid();
            period.VoiceSecondsCommitted = checked(period.VoiceSecondsCommitted + delta); period.Revision = Guid.NewGuid();
        }
        var postedTotal = postings.Sum(x => x.CumulativeSeconds);
        var originalPeriod = periods.Single(x => x.Id == call.OwnerBudgetPeriodId);
        if (finalTotal is { } total)
        {
            var expectedPeriods = periods.Where(x => x.EndUtc > call.ReservedUtc && x.StartUtc < call.ProviderClosedUtc!.Value).OrderBy(x => x.StartUtc).ToArray();
            if (expectedPeriods.Length == 0) expectedPeriods = [originalPeriod];
            if (expectedPeriods[0].StartUtc > call.ReservedUtc || expectedPeriods[^1].EndUtc < call.ProviderClosedUtc!.Value ||
                expectedPeriods.Zip(expectedPeriods.Skip(1)).Any(x => x.First.EndUtc != x.Second.StartUtc) ||
                expectedPeriods.Any(x => !x.OpeningReconciled || postings.All(posting => posting.OwnerBudgetPeriodId != x.Id)) ||
                total != postedTotal || total < call.ObservedVoiceSeconds)
                throw new InvalidOperationException("Final duration requires explicit allocations for every service period, including verified zero periods, and the complete matched total.");
        }
        var invalidatedFinal = call.VoiceUsageFinal && postedTotal > call.ObservedVoiceSeconds;
        if (postedTotal > call.ReservedVoiceSeconds || invalidatedFinal)
            await DisableAccountsAsync(write, [call.OwnerBudgetAccountId], ct);
        call.ObservedVoiceSeconds = Math.Max(call.ObservedVoiceSeconds, postedTotal);
        if (finalTotal.HasValue)
        { call.VoiceUsageFinal = true; call.VoiceAllocationUncertain = false; }
        else if (postedTotal > call.ReservedVoiceSeconds || invalidatedFinal ||
            (call.ProviderClosedUtc is { } closed ? closed > originalPeriod.EndUtc : clock.GetUtcNow().UtcDateTime >= originalPeriod.EndUtc))
            await HoldVoiceAllocationAsync(write, call, ct);
        call.Revision = Guid.NewGuid();
    }

    private static async Task HoldVoiceAllocationAsync(MasterAppDbContext write, ReceptionistCall call, CancellationToken ct)
    {
        if (!call.VoiceAllocationUncertain) await DisableAccountsAsync(write, [call.OwnerBudgetAccountId], ct);
        call.VoiceAllocationUncertain = true; call.VoiceUsageFinal = false; call.Revision = Guid.NewGuid();
    }

    private static string VoiceReceiptScope(ReceptionistCall call) =>
        ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new { kind = "voice", call.Provider, call.ProviderProjectKey }));

    /// <summary>Records verified funding, rental, tax or other non-call charges once in both dimensions.</summary>
    internal Task ReconcileAccountChargeAsync(ReceptionistVerifiedAccountCharge receipt, CancellationToken ct = default) =>
        ReconcileAccountChargeCoreAsync(receipt, null, ct);

    private async Task ReconcileAccountChargeCoreAsync(ReceptionistVerifiedAccountCharge receipt,
        Func<MasterAppDbContext, Task>? guard, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.OwnerBudgetAccountId == receipt.FundingBudgetAccountId)
            throw new ArgumentException("Distinct accounting dimensions are required.");
        ReceptionistConfigurationCodec.Required(receipt.ProviderChargeKey, 200, nameof(receipt.ProviderChargeKey));
        ReceptionistConfigurationCodec.Identifier(receipt.ComponentKey, 100, nameof(receipt.ComponentKey));
        ReceptionistConfigurationCodec.Required(receipt.ProviderReceiptKey, 200, nameof(receipt.ProviderReceiptKey));
        ValidateObservationDate(receipt.CashDebitedUtc, receipt.CumulativeCash);
        ValidateObservationDate(receipt.ConsumptionIncurredUtc, receipt.CumulativeConsumption);
        if (receipt.CashDebitedUtc is null && receipt.ConsumptionIncurredUtc is null && !(receipt.CashFinal && receipt.ConsumptionFinal))
            throw new ArgumentException("An actual dated cash or service-consumption observation is required.");
        var scope = FundingReceiptScope(receipt.FundingBudgetAccountId);
        var payload = ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(receipt));
        await ReceptionistSql.TransactionAsync(options, [receipt.OwnerBudgetAccountId, receipt.FundingBudgetAccountId], async write =>
        {
            if (guard is not null) await guard(write);
            var accounts = await write.Set<ReceptionistBudgetAccount>().Where(x => x.Id == receipt.OwnerBudgetAccountId || x.Id == receipt.FundingBudgetAccountId).ToListAsync(ct);
            var owner = accounts.Single(x => x.Id == receipt.OwnerBudgetAccountId);
            var funding = accounts.Single(x => x.Id == receipt.FundingBudgetAccountId);
            if (owner.Kind != "owner" || funding.Kind != "funding" || owner.Currency != funding.Currency)
                throw new InvalidOperationException("Charge accounting dimensions do not match.");
            var charge = await write.Set<ReceptionistAccountCharge>().SingleOrDefaultAsync(x =>
                x.FundingBudgetAccountId == receipt.FundingBudgetAccountId && x.ProviderChargeKey == receipt.ProviderChargeKey, ct);
            if (charge is not null && (charge.OwnerBudgetAccountId != receipt.OwnerBudgetAccountId || charge.ComponentKey != receipt.ComponentKey))
                throw new InvalidOperationException("A provider charge cannot be allocated again under another owner or component.");
            if (await ReceiptExistsAsync(write, scope, receipt.ProviderReceiptKey, payload, null, charge?.Id, ct)) return true;
            if (charge is null)
            {
                charge = new ReceptionistAccountCharge
                {
                    OwnerBudgetAccountId = owner.Id, FundingBudgetAccountId = funding.Id,
                    ProviderChargeKey = receipt.ProviderChargeKey, ComponentKey = receipt.ComponentKey
                };
                write.Add(charge);
            }
            var cashDelta = Math.Max(0m, receipt.CumulativeCash - charge.CashObserved);
            var consumptionDelta = Math.Max(0m, receipt.CumulativeConsumption - charge.ConsumptionObserved);
            if ((receipt.CashFinal && receipt.CumulativeCash < charge.CashObserved) ||
                (receipt.ConsumptionFinal && receipt.CumulativeConsumption < charge.ConsumptionObserved))
                throw new InvalidOperationException("A stale lower account-charge total cannot finalize an unknown amount.");
            await ApplyAccountChargeDateAsync(write, charge, true, receipt.CashDebitedUtc, cashDelta, ct);
            await ApplyAccountChargeDateAsync(write, charge, false, receipt.ConsumptionIncurredUtc, consumptionDelta, ct);
            charge.CashObserved = Math.Max(charge.CashObserved, receipt.CumulativeCash);
            charge.ConsumptionObserved = Math.Max(charge.ConsumptionObserved, receipt.CumulativeConsumption);
            charge.CashFinal = receipt.CashFinal || (charge.CashFinal && cashDelta == 0m);
            charge.ConsumptionFinal = receipt.ConsumptionFinal || (charge.ConsumptionFinal && consumptionDelta == 0m);
            charge.Revision = Guid.NewGuid();
            write.Add(new ReceptionistUsageReceipt { AccountChargeId = charge.Id, ScopeKey = scope, ComponentKey = receipt.ComponentKey,
                ProviderReceiptKey = receipt.ProviderReceiptKey, PayloadSha256 = payload, RecordedUtc = clock.GetUtcNow().UtcDateTime });
            // Unreserved non-call charges can invalidate the approved opening/cost envelope. Reconciliation is explicit.
            await DisableAccountsAsync(write, [owner.Id, funding.Id], ct);
            return true;
        }, ct);
    }

    private static async Task ApplyAccountChargeDateAsync(MasterAppDbContext write, ReceptionistAccountCharge charge,
        bool cash, DateTime? effectiveUtc, decimal delta, CancellationToken ct)
    {
        if (effectiveUtc is null) return;
        var previousDate = cash ? charge.CashDebitedUtc : charge.ConsumptionIncurredUtc;
        if (previousDate is { } previous && previous != effectiveUtc.Value)
            throw new InvalidOperationException("An actual provider charge's cash or consumption date cannot be moved to another accounting period.");
        var periods = await write.Set<ReceptionistBudgetPeriod>().Where(x =>
            (x.BudgetAccountId == charge.OwnerBudgetAccountId || x.BudgetAccountId == charge.FundingBudgetAccountId) &&
            x.StartUtc <= effectiveUtc.Value && x.EndUtc > effectiveUtc.Value).ToListAsync(ct);
        if (periods.Count != 2 || periods.Any(x => !x.OpeningReconciled || (delta > 0m && effectiveUtc.Value <= x.ReconciledThroughUtc)))
            throw new InvalidOperationException("The actual charge date requires reconciled periods and must not be counted twice in an opening.");
        var ownerPeriod = periods.Single(x => x.BudgetAccountId == charge.OwnerBudgetAccountId);
        var fundingPeriod = periods.Single(x => x.BudgetAccountId == charge.FundingBudgetAccountId);
        if (cash)
        {
            charge.CashDebitedUtc = effectiveUtc; charge.CashOwnerBudgetPeriodId = ownerPeriod.Id; charge.CashFundingBudgetPeriodId = fundingPeriod.Id;
        }
        else
        {
            charge.ConsumptionIncurredUtc = effectiveUtc; charge.ConsumptionOwnerBudgetPeriodId = ownerPeriod.Id; charge.ConsumptionFundingBudgetPeriodId = fundingPeriod.Id;
        }
        await ApplyMoneyAsync(write, ownerPeriod.Id, fundingPeriod.Id, cash ? delta : 0m, cash ? 0m : delta, ct);
    }

    private static async Task<bool> ReceiptExistsAsync(MasterAppDbContext read, string scope, string receiptKey,
        string payload, Guid? callId, Guid? chargeId, CancellationToken ct)
    {
        var existing = await read.Set<ReceptionistUsageReceipt>().AsNoTracking().SingleOrDefaultAsync(
            x => x.ScopeKey == scope && x.ProviderReceiptKey == receiptKey, ct);
        if (existing is null) return false;
        if (existing.PayloadSha256 != payload || existing.CallId != callId || existing.AccountChargeId != chargeId)
            throw new InvalidOperationException("A provider accounting receipt cannot be changed, moved or posted twice.");
        return true;
    }

    private static async Task ApplyMoneyAsync(MasterAppDbContext write, Guid ownerPeriodId, Guid fundingPeriodId,
        decimal cashDelta, decimal consumptionDelta, CancellationToken ct)
    {
        if (ownerPeriodId == fundingPeriodId) throw new InvalidOperationException("Owner and funding periods must be distinct.");
        var periods = await write.Set<ReceptionistBudgetPeriod>().Where(x => x.Id == ownerPeriodId || x.Id == fundingPeriodId).ToListAsync(ct);
        if (periods.Count != 2) throw new InvalidOperationException("The original accounting periods are absent.");
        foreach (var period in periods)
        {
            period.CashCommitted = ReceptionistConfigurationCodec.Money(checked(period.CashCommitted + cashDelta));
            period.ConsumptionCommitted = ReceptionistConfigurationCodec.Money(checked(period.ConsumptionCommitted + consumptionDelta));
            period.Revision = Guid.NewGuid();
        }
    }

    private static async Task DisableAccountsAsync(MasterAppDbContext write, Guid[] ids, CancellationToken ct)
    {
        foreach (var account in await write.Set<ReceptionistBudgetAccount>().Where(x => ids.Contains(x.Id)).ToListAsync(ct))
        { account.AdmissionEnabled = false; account.ControlRevision = Guid.NewGuid(); account.Revision = Guid.NewGuid(); }
    }

    private static string FundingReceiptScope(Guid accountId) => "funding:" + accountId.ToString("N");
}
