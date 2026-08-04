using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;

namespace ClientApp.Services;

/// <summary>
/// One-time preservation of photos saved before client avatars became profile-owned.
/// After import, all runtime reads use ClientProfile image data exclusively.
/// </summary>
public sealed class ClientProfileImageLegacyBackfillService
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
    private readonly ILogger<ClientProfileImageLegacyBackfillService> _logger;
    private readonly IProfileImageWriter _profileImages;

    public ClientProfileImageLegacyBackfillService(
        MasterAppDbContext db,
        IWebHostEnvironment environment,
        ILogger<ClientProfileImageLegacyBackfillService> logger,
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
                var info = new FileInfo(image);
                if (info.Length is <= 0 or > MaximumImageBytes)
                {
                    _logger.LogWarning(
                        "Skipping legacy client profile image outside the permitted size. ClientProfileId={ClientProfileId} SizeBytes={SizeBytes}",
                        profile.Id,
                        info.Length);
                    continue;
                }

                var result = await _profileImages.UpdateAsync(
                    new MessagingParticipantIdentity(
                        string.Empty,
                        MessagingParticipantTypes.Client,
                        profile.Id,
                        string.Empty,
                        null,
                        string.Empty),
                    await File.ReadAllBytesAsync(image, cancellationToken),
                    cancellationToken);
                if (!result.Succeeded)
                {
                    _logger.LogWarning(
                        "Skipping invalid legacy client profile image. ClientProfileId={ClientProfileId} ErrorCode={ErrorCode}",
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
                    "Could not import a legacy client profile image. ClientProfileId={ClientProfileId}",
                    profile.Id);
            }
        }

        if (imported > 0)
        {
            _logger.LogInformation(
                "Imported legacy client profile images through the canonical profile-media authority. ImportedCount={ImportedCount}",
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

    private static string? FindLegacyImage(string root, Guid clientProfileId)
    {
        foreach (var extension in ImageExtensions)
        {
            var path = Path.Combine(root, $"{clientProfileId:D}{extension}");
            if (File.Exists(path))
                return path;
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
