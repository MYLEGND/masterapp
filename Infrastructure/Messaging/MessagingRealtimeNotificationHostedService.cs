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
    private readonly Queue<Guid> _recentMessageOrder = new();
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
        var messagingService = scope.ServiceProvider.GetRequiredService<IMessagingService>();
        var messages = await db.InternalMessages
            .AsNoTracking()
            .Where(x => x.SentUtc >= _lastSeenUtc)
            .OrderBy(x => x.SentUtc)
            .Take(100)
            .Select(x => new PendingMessage(x.Id, x.ConversationId, x.SentUtc))
            .ToListAsync(cancellationToken);
        if (messages.Count == 0)
            return;

        foreach (var message in messages)
        {
            _lastSeenUtc = message.SentUtc;
            if (!RememberMessage(message.Id))
                continue;

            var participants = await db.MessageConversationParticipants
                .AsNoTracking()
                .Where(x => x.ConversationId == message.ConversationId && x.IsActive)
                .OrderBy(x => x.JoinedUtc)
                .Select(x => new PendingParticipant(x.UserId, x.ParticipantType))
                .ToListAsync(cancellationToken);

            MessagingConversationDetail? conversation = null;
            foreach (var participant in participants)
            {
                var result = await messagingService.GetConversationAsync(
                    new MessagingActor(participant.UserId, participant.ParticipantType),
                    message.ConversationId,
                    cancellationToken);
                if (result.Succeeded)
                {
                    conversation = result.Conversation;
                    break;
                }
            }

            if (conversation is not null)
            {
                await _realtimePublisher.PublishAsync(
                    new MessagingRealtimeEvent(
                        "messageReceived",
                        conversation.Id,
                        message.Id,
                        message.SentUtc,
                        conversation.Participants.Select(x => x.UserId).ToArray()),
                    cancellationToken);
            }
        }
    }

    private bool RememberMessage(Guid messageId)
    {
        if (!_recentMessageIds.Add(messageId))
            return false;

        _recentMessageOrder.Enqueue(messageId);
        while (_recentMessageOrder.Count > 1_000)
        {
            _recentMessageIds.Remove(_recentMessageOrder.Dequeue());
        }

        return true;
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
