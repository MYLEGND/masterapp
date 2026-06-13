using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using AgentPortal.Filters;
using AgentPortal.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using Infrastructure.Data;
using AgentPortal.Models;
using Domain.Entities;
using Domain.Enums;
using System.Security.Claims;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Solutions.BookingBusinesses.Item.GetStaffAvailability;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

namespace AgentPortal.Controllers;

[Authorize]
[Route("calendar")]
public class CalendarController : Controller
{
    private readonly ITokenAcquisition _tokenAcquisition;
    private readonly ILogger<CalendarController> _logger;
    private readonly MasterAppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAgentTimeZoneResolver _agentTimeZoneResolver;
    private readonly GraphServiceClient _appGraph;

    // Scopes must match what you consent to in /calendar/connect
    private static readonly string[] CalendarScopes = new[] { "offline_access", "Calendars.ReadWrite" };
    private static readonly string[] CalendarAvailabilityScopes = new[] { "offline_access", "Calendars.ReadWrite", "MailboxSettings.Read" };
    private static readonly TimeSpan DefaultWorkdayStart = new(7, 0, 0);
    private static readonly TimeSpan DefaultWorkdayEnd = new(19, 0, 0);

    public CalendarController(ITokenAcquisition tokenAcquisition,
        ILogger<CalendarController> logger,
        MasterAppDbContext db,
        IHttpClientFactory httpClientFactory,
        IAgentTimeZoneResolver agentTimeZoneResolver,
        GraphServiceClient appGraph)
    {
        _tokenAcquisition = tokenAcquisition;
        _logger = logger;
        _db = db;
        _httpClientFactory = httpClientFactory;
        _agentTimeZoneResolver = agentTimeZoneResolver;
        _appGraph = appGraph;
    }

    private static string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant();

    private static bool LooksLikeLeadMetaJson(string? raw)
        => !string.IsNullOrWhiteSpace(raw) && raw.TrimStart().StartsWith("{", StringComparison.Ordinal);

    private static ClientCrmMeta ReadLeadMeta(WorkstationLeadProfile lead)
    {
        var meta = ClientCrmMetaSerializer.Deserialize(lead.CrmNotes);
        if (!LooksLikeLeadMetaJson(lead.CrmNotes))
        {
            var legacyNote = (lead.CrmNotes ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(legacyNote) && string.IsNullOrWhiteSpace(meta.AgentNotes))
                meta.AgentNotes = legacyNote;
        }

        if (meta.StageEnteredUtc == default)
            meta.StageEnteredUtc = lead.CreatedUtc;
        if (string.IsNullOrWhiteSpace(meta.MeetingTime))
            meta.MeetingTime = "09:00";
        if (meta.MeetingDurationMinutes <= 0)
            meta.MeetingDurationMinutes = 30;
        if (string.IsNullOrWhiteSpace(meta.CrmPriority))
            meta.CrmPriority = "Normal";

        return meta;
    }

