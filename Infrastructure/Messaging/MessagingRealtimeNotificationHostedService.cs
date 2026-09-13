using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

internal sealed class MessagingRealtimeNotificationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMessagingRealtimePublisher _realtimePublisher;
    private readonly ILogger<MessagingRealtimeNotificationHostedService> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly HashSet<Guid> _recentMessageIds = new();
    private DateTime _lastSeenUtc;

    public MessagingRealtimeNotificationHostedService(
        IServiceScopeFactory scopeFactory,
        IMessagingRealtimePublisher realtimePublisher,
        IConfiguration configuration,
        ILogger<MessagingRealtimeNotificationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _realtimePublisher = realtimePublisher;
        _logger = logger;
        _pollInterval = TimeSpan.FromSeconds(ParsePollingIntervalSeconds(configuration["Messaging:RealtimePollingSeconds"]));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastSeenUtc = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishPendingMessagesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Messaging realtime notification poll failed.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PublishPendingMessagesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var remembered = _recentMessageIds.ToArray();
        var messages = await db.InternalMessages
            .AsNoTracking()
            .Where(x => x.SentUtc >= _lastSeenUtc && !remembered.Contains(x.Id))
            .OrderBy(x => x.SentUtc)
            .Take(100)
            .Select(x => new PendingMessage(x.Id, x.ConversationId, x.SentUtc))
            .ToListAsync(cancellationToken);
        if (messages.Count == 0)
            return;

        foreach (var message in messages)
        {
            var participants = await db.MessageConversationParticipants
                .AsNoTracking()
                .Where(x => x.ConversationId == message.ConversationId && x.IsActive)
                .OrderBy(x => x.JoinedUtc)
                .Select(x => new PendingParticipant(x.UserId, x.ParticipantType))
                .ToListAsync(cancellationToken);

            await _realtimePublisher.PublishAsync(
                new MessagingRealtimeEvent(
                    "messageReceived", message.ConversationId, message.Id, message.SentUtc,
                    participants.Select(participant => new MessagingRealtimeRecipient(
                        participant.UserId, participant.ParticipantType)).ToArray()),
                cancellationToken);
            // Only successful publication advances the cursor. Excluding remembered
            // IDs in SQL lets subsequent pages drain identical SentUtc timestamps.
            if (message.SentUtc > _lastSeenUtc)
                _recentMessageIds.Clear();
            _lastSeenUtc = message.SentUtc;
            _recentMessageIds.Add(message.Id);
        }
    }

    private static int ParsePollingIntervalSeconds(string? configuredValue)
    {
        return int.TryParse(configuredValue, out var seconds)
            ? Math.Clamp(seconds, 1, 30)
            : 2;
    }

    private sealed record PendingMessage(Guid Id, Guid ConversationId, DateTime SentUtc);

    private sealed record PendingParticipant(string UserId, string ParticipantType);
}
