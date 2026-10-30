using Domain.Entities;
using Infrastructure.Crm;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Crm;

namespace AgentPortal.Services;

public sealed record CrmHistoryRelationship(string Key, WorkstationLeadProfile? Lead, ClientProfile? Client, string AgentId);

public sealed class CrmHistoryAccess(MasterAppDbContext db)
{
    public async Task<CrmHistoryRelationship?> ResolveAsync(string agentId, string kind, string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(id)) return null;
        WorkstationLeadProfile? lead = null;
        ClientProfile? client = null;
        if (kind == "lead")
        {
            lead = await db.WorkstationLeadProfiles.SingleOrDefaultAsync(x => x.LeadId == id && x.AgentUserId == agentId && x.CommerceBusinessId == null, ct);
            if (lead is null) return null;
            var meta = ClientCrmMetaSerializer.Deserialize(lead.CrmNotes);
            if (meta.ConvertedClientProfileId is Guid clientId)
            {
                client = await db.ClientProfiles.SingleOrDefaultAsync(x => x.Id == clientId, ct);
                if (client is null || ClientCrmMetaSerializer.Deserialize(client.CrmNotes).SourceWorkstationLeadId != lead.LeadId) return null;
                if (!await db.AgentClients.AnyAsync(x => x.AgentUserId == agentId && x.ClientUserId == client.ClientUserId, ct)) return null;
            }
        }
        else if (kind is "client" or "profile")
        {
            if (kind == "profile")
            {
                if (!Guid.TryParse(id, out var profileId)) return null;
                client = await db.ClientProfiles.SingleOrDefaultAsync(x => x.Id == profileId, ct);
            }
            else client = await db.ClientProfiles.SingleOrDefaultAsync(x => x.ClientUserId == id, ct);
            if (client is null || !await db.AgentClients.AnyAsync(x => x.AgentUserId == agentId && x.ClientUserId == client.ClientUserId, ct)) return null;
            var meta = ClientCrmMetaSerializer.Deserialize(client.CrmNotes);
            if (!string.IsNullOrWhiteSpace(meta.SourceWorkstationLeadId))
            {
                lead = await db.WorkstationLeadProfiles.SingleOrDefaultAsync(x => x.LeadId == meta.SourceWorkstationLeadId && x.AgentUserId == agentId && x.CommerceBusinessId == null, ct);
                if (lead is null || ClientCrmMetaSerializer.Deserialize(lead.CrmNotes).ConvertedClientProfileId != client.Id) return null;
            }
        }
        else return null;
        return new(lead is null ? CrmHistoryStore.ClientKey(client!.Id) : CrmHistoryStore.LeadKey(lead.LeadId), lead, client, agentId);
    }

    public IQueryable<WebsiteLeadIntakeLink> Submissions(CrmHistoryRelationship relationship)
    {
        var query = db.WebsiteLeadIntakeLinks.AsNoTracking();
        return relationship.Lead is { } lead
            ? query.Where(x => x.HistoryClientProfileId == null && x.WorkstationLeadId == lead.LeadId && x.CommerceBusinessId == lead.CommerceBusinessId)
            : query.Where(x => x.HistoryClientProfileId == relationship.Client!.Id && x.WorkstationLeadId == null && x.CommerceBusinessId == null);
    }

    public async Task BackfillAsync(CrmHistoryRelationship relationship, CancellationToken ct)
    {
        var store = new CrmHistoryStore(db);
        if (relationship.Lead is { } lead)
        {
            await store.BackfillMetadataAsync(relationship.Key, "WorkstationLead", lead.LeadId, lead.CrmNotes, ct);
        }
        if (relationship.Client is { } client)
            await store.BackfillMetadataAsync(relationship.Key, "ClientProfile", client.Id.ToString("N"), client.CrmNotes, ct);
        foreach (var link in await Submissions(relationship).ToListAsync(ct)) await store.StageSubmissionAsync(link, ct);
        await store.BackfillLegacySelfNotesAsync(relationship.Key, relationship.AgentId,
            new[] { relationship.Lead?.LeadId, relationship.Client?.ClientUserId, relationship.Client is null ? null : ClientCrmMetaSerializer.Deserialize(relationship.Client.CrmNotes).SourceLeadClientUserId }, ct);
        await db.SaveChangesAsync(ct);
    }
}
