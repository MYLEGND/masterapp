using Infrastructure.Notifications;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Mobile;

/// <summary>
/// The only mobile notification boundary. All counts originate from the
/// database notification ledger; no client feature supplies a badge total.
/// </summary>
[ApiController]
[Route("api/v1/mobile/notifications")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileNotificationsController : MobileApiControllerBase
{
    private readonly INotificationEngine _notifications;

    public MobileNotificationsController(
        IMobileActorResolver actorResolver,
        INotificationEngine notifications)
        : base(actorResolver)
    {
        _notifications = notifications;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? take, CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var snapshot = await _notifications.GetSnapshotAsync(
            resolved.Actor!.Actor,
            take ?? 50,
            cancellationToken);
        return Ok(ToResponse(snapshot));
    }

    [HttpGet("unread-count")]
    public async Task<IActionResult> UnreadCount(CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var snapshot = await _notifications.GetSnapshotAsync(
            resolved.Actor!.Actor,
            take: 1,
            cancellationToken);
        return Ok(new MobileNotificationUnreadCountDto(
            snapshot.Badge.UnreadCount,
            snapshot.Badge.Revision,
            snapshot.Badge.UpdatedUtc));
    }

    [HttpPost("{notificationId:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid notificationId, CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var badge = await _notifications.MarkReadAndPublishAsync(
            resolved.Actor!.Actor,
            notificationId,
            cancellationToken);
        return Ok(new MobileNotificationUnreadCountDto(
            badge.UnreadCount,
            badge.Revision,
            badge.UpdatedUtc));
    }

    [HttpPost("clear-badges")]
    public async Task<IActionResult> ClearBadges(CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var badge = await _notifications.ClearBadgeAndPublishAsync(
            resolved.Actor!.Actor,
            cancellationToken);
        return Ok(new MobileNotificationUnreadCountDto(
            badge.UnreadCount,
            badge.Revision,
            badge.UpdatedUtc));
    }

    [HttpPut("devices/apns")]
    public async Task<IActionResult> RegisterApnsDevice(
        [FromBody] MobileApnsDeviceRegistrationRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        if (string.IsNullOrWhiteSpace(request?.DeviceToken))
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "mobile_notification_device_required",
                "An APNs device token is required.");
        }

        try
        {
            await _notifications.RegisterDeviceAsync(
                resolved.Actor!.Actor,
                request.DeviceToken,
                request.Environment ?? "production",
                cancellationToken);
        }
        catch (ArgumentException)
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "mobile_notification_device_invalid",
                "The APNs device token is invalid.");
        }

        var snapshot = await _notifications.GetSnapshotAsync(
            resolved.Actor!.Actor,
            take: 1,
            cancellationToken);
        return Ok(new MobileNotificationUnreadCountDto(
            snapshot.Badge.UnreadCount,
            snapshot.Badge.Revision,
            snapshot.Badge.UpdatedUtc));
    }

    private static MobileNotificationSnapshotDto ToResponse(NotificationSnapshot snapshot) => new(
        new MobileNotificationUnreadCountDto(
            snapshot.Badge.UnreadCount,
            snapshot.Badge.Revision,
            snapshot.Badge.UpdatedUtc),
        snapshot.Notifications.Select(notification => new MobileNotificationDto(
            notification.Id,
            notification.Kind,
            notification.Title,
            notification.Detail,
            notification.ConversationId,
            notification.OccurredUtc,
            notification.IsRead,
            notification.IsCleared)).ToArray());
}

public sealed record MobileNotificationUnreadCountDto(
    int UnreadCount,
    long Revision,
    DateTime UpdatedUtc);

public sealed record MobileNotificationDto(
    Guid Id,
    string Kind,
    string Title,
    string Detail,
    Guid? ConversationId,
    DateTime OccurredUtc,
    bool IsRead,
    bool IsCleared);

public sealed record MobileNotificationSnapshotDto(
    MobileNotificationUnreadCountDto Badge,
    IReadOnlyList<MobileNotificationDto> Notifications);

public sealed record MobileApnsDeviceRegistrationRequest(
    string? DeviceToken,
    string? Environment = null);
