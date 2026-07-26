using System.Security.Claims;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shared.Auth;

namespace Infrastructure.Mobile;

public interface IMobileActorResolver
{
    Task<MobileActorResolution> ResolveAsync(
        ClaimsPrincipal principal,
        string? requestedParticipantType = null,
        CancellationToken cancellationToken = default);
}

public sealed record MobileResolvedActor(
    MessagingActor Actor,
    Guid ProfileId,
    string DisplayName);

public sealed record MobileActorResolution(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<MobileResolvedActor> PermittedActors,
    MobileResolvedActor? SelectedActor,
    bool RequiresParticipantSelection)
{
    public static MobileActorResolution Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, Array.Empty<MobileResolvedActor>(), null, false);
}

/// <summary>
/// Resolves the mobile actor from the validated Entra object ID and existing
/// typed profiles. It deliberately has no browser-cookie, UPN, email, or
/// provisioning fallback.
/// </summary>
public sealed class MobileActorResolver : IMobileActorResolver
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<MobileActorResolver> _logger;

    public MobileActorResolver(MasterAppDbContext db, ILogger<MobileActorResolver> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<MobileActorResolution> ResolveAsync(
        ClaimsPrincipal principal,
        string? requestedParticipantType = null,
        CancellationToken cancellationToken = default)
    {
        var userId = principal.GetCanonicalUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("Mobile actor resolution rejected a token without a canonical Entra object ID.");
            return MobileActorResolution.Failure("MOBILE_ACTOR_UNRESOLVED", "Your account could not be resolved for mobile access.");
        }

        var agents = await _db.AgentProfiles
            .AsNoTracking()
            .Where(profile => profile.IsActive && profile.AgentUserId.ToLower() == userId)
            .Select(profile => new MobileResolvedActor(
                new MessagingActor(userId, MessagingParticipantTypes.Agent),
                profile.Id,
                string.IsNullOrWhiteSpace(profile.FullName) ? "Agent" : profile.FullName.Trim()))
            .ToListAsync(cancellationToken);

        var clients = await _db.ClientProfiles
            .AsNoTracking()
            .Where(profile =>
                profile.ClientUserId.ToLower() == userId ||
                (profile.ExternalIdentityObjectId != null && profile.ExternalIdentityObjectId.ToLower() == userId))
            .Select(profile => new MobileResolvedActor(
                new MessagingActor(userId, MessagingParticipantTypes.Client),
                profile.Id,
                string.IsNullOrWhiteSpace($"{profile.FirstName} {profile.LastName}".Trim())
                    ? "Client"
                    : $"{profile.FirstName} {profile.LastName}".Trim()))
            .ToListAsync(cancellationToken);

        if (agents.Count > 1 || clients.Count > 1)
        {
            _logger.LogWarning(
                "Mobile actor resolution failed closed because a typed profile is ambiguous. UserId={UserId} AgentProfileCount={AgentProfileCount} ClientProfileCount={ClientProfileCount}",
                userId,
                agents.Count,
                clients.Count);
            return MobileActorResolution.Failure("MOBILE_ACTOR_AMBIGUOUS", "Your mobile access could not be resolved safely.");
        }

        var permittedActors = agents.Concat(clients).ToArray();
        if (permittedActors.Length == 0)
        {
            _logger.LogWarning("Mobile actor resolution found no active typed profile. UserId={UserId}", userId);
            return MobileActorResolution.Failure("MOBILE_ACTOR_UNRESOLVED", "Your account does not have mobile messaging access.");
        }

        if (string.IsNullOrWhiteSpace(requestedParticipantType))
        {
            return new MobileActorResolution(
                true,
                null,
                null,
                permittedActors,
                permittedActors.Length == 1 ? permittedActors[0] : null,
                permittedActors.Length > 1);
        }

        var normalizedParticipantType = NormalizeParticipantType(requestedParticipantType);
        if (normalizedParticipantType is null)
        {
            _logger.LogWarning("Mobile actor resolution rejected an unsupported participant type.");
            return MobileActorResolution.Failure("MOBILE_ROLE_INVALID", "The selected mobile role is not available.");
        }

        var selected = permittedActors.SingleOrDefault(actor =>
            string.Equals(actor.Actor.ParticipantType, normalizedParticipantType, StringComparison.Ordinal));
        if (selected is null)
        {
            _logger.LogWarning(
                "Mobile actor resolution rejected a role not permitted to the authenticated user. UserId={UserId} ParticipantType={ParticipantType}",
                userId,
                normalizedParticipantType);
            return MobileActorResolution.Failure("MOBILE_ROLE_FORBIDDEN", "The selected mobile role is not available.");
        }

        return new MobileActorResolution(true, null, null, permittedActors, selected, false);
    }

    private static string? NormalizeParticipantType(string? participantType) => participantType?.Trim() switch
    {
        MessagingParticipantTypes.Agent => MessagingParticipantTypes.Agent,
        MessagingParticipantTypes.Client => MessagingParticipantTypes.Client,
        _ => null
    };
}
