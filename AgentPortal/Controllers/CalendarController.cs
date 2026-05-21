using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using AgentPortal.Filters;
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

    // Scopes must match what you consent to in /calendar/connect
    private static readonly string[] CalendarScopes = new[] { "offline_access", "Calendars.ReadWrite" };
    private static readonly string[] CalendarAvailabilityScopes = new[] { "offline_access", "Calendars.ReadWrite", "MailboxSettings.Read" };
    private static readonly TimeSpan DefaultWorkdayStart = new(7, 0, 0);
    private static readonly TimeSpan DefaultWorkdayEnd = new(19, 0, 0);

    public CalendarController(ITokenAcquisition tokenAcquisition,
        ILogger<CalendarController> logger,
        MasterAppDbContext db,
        IHttpClientFactory httpClientFactory)
    {
        _tokenAcquisition = tokenAcquisition;
        _logger = logger;
        _db = db;
        _httpClientFactory = httpClientFactory;
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
            LeadAppointmentBookingSources.WorkstationCalendar => "Workstation calendar",
            LeadAppointmentBookingSources.WebsiteEmbed => "Website embed",
            LeadAppointmentBookingSources.WebsiteModal => "Website modal",
            LeadAppointmentBookingSources.ExternalRedirectFallback => "External redirect fallback",
            _ => string.IsNullOrWhiteSpace(source) ? "Internal manual" : source.Trim()
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
            calendarEventId = appointment.CalendarEventId,
            calendarEventWebLink = appointment.CalendarEventWebLink,
            scheduledStartUtc = appointment.ScheduledStartUtc,
            scheduledEndUtc = appointment.ScheduledEndUtc,
            meetingUrl = appointment.MeetingUrl,
            createdUtc = appointment.CreatedUtc,
            updatedUtc = appointment.UpdatedUtc,
            lastStatusChangedUtc = appointment.LastStatusChangedUtc,
            requestedUtc = appointment.RequestedUtc,
            bookedUtc = appointment.BookedUtc,
            confirmedUtc = appointment.ConfirmedUtc,
            completedUtc = appointment.CompletedUtc,
            noShowUtc = appointment.NoShowUtc,
            cancelledUtc = appointment.CancelledUtc,
            rescheduledUtc = appointment.RescheduledUtc
        };
    }

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
            var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(CalendarScopes);

            using var httpClient = _httpClientFactory.CreateClient("ResilientDefault");
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

            var dayStart = new DateTimeOffset(localDate.Date, TimeZoneInfo.Local.GetUtcOffset(localDate.Date));
            var dayEnd = dayStart.AddDays(1);
            var url =
                "https://graph.microsoft.com/v1.0/me/calendar/calendarView" +
                $"?startDateTime={Uri.EscapeDataString(dayStart.ToString("o", CultureInfo.InvariantCulture))}" +
                $"&endDateTime={Uri.EscapeDataString(dayEnd.ToString("o", CultureInfo.InvariantCulture))}" +
                "&$select=id,subject,start,end,showAs,isAllDay&$orderby=start/dateTime";

            using var response = await httpClient.GetAsync(url);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Graph day availability failed. Status={StatusCode}. Body={Body}", (int)response.StatusCode, responseText);
                return StatusCode((int)response.StatusCode, ExtractGraphErrorMessage(responseText) ?? "Calendar availability failed.");
            }

            var parsed = JsonSerializer.Deserialize<CalendarViewResponse>(responseText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            MailboxSettingsResponse? mailbox = null;
            try
            {
                var mailboxToken = await _tokenAcquisition.GetAccessTokenForUserAsync(CalendarAvailabilityScopes);
                using var mailboxClient = _httpClientFactory.CreateClient("ResilientDefault");
                mailboxClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", mailboxToken);
                mailboxClient.DefaultRequestHeaders.Add("Accept", "application/json");
                using var mailboxResponse = await mailboxClient.GetAsync("https://graph.microsoft.com/v1.0/me/mailboxSettings?$select=timeZone,workingHours");
                var mailboxText = await mailboxResponse.Content.ReadAsStringAsync();

                if (mailboxResponse.IsSuccessStatusCode)
                {
                    mailbox = JsonSerializer.Deserialize<MailboxSettingsResponse>(mailboxText, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
                else
                {
                    _logger.LogWarning("Graph mailbox settings unavailable. Status={StatusCode}. Body={Body}", (int)mailboxResponse.StatusCode, mailboxText);
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "MailboxSettings.Read unavailable. Falling back to default work hours.");
            }

            var busyStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "busy",
                "tentative",
                "oof",
                "workingElsewhere"
            };

            var items = (parsed?.Value ?? new List<CalendarViewEvent>())
                .Where(x => x.IsAllDay || busyStates.Contains(x.ShowAs ?? ""))
                .Select(x =>
                {
                    var start = ParseGraphDateTime(x.Start);
                    var end = ParseGraphDateTime(x.End);

                    return new
                    {
                        id = x.Id ?? "",
                        subject = string.IsNullOrWhiteSpace(x.Subject) ? "Busy" : x.Subject.Trim(),
                        showAs = string.IsNullOrWhiteSpace(x.ShowAs) ? "busy" : x.ShowAs!.Trim().ToLowerInvariant(),
                        isAllDay = x.IsAllDay,
                        startIso = start == DateTimeOffset.MinValue ? "" : start.ToString("o", CultureInfo.InvariantCulture),
                        endIso = end == DateTimeOffset.MinValue ? "" : end.ToString("o", CultureInfo.InvariantCulture),
                        startLabel = x.IsAllDay
                            ? "All day"
                            : (start == DateTimeOffset.MinValue ? "" : start.ToString("h:mm tt", CultureInfo.InvariantCulture)),
                        endLabel = x.IsAllDay
                            ? "All day"
                            : (end == DateTimeOffset.MinValue ? "" : end.ToString("h:mm tt", CultureInfo.InvariantCulture))
                    };
                })
                .OrderBy(x => x.isAllDay ? 0 : 1)
                .ThenBy(x => x.startIso)
                .ToList();

            var workingHours = mailbox?.WorkingHours;
            var isWorkingDay = IsWorkingDay(localDate, workingHours);
            var workStartTime = ParseWorkingTime(workingHours?.StartTime, DefaultWorkdayStart);
            var workEndTime = ParseWorkingTime(workingHours?.EndTime, DefaultWorkdayEnd);
            var workStart = localDate.Date.Add(workStartTime);
            var workEnd = localDate.Date.Add(workEndTime);

            if (workEnd <= workStart)
                workEnd = workStart.AddHours(8);

            var busyRanges = items
                .Select(x => new
                {
                    IsAllDay = x.isAllDay,
                    Start = DateTimeOffset.TryParse(x.startIso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start)
                        ? start.LocalDateTime
                        : DateTime.MinValue,
                    End = DateTimeOffset.TryParse(x.endIso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end)
                        ? end.LocalDateTime
                        : DateTime.MinValue
                })
                .OrderBy(x => x.IsAllDay ? DateTime.MinValue : x.Start)
                .ToList();

            var freeSlots = new List<object>();

            if (isWorkingDay && !busyRanges.Any(x => x.IsAllDay))
            {
                var cursor = workStart;
                foreach (var busy in busyRanges.Where(x => !x.IsAllDay && x.End > workStart && x.Start < workEnd))
                {
                    var busyStart = busy.Start < workStart ? workStart : busy.Start;
                    var busyEnd = busy.End > workEnd ? workEnd : busy.End;

                if (busyStart > cursor)
                {
                    freeSlots.Add(new
                    {
                        startIso = new DateTimeOffset(cursor, TimeZoneInfo.Local.GetUtcOffset(cursor)).ToString("o", CultureInfo.InvariantCulture),
                        endIso = new DateTimeOffset(busyStart, TimeZoneInfo.Local.GetUtcOffset(busyStart)).ToString("o", CultureInfo.InvariantCulture),
                        startTimeValue = cursor.ToString("HH:mm", CultureInfo.InvariantCulture),
                        startLabel = cursor.ToString("h:mm tt", CultureInfo.InvariantCulture),
                        endLabel = busyStart.ToString("h:mm tt", CultureInfo.InvariantCulture)
                    });
                }

                    if (busyEnd > cursor)
                        cursor = busyEnd;
                }

                if (cursor < workEnd)
                {
                    freeSlots.Add(new
                    {
                        startIso = new DateTimeOffset(cursor, TimeZoneInfo.Local.GetUtcOffset(cursor)).ToString("o", CultureInfo.InvariantCulture),
                        endIso = new DateTimeOffset(workEnd, TimeZoneInfo.Local.GetUtcOffset(workEnd)).ToString("o", CultureInfo.InvariantCulture),
                        startTimeValue = cursor.ToString("HH:mm", CultureInfo.InvariantCulture),
                        startLabel = cursor.ToString("h:mm tt", CultureInfo.InvariantCulture),
                        endLabel = workEnd.ToString("h:mm tt", CultureInfo.InvariantCulture)
                    });
                }
            }

            return Ok(new
            {
                connected = true,
                date = localDate.ToString("yyyy-MM-dd"),
                items,
                workHours = new
                {
                    enabled = isWorkingDay,
                    source = mailbox?.WorkingHours != null ? "outlook" : "default",
                    startLabel = workStart.ToString("h:mm tt", CultureInfo.InvariantCulture),
                    endLabel = workEnd.ToString("h:mm tt", CultureInfo.InvariantCulture)
                },
                freeSlots
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Calendar day availability failed.");
            return Ok(new
            {
                connected = false,
                date = localDate.ToString("yyyy-MM-dd"),
                items = Array.Empty<object>(),
                freeSlots = Array.Empty<object>()
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

            // Ensure token exists (will throw if not consented)
            var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(CalendarScopes);

            var httpClient = _httpClientFactory.CreateClient("ResilientDefault");
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var zoomJoinUrl = string.IsNullOrWhiteSpace(req.ZoomJoinUrl) ? null : req.ZoomJoinUrl.Trim();
            var displayLocation = string.IsNullOrWhiteSpace(req.Location) ? "" : req.Location.Trim();
            var encodedBody = System.Net.WebUtility.HtmlEncode(req.Body ?? "").Replace("\n", "<br/>");
            var htmlBody = $"""
                           <div>{encodedBody}</div>
                           {(string.IsNullOrWhiteSpace(zoomJoinUrl) ? "" : $"<p><strong>Zoom:</strong> <a href=\"{System.Net.WebUtility.HtmlEncode(zoomJoinUrl)}\">Join Meeting</a></p>")}
                           """;
            var utcStart = localStart.ToUniversalTime();
            var utcEnd = localEnd.ToUniversalTime();
            var graphPayload = new
            {
                subject = req.Subject.Trim(),
                start = new
                {
                    dateTime = utcStart.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                    timeZone = "UTC"
                },
                end = new
                {
                    dateTime = utcEnd.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                    timeZone = "UTC"
                },
                body = new
                {
                    contentType = "HTML",
                    content = htmlBody
                },
                isReminderOn = true,
                reminderMinutesBeforeStart = 30,
                location = new { displayName = displayLocation }
            };

            using var response = await httpClient.PostAsync(
                "https://graph.microsoft.com/v1.0/me/events",
                new StringContent(JsonSerializer.Serialize(graphPayload), Encoding.UTF8, "application/json"));

            var responseText = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Graph event create failed. Status={StatusCode}. Body={Body}", (int)response.StatusCode, responseText);
                return StatusCode((int)response.StatusCode, ExtractGraphErrorMessage(responseText) ?? "Calendar create failed.");
            }

            var created = JsonSerializer.Deserialize<GraphEventResponse>(responseText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (profile != null)
            {
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

                profile.CrmLastTouch = DateTime.Today;
                profile.CrmNotes = ClientCrmMetaSerializer.Serialize(meta);
                profile.UpdatedUtc = DateTime.UtcNow;
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
                        .ToList()
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
                BookingSource = LeadAppointmentBookingSources.WorkstationCalendar,
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
