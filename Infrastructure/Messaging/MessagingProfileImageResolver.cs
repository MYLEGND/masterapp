using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Messaging;

public interface IMessagingProfileImageResolver
{
    Task<MessagingProfileImage?> ResolveAsync(
        MessagingRecipientSummary participant,
        CancellationToken cancellationToken = default);
}

public sealed record MessagingProfileImage(string PhysicalPath, string ContentType);

internal sealed class MessagingProfileImageResolver : IMessagingProfileImageResolver
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp"];

    private readonly MasterAppDbContext _db;
    private readonly IWebHostEnvironment _environment;

    public MessagingProfileImageResolver(MasterAppDbContext db, IWebHostEnvironment environment)
    {
        _db = db;
        _environment = environment;
    }

    public async Task<MessagingProfileImage?> ResolveAsync(
        MessagingRecipientSummary participant,
        CancellationToken cancellationToken = default)
    {
        var candidates = participant.ParticipantType switch
        {
            MessagingParticipantTypes.Agent => await ResolveAgentKeysAsync(participant.UserId, cancellationToken),
            MessagingParticipantTypes.Client => await ResolveClientKeysAsync(participant.UserId, cancellationToken),
            _ => Array.Empty<string>()
        };

        var root = GetAvatarRoot();
        foreach (var candidate in candidates)
        {
            var safeCandidate = Path.GetFileName(candidate.Trim());
            if (string.IsNullOrWhiteSpace(safeCandidate) || !string.Equals(safeCandidate, candidate.Trim(), StringComparison.Ordinal))
                continue;

            foreach (var extension in Extensions)
            {
                var path = Path.Combine(root, $"{safeCandidate}{extension}");
                if (!File.Exists(path))
                    continue;

                return new MessagingProfileImage(path, ContentTypeFor(extension));
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<string>> ResolveAgentKeysAsync(string userId, CancellationToken cancellationToken)
    {
        var normalizedUserId = userId.Trim().ToLowerInvariant();
        var profile = await _db.AgentProfiles
            .AsNoTracking()
            .Where(x => x.AgentUserId.ToLower() == normalizedUserId)
            .OrderByDescending(x => x.UpdatedUtc)
            .Select(x => new { x.AgentUserId, x.AgentUpn })
            .FirstOrDefaultAsync(cancellationToken);

        var keys = new List<string> { userId.Trim() };
        if (string.IsNullOrWhiteSpace(profile?.AgentUpn))
            return keys;

        var sameUpnKeys = await _db.AgentProfiles
            .AsNoTracking()
            .Where(x => x.AgentUpn == profile.AgentUpn)
            .OrderByDescending(x => x.UpdatedUtc)
            .Select(x => x.AgentUserId)
            .ToListAsync(cancellationToken);
        keys.AddRange(sameUpnKeys);

        var sameTrackingUpnKeys = await _db.AgentTrackingProfiles
            .AsNoTracking()
            .Where(x => x.AgentUpn == profile.AgentUpn)
            .OrderByDescending(x => x.UpdatedUtc)
            .Select(x => x.AgentUserId)
            .ToListAsync(cancellationToken);
        keys.AddRange(sameTrackingUpnKeys);

        return keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<IReadOnlyList<string>> ResolveClientKeysAsync(string userId, CancellationToken cancellationToken)
    {
        var normalizedUserId = userId.Trim().ToLowerInvariant();
        var clientUserId = await _db.ClientProfiles
            .AsNoTracking()
            .Where(x => x.ClientUserId.ToLower() == normalizedUserId ||
                        (x.ExternalIdentityObjectId != null && x.ExternalIdentityObjectId.ToLower() == normalizedUserId))
            .Select(x => x.ClientUserId)
            .FirstOrDefaultAsync(cancellationToken);

        return Guid.TryParse(clientUserId, out var clientId)
            ? [clientId.ToString("D")]
            : Array.Empty<string>();
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
}
