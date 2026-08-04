using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

/// <summary>
/// Preserves images saved before agent avatars became AgentProfile-owned.
/// It imports exact AgentUserId file keys once; runtime image resolution never
/// reads the legacy filesystem.
/// </summary>
public sealed class AgentProfileImageLegacyBackfillService
{
    private const long MaximumImageBytes = 3 * 1024 * 1024;
    private static readonly string[] ImageExtensions =
    [
        ".png",
        ".jpg",
        ".jpeg",
        ".webp"
    ];

    private readonly MasterAppDbContext _db;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AgentProfileImageLegacyBackfillService> _logger;
    private readonly IProfileImageWriter _profileImages;

    public AgentProfileImageLegacyBackfillService(
        MasterAppDbContext db,
        IWebHostEnvironment environment,
        ILogger<AgentProfileImageLegacyBackfillService> logger,
        IProfileImageWriter profileImages)
    {
        _db = db;
        _environment = environment;
        _logger = logger;
        _profileImages = profileImages;
    }

    public async Task<int> BackfillAsync(CancellationToken cancellationToken = default)
    {
        var root = GetLegacyAvatarRoot();
        if (!Directory.Exists(root))
            return 0;

        var profiles = await _db.AgentProfiles
            .Where(profile => profile.IsActive && (profile.ProfileImageContent == null || profile.ProfileImageContent.Length == 0))
            .ToListAsync(cancellationToken);
        var imported = 0;
        foreach (var profile in profiles)
        {
            var image = FindLegacyImage(
                root,
                await ResolveLegacyImageKeysAsync(profile, cancellationToken));
            if (image is null)
                continue;

            try
            {
                var info = new FileInfo(image);
                if (info.Length is <= 0 or > MaximumImageBytes)
                {
                    _logger.LogWarning(
                        "Skipping legacy agent profile image outside the permitted size. AgentProfileId={AgentProfileId} SizeBytes={SizeBytes}",
                        profile.Id,
                        info.Length);
                    continue;
                }

                var result = await _profileImages.UpdateAsync(
                    new MessagingParticipantIdentity(
                        string.Empty,
                        MessagingParticipantTypes.Agent,
                        profile.Id,
                        string.Empty,
                        null,
                        string.Empty),
                    await File.ReadAllBytesAsync(image, cancellationToken),
                    cancellationToken);
                if (!result.Succeeded)
                {
                    _logger.LogWarning(
                        "Skipping invalid legacy agent profile image. AgentProfileId={AgentProfileId} ErrorCode={ErrorCode}",
                        profile.Id,
                        result.ErrorCode);
                    continue;
                }

                imported++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    ex,
                    "Could not import a legacy agent profile image. AgentProfileId={AgentProfileId}",
                    profile.Id);
            }
        }

        if (imported > 0)
        {
            _logger.LogInformation(
                "Imported legacy agent profile images through the canonical profile-media authority. ImportedCount={ImportedCount}",
                imported);
        }
        return imported;
    }

    private string GetLegacyAvatarRoot()
    {
        var configured = Environment.GetEnvironmentVariable("LEGEND_AVATAR_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
            return Path.GetFullPath(Path.Combine(home.Trim(), "avatars"));

        return Path.Combine(_environment.ContentRootPath, "App_Data", "avatars");
    }

    private async Task<IReadOnlyList<string>> ResolveLegacyImageKeysAsync(
        AgentProfile profile,
        CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        AddLegacyImageKey(keys, profile.AgentUserId);

        var upn = Normalize(profile.AgentUpn);
        if (string.IsNullOrWhiteSpace(upn))
            return keys;

        var profileKeys = await _db.AgentProfiles
            .AsNoTracking()
            .Where(candidate => candidate.IsActive && candidate.AgentUpn.ToLower() == upn)
            .Select(candidate => candidate.AgentUserId)
            .ToListAsync(cancellationToken);
        var trackingKeys = await _db.AgentTrackingProfiles
            .AsNoTracking()
            .Where(candidate => candidate.AgentUpn.ToLower() == upn)
            .Select(candidate => candidate.AgentUserId)
            .ToListAsync(cancellationToken);

        foreach (var key in profileKeys.Concat(trackingKeys))
            AddLegacyImageKey(keys, key);

        return keys;
    }

    private static string? FindLegacyImage(string root, IEnumerable<string> agentUserIds)
    {
        foreach (var agentUserId in agentUserIds)
        {
            var key = agentUserId.Trim();
            if (!string.Equals(Path.GetFileName(key), key, StringComparison.Ordinal))
                continue;

            foreach (var extension in ImageExtensions)
            {
                var path = Path.Combine(root, $"{key}{extension}");
                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }

    private static void AddLegacyImageKey(ICollection<string> keys, string? candidate)
    {
        var key = candidate?.Trim();
        if (string.IsNullOrWhiteSpace(key) ||
            !string.Equals(Path.GetFileName(key), key, StringComparison.Ordinal) ||
            keys.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        keys.Add(key);
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}

internal sealed class AgentProfileImageLegacyBackfillHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentProfileImageLegacyBackfillHostedService> _logger;

    public AgentProfileImageLegacyBackfillHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<AgentProfileImageLegacyBackfillHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var importer = scope.ServiceProvider.GetRequiredService<AgentProfileImageLegacyBackfillService>();
            await importer.BackfillAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent profile image backfill failed; legacy images remain preserved for the next startup attempt.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
