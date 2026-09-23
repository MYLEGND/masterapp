using System.ComponentModel.DataAnnotations;
using Domain.Entities;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;

namespace Infrastructure.Businesses;

public abstract partial class BusinessWorkspaceControllerBase
{
    public sealed class BusinessActionInput
    {
        public Guid Id { get; set; }
        [MaxLength(180)] public string? ClientId { get; set; }
        [MaxLength(180)] public string? LeadId { get; set; }
        [Required, MaxLength(240)] public string Title { get; set; } = "";
        [MaxLength(20000)] public string? Description { get; set; }
        public DateTime? DueDateUtc { get; set; }
        [EnumDataType(typeof(ActionPriority))] public ActionPriority Priority { get; set; } = ActionPriority.P2;
        public bool ShowInCommandCenter { get; set; }
    }
    public sealed record BusinessActionId(Guid Id);

    [HttpGet("crm/api/{recordSet:regex(^(Clients|Leads)$)}/Actions")]
    public async Task<IActionResult> Actions(Guid businessId, string recordSet, string id, CancellationToken cancellationToken)
    {
        if (await ResolveBusinessAsync(businessId, "crm", cancellationToken) is null) return Forbid();
        if (await workspace.QuickViewAsync(businessId, id, recordSet == "Clients" ? "Client" : "Lead", cancellationToken) is null) return NotFound();
        var items = await workspace.BusinessActions(businessId).GetByRelatedAsync(RelatedEntityType.BusinessContact,
            id, User.GetCanonicalUserId(), cancellationToken);
        ViewBag.ClientId = id;
        ViewBag.LeadId = id;
        ViewData["CrmApiBase"] = $"/business/{businessId}/crm/api";
        return PartialView(recordSet == "Clients" ? "~/Views/Clients/_ClientActionsTab.cshtml" : "~/Views/Leads/_ActionsTab.cshtml", items);
    }

    [HttpPost("crm/api/{recordSet:regex(^(Clients|Leads)$)}/CreateAction")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateAction(Guid businessId, string recordSet, [FromForm] BusinessActionInput input, CancellationToken cancellationToken)
    {
        if (await ResolveBusinessAsync(businessId, "crm", cancellationToken) is null) return Forbid();
        if (!ModelState.IsValid) return BadRequest(ModelState);
        var contact = recordSet == "Clients" ? input.ClientId : input.LeadId;
        if (string.IsNullOrWhiteSpace(contact)) return BadRequest("Choose a contact.");
        if (await workspace.QuickViewAsync(businessId, contact, recordSet == "Clients" ? "Client" : "Lead", cancellationToken) is null) return NotFound();
        await workspace.BusinessActions(businessId).CreateActionAsync(new ActionItem
        {
            RelatedEntityType = RelatedEntityType.BusinessContact, RelatedEntityId = contact,
            OwnerType = ActionOwnerType.Business, OwnerId = businessId.ToString(), EffectiveAgentOid = "",
            Title = input.Title.Trim(), Description = input.Description?.Trim() ?? "", DueDateUtc = input.DueDateUtc,
            Priority = input.Priority, Status = ActionStatus.Planned, CreatedBy = User.GetCanonicalUserId(),
            ActionSurface = input.ShowInCommandCenter ? ActionSurface.CommandCenter : ActionSurface.CrmOnly,
            Source = "business-manual", SourceRef = contact + "-manual"
        }, cancellationToken);
        return await Actions(businessId, recordSet, contact, cancellationToken);
    }

    [HttpPost("crm/api/Dashboard/CompleteAction")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CompleteAction(Guid businessId, [FromBody] BusinessActionId input, CancellationToken cancellationToken)
    {
        if (await ResolveBusinessAsync(businessId, "crm", cancellationToken) is null) return Forbid();
        if (!ModelState.IsValid || input.Id == Guid.Empty) return BadRequest();
        var item = await workspace.BusinessActions(businessId).CompleteActionAsync(input.Id, User.GetCanonicalUserId(), cancellationToken);
        return item is null ? NotFound() : Json(new { ok = true });
    }

    [HttpGet("crm/api/Actions/Edit/{id:guid}")]
    public async Task<IActionResult> EditAction(Guid businessId, Guid id, CancellationToken cancellationToken)
    {
        if (await ResolveBusinessAsync(businessId, "crm", cancellationToken) is null) return Forbid();
        var item = await workspace.BusinessActions(businessId).GetByIdAsync(id, User.GetCanonicalUserId(), cancellationToken);
        return item is null ? NotFound() : View("~/Views/Actions/Edit.cshtml", item);
    }

    [HttpPost("crm/api/Actions/Edit/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAction(Guid businessId, Guid id, [FromForm] BusinessActionInput input, CancellationToken cancellationToken)
    {
        if (await ResolveBusinessAsync(businessId, "crm", cancellationToken) is null) return Forbid();
        if (!ModelState.IsValid || id == Guid.Empty || id != input.Id) return BadRequest(ModelState);
        var item = await workspace.BusinessActions(businessId).UpdateActionAsync(id, User.GetCanonicalUserId(),
            input.Title, input.Description, input.DueDateUtc, input.Priority, cancellationToken);
        if (item is null) return NotFound();
        return await ReturnToActionContact(businessId, item, cancellationToken);
    }

    [HttpPost("crm/api/Actions/Delete/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAction(Guid businessId, Guid id, CancellationToken cancellationToken)
    {
        if (await ResolveBusinessAsync(businessId, "crm", cancellationToken) is null) return Forbid();
        var engine = workspace.BusinessActions(businessId);
        var item = await engine.GetByIdAsync(id, User.GetCanonicalUserId(), cancellationToken);
        if (item is null || !await engine.DeleteActionAsync(id, User.GetCanonicalUserId(), cancellationToken)) return NotFound();
        // The canonical quick view reloads its panel after this response.
        return Json(new { ok = true });
    }

    private async Task<IActionResult> ReturnToActionContact(Guid businessId, ActionItem item, CancellationToken ct)
    {
        var isClient = await workspace.QuickViewAsync(businessId, item.RelatedEntityId, "Client", ct) is not null;
        return RedirectToAction(isClient ? nameof(Clients) : nameof(Leads), new { businessId, contactId = item.RelatedEntityId });
    }
}
