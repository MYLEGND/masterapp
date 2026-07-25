using Domain.Messaging;
using Microsoft.AspNetCore.SignalR;
using Shared.Messaging;

namespace Infrastructure.Messaging;

internal sealed class MessagingRealtimePublisher : IMessagingRealtimePublisher
{
    private readonly IHubContext<MessagingHub> _hubContext;

    public MessagingRealtimePublisher(IHubContext<MessagingHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishAsync(
        MessagingRealtimeEvent notification,
        CancellationToken cancellationToken = default)
    {
        var groups = notification.Recipients
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.UserId) &&
                !string.IsNullOrWhiteSpace(x.ParticipantType))
            .Select(x => MessagingHub.GroupName(x.UserId, x.ParticipantType))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (groups.Length == 0)
            return Task.CompletedTask;

        return _hubContext.Clients.Groups(groups)
            .SendAsync(notification.EventType, notification, cancellationToken);
    }
}
