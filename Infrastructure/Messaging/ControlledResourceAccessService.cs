using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shared.Auth;

namespace Infrastructure.Messaging;

public sealed record FounderAssistantRule(
    Guid Id,
    string Key,
    string Scope,
    string RuleText,
    string Provenance,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? SupersededUtc = null,
    Guid? SupersededById = null);

public sealed record FounderAssistantRuleWriteResult(
    bool Succeeded,
    string ReasonCode,
    FounderAssistantRule? Rule = null);

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


    Task<IReadOnlyList<FounderAssistantRule>> GetFounderAssistantRulesAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FounderAssistantRule>>(Array.Empty<FounderAssistantRule>());

    Task<FounderAssistantRuleWriteResult> UpsertFounderAssistantRuleAsync(
        MessagingActor actor,
        string key,
        string scope,
        string ruleText,
        string currentUserMessage,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new FounderAssistantRuleWriteResult(false, "founder_rule_authority_unavailable"));
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

    public async Task<bool> IsCanonicalFounderManagerAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default)
    {
        actor = Normalize(actor);
        var configuredFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID")
            ?? Environment.GetEnvironmentVariable("FounderOid")
            ?? _configuration["Founder:Oid"];
        // Member routes may carry the stored ClientUserId instead of the Entra
        // object ID. Resolve only the existing database-backed identity forms;
        // names, email addresses and client-supplied claims grant no authority.
        var userIds = await ParticipantUserIdFormsAsync(actor, cancellationToken);
        return userIds.Any(userId => FounderAuthority.IsConfiguredFounderIdentity(
            userId, configuredFounderOid));
    }

    public async Task<string?> GetPreferredLanguageAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default)
    {
        actor = Normalize(actor);
        var profileId = await ResolveProfileIdAsync(actor, cancellationToken);

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
        var profileId = await ResolveProfileIdAsync(actor, cancellationToken);

        if (!profileId.HasValue)
            return null;

        var language = await _db.MobileProfileSettings.AsNoTracking()
            .Where(setting => setting.ProfileId == profileId.Value && setting.ParticipantType == actor.ParticipantType)
            .Select(setting => setting.PreferredCommunicationLanguage)
            .SingleOrDefaultAsync(cancellationToken);
        return await _languages.NormalizeEnabledTranslationLanguageReadOnlyAsync(language, cancellationToken);
    }

    public async Task<IReadOnlyList<FounderAssistantRule>> GetFounderAssistantRulesAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default)
    {
        actor = Normalize(actor);
        if (!await IsCanonicalFounderManagerAsync(actor, cancellationToken))
            return Array.Empty<FounderAssistantRule>();
        var profileId = await ResolveProfileIdAsync(actor, cancellationToken);
        if (!profileId.HasValue)
            return Array.Empty<FounderAssistantRule>();
        var json = await _db.MobileProfileSettings.AsNoTracking()
            .Where(setting => setting.ProfileId == profileId.Value && setting.ParticipantType == actor.ParticipantType)
            .Select(setting => setting.FounderAssistantRulesJson)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<FounderAssistantRule>();
        var rules = JsonSerializer.Deserialize<List<FounderAssistantRule>>(json)
            ?? throw new InvalidOperationException("Founder assistant rules JSON is invalid.");
        return rules.Where(rule => rule.SupersededUtc is null)
            .OrderBy(rule => rule.Scope, StringComparer.Ordinal)
            .ThenBy(rule => rule.Key, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<FounderAssistantRuleWriteResult> UpsertFounderAssistantRuleAsync(
        MessagingActor actor,
        string key,
        string scope,
        string ruleText,
        string currentUserMessage,
        CancellationToken cancellationToken = default)
    {
        actor = Normalize(actor);
        if (!await IsCanonicalFounderManagerAsync(actor, cancellationToken))
            return new(false, "founder_rule_founder_required");
        key = (key ?? string.Empty).Trim().ToLowerInvariant();
        scope = (scope ?? string.Empty).Trim().ToLowerInvariant();
        ruleText = (ruleText ?? string.Empty).Trim();
        if (!Regex.IsMatch(key, "^[a-z0-9][a-z0-9._-]{0,79}$", RegexOptions.CultureInvariant) ||
            scope is not ("global" or "engineering" or "design" or "analytics" or "communication" or "workflow") ||
            ruleText.Length is < 1 or > 1000 ||
            string.IsNullOrWhiteSpace(currentUserMessage) ||
            !currentUserMessage.Contains(ruleText, StringComparison.Ordinal))
            return new(false, "founder_rule_literal_validation_failed");
        if (ContainsSensitiveFounderRule(ruleText))
            return new(false, "founder_rule_private_or_secret_content_rejected");

        var profileId = await ResolveProfileIdAsync(actor, cancellationToken);
        if (!profileId.HasValue)
            return new(false, "founder_rule_profile_unavailable");
        var settings = await _db.MobileProfileSettings
            .SingleOrDefaultAsync(setting => setting.ProfileId == profileId.Value && setting.ParticipantType == actor.ParticipantType, cancellationToken);
        if (settings is null)
        {
            settings = new Domain.Entities.MobileProfileSettings
            {
                ProfileId = profileId.Value,
                ParticipantType = actor.ParticipantType,
                FounderAssistantRulesJson = "[]"
            };
            _db.MobileProfileSettings.Add(settings);
        }
        var rules = string.IsNullOrWhiteSpace(settings.FounderAssistantRulesJson)
            ? new List<FounderAssistantRule>()
            : JsonSerializer.Deserialize<List<FounderAssistantRule>>(settings.FounderAssistantRulesJson)
                ?? throw new InvalidOperationException("Founder assistant rules JSON is invalid.");
        var existing = rules.LastOrDefault(rule => rule.SupersededUtc is null && rule.Key == key && rule.Scope == scope);
        if (existing is not null && string.Equals(existing.RuleText, ruleText, StringComparison.Ordinal))
            return new(true, "founder_rule_already_current", existing);

        var now = DateTime.UtcNow;
        var next = new FounderAssistantRule(Guid.NewGuid(), key, scope, ruleText,
            "FounderExplicitInstruction", now, now);
        if (existing is not null)
        {
            var index = rules.IndexOf(existing);
            rules[index] = existing with { SupersededUtc = now, SupersededById = next.Id, UpdatedUtc = now };
        }
        rules.Add(next);
        if (rules.Count > 80)
            rules = rules.OrderByDescending(rule => rule.UpdatedUtc).Take(80).OrderBy(rule => rule.CreatedUtc).ToList();
        var serialized = JsonSerializer.Serialize(rules);
        if (serialized.Length > 16_000)
            return new(false, "founder_rule_capacity_exceeded");
        settings.FounderAssistantRulesJson = serialized;
        settings.UpdatedUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
        return new(true, "founder_rule_saved", next);
    }

    private async Task<Guid?> ResolveProfileIdAsync(MessagingActor actor, CancellationToken cancellationToken) =>
        actor.ParticipantType switch
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

    private static bool ContainsSensitiveFounderRule(string value) =>
        Regex.IsMatch(value,
            @"(?:password|passwd|pwd|secret|token|api[_ -]?key|connection[_ -]?string)\s*[:=]|-----BEGIN [A-Z ]*PRIVATE KEY-----|\b(?:gh[pousr]_|github_pat_|AKIA)[A-Za-z0-9_]{8,}|\b\d{3}-\d{2}-\d{4}\b|\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b|\b(?:\+?1[-. (]*)?(?:\d{3}[-. )]*)\d{3}[-. ]*\d{4}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));

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
