namespace AgentPortal.Services;

public sealed class ClientBillingNotificationDeliveryHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClientBillingNotificationDeliveryHostedService> _logger;

    public ClientBillingNotificationDeliveryHostedService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ClientBillingNotificationDeliveryHostedService> logger)
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
                if (IsEnabled())
                {
                    using var scope = _scopeFactory.CreateScope();
                    var delivery = scope.ServiceProvider.GetRequiredService<ClientBillingNotificationDeliveryService>();
                    var result = await delivery.DeliverDueAsync(25, stoppingToken);
                    if (result.Selected > 0)
                    {
                        _logger.LogInformation(
                            "Billing notification delivery selected {Selected}, sent {Sent}, and deferred {Failed} notifications.",
                            result.Selected,
                            result.Sent,
                            result.Failed);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Billing notification delivery worker run failed.");
            }

            await Task.Delay(GetInterval(), stoppingToken);
        }
    }

    private bool IsEnabled() =>
        !string.Equals(_configuration["Billing:Notifications:Enabled"], "false", StringComparison.OrdinalIgnoreCase);

    private TimeSpan GetInterval()
    {
        return int.TryParse(_configuration["Billing:Notifications:IntervalSeconds"], out var seconds) && seconds >= 30
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(3);
    }
}
