using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Identity;

namespace ClientApp.Services;

public sealed record AzureUserUpdateResult(bool Success, bool Skipped, string? Message = null);

public interface IAzureUserUpdater
{
    Task<AzureUserUpdateResult> UpdateEmailAsync(string userObjectId, string newEmail, CancellationToken cancellationToken = default);
}

public sealed class AzureUserUpdaterAdapter : IAzureUserUpdater
{
    private readonly IAzureClientEmailSyncService _syncService;

    public AzureUserUpdaterAdapter(IAzureClientEmailSyncService syncService)
    {
        _syncService = syncService;
    }

    public async Task<AzureUserUpdateResult> UpdateEmailAsync(string userObjectId, string newEmail, CancellationToken cancellationToken = default)
    {
        var result = await _syncService.UpdateEmailAsync(userObjectId, newEmail, cancellationToken);
        return new AzureUserUpdateResult(result.Success, result.Skipped, result.Message);
    }
}
