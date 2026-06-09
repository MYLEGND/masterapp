using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProtectWebsite.Services.MetaSignal;

namespace ProtectWebsite.Services.Booking;

public sealed record PublicBookingConfirmationResult(
    bool Verified,
    bool PendingConfirmation,
    bool LinkedToLead,
    Guid? AppointmentId,
    string? AppointmentStatus,
    string? BookingSource,
    string? ConfirmationSource,
    string Reason,
    string? CalendarEventId,
    string? CalendarEventWebLink,
    DateTime? ScheduledStartUtc,
    DateTime? ScheduledEndUtc);

public sealed record PublicBookingCalendarMatchRequest(
    string? CalendarUserId,
    string? CalendarEmail,
    string? BookingPageIdOrMailbox,
    string? ExistingCalendarEventId,
    string? LeadFirstName,
    string? LeadLastName,
    string? LeadEmail,
    string? LeadPhone);

public sealed record PublicBookingCalendarMatchResult(
    string EventId,
    string? WebLink,
    DateTime? ScheduledStartUtc,
    DateTime? ScheduledEndUtc,
    string? MeetingUrl,
    string MatchReason);

public interface IPublicBookingCalendarMatcher
{
    Task<PublicBookingCalendarMatchResult?> TryMatchAsync(PublicBookingCalendarMatchRequest request, CancellationToken cancellationToken = default);
}

public interface IPublicBookingConfirmationService
{
    Task<PublicBookingConfirmationResult> TryConfirmAsync(PublicBookingContext context, CancellationToken cancellationToken = default);
}

public sealed class PublicBookingConfirmationService : IPublicBookingConfirmationService
{
    private readonly MasterAppDbContext _db;
    private readonly IPublicBookingCalendarMatcher _calendarMatcher;
    private readonly IPublicBookingResolver _publicBookingResolver;
    private readonly IMetaSignalIntelligenceService _metaSignal;
    private readonly ILogger<PublicBookingConfirmationService> _logger;

    public PublicBookingConfirmationService(
        MasterAppDbContext db,
        IPublicBookingCalendarMatcher calendarMatcher,
        IPublicBookingResolver publicBookingResolver,
        IMetaSignalIntelligenceService metaSignal,
        ILogger<PublicBookingConfirmationService> logger)
    {
        _db = db;
        _calendarMatcher = calendarMatcher;
        _publicBookingResolver = publicBookingResolver;
        _metaSignal = metaSignal;
        _logger = logger;
    }

    public async Task<PublicBookingConfirmationResult> TryConfirmAsync(
        PublicBookingContext context,
        CancellationToken cancellationToken = default)
    {
        var intakeLink = await _db.WebsiteLeadIntakeLinks
            .AsNoTracking()
            .Where(x => x.WebsiteLeadPublicId == context.WebsiteLeadId)
            .OrderByDescending(x => x.SubmittedUtc)
            .ThenByDescending(x => x.CapturedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (intakeLink == null || string.IsNullOrWhiteSpace(intakeLink.WorkstationLeadId))
        {
            return new PublicBookingConfirmationResult(
                Verified: false,
                PendingConfirmation: false,
                LinkedToLead: false,
                AppointmentId: null,
                AppointmentStatus: null,
                BookingSource: null,
                ConfirmationSource: null,
                Reason: "lead_not_linked",
                CalendarEventId: null,
                CalendarEventWebLink: null,
                ScheduledStartUtc: null,
                ScheduledEndUtc: null);
        }

        var appointment = await _db.LeadAppointments
            .Where(x => x.WorkstationLeadId == intakeLink.WorkstationLeadId &&
                        (x.WebsiteLeadIntakeLinkId == intakeLink.Id || x.WebsiteLeadIntakeLinkId == null))
            .OrderByDescending(x => x.UpdatedUtc)
            .ThenByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (appointment == null)
        {
            return new PublicBookingConfirmationResult(
                Verified: false,
                PendingConfirmation: false,
                LinkedToLead: true,
                AppointmentId: null,
                AppointmentStatus: null,
                BookingSource: null,
                ConfirmationSource: null,
                Reason: "appointment_not_found",
                CalendarEventId: null,
                CalendarEventWebLink: null,
                ScheduledStartUtc: null,
                ScheduledEndUtc: null);
        }

        if (IsTrustedBookedAppointment(appointment))
        {
            return BuildResult(appointment, verified: true, pendingConfirmation: false, reason: "already_confirmed");
        }

        var resolution = await _publicBookingResolver.ResolveAsync(
            new PublicBookingResolveContext(
                WebsiteLeadId: context.WebsiteLeadId,
                AgentTrackingProfileId: context.AgentTrackingProfileId,
                AgentUserId: context.AgentUserId,
                AgentSlug: context.AgentSlug),
            cancellationToken);

        ApplyBookingResolutionSnapshot(appointment, resolution);

        var leadProfile = await _db.WorkstationLeadProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.LeadId == intakeLink.WorkstationLeadId, cancellationToken);
        var websiteLead = await _db.WebsiteLeads
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.LeadId == context.WebsiteLeadId, cancellationToken);

