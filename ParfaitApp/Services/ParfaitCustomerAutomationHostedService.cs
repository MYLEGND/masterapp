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
                var automations = scope.ServiceProvider.GetRequiredService<ParfaitCustomerAutomationService>();
                var mail = scope.ServiceProvider.GetRequiredService<IGraphMailService>();

                var dueDispatches = automations.GetDueDispatchCandidates();
                foreach (var candidate in dueDispatches)
                {
                    try
                    {
                        await mail.SendAutomationEmailAsync(
                            candidate.ToEmail,
                            candidate.Subject,
                            candidate.HtmlBody,
                            stoppingToken);

                        automations.MarkDispatchSent(candidate);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Automation email failed. Workflow:{WorkflowName} Recipient:{RecipientEmail}",
                            candidate.WorkflowName,
                            candidate.ToEmail);

                        automations.MarkDispatchFailed(candidate, ex.Message);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Parfait customer automation hosted service failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }
}
