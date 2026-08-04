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

/// <summary>
/// A typed key for profile-media projection. A profile GUID is only meaningful
/// together with its participant type because agent and client records are
/// separate authorities.
/// </summary>
public readonly record struct MessagingProfileImageKey(string ParticipantType, Guid ProfileId)
{
    public static MessagingProfileImageKey From(MessagingParticipantIdentity participant) => new(
        participant.ParticipantType,
        participant.ProfileId);
}

/// <summary>
/// Optional bulk read capability of the canonical profile-media authority.
/// List and feed surfaces use it to avoid issuing one profile query per row.
/// </summary>
public interface IMessagingProfileImageBatchResolver
{
    Task<IReadOnlyDictionary<MessagingProfileImageKey, MessagingProfileImage>> ResolveManyAsync(
        IEnumerable<MessagingParticipantIdentity> participants,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The only mutation boundary for a member profile image. AgentPortal,
/// ClientApp, mobile Account, and messaging projections all use the same
/// typed profile records; no surface maintains a second avatar copy.
/// </summary>
public interface IProfileImageWriter
{
    Task<ProfileImageUpdateResult> UpdateAsync(
        MessagingParticipantIdentity participant,
        byte[] content,
        CancellationToken cancellationToken = default);
}

public sealed record ProfileImageUpdateResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    MessagingProfileImage? Image)
{
    public static ProfileImageUpdateResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, null);

    public static ProfileImageUpdateResult Success(MessagingProfileImage image) =>
        new(true, null, null, image);
}

internal sealed class MessagingProfileImageResolver :
    IMessagingProfileImageResolver,
    IMessagingProfileImageBatchResolver,
    IProfileImageWriter
{
    private const int MaximumProfileImageBytes = 3 * 1024 * 1024;
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
        var images = await ResolveManyAsync([participant], cancellationToken);
        return images.GetValueOrDefault(MessagingProfileImageKey.From(participant));
    }

    public async Task<IReadOnlyDictionary<MessagingProfileImageKey, MessagingProfileImage>> ResolveManyAsync(
        IEnumerable<MessagingParticipantIdentity> participants,
        CancellationToken cancellationToken = default)
    {
        var requested = participants
            .Where(participant => participant.ProfileId != Guid.Empty)
            .Select(MessagingProfileImageKey.From)
            .Where(key =>
                key.ParticipantType == MessagingParticipantTypes.Agent ||
                key.ParticipantType == MessagingParticipantTypes.Client)
            .Distinct()
            .ToArray();
        if (requested.Length == 0)
            return new Dictionary<MessagingProfileImageKey, MessagingProfileImage>();

        var images = new Dictionary<MessagingProfileImageKey, MessagingProfileImage>();
        var agentProfileIds = requested
            .Where(key => key.ParticipantType == MessagingParticipantTypes.Agent)
            .Select(key => key.ProfileId)
            .ToArray();
        var clientProfileIds = requested
            .Where(key => key.ParticipantType == MessagingParticipantTypes.Client)
            .Select(key => key.ProfileId)
            .ToArray();

        if (agentProfileIds.Length > 0)
        {
            var agents = await _db.AgentProfiles
                .AsNoTracking()
                .Where(profile => profile.IsActive && agentProfileIds.Contains(profile.Id))
                .Select(profile => new ProfileImageRow(
                    profile.Id,
                    profile.ProfileImageContent,
                    profile.ProfileImageContentType))
                .ToListAsync(cancellationToken);

            AddResolvedImages(images, MessagingParticipantTypes.Agent, agents);
        }

        if (clientProfileIds.Length > 0)
        {
            var clients = await _db.ClientProfiles
                .AsNoTracking()
                .Where(profile => clientProfileIds.Contains(profile.Id))
                .Select(profile => new ProfileImageRow(
                    profile.Id,
                    profile.ProfileImageContent,
                    profile.ProfileImageContentType))
                .ToListAsync(cancellationToken);

            AddResolvedImages(images, MessagingParticipantTypes.Client, clients);
        }

        return images;
    }

    public async Task<ProfileImageUpdateResult> UpdateAsync(
        MessagingParticipantIdentity participant,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        if (participant.ProfileId == Guid.Empty)
        {
            return ProfileImageUpdateResult.Failure(
                "PROFILE_IMAGE_PROFILE_REQUIRED",
                "The profile image could not be associated with this account.");
        }

        var validation = Infrastructure.Security.UploadValidation.UploadValidator.ValidateImageContent(
            content,
            Infrastructure.Security.UploadValidation.UploadValidationPolicy.Images(MaximumProfileImageBytes));
        var contentType = NormalizeSupportedImageContentType(validation.DetectedContentType);
        if (!validation.IsValid || contentType is null)
        {
            return ProfileImageUpdateResult.Failure(
                validation.ErrorCode ?? "PROFILE_IMAGE_INVALID",
                validation.ErrorMessage ?? "Choose a valid PNG, JPG, or WEBP profile picture under 3 MB.");
        }

        var now = DateTime.UtcNow;
        switch (participant.ParticipantType)
        {
            case MessagingParticipantTypes.Agent:
            {
                var profile = await _db.AgentProfiles.SingleOrDefaultAsync(
                    candidate => candidate.Id == participant.ProfileId && candidate.IsActive,
                    cancellationToken);
                if (profile is null)
                {
                    return ProfileImageUpdateResult.Failure(
                        "PROFILE_IMAGE_PROFILE_UNAVAILABLE",
                        "Your agent profile is not available.");
                }

                profile.ProfileImageContent = content;
                profile.ProfileImageContentType = contentType;
                profile.UpdatedUtc = now;
                break;
            }

            case MessagingParticipantTypes.Client:
            {
                var profile = await _db.ClientProfiles.SingleOrDefaultAsync(
                    candidate => candidate.Id == participant.ProfileId,
                    cancellationToken);
                if (profile is null)
                {
                    return ProfileImageUpdateResult.Failure(
                        "PROFILE_IMAGE_PROFILE_UNAVAILABLE",
                        "Your client profile is not available.");
                }

                profile.ProfileImageContent = content;
                profile.ProfileImageContentType = contentType;
                profile.UpdatedUtc = now;
                break;
            }

            default:
                return ProfileImageUpdateResult.Failure(
                    "PROFILE_IMAGE_PARTICIPANT_INVALID",
                    "This account cannot update a profile picture.");
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Profile image updated through the canonical profile media path. ParticipantType={ParticipantType} ProfileId={ProfileId}",
            participant.ParticipantType,
            participant.ProfileId);
        return ProfileImageUpdateResult.Success(new MessagingProfileImage(content, contentType));
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
        var images = await ResolveManyAsync(
            [new MessagingParticipantIdentity(
                string.Empty,
                MessagingParticipantTypes.Client,
                clientProfileId,
                string.Empty,
                null,
                string.Empty)],
            cancellationToken);
        return images.GetValueOrDefault(new MessagingProfileImageKey(
            MessagingParticipantTypes.Client,
            clientProfileId));
    }

    private void AddResolvedImages(
        IDictionary<MessagingProfileImageKey, MessagingProfileImage> destination,
        string participantType,
        IEnumerable<ProfileImageRow> rows)
    {
        foreach (var row in rows)
        {
            var contentType = NormalizeSupportedImageContentType(row.ContentType);
            if (row.Content is not { Length: > 0 } || contentType is null)
                continue;

            destination[new MessagingProfileImageKey(participantType, row.Id)] =
                new MessagingProfileImage(row.Content, contentType);
        }
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

    private sealed record ProfileImageRow(Guid Id, byte[]? Content, string? ContentType);
}
