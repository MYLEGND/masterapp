using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Reception;

public sealed record AssistantFundingAdmission(bool Admitted, string Reason, Guid? ReservationId = null);

public sealed partial class ReceptionistAdmissionService
{
    // A reservation is the sole permission for one bounded provider attempt. A
    // repeated operation never grants a second attempt, even after an unknown outcome.
    public Task<AssistantFundingAdmission> ReserveAssistantAsync(MarketingOwnerScope owner,
        BusinessAssistantConnectionStatus snapshot, AssistantFundingEvidence evidence,
        Guid operationId, string fingerprint, Func<MasterAppDbContext, Task> authorize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        var config = snapshot.Configuration;
        if (config is null || owner.Key != snapshot.WorkspaceKey || owner.OwnerType is not ("business" or "agent") ||
            operationId == Guid.Empty || !ReceptionistOperatorService.HashValue(fingerprint))
            throw new ArgumentException("An exact authorized workspace, operation and configuration are required.");
        MarketingConnectionStore.ValidateAssistantConfiguration(config);
        return ReceptionistSql.TransactionAsync(options, [config.OwnerBudgetAccountId, config.FundingBudgetAccountId], async write =>
        {
            await authorize(write);
            var now = clock.GetUtcNow().UtcDateTime;
            AssistantFundingAdmission Deny(string reason) => new(false, reason);
            if (!evidence.Verified || evidence.ExpiresUtc <= now || config.CostCeilingExpiresUtc <= now ||
                evidence.ConnectionId != snapshot.ConnectionId || evidence.ControlRevision != snapshot.Revision)
                return Deny("cost_bound_unverified_or_expired");
            var connection = await write.MarketingConnections.SingleOrDefaultAsync(x => x.Id == snapshot.ConnectionId, ct);
            if (connection is null || ReceptionistSql.Owner(connection) != owner || connection.Provider != config.Provider ||
                connection.DisconnectedUtc is not null || connection.AssistantControlRevision != snapshot.Revision ||
                connection.Revision != evidence.ExpectedConnectionRevision ||
                MarketingConnectionStore.ReadAssistantConfiguration(connection.AssistantConfigurationJson) != config || !config.Enabled)
                return Deny("connection_changed_or_revoked");
            if (config.Provider == "openai" && string.IsNullOrWhiteSpace(connection.AssistantCredentialCiphertext))
                return Deny("credential_missing");
            var accounts = await write.Set<ReceptionistBudgetAccount>().Where(x =>
                x.Id == config.OwnerBudgetAccountId || x.Id == config.FundingBudgetAccountId).ToListAsync(ct);
            var ownerAccount = accounts.Single(x => x.Id == config.OwnerBudgetAccountId);
            var funding = accounts.Single(x => x.Id == config.FundingBudgetAccountId);
            if (ownerAccount.Kind != "owner" || ownerAccount.ScopeKey != owner.Key || funding.Kind != "funding" ||
                accounts.Any(x => !x.AdmissionEnabled || !evidence.ExpectedAccountRevisions.TryGetValue(x.Id, out var revision) || revision != x.Revision) ||
                !evidence.FundingScopes.TryGetValue(funding.Id, out var fundingScope) || fundingScope != funding.ScopeKey ||
                ownerAccount.Currency != funding.Currency)
                return Deny("funding_identity_or_revision_changed");
            // A provider project cannot evade its shared cap by selecting a second
            // funding account. Use existing connection records, not a second registry.
            var peers = await write.MarketingConnections.Where(x => x.Provider == config.Provider && x.AssistantConfigurationJson != null)
                .Select(x => x.AssistantConfigurationJson).ToListAsync(ct);
            foreach (var json in peers)
            {
                var peer = MarketingConnectionStore.ReadAssistantConfiguration(json)!;
                if (peer.ProjectId == config.ProjectId &&
                    peer.FundingBudgetAccountId != config.FundingBudgetAccountId)
                    return Deny("provider_project_funding_conflict");
            }
            var voiceVersions = await (from peer in write.MarketingConnections
                join version in write.Set<ReceptionistConfigurationVersion>() on peer.ReceptionistActiveConfigurationId equals version.Id
                where peer.Provider == config.Provider && peer.ReceptionistProviderProjectKey == config.ProjectId
                select version).ToListAsync(ct);
            foreach (var version in voiceVersions)
            {
                var voiceConfig = ReceptionistConfigurationCodec.Read(version.ConfigurationJson, version.ConfigurationSha256);
                var binding = ReceptionistConfigurationCodec.ReadBinding(version.AcceptedBindingJson, voiceConfig);
                // Existing cost components do not declare per-component provider
                // identities. Ambiguous multi-account voice funding fails closed.
                if (binding is null || binding.ProviderProjectKey != config.ProjectId ||
                    binding.Costs.Any(x => x.FundingBudgetAccountId != config.FundingBudgetAccountId))
                    return Deny("cross_feature_project_funding_unresolved");
            }
            var existing = await write.Set<ReceptionistCostReservation>().SingleOrDefaultAsync(x =>
                x.OwnerBudgetAccountId == ownerAccount.Id && x.AssistantOperationId == operationId && x.ComponentKey == "assistant_response", ct);
            if (existing is not null)
                return new(false, existing.OperationFingerprint == fingerprint ? "operation_already_reserved" : "operation_conflict", existing.Id);
            var ids = accounts.Select(x => x.Id).ToArray();
            var periods = await write.Set<ReceptionistBudgetPeriod>().Where(x => ids.Contains(x.BudgetAccountId) &&
                x.StartUtc <= now && x.EndUtc > now).ToListAsync(ct);
            if (periods.Count != 2 || periods.Any(x => !x.OpeningReconciled) || periods.Select(x => x.BudgetAccountId).Distinct().Count() != 2)
                return Deny("unreconciled_accounting_period");
            var outstanding = await write.Set<ReceptionistCostReservation>().Where(x =>
                (x.OwnerBudgetAccountId == ownerAccount.Id || x.FundingBudgetAccountId == funding.Id) && (!x.CashFinal || !x.ConsumptionFinal)).ToListAsync(ct);
            foreach (var period in periods)
            {
                var held = outstanding.Where(x => period.BudgetAccountId == ownerAccount.Id ?
                    x.OwnerBudgetAccountId == ownerAccount.Id : x.FundingBudgetAccountId == funding.Id).ToArray();
                if (checked(period.CashCommitted + held.Sum(CashRemaining) + config.MaximumCashPerTurn) > period.CashLimit)
                    return Deny("shared_cash_limit");
                if (checked(period.ConsumptionCommitted + held.Sum(ConsumptionRemaining) + config.MaximumServicePerTurn) > period.ConsumptionLimit)
                    return Deny("shared_service_limit");
            }
            var ownerPeriod = periods.Single(x => x.BudgetAccountId == ownerAccount.Id);
            var featurePostings = await (from posting in write.Set<ReceptionistCostPosting>()
                join featureReservation in write.Set<ReceptionistCostReservation>() on posting.CostReservationId equals featureReservation.Id
                where posting.OwnerBudgetPeriodId == ownerPeriod.Id && featureReservation.FeatureKey == "business-assistant"
                select new { posting.Kind, posting.CumulativeAmount }).ToListAsync(ct);
            var featureHeld = outstanding.Where(x => x.OwnerBudgetAccountId == ownerAccount.Id && x.FeatureKey == "business-assistant").ToArray();
            if (checked(ownerPeriod.OpeningCashCommitted + featurePostings.Where(x => x.Kind == "cash").Sum(x => x.CumulativeAmount) + featureHeld.Sum(CashRemaining) + config.MaximumCashPerTurn) > config.MonthlyCashAllocation ||
                checked(ownerPeriod.OpeningConsumptionCommitted + featurePostings.Where(x => x.Kind == "consumption").Sum(x => x.CumulativeAmount) + featureHeld.Sum(ConsumptionRemaining) + config.MaximumServicePerTurn) > config.MonthlyServiceAllocation)
                return Deny("feature_allocation_exhausted");
            var reservation = new ReceptionistCostReservation {
                AssistantOperationId = operationId, FeatureKey = "business-assistant", OperationFingerprint = fingerprint,
                OwnerBudgetAccountId = ownerAccount.Id, OwnerBudgetPeriodId = ownerPeriod.Id,
                FundingBudgetAccountId = funding.Id, FundingBudgetPeriodId = periods.Single(x => x.BudgetAccountId == funding.Id).Id,
                FundingControlRevision = funding.ControlRevision, ComponentKey = "assistant_response",
                CashReserved = config.MaximumCashPerTurn, ConsumptionReserved = config.MaximumServicePerTurn };
            write.Add(reservation);
            // No inferred zero charge, expiring credit or successful model response
            // releases this hold. Settlement requires authoritative dated receipts.
            return new AssistantFundingAdmission(true, "reserved_single_attempt", reservation.Id);
        }, ct);
    }
}
