using AgentPortal.Controllers.Api;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentPortal.Health;

/// <summary>
/// Synthetic readiness indicator for ingest endpoints (controller resolvable, dependencies injected).
/// </summary>
public sealed class IngestHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _services;

    public IngestHealthCheck(IServiceProvider services)
    {
        _services = services;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Ensure controllers can be resolved (DI graph intact)
        _ = _services.GetRequiredService<AnalyticsIngestController>();
        _ = _services.GetRequiredService<LeadSubmitController>();
        return Task.FromResult(HealthCheckResult.Healthy("Ingest controllers resolvable"));
    }
}
