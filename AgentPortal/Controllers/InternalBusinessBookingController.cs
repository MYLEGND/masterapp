using System.Security.Claims;
using AgentPortal.Controllers;
using Domain.Entities;
using Infrastructure.Bookings;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Controllers;

/// <summary>
/// Signed server-to-server adapter for business CRM scheduling. CalendarController
/// remains the sole booking engine and Microsoft Graph authority.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/internal/business-booking")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class InternalBusinessBookingController(
    MasterAppDbContext db,
    BusinessBookingTicketProtector tickets,
    CalendarController calendar) : ControllerBase
{
    private const string TicketHeader = "X-Legend-Business-Booking-Ticket";

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var access = await AuthorizeAsync(ct);
        if (access.Error is not null) return access.Error;
        var agent = access.Agent!;
        var hasMailbox = !string.IsNullOrWhiteSpace(agent.BookingPageIdOrMailbox);
        var enabled = agent.BookingEnabled ?? hasMailbox;
        return Ok(new
        {
            connected = enabled && hasMailbox,
            email = agent.CalendarEmail ?? agent.AgentUpn,
            configurationSource = "agent_profile"
        });
    }

    [HttpGet("day-availability")]
    public async Task<IActionResult> Availability(string date, string? excludeEventId, CancellationToken ct)
    {
        var access = await AuthorizeAsync(ct);
        if (access.Error is not null) return access.Error;
        if (!BookingEnabled(access.Agent!))
            return Conflict("The attached agent booking profile is disabled or incomplete.");

        if (!string.IsNullOrWhiteSpace(excludeEventId))
        {
            var ticket = access.Ticket!;
            var eventId = excludeEventId.Trim();
            var allowed = await db.LeadAppointments.AsNoTracking().AnyAsync(a =>
                a.CalendarEventId == eventId &&
                a.OwnerAgentUserId.ToLower() == ticket.AgentUserId.ToLower() &&
                a.WorkstationLeadId != null &&
                db.WorkstationLeadProfiles.Any(w =>
                    w.LeadId == a.WorkstationLeadId &&
                    w.CommerceBusinessId == ticket.CommerceBusinessId &&
                    (w.AgentUserId ?? string.Empty) == string.Empty), ct);
            if (!allowed) return Forbid();
        }

        calendar.ControllerContext = ControllerContext;
        return await calendar.DayAvailability(date, excludeEventId);
    }

    [HttpPost("create-event")]
    public async Task<IActionResult> Create(CalendarController.CreateEventRequest request, CancellationToken ct)
    {
        var access = await AuthorizeAsync(ct);
        if (access.Error is not null) return access.Error;
        if (!BookingEnabled(access.Agent!))
            return Conflict("The attached agent booking profile is disabled or incomplete.");

        var contact = await ResolveContactAsync(access.Ticket!, request.WorkstationLeadId ?? request.ClientUserId, ct);
        if (contact is null) return NotFound("Business contact not found.");

        ApplyBusinessScope(request, access.Ticket!, contact);
        calendar.ControllerContext = ControllerContext;
        return await calendar.CreateEvent(request);
    }

    [HttpPost("update-appointment")]
    public async Task<IActionResult> Update(CalendarController.UpdateAppointmentRequest request, CancellationToken ct)
    {
        var access = await AuthorizeAsync(ct);
        if (access.Error is not null) return access.Error;
        if (!BookingEnabled(access.Agent!))
            return Conflict("The attached agent booking profile is disabled or incomplete.");

        var contact = await ResolveContactAsync(access.Ticket!, request.WorkstationLeadId ?? request.ClientUserId, ct);
        if (contact is null) return NotFound("Business contact not found.");

        ApplyBusinessScope(request, access.Ticket!, contact);
        calendar.ControllerContext = ControllerContext;
        return await calendar.UpdateAppointment(request);
    }

    [HttpPost("cancel-appointment")]
    public async Task<IActionResult> Cancel(CalendarController.CancelAppointmentRequest request, CancellationToken ct)
    {
        var access = await AuthorizeAsync(ct);
        if (access.Error is not null) return access.Error;
        if (!BookingEnabled(access.Agent!))
            return Conflict("The attached agent booking profile is disabled or incomplete.");

        var contact = await ResolveContactAsync(access.Ticket!, request.WorkstationLeadId ?? request.ClientUserId, ct);
        if (contact is null) return NotFound("Business contact not found.");

        ApplyBusinessScope(request, access.Ticket!, contact);
        calendar.ControllerContext = ControllerContext;
        return await calendar.CancelAppointment(request);
    }

    private static bool BookingEnabled(AgentProfile agent)
    {
        var hasMailbox = !string.IsNullOrWhiteSpace(agent.BookingPageIdOrMailbox);
        return (agent.BookingEnabled ?? hasMailbox) && hasMailbox;
    }

    private async Task<(BusinessBookingTicket? Ticket, AgentProfile? Agent, IActionResult? Error)> AuthorizeAsync(CancellationToken ct)
    {
        var ticket = tickets.TryUnprotect(Request.Headers[TicketHeader].FirstOrDefault());
        if (ticket is null) return (null, null, Unauthorized());

        var agent = await BusinessBookingAccess.ResolveAttachedAgentAsync(
            db, ticket.CommerceBusinessId, ticket.AgentProfileId, ct);
        if (agent is null ||
            !string.Equals(agent.AgentUserId, ticket.AgentUserId, StringComparison.OrdinalIgnoreCase))
            return (null, null, Forbid());

        var claims = new List<Claim>
        {
            new("oid", agent.AgentUserId),
            new("preferred_username", ticket.ActorDisplay)
        };
        if (!string.IsNullOrWhiteSpace(ticket.TimeZoneId))
            claims.Add(new Claim("zoneinfo", ticket.TimeZoneId));
        HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "BusinessBookingHandoff"));
        return (ticket, agent, null);
    }

    private Task<WorkstationLeadProfile?> ResolveContactAsync(
        BusinessBookingTicket ticket,
        string? contactId,
        CancellationToken ct) =>
        BusinessBookingAccess.ResolveBusinessContactAsync(db, ticket.CommerceBusinessId, contactId, ct);

    private static void ApplyBusinessScope(
        CalendarController.CreateEventRequest request,
        BusinessBookingTicket ticket,
        WorkstationLeadProfile contact)
    {
        request.ClientProfileId = null;
        request.ClientUserId = contact.LeadId;
        request.WorkstationLeadId = contact.LeadId;
        request.ScopedBusinessId = ticket.CommerceBusinessId;
        request.ScopedBookingAgentUserId = ticket.AgentUserId;
    }

    private static void ApplyBusinessScope(
        CalendarController.UpdateAppointmentRequest request,
        BusinessBookingTicket ticket,
        WorkstationLeadProfile contact)
    {
        request.ClientProfileId = null;
        request.ClientUserId = contact.LeadId;
        request.WorkstationLeadId = contact.LeadId;
        request.ScopedBusinessId = ticket.CommerceBusinessId;
        request.ScopedBookingAgentUserId = ticket.AgentUserId;
    }

    private static void ApplyBusinessScope(
        CalendarController.CancelAppointmentRequest request,
        BusinessBookingTicket ticket,
        WorkstationLeadProfile contact)
    {
        request.ClientProfileId = null;
        request.ClientUserId = contact.LeadId;
        request.WorkstationLeadId = contact.LeadId;
        request.ScopedBusinessId = ticket.CommerceBusinessId;
        request.ScopedBookingAgentUserId = ticket.AgentUserId;
    }
}
