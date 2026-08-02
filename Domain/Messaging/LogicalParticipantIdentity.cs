namespace Domain.Messaging;

/// <summary>
/// Defines the persisted user-ID forms that represent one logical Legend
/// participant. Client records created before the Azure identity sync can
/// retain a legacy ClientUserId alongside their Azure object ID; both forms
/// must resolve to the same person for delivery and authorization.
/// </summary>
public static class LogicalParticipantIdentity
{
    public static string[] ClientUserIdForms(
        string? clientUserId,
        string? externalIdentityObjectId)
    {
        var forms = new HashSet<string>(StringComparer.Ordinal);
        AddNormalized(forms, clientUserId);
        AddNormalized(forms, externalIdentityObjectId);
        return forms.ToArray();
    }

    private static void AddNormalized(ISet<string> forms, string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalized))
            forms.Add(normalized);
    }
}
