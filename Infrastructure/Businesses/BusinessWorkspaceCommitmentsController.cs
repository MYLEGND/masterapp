using System.ComponentModel.DataAnnotations;
using AgentPortal.Services;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;

namespace Infrastructure.Businesses;

public abstract partial class BusinessWorkspaceControllerBase
{
    public sealed class BusinessCommitmentInput
    {
        [MaxLength(180)] public string? ClientId { get; set; }
        [MaxLength(180)] public string? LeadId { get; set; }
        [Required, MaxLength(4000)] public string PromiseText { get; set; } = "";
        [Required] public DateTimeOffset? DueDateUtc { get; set; }
    }

    [HttpGet("crm/api/{recordSet:regex(^(Clients|Leads)$)}/Commitments")]
    public async Task<IActionResult> Commitments(Guid businessId, string recordSet, string id, CancellationToken cancellationToken)
    {
        if (await ResolveBusinessAsync(businessId, "crm", cancellationToken) is null) return Forbid();
        if (await workspace.QuickViewAsync(businessId, id, recordSet == "Clients" ? "Client" : "Lead", cancellationToken) is null) return NotFound();
        var items = await workspace.BusinessCommitments(businessId).GetByEntityForActorAsync(RelatedEntityType.BusinessContact,
            id, User.GetCanonicalUserId(), cancellationToken);
        ViewBag.ClientId = id;
        ViewBag.LeadId = id;
        ViewData["CrmApiBase"] = $"/business/{businessId}/crm/api";
        return PartialView(recordSet == "Clients" ? "~/Views/Clients/_ClientCommitmentsTab.cshtml" : "~/Views/Leads/_CommitmentsTab.cshtml", items);
    }

    [HttpPost("crm/api/{recordSet:regex(^(Clients|Leads)$)}/CreateCommitment")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCommitment(Guid businessId, string recordSet, [FromForm] BusinessCommitmentInput input, CancellationToken cancellationToken)
    {
        if (await ResolveBusinessAsync(businessId, "crm", cancellationToken) is null) return Forbid();
        if (!ModelState.IsValid || !input.DueDateUtc.HasValue) return BadRequest(ModelState);
        var contact = recordSet == "Clients" ? input.ClientId : input.LeadId;
        if (string.IsNullOrWhiteSpace(contact)) return BadRequest("Choose a contact.");
        if (await workspace.QuickViewAsync(businessId, contact, recordSet == "Clients" ? "Client" : "Lead", cancellationToken) is null) return NotFound();
        await workspace.BusinessCommitments(businessId).CreateCommitmentAsync(new CommitmentCreateRequest(
            RelatedEntityType.BusinessContact, contact, ActionOwnerType.Business, businessId.ToString(),
            ActionOwnerType.Client, contact, input.PromiseText.Trim(), input.DueDateUtc.Value.ToUniversalTime(),
            User.GetCanonicalUserId()), cancellationToken);
        return await Commitments(businessId, recordSet, contact, cancellationToken);
    }

    [HttpPost("crm/api/{recordSet:regex(^(Clients|Leads)$)}/FulfillCommitment")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> FulfillCommitment(Guid businessId, string recordSet, Guid id, CancellationToken cancellationToken) =>
        ChangeCommitment(businessId, recordSet, id, true, cancellationToken);

    [HttpPost("crm/api/{recordSet:regex(^(Clients|Leads)$)}/BreakCommitment")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> BreakCommitment(Guid businessId, string recordSet, Guid id, CancellationToken cancellationToken) =>
        ChangeCommitment(businessId, recordSet, id, false, cancellationToken);

    private async Task<IActionResult> ChangeCommitment(Guid businessId, string recordSet, Guid id, bool fulfilled, CancellationToken ct)
    {
        if (await ResolveBusinessAsync(businessId, "crm", ct) is null) return Forbid();
        var service = workspace.BusinessCommitments(businessId);
        var item = await service.GetByIdForActorAsync(id, User.GetCanonicalUserId(), ct);
        if (item is null || await workspace.QuickViewAsync(businessId, item.RelatedEntityId, recordSet == "Clients" ? "Client" : "Lead", ct) is null)
            return NotFound();
        if (fulfilled) await service.FulfillCommitmentAsync(id, User.GetCanonicalUserId(), ct);
        else await service.BreakCommitmentAsync(id, User.GetCanonicalUserId(), ct);
        return await Commitments(businessId, recordSet, item.RelatedEntityId, ct);
    }
}
