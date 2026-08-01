using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

public interface IMessagingProfileImageResolver
{
    Task<MessagingProfileImage?> ResolveAsync(
        MessagingParticipantIdentity participant,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<(string UserId, string ParticipantType), MessagingParticipantIdentity>> ResolveIdentitiesAsync(
        IEnumerable<MessagingParticipantReference> participants,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, MessagingParticipantIdentity>> ResolveClientIdentitiesByProfileIdAsync(
        IEnumerable<Guid> clientProfileIds,
        CancellationToken cancellationToken = default);

    Task<MessagingProfileImage?> ResolveClientProfileImageAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default);
}

public sealed record MessagingProfileImage(byte[] Content, string ContentType);

internal sealed class MessagingProfileImageResolver : IMessagingProfileImageResolver
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<MessagingProfileImageResolver> _logger;

    public MessagingProfileImageResolver(
        MasterAppDbContext db,
        ILogger<MessagingProfileImageResolver> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<MessagingProfileImage?> ResolveAsync(
        MessagingParticipantIdentity participant,
        CancellationToken cancellationToken = default)
    {
        return participant.ParticipantType switch
        {
            MessagingParticipantTypes.Agent => await ResolveAgentProfileImageAsync(participant.ProfileId, cancellationToken),
            MessagingParticipantTypes.Client => await ResolveClientProfileImageAsync(participant.ProfileId, cancellationToken),
            _ => null
        };
    }

    public async Task<IReadOnlyDictionary<(string UserId, string ParticipantType), MessagingParticipantIdentity>> ResolveIdentitiesAsync(
        IEnumerable<MessagingParticipantReference> participants,
        CancellationToken cancellationToken = default)
    {
        var references = participants
            .Select(reference => new MessagingParticipantReference(
                Normalize(reference.UserId),
                reference.ParticipantType?.Trim() ?? string.Empty))
            .Where(reference =>
                !string.IsNullOrWhiteSpace(reference.UserId) &&
                (reference.ParticipantType == MessagingParticipantTypes.Agent ||
                 reference.ParticipantType == MessagingParticipantTypes.Client))
            .Distinct()
            .ToArray();
        if (references.Length == 0)
            return new Dictionary<(string UserId, string ParticipantType), MessagingParticipantIdentity>();

        var result = new Dictionary<(string UserId, string ParticipantType), MessagingParticipantIdentity>();
        var agentUserIds = references
            .Where(reference => reference.ParticipantType == MessagingParticipantTypes.Agent)
            .Select(reference => reference.UserId)
            .ToArray();
        var clientUserIds = references
            .Where(reference => reference.ParticipantType == MessagingParticipantTypes.Client)
            .Select(reference => reference.UserId)
            .ToArray();

        if (agentUserIds.Length > 0)
        {
            var agents = await _db.AgentProfiles
                .AsNoTracking()
                .Where(profile => profile.IsActive && agentUserIds.Contains(profile.AgentUserId.ToLower()))
                .Select(profile => new AgentIdentityRow(
                    profile.Id,
                    profile.AgentUserId,
                    profile.FullName,
                    profile.AgentUpn,
                    profile.NormalizedEmail,
                    profile.Title,
                    profile.IsVerified,
                    profile.Phone))
                .ToListAsync(cancellationToken);

            foreach (var userId in agentUserIds)
            {
                var matches = agents
                    .Where(profile => string.Equals(Normalize(profile.UserId), userId, StringComparison.Ordinal))
                    .ToArray();
                if (matches.Length != 1)
                {
                    LogIdentityResolutionFailure(userId, MessagingParticipantTypes.Agent, matches.Length);
                    continue;
                }

                var profile = matches[0];
                var displayName = FirstNonEmpty(profile.FullName, profile.Email, "Agent");
                result[(userId, MessagingParticipantTypes.Agent)] = new MessagingParticipantIdentity(
                    userId,
                    MessagingParticipantTypes.Agent,
                    profile.Id,
                    displayName,
                    profile.Email,
                    Initials(displayName),
                    profile.IsVerified || LegendVerifiedIdentity.IsVerifiedAgentEmail(
                        profile.NormalizedEmail ?? profile.Email),
                    AgentProfileIdentity.LegendRoleLabel(profile.Title),
                    profile.Phone);
            }
        }

        if (clientUserIds.Length > 0)
        {
            var clients = await _db.ClientProfiles
                .AsNoTracking()
                .Where(profile =>
                    clientUserIds.Contains(profile.ClientUserId.ToLower()) ||
                    (profile.ExternalIdentityObjectId != null &&
                     clientUserIds.Contains(profile.ExternalIdentityObjectId.ToLower())))
                .Select(profile => new ClientIdentityRow(
                    profile.Id,
                    profile.ClientUserId,
                    profile.ExternalIdentityObjectId,
                    profile.FirstName,
                    profile.LastName,
                    profile.Email,
                    profile.IsVerified,
                    profile.Phone))
                .ToListAsync(cancellationToken);
            foreach (var userId in clientUserIds)
            {
                var matches = clients
                    .Where(profile =>
                        string.Equals(Normalize(profile.ClientUserId), userId, StringComparison.Ordinal) ||
                        string.Equals(Normalize(profile.ExternalIdentityObjectId), userId, StringComparison.Ordinal))
                    .ToArray();
                if (matches.Length != 1)
                {
                    LogIdentityResolutionFailure(userId, MessagingParticipantTypes.Client, matches.Length);
                    continue;
                }

                var profile = matches[0];
                var displayName = FirstNonEmpty($"{profile.FirstName} {profile.LastName}".Trim(), profile.Email, "Client");
                result[(userId, MessagingParticipantTypes.Client)] = new MessagingParticipantIdentity(
                    userId,
                    MessagingParticipantTypes.Client,
                    profile.Id,
                    displayName,
                    profile.Email,
                    Initials(displayName),
                    profile.IsVerified,
                    null,
                    profile.Phone);
            }
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, MessagingParticipantIdentity>> ResolveClientIdentitiesByProfileIdAsync(
        IEnumerable<Guid> clientProfileIds,
        CancellationToken cancellationToken = default)
    {
        var profileIds = clientProfileIds
            .Where(profileId => profileId != Guid.Empty)
            .Distinct()
            .ToArray();

        if (profileIds.Length == 0)
            return new Dictionary<Guid, MessagingParticipantIdentity>();

        var profiles = await _db.ClientProfiles
            .AsNoTracking()
            .Where(profile => profileIds.Contains(profile.Id))
            .Select(profile => new ClientIdentityRow(
                profile.Id,
                profile.ClientUserId,
                profile.ExternalIdentityObjectId,
                profile.FirstName,
                profile.LastName,
                profile.Email,
                profile.IsVerified,
                profile.Phone))
            .ToListAsync(cancellationToken);

        var result = new Dictionary<Guid, MessagingParticipantIdentity>();

        foreach (var profileId in profileIds)
        {
            var matches = profiles
                .Where(profile => profile.Id == profileId)
                .ToArray();

            if (matches.Length != 1)
            {
                _logger.LogWarning(
                    "Messaging client identity resolution by profile ID failed closed. ClientProfileId={ClientProfileId} MatchCount={MatchCount} AmbiguityDetected={AmbiguityDetected}",
                    profileId,
                    matches.Length,
                    matches.Length > 1);
                continue;
            }

            var profile = matches[0];
            var userId = FirstNonEmpty(
                profile.ExternalIdentityObjectId,
                profile.ClientUserId);

            if (string.IsNullOrWhiteSpace(userId))
            {
                _logger.LogWarning(
                    "Messaging client identity resolution by profile ID found no stored user identity. ClientProfileId={ClientProfileId}",
                    profileId);
                continue;
            }

            userId = Normalize(userId);
            var displayName = FirstNonEmpty(
                $"{profile.FirstName} {profile.LastName}".Trim(),
                profile.Email,
                "Client");

            result[profile.Id] = new MessagingParticipantIdentity(
                userId,
                MessagingParticipantTypes.Client,
                profile.Id,
                displayName,
                profile.Email,
                Initials(displayName),
                profile.IsVerified,
                null,
                profile.Phone);
        }

        return result;
    }

    public async Task<MessagingProfileImage?> ResolveClientProfileImageAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default)
    {
        var profile = await _db.ClientProfiles
            .AsNoTracking()
            .Where(x => x.Id == clientProfileId)
            .Select(x => new ClientProfileImageRow(
                x.ProfileImageContent,
                x.ProfileImageContentType))
            .FirstOrDefaultAsync(cancellationToken);
        var contentType = NormalizeSupportedImageContentType(profile?.ContentType);
        if (profile is null ||
            profile.Content is not { Length: > 0 } ||
            contentType is null)
            return null;

        _logger.LogDebug(
            "Messaging client profile image resolved from ClientProfiles. ClientProfileId={ClientProfileId} ImageSourceType={ImageSourceType}",
            clientProfileId,
            "ClientProfile");
        return new MessagingProfileImage(profile.Content, contentType);
    }

    private async Task<MessagingProfileImage?> ResolveAgentProfileImageAsync(
        Guid agentProfileId,
        CancellationToken cancellationToken)
    {
        var profile = await _db.AgentProfiles
            .AsNoTracking()
            .Where(x => x.Id == agentProfileId && x.IsActive)
            .Select(x => new AgentProfileImageRow(
                x.ProfileImageContent,
                x.ProfileImageContentType))
            .FirstOrDefaultAsync(cancellationToken);
        var contentType = NormalizeSupportedImageContentType(profile?.ContentType);
        if (profile is null ||
            profile.Content is not { Length: > 0 } ||
            contentType is null)
            return null;

        _logger.LogDebug(
            "Messaging agent profile image resolved from AgentProfiles. AgentProfileId={AgentProfileId} ImageSourceType={ImageSourceType}",
            agentProfileId,
            "AgentProfile");
        return new MessagingProfileImage(profile.Content, contentType);
    }

    private void LogIdentityResolutionFailure(string userId, string participantType, int matchCount)
    {
        _logger.LogWarning(
            "Messaging participant identity resolution failed closed. ParticipantUserId={ParticipantUserId} ExpectedParticipantType={ExpectedParticipantType} MatchCount={MatchCount} AmbiguityDetected={AmbiguityDetected}",
            userId,
            participantType,
            matchCount,
            matchCount > 1);
    }

    private static string? NormalizeSupportedImageContentType(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "image/png" => "image/png",
            "image/jpeg" or "image/jpg" => "image/jpeg",
            "image/webp" => "image/webp",
            _ => null
        };

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string Initials(string displayName)
    {
        var initials = string.Concat(displayName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(2)
            .Select(part => char.ToUpperInvariant(part[0])));
        return string.IsNullOrWhiteSpace(initials) ? "P" : initials;
    }

    private sealed record AgentIdentityRow(
        Guid Id,
        string UserId,
        string? FullName,
        string? Email,
        string? NormalizedEmail,
        string? Title,
        bool IsVerified,
        string? Phone);

    private sealed record ClientIdentityRow(
        Guid Id,
        string ClientUserId,
        string? ExternalIdentityObjectId,
        string? FirstName,
        string? LastName,
        string? Email,
        bool IsVerified,
        string? Phone);

    private sealed record ClientProfileImageRow(byte[]? Content, string? ContentType);

    private sealed record AgentProfileImageRow(byte[]? Content, string? ContentType);
}
