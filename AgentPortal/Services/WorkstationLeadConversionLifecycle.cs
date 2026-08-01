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
            (lead.CrmStatus == null || lead.CrmStatus != ConvertedCrmStatus) &&
            !(lead.CrmStatus == LegacyConvertedCrmStatus && lead.CrmStage == ConvertedCrmStage));

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
