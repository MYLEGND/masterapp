using System.Text.Json;
using Domain.Engineering;

namespace AgentPortal.Services.Engineering;

internal sealed class LegendEngineeringHostedService(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<LegendEngineeringHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (configuration.GetValue<bool?>("LegendEngineering:Autonomous:Enabled") != true) return;
        var intervalSeconds = Math.Clamp(configuration.GetValue<int?>("LegendEngineering:Autonomous:ScanIntervalSeconds") ?? 60, 15, 900);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        await RunPassAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await RunPassAsync(stoppingToken);
    }

    private async Task RunPassAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<ILegendEngineeringOrchestrator>();
            var store = scope.ServiceProvider.GetRequiredService<LegendEngineeringStateStore>();
            var adapter = scope.ServiceProvider.GetRequiredService<ILegendEngineeringAgentAdapter>();
            var releasePlanner = scope.ServiceProvider.GetRequiredService<LegendEngineeringReleaseCohortPlanner>();

            await orchestrator.ProcessIncidentsAsync(
                Math.Clamp(configuration.GetValue<int?>("LegendEngineering:Autonomous:IncidentScanLimit") ?? 250, 1, 1000),
                cancellationToken);

            // CI/release reconciliation is deterministic and remains active even
            // when ChatGPT plan execution is unavailable.
            await releasePlanner.ReconcileAndReleaseAsync(cancellationToken);

            var status = JsonSerializer.SerializeToElement(await adapter.GetStatusAsync(cancellationToken));
            var ready = status.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
            if (!ready)
            {
                var code = status.TryGetProperty("eligibility", out var eligibility) && eligibility.ValueKind == JsonValueKind.String
                    ? eligibility.GetString()
                    : "chatgpt_plan_executor_not_ready";
                logger.LogInformation("LEGEND engineering monitoring active; autonomous model execution blocked ({Code}).", code);
                return;
            }

            var candidates = (await store.GetOpenWorkItemsAsync(100, cancellationToken))
                .Where(IsAgentActionable)
                .OrderBy(item => PriorityRank(item.PriorityClass))
                .ThenByDescending(item => item.PriorityScore)
                .ThenBy(item => item.UpdatedUtc)
                .Take(Math.Clamp(configuration.GetValue<int?>("LegendEngineering:Autonomous:MaxAgentStartsPerPass") ?? 2, 1, 4))
                .ToArray();

            foreach (var item in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var context = await orchestrator.BootstrapSystemAsync(item.WorkItemId, item.AssignedRole, cancellationToken);
                    await adapter.StartAsync(context.EngineeringContextId, cancellationToken);
                }
                catch (InvalidOperationException exception)
                {
                    logger.LogInformation("LEGEND engineering work {WorkItemId} did not start ({Code}).", item.WorkItemId, exception.Message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning("LEGEND engineering scheduler pass failed closed ({ExceptionType}).", exception.GetType().Name);
        }
    }

    private static bool IsAgentActionable(EngineeringWorkItemSnapshot item)
        => item.LeaseExpiresUtc <= DateTime.UtcNow && item.State is
            "QUEUED" or "NEEDS_SUPERVISOR" or "RECURRED_NEEDS_SUPERVISOR" or "REVIEW_REQUIRED" or "REVIEW_REJECTED";

    private static int PriorityRank(string value) => value switch { "P1" => 1, "P2" => 2, "P3" => 3, _ => 4 };
}