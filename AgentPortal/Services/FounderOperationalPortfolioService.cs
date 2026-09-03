using System.Security.Claims;
using AgentPortal.Security;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Mobile;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

/// <summary>
/// Smallest Founder-authorized read-only projection of the operational client
/// and lead records this deployment owns. It exists so a governed request for
/// current client/lead state is answered from the authenticated database
/// through the existing Founder authorization boundary instead of from
/// provider recollection or the public internet.
///
/// It is not a second data authority: every count is produced by the existing
/// canonical visibility rules — <see cref="LegendMemberDirectory"/> for active
/// subscribed members, <see cref="ClientRecordClassification"/> for the
/// client/lead record type, <see cref="WorkstationLeadConversionLifecycle"/>
/// for the active lead queue, and the website-lead exclusion of internal and
/// deleted rows. This service performs counts only. It never mutates, tracks,
/// or returns personally identifiable client or lead content.
/// </summary>
public sealed class FounderOperationalPortfolioService
{
    private readonly MasterAppDbContext _db;

    public FounderOperationalPortfolioService(MasterAppDbContext db) =>
        _db = db;

    public async Task<FounderOperationalPortfolioSnapshot> GetPortfolioAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        FounderGuard.EnsureFounderOrThrow(user);

        var activeSubscribedProfiles = await LegendMemberDirectory
            .ActiveSubscribedProfiles(_db)
            .ToListAsync(cancellationToken);

        var activeClientCount = LegendMemberDirectory
            .Collapse(activeSubscribedProfiles)
            .Count;

        var agentLinkedRows = await (
                from link in _db.AgentClients.AsNoTracking()
                join profile in _db.ClientProfiles.AsNoTracking()
                    on link.ClientUserId equals profile.ClientUserId
                select new
                {
                    profile.ClientUserId,
                    profile.ExternalIdentityObjectId,
                    profile.CrmNotes,
                    profile.CrmStatus
                })
            .ToListAsync(cancellationToken);

        var agentLinkedClientCount = agentLinkedRows
            .Where(row => ClientRecordClassification.IsClientOrBusinessClient(
                row.ClientUserId,
                row.CrmNotes,
                row.CrmStatus))
            .Select(row => LegendMemberDirectory.CanonicalIdentityKey(
                row.ClientUserId,
                row.ExternalIdentityObjectId))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .Count();

        var activeLeads = _db.WorkstationLeadProfiles
            .AsNoTracking()
            .ActiveLeadQueue();

        var activeLeadCount = await activeLeads
            .CountAsync(cancellationToken);

        var leadsByCrmStatus = await activeLeads
            .GroupBy(lead => lead.CrmStatus)
            .Select(group => new FounderOperationalPortfolioStatusCount(
                group.Key,
                group.Count()))
            .ToListAsync(cancellationToken);

        var websiteLeadCount = await _db.WebsiteLeads
            .AsNoTracking()
            .Where(lead => !lead.IsInternal && !lead.IsDeleted)
            .CountAsync(cancellationToken);

        return new FounderOperationalPortfolioSnapshot(
            DateTime.UtcNow,
            activeClientCount,
            agentLinkedClientCount,
            activeLeadCount,
            leadsByCrmStatus
                .OrderByDescending(item => item.Count)
                .ThenBy(item => item.CrmStatus, StringComparer.Ordinal)
                .ToList(),
            websiteLeadCount,
            "ActiveClientCount applies LegendMemberDirectory.ActiveSubscribedProfiles " +
            "and Collapse (available CRM status, current client-app entitlement, " +
            "client record type, one row per canonical identity). " +
            "AgentLinkedClientCount counts distinct canonical client identities " +
            "linked through AgentClients whose record type is Client or " +
            "BusinessClient. ActiveLeadCount and ActiveLeadsByCrmStatus apply " +
            "WorkstationLeadConversionLifecycle.ActiveLeadQueue, so converted " +
            "leads are excluded. WebsiteLeadCount excludes internal and deleted " +
            "website leads. No other lifecycle meaning is inferred here.",
            "read_only_zero_write");
    }
}

public sealed record FounderOperationalPortfolioStatusCount(
    string CrmStatus,
    int Count);

public sealed record FounderOperationalPortfolioSnapshot(
    DateTime ObservedUtc,
    int ActiveClientCount,
    int AgentLinkedClientCount,
    int ActiveLeadCount,
    IReadOnlyList<FounderOperationalPortfolioStatusCount> ActiveLeadsByCrmStatus,
    int WebsiteLeadCount,
    string Definitions,
    string AccessClass);
