using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentPortal.Services;

/// <summary>
/// Delivers a small bounded batch of already-staged partner invitations. This
/// is deliberately not a data migration or reconciliation worker.
/// </summary>
public sealed class HouseholdPartnerInvitationDeliveryHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HouseholdPartnerInvitationDeliveryHostedService> _logger;

    public HouseholdPartnerInvitationDeliveryHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<HouseholdPartnerInvitationDeliveryHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var delivery = scope.ServiceProvider.GetRequiredService<HouseholdPartnerInvitationDeliveryService>();
                await delivery.DeliverDueAsync(25, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Household partner invitation delivery cycle failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
