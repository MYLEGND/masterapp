using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
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

    Task<MessagingProfileImage?> ResolveClientProfileImageAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default);
}

public sealed record MessagingProfileImage(string PhysicalPath, string ContentType);

internal sealed class MessagingProfileImageResolver : IMessagingProfileImageResolver
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp"];

    private readonly MasterAppDbContext _db;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<MessagingProfileImageResolver> _logger;

    public MessagingProfileImageResolver(
        MasterAppDbContext db,
        IWebHostEnvironment environment,
        ILogger<MessagingProfileImageResolver> logger)
    {
        _db = db;
        _environment = environment;
        _logger = logger;
    }

    public async Task<MessagingProfileImage?> ResolveAsync(
        MessagingParticipantIdentity participant,
        CancellationToken cancellationToken = default)
    {
        var avatarKey = participant.ParticipantType switch
        {
            MessagingParticipantTypes.Agent => Normalize(participant.UserId),
            MessagingParticipantTypes.Client => participant.ProfileId.ToString("D"),
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(avatarKey))
            return null;

        var root = GetAvatarRoot();
        var safeAvatarKey = Path.GetFileName(avatarKey);
        if (string.IsNullOrWhiteSpace(safeAvatarKey) ||
            !string.Equals(safeAvatarKey, avatarKey, StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var extension in Extensions)
        {
            var path = Path.Combine(root, $"{safeAvatarKey}{extension}");
            if (!File.Exists(path))
                continue;

            _logger.LogDebug(
                "Messaging profile image resolved. ParticipantUserId={ParticipantUserId} ParticipantType={ParticipantType} ProfileId={ProfileId} ImageSourceType={ImageSourceType}",
                participant.UserId,
                participant.ParticipantType,
                participant.ProfileId,
                "ProfileAvatarStorage");
            return new MessagingProfileImage(path, ContentTypeFor(extension));
        }

        return null;
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
                    profile.AgentUpn))
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
                    Initials(displayName));
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
                    profile.Email))
                .ToListAsync(cancellationToken);
            var conflictingAgentUserIds = await _db.AgentProfiles
                .AsNoTracking()
                .Where(profile => profile.IsActive && clientUserIds.Contains(profile.AgentUserId.ToLower()))
                .Select(profile => profile.AgentUserId.ToLower())
                .ToListAsync(cancellationToken);
            var conflictingAgentSet = conflictingAgentUserIds.ToHashSet(StringComparer.Ordinal);

            foreach (var userId in clientUserIds)
            {
                if (conflictingAgentSet.Contains(userId))
                {
                    _logger.LogWarning(
                        "Messaging participant identity resolution rejected a conflicting client and agent identity. ParticipantUserId={ParticipantUserId} ExpectedParticipantType={ExpectedParticipantType} AmbiguityDetected={AmbiguityDetected}",
                        userId,
                        MessagingParticipantTypes.Client,
                        true);
                    continue;
                }

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
                    Initials(displayName));
            }
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
            .Select(x => new ClientIdentityRow(
                x.Id,
                x.ClientUserId,
                x.ExternalIdentityObjectId,
                x.FirstName,
                x.LastName,
                x.Email))
            .FirstOrDefaultAsync(cancellationToken);
        if (profile is null)
            return null;

        var displayName = FirstNonEmpty($"{profile.FirstName} {profile.LastName}".Trim(), profile.Email, "Client");
        return await ResolveAsync(
            new MessagingParticipantIdentity(
                Normalize(profile.ClientUserId),
                MessagingParticipantTypes.Client,
                profile.Id,
                displayName,
                profile.Email,
                Initials(displayName)),
            cancellationToken);
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

    private string GetAvatarRoot()
    {
        var configured = Environment.GetEnvironmentVariable("LEGEND_AVATAR_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));
            }
            catch
            {
                // Fall through to the repository's established hosting locations.
            }
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            try
            {
                return Path.GetFullPath(Path.Combine(home.Trim(), "avatars"));
            }
            catch
            {
                // Fall through to the application-local fallback used by the existing avatar controllers.
            }
        }

        return Path.Combine(_environment.ContentRootPath, "App_Data", "avatars");
    }

    private static string ContentTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg"
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

    private sealed record AgentIdentityRow(Guid Id, string UserId, string? FullName, string? Email);

    private sealed record ClientIdentityRow(
        Guid Id,
        string ClientUserId,
        string? ExternalIdentityObjectId,
        string? FirstName,
        string? LastName,
        string? Email);
}
