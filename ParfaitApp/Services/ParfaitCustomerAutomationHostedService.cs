namespace ParfaitApp.Services;

public sealed class ParfaitCustomerAutomationHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ParfaitCustomerAutomationService _automations;
    private readonly ILogger<ParfaitCustomerAutomationHostedService> _logger;

    public ParfaitCustomerAutomationHostedService(
        IServiceScopeFactory scopeFactory,
        ParfaitCustomerAutomationService automations,
        ILogger<ParfaitCustomerAutomationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _automations = automations;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dueDispatches = _automations.GetDueDispatchCandidates();
                if (dueDispatches.Count > 0)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var mail = scope.ServiceProvider.GetRequiredService<IGraphMailService>();

                    foreach (var candidate in dueDispatches)
                    {
                        try
                        {
                            await mail.SendAutomationEmailAsync(
                                candidate.ToEmail,
                                candidate.Subject,
                                candidate.HtmlBody,
                                stoppingToken);
                            _automations.MarkDispatchSent(candidate);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "Automation email failed. Workflow:{WorkflowName} Recipient:{RecipientEmail}",
                                candidate.WorkflowName,
                                candidate.ToEmail);
                            _automations.MarkDispatchFailed(candidate, ex.Message);
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
                _logger.LogError(ex, "Parfait customer automation hosted service failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }
}
