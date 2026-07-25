using Domain.Entities;
using Infrastructure.Data;
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
    private static readonly (string Extension, string ContentType)[] ImageTypes =
    [
        (".png", "image/png"),
        (".jpg", "image/jpeg"),
        (".jpeg", "image/jpeg"),
        (".webp", "image/webp")
    ];

    private readonly MasterAppDbContext _db;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AgentProfileImageLegacyBackfillService> _logger;

    public AgentProfileImageLegacyBackfillService(
        MasterAppDbContext db,
        IWebHostEnvironment environment,
        ILogger<AgentProfileImageLegacyBackfillService> logger)
    {
        _db = db;
        _environment = environment;
        _logger = logger;
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
                var info = new FileInfo(image.Value.Path);
                if (info.Length is <= 0 or > MaximumImageBytes)
                {
                    _logger.LogWarning(
                        "Skipping legacy agent profile image outside the permitted size. AgentProfileId={AgentProfileId} SizeBytes={SizeBytes}",
                        profile.Id,
                        info.Length);
                    continue;
                }

                profile.ProfileImageContent = await File.ReadAllBytesAsync(image.Value.Path, cancellationToken);
                profile.ProfileImageContentType = image.Value.ContentType;
                profile.UpdatedUtc = DateTime.UtcNow;
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

        if (imported == 0)
            return 0;

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Imported legacy agent profile images into AgentProfiles. ImportedCount={ImportedCount}",
            imported);
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

    private static (string Path, string ContentType)? FindLegacyImage(string root, IEnumerable<string> agentUserIds)
    {
        foreach (var agentUserId in agentUserIds)
        {
            var key = agentUserId.Trim();
            if (!string.Equals(Path.GetFileName(key), key, StringComparison.Ordinal))
                continue;

            foreach (var imageType in ImageTypes)
            {
                var path = Path.Combine(root, $"{key}{imageType.Extension}");
                if (File.Exists(path))
                    return (path, imageType.ContentType);
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
