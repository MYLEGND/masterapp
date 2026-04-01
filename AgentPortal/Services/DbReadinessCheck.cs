using Infrastructure.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentPortal.Services;

/// <summary>
/// Readiness check: verifies the database is reachable before the load balancer
/// routes traffic to this instance. Used by /readyz only, not /healthz.
/// </summary>
public sealed class DbReadinessCheck : IHealthCheck
{
    private readonly MasterAppDbContext _db;

    public DbReadinessCheck(MasterAppDbContext db)
    {
        _db = db;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await _db.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Database did not respond to connection check.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database connection check threw an exception.", ex);
        }
    }
}
