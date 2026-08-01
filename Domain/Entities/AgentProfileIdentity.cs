namespace Domain.Entities;

/// <summary>
/// The stable person-level key for agent directory surfaces. Entra object IDs can
/// change across tenant migrations; a normalized email remains the safe key for
/// collapsing historical aliases into one visible agent.
/// </summary>
public static class AgentProfileIdentity
{
    public static string DirectoryKey(
        string? normalizedEmail,
        string? agentUpn,
        string? agentUserId) =>
        FirstNonEmpty(
            Normalize(normalizedEmail),
            Normalize(agentUpn),
            Normalize(agentUserId));

    public static int DirectoryCompleteness(
        string? normalizedEmail,
        string? fullName,
        string? title,
        string? shortBio)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(normalizedEmail)) score += 2;
        if (!string.IsNullOrWhiteSpace(fullName)) score++;
        if (!string.IsNullOrWhiteSpace(title)) score++;
        if (!string.IsNullOrWhiteSpace(shortBio)) score++;
        return score;
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
