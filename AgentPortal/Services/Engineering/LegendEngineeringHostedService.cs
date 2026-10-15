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
        // Monitoring/reconciliation is always live. The Founder operational contract
        // controls model execution and autonomous agent starts without disabling the
        // deterministic incident/release loop.
        var intervalSeconds = Math.Clamp(
            configuration.GetValue<int?>("LegendEngineering:Autonomous:ScanIntervalSeconds") ?? 60,
            15,
            900);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        await RunPassAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunPassAsync(stoppingToken);
    }

    private async Task RunPassAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<ILegendEngineeringOrchestrator>();
            var store = scope.ServiceProvider.GetRequiredService<LegendEngineeringStateStore>();
            var adapter = scope.ServiceProvider.GetRequiredService<ILegendEngineeringAgentAdapter>();
            var contractAuthority = scope.ServiceProvider.GetRequiredService<ILegendEngineeringContractAuthority>();
            var releasePlanner = scope.ServiceProvider.GetRequiredService<LegendEngineeringReleaseCohortPlanner>();
            var founderNotifications = scope.ServiceProvider.GetRequiredService<LegendEngineeringFounderNotificationService>();

            // Runtime incident ingestion is observational. Its failure must not
            // prevent the independent deterministic CI/release reconciliation.
            // A release reconciliation failure still stops downstream agent starts.
            await RunIndependentIncidentAndReleasePassAsync(
                token => orchestrator.ProcessIncidentsAsync(
                    Math.Clamp(configuration.GetValue<int?>("LegendEngineering:Autonomous:IncidentScanLimit") ?? 250, 1, 1000),
                    token),
                token => releasePlanner.ReconcileAndReleaseAsync(token),
                logger,
                cancellationToken);

            // The work-item authority, not the scheduler, owns restart recovery.
            // Any abandoned exact lease is restored before new work is considered.
            await store.RecoverExpiredLeasesAsync(DateTime.UtcNow, cancellationToken);

            var contract = await contractAuthority.GetCurrentAsync(cancellationToken);
            var status = JsonSerializer.SerializeToElement(await adapter.GetStatusAsync(cancellationToken));
            if (contract.AutonomousEngineeringEnabled && contract.ModelExecutionEnabled &&
                RuntimeReconciliationDue(status, DateTime.UtcNow))
            {
                await adapter.ReconcileRuntimeAsync(force: false, cancellationToken);
                status = JsonSerializer.SerializeToElement(
                    await adapter.GetStatusAsync(cancellationToken));
            }
            var runtimeReady = status.TryGetProperty("runtimeReady", out var readyValue) &&
                               readyValue.ValueKind == JsonValueKind.True;
            var autonomyEnabled = contract.AutonomousEngineeringEnabled && contract.ModelExecutionEnabled;
            if (runtimeReady)
                await store.ActivateProviderWaitingWorkAsync(
                    DateTime.UtcNow,
                    releaseProviderControlBlocks: true,
                    cancellationToken);
            var blocker = autonomyEnabled && runtimeReady
                ? null
                : !contract.AutonomousEngineeringEnabled
                    ? "engineering_autonomous_execution_paused"
                    : !contract.ModelExecutionEnabled
                        ? "engineering_operational_execution_paused"
                        : status.TryGetProperty("eligibility", out var eligibility) &&
                          eligibility.ValueKind == JsonValueKind.String
                            ? eligibility.GetString()
                            : "chatgpt_plan_executor_not_ready";

            var openWork = await store.GetOpenWorkItemsAsync(100, cancellationToken);
            var blockerEpisodeId =
                status.TryGetProperty("providerCircuitEpisodeId", out var blockerEpisode) &&
                blockerEpisode.ValueKind == JsonValueKind.String
                    ? blockerEpisode.GetString()
                    : null;
            var recoveredEpisodeId =
                status.TryGetProperty("providerRecoveredEpisodeId", out var recoveredEpisode) &&
                recoveredEpisode.ValueKind == JsonValueKind.String
                    ? recoveredEpisode.GetString()
                    : null;
            await RunNonAuthoritativeObservationAsync(
                token => founderNotifications.NotifyActionableAsync(
                    openWork, blocker, blockerEpisodeId, recoveredEpisodeId, token),
                "founder_notifications", logger, cancellationToken);
            await RunNonAuthoritativeObservationAsync(
                token => founderNotifications.NotifyDailyDigestAsync(openWork, token),
                "founder_digest", logger, cancellationToken);

            if (!autonomyEnabled || !runtimeReady)
            {
                logger.LogInformation(
                    "LEGEND engineering monitoring active; autonomous model execution blocked ({Code}).",
                    blocker);
                return;
            }

            var candidates = openWork
                .Where(IsAgentActionable)
                .OrderBy(item => PriorityRank(item.PriorityClass))
                .ThenByDescending(item => item.PriorityScore)
                .ThenBy(item => item.UpdatedUtc)
                .Take(Math.Clamp(
                    configuration.GetValue<int?>("LegendEngineering:Autonomous:MaxAgentStartsPerPass") ?? 2,
                    1,
                    4))
                .ToArray();

            foreach (var item in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var context = await orchestrator.BootstrapSystemAsync(
                        item.WorkItemId,
                        item.AssignedRole,
                        cancellationToken);
                    await adapter.StartAsync(context.EngineeringContextId, cancellationToken);
                }
                catch (InvalidOperationException exception)
                {
                    logger.LogInformation(
                        "LEGEND engineering work {WorkItemId} did not start ({Code}).",
                        item.WorkItemId,
                        exception.Message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning(
                "LEGEND engineering scheduler pass failed closed ({ExceptionType}).",
                exception.GetType().Name);
        }
    }

    internal static async Task RunIndependentIncidentAndReleasePassAsync(
        Func<CancellationToken, Task<object>> ingestIncidents,
        Func<CancellationToken, Task<object>> reconcileRelease,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await RunNonAuthoritativeObservationAsync(
            async token => { await ingestIncidents(token); },
            "incident_intake", logger, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        // Never mask a failure here: the outer pass guard must block subsequent
        // agent starts if release state cannot be safely reconciled.
        await reconcileRelease(cancellationToken);
    }

    internal static async Task RunNonAuthoritativeObservationAsync(
        Func<CancellationToken, Task> observe,
        string stage,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await observe(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Never forward diagnostic or provider exception messages.
            // Notification and incident failures cannot block authoritative work.
            logger.LogWarning(
                "LEGEND engineering observation {Stage} failed ({ExceptionType}); continue independent work.",
                stage, exception.GetType().Name);
        }
    }

    private static bool IsAgentActionable(EngineeringWorkItemSnapshot item)
        => (item.LeaseExpiresUtc is null || item.LeaseExpiresUtc <= DateTime.UtcNow) &&
           item.NextRetryUtc is null &&
           item.State is
               "QUEUED" or "NEEDS_TRIAGE" or "RECURRED_NEEDS_TRIAGE" or
               "NEEDS_SUPERVISOR" or "RECURRED_NEEDS_SUPERVISOR" or "REVIEW_REQUIRED" or
               "REVIEW_REJECTED" or "CI_FAILED_NEEDS_EVIDENCE";

    private static bool RuntimeReconciliationDue(JsonElement status, DateTime nowUtc)
    {
        var blockerClass =
            status.TryGetProperty("providerBlockerClass", out var blockerClassValue) &&
            blockerClassValue.ValueKind == JsonValueKind.String
                ? blockerClassValue.GetString()
                : null;
        var hasBlocker =
            status.TryGetProperty("providerBlockerCode", out var blocker) &&
            blocker.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(blocker.GetString());
        var readinessState =
            status.TryGetProperty("readinessState", out var readiness) &&
            readiness.ValueKind == JsonValueKind.String
                ? readiness.GetString()
                : null;

        if (hasBlocker)
        {
            // A changed model binding marks readiness UNVERIFIED. That is the only
            // no-timer circuit class automatically retried after a contract change.
            if (blockerClass == "MODEL_BINDING" &&
                !string.Equals(readinessState, "READY", StringComparison.Ordinal))
                return true;

            return status.TryGetProperty("providerRetryNotBeforeUtc", out var retry) &&
                   retry.ValueKind == JsonValueKind.String &&
                   retry.TryGetDateTime(out var retryUtc) &&
                   retryUtc <= nowUtc;
        }

        if (!string.Equals(readinessState, "READY", StringComparison.Ordinal))
            return true;

        return status.TryGetProperty("readinessCheckedUtc", out var checkedValue) &&
               checkedValue.ValueKind == JsonValueKind.String &&
               checkedValue.TryGetDateTime(out var checkedUtc) &&
               checkedUtc <= nowUtc.AddHours(-6);
    }

    private static int PriorityRank(string value) => value switch
    {
        "P1" => 1,
        "P2" => 2,
        "P3" => 3,
        _ => 4
    };
}
