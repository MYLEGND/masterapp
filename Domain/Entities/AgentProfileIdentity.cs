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

    /// <summary>
    /// The public professional label for an agent. The synced web-profile title
    /// remains the source data; mobile and messaging surfaces receive this one
    /// consistent presentation rather than independently adding Legend labels.
    /// </summary>
    public static string LegendRoleLabel(string? jobTitle)
    {
        var title = jobTitle?.Trim();
        return string.IsNullOrWhiteSpace(title)
            ? "LEGEND"
            : $"{title} - LEGEND";
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}

/// <summary>
/// The only authority for Legend's platform-verified identities. Verification
/// is server-projected from the canonical agent directory; the mobile client
/// never grants a badge from a display name or an editable profile field.
/// </summary>
public static class LegendVerifiedIdentity
{
    public const string FounderEmail = "zac.owen@mylegnd.com";
    public const string LegendEmail = "connect@mylegnd.com";

    public static bool IsVerifiedAgentEmail(string? email) =>
        string.Equals(Normalize(email), FounderEmail, StringComparison.Ordinal) ||
        string.Equals(Normalize(email), LegendEmail, StringComparison.Ordinal);

    private static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() ?? string.Empty;
}