        var calendarMatch = await _calendarMatcher.TryMatchAsync(
            new PublicBookingCalendarMatchRequest(
                CalendarUserId: appointment.BookingCalendarUserId,
                CalendarEmail: appointment.BookingCalendarEmail,
                BookingPageIdOrMailbox: appointment.BookingPageIdOrMailbox,
                ExistingCalendarEventId: appointment.CalendarEventId,
                LeadFirstName: leadProfile?.FirstName ?? websiteLead?.FirstName,
                LeadLastName: leadProfile?.LastName ?? websiteLead?.LastName,
                LeadEmail: !string.IsNullOrWhiteSpace(websiteLead?.Email) ? websiteLead.Email : leadProfile?.Email,
                LeadPhone: !string.IsNullOrWhiteSpace(websiteLead?.Phone) ? websiteLead.Phone : leadProfile?.Phone),
            cancellationToken);

        if (calendarMatch == null)
        {
            var pendingUtc = DateTime.UtcNow;
            appointment.LastSyncedUtc = pendingUtc;
            appointment.LastSyncStatus = "graph_fallback_pending";
            appointment.LastSyncError = "No matching Microsoft Graph calendar event found.";
            appointment.UpdatedUtc = pendingUtc;
            await _db.SaveChangesAsync(cancellationToken);

            return BuildResult(
                appointment,
                verified: false,
                pendingConfirmation: true,
                reason: "confirmation_not_verified");
        }

