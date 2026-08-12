using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shared.Auth;

namespace Infrastructure.Messaging;

public interface IControlledResourceAccessService
{
    Task<ControlledResourceAccess> GetAccessAsync(
        MessagingActor actor,
        string resourceType,
        CancellationToken cancellationToken = default);

    Task<bool> IsFounderManagerAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default);

    Task<bool> IsCanonicalFounderManagerAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default);

    Task<string?> GetPreferredLanguageAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the same MobileProfileSettings preference without treating a
    /// translation entitlement as a language identity decision. Messaging
    /// uses it to snapshot the actual sender's route at send time; recipient
    /// presentation still uses <see cref="GetPreferredLanguageAsync"/> and
    /// its existing access guard.
    /// </summary>
    Task<string?> GetCanonicalPreferredLanguageAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default) =>
        GetPreferredLanguageAsync(actor, cancellationToken);
}

/// <summary>
/// One server-side authority for controlled-resource state. Requests continue
/// to be authored and resolved by <see cref="MessagingService"/> so they retain
/// the existing Founder + Legend review queue and audit behavior.
/// </summary>
internal sealed class ControlledResourceAccessService : IControlledResourceAccessService
{
    private readonly MasterAppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILegendLanguageRegistry _languages;

    public ControlledResourceAccessService(
        MasterAppDbContext db,
        IConfiguration? configuration = null,
        ILegendLanguageRegistry? languages = null)
    {
        _db = db;
        _configuration = configuration ?? new ConfigurationBuilder().Build();
        _languages = languages ?? new LegendLanguageRegistry(_db, _configuration);
    }

    public async Task<ControlledResourceAccess> GetAccessAsync(
        MessagingActor actor,
        string resourceType,
        CancellationToken cancellationToken = default)
    {
        actor = Normalize(actor);
        if (!ControlledResourceTypes.IsSupported(resourceType))
            return new ControlledResourceAccess(resourceType, ControlledResourceAccessStates.NotGranted, false);

        var requiresCanonicalFounderAuthority = resourceType is
            ControlledResourceTypes.ScriptureManagement or
            ControlledResourceTypes.CommunityManagement or
            ControlledResourceTypes.SocialContentPriority;
        var canManage = requiresCanonicalFounderAuthority
            ? await IsCanonicalFounderManagerAsync(actor, cancellationToken)
            : await IsFounderManagerAsync(actor, cancellationToken);
        var actorUserIds = await ParticipantUserIdFormsAsync(actor, cancellationToken);
        var granted = resourceType switch
        {
            ControlledResourceTypes.VerificationBadge => await IsVerificationGrantedAsync(actor, cancellationToken),
            ControlledResourceTypes.LanguageTranslation or
            ControlledResourceTypes.ScriptureManagement or
            ControlledResourceTypes.CommunityManagement or
            ControlledResourceTypes.SocialContentPriority =>
                canManage || await HasActiveGrantAsync(actor, actorUserIds, resourceType, cancellationToken),
            _ => false
        };

        if (granted)
            return new ControlledResourceAccess(resourceType, ControlledResourceAccessStates.Granted, canManage);

        var pending = await _db.VerificationReviewRequests
            .AsNoTracking()
            .AnyAsync(request =>
                request.ResourceType == resourceType &&
                request.Status == VerificationReviewStatuses.Pending &&
                actorUserIds.Contains(request.RequesterUserId.ToLower()) &&
                request.RequesterParticipantType == actor.ParticipantType,
                cancellationToken);

        return new ControlledResourceAccess(
            resourceType,
            pending ? ControlledResourceAccessStates.Pending : ControlledResourceAccessStates.NotGranted,
            canManage);
    }

