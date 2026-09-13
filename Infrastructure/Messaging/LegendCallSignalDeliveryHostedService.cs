using System.Text.Json;
using Infrastructure.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Calling;
using Shared.Messaging;

namespace Infrastructure.Messaging;

// Each host delivers the canonical outbox only to its own connected actors.
// A lookback plus local deduplication handles out-of-order transaction commits;
// no global consumer claim can steal a signal from a different Azure instance.
internal sealed class LegendCallSignalDeliveryHostedService(IServiceScopeFactory scopes,
    IHubContext<MessagingHub> hub, ILogger<LegendCallSignalDeliveryHostedService> logger) : BackgroundService
{
    private readonly Dictionary<long, DateTime> _delivered = new();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var groups = LegendCallConnections.Groups;
                if (groups.Length > 0)
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
                    var now = DateTime.UtcNow;
                    var delivered = _delivered.Keys.ToArray();
                    var pending = await db.LegendCallSignals.AsNoTracking()
                        .Where(s => s.ExpiresUtc > now && groups.Contains(s.RecipientGroup) && !delivered.Contains(s.Id))
                        .OrderBy(s => s.Id).Take(2048).ToArrayAsync(stoppingToken);
                    foreach (var signal in pending)
                    {
                        if (_delivered.ContainsKey(signal.Id)) continue;
                        var value = JsonSerializer.Deserialize<LegendCallEvent>(signal.Payload);
                        if (value == null) continue;
                        await hub.Clients.Group(signal.RecipientGroup).SendAsync("callUpdated", value, stoppingToken);
                        _delivered[signal.Id] = signal.ExpiresUtc;
                    }
                    foreach (var id in _delivered.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
                        _delivered.Remove(id);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Call signaling delivery will retry."); }
            await Task.Delay(500, stoppingToken);
        }
    }
}
