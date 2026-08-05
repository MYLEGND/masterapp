using Infrastructure.Identity;

namespace AgentPortal.Services;

/// <summary>
/// Bounded background delivery of durable account-closure requests. The
/// executor's database lease, not process memory, provides restart and
/// multi-instance safety.
/// </summary>
public sealed class AccountClosureHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AccountClosureHostedService> _logger;

    public AccountClosureHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<AccountClosureHostedService> logger)
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
                var closure = scope.ServiceProvider.GetRequiredService<IAccountClosureService>();
                var closedCount = await closure.ProcessPendingAsync(10, stoppingToken);
                if (closedCount > 0)
                    _logger.LogInformation("Completed {ClosedCount} pending account closures.", closedCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Account closure worker run failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
