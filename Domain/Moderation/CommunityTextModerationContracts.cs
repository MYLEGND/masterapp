namespace Domain.Moderation;

public sealed record CommunityTextModerationResult(bool IsAllowed, string? Category, string? Severity, string? ReasonCode, bool RequiresReview)
{
    public static CommunityTextModerationResult Allowed() => new(true, null, null, null, false);
}

public interface ICommunityTextModerationService
{
    CommunityTextModerationResult Evaluate(string? content, string surface);
}
