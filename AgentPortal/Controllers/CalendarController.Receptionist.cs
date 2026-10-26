using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentPortal.Services;
using Domain.Accounts;
using Domain.Entities;
using Domain.Enums;
using Domain.Messaging;
using Infrastructure.Bookings;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Shared.Analytics;

namespace AgentPortal.Controllers;

public partial class CalendarController
{
    private ReceptionistCalendarContext? receptionistBooking;
    private sealed class ReceptionistCalendarContext(ReceptionistTrustedBookingTarget target, string actorId, string businessId,
        string targetSha256, string? staffId = null, IReceptionistBookingExecutionFence? fence = null)
    {
        internal ReceptionistTrustedBookingTarget Target { get; } = target;
        internal string ActorId { get; } = actorId;
        internal string BusinessId { get; } = businessId;
        internal string TargetSha256 { get; } = targetSha256;
        internal string? StaffId { get; set; } = staffId;
        internal IReceptionistBookingExecutionFence? Fence { get; } = fence;
    }

    private TimeZoneInfo CalendarTimeZone() => receptionistBooking is null
        ? _agentTimeZoneResolver.Resolve(HttpContext) : TimeZoneInfo.FindSystemTimeZoneById(receptionistBooking.Target.Service.TimeZoneId);

    private Task<GraphServiceClient> CalendarGraphAsync(MarketingOwnerScope owner, CancellationToken ct) => receptionistBooking is null
        ? _calendarConnections.CreateGraphClientAsync(owner, ct)
        : _calendarConnections.CreateReviewedBookingClientAsync(owner, receptionistBooking.Target.CalendarConfigurationSha256, ct);

