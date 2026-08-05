using Domain.Entities;
using Domain.Messaging;
using Domain.Moderation;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace Infrastructure.Moderation;

/// <summary>
/// Typed community-safety operations backed by the existing Journey block and
/// report records. User-facing blocks are enforced immediately; reports enter
/// the founder-controlled review queue without pretending that an automated
/// moderator made a decision.
/// </summary>
public sealed class CommunitySafetyService : ICommunitySafetyService
{
    private const int MaximumReportDetailLength = 600;
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    {
        "Harassment", "Hate", "SexualContent", "Violence", "Scam", "Impersonation", "SelfHarm", "Other"
    };

    private readonly MasterAppDbContext _db;

    public CommunitySafetyService(MasterAppDbContext db)
    {
        _db = db;
    }

    public async Task<CommunitySafetyOperationResult> BlockAsync(
        CommunitySafetyBlockCommand command,
        CancellationToken cancellationToken = default)
    {
        var actor = await ResolveParticipantAsync(command.Actor, cancellationToken);
        var target = await ResolveParticipantAsync(command.Target, cancellationToken);
        if (actor is null || target is null ||
            (actor.ParticipantType == target.ParticipantType && actor.ProfileId == target.ProfileId))
        {
            return CommunitySafetyOperationResult.Failure(
                "community_block_invalid",
                "This community block is not available.");
        }

        var existing = await IsInteractionBlockedAsync(command.Actor, command.Target, cancellationToken);
        if (existing)
            return CommunitySafetyOperationResult.Success();

        _db.JourneyCircleBlocks.Add(new JourneyCircleBlock
        {
            Id = Guid.NewGuid(),
            BlockerClientProfileId = actor.ParticipantType == MessagingParticipantTypes.Client ? actor.ProfileId : null,
            BlockedClientProfileId = target.ParticipantType == MessagingParticipantTypes.Client ? target.ProfileId : null,
            BlockerUserId = actor.UserId,
            BlockerParticipantType = actor.ParticipantType,
            BlockedUserId = target.UserId,
            BlockedParticipantType = target.ParticipantType,
            CreatedUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
        return CommunitySafetyOperationResult.Success();
    }

    public async Task<CommunitySafetyOperationResult> ReportAsync(
        CommunitySafetyReportCommand command,
        CancellationToken cancellationToken = default)
    {
        var reporter = await ResolveParticipantAsync(command.Reporter, cancellationToken);
        var target = await ResolveParticipantAsync(command.Target, cancellationToken);
        var targetKind = CommunitySafetyTargetKinds.Normalize(command.TargetKind);
        var category = NormalizeCategory(command.Category);
        if (reporter is null || target is null || targetKind is null || category is null ||
            (reporter.ParticipantType == target.ParticipantType && reporter.ProfileId == target.ProfileId) ||
            !await IsValidTargetAsync(target, targetKind, command.TargetEntityId, cancellationToken))
        {
            return CommunitySafetyOperationResult.Failure(
                "community_report_invalid",
                "This community report is not available.");
        }

        _db.JourneyCircleReports.Add(new JourneyCircleReport
        {
            Id = Guid.NewGuid(),
            ReporterClientProfileId = reporter.ParticipantType == MessagingParticipantTypes.Client ? reporter.ProfileId : null,
            ReportedClientProfileId = target.ParticipantType == MessagingParticipantTypes.Client ? target.ProfileId : null,
            ReporterUserId = reporter.UserId,
            ReporterParticipantType = reporter.ParticipantType,
            ReportedUserId = target.UserId,
            ReportedParticipantType = target.ParticipantType,
            TargetKind = targetKind,
            TargetEntityId = command.TargetEntityId,
            Category = category,
            Detail = Limit(command.Detail, MaximumReportDetailLength),
            Status = "Open",
            CreatedUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
        return CommunitySafetyOperationResult.Success();
    }

    public async Task<bool> IsInteractionBlockedAsync(
        MessagingActor first,
        MessagingActor second,
        CancellationToken cancellationToken = default)
    {
        var firstParticipant = await ResolveParticipantAsync(first, cancellationToken);
        var secondParticipant = await ResolveParticipantAsync(second, cancellationToken);
        if (firstParticipant is null || secondParticipant is null)
            return true;

        var genericBlocked = await _db.JourneyCircleBlocks.AsNoTracking().AnyAsync(block =>
            ((block.BlockerUserId != null && firstParticipant.UserIdForms.Contains(block.BlockerUserId.ToLower()) &&
              block.BlockerParticipantType == firstParticipant.ParticipantType &&
              block.BlockedUserId != null && secondParticipant.UserIdForms.Contains(block.BlockedUserId.ToLower()) &&
              block.BlockedParticipantType == secondParticipant.ParticipantType) ||
             (block.BlockerUserId != null && secondParticipant.UserIdForms.Contains(block.BlockerUserId.ToLower()) &&
              block.BlockerParticipantType == secondParticipant.ParticipantType &&
              block.BlockedUserId != null && firstParticipant.UserIdForms.Contains(block.BlockedUserId.ToLower()) &&
              block.BlockedParticipantType == firstParticipant.ParticipantType)),
            cancellationToken);
        if (genericBlocked)
            return true;

        if (firstParticipant.ParticipantType != MessagingParticipantTypes.Client ||
            secondParticipant.ParticipantType != MessagingParticipantTypes.Client)
        {
            return false;
        }

        return await _db.JourneyCircleBlocks.AsNoTracking().AnyAsync(block =>
            (block.BlockerClientProfileId == firstParticipant.ProfileId && block.BlockedClientProfileId == secondParticipant.ProfileId) ||
            (block.BlockerClientProfileId == secondParticipant.ProfileId && block.BlockedClientProfileId == firstParticipant.ProfileId),
            cancellationToken);
    }

    public async Task<IReadOnlyList<CommunitySafetyParticipant>> GetBlockedParticipantsAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default)
    {
        var participant = await ResolveParticipantAsync(actor, cancellationToken);
        if (participant is null)
            return Array.Empty<CommunitySafetyParticipant>();

        var genericBlocks = await _db.JourneyCircleBlocks.AsNoTracking()
            .Where(block =>
                (block.BlockerUserId != null &&
                 participant.UserIdForms.Contains(block.BlockerUserId.ToLower()) &&
                 block.BlockerParticipantType == participant.ParticipantType &&
                 block.BlockedUserId != null && block.BlockedParticipantType != null) ||
                (block.BlockedUserId != null &&
                 participant.UserIdForms.Contains(block.BlockedUserId.ToLower()) &&
                 block.BlockedParticipantType == participant.ParticipantType &&
                 block.BlockerUserId != null && block.BlockerParticipantType != null))
            .Select(block => new
            {
                block.BlockerUserId,
                block.BlockerParticipantType,
                block.BlockedUserId,
                block.BlockedParticipantType
            })
            .ToArrayAsync(cancellationToken);
        var result = new Dictionary<(string UserId, string ParticipantType), CommunitySafetyParticipant>();
        foreach (var block in genericBlocks)
        {
            var actorIsBlocker = block.BlockerUserId is not null &&
                                 participant.UserIdForms.Contains(block.BlockerUserId.ToLower()) &&
                                 block.BlockerParticipantType == participant.ParticipantType;
            var target = actorIsBlocker
                ? new MessagingActor(block.BlockedUserId!, block.BlockedParticipantType!)
                : new MessagingActor(block.BlockerUserId!, block.BlockerParticipantType!);
            var resolved = await ResolveParticipantAsync(target, cancellationToken);
            if (resolved is not null)
                result[(resolved.UserId, resolved.ParticipantType)] = resolved;
        }

        if (participant.ParticipantType == MessagingParticipantTypes.Client)
        {
            var legacyTargetIds = await _db.JourneyCircleBlocks.AsNoTracking()
                .Where(block =>
                    (block.BlockerClientProfileId == participant.ProfileId && block.BlockedClientProfileId != null) ||
                    (block.BlockedClientProfileId == participant.ProfileId && block.BlockerClientProfileId != null))
                .Select(block => block.BlockerClientProfileId == participant.ProfileId
                    ? block.BlockedClientProfileId!.Value
                    : block.BlockerClientProfileId!.Value)
                .ToArrayAsync(cancellationToken);
            foreach (var targetId in legacyTargetIds)
            {
                var target = await ResolveClientByProfileIdAsync(targetId, cancellationToken);
                if (target is not null)
                    result[(target.UserId, target.ParticipantType)] = target;
            }
        }

        return result.Values.ToArray();
    }

    public async Task<IReadOnlyList<CommunitySafetyReportView>> GetOpenReportsAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        return await _db.JourneyCircleReports.AsNoTracking()
            .Where(report => report.Status == "Open")
            .OrderBy(report => report.CreatedUtc)
            .Take(Math.Clamp(take, 1, 200))
            .Select(report => new CommunitySafetyReportView(
                report.Id,
                report.TargetKind ?? CommunitySafetyTargetKinds.JourneyCircleProfile,
                report.TargetEntityId,
                report.Category,
                report.Detail,
                report.Status,
                report.CreatedUtc,
                report.ReporterParticipantType ?? MessagingParticipantTypes.Client,
                report.ReportedParticipantType ?? MessagingParticipantTypes.Client,
                report.ResolvedUtc,
                report.Resolution))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<CommunitySafetyReportView?> GetOpenReportAsync(
        Guid reportId,
        CancellationToken cancellationToken = default)
    {
        if (reportId == Guid.Empty)
            return null;

        return await _db.JourneyCircleReports.AsNoTracking()
            .Where(report => report.Id == reportId && report.Status == "Open")
            .Select(report => new CommunitySafetyReportView(
                report.Id,
                report.TargetKind ?? CommunitySafetyTargetKinds.JourneyCircleProfile,
                report.TargetEntityId,
                report.Category,
                report.Detail,
                report.Status,
                report.CreatedUtc,
                report.ReporterParticipantType ?? MessagingParticipantTypes.Client,
                report.ReportedParticipantType ?? MessagingParticipantTypes.Client,
                report.ResolvedUtc,
                report.Resolution))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<CommunitySafetyOperationResult> ResolveReportAsync(
        Guid reportId,
        string moderatorUserId,
        string resolution,
        CancellationToken cancellationToken = default)
    {
        var normalizedModerator = Normalize(moderatorUserId);
        var normalizedResolution = CommunitySafetyReviewResolutions.Normalize(resolution);
        if (reportId == Guid.Empty || string.IsNullOrWhiteSpace(normalizedModerator) || normalizedResolution is null)
        {
            return CommunitySafetyOperationResult.Failure(
                "community_report_resolution_invalid",
                "Choose a valid report decision.");
        }

        var report = await _db.JourneyCircleReports.SingleOrDefaultAsync(item => item.Id == reportId, cancellationToken);
        if (report is null)
            return CommunitySafetyOperationResult.Failure("community_report_not_found", "This community report was not found.");
        if (report.Status != "Open")
            return CommunitySafetyOperationResult.Success();

        report.Status = "Resolved";
        report.Resolution = normalizedResolution;
        report.ResolvedByUserId = normalizedModerator;
        report.ResolvedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return CommunitySafetyOperationResult.Success();
    }

    private async Task<bool> IsValidTargetAsync(
        CommunitySafetyParticipant target,
        string targetKind,
        Guid? targetEntityId,
        CancellationToken cancellationToken)
    {
        return targetKind switch
        {
            CommunitySafetyTargetKinds.Profile => !targetEntityId.HasValue,
            CommunitySafetyTargetKinds.JourneyCircleProfile =>
                target.ParticipantType == MessagingParticipantTypes.Client &&
                targetEntityId == target.ProfileId &&
                await _db.JourneyCircleProfiles.AsNoTracking().AnyAsync(
                    profile => profile.ClientProfileId == target.ProfileId,
                    cancellationToken),
            CommunitySafetyTargetKinds.SocialPost => targetEntityId.HasValue &&
                await _db.SocialPosts.AsNoTracking().AnyAsync(
                    post => post.Id == targetEntityId.Value &&
                            post.AuthorProfileId == target.ProfileId &&
                            post.AuthorParticipantType == target.ParticipantType,
                    cancellationToken),
            CommunitySafetyTargetKinds.SocialComment => targetEntityId.HasValue &&
                await _db.SocialPostComments.AsNoTracking().AnyAsync(
                    comment => comment.Id == targetEntityId.Value &&
                               comment.AuthorProfileId == target.ProfileId &&
                               comment.AuthorParticipantType == target.ParticipantType,
                    cancellationToken),
            CommunitySafetyTargetKinds.Message => targetEntityId.HasValue &&
                await _db.InternalMessages.AsNoTracking().AnyAsync(
                    message => message.Id == targetEntityId.Value &&
                               message.SenderType == target.ParticipantType &&
                               target.UserIdForms.Contains(message.SenderUserId.ToLower()),
                    cancellationToken),
            _ => false
        };
    }

    private async Task<CommunitySafetyParticipant?> ResolveParticipantAsync(
        MessagingActor actor,
        CancellationToken cancellationToken)
    {
        var userId = Normalize(actor.UserId);
        if (string.IsNullOrWhiteSpace(userId))
            return null;

        if (actor.ParticipantType == MessagingParticipantTypes.Agent)
        {
            var agent = await _db.AgentProfiles.AsNoTracking().SingleOrDefaultAsync(
                profile => profile.IsActive && profile.AgentUserId.ToLower() == userId,
                cancellationToken);
            return agent is null
                ? null
                : new CommunitySafetyParticipant(
                    Normalize(agent.AgentUserId),
                    MessagingParticipantTypes.Agent,
                    agent.Id,
                    [Normalize(agent.AgentUserId)]);
        }

        if (actor.ParticipantType != MessagingParticipantTypes.Client)
            return null;

        var client = await _db.ClientProfiles.AsNoTracking().SingleOrDefaultAsync(
            profile => (profile.CrmStatus == null || profile.CrmStatus == "" || profile.CrmStatus == "Active") &&
                       (profile.ClientUserId.ToLower() == userId ||
                        (profile.ExternalIdentityObjectId != null && profile.ExternalIdentityObjectId.ToLower() == userId)),
            cancellationToken);
        return client is null ? null : ToClientParticipant(client);
    }

    private async Task<CommunitySafetyParticipant?> ResolveClientByProfileIdAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var client = await _db.ClientProfiles.AsNoTracking().SingleOrDefaultAsync(
            profile => profile.Id == profileId,
            cancellationToken);
        return client is null ? null : ToClientParticipant(client);
    }

    private static CommunitySafetyParticipant ToClientParticipant(ClientProfile profile)
    {
        var forms = LogicalParticipantIdentity.ClientUserIdForms(
            profile.ClientUserId,
            profile.ExternalIdentityObjectId);
        return new CommunitySafetyParticipant(
            Normalize(profile.ExternalIdentityObjectId ?? profile.ClientUserId),
            MessagingParticipantTypes.Client,
            profile.Id,
            forms);
    }

    private static string? NormalizeCategory(string? category)
    {
        var candidate = category?.Trim();
        return candidate is not null && Categories.Contains(candidate) ? candidate : null;
    }

    private static string? Limit(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}
