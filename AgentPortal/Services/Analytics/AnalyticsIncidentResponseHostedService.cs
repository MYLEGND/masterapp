namespace AgentPortal.Services.Analytics;

public sealed class AnalyticsIncidentResponseHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AnalyticsIncidentResponseHostedService> _logger;

    public AnalyticsIncidentResponseHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<AnalyticsIncidentResponseHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IAnalyticsIncidentQueryService>();
                var activeCount = await service.RefreshSystemIncidentsAsync(stoppingToken);
                _logger.LogInformation("Analytics incident monitor refreshed. activeIncidentCount={ActiveIncidentCount}", activeCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Analytics incident monitor refresh failed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