    private static bool IsMissingTable(Exception ex, string tableName)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqliteEx &&
                sqliteEx.SqliteErrorCode == 1 &&
                sqliteEx.Message.Contains(tableName, StringComparison.OrdinalIgnoreCase) &&
                sqliteEx.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (current.Message.Contains(tableName, StringComparison.OrdinalIgnoreCase) &&
                (current.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("invalid object name", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMissingLeadAppointmentsTable(Exception ex) => IsMissingTable(ex, "LeadAppointments");
    private static bool IsMissingWebsiteLeadIntakeLinksTable(Exception ex) => IsMissingTable(ex, "WebsiteLeadIntakeLinks");

    private static string HumanizeAppointmentStatus(LeadAppointmentStatus status)
        => status switch
        {
            LeadAppointmentStatus.NoShow => "No Show",
            _ => status.ToString()
        };

    private static string HumanizeAppointmentSource(string? source)
        => source switch
        {
            LeadAppointmentBookingSources.InternalManual => "Internal manual",
            LeadAppointmentBookingSources.InternalCalendar => "Internal calendar",
            LeadAppointmentBookingSources.WebsiteEmbed => "Website embed",
            LeadAppointmentBookingSources.WebsiteModal => "Website modal",
            LeadAppointmentBookingSources.ExternalRedirectFallback => "External redirect fallback",
            LeadAppointmentBookingSources.MicrosoftGraphConfirmation => "Microsoft Graph confirmation",
            LeadAppointmentBookingSources.ManualVerified => "Manual verified",
            _ => string.IsNullOrWhiteSpace(source) ? "Internal manual" : source.Trim()
        };

    private static string HumanizeBookingConfigurationSource(string? source)
        => source switch
        {
            "agent_profile" => "Agent profile",
            "slug_override" => "Slug override",
            "global_fallback" => "Global fallback",
            _ => string.IsNullOrWhiteSpace(source) ? "Not recorded" : source.Trim()
        };

    private static bool IsTrustedAppointment(LeadAppointment appointment)
    {
        var trustedSource = appointment.ConfirmationSource ?? appointment.BookingSource;
        return appointment.Status is LeadAppointmentStatus.Booked or LeadAppointmentStatus.Confirmed or LeadAppointmentStatus.Completed &&
            (string.Equals(trustedSource, LeadAppointmentBookingSources.InternalCalendar, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(trustedSource, LeadAppointmentBookingSources.MicrosoftGraphConfirmation, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(trustedSource, LeadAppointmentBookingSources.ManualVerified, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildBookingConfigurationLabel(LeadAppointment appointment)
    {
        var parts = new List<string>();
        var sourceLabel = HumanizeBookingConfigurationSource(appointment.BookingConfigurationSource);
        if (!string.Equals(sourceLabel, "Not recorded", StringComparison.OrdinalIgnoreCase))
            parts.Add(sourceLabel);
        if (!string.IsNullOrWhiteSpace(appointment.BookingAgentSlug))
            parts.Add($"slug {appointment.BookingAgentSlug.Trim()}");
        if (!string.IsNullOrWhiteSpace(appointment.BookingCalendarEmail))
            parts.Add(appointment.BookingCalendarEmail.Trim());
        else if (!string.IsNullOrWhiteSpace(appointment.BookingPageIdOrMailbox))
            parts.Add(appointment.BookingPageIdOrMailbox.Trim());
        return parts.Count == 0
            ? (string.Equals(appointment.BookingSource, LeadAppointmentBookingSources.InternalCalendar, StringComparison.OrdinalIgnoreCase)
                ? "Internal calendar path"
                : "Not recorded")
            : string.Join(" • ", parts);
    }

    private static string BuildAppointmentConfirmationStateLabel(LeadAppointment appointment)
    {
        if (IsTrustedAppointment(appointment))
            return "Booked / verified";
        if (appointment.Status == LeadAppointmentStatus.Requested)
            return "Requested / awaiting verification";
        if (appointment.Status is LeadAppointmentStatus.Booked or LeadAppointmentStatus.Confirmed or LeadAppointmentStatus.Completed)
            return $"{HumanizeAppointmentStatus(appointment.Status)} / source not verified";
        return HumanizeAppointmentStatus(appointment.Status);
    }

    private static DateTime? ResolveAppointmentStatusTimestamp(LeadAppointment appointment)
        => appointment.Status switch
        {
            LeadAppointmentStatus.Requested => appointment.RequestedUtc,
            LeadAppointmentStatus.Booked => appointment.BookedUtc,
            LeadAppointmentStatus.Confirmed => appointment.ConfirmedUtc,
            LeadAppointmentStatus.Completed => appointment.CompletedUtc,
            LeadAppointmentStatus.NoShow => appointment.NoShowUtc,
            LeadAppointmentStatus.Cancelled => appointment.CancelledUtc,
            LeadAppointmentStatus.Rescheduled => appointment.RescheduledUtc,
            _ => appointment.LastStatusChangedUtc
        };

    private static object? BuildLeadAppointmentPayload(LeadAppointment? appointment)
    {
        if (appointment == null)
            return null;

        return new
        {
            id = appointment.Id,
            workstationLeadId = appointment.WorkstationLeadId,
            ownerAgentUserId = appointment.OwnerAgentUserId,
            websiteLeadIntakeLinkId = appointment.WebsiteLeadIntakeLinkId,
            status = appointment.Status.ToString(),
            statusLabel = HumanizeAppointmentStatus(appointment.Status),
            bookingSource = appointment.BookingSource,
            bookingSourceLabel = HumanizeAppointmentSource(appointment.BookingSource),
            requestedBookingSource = appointment.RequestedBookingSource,
            requestedBookingSourceLabel = HumanizeAppointmentSource(appointment.RequestedBookingSource),
            confirmationSource = appointment.ConfirmationSource,
            confirmationSourceLabel = HumanizeAppointmentSource(appointment.ConfirmationSource),
            confirmationVerified = IsTrustedAppointment(appointment),
            confirmationStateLabel = BuildAppointmentConfirmationStateLabel(appointment),
            bookingConfigurationSource = appointment.BookingConfigurationSource,
            bookingConfigurationSourceLabel = HumanizeBookingConfigurationSource(appointment.BookingConfigurationSource),
            bookingConfigurationLabel = BuildBookingConfigurationLabel(appointment),
            bookingTrackingProfileId = appointment.BookingTrackingProfileId,
            bookingAgentSlug = appointment.BookingAgentSlug,
            bookingAgentUserId = appointment.BookingAgentUserId,
            bookingCalendarUserId = appointment.BookingCalendarUserId,
            bookingCalendarEmail = appointment.BookingCalendarEmail,
            bookingPageIdOrMailbox = appointment.BookingPageIdOrMailbox,
            calendarEventId = appointment.CalendarEventId,
            calendarEventWebLink = appointment.CalendarEventWebLink,
            scheduledStartUtc = UtcDate(appointment.ScheduledStartUtc),
            scheduledEndUtc = UtcDate(appointment.ScheduledEndUtc),
            meetingUrl = appointment.MeetingUrl,
            createdUtc = UtcDate(appointment.CreatedUtc),
            updatedUtc = UtcDate(appointment.UpdatedUtc),
            lastStatusChangedUtc = UtcDate(appointment.LastStatusChangedUtc),
            statusTimestampUtc = UtcDate(ResolveAppointmentStatusTimestamp(appointment)),
            requestedUtc = UtcDate(appointment.RequestedUtc),
            bookedUtc = UtcDate(appointment.BookedUtc),
            confirmedUtc = UtcDate(appointment.ConfirmedUtc),
            completedUtc = UtcDate(appointment.CompletedUtc),
            noShowUtc = UtcDate(appointment.NoShowUtc),
            cancelledUtc = UtcDate(appointment.CancelledUtc),
            rescheduledUtc = UtcDate(appointment.RescheduledUtc)
        };
    }

    private static DateTime? UtcDate(DateTime? value)
        => value.HasValue
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
            : null;

    private string GetAgentOidOrThrow()
    {
        var oid =
            User.FindFirstValue("oid") ??
            User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");

        oid = Norm(oid);

        if (string.IsNullOrWhiteSpace(oid))
            throw new InvalidOperationException("Missing agent OID claim.");

        return oid;
    }

    // GET /calendar/connect
    // Triggers incremental consent for delegated calendar scopes.
    [HttpGet("connect")]
    public IActionResult Connect()
    {
        var props = new AuthenticationProperties
        {
            RedirectUri = Url.Action("Connected", "Calendar") ?? "/calendar/connected"
        };

        // Force consent prompt during testing
        props.Items["prompt"] = "consent";

        // Correct incremental consent for Microsoft.Identity.Web
        props.Items["scope"] = string.Join(" ", CalendarAvailabilityScopes);
        return Challenge(props, OpenIdConnectDefaults.AuthenticationScheme);
    }

    // GET /calendar/connected
    // Redirect back to CRM instead of blank text page.
    [HttpGet("connected")]
    public IActionResult Connected()
    {
        TempData["Created"] = "✅ Calendar connected for this agent.";
        return RedirectToAction("Index", "Clients");
    }

    // GET /calendar/status
    // This is what your Index.cshtml calls to set the button state.
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        try
        {
            // If we can acquire an access token for these scopes, we're "connected"
            var token = await _tokenAcquisition.GetAccessTokenForUserAsync(CalendarScopes);

            // Optional: show the signed-in user
            var email =
                User.FindFirstValue("preferred_username") ??
                User.FindFirstValue(ClaimTypes.Upn) ??
                User.Identity?.Name ??
                "";

            return Json(new { connected = !string.IsNullOrWhiteSpace(token), email });
        }
        catch (MicrosoftIdentityWebChallengeUserException ex)
        {
            _logger.LogWarning(ex, "Calendar status requires user interaction/consent.");
            return Json(new { connected = false, needsConsent = true });
        }
        catch (MsalUiRequiredException ex)
        {
            _logger.LogWarning(ex, "Calendar status token not available (user_null).");
            return Json(new { connected = false, needsConsent = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Calendar status check failed (not consented or token unavailable).");
            return Json(new { connected = false });
        }
    }

    public sealed class CreateEventRequest
    {
        public string? ClientUserId { get; set; }
        public string? Subject { get; set; }
        public string? StartISO { get; set; }  // "YYYY-MM-DDTHH:mm:ss"
        public string? EndISO { get; set; }
        public string? Body { get; set; }
        public string? Location { get; set; }
        public string? ZoomJoinUrl { get; set; }
        public string? ActivityNote { get; set; }
    }

    private sealed class GraphEventResponse
    {
        public string? Id { get; set; }
        public string? WebLink { get; set; }
    }

    private sealed class CalendarViewResponse
    {
        public List<CalendarViewEvent>? Value { get; set; }
    }

    private sealed class CalendarViewEvent
    {
        public string? Id { get; set; }
        public string? Subject { get; set; }
        public string? ShowAs { get; set; }
        public bool IsAllDay { get; set; }
        public GraphDateTimeTimeZone? Start { get; set; }
        public GraphDateTimeTimeZone? End { get; set; }
    }

    private sealed class GraphDateTimeTimeZone
    {
        public string? DateTime { get; set; }
        public string? TimeZone { get; set; }
    }

    private sealed class MailboxSettingsResponse
    {
        public string? TimeZone { get; set; }
        public WorkingHoursResponse? WorkingHours { get; set; }
    }

    private sealed class WorkingHoursResponse
    {
        public List<string>? DaysOfWeek { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public GraphTimeZoneReference? TimeZone { get; set; }
    }

    private sealed class GraphTimeZoneReference
    {
        public string? Name { get; set; }
    }

    private static bool TryParseLocalIso(string? value, out DateTime parsed)
    {
        return DateTime.TryParseExact(
            value ?? "",
            "yyyy-MM-dd'T'HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out parsed);
    }

    private static bool TryParseDateOnlyIso(string? value, out DateTime parsed)
    {
        return DateTime.TryParseExact(
            value ?? "",
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out parsed);
    }

    private static DateTimeOffset ParseGraphDateTime(GraphDateTimeTimeZone? value)
    {
        if (DateTimeOffset.TryParse(value?.DateTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed.ToLocalTime();

        return DateTimeOffset.MinValue;
    }

    private static TimeSpan ParseWorkingTime(string? value, TimeSpan fallback)
    {
        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static bool IsWorkingDay(DateTime localDate, WorkingHoursResponse? workingHours)
    {
        if (workingHours?.DaysOfWeek == null || workingHours.DaysOfWeek.Count == 0)
            return localDate.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday;

        return workingHours.DaysOfWeek.Any(day =>
            Enum.TryParse<DayOfWeek>(day, ignoreCase: true, out var parsed) && parsed == localDate.DayOfWeek);
    }

    [HttpGet("day-availability")]
    public async Task<IActionResult> DayAvailability(string date)
    {
        if (!TryParseDateOnlyIso(date, out var localDate))
            return BadRequest("Invalid date.");

        try
        {
            var agentOid = GetAgentOidOrThrow();
            var agentProfile = await _db.AgentProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => (x.AgentUserId ?? "").Trim().ToLower() == agentOid.Trim().ToLower());

            if (agentProfile == null)
            {
                var currentUpn = User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.Upn) ?? User.Identity?.Name ?? "";
                agentProfile = await _db.AgentProfiles.AsNoTracking()
                    .FirstOrDefaultAsync(x => !string.IsNullOrWhiteSpace(currentUpn) &&
                                              ((x.AgentUpn ?? "").Trim().ToLower() == currentUpn.Trim().ToLower() ||
                                               (x.NormalizedEmail ?? "").Trim().ToLower() == currentUpn.Trim().ToLower()));
            }

            var bookingBusinessId = (agentProfile?.BookingPageIdOrMailbox ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(bookingBusinessId))
            {
                return Ok(new
                {
                    connected = false,
                    date = localDate.ToString("yyyy-MM-dd"),
                    items = Array.Empty<object>(),
                    freeSlots = Array.Empty<object>(),
                    message = "Agent booking configuration missing."
                });
            }

            var services = await _appGraph.Solutions.BookingBusinesses[bookingBusinessId]
                .Services
                .GetAsync(cancellationToken: HttpContext.RequestAborted);

            var serviceStaffIds = (services?.Value ?? new List<BookingService>())
                .Where(x => x.IsHiddenFromCustomers != true)
                .SelectMany(x => x.StaffMemberIds ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var visibleServices = (services?.Value ?? new List<BookingService>())
                .Where(x => x.IsHiddenFromCustomers != true)
                .ToList();

            var slotIntervalMinutes = visibleServices
                .Select(x => x.SchedulingPolicy?.TimeSlotInterval)
                .Where(x => x.HasValue && x.Value.TotalMinutes > 0)
                .Select(x => (int)Math.Round(x!.Value.TotalMinutes))
                .FirstOrDefault();

            if (slotIntervalMinutes <= 0)
                slotIntervalMinutes = 30;

            if (serviceStaffIds.Count == 0)
            {
                var staff = await _appGraph.Solutions.BookingBusinesses[bookingBusinessId]
                    .StaffMembers
                    .GetAsync(cancellationToken: HttpContext.RequestAborted);

                serviceStaffIds = (staff?.Value ?? new List<BookingStaffMemberBase>())
                    .Select(x => x.Id)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()!;
            }

            if (serviceStaffIds.Count == 0)
            {
                return Ok(new
                {
                    connected = false,
                    date = localDate.ToString("yyyy-MM-dd"),
                    items = Array.Empty<object>(),
                    freeSlots = Array.Empty<object>(),
                    message = "No Bookings staff configured."
                });
            }

            var agentTimeZone = _agentTimeZoneResolver.Resolve(HttpContext);
            var localStart = localDate.Date;
            var localEnd = localStart.AddDays(1);
            var graphTimeZone = string.IsNullOrWhiteSpace(agentTimeZone.Id)
                ? "UTC"
                : agentTimeZone.Id;

            var request = new GetStaffAvailabilityPostRequestBody
            {
                StaffIds = serviceStaffIds,
                StartDateTime = new DateTimeTimeZone
                {
                    DateTime = localStart.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                    TimeZone = graphTimeZone
                },
                EndDateTime = new DateTimeTimeZone
                {
                    DateTime = localEnd.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                    TimeZone = graphTimeZone
                }
            };

            var availability = await _appGraph.Solutions.BookingBusinesses[bookingBusinessId]
                .GetStaffAvailability
                .PostAsGetStaffAvailabilityPostResponseAsync(request, cancellationToken: HttpContext.RequestAborted);

            DateTime ParseAvailabilityLocal(DateTimeTimeZone? value, TimeZoneInfo tz)
            {
                if (value == null || string.IsNullOrWhiteSpace(value.DateTime))
                    return DateTime.MinValue;

                _logger.LogWarning(
                    "BOOKINGS RAW DateTime={DateTime} TimeZone={TimeZone}",
                    value.DateTime,
                    value.TimeZone);

                var parsed = DateTime.Parse(
                    value.DateTime,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);

                _logger.LogWarning(
                    "BOOKINGS PARSED={Parsed:o}",
                    parsed);

                // Microsoft Bookings availability can return wall-clock business hours with a UTC label.
                // Treat the payload as local Bookings time here; otherwise Azure shifts the workday into evening/overnight.
                return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
            }

            var availabilityItems = (availability?.Value ?? new List<StaffAvailabilityItem>())
                .SelectMany(staff => staff.AvailabilityItems ?? new List<AvailabilityItem>())
                .Where(x => x.Status == BookingsAvailabilityStatus.Available)
                .Select(x =>
                {
                    var start = ParseAvailabilityLocal(x.StartDateTime, agentTimeZone);
                    var end = ParseAvailabilityLocal(x.EndDateTime, agentTimeZone);
                    return new { Start = start, End = end, ServiceId = x.ServiceId ?? "" };
                })
                .Where(x => x.Start != DateTime.MinValue && x.End != DateTime.MinValue && x.End > x.Start)
                .OrderBy(x => x.Start)
                .ToList();

            var appointmentPage = await _appGraph.Solutions.BookingBusinesses[bookingBusinessId]
                .Appointments
                .GetAsync(config =>
                {
                    config.QueryParameters.Top = 100;
                    config.QueryParameters.Select = new[] { "id", "startDateTime", "endDateTime", "serviceId", "customerName" };
                    config.QueryParameters.Filter =
                        $"startDateTime/dateTime ge '{utcStart:yyyy-MM-ddTHH:mm:ss}' and startDateTime/dateTime lt '{utcEnd:yyyy-MM-ddTHH:mm:ss}'";
                }, cancellationToken: HttpContext.RequestAborted);

            var allExistingAppointments = new List<BookingAppointment>();
            if (appointmentPage?.Value != null)
                allExistingAppointments.AddRange(appointmentPage.Value);

            while (!string.IsNullOrWhiteSpace(appointmentPage?.OdataNextLink))
            {
                appointmentPage = await _appGraph.Solutions.BookingBusinesses[bookingBusinessId]
                    .Appointments
                    .WithUrl(appointmentPage.OdataNextLink)
                    .GetAsync(cancellationToken: HttpContext.RequestAborted);

                if (appointmentPage?.Value != null)
                    allExistingAppointments.AddRange(appointmentPage.Value);
            }

            var serviceBufferMap = visibleServices
                .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .ToDictionary(
                    x => x.Id!,
                    x => new
                    {
                        Pre = x.PreBuffer ?? TimeSpan.Zero,
                        Post = x.PostBuffer ?? TimeSpan.Zero
                    },
                    StringComparer.OrdinalIgnoreCase);

            var busyRanges = allExistingAppointments
                .Select(x =>
                {
                    var start = ParseAvailabilityLocal(x.StartDateTime, agentTimeZone);
                    var end = ParseAvailabilityLocal(x.EndDateTime, agentTimeZone);

                    var pre = TimeSpan.Zero;
                    var post = TimeSpan.Zero;
                    if (!string.IsNullOrWhiteSpace(x.ServiceId) &&
                        serviceBufferMap.TryGetValue(x.ServiceId, out var buffers))
                    {
                        pre = buffers.Pre;
                        post = buffers.Post;
                    }

                    return new
                    {
                        Start = start == DateTime.MinValue ? start : start.Subtract(pre),
                        End = end == DateTime.MinValue ? end : end.Add(post),
                        AppointmentStart = start,
                        AppointmentEnd = end,
                        ServiceId = x.ServiceId ?? ""
                    };
                })
                .Where(x => x.AppointmentStart.Date == localDate.Date &&
                            x.Start != DateTime.MinValue &&
                            x.End != DateTime.MinValue &&
                            x.End > x.Start)
                .Select(x => (x.Start, x.End))
                .ToList();

            var freeRanges = SubtractBusyRanges(
                availabilityItems.Select(x => (x.Start, x.End)),
                busyRanges);

            var freeSlots = freeRanges
                .Select(x => new
                {
                    startIso = DateTime.SpecifyKind(x.Start, DateTimeKind.Unspecified).ToString("o", CultureInfo.InvariantCulture),
                    endIso = DateTime.SpecifyKind(x.End, DateTimeKind.Unspecified).ToString("o", CultureInfo.InvariantCulture),
                    startTimeValue = x.Start.ToString("HH:mm", CultureInfo.InvariantCulture),
                    startLabel = x.Start.ToString("h:mm tt", CultureInfo.InvariantCulture),
                    endLabel = x.End.ToString("h:mm tt", CultureInfo.InvariantCulture),
                    serviceId = ""
                })
                .ToList();

            return Ok(new
            {
                connected = true,
                date = localDate.ToString("yyyy-MM-dd"),
                source = "microsoft_bookings",
                businessId = bookingBusinessId,
                slotIntervalMinutes,
                items = Array.Empty<object>(),
                workHours = new
                {
                    enabled = freeSlots.Count > 0,
                    source = "microsoft_bookings",
                    startLabel = freeSlots.FirstOrDefault()?.startLabel ?? "",
                    endLabel = freeSlots.LastOrDefault()?.endLabel ?? ""
                },
                buffers = visibleServices
                    .Where(x => x.IsHiddenFromCustomers != true)
                    .Select(x => new
                    {
                        serviceId = x.Id,
                        serviceName = x.DisplayName,
                        durationMinutes = (int)Math.Round((x.DefaultDuration ?? TimeSpan.FromMinutes(30)).TotalMinutes),
                        preBufferMinutes = (int)Math.Round((x.PreBuffer ?? TimeSpan.Zero).TotalMinutes),
                        postBufferMinutes = (int)Math.Round((x.PostBuffer ?? TimeSpan.Zero).TotalMinutes)
                    })
                    .ToList(),
                freeSlots
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bookings day availability failed.");
            return Ok(new
            {
                connected = false,
                date = localDate.ToString("yyyy-MM-dd"),
                items = Array.Empty<object>(),
                freeSlots = Array.Empty<object>(),
                message = ex.Message
            });
        }
    }

    // POST /calendar/create-event
    [HttpPost("create-event")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateEvent([FromBody] CreateEventRequest req)
    {
        // Basic validation
        if (string.IsNullOrWhiteSpace(req.Subject) ||
            string.IsNullOrWhiteSpace(req.StartISO) ||
            string.IsNullOrWhiteSpace(req.EndISO))
        {
            return BadRequest("Missing subject/start/end.");
        }

        if (!TryParseLocalIso(req.StartISO, out var localStart) ||
            !TryParseLocalIso(req.EndISO, out var localEnd))
        {
            return BadRequest("Invalid start or end date.");
        }

        if (localEnd <= localStart)
            return BadRequest("End time must be after start time.");

        try
        {
            var agentOid = GetAgentOidOrThrow();
            var clientUserId = Norm(req.ClientUserId);
            if (string.IsNullOrWhiteSpace(clientUserId))
                return BadRequest("Missing client.");

            var profile = await _db.ClientProfiles
                .FirstOrDefaultAsync(x => (x.ClientUserId ?? "").Trim().ToLower() == clientUserId);

            WorkstationLeadProfile? leadProfile = null;

            if (profile == null)
            {
                leadProfile = await _db.WorkstationLeadProfiles
                    .FirstOrDefaultAsync(x => (x.LeadId ?? "").Trim().ToLower() == clientUserId &&
                                               (x.AgentUserId ?? "").Trim().ToLower() == agentOid);

                if (leadProfile == null)
                    return NotFound("Client/lead not found.");
            }
            else
            {
                var ownsClient = await _db.AgentClients.AnyAsync(x =>
                    (x.AgentUserId ?? "").Trim().ToLower() == agentOid &&
                    (x.ClientUserId ?? "").Trim().ToLower() == clientUserId);

                if (!ownsClient)
                    return Forbid();
            }

            var zoomJoinUrl = string.IsNullOrWhiteSpace(req.ZoomJoinUrl) ? null : req.ZoomJoinUrl.Trim();
            var displayLocation = string.IsNullOrWhiteSpace(req.Location) ? "" : req.Location.Trim();
            var encodedBody = System.Net.WebUtility.HtmlEncode(req.Body ?? "").Replace("\n", "<br/>");
            var htmlBody = $"""
                           <div>{encodedBody}</div>
                           {(string.IsNullOrWhiteSpace(zoomJoinUrl) ? "" : $"<p><strong>Zoom:</strong> <a href=\"{System.Net.WebUtility.HtmlEncode(zoomJoinUrl)}\">Join Meeting</a></p>")}
                           """;
            var agentTimeZone = _agentTimeZoneResolver.Resolve(HttpContext);
            var utcStart = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified),
                agentTimeZone);

            var utcEnd = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(localEnd, DateTimeKind.Unspecified),
                agentTimeZone);

            var ownerAgentUserId = profile == null
                ? (string.IsNullOrWhiteSpace(leadProfile?.AgentUserId) ? agentOid : leadProfile!.AgentUserId)
                : agentOid;

            var ownerAgentProfile = await _db.AgentProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => (x.AgentUserId ?? "").Trim().ToLower() == ownerAgentUserId.Trim().ToLower());

            if (ownerAgentProfile == null)
            {
                var currentUpn = User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.Upn) ?? User.Identity?.Name ?? "";
                ownerAgentProfile = await _db.AgentProfiles.AsNoTracking()
                    .FirstOrDefaultAsync(x => !string.IsNullOrWhiteSpace(currentUpn) &&
                                              ((x.AgentUpn ?? "").Trim().ToLower() == currentUpn.Trim().ToLower() ||
                                               (x.NormalizedEmail ?? "").Trim().ToLower() == currentUpn.Trim().ToLower()));
            }

            var bookingBusinessId = (ownerAgentProfile?.BookingPageIdOrMailbox ?? "").Trim();
            if (string.IsNullOrWhiteSpace(bookingBusinessId))
                return BadRequest("Agent booking configuration missing.");

            var durationMinutes = MinutesBetween(localStart, localEnd);
            var bookingService = await ResolveBookingServiceByDurationAsync(bookingBusinessId, durationMinutes, HttpContext.RequestAborted);
            if (bookingService == null || string.IsNullOrWhiteSpace(bookingService.Id))
                return BadRequest($"No Microsoft Bookings service matches {durationMinutes} minutes.");

            var customerName = profile != null
                ? $"{profile.FirstName} {profile.LastName}".Trim()
                : $"{leadProfile?.FirstName} {leadProfile?.LastName}".Trim();

            if (string.IsNullOrWhiteSpace(customerName))
                customerName = "Client";

            var customerEmail = profile != null ? profile.Email : leadProfile?.Email;
            var customerPhone = profile != null ? profile.Phone : leadProfile?.Phone;

            var bookingAppointment = new BookingAppointment
            {
                ServiceId = bookingService.Id,
                ServiceName = bookingService.DisplayName,
                StaffMemberIds = bookingService.StaffMemberIds?.Where(x => !string.IsNullOrWhiteSpace(x)).Take(1).ToList(),
                StartDateTime = new DateTimeTimeZone
                {
                    DateTime = utcStart.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                    TimeZone = "UTC"
                },
                EndDateTime = new DateTimeTimeZone
                {
                    DateTime = utcEnd.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                    TimeZone = "UTC"
                },
                CustomerName = customerName,
                CustomerEmailAddress = customerEmail,
                CustomerPhone = customerPhone,
                CustomerTimeZone = agentTimeZone.Id,
                Customers = new List<BookingCustomerInformationBase>
                {
                    new BookingCustomerInformation
                    {
                        Name = customerName,
                        EmailAddress = customerEmail,
                        Phone = customerPhone,
                        TimeZone = agentTimeZone.Id
                    }
                },
                AdditionalInformation = htmlBody,
                IsCustomerAllowedToManageBooking = true,
                OptOutOfCustomerEmail = false
            };

            var bookingCreated = await _appGraph
                .Solutions
                .BookingBusinesses[bookingBusinessId]
                .Appointments
                .PostAsync(bookingAppointment, cancellationToken: HttpContext.RequestAborted);

            var created = new CreatedCalendarLikeResponse
            {
                Id = bookingCreated?.Id,
                WebLink = bookingCreated?.SelfServiceAppointmentId,
                SelfServiceAppointmentId = bookingCreated?.SelfServiceAppointmentId,
                ServiceId = bookingCreated?.ServiceId,
                ServiceName = bookingCreated?.ServiceName
            };

            if (profile != null)
            {
                var clientNowUtc = DateTime.UtcNow;
                var meta = ClientCrmMetaSerializer.Deserialize(profile.CrmNotes);
                meta.MeetingLocation = displayLocation;
                meta.ZoomJoinUrl = zoomJoinUrl;
                meta.LastCalendarEventId = created?.Id;
                meta.LastCalendarEventWebLink = created?.WebLink;
                meta.Activities.Add(new ClientCrmActivity
                {
                    Type = "Meeting",
                    Date = DateOnly.FromDateTime(localStart).ToString("yyyy-MM-dd"),
                    Note = string.IsNullOrWhiteSpace(req.ActivityNote) ? (req.Subject ?? "Calendar event created") : req.ActivityNote.Trim(),
                    Location = displayLocation,
                    MeetingLink = zoomJoinUrl,
                    CalendarEventId = created?.Id,
                    CalendarWebLink = created?.WebLink,
                    CreatedBy = User.FindFirstValue("preferred_username") ?? User.Identity?.Name
                });

                var clientLeadId = (profile.ClientUserId ?? string.Empty).Trim();
                var linkedLeadProfile = string.IsNullOrWhiteSpace(clientLeadId)
                    ? null
                    : await _db.WorkstationLeadProfiles.FirstOrDefaultAsync(x =>
                        (x.LeadId ?? "").Trim().ToLower() == clientLeadId.ToLower() &&
                        (x.AgentUserId ?? "").Trim().ToLower() == agentOid);

                LeadAppointment? clientPersistedAppointment = null;

                if (linkedLeadProfile != null)
                {
                    var createdEventId = created?.Id?.Trim();

                    clientPersistedAppointment = !string.IsNullOrWhiteSpace(createdEventId)
                        ? await _db.LeadAppointments
                            .FirstOrDefaultAsync(x => x.CalendarEventId == createdEventId)
                        : null;

                    clientPersistedAppointment ??= await _db.LeadAppointments
                        .Where(x => x.WorkstationLeadId == linkedLeadProfile.LeadId &&
                                    x.ClientProfileId == profile.Id.ToString())
                        .OrderByDescending(x => x.UpdatedUtc)
                        .FirstOrDefaultAsync();

                    if (clientPersistedAppointment == null)
                    {
                        Guid? clientLatestIntakeLinkId = null;
                        try
                        {
                            clientLatestIntakeLinkId = await _db.WebsiteLeadIntakeLinks
                                .AsNoTracking()
                                .Where(x => x.WorkstationLeadId == linkedLeadProfile.LeadId)
                                .OrderByDescending(x => x.SubmittedUtc)
                                .ThenByDescending(x => x.CapturedUtc)
                                .Select(x => (Guid?)x.Id)
                                .FirstOrDefaultAsync();
                        }
                        catch (Exception ex) when (IsMissingWebsiteLeadIntakeLinksTable(ex))
                        {
                            _logger.LogWarning(ex, "WebsiteLeadIntakeLinks table is unavailable; client calendar event will persist without intake linkage.");
                        }

                        clientPersistedAppointment = new LeadAppointment
                        {
                            Id = Guid.NewGuid(),
                            WorkstationLeadId = linkedLeadProfile.LeadId,
                            OwnerAgentUserId = string.IsNullOrWhiteSpace(linkedLeadProfile.AgentUserId) ? agentOid : linkedLeadProfile.AgentUserId,
                            WebsiteLeadIntakeLinkId = clientLatestIntakeLinkId,
                            ClientProfileId = profile.Id.ToString(),
                            BookingProvider = "microsoft_bookings",
                            BookingSource = LeadAppointmentBookingSources.InternalCalendar,
                            RequestedBookingSource = LeadAppointmentBookingSources.InternalCalendar,
                            ConfirmationSource = LeadAppointmentBookingSources.InternalCalendar,
                            BookingAgentUserId = string.IsNullOrWhiteSpace(linkedLeadProfile.AgentUserId) ? agentOid : linkedLeadProfile.AgentUserId,
                            CreatedUtc = clientNowUtc,
                            RequestedUtc = clientNowUtc
                        };

                        _db.LeadAppointments.Add(clientPersistedAppointment);
                    }

                    clientPersistedAppointment.ClientProfileId = profile.Id.ToString();
                    clientPersistedAppointment.BookingProvider = "microsoft_bookings";
                    clientPersistedAppointment.CalendarEventId = created?.Id;
                    clientPersistedAppointment.CalendarEventWebLink = created?.WebLink;
                    clientPersistedAppointment.ScheduledStartUtc = utcStart;
                    clientPersistedAppointment.ScheduledEndUtc = utcEnd;
                    clientPersistedAppointment.MeetingUrl = zoomJoinUrl;
                    clientPersistedAppointment.LastSyncedUtc = clientNowUtc;
                    clientPersistedAppointment.LastSyncStatus = "bookings_appointment_created";
                    clientPersistedAppointment.LastSyncError = null;
                    clientPersistedAppointment.UpdatedUtc = clientNowUtc;
                    clientPersistedAppointment.ApplyStatus(LeadAppointmentStatus.Booked, clientNowUtc);
                }
                else
                {
                    _logger.LogWarning(
                        "Client calendar event {EventId} could not be attached to LeadAppointments because ClientProfile {ClientProfileId} / ClientUserId {ClientUserId} has no matching WorkstationLeadProfile for agent {AgentOid}.",
                        created?.Id,
                        profile.Id,
                        profile.ClientUserId,
                        agentOid);
                }

                profile.CrmLastTouch = DateTime.Today;
                profile.CrmNotes = ClientCrmMetaSerializer.Serialize(meta);
                profile.UpdatedUtc = clientNowUtc;
                await _db.SaveChangesAsync();

                return Ok(new
                {
                    ok = true,
                    eventId = created?.Id,
                    webLink = created?.WebLink,
                    crmLastTouch = profile.CrmLastTouch?.ToString("yyyy-MM-dd"),
                    activities = meta.Activities
                        .OrderByDescending(x => x.Date)
                        .ThenByDescending(x => x.CreatedUtc)
                        .ToList(),
                    latestAppointment = BuildLeadAppointmentPayload(clientPersistedAppointment)
                });
            }

            // Lead-only fallback (no client profile yet)
            var nowUtc = DateTime.UtcNow;
            var resolvedLeadProfile = leadProfile!;
            var leadMeta = ReadLeadMeta(resolvedLeadProfile);
            leadMeta.MeetingLocation = displayLocation;
            leadMeta.ZoomJoinUrl = zoomJoinUrl;
            leadMeta.LastCalendarEventId = created?.Id;
            leadMeta.LastCalendarEventWebLink = created?.WebLink;
            leadMeta.Activities ??= new List<ClientCrmActivity>();
            leadMeta.Activities.Add(new ClientCrmActivity
            {
                Type = "Meeting",
                Date = DateOnly.FromDateTime(localStart).ToString("yyyy-MM-dd"),
                Note = string.IsNullOrWhiteSpace(req.ActivityNote) ? (req.Subject ?? "Calendar event created") : req.ActivityNote.Trim(),
                Location = displayLocation,
                MeetingLink = zoomJoinUrl,
                CalendarEventId = created?.Id,
                CalendarWebLink = created?.WebLink,
                CreatedBy = User.FindFirstValue("preferred_username") ?? User.Identity?.Name
            });

            Guid? latestIntakeLinkId = null;
            try
            {
                latestIntakeLinkId = await _db.WebsiteLeadIntakeLinks
                    .AsNoTracking()
                    .Where(x => x.WorkstationLeadId == resolvedLeadProfile.LeadId)
                    .OrderByDescending(x => x.SubmittedUtc)
                    .ThenByDescending(x => x.CapturedUtc)
                    .Select(x => (Guid?)x.Id)
                    .FirstOrDefaultAsync();
            }
            catch (Exception ex) when (IsMissingWebsiteLeadIntakeLinksTable(ex))
            {
                _logger.LogWarning(ex, "WebsiteLeadIntakeLinks table is unavailable; calendar event will persist without intake linkage.");
            }

            var leadAppointment = new LeadAppointment
            {
                Id = Guid.NewGuid(),
                WorkstationLeadId = resolvedLeadProfile.LeadId,
                OwnerAgentUserId = string.IsNullOrWhiteSpace(resolvedLeadProfile.AgentUserId) ? agentOid : resolvedLeadProfile.AgentUserId,
                WebsiteLeadIntakeLinkId = latestIntakeLinkId,
                BookingProvider = "microsoft_bookings",
                BookingSource = LeadAppointmentBookingSources.InternalCalendar,
                RequestedBookingSource = LeadAppointmentBookingSources.InternalCalendar,
                ConfirmationSource = LeadAppointmentBookingSources.InternalCalendar,
                BookingAgentUserId = string.IsNullOrWhiteSpace(resolvedLeadProfile.AgentUserId) ? agentOid : resolvedLeadProfile.AgentUserId,
                CalendarEventId = created?.Id,
                CalendarEventWebLink = created?.WebLink,
                ScheduledStartUtc = utcStart,
                ScheduledEndUtc = utcEnd,
                MeetingUrl = zoomJoinUrl,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
                RequestedUtc = nowUtc
            };
            leadAppointment.ApplyStatus(LeadAppointmentStatus.Booked, nowUtc);

            resolvedLeadProfile.CrmNotes = ClientCrmMetaSerializer.Serialize(leadMeta);
            resolvedLeadProfile.UpdatedUtc = nowUtc;

            LeadAppointment? persistedAppointment = leadAppointment;
            _db.LeadAppointments.Add(leadAppointment);

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (Exception ex) when (IsMissingLeadAppointmentsTable(ex))
            {
                _logger.LogWarning(ex, "LeadAppointments table is unavailable; calendar event will persist without appointment linkage.");
                _db.Entry(leadAppointment).State = EntityState.Detached;
                persistedAppointment = null;
                await _db.SaveChangesAsync();
            }

            return Ok(new
            {
                ok = true,
                eventId = created?.Id,
                webLink = created?.WebLink,
                crmLastTouch = DateTime.Today.ToString("yyyy-MM-dd"),
                activities = leadMeta.Activities
                    .OrderByDescending(x => x.Date)
                    .ThenByDescending(x => x.CreatedUtc)
                    .ToList(),
                latestAppointment = BuildLeadAppointmentPayload(persistedAppointment)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Create calendar event failed.");
            return StatusCode(500, ex.Message);
        }
    }



    private static List<(DateTime Start, DateTime End)> SubtractBusyRanges(
        IEnumerable<(DateTime Start, DateTime End)> freeRanges,
        IEnumerable<(DateTime Start, DateTime End)> busyRanges)
    {
        var result = new List<(DateTime Start, DateTime End)>();

        foreach (var free in freeRanges.OrderBy(x => x.Start))
        {
            var segments = new List<(DateTime Start, DateTime End)> { free };

            foreach (var busy in busyRanges.Where(x => x.End > free.Start && x.Start < free.End).OrderBy(x => x.Start))
            {
                var next = new List<(DateTime Start, DateTime End)>();

                foreach (var seg in segments)
                {
                    if (busy.End <= seg.Start || busy.Start >= seg.End)
                    {
                        next.Add(seg);
                        continue;
                    }

                    if (busy.Start > seg.Start)
                        next.Add((seg.Start, busy.Start));

                    if (busy.End < seg.End)
                        next.Add((busy.End, seg.End));
                }

                segments = next;
                if (segments.Count == 0) break;
            }

            result.AddRange(segments.Where(x => x.End > x.Start));
        }

        return result;
    }

    private static int MinutesBetween(DateTime start, DateTime end)
        => Math.Max(1, (int)Math.Round((end - start).TotalMinutes));

    private async Task<BookingService?> ResolveBookingServiceByDurationAsync(string businessId, int durationMinutes, CancellationToken ct)
    {
        var services = await _appGraph.Solutions.BookingBusinesses[businessId].Services.GetAsync(cancellationToken: ct);

        _logger.LogInformation(
            "Bookings services: {Services}",
            JsonSerializer.Serialize(
                services?.Value?.Select(x => new
                {
                    x.Id,
                    x.DisplayName,
                    DurationMinutes = x.DefaultDuration.HasValue
                        ? (int)Math.Round(x.DefaultDuration.Value.TotalMinutes)
                        : 0,
                    x.IsHiddenFromCustomers
                })
            )
        );

        return services?.Value?
            .Where(x => x.IsHiddenFromCustomers != true && x.DefaultDuration.HasValue)
            .Select(x => new { Service = x, Minutes = (int)Math.Round(x.DefaultDuration!.Value.TotalMinutes) })
            .OrderBy(x => Math.Abs(x.Minutes - durationMinutes))
            .FirstOrDefault(x => Math.Abs(x.Minutes - durationMinutes) <= 2)
            ?.Service;
    }

    private sealed class CreatedCalendarLikeResponse
    {
        public string? Id { get; set; }
        public string? WebLink { get; set; }
        public string? SelfServiceAppointmentId { get; set; }
        public string? ServiceName { get; set; }
        public string? ServiceId { get; set; }
    }

    private static string? ExtractGraphErrorMessage(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(responseText);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("message", out var message))
                    return message.GetString();
            }
        }
        catch
        {
        }

        return responseText.Length <= 300 ? responseText : "Calendar create failed.";
    }
}
