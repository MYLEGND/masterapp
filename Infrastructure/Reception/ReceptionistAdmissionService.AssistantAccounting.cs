using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Reception;

internal sealed record AssistantAccountingState(MarketingOwnerScope Owner, MarketingConnection Connection,
    ReceptionistCostReservation Reservation, AssistantReservationBinding Binding,
    IReadOnlyList<ReceptionistBudgetAccount> Accounts, string StateSha256, string? CompletedReference,
    IReadOnlyList<ReceptionistOperatorFact> Facts)
{
    internal void RequireRevisions(ReceptionistOperatorPackage package)
    {
        if (Connection.Revision != package.ExpectedConnectionRevision || Accounts.Count != package.ExpectedAccountRevisions.Count ||
            Accounts.Any(x => !package.ExpectedAccountRevisions.TryGetValue(x.Id, out var revision) || revision != x.Revision))
            throw new DbUpdateConcurrencyException("Assistant accounting state changed. Review the current state.");
    }
}

public sealed partial class ReceptionistAdmissionService
{
    internal async Task<AssistantAccountingState> ReadAssistantAccountingStateAsync(ReceptionistOperatorPackage package,
        string packageHash, CancellationToken ct)
    {
        await using var read = new MasterAppDbContext(options);
        return await ReadAssistantAccountingStateAsync(read, package, packageHash, ct);
    }

