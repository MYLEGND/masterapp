using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.WebsiteEditing;

/// <summary>Refreshes provider evidence; stale evidence is rejected by the resolver.</summary>
public sealed class WebsiteDomainHealthWorker(IServiceScopeFactory scopes, ILogger<WebsiteDomainHealthWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
                var service = scope.ServiceProvider.GetRequiredService<WebsiteDomainService>();
                var cutoff = DateTime.UtcNow.AddHours(-1);
                var rows = await db.Set<WebsiteDomainBinding>().AsNoTracking()
                    .Where(x => x.Status != "removing" && (x.LastCheckedUtc == null || x.LastCheckedUtc < cutoff))
                    .OrderBy(x => x.LastCheckedUtc).Take(30).Select(x => new { x.Id, x.CommerceBusinessId }).ToListAsync(stoppingToken);
                foreach (var row in rows)
                {
                    try { await service.RefreshAsync(row.CommerceBusinessId, row.Id, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception ex) { logger.LogWarning("Website domain refresh failed for {BindingId}: {FailureType}", row.Id, ex.GetType().Name); db.ChangeTracker.Clear(); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning("Website domain health unavailable: {FailureType}", ex.GetType().Name); }
        }
    }
}
