using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClientApp.Services;

public sealed record AzureUserUpdateResult(bool Success, bool Skipped, string? Message = null);

public interface IAzureUserUpdater
{
    Task<AzureUserUpdateResult> UpdateEmailAsync(string userObjectId, string newEmail, CancellationToken cancellationToken = default);
}

/// <summary>
/// Placeholder/no-op updater so we can wire the controller without breaking deployments.
/// When Graph credentials are provided, replace this with a real implementation.
/// </summary>
public sealed class NoopAzureUserUpdater : IAzureUserUpdater
{
    private readonly ILogger<NoopAzureUserUpdater> _logger;
    private readonly IConfiguration _config;

    public NoopAzureUserUpdater(ILogger<NoopAzureUserUpdater> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public Task<AzureUserUpdateResult> UpdateEmailAsync(string userObjectId, string newEmail, CancellationToken cancellationToken = default)
    {
        // If any Graph creds are missing, skip silently but log at debug.
        var tenantId = _config["AzureAd:TenantId"];
        var clientId = _config["AzureAd:ClientId"];
        var clientSecret = _config["AzureAd:ClientSecret"];

        var configured = !string.IsNullOrWhiteSpace(tenantId)
                         && !string.IsNullOrWhiteSpace(clientId)
                         && !string.IsNullOrWhiteSpace(clientSecret);

        if (!configured)
        {
            _logger.LogDebug("Graph email update skipped: AzureAd credentials not configured.");
            return Task.FromResult(new AzureUserUpdateResult(Success: true, Skipped: true,
                Message: "Graph updater not configured"));
        }

        // Placeholder success path; replace with real Graph call when creds are available.
        _logger.LogInformation("Graph email update placeholder: would update user {UserObjectId} to {Email}.",
            userObjectId, newEmail);

        return Task.FromResult(new AzureUserUpdateResult(Success: true, Skipped: false, Message: null));
    }
}
