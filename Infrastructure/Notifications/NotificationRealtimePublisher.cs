using Domain.Messaging;
using Microsoft.AspNetCore.SignalR;
using Shared.Messaging;

namespace Infrastructure.Notifications;

internal sealed class NotificationRealtimePublisher : INotificationRealtimePublisher
{
    private readonly IHubContext<MessagingHub> _hubContext;

    public NotificationRealtimePublisher(IHubContext<MessagingHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishAsync(
        MessagingActor recipient,
        NotificationRealtimeEvent notification,
        CancellationToken cancellationToken = default) =>
        _hubContext.Clients
            .Group(MessagingHub.GroupName(recipient.UserId, recipient.ParticipantType))
            .SendAsync("notificationUpdated", notification, cancellationToken);
}