    [NonAction]
    internal async Task<IReadOnlyList<ReceptionistCalendarSlot>> ReadReceptionistSlotsAsync(ReceptionistTrustedBookingTarget target,
        DateOnly date, int minimumNoticeMinutes, int maximumAdvanceDays, CancellationToken ct, Func<DateTime, bool>? eligibleStart = null)
    {
        if (receptionistBooking is not null) throw new InvalidOperationException("A calendar command is already active.");
        receptionistBooking = await ResolveReceptionistTargetAsync(target, null, null, ct);
        try
        {
            var result = await DayAvailability(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (result is not OkObjectResult { Value: not null } ok) return [];
            var payload = JsonSerializer.SerializeToElement(ok.Value);
            if (!payload.GetProperty("connected").GetBoolean()) return [];
            var buffer = payload.GetProperty("buffers").EnumerateArray().Single();
            var pre = buffer.GetProperty("preBufferMinutes").GetInt32();
            var post = buffer.GetProperty("postBufferMinutes").GetInt32();
            var interval = payload.GetProperty("slotIntervalMinutes").GetInt32();
            if (pre < 0 || post < 0 || pre > 240 || post > 240 || interval is < 1 or > 240) return [];
            var zone = CalendarTimeZone(); var now = DateTime.UtcNow;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone));
            if (date < today || date > today.AddDays(maximumAdvanceDays)) return [];
            var slots = new List<ReceptionistCalendarSlot>();
            foreach (var range in payload.GetProperty("freeSlots").EnumerateArray())
            {
                var start = DateTime.Parse(range.GetProperty("startIso").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.None).AddMinutes(pre);
                var limit = DateTime.Parse(range.GetProperty("endIso").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.None).AddMinutes(-post);
                start = start.Date.AddMinutes(Math.Ceiling(start.TimeOfDay.TotalMinutes / interval) * interval);
                for (; start.AddMinutes(target.Service.DurationMinutes) <= limit && slots.Count < 3; start = start.AddMinutes(interval))
                {
                    var end = start.AddMinutes(target.Service.DurationMinutes);
                    if (zone.IsInvalidTime(start) || zone.IsAmbiguousTime(start) || zone.IsInvalidTime(end) || zone.IsAmbiguousTime(end)) continue;
                    var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(start, DateTimeKind.Unspecified), zone);
                    var utcEnd = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(end, DateTimeKind.Unspecified), zone);
                    if (eligibleStart?.Invoke(utc) == false || utc <= now.AddMinutes(minimumNoticeMinutes) || utcEnd - utc != TimeSpan.FromMinutes(target.Service.DurationMinutes)) continue;
                    slots.Add(new(utc, utcEnd, receptionistBooking.StaffId!, receptionistBooking.TargetSha256));
                }
                if (slots.Count == 3) break;
            }
            return slots;
        }
        finally { receptionistBooking = null; }
    }

    [NonAction]
    internal async Task CreateReceptionistAppointmentAsync(ReceptionistTrustedBookingCommit command,
        IReceptionistBookingExecutionFence fence, CancellationToken ct)
    {
        if (!command.CustomerEmailConsent || receptionistBooking is not null)
            throw new InvalidOperationException("A distinct confirmed email invitation is required.");
        receptionistBooking = await ResolveReceptionistTargetAsync(command.Target, command.Slot.StaffId, fence, ct);
        try
        {
            if (receptionistBooking.TargetSha256 != command.Slot.TargetSha256)
                throw new InvalidOperationException("The approved calendar target changed.");
            var zone = CalendarTimeZone();
            var start = TimeZoneInfo.ConvertTimeFromUtc(command.Slot.StartUtc, zone);
            var end = TimeZoneInfo.ConvertTimeFromUtc(command.Slot.EndUtc, zone);
            if (zone.IsInvalidTime(start) || zone.IsAmbiguousTime(start) || zone.IsInvalidTime(end) || zone.IsAmbiguousTime(end) ||
                command.Slot.StartUtc.Kind != DateTimeKind.Utc || command.Slot.EndUtc - command.Slot.StartUtc != TimeSpan.FromMinutes(command.Target.Service.DurationMinutes))
                throw new InvalidOperationException("The appointment timezone or duration is ambiguous.");
            // This calls the existing engine, locks, final availability check, provider
            // create and canonical LeadAppointment writer. Human responses stay internal.
            _ = await CreateEvent(new CreateEventRequest
            {
                ClientUserId = command.Target.WorkstationLeadId, WorkstationLeadId = command.Target.WorkstationLeadId,
                ScopedBusinessId = command.Target.Owner.CommerceBusinessId, ScopedBookingAgentUserId = receptionistBooking.ActorId,
                ServiceId = command.Target.Service.BookingServiceId, Subject = "Caller-confirmed service appointment",
                StartISO = start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                EndISO = end.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                Body = "Appointment requested and confirmed during the incoming call.", ActivityNote = "Caller-confirmed appointment with explicit email invitation consent."
            });
        }
        finally { receptionistBooking = null; }
    }

    [NonAction]
    internal async Task<ReceptionistCalendarSlot?> ReadOriginalReceptionistSlotAsync(ReceptionistTrustedBookingTarget target,
        string originalAppointmentId, CancellationToken ct)
    {
        var context = await ResolveReceptionistTargetAsync(target, null, null, ct);
        if (string.IsNullOrWhiteSpace(originalAppointmentId) || originalAppointmentId.Length > 400) return null;
        var graph = await _calendarConnections.CreateReviewedBookingClientAsync(target.Owner, target.CalendarConfigurationSha256, ct);
        var record = await graph.Solutions.BookingBusinesses[context.BusinessId].Appointments[originalAppointmentId].GetAsync(request =>
            request.QueryParameters.Select = ["id", "serviceId", "startDateTime", "endDateTime", "staffMemberIds"], ct);
        if (record?.Id != originalAppointmentId || record.ServiceId != target.Service.BookingServiceId ||
            record.StaffMemberIds?.Count != 1 || string.IsNullOrWhiteSpace(record.StaffMemberIds[0]) ||
            ProviderUtc(record.StartDateTime) is not { } start || ProviderUtc(record.EndDateTime) is not { } end ||
            end - start != TimeSpan.FromMinutes(target.Service.DurationMinutes)) return null;
        return new(start, end, record.StaffMemberIds[0], context.TargetSha256);
    }

    [NonAction]
    internal async Task<bool> VerifyReceptionistAppointmentAsync(ReceptionistTrustedBookingCommit command,
        ReceptionistProviderBookingIdentity identity, CancellationToken ct)
    {
        var context = await ResolveReceptionistTargetAsync(command.Target, command.Slot.StaffId, null, ct);
        if (identity.TargetSha256 != context.TargetSha256 || command.Slot.TargetSha256 != context.TargetSha256 ||
            string.IsNullOrWhiteSpace(identity.AppointmentId)) return false;
        var normalizedTarget = CalendarBookingLock.NormalizeTarget(context.BusinessId);
        await using var fresh = new MasterAppDbContext(HttpContext.RequestServices.GetRequiredService<DbContextOptions<MasterAppDbContext>>());
        var persisted = await fresh.LeadAppointments.AsNoTracking().Where(x => x.CalendarEventId == identity.AppointmentId &&
            x.WorkstationLeadId == command.Target.WorkstationLeadId && x.WebsiteLeadIntakeLinkId == command.Target.IntakeLinkId &&
            x.OwnerAgentUserId == context.ActorId && x.CommerceBusinessId == command.Target.Owner.CommerceBusinessId &&
            x.BookingPageIdOrMailbox == normalizedTarget &&
            x.BookingProvider == "microsoft_bookings" && x.ConfirmationSource == LeadAppointmentBookingSources.InternalCalendar &&
            (x.Status == LeadAppointmentStatus.Booked || x.Status == LeadAppointmentStatus.Confirmed) &&
            x.ScheduledStartUtc == command.Slot.StartUtc && x.ScheduledEndUtc == command.Slot.EndUtc).Select(x => x.Id).ToArrayAsync(ct);
        if (persisted.Length != 1) return false;
        var graph = await _calendarConnections.CreateReviewedBookingClientAsync(command.Target.Owner, command.Target.CalendarConfigurationSha256, ct);
        var record = await graph.Solutions.BookingBusinesses[context.BusinessId].Appointments[identity.AppointmentId].GetAsync(request =>
        {
            request.QueryParameters.Select = ["id", "serviceId", "startDateTime", "endDateTime", "customerName", "customerEmailAddress",
                "customers", "staffMemberIds", "optOutOfCustomerEmail", "smsNotificationsEnabled"];
        }, ct);
        return record?.Id == identity.AppointmentId && record.ServiceId == command.Target.Service.BookingServiceId &&
            ProviderUtc(record.StartDateTime) == command.Slot.StartUtc && ProviderUtc(record.EndDateTime) == command.Slot.EndUtc &&
            record.StaffMemberIds?.Count == 1 && record.StaffMemberIds[0] == command.Slot.StaffId &&
            record.CustomerName == command.Target.CustomerName && string.Equals(record.CustomerEmailAddress, command.Target.InvitationEmail, StringComparison.OrdinalIgnoreCase) &&
            record.Customers?.Count == 1 && record.Customers[0] is BookingCustomerInformation customer &&
            string.Equals(customer.EmailAddress, command.Target.InvitationEmail, StringComparison.OrdinalIgnoreCase) &&
            record.OptOutOfCustomerEmail == false && record.SmsNotificationsEnabled == false;
    }

    private async Task<ReceptionistCalendarContext> ResolveReceptionistTargetAsync(ReceptionistTrustedBookingTarget target,
        string? staff, IReceptionistBookingExecutionFence? fence, CancellationToken ct)
    {
        var reader = HttpContext.RequestServices.GetRequiredService<ReceptionistBookingReadinessReader>();
        if (!await reader.MatchesReviewedConnectionAsync(target.Owner, target.CalendarConfigurationSha256, ct))
            throw new InvalidOperationException("The reviewed calendar connection is unavailable.");
        var link = await _db.WebsiteLeadIntakeLinks.AsNoTracking().Where(x => x.Id == target.IntakeLinkId &&
            x.WorkstationLeadId == target.WorkstationLeadId && x.CommerceBusinessId == target.Owner.CommerceBusinessId)
            .Select(x => new { x.AgentUserId }).SingleOrDefaultAsync(ct) ?? throw new InvalidOperationException("The canonical intake target is unavailable.");
        AgentProfile? agent;
        if (target.Owner.CommerceBusinessId is { } business)
        {
            if (link.AgentUserId != "" || target.BookingAgentProfileId is not { } profileId)
                throw new InvalidOperationException("An existing attached booking agent is required.");
            agent = await BusinessBookingAccess.ResolveAttachedAgentAsync(_db, business, profileId, ct);
        }
        else
        {
            if (target.Owner.AgentTrackingProfileId is { } tracking && !await _db.AgentTrackingProfiles.AsNoTracking()
                .AnyAsync(x => x.Id == tracking && x.AgentUserId == link.AgentUserId, ct))
                throw new InvalidOperationException("The canonical agent scope changed.");
            agent = await _db.AgentProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.AgentUserId == link.AgentUserId && x.Id == target.BookingAgentProfileId, ct);
        }
        if (agent?.IsActive != true || !(agent.BookingEnabled ?? !string.IsNullOrWhiteSpace(agent.BookingPageIdOrMailbox)))
            throw new InvalidOperationException("The approved booking agent is disabled or unavailable.");
        if (await _db.AccountLifecycleRecords.AsNoTracking().AnyAsync(x => x.UserId == agent.AgentUserId &&
            x.ParticipantType == MessagingParticipantTypes.Agent && (x.ProfileId != agent.Id || x.State != AccountLifecycleStates.Active), ct))
            throw new InvalidOperationException("The booking agent account is unavailable.");
        var contact = await _db.WorkstationLeadProfiles.AsNoTracking().Where(x => x.LeadId == target.WorkstationLeadId &&
            x.CommerceBusinessId == target.Owner.CommerceBusinessId && x.AgentUserId == link.AgentUserId)
            .Select(x => new { x.FirstName, x.LastName, x.Email }).SingleOrDefaultAsync(ct);
        if (contact is null || (contact.FirstName + " " + contact.LastName).Trim() != target.CustomerName ||
            !string.Equals(contact.Email, target.InvitationEmail, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(target.InvitationEmail))
            throw new InvalidOperationException("The confirmed invitation recipient changed.");
        string? mailbox = target.Owner.CommerceBusinessId is { } businessId
            ? await _db.CommerceBusinessStorefrontSettings.AsNoTracking().Where(x => x.CommerceBusinessId == businessId).Select(x => x.BookingMailboxId).SingleOrDefaultAsync(ct)
            : null;
        mailbox = CleanOptional(mailbox) ?? CleanOptional(agent.BookingPageIdOrMailbox);
        if (string.IsNullOrWhiteSpace(mailbox)) throw new InvalidOperationException("The existing calendar target is unavailable.");
        mailbox = mailbox.Trim().TrimEnd('/');
        var hash = ReceptionistBookingReadinessReader.TargetFingerprint(target.Owner, agent.AgentUserId, mailbox);
        if (hash != target.CalendarTargetSha256) throw new InvalidOperationException("The reviewed calendar target changed.");
        return new(target, agent.AgentUserId, mailbox, hash, staff, fence);
    }

    private async Task<bool> RecheckReceptionistTargetAsync(ReceptionistCalendarContext expected, CancellationToken ct)
    {
        var current = await ResolveReceptionistTargetAsync(expected.Target, expected.StaffId, expected.Fence, ct);
        return current.TargetSha256 == expected.TargetSha256;
    }

    internal static DateTime? ProviderUtc(DateTimeTimeZone? value)
    {
        if (value?.DateTime is null || string.IsNullOrWhiteSpace(value.TimeZone) ||
            !DateTime.TryParse(value.DateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall)) return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(value.TimeZone);
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        return zone.IsInvalidTime(wall) || zone.IsAmbiguousTime(wall) ? null : TimeZoneInfo.ConvertTimeToUtc(wall, zone);
    }
}
