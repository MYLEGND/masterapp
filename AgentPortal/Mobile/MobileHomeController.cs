using Infrastructure.Mobile;
using Infrastructure.Messaging;
using Domain.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Mobile;

[ApiController]
[Route("api/v1/mobile")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileHomeController : MobileApiControllerBase
{
    private readonly IMobileHomeService _home;
    private readonly IMessagingProfileImageResolver _profiles;

    public MobileHomeController(
        IMobileActorResolver actorResolver,
        IMobileHomeService home,
        IMessagingProfileImageResolver profiles)
        : base(actorResolver)
    {
        _home = home;
        _profiles = profiles;
    }

    [HttpGet("home")]
    public async Task<IActionResult> Home(CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _home.GetHomeAsync(resolved.Actor!, cancellationToken);
        return result.Succeeded && result.Home is not null
            ? Ok(result.Home)
            : Error(
                StatusCodes.Status403Forbidden,
                result.ErrorCode ?? "mobile_home_unavailable",
                result.ErrorMessage ?? "Your mobile home is not available.");
    }

    [HttpGet("financial")]
    public async Task<IActionResult> Financial(CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _home.GetFinancialAsync(resolved.Actor!, cancellationToken);
        return result.Succeeded && result.Snapshot is not null
            ? Ok(result.Snapshot)
            : Error(
                StatusCodes.Status403Forbidden,
                result.ErrorCode ?? "mobile_financial_unavailable",
                result.ErrorMessage ?? "Financial intelligence is not available.");
    }

    [HttpGet("agent/clients")]
    public async Task<IActionResult> AgentClients(CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;
        if (!string.Equals(resolved.Actor!.Actor.ParticipantType, MessagingParticipantTypes.Agent, StringComparison.Ordinal))
            return Error(StatusCodes.Status403Forbidden, "mobile_agent_role_required", "Client CRM is available from an agent mobile identity.");

        var result = await _home.GetAgentClientsAsync(resolved.Actor, cancellationToken);
        return result.Succeeded
            ? Ok(await ToAgentClientDtosAsync(result.Clients, cancellationToken))
            : Error(
                StatusCodes.Status403Forbidden,
                result.ErrorCode ?? "mobile_agent_clients_unavailable",
                result.ErrorMessage ?? "Your client CRM is not available.");
    }

    [HttpGet("agent/leads")]
    public async Task<IActionResult> AgentLeads(CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;
        if (!string.Equals(resolved.Actor!.Actor.ParticipantType, MessagingParticipantTypes.Agent, StringComparison.Ordinal))
            return Error(StatusCodes.Status403Forbidden, "mobile_agent_role_required", "Lead CRM is available from an agent mobile identity.");

        var result = await _home.GetAgentLeadsAsync(resolved.Actor, cancellationToken);
        return result.Succeeded
            ? Ok(result.Leads.Select(lead => new MobileAgentLeadDto(
                lead.LeadId,
                lead.DisplayName,
                lead.CrmStage,
                lead.UpdatedUtc)))
            : Error(
                StatusCodes.Status403Forbidden,
                result.ErrorCode ?? "mobile_agent_leads_unavailable",
                result.ErrorMessage ?? "Your lead CRM is not available.");
    }

    private async Task<IReadOnlyList<MobileAgentClientDto>> ToAgentClientDtosAsync(
        IEnumerable<MobileAgentClient> clients,
        CancellationToken cancellationToken)
    {
        var clientRows = clients.ToArray();
        var avatars = await MobileAvatarProjection.ResolveManyAsync(
            _profiles,
            clientRows.Select(client => new MessagingParticipantIdentity(
                string.Empty,
                MessagingParticipantTypes.Client,
                client.ProfileId,
                client.DisplayName,
                null,
                string.Empty)),
            cancellationToken);

        var result = new List<MobileAgentClientDto>(clientRows.Length);
        foreach (var client in clientRows)
        {
            avatars.TryGetValue(
                new MessagingProfileImageKey(
                    MessagingParticipantTypes.Client,
                    client.ProfileId),
                out var avatar);
            result.Add(new MobileAgentClientDto(
                client.ProfileId,
                client.DisplayName,
                client.Email,
                client.CrmStatus,
                avatar));
        }

        return result;
    }
}

public sealed record MobileAgentClientDto(Guid ProfileId, string DisplayName, string Email, string CrmStatus, MobileAvatarDto? Avatar);
public sealed record MobileAgentLeadDto(string LeadId, string DisplayName, string CrmStage, DateTime UpdatedUtc);
