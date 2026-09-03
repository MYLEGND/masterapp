using System.Security.Claims;
using AgentPortal.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

/// <summary>
/// Smallest Founder-authorized read-only projection of the operational client
/// and lead records this deployment owns. It exists so a governed request for
/// current client/lead state is answered from the authenticated database
/// through the existing Founder authorization boundary instead of from
/// provider recollection or the public internet.
///
/// This service performs counts only. It never mutates, tracks, or returns
/// personally identifiable client or lead content.
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

        var clientProfileCount = await _db.ClientProfiles
            .AsNoTracking()
            .CountAsync(cancellationToken);

        var agentLinkedClientCount = await _db.AgentClients
            .AsNoTracking()
            .Select(link => link.ClientUserId)
            .Distinct()
            .CountAsync(cancellationToken);

        var workstationLeadCount = await _db.WorkstationLeadProfiles
            .AsNoTracking()
            .CountAsync(cancellationToken);

        var leadsByCrmStatus = await _db.WorkstationLeadProfiles
            .AsNoTracking()
            .GroupBy(lead => lead.CrmStatus)
            .Select(group => new FounderOperationalPortfolioStatusCount(
                group.Key,
                group.Count()))
            .ToListAsync(cancellationToken);

        var websiteLeadCount = await _db.WebsiteLeads
            .AsNoTracking()
            .CountAsync(cancellationToken);

        return new FounderOperationalPortfolioSnapshot(
            DateTime.UtcNow,
            clientProfileCount,
            agentLinkedClientCount,
            workstationLeadCount,
            leadsByCrmStatus
                .OrderByDescending(item => item.Count)
                .ThenBy(item => item.CrmStatus, StringComparer.Ordinal)
                .ToList(),
            websiteLeadCount,
            "ClientProfileCount counts every client profile record. " +
            "AgentLinkedClientCount counts distinct clients linked to an agent. " +
            "WorkstationLeadCount counts every workstation lead record and " +
            "WorkstationLeadsByCrmStatus reports the canonical CRM status of each. " +
            "WebsiteLeadCount counts captured website leads. " +
            "No lifecycle status beyond these stored values is inferred here.",
            "read_only_zero_write");
    }
}

public sealed record FounderOperationalPortfolioStatusCount(
    string CrmStatus,
    int Count);

public sealed record FounderOperationalPortfolioSnapshot(
    DateTime ObservedUtc,
    int ClientProfileCount,
    int AgentLinkedClientCount,
    int WorkstationLeadCount,
    IReadOnlyList<FounderOperationalPortfolioStatusCount> WorkstationLeadsByCrmStatus,
    int WebsiteLeadCount,
    string Definitions,
    string AccessClass);
