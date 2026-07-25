using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ClientApp.Services;

/// <summary>
/// One-time preservation of photos saved before client avatars became profile-owned.
/// After import, all runtime reads use ClientProfile image data exclusively.
/// </summary>
public sealed class ClientProfileImageLegacyBackfillService
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
    private readonly ILogger<ClientProfileImageLegacyBackfillService> _logger;

    public ClientProfileImageLegacyBackfillService(
        MasterAppDbContext db,
        IWebHostEnvironment environment,
        ILogger<ClientProfileImageLegacyBackfillService> logger)
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

        var profiles = await _db.ClientProfiles
            .Where(profile => profile.ProfileImageContent == null || profile.ProfileImageContent.Length == 0)
            .ToListAsync(cancellationToken);
        var imported = 0;
        foreach (var profile in profiles)
        {
            var image = FindLegacyImage(root, profile.Id);
            if (image is null)
                continue;

            try
            {
                var info = new FileInfo(image.Value.Path);
                if (info.Length is <= 0 or > MaximumImageBytes)
                {
                    _logger.LogWarning(
                        "Skipping legacy client profile image outside the permitted size. ClientProfileId={ClientProfileId} SizeBytes={SizeBytes}",
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
                    "Could not import a legacy client profile image. ClientProfileId={ClientProfileId}",
                    profile.Id);
            }
        }

        if (imported == 0)
            return 0;

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Imported legacy client profile images into ClientProfiles. ImportedCount={ImportedCount}",
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

    private static (string Path, string ContentType)? FindLegacyImage(string root, Guid clientProfileId)
    {
        foreach (var imageType in ImageTypes)
        {
            var path = Path.Combine(root, $"{clientProfileId:D}{imageType.Extension}");
            if (File.Exists(path))
                return (path, imageType.ContentType);
        }

        return null;
    }
}

internal sealed class ClientProfileImageLegacyBackfillHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ClientProfileImageLegacyBackfillHostedService> _logger;

    public ClientProfileImageLegacyBackfillHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<ClientProfileImageLegacyBackfillHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var importer = scope.ServiceProvider.GetRequiredService<ClientProfileImageLegacyBackfillService>();
            await importer.BackfillAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Client profile image backfill failed; legacy images remain preserved for the next startup attempt.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
