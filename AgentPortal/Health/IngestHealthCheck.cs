using AgentPortal.Controllers.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentPortal.Health;

/// <summary>
/// Synthetic readiness indicator for ingest endpoints (controller resolvable, dependencies injected).
/// </summary>
public sealed class IngestHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;

    public IngestHealthCheck(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // MVC discovers controllers but does not register them as services. Activate each
        // through its scoped dependency graph without adding duplicate controller services.
        using var scope = _scopeFactory.CreateScope();
        _ = ActivatorUtilities.CreateInstance<AnalyticsIngestController>(scope.ServiceProvider);
        _ = ActivatorUtilities.CreateInstance<LeadSubmitController>(scope.ServiceProvider);
        return Task.FromResult(HealthCheckResult.Healthy("Ingest controllers resolvable"));
    }
}