    public Task<bool> IsFounderManagerAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default)
    {
        // Founder-manager authority used to be reconstructed here from a
        // profile email while the Portal's FounderOnly guard used the
        // configured Entra object ID. That allowed the route and the mutation
        // boundary to disagree about the same signed-in Founder. Reuse the
        // established fail-closed object-ID authority for every Founder-managed
        // resource instead of maintaining a second email-based authority.
        return IsCanonicalFounderManagerAsync(actor, cancellationToken);
    }

    public Task<bool> IsCanonicalFounderManagerAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default)
    {
        actor = Normalize(actor);
        var configuredFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID")
            ?? Environment.GetEnvironmentVariable("FounderOid")
            ?? _configuration["Founder:Oid"];
        return Task.FromResult(FounderAuthority.IsConfiguredFounderIdentity(
            actor.UserId,
            configuredFounderOid));
    }

    public async Task<string?> GetPreferredLanguageAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default)
    {
        actor = Normalize(actor);
        var profileId = actor.ParticipantType switch
        {
            MessagingParticipantTypes.Agent => await _db.AgentProfiles.AsNoTracking()
                .Where(profile => profile.IsActive && profile.AgentUserId.ToLower() == actor.UserId)
                .Select(profile => (Guid?)profile.Id)
                .SingleOrDefaultAsync(cancellationToken),
            MessagingParticipantTypes.Client => await _db.ClientProfiles.AsNoTracking()
                .Where(profile => profile.ClientUserId.ToLower() == actor.UserId ||
                    (profile.ExternalIdentityObjectId != null && profile.ExternalIdentityObjectId.ToLower() == actor.UserId))
                .Select(profile => (Guid?)profile.Id)
                .SingleOrDefaultAsync(cancellationToken),
            _ => null
        };

        if (!profileId.HasValue)
            return null;

        var access = await GetAccessAsync(actor, ControlledResourceTypes.LanguageTranslation, cancellationToken);
        if (access.State != ControlledResourceAccessStates.Granted)
            return null;

        var language = await _db.MobileProfileSettings.AsNoTracking()
            .Where(setting => setting.ProfileId == profileId.Value && setting.ParticipantType == actor.ParticipantType)
            .Select(setting => setting.PreferredCommunicationLanguage)
            .SingleOrDefaultAsync(cancellationToken);
        return await _languages.NormalizeEnabledTranslationLanguageAsync(language, cancellationToken);
    }

    public async Task<string?> GetCanonicalPreferredLanguageAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default)
    {
        actor = Normalize(actor);
        var profileId = actor.ParticipantType switch
        {
            MessagingParticipantTypes.Agent => await _db.AgentProfiles.AsNoTracking()
                .Where(profile => profile.IsActive && profile.AgentUserId.ToLower() == actor.UserId)
                .Select(profile => (Guid?)profile.Id)
                .SingleOrDefaultAsync(cancellationToken),
            MessagingParticipantTypes.Client => await _db.ClientProfiles.AsNoTracking()
                .Where(profile => profile.ClientUserId.ToLower() == actor.UserId ||
                    (profile.ExternalIdentityObjectId != null && profile.ExternalIdentityObjectId.ToLower() == actor.UserId))
                .Select(profile => (Guid?)profile.Id)
                .SingleOrDefaultAsync(cancellationToken),
            _ => null
        };

        if (!profileId.HasValue)
            return null;

        var language = await _db.MobileProfileSettings.AsNoTracking()
            .Where(setting => setting.ProfileId == profileId.Value && setting.ParticipantType == actor.ParticipantType)
            .Select(setting => setting.PreferredCommunicationLanguage)
            .SingleOrDefaultAsync(cancellationToken);
        return await _languages.NormalizeEnabledTranslationLanguageAsync(language, cancellationToken);
    }

    private async Task<bool> IsVerificationGrantedAsync(
        MessagingActor actor,
        CancellationToken cancellationToken)
    {
        if (actor.ParticipantType == MessagingParticipantTypes.Agent)
        {
            return await _db.AgentProfiles.AsNoTracking().AnyAsync(profile =>
                profile.IsActive &&
                profile.AgentUserId.ToLower() == actor.UserId &&
                (profile.IsVerified ||
                 profile.NormalizedEmail == LegendVerifiedIdentity.FounderEmail ||
                 profile.NormalizedEmail == LegendVerifiedIdentity.LegendEmail ||
                 (profile.AgentUpn != null &&
                  (profile.AgentUpn.ToLower() == LegendVerifiedIdentity.FounderEmail ||
                   profile.AgentUpn.ToLower() == LegendVerifiedIdentity.LegendEmail))),
                cancellationToken);
        }

        if (actor.ParticipantType == MessagingParticipantTypes.Client)
        {
            var actorUserIds = await ParticipantUserIdFormsAsync(actor, cancellationToken);
            return await _db.ClientProfiles.AsNoTracking().AnyAsync(profile =>
                (actorUserIds.Contains(profile.ClientUserId.ToLower()) ||
                 (profile.ExternalIdentityObjectId != null &&
                  actorUserIds.Contains(profile.ExternalIdentityObjectId.ToLower()))) &&
                profile.IsVerified,
                cancellationToken);
        }

        return false;
    }

    private Task<bool> HasActiveGrantAsync(
        MessagingActor actor,
        string[] actorUserIds,
        string resourceType,
        CancellationToken cancellationToken) =>
        _db.ControlledResourceGrants
            .AsNoTracking()
            .AnyAsync(grant =>
                grant.IsActive &&
                grant.ResourceType == resourceType &&
                actorUserIds.Contains(grant.UserId.ToLower()) &&
                grant.ParticipantType == actor.ParticipantType,
                cancellationToken);

    private async Task<string[]> ParticipantUserIdFormsAsync(
        MessagingActor actor,
        CancellationToken cancellationToken)
    {
        if (actor.ParticipantType != MessagingParticipantTypes.Client)
            return [actor.UserId];

        var profile = await _db.ClientProfiles.AsNoTracking()
            .Where(candidate => candidate.ClientUserId.ToLower() == actor.UserId ||
                                (candidate.ExternalIdentityObjectId != null &&
                                 candidate.ExternalIdentityObjectId.ToLower() == actor.UserId))
            .Select(candidate => new
            {
                candidate.ClientUserId,
                candidate.ExternalIdentityObjectId
            })
            .FirstOrDefaultAsync(cancellationToken);

        return profile is null
            ? [actor.UserId]
            : LogicalParticipantIdentity.ClientUserIdForms(
                profile.ClientUserId,
                profile.ExternalIdentityObjectId);
    }

    private static MessagingActor Normalize(MessagingActor actor) => new(
        actor.UserId.Trim().ToLowerInvariant(),
        actor.ParticipantType.Trim());
}

public sealed record ControlledResourceAccess(
    string ResourceType,
    string State,
    bool CanManage);
