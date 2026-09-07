using AgentPortal.Services;
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
