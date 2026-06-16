using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Controllers.API;

[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("api/graph/calendar-webhook")]
public sealed class GraphCalendarWebhookController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly MasterAppDbContext _db;
    private readonly ILogger<GraphCalendarWebhookController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    public GraphCalendarWebhookController(
        MasterAppDbContext db,
        ILogger<GraphCalendarWebhookController> logger,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _logger = logger;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    [HttpPost]
    public async Task<IActionResult> Post(
        [FromQuery] string? validationToken,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(validationToken))
        {
            return Content(validationToken, "text/plain");
        }

        string rawBody;
        using (var reader = new StreamReader(Request.Body))
        {
            rawBody = await reader.ReadToEndAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(rawBody))
        {
            _db.AppointmentSyncLogs.Add(new AppointmentSyncLog
            {
                Id = Guid.NewGuid(),
                Operation = "empty_body",
                Source = LeadAppointmentBookingSources.MicrosoftGraphWebhook,
                Success = false,
                Error = "Microsoft Graph webhook POST body was empty.",
                CreatedUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(cancellationToken);
            return Accepted();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawBody);
        }
        catch (Exception ex)
        {
            _db.AppointmentSyncLogs.Add(new AppointmentSyncLog
            {
                Id = Guid.NewGuid(),
                Operation = "parse_failed",
                Source = LeadAppointmentBookingSources.MicrosoftGraphWebhook,
                Success = false,
                Error = ex.Message,
                DiagnosticJson = rawBody.Length > 3500 ? rawBody[..3500] : rawBody,
                CreatedUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(cancellationToken);
            return Accepted();
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("value", out var notifications) ||
                notifications.ValueKind != JsonValueKind.Array)
            {
                _db.AppointmentSyncLogs.Add(new AppointmentSyncLog
                {
                    Id = Guid.NewGuid(),
                    Operation = "missing_value",
                    Source = LeadAppointmentBookingSources.MicrosoftGraphWebhook,
                    Success = false,
                    Error = "Microsoft Graph webhook payload did not contain a value array.",
                    DiagnosticJson = rawBody.Length > 3500 ? rawBody[..3500] : rawBody,
                    CreatedUtc = DateTime.UtcNow
                });
                await _db.SaveChangesAsync(cancellationToken);
                return Accepted();
            }

            foreach (var notification in notifications.EnumerateArray())
            {
                await HandleNotificationAsync(notification, cancellationToken);
            }
        }

        return Accepted();
    }

    private async Task HandleNotificationAsync(JsonElement notification, CancellationToken cancellationToken)
    {
        var subscriptionId = TryGetString(notification, "subscriptionId");
        var changeType = TryGetString(notification, "changeType") ?? "unknown";
        var resource = TryGetString(notification, "resource");
        var clientState = TryGetString(notification, "clientState");

        var syncLog = new AppointmentSyncLog
        {
            Id = Guid.NewGuid(),
            GraphSubscriptionId = subscriptionId,
            Operation = changeType,
            Source = LeadAppointmentBookingSources.MicrosoftGraphWebhook,
            Success = false,
            DiagnosticJson = notification.GetRawText(),
            CreatedUtc = DateTime.UtcNow
        };

        try
        {
            var subscription = string.IsNullOrWhiteSpace(subscriptionId)
                ? null
                : await _db.GraphCalendarSubscriptions
                    .FirstOrDefaultAsync(x => x.GraphSubscriptionId == subscriptionId, cancellationToken);

            if (subscription == null)
            {
                syncLog.Error = "Subscription not found.";
                _db.AppointmentSyncLogs.Add(syncLog);
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }

            if (!string.Equals(subscription.ClientState, clientState, StringComparison.Ordinal))
            {
                syncLog.AgentUserId = subscription.AgentUserId;
                syncLog.CalendarUserId = subscription.CalendarUserId;
                syncLog.CalendarEmail = subscription.CalendarEmail;
                syncLog.Error = "Client state mismatch.";
                _db.AppointmentSyncLogs.Add(syncLog);
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }

            var webhookUtc = DateTime.UtcNow;
            subscription.LastWebhookUtc = webhookUtc;
            subscription.UpdatedUtc = webhookUtc;

            syncLog.AgentUserId = subscription.AgentUserId;
            syncLog.CalendarUserId = subscription.CalendarUserId;
            syncLog.CalendarEmail = subscription.CalendarEmail;

            var eventId = ExtractEventId(resource);
            syncLog.GraphEventId = eventId;

            if (string.IsNullOrWhiteSpace(eventId))
            {
                syncLog.Success = false;
                syncLog.Error = "Graph event id could not be extracted from resource.";
                _db.AppointmentSyncLogs.Add(syncLog);
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }

            if (string.Equals(changeType, "deleted", StringComparison.OrdinalIgnoreCase))
            {
                await MarkAppointmentCancelledAsync(subscription, eventId, syncLog, webhookUtc, cancellationToken);
                return;
            }

            var graphEvent = await TryFetchGraphEventAsync(subscription, eventId, cancellationToken);
            if (graphEvent == null)
            {
                syncLog.Success = false;
                syncLog.Error = "Graph event could not be fetched.";
                _db.AppointmentSyncLogs.Add(syncLog);
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }

            await ApplyGraphEventToAppointmentAsync(subscription, graphEvent, syncLog, webhookUtc, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Graph calendar webhook notification failed for subscription {SubscriptionId}.", subscriptionId);
            syncLog.Error = ex.Message;
            _db.AppointmentSyncLogs.Add(syncLog);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task MarkAppointmentCancelledAsync(
        GraphCalendarSubscription subscription,
        string eventId,
        AppointmentSyncLog syncLog,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var appointment = await _db.LeadAppointments
            .FirstOrDefaultAsync(x => x.CalendarEventId == eventId, cancellationToken);

        if (appointment == null)
        {
            syncLog.Success = false;
            syncLog.Error = "No LeadAppointment matched deleted Graph event id.";
            _db.AppointmentSyncLogs.Add(syncLog);
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        appointment.BookingProvider = "microsoft_graph";
        appointment.ConfirmationSource = LeadAppointmentBookingSources.MicrosoftGraphWebhook;
        appointment.LastSyncedUtc = utcNow;
        appointment.LastSyncStatus = "webhook_deleted";
        appointment.LastSyncError = null;
        appointment.UpdatedUtc = utcNow;
        appointment.ApplyStatus(LeadAppointmentStatus.Cancelled, utcNow);

        syncLog.AppointmentId = appointment.Id;
        syncLog.WorkstationLeadId = appointment.WorkstationLeadId;
        syncLog.ClientProfileId = appointment.ClientProfileId;
        syncLog.Success = true;
        syncLog.Error = null;

        _db.AppointmentSyncLogs.Add(syncLog);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task ApplyGraphEventToAppointmentAsync(
        GraphCalendarSubscription subscription,
        GraphCalendarEvent graphEvent,
        AppointmentSyncLog syncLog,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(graphEvent.Id))
        {
            syncLog.Success = false;
            syncLog.Error = "Fetched Graph event did not include an id.";
            _db.AppointmentSyncLogs.Add(syncLog);
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        var subscriptionCalendarIdentity = ResolveSubscriptionCalendarIdentity(subscription);

        var appointment = await _db.LeadAppointments
            .OrderByDescending(x => x.UpdatedUtc)
            .ThenByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(x =>
                x.CalendarEventId == graphEvent.Id ||
                (x.CalendarEventId == null &&
                 x.LastSyncStatus == "graph_fallback_pending" &&
                 (
                     (!string.IsNullOrWhiteSpace(subscription.CalendarUserId) &&
                      x.BookingCalendarUserId == subscription.CalendarUserId) ||
                     (!string.IsNullOrWhiteSpace(subscriptionCalendarIdentity) &&
                      (x.BookingCalendarEmail == subscriptionCalendarIdentity ||
                       x.BookingPageIdOrMailbox == subscriptionCalendarIdentity)) ||
                     (!string.IsNullOrWhiteSpace(subscription.CalendarEmail) &&
                      (x.BookingCalendarEmail == subscription.CalendarEmail ||
                       x.BookingPageIdOrMailbox == subscription.CalendarEmail))
                 )),
                cancellationToken);

        if (appointment != null && string.IsNullOrWhiteSpace(appointment.CalendarEventId))
        {
            appointment.CalendarEventId = graphEvent.Id;
        }

        if (appointment == null)
        {
            syncLog.Success = false;
            syncLog.Error = "No LeadAppointment matched fetched Graph event id.";
            _db.AppointmentSyncLogs.Add(syncLog);
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        var oldStart = appointment.ScheduledStartUtc;
        var oldEnd = appointment.ScheduledEndUtc;
        var newStart = ParseGraphDateTime(graphEvent.Start);
        var newEnd = ParseGraphDateTime(graphEvent.End);
        var isReschedule = oldStart.HasValue && newStart.HasValue && oldStart.Value != newStart.Value;

        appointment.BookingProvider = "microsoft_graph";
        appointment.CalendarEventWebLink = Clean(graphEvent.WebLink);
        appointment.ScheduledStartUtc = newStart;
        appointment.ScheduledEndUtc = newEnd;
        appointment.MeetingUrl =
            Clean(graphEvent.OnlineMeeting?.JoinUrl) ??
            Clean(graphEvent.OnlineMeetingUrl) ??
            appointment.MeetingUrl;
        appointment.ConfirmationSource = LeadAppointmentBookingSources.MicrosoftGraphWebhook;
        appointment.LastSyncedUtc = utcNow;
        appointment.LastSyncStatus = isReschedule ? "webhook_rescheduled" : "webhook_updated";
        appointment.LastSyncError = null;
        appointment.RawProviderPayloadJson = syncLog.DiagnosticJson;
        appointment.UpdatedUtc = utcNow;
        appointment.ApplyStatus(isReschedule ? LeadAppointmentStatus.Rescheduled : LeadAppointmentStatus.Booked, utcNow);

        if (!isReschedule)
        {
            await TryRecordAppointmentBookedMetaSignalAsync(appointment, utcNow, cancellationToken);
        }

        syncLog.AppointmentId = appointment.Id;
        syncLog.WorkstationLeadId = appointment.WorkstationLeadId;
        syncLog.ClientProfileId = appointment.ClientProfileId;
        syncLog.Success = true;
        syncLog.Error = null;

        _db.AppointmentSyncLogs.Add(syncLog);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task TryRecordAppointmentBookedMetaSignalAsync(
        LeadAppointment appointment,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        if (appointment.Id == Guid.Empty || string.IsNullOrWhiteSpace(appointment.WorkstationLeadId))
            return;

        if (!Guid.TryParse(appointment.WorkstationLeadId, out var workstationLeadGuid))
            return;

        var deduplicationKey = $"AppointmentBooked:{workstationLeadGuid:N}:{appointment.Id:N}";
        var alreadyRecorded = await _db.MetaSignalEvents
            .AsNoTracking()
            .AnyAsync(x => x.EventName == "AppointmentBooked" && x.MetaDeduplicationKey == deduplicationKey, cancellationToken);

        if (alreadyRecorded)
            return;

        var intakeLink = await _db.WebsiteLeadIntakeLinks
            .AsNoTracking()
            .Where(x =>
                x.Id == appointment.WebsiteLeadIntakeLinkId ||
                x.WorkstationLeadId == appointment.WorkstationLeadId)
            .OrderByDescending(x => x.SubmittedUtc)
            .ThenByDescending(x => x.CapturedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var metadataJson = JsonSerializer.Serialize(new
        {
            appointmentId = appointment.Id,
            appointment.WorkstationLeadId,
            appointment.OwnerAgentUserId,
            appointment.CalendarEventId,
            appointment.CalendarEventWebLink,
            appointment.ScheduledStartUtc,
            appointment.ScheduledEndUtc,
            appointment.BookingSource,
            appointment.ConfirmationSource,
            appointment.LastSyncStatus,
            source = "graph_calendar_webhook",
            metaServerStatus = "pending"
        }, JsonOptions);

        _db.MetaSignalEvents.Add(new MetaSignalEvent
        {
            CreatedUtc = utcNow,
            EventId = $"appointment_booked_{appointment.Id:N}",
            EventName = "AppointmentBooked",
            EventCategory = "conversion",
            LeadId = workstationLeadGuid,
            SessionId = intakeLink?.SessionId,
            VisitorId = intakeLink?.VisitorId,
            QuoteType = intakeLink?.InterestType ?? intakeLink?.ProductType ?? "crm",
            PageKey = intakeLink?.SourcePageKey,
            EffectivePageKey = intakeLink?.SourcePageKey,
            PageVariant = intakeLink?.PageVariant,
            PageMode = intakeLink?.PageMode,
            TrafficType = "crm",
            FunnelStep = 4,
            StepName = "appointment_booked",
            IntentScore = 120,
            EngagementScore = 120,
            QualificationScore = 120,
            FrictionScore = 0,
            TotalSignalScore = 120,
            ScoreTier = "AppointmentBooked",
            MetaBrowserSent = false,
            MetaServerSent = false,
            MetaDeduplicationKey = deduplicationKey,
            UtmSource = intakeLink?.UtmSource,
            UtmMedium = intakeLink?.UtmMedium,
            UtmCampaign = intakeLink?.UtmCampaign,
            UtmId = intakeLink?.UtmId,
            UtmContent = intakeLink?.UtmContent,
            FbclidPresent = !string.IsNullOrWhiteSpace(intakeLink?.Fbclid),
            FbcPresent = false,
            FbpPresent = false,
            Referrer = intakeLink?.ReferrerUrl,
            AgentSlug = null,
            Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            Host = null,
            MetadataJson = metadataJson
        });
    }

    private static string? ResolveSubscriptionCalendarIdentity(GraphCalendarSubscription subscription)
    {
        var resource = Clean(subscription.Resource);
        const string prefix = "users/";
        const string suffix = "/events";

        if (!string.IsNullOrWhiteSpace(resource) &&
            resource.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            resource.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            var identity = resource[prefix.Length..^suffix.Length];
            if (!string.IsNullOrWhiteSpace(identity))
            {
                return identity.Trim();
            }
        }

        return FirstNotEmpty(subscription.CalendarUserId, subscription.CalendarEmail);
    }

    private async Task<GraphCalendarEvent?> TryFetchGraphEventAsync(
        GraphCalendarSubscription subscription,
        string eventId,
        CancellationToken cancellationToken)
    {
        var calendarIdentity = ResolveSubscriptionCalendarIdentity(subscription);
        if (string.IsNullOrWhiteSpace(calendarIdentity))
        {
            return null;
        }

        var accessToken = await TryGetAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(calendarIdentity)}/events/{Uri.EscapeDataString(eventId)}?$select=id,webLink,subject,bodyPreview,start,end,attendees,onlineMeeting,onlineMeetingUrl";

        try
        {
            var client = _httpClientFactory.CreateClient("ResilientDefault");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Graph webhook event fetch failed. calendar={CalendarIdentity} event={EventId} status={StatusCode}",
                    calendarIdentity,
                    eventId,
                    (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<GraphCalendarEvent>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graph webhook event fetch threw. event={EventId}", eventId);
            return null;
        }
    }

    private async Task<string?> TryGetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var tenantId = _configuration["AzureAd:TenantId"];
        var clientId = _configuration["AzureAd:ClientId"];
        var clientSecret = _configuration["AzureAd:ClientSecret"];

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            return null;
        }

        try
        {
            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }),
                cancellationToken);
            return token.Token;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to acquire Graph application token for calendar webhook sync.");
            return null;
        }
    }

    private static string? ExtractEventId(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            return null;
        }

        var marker = "/events/";
        var index = resource.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var id = resource[(index + marker.Length)..];
        var slash = id.IndexOf('/');
        return slash >= 0 ? id[..slash] : id;
    }

    private static DateTime? ParseGraphDateTime(GraphDateTimeTimeZone? value)
    {
        if (value == null || string.IsNullOrWhiteSpace(value.DateTime))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value.DateTime, out var dto))
        {
            return dto.UtcDateTime;
        }

        return DateTime.TryParse(value.DateTime, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
    }

    private static string? FirstNotEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class GraphCalendarEvent
    {
        public string? Id { get; set; }
        public string? WebLink { get; set; }
        public GraphDateTimeTimeZone? Start { get; set; }
        public GraphDateTimeTimeZone? End { get; set; }
        public GraphOnlineMeeting? OnlineMeeting { get; set; }
        public string? OnlineMeetingUrl { get; set; }
    }

    private sealed class GraphDateTimeTimeZone
    {
        public string? DateTime { get; set; }
        public string? TimeZone { get; set; }
    }

    private sealed class GraphOnlineMeeting
    {
        public string? JoinUrl { get; set; }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
