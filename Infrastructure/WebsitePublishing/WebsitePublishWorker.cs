using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Infrastructure.WebsiteEditing.Controllers;

namespace Infrastructure.WebsitePublishing;

/// <summary>Runs the same publishing authority after rechecking the scheduled actor's current access.</summary>
public sealed class WebsitePublishWorker(IServiceScopeFactory scopes, ILogger<WebsitePublishWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await RunDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Scheduled website publish scan failed."); }
        }
    }

    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        using var scan = scopes.CreateScope();
        var database = scan.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var ids = await database.Set<WebsiteContentState>().AsNoTracking().Where(s => s.ScheduledPublishUtc <= DateTime.UtcNow)
            .OrderBy(s => s.ScheduledPublishUtc).Select(s => s.Id).Take(20).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            var state = await db.Set<WebsiteContentState>().SingleAsync(s => s.Id == id, cancellationToken);
            if (!state.ScheduledPublishUtc.HasValue || state.ScheduledPublishUtc > DateTime.UtcNow) continue;
            try
            {
                if (state.ScheduledRevision != state.Revision) throw new InvalidOperationException("The scheduled draft changed. Review and schedule again.");
                var actor = JsonSerializer.Deserialize<WebsiteEditorTicket>(state.ScheduledActorJson ?? "", new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidOperationException("Scheduled owner is unavailable.");
                var tickets = scope.ServiceProvider.GetRequiredService<WebsiteEditorTicketProtector>();
                var token = tickets.Protect(actor with { ExpiresUtc = DateTime.UtcNow.AddMinutes(5) });
                var controller = new WebsitePlatformController(db, tickets, scope.ServiceProvider.GetRequiredService<IConfiguration>())
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider } }
                };
                var result = await controller.Publish(new WebsitePlatformController.PublishRequest(token, state.Revision), cancellationToken);
                if (result is not OkObjectResult) throw new InvalidOperationException("Scheduled publication was rejected. Review access and draft readiness.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (DbUpdateConcurrencyException) { /* Another publisher won; never overwrite its version. */ }
            catch (Exception exception)
            {
                // Failed schedules are retained for explicit correction, never retried into a surprise release.
                await db.Entry(state).ReloadAsync(cancellationToken);
                if (!state.ScheduledPublishUtc.HasValue) continue;
                state.ScheduledPublishUtc = null;
                state.ScheduleError = "Publication failed. Review the draft and owner permissions before scheduling again.";
                state.Revision++;
                try { await db.SaveChangesAsync(cancellationToken); }
                catch (DbUpdateConcurrencyException) { }
                logger.LogWarning(exception, "Website scheduled publication failed for state {StateId}.", id);
            }
        }
    }
}
