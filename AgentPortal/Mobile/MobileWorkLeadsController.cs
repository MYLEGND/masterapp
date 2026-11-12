using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Leads;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Mobile;

[ApiController]
[Route("api/v1/mobile/work/leads")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MobileWorkLeadsController(IMobileActorResolver actors, MasterAppDbContext db, MobileAgentCrmService crm)
    : MobileApiControllerBase(actors)
{
    public sealed record WorkLead(string Id, string Kind, string RecordId, Guid? BusinessId, Guid? ProfileId,
        string BusinessName, string DisplayName, string? Email, string? Phone, string? Detail,
        string Stage, string Bucket, string Tone, IReadOnlyList<string> AllowedStages, Guid? NotificationId, DateTime? UpdatedUtc = null, string? Revision = null);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct, [FromQuery] int skip = 0, [FromQuery] Guid? notificationId = null, [FromQuery] string? recordId = null)
    {
        if (skip < 0) return BadRequest();
        var resolved = await ResolveActorAsync(ct);
        if (resolved.Error != null) return resolved.Error;
        var actor = resolved.Actor!;
        var output = new List<WorkLead>();
        var allowed = await MobileWorkOrdersController.AllowedBusinessesAsync(db, actor, "crm", ct);
        Guid? targetWebsiteLead = null;
        string? targetContact = null;
        if (notificationId.HasValue)
        {
            var sourceLinks = await db.WebsiteLeadIntakeLinks.AsNoTracking()
                .Where(x => (x.CommerceBusinessId.HasValue && allowed.Keys.Contains(x.CommerceBusinessId.Value)) ||
                    (actor.Actor.ParticipantType == MessagingParticipantTypes.Agent && x.AgentUserId == actor.Actor.UserId))
                .Select(x => new { x.WebsiteLeadPublicId, x.WorkstationLeadId }).ToListAsync(ct);
            var target = sourceLinks.FirstOrDefault(x => WebsiteLeadAppNotificationService.NotificationId(x.WebsiteLeadPublicId) == notificationId.Value);
            targetWebsiteLead = target?.WebsiteLeadPublicId; targetContact = target?.WorkstationLeadId;
            if (target == null)
            {
                var inquiryIds = await db.Set<CommerceWebsiteInquiry>().AsNoTracking().Where(x => allowed.Keys.Contains(x.CommerceBusinessId) && x.WebsiteLeadId.HasValue)
                    .Select(x => x.WebsiteLeadId!.Value).ToListAsync(ct);
                targetWebsiteLead = inquiryIds.FirstOrDefault(id => WebsiteLeadAppNotificationService.NotificationId(id) == notificationId.Value);
            }
            if (targetWebsiteLead is null || targetWebsiteLead == Guid.Empty) return Ok(output);
        }
        var records = actor.Actor.ParticipantType == MessagingParticipantTypes.Agent
            ? await crm.WorkLeadsAsync(actor, ct) : Array.Empty<MobileCrmRecord>();
        var recordKeys = records.SelectMany(x => new[] { x.UserId, x.SourceLeadId }).Where(x => x != null).ToArray();
        var agentLinks = await db.WebsiteLeadIntakeLinks.AsNoTracking()
            .Where(x => actor.Actor.ParticipantType == MessagingParticipantTypes.Agent && x.AgentUserId == actor.Actor.UserId && recordKeys.Contains(x.WorkstationLeadId)).ToListAsync(ct);
        var businessManagedSources = await (from link in db.WebsiteLeadIntakeLinks.AsNoTracking()
            join contact in db.WorkstationLeadProfiles.AsNoTracking() on link.WorkstationLeadId equals contact.LeadId
            where contact.CommerceBusinessId.HasValue && allowed.Keys.Contains(contact.CommerceBusinessId.Value) &&
                link.CommerceBusinessId == contact.CommerceBusinessId && contact.AgentUserId == ""
            select link.WebsiteLeadPublicId).ToListAsync(ct);
        var managedSources = businessManagedSources.Concat(agentLinks.Select(x => x.WebsiteLeadPublicId)).Distinct().ToArray();
        // Resolve the management authority before paging: an intake delivery record
        // must never become a second actionable pipeline for its canonical CRM contact.
        var inquiryRecordId = Guid.TryParse(recordId, out var parsedRecordId) ? parsedRecordId : Guid.Empty;
        var inquiries = await db.Set<CommerceWebsiteInquiry>().AsNoTracking()
            .Where(x => allowed.Keys.Contains(x.CommerceBusinessId) && (recordId == null || x.Id == inquiryRecordId) && (!x.WebsiteLeadId.HasValue || !managedSources.Contains(x.WebsiteLeadId.Value)) && (!notificationId.HasValue || x.WebsiteLeadId == targetWebsiteLead)).OrderBy(x => x.Status == "Closed").ThenByDescending(x => x.CreatedUtc).ThenBy(x => x.Id).Skip(skip).Take(100).ToListAsync(ct);
        var names = await db.CommerceBusinesses.AsNoTracking().Where(x => allowed.Keys.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        foreach (var x in inquiries)
            output.Add(new("inquiry:" + x.Id, "inquiry", x.Id.ToString(), x.CommerceBusinessId, allowed[x.CommerceBusinessId],
                names.GetValueOrDefault(x.CommerceBusinessId) ?? "Business", x.Name, x.Email, null, x.Message,
                x.Status, WorkPipelinePresentation.LeadBucket(x.Status), WorkPipelinePresentation.Tone(x.Status),
                WebsiteInquiryAuthority.InquiryStatuses, x.WebsiteLeadId.HasValue ? WebsiteLeadAppNotificationService.NotificationId(x.WebsiteLeadId.Value) : null));
        if (allowed.Count > 0)
        {
            var workspace = HttpContext.RequestServices.GetRequiredService<Infrastructure.Businesses.BusinessWorkspaceService>();
            foreach (var (businessId, profileId) in allowed)
            {
                if (notificationId.HasValue && targetContact == null) continue;
                var page = await workspace.WorkContactsAsync(businessId, skip, ct, notificationId.HasValue ? targetContact : recordId);
                var contactIds = page.Contacts.Select(x => x.Id).ToArray();
                var links = await db.WebsiteLeadIntakeLinks.AsNoTracking().Where(x => x.CommerceBusinessId == businessId && contactIds.Contains(x.WorkstationLeadId!)).ToListAsync(ct);
                var sources = links.Select(x => x.WebsiteLeadPublicId).ToArray();
                var details = await db.Set<CommerceWebsiteInquiry>().AsNoTracking().Where(x => x.CommerceBusinessId == businessId && x.WebsiteLeadId.HasValue && sources.Contains(x.WebsiteLeadId.Value)).ToListAsync(ct);
                foreach (var contact in page.Contacts)
                {
                    var link = links.FirstOrDefault(x => x.WorkstationLeadId == contact.Id);
                    var inquiry = link == null ? null : details.FirstOrDefault(x => x.WebsiteLeadId == link.WebsiteLeadPublicId);
                    output.Add(new("business:" + businessId + ":" + contact.Id, "business", contact.Id, businessId, profileId,
                        names.GetValueOrDefault(businessId) ?? "Business", contact.Name, contact.Email, contact.Phone, inquiry?.Message ?? contact.Notes,
                        contact.Stage, contact.Kind == "Client" ? "completed" : WorkPipelinePresentation.LeadBucket(contact.Stage),
                        contact.Kind == "Client" ? "success" : WorkPipelinePresentation.Tone(contact.Stage), page.Stages,
                        link == null ? null : notificationId ?? WebsiteLeadAppNotificationService.NotificationId(link.WebsiteLeadPublicId), contact.UpdatedUtc, contact.Revision));
                }
            }
        }
        if (actor.Actor.ParticipantType == MessagingParticipantTypes.Agent)
        {
            var links = agentLinks;
            var sources = links.Select(x => x.WebsiteLeadPublicId).ToArray();
            var details = await db.Set<CommerceWebsiteInquiry>().AsNoTracking().Where(x => allowed.Keys.Contains(x.CommerceBusinessId) && x.WebsiteLeadId.HasValue && sources.Contains(x.WebsiteLeadId.Value)).ToListAsync(ct);
            foreach (var x in records)
            {
                if (recordId != null && x.Id != recordId) continue;
                if (notificationId.HasValue && x.UserId != targetContact && x.SourceLeadId != targetContact && x.Id != targetContact) continue;
                var link = links.FirstOrDefault(l => l.WorkstationLeadId == x.UserId || l.WorkstationLeadId == x.SourceLeadId);
                var inquiry = link == null ? null : details.FirstOrDefault(i => i.WebsiteLeadId == link.WebsiteLeadPublicId);
                if (skip > 0) continue;
                output.Add(new("crm:" + x.Id, "crm", x.Id, inquiry?.CommerceBusinessId, null, inquiry == null ? x.EntityName ?? "Assigned CRM" : names.GetValueOrDefault(inquiry.CommerceBusinessId) ?? "Business", x.DisplayName, x.Email, x.Phone, inquiry?.Message,
                    x.Stage, x.Kind == "Client" && !x.Archived ? "completed" : WorkPipelinePresentation.LeadBucket(x.Stage, x.Archived), x.Kind == "Client" ? "success" : WorkPipelinePresentation.Tone(x.Stage),
                    x.Archived ? [] : x.AvailableOutcomes ?? [], link == null ? null : notificationId ?? WebsiteLeadAppNotificationService.NotificationId(link.WebsiteLeadPublicId), x.UpdatedUtc));
            }
        }
        return Ok(output);
    }

    public sealed record StageInput(string Kind, string RecordId, Guid? BusinessId, Guid? ProfileId, string Stage, string ExpectedStage, string? Note, DateTime? ExpectedUpdatedUtc = null, string? Revision = null);
    [HttpPut("stage")]
    public async Task<IActionResult> Stage(StageInput input, CancellationToken ct)
    {
        var resolved = await ResolveActorAsync(ct);
        if (resolved.Error != null) return resolved.Error;
        var actor = resolved.Actor!;
        if (input.Kind == "business" && input.BusinessId is Guid business && input.ProfileId is Guid profile)
        {
            if (await Infrastructure.Businesses.BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, business, actor.Actor.UserId, "crm", profile, ct) == null) return Forbid();
            if (string.IsNullOrEmpty(input.Revision) || input.Note?.Length > 4000) return BadRequest();
            try
            {
                var workspace = HttpContext.RequestServices.GetRequiredService<Infrastructure.Businesses.BusinessWorkspaceService>();
                var result = await workspace.BulkUpdateAsync(business, new Shared.Crm.BusinessCrmBulkRequest {
                    ClientUserIds = [input.RecordId], Revisions = new() { [input.RecordId] = input.Revision },
                    PipelineStage = input.Stage, SharedNote = input.Note
                }, actor.Actor.UserId, ct);
                return result == null ? NotFound() : Ok(result);
            }
            catch (DbUpdateConcurrencyException) { return Conflict(new { message = "This contact changed. Refresh before saving." }); }
            catch (ArgumentException e) { return BadRequest(new { message = e.Message }); }
        }
        if (input.Kind == "inquiry" && input.BusinessId is Guid businessId && input.ProfileId is Guid profileId && Guid.TryParse(input.RecordId, out var id))
        {
            if (await Infrastructure.Businesses.BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, businessId, actor.Actor.UserId, "crm", profileId, ct) == null) return Forbid();
            return await WebsiteInquiryAuthority.UpdateStatusAsync(db, businessId, id, input.Stage, input.ExpectedStage, ct);
        }
        if (input.Kind != "crm" || actor.Actor.ParticipantType != MessagingParticipantTypes.Agent) return Forbid();
        if (!input.ExpectedUpdatedUtc.HasValue) return BadRequest(new { message = "Refresh the record before saving." });
        var record = await crm.RecordAsync(actor, "leads", input.RecordId, ct);
        if (record == null) return NotFound();
        if (record.Stage != input.ExpectedStage) return Conflict(new { message = "This lead changed. Refresh before saving." });
        var controller = new MobileAgentCrmController(actors, crm);
        controller.ControllerContext = ControllerContext;
        return await controller.Outcome("leads", input.RecordId, new(input.Stage, input.Note, input.ExpectedUpdatedUtc), ct);
    }
}