    internal async Task<AssistantAccountingState> ReadAssistantAccountingStateAsync(MasterAppDbContext read,
        ReceptionistOperatorPackage package, string packageHash, CancellationToken ct)
    {
        if (package.Operation != "reconcile_assistant_cost" || package.AssistantReservationId is not { } id || package.CallCost is null)
            throw new ArgumentException("One reviewed assistant settlement is required.");
        var reservation = await read.Set<ReceptionistCostReservation>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException("The assistant reservation is unavailable.");
        if (reservation.CallId is not null || reservation.AssistantOperationId is null || reservation.FeatureKey != "business-assistant" || reservation.ComponentKey != "assistant_response" ||
            reservation.AssistantBindingJson is null)
            throw new InvalidOperationException("An immutable assistant provider binding is unavailable; its hold cannot be released.");
        var binding = JsonSerializer.Deserialize<AssistantReservationBinding>(reservation.AssistantBindingJson, ReceptionistConfigurationCodec.Json)
            ?? throw new InvalidOperationException("Assistant binding is invalid.");
        var connection = await read.MarketingConnections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == binding.ConnectionId, ct)
            ?? throw new InvalidOperationException("The original assistant connection is unavailable.");
        var owner = ReceptionistSql.Owner(connection);
        if (owner.Key != binding.WorkspaceKey || package.ExpectedOwnerKey != owner.Key || package.MarketingConnectionId != connection.Id ||
            package.ConfigurationId != binding.ConfigurationRevision || package.ConfigurationSha256 != binding.ConfigurationSha256 ||
            binding.ConfigurationSha256 != ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(binding.Configuration)) ||
            package.Provider != binding.Configuration.Provider || package.ProviderProjectKey != binding.Configuration.ProjectId ||
            reservation.OwnerBudgetAccountId != binding.Configuration.OwnerBudgetAccountId || reservation.FundingBudgetAccountId != binding.Configuration.FundingBudgetAccountId)
            throw new InvalidOperationException("The cost receipt does not match the immutable tenant/provider/funding binding.");
        var accounts = await read.Set<ReceptionistBudgetAccount>().AsNoTracking().Where(x =>
            x.Id == reservation.OwnerBudgetAccountId || x.Id == reservation.FundingBudgetAccountId).OrderBy(x => x.Id).ToListAsync(ct);
        if (accounts.Count != 2 || accounts.Single(x => x.Id == reservation.OwnerBudgetAccountId).ScopeKey != owner.Key ||
            package.FundingScopes.Count != 1 || !package.FundingScopes.TryGetValue(reservation.FundingBudgetAccountId, out var fundingScope) ||
            accounts.Single(x => x.Id == reservation.FundingBudgetAccountId).ScopeKey != fundingScope)
            throw new InvalidOperationException("The cost receipt funding dimensions changed.");
        var payload = AssistantCostPayload(id, package.CallCost);
        var completed = await ReceiptExistsAsync(read, FundingReceiptScope(reservation.FundingBudgetAccountId), package.ReceiptReference,
            payload, null, null, ct, id);
        var accountIds = accounts.Select(x => x.Id).ToArray();
        var periods = await read.Set<ReceptionistBudgetPeriod>().AsNoTracking().Where(x => accountIds.Contains(x.BudgetAccountId))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Revision }).ToListAsync(ct);
        var hash = ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new { packageHash, connection.Revision,
            reservationId = id, reservationRevision = reservation.Revision, accounts = accounts.Select(x => new { x.Id, x.Revision }), periods }));
        return new(owner, connection, reservation, binding, accounts, hash, completed ? package.ReceiptReference : null,
            [new("Workspace", owner.Key), new("Provider project", binding.Configuration.ProjectId),
             new("Reservation", id.ToString("D")), new("Cash reserved", reservation.CashReserved.ToString(System.Globalization.CultureInfo.InvariantCulture)),
             new("Service reserved", reservation.ConsumptionReserved.ToString(System.Globalization.CultureInfo.InvariantCulture)),
             new("Currency", accounts.Single(x => x.Id == reservation.OwnerBudgetAccountId).Currency),
             new("Provider receipt", package.ReceiptReference),
             new("Cash debit date", package.CallCost.CashDebitedUtc?.ToString("O") ?? "not established"),
             new("Dated cumulative cash", package.CallCost.CumulativeCashForPeriod.ToString(System.Globalization.CultureInfo.InvariantCulture)),
             new("Service consumption date", package.CallCost.ConsumptionIncurredUtc?.ToString("O") ?? "not established"),
             new("Dated cumulative service", package.CallCost.CumulativeConsumptionForPeriod.ToString(System.Globalization.CultureInfo.InvariantCulture)),
             new("Final cash total", package.CallCost.FinalCashTotal?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown; retain unresolved cash hold"),
             new("Final service total", package.CallCost.FinalConsumptionTotal?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown; retain unresolved service hold"),
             new("Hold release", "Only the dimensions with exact verified final totals are released; unknown dimensions remain reserved."),
             new("Reviewed package SHA256", packageHash)]);
    }

    private static string AssistantCostPayload(Guid id, ReceptionistDatedCostObservation observation) =>
        ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new { reservationId = id, observation }));

    internal async Task ReconcileAssistantCostAsync(ReceptionistOperatorPackage package, AssistantAccountingState initial,
        Func<MasterAppDbContext, Task> guard, CancellationToken ct)
    {
        var observation = package.CallCost!;
        ValidateObservationDate(observation.CashDebitedUtc, observation.CumulativeCashForPeriod);
        ValidateObservationDate(observation.ConsumptionIncurredUtc, observation.CumulativeConsumptionForPeriod);
        if (observation.FinalCashTotal is { } cash) ReceptionistConfigurationCodec.Money(cash);
        if (observation.FinalConsumptionTotal is { } service) ReceptionistConfigurationCodec.Money(service);
        await ReceptionistSql.TransactionAsync(options, initial.Accounts.Select(x => x.Id).ToArray(), async write =>
        {
            await guard(write);
            var reservation = await write.Set<ReceptionistCostReservation>().SingleAsync(x => x.Id == package.AssistantReservationId, ct);
            var scope = FundingReceiptScope(reservation.FundingBudgetAccountId);
            var payload = AssistantCostPayload(reservation.Id, observation);
            if (await ReceiptExistsAsync(write, scope, package.ReceiptReference, payload, null, null, ct, reservation.Id)) return true;
            var cashDelta = await ApplyDatedPostingAsync(write, reservation, "cash", observation.CashDebitedUtc,
                observation.CumulativeCashForPeriod, observation.FinalCashTotal.HasValue, ct);
            var serviceDelta = await ApplyDatedPostingAsync(write, reservation, "consumption", observation.ConsumptionIncurredUtc,
                observation.CumulativeConsumptionForPeriod, observation.FinalConsumptionTotal.HasValue, ct);
            reservation.CashObserved = ReceptionistConfigurationCodec.Money(checked(reservation.CashObserved + cashDelta));
            reservation.ConsumptionObserved = ReceptionistConfigurationCodec.Money(checked(reservation.ConsumptionObserved + serviceDelta));
            if ((observation.FinalCashTotal is { } finalCash && finalCash != reservation.CashObserved) ||
                (observation.FinalConsumptionTotal is { } finalService && finalService != reservation.ConsumptionObserved))
                throw new InvalidOperationException("Final totals must equal all authoritative dated postings before releasing a hold.");
            var invalidated = reservation.CashObserved > reservation.CashReserved || reservation.ConsumptionObserved > reservation.ConsumptionReserved ||
                (reservation.CashFinal && cashDelta > 0m) || (reservation.ConsumptionFinal && serviceDelta > 0m);
            reservation.CashFinal = observation.FinalCashTotal.HasValue || (reservation.CashFinal && cashDelta == 0m);
            reservation.ConsumptionFinal = observation.FinalConsumptionTotal.HasValue || (reservation.ConsumptionFinal && serviceDelta == 0m);
            reservation.Revision = Guid.NewGuid();
            write.Add(new ReceptionistUsageReceipt { AssistantReservationId = reservation.Id, ScopeKey = scope,
                ComponentKey = reservation.ComponentKey, ProviderReceiptKey = package.ReceiptReference, PayloadSha256 = payload,
                RecordedUtc = clock.GetUtcNow().UtcDateTime });
            if (invalidated) await DisableAccountsAsync(write, initial.Accounts.Select(x => x.Id).ToArray(), ct);
            return true;
        }, ct);
    }
}
