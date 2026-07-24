using Domain.Billing;
using Infrastructure.Billing;
using Infrastructure.Data;
using Infrastructure.Billing.Square;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

public sealed class BillingReconciliationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BillingReconciliationHostedService> _logger;

    public BillingReconciliationHostedService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<BillingReconciliationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Billing reconciliation worker run failed.");
            }

            await Task.Delay(GetInterval(), stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled())
            return;

        using var scope = _scopeFactory.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<SquareBillingOptions>();
        var billingOrchestrator = scope.ServiceProvider.GetRequiredService<IBillingOrchestrator>();
        var reconciliationService = scope.ServiceProvider.GetRequiredService<IBillingReconciliationService>();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var recurringBilling = await billingOrchestrator.ProcessDueClientSubscriptionRenewalsAsync(
            25,
            $"{Environment.MachineName}:{Environment.ProcessId}",
            cancellationToken);

        if (!options.HasServerCredentials())
        {
            if (recurringBilling.DueSubscriptions > 0)
            {
                _logger.LogWarning(
                    "Platform recurring billing processed {DueSubscriptions} due subscriptions without configured Square server credentials. Attempts succeeded={Succeeded}, failed={Failed}, ended={Ended}.",
                    recurringBilling.DueSubscriptions,
                    recurringBilling.ChargesSucceeded,
                    recurringBilling.ChargesFailed,
                    recurringBilling.EndedAtPeriodBoundary);
            }

            return;
        }

        var processedEvents = await reconciliationService.ReconcilePendingProviderEventsAsync(25, cancellationToken);

        var reconciliationRequiredSubscriptionIds = await db.ClientSubscriptions
            .AsNoTracking()
            .Where(x => x.IsPlatformManaged && x.Status == ClientSubscriptionStatus.ReconciliationRequired)
            .OrderBy(x => x.UpdatedUtc)
            .Select(x => x.Id)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var subscriptionId in reconciliationRequiredSubscriptionIds)
        {
            await reconciliationService.ReconcileSubscriptionAsync(subscriptionId, correlationId: null, cancellationToken);
        }

        if (processedEvents > 0 || reconciliationRequiredSubscriptionIds.Count > 0 || recurringBilling.DueSubscriptions > 0)
        {
            _logger.LogInformation(
                "Billing reconciliation processed {ProcessedEvents} provider events, refreshed {SubscriptionCount} reconciliation-required subscriptions, and handled {DueSubscriptions} due renewals (succeeded={Succeeded}, failed={Failed}, ended={Ended}).",
                processedEvents,
                reconciliationRequiredSubscriptionIds.Count,
                recurringBilling.DueSubscriptions,
                recurringBilling.ChargesSucceeded,
                recurringBilling.ChargesFailed,
                recurringBilling.EndedAtPeriodBoundary);
        }
    }

    private bool IsEnabled()
    {
        var configured = _configuration["Billing:Reconciliation:Enabled"];
        return !string.Equals(configured, "false", StringComparison.OrdinalIgnoreCase);
    }

    private TimeSpan GetInterval()
    {
        if (int.TryParse(_configuration["Billing:Reconciliation:IntervalSeconds"], out var seconds) && seconds >= 30)
            return TimeSpan.FromSeconds(seconds);

        return TimeSpan.FromMinutes(3);
    }
}
