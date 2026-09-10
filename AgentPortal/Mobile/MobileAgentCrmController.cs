using AgentPortal.Services;
using AgentPortal.Controllers;
using AgentPortal.Models;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Domain.Messaging;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Mobile;

[ApiController]
[Route("api/v1/mobile/agent/crm")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[TypeFilter(typeof(MobileApiExceptionFilter))]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MobileAgentCrmController(IMobileActorResolver actors, MobileAgentCrmService crm)
    : MobileApiControllerBase(actors)
{
    public sealed record OutcomeInput(string OutcomeCode, string? Note);

    [HttpPost("clients/{id:guid}/restore")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        var resolved = await ResolveActorAsync(ct);
        if (resolved.Error is not null) return resolved.Error;
        if (resolved.Actor!.Actor.ParticipantType != MessagingParticipantTypes.Agent) return StatusCode(403);
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Identity.IFounderAccountRemovalService>();
        var result = await service.RestoreAssignedClientAsync(id, resolved.Actor.Actor.UserId, ct);
        return result.Succeeded ? Ok(await crm.RecordAsync(resolved.Actor, "clients", id.ToString(), ct))
            : Conflict(new { error = result.ErrorCode, message = result.Message });
    }

    [HttpPost("{kind}/{id}/outcome")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Outcome(string kind, string id, [FromBody] OutcomeInput input, CancellationToken ct)
    {
        var resolved = await ResolveActorAsync(ct);
        if (resolved.Error is not null) return resolved.Error;
        if (resolved.Actor!.Actor.ParticipantType != MessagingParticipantTypes.Agent) return StatusCode(403);
        if (kind is not ("clients" or "leads")) return NotFound();
        var record = await crm.RecordAsync(resolved.Actor, kind, id, ct);
        if (record is null || record.Archived) return NotFound();
        if (record.AvailableOutcomes?.Contains(input.OutcomeCode) != true || input.Note?.Length > 4000)
            return BadRequest("Choose an available outcome and a note up to 4,000 characters.");
        IActionResult result;
        if (record.ProfileId is not null)
        {
            var db = HttpContext.RequestServices.GetRequiredService<MasterAppDbContext>();
            var profileId = Guid.Parse(record.ProfileId);
            var profile = await db.ClientProfiles.AsNoTracking().SingleAsync(p => p.Id == profileId, ct);
            var meta = ClientCrmMetaSerializer.Deserialize(profile.CrmNotes);
            var controller = HttpContext.RequestServices.GetRequiredService<ClientsController>();
            controller.ControllerContext = ControllerContext;
            result = await controller.ApplyOutcome(new ClientsController.OutcomeRequest {
                ClientUserId = record.UserId, OutcomeCode = input.OutcomeCode, CustomNote = input.Note,
                UsePersonalZoomLink = meta?.UsePersonalZoomLink ?? false, MeetingDurationMinutes = 0
            });
        }
        else
        {
            var controller = HttpContext.RequestServices.GetRequiredService<LeadsController>();
            controller.ControllerContext = ControllerContext;
            result = await controller.ApplyOutcome(new LeadsController.LeadOutcomeRequest(record.UserId, input.OutcomeCode, input.Note));
        }
        if (result is ObjectResult { StatusCode: >= 400 } || result is StatusCodeResult { StatusCode: >= 400 }) return result;
        if (result is not (JsonResult or OkObjectResult)) return result;
        return Ok(await crm.RecordAsync(resolved.Actor, kind, id, ct));
    }

    [HttpPost("{kind}/{id}/contact")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Contact(string kind, string id, [FromBody] CrmContactUpdate input, CancellationToken ct)
    {
        var resolved = await ResolveActorAsync(ct);
        if (resolved.Error is not null) return resolved.Error;
        if (resolved.Actor!.Actor.ParticipantType != MessagingParticipantTypes.Agent) return StatusCode(403);
        if (kind is not ("clients" or "leads")) return NotFound();
        var record = await crm.RecordAsync(resolved.Actor, kind, id, ct);
        if (record is null || record.Archived) return NotFound();
        if (record.ProfileId is not null)
        {
            var controller = HttpContext.RequestServices.GetRequiredService<ClientsController>();
            controller.ControllerContext = ControllerContext;
            return await controller.SaveContactForAgentAsync(resolved.Actor.Actor.UserId, record.UserId, input);
        }
        var leads = HttpContext.RequestServices.GetRequiredService<LeadsController>();
        leads.ControllerContext = ControllerContext;
        return await leads.SaveContactForAgentAsync(resolved.Actor.Actor.UserId, record.UserId, input);
    }

    [HttpPost("appointments/{id:guid}/cancel")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var resolved = await ResolveActorAsync(ct);
        if (resolved.Error is not null) return resolved.Error;
        if (resolved.Actor!.Actor.ParticipantType != MessagingParticipantTypes.Agent) return StatusCode(403);
        if (!(await crm.ScheduleAsync(resolved.Actor, ct)).Any(a => a.Id == id)) return NotFound();
        var calendar = HttpContext.RequestServices.GetRequiredService<CalendarController>();
        calendar.ControllerContext = ControllerContext;
        return await calendar.CancelAppointment(new CalendarController.CancelAppointmentRequest { AppointmentId = id });
    }

    [HttpGet("schedule")]
    public async Task<IActionResult> Schedule(CancellationToken ct)
    {
        var resolved = await ResolveActorAsync(ct);
        if (resolved.Error is not null) return resolved.Error;
        if (resolved.Actor!.Actor.ParticipantType != MessagingParticipantTypes.Agent)
            return Error(403, "mobile_agent_role_required", "Schedule is available to agents.");
        return Ok(await crm.ScheduleAsync(resolved.Actor, ct));
    }

    [HttpGet("{kind}/{id}")]
    public async Task<IActionResult> Record(string kind, string id, CancellationToken ct)
    {
        var resolved = await ResolveActorAsync(ct);
        if (resolved.Error is not null) return resolved.Error;
        if (resolved.Actor!.Actor.ParticipantType != MessagingParticipantTypes.Agent)
            return Error(403, "mobile_agent_role_required", "CRM is available to agents.");
        if (kind is not ("clients" or "leads")) return NotFound();
        var record = await crm.RecordAsync(resolved.Actor, kind, id, ct);
        return record is null ? NotFound() : Ok(record);
    }
}
