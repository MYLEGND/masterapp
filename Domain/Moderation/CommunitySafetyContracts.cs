using Domain.Messaging;

namespace Domain.Moderation;

public static class CommunitySafetyTargetKinds
{
    public const string Profile = "Profile";
    public const string JourneyCircleProfile = "JourneyCircleProfile";
    public const string SocialPost = "SocialPost";
    public const string SocialComment = "SocialComment";
    public const string Message = "Message";

    public static string? Normalize(string? value) => value?.Trim() switch
    {
        Profile => Profile,
        JourneyCircleProfile => JourneyCircleProfile,
        SocialPost => SocialPost,
        SocialComment => SocialComment,
        Message => Message,
        _ => null
    };
}

public static class CommunitySafetyReviewResolutions
{
    public const string Dismissed = "Dismissed";
    public const string NeedsInvestigation = "NeedsInvestigation";
    public const string Actioned = "Actioned";

    public static string? Normalize(string? value) => value?.Trim() switch
    {
        Dismissed => Dismissed,
        NeedsInvestigation => NeedsInvestigation,
        Actioned => Actioned,
        _ => null
    };
}

public sealed record CommunitySafetyParticipant(
    string UserId,
    string ParticipantType,
    Guid ProfileId,
    IReadOnlyList<string> UserIdForms);

public sealed record CommunitySafetyBlockCommand(
    MessagingActor Actor,
    MessagingActor Target);

public sealed record CommunitySafetyReportCommand(
    MessagingActor Reporter,
    MessagingActor Target,
    string TargetKind,
    Guid? TargetEntityId,
    string Category,
    string? Detail);

public sealed record CommunitySafetyOperationResult(
    bool Succeeded,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static CommunitySafetyOperationResult Success() => new(true);
    public static CommunitySafetyOperationResult Failure(string code, string message) => new(false, code, message);
}

public sealed record CommunitySafetyReportView(
    Guid Id,
    string TargetKind,
    Guid? TargetEntityId,
    string Category,
    string? Detail,
    string Status,
    DateTime CreatedUtc,
    string ReporterParticipantType,
    string ReportedParticipantType,
    DateTime? ResolvedUtc,
    string? Resolution);

/// <summary>
/// One typed safety authority shared by profile, social, messaging, and
/// Journey Circles surfaces. It deliberately has no client-specific endpoint
/// or second persistence model.
/// </summary>
public interface ICommunitySafetyService
{
    Task<CommunitySafetyOperationResult> BlockAsync(CommunitySafetyBlockCommand command, CancellationToken cancellationToken = default);
    Task<CommunitySafetyOperationResult> ReportAsync(CommunitySafetyReportCommand command, CancellationToken cancellationToken = default);
    Task<bool> IsInteractionBlockedAsync(MessagingActor first, MessagingActor second, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommunitySafetyParticipant>> GetBlockedParticipantsAsync(MessagingActor actor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommunitySafetyReportView>> GetOpenReportsAsync(int take, CancellationToken cancellationToken = default);
    Task<CommunitySafetyReportView?> GetOpenReportAsync(Guid reportId, CancellationToken cancellationToken = default);
    Task<CommunitySafetyOperationResult> ResolveReportAsync(Guid reportId, string moderatorUserId, string resolution, CancellationToken cancellationToken = default);
}
