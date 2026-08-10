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

    /// <summary>
    /// Returns only the current typed actor's safe APNs registration and
    /// delivery projection. Opaque device tokens and credential material never
    /// cross this authenticated boundary.
    /// </summary>
    [HttpGet("devices/apns/status")]
    public async Task<IActionResult> ApnsStatus(CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var status = await _notifications.GetPushDiagnosticAsync(
            resolved.Actor!.Actor,
            cancellationToken);
        return Ok(new MobilePushDiagnosticDto(
            status.RegistrationState,
            status.Environment,
            status.LastRegistrationUtc,
            status.LastRegistrationResult,
            status.LastDeliveryUtc,
            status.DeliveryState,
            status.LastApnsStatus,
            status.LastApnsReason,
            status.DeliveryAttemptCount));
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
                request.Environment ?? string.Empty,
                cancellationToken);
        }
        catch (ArgumentException exception) when (exception.ParamName == "environment")
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "mobile_notification_environment_invalid",
                "The APNs environment must be sandbox or production.");
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

    [HttpDelete("devices/apns")]
    public async Task<IActionResult> DeactivateApnsDevice(
        [FromBody] MobileApnsDeviceRemovalRequest? request,
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
            await _notifications.DeactivateDeviceAsync(
                resolved.Actor!.Actor,
                request.DeviceToken,
                cancellationToken);
        }
        catch (ArgumentException)
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "mobile_notification_device_invalid",
                "The APNs device token is invalid.");
        }

        return NoContent();
    }

    /// <summary>
    /// Registers Android's opaque FCM transport token against the current typed
    /// actor. Notification content, language, targeting, and badges continue to
    /// originate exclusively from the server notification ledger.
    /// </summary>
    [HttpPut("devices/fcm")]
    public async Task<IActionResult> RegisterFcmDevice(
        [FromBody] MobileFcmDeviceRegistrationRequest? request,
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
                "An FCM device token is required.");
        }

        try
        {
            await _notifications.RegisterFcmDeviceAsync(
                resolved.Actor!.Actor,
                request.DeviceToken,
                cancellationToken);
        }
        catch (ArgumentException)
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "mobile_notification_device_invalid",
                "The FCM device token is invalid.");
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

    [HttpDelete("devices/fcm")]
    public async Task<IActionResult> DeactivateFcmDevice(
        [FromBody] MobileFcmDeviceRemovalRequest? request,
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
                "An FCM device token is required.");
        }

        try
        {
            await _notifications.DeactivateFcmDeviceAsync(
                resolved.Actor!.Actor,
                request.DeviceToken,
                cancellationToken);
        }
        catch (ArgumentException)
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "mobile_notification_device_invalid",
                "The FCM device token is invalid.");
        }

        return NoContent();
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

public sealed record MobileApnsDeviceRemovalRequest(string? DeviceToken);

public sealed record MobileFcmDeviceRegistrationRequest(string? DeviceToken);

public sealed record MobileFcmDeviceRemovalRequest(string? DeviceToken);

public sealed record MobilePushDiagnosticDto(
    string RegistrationState,
    string? Environment,
    DateTime? LastRegistrationUtc,
    string LastRegistrationResult,
    DateTime? LastDeliveryUtc,
    string DeliveryState,
    int? LastApnsStatus,
    string? LastApnsReason,
    int? DeliveryAttemptCount);
