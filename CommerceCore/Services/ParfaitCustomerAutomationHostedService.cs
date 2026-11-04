namespace ParfaitApp.Services;

public sealed class ParfaitCustomerAutomationHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ParfaitCustomerAutomationHostedService> _logger;

    public ParfaitCustomerAutomationHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<ParfaitCustomerAutomationHostedService> logger)
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
                await using var dispatcherLease = await scope.ServiceProvider
                    .GetRequiredService<Legend.Commerce.ICommerceAutomationDispatchLease>()
                    .TryAcquireAsync(stoppingToken);
                if (dispatcherLease is not null)
                {
                var automations = scope.ServiceProvider.GetRequiredService<ParfaitCustomerAutomationService>();
                var mail = scope.ServiceProvider.GetRequiredService<IGraphMailService>();

                var dueDispatches = automations.GetDueDispatchCandidatesForAllBusinesses();
                foreach (var candidate in dueDispatches)
                {
                    if (!automations.TryBeginDispatch(candidate)) continue;
                    try
                    {
                        await mail.SendAutomationEmailAsync(
                            candidate.CommerceBusinessId,
                            candidate.ToEmail,
                            candidate.Subject,
                            candidate.HtmlBody,
                            stoppingToken);

                        automations.MarkDispatchSent(candidate);
                    }
                    catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode is >= 400 and < 500 and not 408)
                    {
                        automations.MarkDispatchRejected(candidate, ex.ResponseStatusCode);
                        _logger.LogWarning("Automation request rejected. Delivery:{DeliveryId} Status:{Status}", candidate.DeliveryId, ex.ResponseStatusCode);
                    }
                    catch (Exception)
                    {
                        automations.MarkDispatchFailed(candidate, "Outcome unknown");
                        _logger.LogWarning("Automation outcome requires review. Delivery:{DeliveryId}", candidate.DeliveryId);
                    }
                }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Commerce customer automation hosted service failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }
}
