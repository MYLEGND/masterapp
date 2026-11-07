using AgentPortal.Models;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

/// <summary>
/// Owns the lifecycle boundary between an active workstation lead and a
/// converted client. Conversion is archival: the original lead stays in place
/// for website attribution, campaign reporting, appointments, and audit
/// history, but no longer appears in active lead queues.
/// </summary>
public static class WorkstationLeadConversionLifecycle
{
    public const string ArchivedCrmStatus = "Archived";
    public const string ConvertedCrmStatus = "Converted";
    public const string ConvertedCrmStage = "PolicyPlaced";
    private const string LegacyConvertedCrmStatus = "Active";

    /// <summary>
    /// Applies the one authoritative active-lead predicate to database-backed
    /// CRM and live-queue queries.
    /// </summary>
    public static IQueryable<WorkstationLeadProfile> ActiveLeadQueue(
        this IQueryable<WorkstationLeadProfile> query)
        => query.Where(lead =>
            (lead.CrmStatus == null || (lead.CrmStatus != ConvertedCrmStatus &&
                lead.CrmStatus.Trim().ToLower() != "archived" && lead.CrmStatus.Trim().ToLower() != "deleted" && lead.CrmStatus.Trim().ToLower() != "closed")) &&
            (lead.CrmStage == null || (lead.CrmStage.Trim().ToLower() != "archived" && lead.CrmStage.Trim().ToLower() != "deleted" && lead.CrmStage.Trim().ToLower() != "closed")) &&
            !(lead.CrmStatus == LegacyConvertedCrmStatus && lead.CrmStage == ConvertedCrmStage));

    // Removal from the working CRM retains attribution, appointments and immutable history.
    public static void MarkArchived(WorkstationLeadProfile lead, DateTime archivedUtc)
    {
        lead.CrmStatus = ArchivedCrmStatus;
        lead.UpdatedUtc = archivedUtc;
    }

    public static async Task<int?> ArchiveAsync(Infrastructure.Data.MasterAppDbContext db, IEnumerable<WorkstationLeadProfile> leads, CancellationToken cancellationToken)
    {
        var ownedLeads = leads
            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.LeadId))
            .GroupBy(x => x.LeadId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.UpdatedUtc).First())
            .ToList();

        if (ownedLeads.Count == 0)
            return 0;

        var leadIds = ownedLeads
            .Select(x => x.LeadId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (leadIds.Length == 0)
            return 0;

        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken) : null;
        var owner = ownedLeads[0].AgentUserId;
        var current = await db.WorkstationLeadProfiles.AsNoTracking()
            .Where(x => leadIds.Contains(x.LeadId) && x.AgentUserId == owner).ToListAsync(cancellationToken);
        if (current.Count != ownedLeads.Count || current.Any(x =>
            IsConverted(x) || Infrastructure.Mobile.CrmArchiveScope.ArchivedStatus(x.CrmStatus) ||
            Infrastructure.Mobile.CrmArchiveScope.ArchivedStatus(x.CrmStage))) return null;

        var now = DateTime.UtcNow;
        var history = new Infrastructure.Crm.CrmHistoryStore(db);
        foreach (var lead in ownedLeads)
        {
            WorkstationLeadConversionLifecycle.MarkArchived(lead, now);
            await history.StageAsync(new CrmHistoryEntry
            {
                RelationshipKey = Infrastructure.Crm.CrmHistoryStore.LeadKey(lead.LeadId),
                RecordedBy = owner, Type = "Archived", Source = "LeadLifecycle",
                SourceId = Guid.NewGuid().ToString("N"), OccurredUtc = now,
                ContentJson = "{\"action\":\"removed_from_active_crm\",\"historyRetained\":true}"
            }, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return ownedLeads.Count;
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return null;
        }
    }

    public static bool IsConverted(WorkstationLeadProfile lead)
        => string.Equals(lead.CrmStatus, ConvertedCrmStatus, StringComparison.OrdinalIgnoreCase)
           || (string.Equals(lead.CrmStatus, LegacyConvertedCrmStatus, StringComparison.OrdinalIgnoreCase)
               && string.Equals(lead.CrmStage, ConvertedCrmStage, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads workstation CRM metadata without discarding pre-JSON CRM notes.
    /// Older website leads stored their note as plain text, so that text must
    /// be carried through conversion as the client CRM note.
    /// </summary>
    public static ClientCrmMeta ReadMetadata(WorkstationLeadProfile lead)
    {
        var meta = ClientCrmMetaSerializer.Deserialize(lead.CrmNotes);
        var rawNotes = (lead.CrmNotes ?? string.Empty).Trim();
        if (!rawNotes.StartsWith("{", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(rawNotes) &&
            string.IsNullOrWhiteSpace(meta.AgentNotes))
        {
            meta.AgentNotes = rawNotes;
        }

        return meta;
    }

    /// <summary>
    /// Archives a lead only after a ClientProfile has been staged in the same
    /// transaction. The source lead identifier remains immutable, while the
    /// reciprocal client profile identifier makes the conversion traceable.
    /// </summary>
    public static void MarkConverted(
        WorkstationLeadProfile lead,
        Guid clientProfileId,
        string recordType,
        DateTime convertedUtc)
    {
        if (clientProfileId == Guid.Empty)
            throw new ArgumentException("A converted client profile is required.", nameof(clientProfileId));

        var meta = ReadMetadata(lead);
        meta.ConvertedClientProfileId = clientProfileId;
        meta.ConvertedUtc = convertedUtc;
        meta.ConversionRecordType = string.IsNullOrWhiteSpace(recordType) ? "Client" : recordType.Trim();
        meta.Activities.Insert(0, new ClientCrmActivity
        {
            Type = "Conversion",
            Date = convertedUtc.ToString("yyyy-MM-dd"),
            Note = $"Converted to an active {meta.ConversionRecordType} record.",
            IsSystem = true,
            Channel = "System",
            CreatedUtc = convertedUtc
        });

        lead.CrmStatus = ConvertedCrmStatus;
        lead.CrmStage = ConvertedCrmStage;
        lead.CrmNotes = ClientCrmMetaSerializer.Serialize(meta);
        lead.UpdatedUtc = convertedUtc;
    }
}