        var nowUtc = DateTime.UtcNow;
        appointment.WorkstationLeadId = intakeLink.WorkstationLeadId;
        appointment.OwnerAgentUserId = string.IsNullOrWhiteSpace(appointment.OwnerAgentUserId)
            ? intakeLink.AgentUserId
            : appointment.OwnerAgentUserId;
        appointment.WebsiteLeadIntakeLinkId ??= intakeLink.Id;
        appointment.RequestedBookingSource = string.IsNullOrWhiteSpace(appointment.RequestedBookingSource)
            ? LeadAppointmentBookingSources.WebsiteEmbed
            : appointment.RequestedBookingSource;
        appointment.CalendarEventId = calendarMatch.EventId;
        appointment.CalendarEventWebLink = calendarMatch.WebLink;
        appointment.ScheduledStartUtc = calendarMatch.ScheduledStartUtc;
        appointment.ScheduledEndUtc = calendarMatch.ScheduledEndUtc;
        appointment.MeetingUrl = calendarMatch.MeetingUrl;
        appointment.BookingProvider = "microsoft_graph";
        appointment.BookingSource = LeadAppointmentBookingSources.MicrosoftGraphFallbackMatch;
        appointment.ConfirmationSource = LeadAppointmentBookingSources.MicrosoftGraphFallbackMatch;
        appointment.LastSyncedUtc = nowUtc;
        appointment.LastSyncStatus = calendarMatch.MatchReason;
        appointment.LastSyncError = null;
        if (!appointment.RequestedUtc.HasValue)
        {
            appointment.RequestedUtc = nowUtc;
        }
        appointment.ApplyStatus(LeadAppointmentStatus.Booked, nowUtc);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);

            try
            {
                if (websiteLead != null)
                {
                    await _metaSignal.RecordAppointmentBookedAsync(
                        new MetaSignalAppointmentBookedRequest
                        {
                            AppointmentId = appointment.Id,
                            LeadId = websiteLead.LeadId,
                            QuoteType = websiteLead.InterestType ?? string.Empty,
                            PageKey = websiteLead.SourcePageKey ?? string.Empty,
                            EffectivePageKey = websiteLead.SourcePageKey ?? string.Empty,
                            PageMode = string.Empty,
                            SessionId = websiteLead.SessionId,
                            VisitorId = websiteLead.VisitorId,
                            AgentTrackingProfileId = websiteLead.AgentTrackingProfileId ?? resolution.AgentTrackingProfileId,
                            AgentSlug = websiteLead.AgentSlug ?? resolution.AgentSlug,
                            UtmSource = websiteLead.UtmSource,
                            UtmMedium = websiteLead.UtmMedium,
                            UtmCampaign = websiteLead.UtmCampaign,
                            UtmId = websiteLead.UtmId,
                            Fbclid = websiteLead.Fbclid,
                            Email = !string.IsNullOrWhiteSpace(websiteLead.Email) ? websiteLead.Email : leadProfile?.Email,
                            Phone = !string.IsNullOrWhiteSpace(websiteLead.Phone) ? websiteLead.Phone : leadProfile?.Phone,
                            AllowHashedContactData = true,
                            CalendarEventId = appointment.CalendarEventId,
                            CalendarEventWebLink = appointment.CalendarEventWebLink,
                            ScheduledStartUtc = appointment.ScheduledStartUtc,
                            ScheduledEndUtc = appointment.ScheduledEndUtc,
                            BookingSource = appointment.BookingSource,
                            ConfirmationSource = appointment.ConfirmationSource
                        },
                        null,
                        cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "MetaSignal AppointmentBooked failed for WebsiteLead {LeadId} appointment {AppointmentId}.",
                    context.WebsiteLeadId,
                    appointment.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Public booking confirmation save failed for WebsiteLead {LeadId} appointment {AppointmentId}.",
                context.WebsiteLeadId,
                appointment.Id);
            throw;
        }

        return BuildResult(
            appointment,
            verified: true,
            pendingConfirmation: false,
            reason: calendarMatch.MatchReason);
    }

    private static bool IsTrustedBookedAppointment(LeadAppointment appointment)
    {
        var trustedSource = appointment.ConfirmationSource ?? appointment.BookingSource;
        return appointment.Status is LeadAppointmentStatus.Booked or LeadAppointmentStatus.Confirmed or LeadAppointmentStatus.Completed &&
            (string.Equals(trustedSource, LeadAppointmentBookingSources.InternalCalendar, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(trustedSource, LeadAppointmentBookingSources.MicrosoftGraphConfirmation, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(trustedSource, LeadAppointmentBookingSources.MicrosoftGraphWebhook, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(trustedSource, LeadAppointmentBookingSources.MicrosoftGraphFallbackMatch, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(trustedSource, LeadAppointmentBookingSources.ManualVerified, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyBookingResolutionSnapshot(LeadAppointment appointment, PublicBookingResolution resolution)
    {
        appointment.BookingConfigurationSource = resolution.ConfigurationSource;
        appointment.BookingTrackingProfileId = resolution.AgentTrackingProfileId;
        appointment.BookingAgentUserId = resolution.AgentUserId;
        appointment.BookingAgentSlug = resolution.AgentSlug;
        appointment.BookingCalendarUserId = resolution.CalendarUserId;
        appointment.BookingCalendarEmail = resolution.CalendarEmail;
        appointment.BookingPageIdOrMailbox = resolution.BookingPageIdOrMailbox;
    }

    private static PublicBookingConfirmationResult BuildResult(
        LeadAppointment appointment,
        bool verified,
        bool pendingConfirmation,
        string reason)
    {
        return new PublicBookingConfirmationResult(
            Verified: verified,
            PendingConfirmation: pendingConfirmation,
            LinkedToLead: true,
            AppointmentId: appointment.Id,
            AppointmentStatus: appointment.Status.ToString(),
            BookingSource: appointment.BookingSource,
            ConfirmationSource: appointment.ConfirmationSource,
            Reason: reason,
            CalendarEventId: appointment.CalendarEventId,
            CalendarEventWebLink: appointment.CalendarEventWebLink,
            ScheduledStartUtc: appointment.ScheduledStartUtc,
            ScheduledEndUtc: appointment.ScheduledEndUtc);
    }
}

public sealed class MicrosoftGraphPublicBookingCalendarMatcher : IPublicBookingCalendarMatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IConfiguration _configuration;
    private readonly ILogger<MicrosoftGraphPublicBookingCalendarMatcher> _logger;

    public MicrosoftGraphPublicBookingCalendarMatcher(
        IConfiguration configuration,
        ILogger<MicrosoftGraphPublicBookingCalendarMatcher> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<PublicBookingCalendarMatchResult?> TryMatchAsync(
        PublicBookingCalendarMatchRequest request,
        CancellationToken cancellationToken = default)
    {
        var calendarIdentity = FirstNotEmpty(
            request.CalendarUserId,
            request.CalendarEmail,
            request.BookingPageIdOrMailbox);
        if (string.IsNullOrWhiteSpace(calendarIdentity))
        {
            return null;
        }

        var accessToken = await TryGetAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(request.ExistingCalendarEventId))
        {
            var directEvent = await TryGetEventByIdAsync(
                calendarIdentity,
                request.ExistingCalendarEventId!,
                accessToken,
                cancellationToken);
            if (directEvent != null)
            {
                return MapEvent(directEvent, "calendar_event_id");
            }
        }

        var fromUtc = DateTime.UtcNow.AddHours(-12);
        var toUtc = DateTime.UtcNow.AddDays(60);
        var url = QueryHelpers.AddQueryString(
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(calendarIdentity)}/calendar/calendarView",
            new Dictionary<string, string?>
            {
                ["startDateTime"] = fromUtc.ToString("o"),
                ["endDateTime"] = toUtc.ToString("o"),
                ["$top"] = "50",
                ["$select"] = "id,webLink,subject,bodyPreview,start,end,attendees,onlineMeeting"
            });

        try
        {
            using var client = new HttpClient();
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await client.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Public booking Graph lookup failed for calendar {CalendarIdentity}. status={StatusCode}",
                    calendarIdentity,
                    (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<GraphCalendarViewResponse>(stream, JsonOptions, cancellationToken);
            var match = payload?.Value?
                .Select(item => new { Event = item, Score = ScoreEvent(item, request) })
                .OrderByDescending(item => item.Score)
                .FirstOrDefault(item => item.Score >= 90);

            return match == null
                ? null
                : MapEvent(match.Event, $"match_score_{match.Score}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Public booking Graph lookup threw while searching calendar {CalendarIdentity}.",
                calendarIdentity);
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
            _logger.LogWarning("Public booking Graph lookup is disabled because Azure AD application credentials are not configured.");
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
            _logger.LogWarning(ex, "Failed to acquire Graph application token for public booking confirmation.");
            return null;
        }
    }

    private async Task<GraphCalendarEvent?> TryGetEventByIdAsync(
        string calendarIdentity,
        string eventId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(calendarIdentity)}/events/{Uri.EscapeDataString(eventId)}?$select=id,webLink,subject,bodyPreview,start,end,attendees,onlineMeeting");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<GraphCalendarEvent>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Direct Graph event lookup failed for calendar {CalendarIdentity} event {EventId}.", calendarIdentity, eventId);
            return null;
        }
    }

    private static int ScoreEvent(GraphCalendarEvent evt, PublicBookingCalendarMatchRequest request)
    {
        var score = 0;
        var fullName = string.Join(" ", new[] { request.LeadFirstName, request.LeadLastName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim()))
            .Trim();
        var normalizedEmail = NormalizeEmail(request.LeadEmail);
        var normalizedPhone = NormalizePhone(request.LeadPhone);
        var subject = (evt.Subject ?? string.Empty).Trim();
        var body = (evt.BodyPreview ?? string.Empty).Trim();
        var eventAttendeeEmails = evt.Attendees?
            .Select(attendee => NormalizeEmail(attendee.EmailAddress?.Address))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList() ?? new List<string?>();

        if (!string.IsNullOrWhiteSpace(normalizedEmail) &&
            eventAttendeeEmails.Any(value => string.Equals(value, normalizedEmail, StringComparison.OrdinalIgnoreCase)))
        {
            score += 120;
        }

        if (!string.IsNullOrWhiteSpace(fullName) &&
            subject.Contains(fullName, StringComparison.OrdinalIgnoreCase))
        {
            score += 70;
        }

        if (!string.IsNullOrWhiteSpace(normalizedEmail) &&
            body.Contains(normalizedEmail, StringComparison.OrdinalIgnoreCase))
        {
            score += 60;
        }

        if (!string.IsNullOrWhiteSpace(normalizedPhone))
        {
            var digits = NormalizePhone(body);
            if (!string.IsNullOrWhiteSpace(digits) &&
                digits.Contains(normalizedPhone, StringComparison.OrdinalIgnoreCase))
            {
                score += 35;
            }
        }

        return score;
    }

    private static PublicBookingCalendarMatchResult MapEvent(GraphCalendarEvent evt, string reason)
    {
        return new PublicBookingCalendarMatchResult(
            EventId: evt.Id ?? string.Empty,
            WebLink: Clean(evt.WebLink),
            ScheduledStartUtc: ParseGraphDateTime(evt.Start),
            ScheduledEndUtc: ParseGraphDateTime(evt.End),
            MeetingUrl: Clean(evt.OnlineMeeting?.JoinUrl),
            MatchReason: reason);
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
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private static string? NormalizeEmail(string? email)
    {
        return string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
    }

    private static string? NormalizePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(digits) ? null : digits;
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed class GraphCalendarViewResponse
    {
        public List<GraphCalendarEvent>? Value { get; set; }
    }

    private sealed class GraphCalendarEvent
    {
        public string? Id { get; set; }
        public string? WebLink { get; set; }
        public string? Subject { get; set; }
        public string? BodyPreview { get; set; }
        public GraphDateTimeTimeZone? Start { get; set; }
        public GraphDateTimeTimeZone? End { get; set; }
        public List<GraphAttendee>? Attendees { get; set; }
        public GraphOnlineMeeting? OnlineMeeting { get; set; }
    }

    private sealed class GraphDateTimeTimeZone
    {
        public string? DateTime { get; set; }
        public string? TimeZone { get; set; }
    }

    private sealed class GraphAttendee
    {
        public GraphEmailAddress? EmailAddress { get; set; }
    }

    private sealed class GraphEmailAddress
    {
        public string? Address { get; set; }
    }

    private sealed class GraphOnlineMeeting
    {
        public string? JoinUrl { get; set; }
    }
}
