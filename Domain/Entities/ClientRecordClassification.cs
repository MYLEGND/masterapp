using System.Text.Json;

namespace Domain.Entities;

/// <summary>
/// Classifies the canonical CRM metadata stored on a client profile without
/// depending on either portal's presentation-layer CRM model.
/// </summary>
public static class ClientRecordClassification
{
    public const string Client = "Client";
    public const string BusinessClient = "BusinessClient";
    public const string Lead = "Lead";

    public static bool IsClientOrBusinessClient(
        string? clientUserId,
        string? crmNotes,
        string? crmStatus = null) =>
        !IsLead(clientUserId, crmNotes, crmStatus);

    public static bool IsLead(
        string? clientUserId,
        string? crmNotes,
        string? crmStatus = null)
    {
        var resolved = Resolve(clientUserId, crmNotes);
        if (resolved is Client or BusinessClient)
            return false;

        var normalizedStatus = Normalize(crmStatus);
        return normalizedStatus is "lead" or "prospect" ||
            (string.IsNullOrWhiteSpace(normalizedStatus) && resolved == Lead);
    }

    public static string Resolve(string? clientUserId, string? crmNotes)
    {
        return Resolve(
            clientUserId,
            ReadMetadataValue(crmNotes, "recordType"),
            ReadMetadataValue(crmNotes, "pipelineStage"));
    }

    /// <summary>
    /// Resolves the CRM record type when callers have already parsed the
    /// canonical CRM metadata. This keeps every projection on the same
    /// record-type authority without asking presentation layers to recreate
    /// the fallback rules.
    /// </summary>
    public static string Resolve(
        string? clientUserId,
        string? recordType,
        string? pipelineStage)
    {
        var explicitRecordType = NormalizeRecordType(recordType);
        if (explicitRecordType is not null)
            return explicitRecordType;

        var typeFromPipeline = NormalizeRecordTypePipelineStage(pipelineStage);
        if (typeFromPipeline is not null)
            return typeFromPipeline;

        return Guid.TryParse(clientUserId?.Trim(), out _) ? Client : Lead;
    }

    /// <summary>
    /// Resolves the CRM pipeline bucket from persisted metadata. If older
    /// records lack that metadata, their canonical record type supplies the
    /// deterministic bucket instead of silently presenting a portal client as
    /// a new lead.
    /// </summary>
    public static string ResolvePipelineStage(string? clientUserId, string? crmNotes)
    {
        var recordType = ReadMetadataValue(crmNotes, "recordType");
        var pipelineStage = ReadMetadataValue(crmNotes, "pipelineStage");
        var normalizedPipelineStage = NormalizeCrmPipelineStage(pipelineStage);
        if (normalizedPipelineStage is not null)
            return normalizedPipelineStage;

        return Resolve(clientUserId, recordType, pipelineStage) switch
        {
            BusinessClient => BusinessClient,
            Client => Client,
            _ => "NewLead"
        };
    }

    private static string? NormalizeRecordType(string? value)
    {
        var normalized = Normalize(value);
        return normalized switch
        {
            "client" => Client,
            "businessclient" => BusinessClient,
            _ => null
        };
    }

    private static string? NormalizeRecordTypePipelineStage(string? value)
    {
        var normalized = Normalize(value);
        return normalized switch
        {
            "client" => Client,
            "businessclient" or "closedwon" or "placedbusiness" => BusinessClient,
            _ => null
        };
    }

    private static string? NormalizeCrmPipelineStage(string? value)
    {
        var normalized = Normalize(value);
        return normalized switch
        {
            "newlead" or "lead" => "NewLead",
            "opportunities" => "Opportunities",
            "contacted" => "Contacted",
            "qualified" => "Qualified",
            "client" => Client,
            "businessclient" or "closedwon" or "placedbusiness" => BusinessClient,
            "meetingscheduled" => "MeetingScheduled",
            "proposalsent" => "ProposalSent",
            "applicationstarted" => "ApplicationStarted",
            "submitted" => "Submitted",
            "closedlost" => "ClosedLost",
            "nurture" => "Nurture",
            _ => null
        };
    }

    private static string? ReadMetadataValue(string? crmNotes, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(crmNotes))
            return null;

        try
        {
            using var document = JsonDocument.Parse(crmNotes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals(propertyName) ||
                    property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : null;
                }
            }
        }
        catch (JsonException)
        {
            // Malformed CRM metadata is not a portal-client authorization signal.
        }

        return null;
    }

    private static string Normalize(string? value) => new string((value ?? string.Empty)
        .Trim()
        .Where(character => char.IsLetterOrDigit(character))
        .Select(char.ToLowerInvariant)
        .ToArray());
}
