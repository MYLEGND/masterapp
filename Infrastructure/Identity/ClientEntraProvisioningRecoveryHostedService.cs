using Domain.Billing;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Identity;

/// <summary>
/// Retries the existing Entra projection only for already-active client
/// subscriptions whose Microsoft identity binding did not finish after activation.
/// Billing, entitlement, invitation, and sign-in authorities remain unchanged.
/// </summary>
public sealed class ClientEntraProvisioningRecoveryHostedService(
    IServiceScopeFactory scopes,
    ILogger<ClientEntraProvisioningRecoveryHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
                var lifecycle = scope.ServiceProvider.GetRequiredService<IClientEntraLifecycleService>();
                await RecoverPendingAsync(db, lifecycle, logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Client Entra provisioning recovery cycle failed.");
            }

            try
            {
                await Task.Delay(RetryInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal static async Task<int> RecoverPendingAsync(
        MasterAppDbContext db,
        IClientEntraLifecycleService lifecycle,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var clientIds = await db.ClientProfiles
            .AsNoTracking()
            .Where(profile =>
                string.IsNullOrEmpty(profile.ExternalIdentityObjectId) &&
                db.ClientSubscriptions.Any(subscription =>
                    subscription.ClientProfileId == profile.Id &&
                    subscription.Status == ClientSubscriptionStatus.Active))
            .OrderBy(profile => profile.UpdatedUtc)
            .Select(profile => profile.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var recovered = 0;
        foreach (var clientId in clientIds)
        {
            try
            {
                await lifecycle.EnsureClientIdentityAsync(clientId, cancellationToken);
                recovered++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Active client Entra provisioning remains pending. ClientProfileId={ClientProfileId}",
                    clientId);
            }
        }

        return recovered;
    }
}
