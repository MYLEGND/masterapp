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
        var explicitRecordType = NormalizeRecordType(ReadMetadataValue(crmNotes, "recordType"));
        if (explicitRecordType is not null)
            return explicitRecordType;

        var pipelineStage = NormalizePipelineStage(ReadMetadataValue(crmNotes, "pipelineStage"));
        if (pipelineStage is not null)
            return pipelineStage;

        return Guid.TryParse(clientUserId?.Trim(), out _) ? Client : Lead;
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

    private static string? NormalizePipelineStage(string? value)
    {
        var normalized = Normalize(value);
        return normalized switch
        {
            "client" => Client,
            "businessclient" or "closedwon" or "placedbusiness" => BusinessClient,
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
