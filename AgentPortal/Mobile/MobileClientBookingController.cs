using System.Security.Claims;
using System.Text.Json;
using AgentPortal.Controllers;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Mobile;

/// A client-bound mobile transport for the existing portal scheduler. CalendarController
/// remains the sole owner of availability, Graph writes, CRM activity and cancellations.
[ApiController]
[TypeFilter(typeof(MobileApiExceptionFilter))]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MobileClientBookingController(
    IMobileActorResolver actors, MasterAppDbContext db, IDataProtectionProvider protection,
    IAgentTimeZoneResolver timeZones, CalendarController calendar) : MobileApiControllerBase(actors)
{
    private readonly ITimeLimitedDataProtector tickets = protection
        .CreateProtector("AgentPortal.MobileClientBooking.v1").ToTimeLimitedDataProtector();
    private sealed record Ticket(string AgentId, Guid ProfileId, string TimeZone);

    [Authorize(Policy = MobileApiAuthorization.PolicyName)]
    [HttpGet("/api/v1/mobile/agent/clients/{profileId:guid}/booking-access")]
    public async Task<IActionResult> Access(Guid profileId, CancellationToken ct)
    {
        var resolved = await ResolveActorAsync(ct);
        if (resolved.Error is not null) return resolved.Error;
        var allowed = resolved.Actor!.Actor.ParticipantType == MessagingParticipantTypes.Agent &&
            await OwnedClientAsync(resolved.Actor.Actor.UserId, profileId, ct) != null;
        return Ok(new { allowed });
    }

    [Authorize(Policy = MobileApiAuthorization.PolicyName)]
    [IgnoreAntiforgeryToken]
    [HttpPost("/api/v1/mobile/agent/clients/{profileId:guid}/booking-launch")]
    public async Task<IActionResult> Launch(Guid profileId, CancellationToken ct)
    {
        var resolved = await ResolveActorAsync(ct);
        if (resolved.Error is not null) return resolved.Error;
        if (resolved.Actor!.Actor.ParticipantType != MessagingParticipantTypes.Agent ||
            await OwnedClientAsync(resolved.Actor.Actor.UserId, profileId, ct) == null)
            return Error(403, "mobile_booking_forbidden", "Booking is available for your assigned clients.");
        var ticket = tickets.Protect(JsonSerializer.Serialize(new Ticket(resolved.Actor.Actor.UserId,
            profileId, timeZones.Resolve(HttpContext).Id)), TimeSpan.FromMinutes(20));
        return Ok(new { launchPath = "/mobile/agent/booking?ticket=" + Uri.EscapeDataString(ticket) });
    }

    [AllowAnonymous]
    [HttpGet("/mobile/agent/booking")]
    public async Task<IActionResult> Page([FromQuery] string? ticket, CancellationToken ct)
    {
        var access = await AuthorizeTicketAsync(ticket, ct);
        if (access.Error != null) return access.Error;
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        var profile = access.Profile!;
        var zone = timeZones.Resolve(HttpContext).Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone, out var iana)) zone = iana;
        return new ViewResult {
            ViewName = "~/Views/Calendar/MobileBooking.cshtml",
            ViewData = new Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary(
                new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), ModelState) {
                Model = new MobileClientBookingPage(ticket!, profile.Id, profile.ClientUserId,
                    $"{profile.FirstName} {profile.LastName}".Trim(), profile.Email, profile.Phone, zone)
            }
        };
    }

    [AllowAnonymous]
    [HttpGet("/mobile/agent/booking/availability")]
    public async Task<IActionResult> Availability(string date, string? excludeEventId, CancellationToken ct)
    {
        var access = await AuthorizeTicketAsync(Request.Headers["X-Legend-Booking-Ticket"], ct);
        if (access.Error != null) return access.Error;
        // An exclusion is permitted only for an appointment attached to this ticket's client.
        if (!string.IsNullOrWhiteSpace(excludeEventId) &&
            !(await ClientAppointmentsAsync(access.Profile!, ct)).Any(a => a.CalendarEventId == excludeEventId))
            return StatusCode(StatusCodes.Status403Forbidden);
        calendar.ControllerContext = ControllerContext;
        return await calendar.DayAvailability(date, excludeEventId);
    }

    [AllowAnonymous]
    [HttpGet("/mobile/agent/booking/appointments")]
    public async Task<IActionResult> Appointments(CancellationToken ct)
    {
        var access = await AuthorizeTicketAsync(Request.Headers["X-Legend-Booking-Ticket"], ct);
        if (access.Error != null) return access.Error;
        return Ok((await ClientAppointmentsAsync(access.Profile!, ct)).Select(a => new {
            id = a.Id, calendarEventId = a.CalendarEventId,
            scheduledStartUtc = a.ScheduledStartUtc.HasValue ? DateTime.SpecifyKind(a.ScheduledStartUtc.Value, DateTimeKind.Utc) : (DateTime?)null,
            scheduledEndUtc = a.ScheduledEndUtc.HasValue ? DateTime.SpecifyKind(a.ScheduledEndUtc.Value, DateTimeKind.Utc) : (DateTime?)null,
            status = a.Status.ToString()
        }));
    }

    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [HttpPost("/mobile/agent/booking/create")]
    public async Task<IActionResult> Create(CalendarController.CreateEventRequest request, CancellationToken ct)
    {
        var access = await AuthorizeTicketAsync(Request.Headers["X-Legend-Booking-Ticket"], ct);
        if (access.Error != null) return access.Error;
        request.ClientProfileId = access.Profile!.Id;
        request.ClientUserId = access.Profile.ClientUserId;
        request.WorkstationLeadId = null;
        calendar.ControllerContext = ControllerContext;
        return await calendar.CreateEvent(request);
    }

    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [HttpPost("/mobile/agent/booking/update")]
    public async Task<IActionResult> Update(CalendarController.UpdateAppointmentRequest request, CancellationToken ct)
    {
        var access = await AuthorizeTicketAsync(Request.Headers["X-Legend-Booking-Ticket"], ct);
        if (access.Error != null) return access.Error;
        if (!(await ClientAppointmentsAsync(access.Profile!, ct)).Any(a => a.Id == request.AppointmentId)) return NotFound();
        request.ClientProfileId = access.Profile!.Id;
        request.ClientUserId = access.Profile.ClientUserId;
        request.WorkstationLeadId = null;
        calendar.ControllerContext = ControllerContext;
        return await calendar.UpdateAppointment(request);
    }

    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [HttpPost("/mobile/agent/booking/cancel")]
    public async Task<IActionResult> Cancel(CalendarController.CancelAppointmentRequest request, CancellationToken ct)
    {
        var access = await AuthorizeTicketAsync(Request.Headers["X-Legend-Booking-Ticket"], ct);
        if (access.Error != null) return access.Error;
        if (!(await ClientAppointmentsAsync(access.Profile!, ct)).Any(a => a.Id == request.AppointmentId)) return NotFound();
        request.ClientProfileId = access.Profile!.Id;
        request.ClientUserId = access.Profile.ClientUserId;
        request.WorkstationLeadId = null;
        calendar.ControllerContext = ControllerContext;
        return await calendar.CancelAppointment(request);
    }

    private Task<ClientProfile?> OwnedClientAsync(string agentId, Guid profileId, CancellationToken ct) =>
        db.ClientProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId &&
            db.AgentClients.Any(link => link.AgentUserId.ToLower() == agentId.ToLower() &&
                link.ClientUserId.ToLower() == p.ClientUserId.ToLower()), ct);

    private async Task<(ClientProfile? Profile, IActionResult? Error)> AuthorizeTicketAsync(string? value, CancellationToken ct)
    {
        Ticket? ticket;
        try { ticket = JsonSerializer.Deserialize<Ticket>(tickets.Unprotect(value ?? "")); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or ArgumentException)
        { return (null, Unauthorized()); }
        if (ticket == null) return (null, Unauthorized());
        // Re-resolve the active role and account lifecycle on every request, then
        // recheck the live assignment: old tickets cannot outlive reassignment.
        HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
            new Claim("oid", ticket.AgentId), new Claim("zoneinfo", ticket.TimeZone)
        }, "MobileClientBooking"));
        Request.Headers[MobileApiAuthorization.ParticipantTypeHeader] = MessagingParticipantTypes.Agent;
        var resolved = await ResolveActorAsync(ct);
        if (resolved.Error != null) return (null, resolved.Error);
        if (resolved.Actor?.Actor.ParticipantType != MessagingParticipantTypes.Agent) return (null, StatusCode(StatusCodes.Status403Forbidden));
        var profile = await OwnedClientAsync(resolved.Actor.Actor.UserId, ticket.ProfileId, ct);
        return profile == null ? (null, StatusCode(StatusCodes.Status403Forbidden)) : (profile, null);
    }

    private async Task<List<LeadAppointment>> ClientAppointmentsAsync(ClientProfile profile, CancellationToken ct)
    {
        var source = ClientCrmMetaSerializer.Deserialize(profile.CrmNotes).SourceWorkstationLeadId;
        var owner = User.FindFirstValue("oid")!;
        var profileId = profile.Id.ToString();
        return await db.LeadAppointments.AsNoTracking().Where(a => a.OwnerAgentUserId.ToLower() == owner.ToLower() &&
            (a.ClientProfileId == profileId || a.WorkstationLeadId == profile.ClientUserId ||
                (source != null && a.WorkstationLeadId == source)) &&
            (a.Status == LeadAppointmentStatus.Booked || a.Status == LeadAppointmentStatus.Confirmed || a.Status == LeadAppointmentStatus.Rescheduled) &&
            (a.ScheduledEndUtc ?? a.ScheduledStartUtc) >= DateTime.UtcNow)
            .OrderBy(a => a.ScheduledStartUtc).ToListAsync(ct);
    }
}

public sealed record MobileClientBookingPage(string Ticket, Guid ProfileId, string ClientUserId,
    string DisplayName, string Email, string Phone, string TimeZone);
