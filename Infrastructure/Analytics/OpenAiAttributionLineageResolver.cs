using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;
using Shared.Crm;

namespace Infrastructure.Analytics;

/// <summary>
/// Resolves the original OpenAI click reference through canonical CRM lineage.
/// It never infers attribution from names, email, or unrelated sessions.
/// </summary>
public sealed class OpenAiAttributionLineageResolver(MasterAppDbContext db)
{
    public async Task<string?> ResolveForWebsiteLeadAsync(Guid? websiteLeadId, CancellationToken ct = default)
    {
        if (!websiteLeadId.HasValue || websiteLeadId == Guid.Empty) return null;
        var lead = await db.WebsiteLeads.AsNoTracking()
            .Where(x => x.LeadId == websiteLeadId.Value)
            .Select(x => x.Oppref)
            .FirstOrDefaultAsync(ct);
        if (OpenAiClickReference.Normalize(lead) is { } direct) return direct;

        var intake = await db.WebsiteLeadIntakeLinks.AsNoTracking()
            .Where(x => x.WebsiteLeadPublicId == websiteLeadId.Value)
            .OrderByDescending(x => x.SubmittedUtc)
            .ThenByDescending(x => x.CapturedUtc)
            .Select(x => x.Oppref)
            .FirstOrDefaultAsync(ct);
        return OpenAiClickReference.Normalize(intake);
    }

    public async Task<string?> ResolveForWorkstationLeadAsync(string? workstationLeadId, CancellationToken ct = default)
    {
        var key = NormalizeKey(workstationLeadId);
        if (key is null) return null;

        var intake = await db.WebsiteLeadIntakeLinks.AsNoTracking()
            .Where(x => x.WorkstationLeadId == key)
            .OrderByDescending(x => x.SubmittedUtc)
            .ThenByDescending(x => x.CapturedUtc)
            .Select(x => new { x.Oppref, x.WebsiteLeadPublicId })
            .FirstOrDefaultAsync(ct);
        if (intake is null) return null;
        return OpenAiClickReference.Normalize(intake.Oppref)
            ?? await ResolveForWebsiteLeadAsync(intake.WebsiteLeadPublicId, ct);
    }

    public async Task<string?> ResolveForClientAsync(string? clientUserId, CancellationToken ct = default)
    {
        var key = NormalizeKey(clientUserId);
        if (key is null) return null;

        var client = await db.ClientProfiles.AsNoTracking()
            .Where(x => x.ClientUserId == key)
            .Select(x => new { x.CrmNotes })
            .FirstOrDefaultAsync(ct);

        if (client is not null)
        {
            var meta = ClientCrmMetaSerializer.Deserialize(client.CrmNotes);
            var source = NormalizeKey(meta?.SourceWorkstationLeadId);
            if (source is not null && await ResolveForWorkstationLeadAsync(source, ct) is { } bySource)
                return bySource;
        }

        // Historical clients may retain the workstation lead id as ClientUserId.
        return await ResolveForWorkstationLeadAsync(key, ct);
    }

    public Task<string?> ResolveForProductionAsync(
        Domain.Enums.ProductionSide side,
        string? leadId,
        string? clientUserId,
        CancellationToken ct = default) =>
        side == Domain.Enums.ProductionSide.Lead
            ? ResolveForWorkstationLeadAsync(leadId, ct)
            : ResolveForClientAsync(clientUserId, ct);

    private static string? NormalizeKey(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
