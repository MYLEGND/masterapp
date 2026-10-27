using Domain.Entities;
using Infrastructure.Bookings;
using Infrastructure.Data;
using Infrastructure.Leads;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Reception;

public sealed partial class ReceptionistAdmissionService
{
    public async Task<IReadOnlyList<ReceptionistQaOutcomeStatus>> ReadQaOutcomesAsync(string ownerKey,
        Guid configurationId, Guid? ticketId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerKey) || ownerKey.Length > 160 || configurationId == Guid.Empty || ticketId == Guid.Empty)
            throw new ArgumentException("An exact owner and configuration are required.");
        await using var db = new MasterAppDbContext(options);
        var rows = await (from action in db.Set<ReceptionistActionIntent>().AsNoTracking()
            join call in db.Set<ReceptionistCall>().AsNoTracking() on action.CallId equals call.Id
            join connection in db.MarketingConnections.AsNoTracking() on call.MarketingConnectionId equals connection.Id
            where connection.OwnerKey == ownerKey && connection.Provider == "openai" && call.ConfigurationVersionId == configurationId &&
                call.ProviderSessionId != null && call.ControlledTest && (!ticketId.HasValue || action.Id == ticketId) &&
                (action.CapabilityKey == ReceptionistCapabilities.CanonicalIntake || action.CapabilityKey == ReceptionistCapabilities.CanonicalBooking)
            orderby action.UpdatedUtc descending, action.Id
            select new { Action = action, call.Provider, call.ProviderProjectKey, call.ProviderSessionId, call.ConfigurationVersionId })
            .Take(50).ToListAsync(ct);
        return rows.Select(row =>
        {
            var action = row.Action;
            var intake = action.CapabilityKey == ReceptionistCapabilities.CanonicalIntake;
            var scope = new ReceptionistQaScope(ownerKey, action.CallId, row.ConfigurationVersionId,
                row.Provider, row.ProviderProjectKey, row.ProviderSessionId!);
            var verified = intake ? ReceptionistInquiryAuthority.VerifiedQaOutcome(scope, action)
                : ReceptionistBookingAuthority.VerifiedQaAction(scope, action);
            return new ReceptionistQaOutcomeStatus(action.Id, action.CallId, intake ? "Synthetic intake" : "Synthetic booking",
                verified ? "Independently verified synthetic outcome" : "Unresolved synthetic outcome", action.UpdatedUtc);
        }).ToArray();
    }

    internal async Task<ReceptionistBookingRecoveryPreparation> ReadBookingRecoveryPreparationAsync(string expectedOwnerKey,
        Guid configurationId, Guid? onlyCallId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedOwnerKey) || expectedOwnerKey.Length > 160 || configurationId == Guid.Empty || onlyCallId == Guid.Empty)
            throw new ArgumentException("An exact owner and pinned configuration are required.");
        await using var db = new MasterAppDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var scope = await (from connection in db.MarketingConnections.AsNoTracking()
            join version in db.Set<ReceptionistConfigurationVersion>().AsNoTracking() on connection.Id equals version.MarketingConnectionId
            where connection.OwnerKey == expectedOwnerKey && connection.Provider == "openai" && version.Id == configurationId
            select new { ConnectionId = connection.Id, connection.Revision, version.ConfigurationSha256 }).SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The pinned configuration does not belong to this owner.");
        var rows = await (from action in db.Set<ReceptionistActionIntent>().AsNoTracking()
            join call in db.Set<ReceptionistCall>().AsNoTracking() on action.CallId equals call.Id
            where call.MarketingConnectionId == scope.ConnectionId && call.ConfigurationVersionId == configurationId &&
                !call.ControlledTest && (!onlyCallId.HasValue || call.Id == onlyCallId) &&
                action.CapabilityKey == ReceptionistCapabilities.CanonicalBooking && action.RouteKey == ReceptionistBookingAuthority.RouteKey
            orderby action.UpdatedUtc descending, action.Id
            select new { CallId = call.Id, CallRevision = call.Revision, call.OwnerBudgetAccountId,
                ActionId = action.Id, ActionRevision = action.Revision, action.State,
                HasProvider = action.ActualRecordReference != null && action.ActualRecordReference.StartsWith("booking-provider:v1:") })
            .Take(51).ToArrayAsync(ct);
        if (rows.Length > 50) throw new InvalidOperationException("Scope this preparation to one original call.");
        var callIds = rows.Select(x => x.CallId).Distinct().ToArray();
        var funding = await db.Set<ReceptionistCostReservation>().AsNoTracking().Where(x => callIds.Contains(x.CallId))
            .Select(x => x.FundingBudgetAccountId).Distinct().ToArrayAsync(ct);
        var accountIds = funding.Concat(rows.Select(x => x.OwnerBudgetAccountId)).Distinct().ToArray();
        var accounts = await db.Set<ReceptionistBudgetAccount>().AsNoTracking().Where(x => accountIds.Contains(x.Id))
            .OrderBy(x => x.Id).Select(x => new ReceptionistPreparationAccount(x.Id, x.Revision, x.Kind, x.ScopeKey, x.Currency, x.TimeZoneId)).ToArrayAsync(ct);
        return new(clock.GetUtcNow().UtcDateTime, "observed_not_reviewed", scope.ConnectionId, expectedOwnerKey, scope.Revision,
            configurationId, scope.ConfigurationSha256, rows.Select(x => new ReceptionistBookingRecoveryCandidate(x.CallId,
                x.CallRevision, x.ActionId, x.ActionRevision, x.State is "pending" or "uncertain" or "verified" or "rejected" ? x.State : "unknown", x.HasProvider)).ToArray(), accounts);
    }
}
