using System.Threading.Channels;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

internal static class LegendCallPushWakeup
{
    private static readonly Channel<bool> Channel = System.Threading.Channels.Channel.CreateBounded<bool>(1);
    public static void Notify() => Channel.Writer.TryWrite(true);
    public static async Task WaitAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await Channel.Reader.ReadAsync(timeout.Token); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }
}

// Durable invitation metadata is in the same database as the conversation.
// Waking a worker is only an optimization; another host can recover pending work.
internal sealed class LegendCallPushDeliveryHostedService(IServiceScopeFactory scopes,
    ILogger<LegendCallPushDeliveryHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DeliverAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Legend call invitation delivery will retry."); }
            await LegendCallPushWakeup.WaitAsync(stoppingToken);
        }
    }
    private async Task DeliverAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var now = DateTime.UtcNow;
        await db.LegendCallSignals.Where(s => s.ExpiresUtc <= now).ExecuteDeleteAsync(ct);
        var waiting = await db.LegendCallSessions.AsNoTracking().Where(c => c.Status == "ringing" && c.ExpiresUtc > now &&
            c.InvitationDispatchedUtc == null && c.PushAttempts < 3 && (c.NextPushUtc == null || c.NextPushUtc <= now))
            .OrderBy(c => c.CreatedUtc).Take(20).ToArrayAsync(ct);
        foreach (var call in waiting)
        {
            // Database claim prevents two Azure instances dispatching simultaneously.
            var claimed = await db.LegendCallSessions.Where(c => c.Id == call.Id && c.Status == "ringing" && c.InvitationDispatchedUtc == null &&
                c.PushAttempts == call.PushAttempts && (c.NextPushUtc == null || c.NextPushUtc <= now))
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.PushAttempts, c => c.PushAttempts + 1)
                    .SetProperty(c => c.NextPushUtc, now.AddSeconds(12)), ct);
            if (claimed != 1) continue;
            var recipientIds = call.CalleeType == "Client"
                ? await db.ClientProfiles.Where(p => p.ClientUserId == call.CalleeUserId || p.ExternalIdentityObjectId == call.CalleeUserId)
                    .Select(p => new { p.ClientUserId, p.ExternalIdentityObjectId }).ToArrayAsync(ct)
                : [];
            var userIds = recipientIds.SelectMany(p => new[] { p.ClientUserId, p.ExternalIdentityObjectId ?? "" })
                .Append(call.CalleeUserId).Select(id => id.ToLowerInvariant()).Distinct().ToArray();
            var devices = await db.MobilePushDevices.AsNoTracking().Where(d => d.IsActive &&
                userIds.Contains(d.UserId.ToLower()) && d.ParticipantType == call.CalleeType &&
                (d.Provider == MobilePushProviders.ApnsVoip || d.Provider == MobilePushProviders.Fcm)).ToArrayAsync(ct);
            var retry = false;
            foreach (var device in devices)
            {
                if (DateTime.UtcNow >= call.ExpiresUtc) break;
                var snapshot = await scope.ServiceProvider.GetRequiredService<MessagingService>().SnapshotAsync(call, ct);
                if (device.Provider == MobilePushProviders.ApnsVoip)
                {
                    var result = await scope.ServiceProvider.GetRequiredService<IApplePushGateway>().SendAsync(
                        new(device.DeviceToken, device.Environment, call.CallerName, "Incoming Legend call", call.Id, 0, call.ConversationId, snapshot), ct);
                    retry |= result.Outcome == ApplePushDeliveryOutcome.Retry;
                    if (result.Outcome == ApplePushDeliveryOutcome.InvalidDevice)
                        await scope.ServiceProvider.GetRequiredService<INotificationEngine>().DeactivateVoipDeviceAsync(new(device.UserId, device.ParticipantType), device.DeviceToken, ct);
                }
                else
                {
                    var result = await scope.ServiceProvider.GetRequiredService<IFirebasePushGateway>().SendAsync(
                        new(device.DeviceToken, call.CallerName, "Incoming Legend call", call.Id, 0, call.ConversationId, snapshot), ct);
                    retry |= result.Outcome == FirebasePushDeliveryOutcome.Retry;
                    if (result.Outcome == FirebasePushDeliveryOutcome.InvalidDevice)
                        await scope.ServiceProvider.GetRequiredService<INotificationEngine>().DeactivateFcmDeviceAsync(new(device.UserId, device.ParticipantType), device.DeviceToken, ct);
                }
            }
            if (!retry) await db.LegendCallSessions.Where(c => c.Id == call.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.InvitationDispatchedUtc, DateTime.UtcNow), ct);
        }
    }
}
