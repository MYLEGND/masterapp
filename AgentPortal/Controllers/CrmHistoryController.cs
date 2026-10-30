using System.Security.Claims;
using System.Text.Json;
using AgentPortal.Services;
using Domain.Entities;
using Infrastructure.Crm;
using Infrastructure.Data;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace AgentPortal.Controllers;

[Authorize]
[Route("CrmHistory")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CrmHistoryController(MasterAppDbContext db, EffectiveAgentContext agents, IConfiguration configuration, IAgentTimeZoneResolver timeZones) : Controller
{
    private string Agent => (agents.EffectiveAgentOid ?? User.GetStableUserId() ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "").Trim();
    private Task<CrmHistoryRelationship?> Resolve(string kind, string id) => RestrictedAiIdentity.IsRestricted(User, configuration)
        ? Task.FromResult<CrmHistoryRelationship?>(null) : new CrmHistoryAccess(db).ResolveAsync(Agent, kind, id, HttpContext.RequestAborted);

    [HttpGet("")]
    public async Task<IActionResult> Index(string kind, string id)
    {
        var relationship = await Resolve(kind, id); if (relationship is null) return NotFound();
        if (kind == "client") return RedirectToAction(nameof(Index), new { kind = "profile", id = relationship.Client!.Id.ToString("N") });
        var isLead = kind == "lead";
        var firstName = isLead ? relationship.Lead!.FirstName : relationship.Client!.FirstName;
        var lastName = isLead ? relationship.Lead!.LastName : relationship.Client!.LastName;
        var displayName = string.Join(" ", new[] { firstName, lastName }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
        ViewData["RelationshipType"] = isLead ? "Lead" : "Client";
        ViewData["RelationshipName"] = displayName.Length == 0 ? "Name unavailable" : displayName;
        ViewData["TimeZone"] = timeZones.Resolve(HttpContext).Id;
        ViewData["AgentId"] = Agent;
        ViewData["DraftScope"] = (User.GetStableUserId() ?? Agent) + ":" + Agent;
        ViewData["MayManageHolds"] = AgentPortal.Security.FounderGuard.IsFounder(User);
        ViewData["Kind"] = kind; ViewData["RelationshipId"] = id;
        return View();
    }

    [HttpPost("Backfill")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Backfill(string kind, string id)
    {
        var relationship = await Resolve(kind, id); if (relationship is null) return NotFound();
        await new CrmHistoryAccess(db).BackfillAsync(relationship, HttpContext.RequestAborted);
        return Ok(new { saved = true });
    }

    public sealed record AssociationCorrection(Guid RequestId, Guid SubmissionId, string TargetLeadId, string Reason, Guid? TargetClientProfileId = null, Guid ExpectedVersion = default);

    public sealed record OwnerCorrection(Guid RequestId, Guid SubmissionId, string Reason, bool Verified = false);

    [HttpGet("Ownership")]
    [Authorize(Policy = "FounderOnly")]
    public IActionResult Ownership()
    {
        if (!AgentPortal.Security.FounderGuard.IsFounder(User) || RestrictedAiIdentity.IsRestricted(User, configuration)) return NotFound();
        ViewData["AgentId"] = Agent;
        ViewData["DraftScope"] = (User.GetStableUserId() ?? Agent) + ":" + Agent;
        return View();
    }

    [HttpGet("UnresolvedOwnership")]
    [Authorize(Policy = "FounderOnly")]
    public async Task<IActionResult> UnresolvedOwnership(long afterRowId = 0)
    {
        if (!AgentPortal.Security.FounderGuard.IsFounder(User) || RestrictedAiIdentity.IsRestricted(User, configuration)) return NotFound();
        if (afterRowId < 0) return BadRequest();
        // The restricted queue exposes source receipts, not customer answers or
        // contact matching candidates. The human must establish the owner explicitly.
        var rows = await db.WebsiteLeads.AsNoTracking().Where(x => x.Id > afterRowId && x.AgentTrackingProfileId == null && x.CommerceBusinessId == null &&
            !db.WebsiteLeadIntakeLinks.Any(link => link.WebsiteLeadPublicId == x.LeadId)).OrderBy(x => x.Id).Take(101)
            .Select(x => new { x.Id, x.LeadId, x.SourcePageKey, x.CreatedUtc }).ToListAsync(HttpContext.RequestAborted);
        return Ok(new { submissions = rows.Take(100), nextAfterRowId = rows.Count > 100 ? rows[99].Id : (long?)null });
    }

    [HttpPost("CorrectOwner")]
    [Authorize(Policy = "FounderOnly")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CorrectOwner([FromBody] OwnerCorrection request)
    {
        if (!AgentPortal.Security.FounderGuard.IsFounder(User) || RestrictedAiIdentity.IsRestricted(User, configuration)) return NotFound();
        if (!ModelState.IsValid || request is null || request.RequestId == Guid.Empty || request.SubmissionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 4000 || !request.Verified) return BadRequest();
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, HttpContext.RequestAborted) : null;
        var owner = await agents.GetEffectiveTrackingProfileAsync(HttpContext.RequestAborted);
        if (owner is null || owner.AgentUserId != Agent) return BadRequest("Select an existing authorized agent with a canonical tracking profile.");
        var actor = User.GetStableUserId() ?? Agent;
        var content = JsonSerializer.Serialize(new { request, assignedOwner = Agent, trackingProfileId = owner.Id });
        var sourceId = request.RequestId.ToString("N");
        var prior = await db.Set<CrmHistoryEntry>().AsNoTracking().FirstOrDefaultAsync(x => x.Source == "OwnerAssociationCorrection" &&
            x.SourceId == sourceId && x.RecordedBy == actor, HttpContext.RequestAborted);
        if (prior is not null) return prior.ContentJson == content ? Ok(new { saved = true, id = prior.Id }) : Conflict("This request ID already identifies another correction or owner context.");
        var source = await db.WebsiteLeads.SingleOrDefaultAsync(x => x.LeadId == request.SubmissionId && x.AgentTrackingProfileId == null && x.CommerceBusinessId == null &&
            !db.WebsiteLeadIntakeLinks.Any(link => link.WebsiteLeadPublicId == x.LeadId), HttpContext.RequestAborted);
        if (source is null) return NotFound();
        // Preserve the originally received source metadata and public submission
        // identity. Only the explicit owner binding is repaired.
        source.AgentTrackingProfileId = owner.Id;
        try { await db.SaveChangesAsync(HttpContext.RequestAborted); }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null) await transaction.RollbackAsync(HttpContext.RequestAborted);
            db.ChangeTracker.Clear();
            return Conflict("The source owner changed concurrently. Reload the receipt; no repair was filed.");
        }
        var captured = await new Infrastructure.Leads.WebsiteLifeLeadCaptureService(db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Leads.WebsiteLifeLeadCaptureService>.Instance)
            .UpsertAsync(new Infrastructure.Leads.WebsiteLifeLeadCaptureRequest { WebsiteLeadId = source.LeadId,
                AgentTrackingProfileId = owner.Id, AgentSlug = owner.Slug, ProductType = source.InterestType ?? "inquiry",
                SubmittedUtc = source.CreatedUtc }, HttpContext.RequestAborted);
        if (!captured.Captured || captured.WorkstationLeadId is null)
        {
            if (transaction is not null) await transaction.RollbackAsync(HttpContext.RequestAborted);
            db.ChangeTracker.Clear();
            return UnprocessableEntity(new { saved = false, reason = captured.Reason });
        }
        var entry = await new CrmHistoryStore(db).StageAsync(new() { RelationshipKey = CrmHistoryStore.LeadKey(captured.WorkstationLeadId),
            Source = "OwnerAssociationCorrection", SourceId = sourceId, Type = "AssociationCorrection", RecordedBy = actor,
            OccurredUtc = DateTime.UtcNow, TimePrecision = "instant", ContentJson = content,
            EvidenceStatus = "Human-reviewed owner correction" }, HttpContext.RequestAborted);
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        if (transaction is not null) await transaction.CommitAsync(HttpContext.RequestAborted);
        return Ok(new { saved = true, id = entry.Id });
    }

    public sealed record PreservationInstruction(Guid RequestId, bool Release, Guid? HoldEntryId, string Reason);

    [HttpPost("Preservation")]
    [Authorize(Policy = "FounderOnly")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Preservation(string kind, string id, [FromBody] PreservationInstruction request)
    {
        if (!ModelState.IsValid || request is null) return BadRequest("The submitted fields are invalid.");
        if (!AgentPortal.Security.FounderGuard.IsFounder(User)) return Forbid();
        var relationship = await Resolve(kind, id); if (relationship is null) return NotFound();
        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 4000) return BadRequest();
        if (request.Release && (!request.HoldEntryId.HasValue || !await db.Set<CrmHistoryEntry>().AnyAsync(x =>
            x.Id == request.HoldEntryId && x.RelationshipKey == relationship.Key && x.Type == "PreservationHold", HttpContext.RequestAborted))) return BadRequest("Select the original preservation instruction.");
        var content = JsonSerializer.Serialize(request);
        var sourceId = request.RequestId.ToString("N");
        var eventId = CrmHistoryStore.EntryId(relationship.Key, "HumanPreservationInstruction", sourceId);
        var previous = await db.Set<CrmHistoryEntry>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == eventId, HttpContext.RequestAborted);
        if (previous is not null) return previous.ContentJson == content ? Ok(new { saved = true, id = previous.Id }) : Conflict();
        var entry = await new CrmHistoryStore(db).StageAsync(new() { RelationshipKey = relationship.Key,
            Source = "HumanPreservationInstruction", SourceId = sourceId, Type = request.Release ? "PreservationRelease" : "PreservationHold",
            RecordedBy = User.GetStableUserId() ?? Agent, OccurredUtc = DateTime.UtcNow, TimePrecision = "instant",
            RelatedEntryId = request.Release ? request.HoldEntryId : null, ContentJson = content,
            EvidenceStatus = "Human preservation instruction" }, HttpContext.RequestAborted);
        // This records an instruction; it never purges evidence or grants disposition authority.
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(new { saved = true, id = entry.Id });
    }

    [HttpGet("UnlinkedSubmissions")]
    public async Task<IActionResult> UnlinkedSubmissions(long afterRowId = 0)
    {
        if (RestrictedAiIdentity.IsRestricted(User, configuration) || string.IsNullOrWhiteSpace(Agent)) return NotFound();
        if (afterRowId < 0) return BadRequest();
        var rows = await db.WebsiteLeads.AsNoTracking().Where(x => x.Id > afterRowId && x.CommerceBusinessId == null &&
            db.AgentTrackingProfiles.Any(owner => owner.Id == x.AgentTrackingProfileId && owner.AgentUserId == Agent) &&
            !db.WebsiteLeadIntakeLinks.Any(link => link.WebsiteLeadPublicId == x.LeadId))
            .OrderBy(x => x.Id).Take(101)
            .Select(x => new { x.Id, x.LeadId, x.CreatedUtc, x.SourcePageKey, x.FirstName, x.LastName }).ToListAsync(HttpContext.RequestAborted);
        return Ok(new { submissions = rows.Take(100), hasMore = rows.Count > 100, nextAfterRowId = rows.Count > 100 ? rows[99].Id : (long?)null,
            scope = "Unlinked persisted submissions with an existing exact tracking-profile ownership mapping. Unknown ownership is not inferred from contact details." });
    }

    [HttpPost("ReconcileSubmission/{submissionId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReconcileSubmission(Guid submissionId)
    {
        if (RestrictedAiIdentity.IsRestricted(User, configuration) || string.IsNullOrWhiteSpace(Agent)) return NotFound();
        var source = await db.WebsiteLeads.AsNoTracking().SingleOrDefaultAsync(x => x.LeadId == submissionId && x.CommerceBusinessId == null &&
            db.AgentTrackingProfiles.Any(owner => owner.Id == x.AgentTrackingProfileId && owner.AgentUserId == Agent), HttpContext.RequestAborted);
        if (source is null) return NotFound();
        var result = await new Infrastructure.Leads.WebsiteLifeLeadCaptureService(db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Leads.WebsiteLifeLeadCaptureService>.Instance)
            .UpsertAsync(new Infrastructure.Leads.WebsiteLifeLeadCaptureRequest { WebsiteLeadId = source.LeadId,
                SubmittedUtc = source.CreatedUtc, ProductType = source.InterestType ?? "inquiry",
                AgentTrackingProfileId = source.AgentTrackingProfileId, AgentSlug = source.AgentSlug }, HttpContext.RequestAborted);
        if (!result.Captured) return UnprocessableEntity(new { saved = false, reason = result.Reason });
        return Ok(new { saved = true, id = result.WorkstationLeadId });
    }

    [HttpPost("CorrectAssociation")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CorrectAssociation([FromBody] AssociationCorrection request)
    {
        if (!ModelState.IsValid || request is null) return BadRequest("The submitted fields are invalid.");
        if (request.RequestId == Guid.Empty || request.SubmissionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 4000) return BadRequest();
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, HttpContext.RequestAborted) : null;
        var target = request.TargetClientProfileId.HasValue
            ? await Resolve("profile", request.TargetClientProfileId.Value.ToString("N")) : await Resolve("lead", request.TargetLeadId);
        if (target is null) return NotFound();
        var sourceId = request.RequestId.ToString("N");
        var auditId = CrmHistoryStore.EntryId(target.Key, "AssociationCorrection", sourceId);
        var prior = await db.Set<CrmHistoryEntry>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == auditId, HttpContext.RequestAborted);
        if (prior is not null)
        {
            using var original = JsonDocument.Parse(prior.ContentJson);
            return original.RootElement.GetProperty("request").Deserialize<AssociationCorrection>() == request
                ? Ok(new { saved = true, id = prior.Id }) : Conflict("This correction ID already identifies different content.");
        }
        if (target.Lead is null && !configuration.GetValue<bool>("CrmHistory:NativeClientAssociationsEnabled"))
            return StatusCode(503, "Direct client associations are awaiting verified compatible rollout. The original association is preserved.");
        var link = await db.WebsiteLeadIntakeLinks.SingleOrDefaultAsync(x => x.Id == request.SubmissionId &&
            x.CommerceBusinessId == (target.Lead == null ? null : target.Lead.CommerceBusinessId), HttpContext.RequestAborted);
        if (link is null) return NotFound();
        var previous = link.HistoryClientProfileId.HasValue ? await Resolve("profile", link.HistoryClientProfileId.Value.ToString("N"))
            : link.WorkstationLeadId is not null ? await Resolve("lead", link.WorkstationLeadId) : null;
        if (previous is null) return NotFound();
        if (link.AssociationVersion != request.ExpectedVersion) return Conflict("The association changed. Reload the current submission before correcting it.");
        if (previous.Key == target.Key) return BadRequest("This submission is already associated with that relationship.");
        var occurred = DateTime.UtcNow;
        var content = JsonSerializer.Serialize(new { request, originalRelationship = previous.Key, currentRelationship = target.Key,
            originalSubmissionId = link.Id, originalPublicSubmissionId = link.WebsiteLeadPublicId,
            originalSubmittedUtc = link.SubmittedUtc, correctionRecordedUtc = occurred });
        var store = new CrmHistoryStore(db);
        foreach (var relationship in new[] { previous.Key, target.Key })
            await store.StageAsync(new() { RelationshipKey = relationship, Source = "AssociationCorrection", SourceId = sourceId,
                Type = "AssociationCorrection", RecordedBy = User.GetStableUserId() ?? Agent, OccurredUtc = occurred,
                TimePrecision = "instant", SubmissionId = link.Id, ContentJson = content, EvidenceStatus = "Human-corrected association" }, HttpContext.RequestAborted);
        // Only the current association changes. Original answers, submission identity,
        // dates and the two immutable audit records are preserved.
        link.WorkstationLeadId = target.Lead?.LeadId;
        link.HistoryClientProfileId = target.Lead is null ? target.Client!.Id : null;
        link.AssociationVersion = Guid.NewGuid();
        await store.StageSubmissionAsync(link, HttpContext.RequestAborted);
        try { await db.SaveChangesAsync(HttpContext.RequestAborted); }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null) await transaction.RollbackAsync(HttpContext.RequestAborted);
            db.ChangeTracker.Clear();
            return Conflict("The association changed concurrently. Reload the submission before correcting it; no correction was filed.");
        }
        if (transaction is not null) await transaction.CommitAsync(HttpContext.RequestAborted);
        return Ok(new { saved = true, id = auditId });
    }

    [HttpGet("Reminders")]
    public async Task<IActionResult> Reminders()
    {
        if (RestrictedAiIdentity.IsRestricted(User, configuration) || string.IsNullOrWhiteSpace(Agent)) return NotFound();
        var zone = timeZones.Resolve(HttpContext);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        var through = DayStart(today.AddDays(1), zone);
        var candidates = await db.Set<CrmHistoryEntry>().AsNoTracking().Where(x => x.AssignedOwner == Agent && x.DueUtc < through &&
            (x.Type == "FollowUp" || x.Type == "Complaint") && !db.Set<CrmHistoryEntry>().Any(done => done.RelationshipKey == x.RelationshipKey &&
                done.RelatedEntryId == x.Id && (done.Type == "TaskCompleted" || done.Type == "TaskCanceled")))
            .OrderBy(x => x.DueUtc).ThenBy(x => x.Id).Take(501)
            .Select(x => new { x.Id, x.RelationshipKey, x.Type, x.DueUtc }).ToListAsync(HttpContext.RequestAborted);
        var access = new Dictionary<string, bool>(); var items = new List<object>();
        foreach (var item in candidates.Take(500))
        {
            var kind = item.RelationshipKey.StartsWith("lead:", StringComparison.Ordinal) ? "lead" : "profile";
            var id = item.RelationshipKey[(item.RelationshipKey.IndexOf(':') + 1)..];
            if (!access.TryGetValue(item.RelationshipKey, out var allowed))
                access[item.RelationshipKey] = allowed = await Resolve(kind, id) is not null;
            if (!allowed) continue;
            var day = EventDay(item.DueUtc, null, zone);
            items.Add(new { id = "history:" + item.Id, name = item.Type, nextDate = day, nextText = "Review filed relationship follow-up",
                email = "", phone = "", pri = item.Type == "Complaint" ? "High" : "Normal",
                overdue = string.CompareOrdinal(day, today.ToString("yyyy-MM-dd")) < 0, today = day == today.ToString("yyyy-MM-dd"),
                historyUrl = "/CrmHistory?" + "kind=" + kind + "&id=" + Uri.EscapeDataString(id) + "&entry=" + item.Id });
        }
        return Ok(new { items, hasMore = candidates.Count > 500, transport = "Existing opt-in browser reminders only" });
    }

    [HttpGet("Entries")]
    public async Task<IActionResult> Entries(string kind, string id, DateTime? fromUtc, DateTime? untilUtc,
        string? type, string? product, string? caseReference, string? owner, string? status, string? evidence,
        string? search, int page = 0, bool recordedTime = false, string range = "all", string? fromDate = null, string? throughDate = null,
        DateTime? snapshotUtc = null, string? obligations = null)
    {
        var relationship = await Resolve(kind, id); if (relationship is null) return NotFound();
        var snapshot = snapshotUtc ?? DateTime.UtcNow;
        if (snapshot.Kind == DateTimeKind.Local) snapshot = snapshot.ToUniversalTime();
        if (snapshot > DateTime.UtcNow || page < 0 || page > 10000) return BadRequest();
        var query = db.Set<CrmHistoryEntry>().AsNoTracking().Where(x => x.RelationshipKey == relationship.Key && x.FiledUtc <= snapshot);
        var zone = timeZones.Resolve(HttpContext);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        DateOnly? first = null, last = null;
        switch (range)
        {
            case "today": first = last = today; break;
            case "week": first = today.AddDays(-((int)today.DayOfWeek + 6) % 7); last = today; break;
            case "month": first = new DateOnly(today.Year, today.Month, 1); last = today; break;
            case "custom":
                if (!DateOnly.TryParseExact(fromDate, "yyyy-MM-dd", out var start) || !DateOnly.TryParseExact(throughDate, "yyyy-MM-dd", out var end) || start > end || end == DateOnly.MaxValue) return BadRequest("Select a valid inclusive date range.");
                first = start; last = end; break;
            case "all": break;
            case "unknown": query = query.Where(x => x.OccurredUtc == null && x.OccurredDate == null); break;
            default: return BadRequest("Unknown date range.");
        }
        if (first.HasValue)
        {
            fromUtc = DayStart(first.Value, zone); untilUtc = DayStart(last!.Value.AddDays(1), zone);
            var firstDate = first.Value.ToString("yyyy-MM-dd"); var lastDate = last.Value.ToString("yyyy-MM-dd");
            query = recordedTime ? query.Where(x => x.RecordedUtc >= fromUtc && x.RecordedUtc < untilUtc)
                : query.Where(x => x.OccurredUtc >= fromUtc && x.OccurredUtc < untilUtc ||
                    x.OccurredUtc == null && x.OccurredDate != null && string.Compare(x.OccurredDate, firstDate) >= 0 && string.Compare(x.OccurredDate, lastDate) <= 0);
        }
        if (!string.IsNullOrEmpty(type)) query = query.Where(x => x.Type == type);
        if (!string.IsNullOrEmpty(product)) query = query.Where(x => x.Product == product);
        if (!string.IsNullOrEmpty(caseReference)) query = query.Where(x => x.CaseReference == caseReference);
        if (!string.IsNullOrEmpty(owner)) query = query.Where(x => x.AssignedOwner == owner);
        if (!string.IsNullOrEmpty(status)) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrEmpty(evidence)) query = query.Where(x => x.EvidenceStatus == evidence);
        if (obligations is "open" or "overdue")
        {
            query = query.Where(x => (x.Type == "FollowUp" || x.Type == "Complaint") && !db.Set<CrmHistoryEntry>().Any(done =>
                done.RelationshipKey == relationship.Key && done.RelatedEntryId == x.Id && done.FiledUtc <= snapshot &&
                (done.Type == "TaskCompleted" || done.Type == "TaskCanceled")));
            if (obligations == "overdue") query = query.Where(x => x.DueUtc < snapshot);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            if (search.Length > 200) return BadRequest();
            query = query.Where(x => x.ContentJson.Contains(search));
        }
        var count = await query.CountAsync(HttpContext.RequestAborted);
        var prefix = checked(page * 50 + 50);
        System.Linq.Expressions.Expression<Func<CrmHistoryEntry, CrmHistoryListItem>> projection = x => new CrmHistoryListItem(x.Id, x.Type, x.Source, x.OccurredUtc, x.OccurredDate,
            x.RecordedUtc, x.FiledUtc, x.TimeZoneId, x.TimePrecision, x.Product, x.CaseReference,
            x.Status, x.EvidenceStatus, x.AssignedOwner, x.DueUtc, x.SubmissionId, x.FormName, x.FormVersion);
        List<CrmHistoryListItem> candidates;
        if (recordedTime)
            candidates = await query.OrderByDescending(x => x.RecordedUtc).ThenBy(x => x.Id).Take(prefix).Select(projection).ToListAsync(HttpContext.RequestAborted);
        else
        {
            candidates = await query.Where(x => x.OccurredUtc != null).OrderByDescending(x => x.OccurredUtc)
                .ThenByDescending(x => x.RecordedUtc).ThenBy(x => x.Id).Take(prefix).Select(projection).ToListAsync(HttpContext.RequestAborted);
            candidates.AddRange(await query.Where(x => x.OccurredUtc == null && x.OccurredDate != null)
                .OrderByDescending(x => x.OccurredDate).ThenByDescending(x => x.RecordedUtc).ThenBy(x => x.Id).Take(prefix).Select(projection).ToListAsync(HttpContext.RequestAborted));
            candidates.AddRange(await query.Where(x => x.OccurredUtc == null && x.OccurredDate == null)
                .OrderByDescending(x => x.RecordedUtc).ThenBy(x => x.Id).Take(prefix).Select(projection).ToListAsync(HttpContext.RequestAborted));
        }
        // SQL Server uniqueidentifier order differs from Guid.CompareTo. Keep
        // the merge's tie order identical to the database prefix on every page.
        var idComparer = HistoryIdComparer(db.Database.IsSqlServer());
        var rows = (recordedTime ? candidates.OrderByDescending(x => x.RecordedUtc).ThenBy(x => x.Id, idComparer)
            : candidates.OrderByDescending(x => EventDay(x.OccurredUtc, x.OccurredDate, zone), StringComparer.Ordinal)
                .ThenByDescending(x => x.OccurredUtc.HasValue).ThenByDescending(x => x.OccurredUtc)
                .ThenByDescending(x => x.RecordedUtc).ThenBy(x => x.Id, idComparer)).Skip(page * 50).Take(50).ToList();
        var activeHolds = await db.Set<CrmHistoryEntry>().AsNoTracking().Where(x => x.RelationshipKey == relationship.Key &&
            x.Type == "PreservationHold" && x.FiledUtc <= snapshot && !db.Set<CrmHistoryEntry>().Any(release =>
                release.RelationshipKey == relationship.Key && release.RelatedEntryId == x.Id && release.Type == "PreservationRelease" && release.FiledUtc <= snapshot))
            .Select(x => x.Id).ToListAsync(HttpContext.RequestAborted);
        var outstanding = db.Set<CrmHistoryEntry>().AsNoTracking().Where(x => x.RelationshipKey == relationship.Key &&
            (x.Type == "FollowUp" || x.Type == "Complaint") && x.FiledUtc <= snapshot && !db.Set<CrmHistoryEntry>().Any(done =>
                done.RelationshipKey == relationship.Key && done.RelatedEntryId == x.Id && done.FiledUtc <= snapshot &&
                (done.Type == "TaskCompleted" || done.Type == "TaskCanceled")));
        var openObligations = await outstanding.CountAsync(HttpContext.RequestAborted);
        var overdueObligations = await outstanding.CountAsync(x => x.DueUtc < snapshot, HttpContext.RequestAborted);
        return Ok(new { count, page, pageSize = 50, snapshotUtc = DateTime.SpecifyKind(snapshot, DateTimeKind.Utc), entries = rows, activeHolds, openObligations, overdueObligations });
    }

    private static readonly IComparer<Guid> SqlHistoryIdComparer = Comparer<Guid>.Create((left, right) =>
        new System.Data.SqlTypes.SqlGuid(left).CompareTo(new System.Data.SqlTypes.SqlGuid(right)));

    internal static IComparer<Guid> HistoryIdComparer(bool sqlServer) => sqlServer ? SqlHistoryIdComparer : Comparer<Guid>.Default;

    private static DateTime DayStart(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return zone.IsAmbiguousTime(local)
            ? DateTime.SpecifyKind(local - zone.GetAmbiguousTimeOffsets(local).Max(), DateTimeKind.Utc)
            : TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private sealed record CrmHistoryListItem(Guid Id, string Type, string Source, DateTime? OccurredUtc, string? OccurredDate,
        DateTime RecordedUtc, DateTime FiledUtc, string TimeZoneId, string TimePrecision, string? Product, string? CaseReference,
        string Status, string EvidenceStatus, string? AssignedOwner, DateTime? DueUtc, Guid? SubmissionId, string? FormName, string? FormVersion);

    private static string EventDay(DateTime? instant, string? day, TimeZoneInfo zone) => instant.HasValue
        ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(instant.Value, DateTimeKind.Utc), zone).ToString("yyyy-MM-dd") : day ?? "";

    [HttpGet("Entry/{entryId:guid}")]
    public async Task<IActionResult> Entry(Guid entryId, string kind, string id)
    {
        var relationship = await Resolve(kind, id); if (relationship is null) return NotFound();
        var entry = await db.Set<CrmHistoryEntry>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == entryId && x.RelationshipKey == relationship.Key, HttpContext.RequestAborted);
        if (entry is null) return NotFound();
        object? submission = null;
        string? association = null;
        if (entry.SubmissionId is Guid submissionId)
        {
            // Resolve already proves current relationship ownership. Intake.AgentUserId
            // is original provenance and must not revoke the new owner's access after
            // an authorized reassignment; the stable relationship and tenant still match.
            var link = await new CrmHistoryAccess(db).Submissions(relationship).SingleOrDefaultAsync(x => x.Id == submissionId, HttpContext.RequestAborted);
            if (link is null) association = "Submission evidence is unavailable under this relationship's current association. Review its correction history; no answers from another relationship are disclosed.";
            else submission = new { link.WebsiteLeadPublicId, link.SourcePageKey, link.SubmittedUtc, link.CapturedUtc,
                link.ProductType, link.SnapshotJson, link.AssociationVersion, completeness = "Captured original fields only. Missing historical question wording and authorization evidence are unavailable." };
        }
        await new CrmHistoryStore(db).StageAsync(new() { RelationshipKey = relationship.Key, Source = "HistoryAccess",
            SourceId = Guid.NewGuid().ToString("N"), Type = "Access", RecordedBy = User.GetStableUserId() ?? Agent,
            OccurredUtc = DateTime.UtcNow, TimePrecision = "instant", RelatedEntryId = entry.Id,
            ContentJson = JsonSerializer.Serialize(new { action = "Authenticated detail read", entryId = entry.Id, submissionEvidenceIncluded = submission is not null }) }, HttpContext.RequestAborted);
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(new { entry, submission, association });
    }

    [HttpPost("Export")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Export(string kind, string id)
    {
        var relationship = await Resolve(kind, id); if (relationship is null) return NotFound();
        var rows = await db.Set<CrmHistoryEntry>().AsNoTracking().Where(x => x.RelationshipKey == relationship.Key)
            .OrderBy(x => x.OccurredUtc).ThenBy(x => x.OccurredDate).ThenBy(x => x.RecordedUtc).ThenBy(x => x.Id)
            .Take(10001).ToListAsync(HttpContext.RequestAborted);
        if (rows.Count > 10000) return BadRequest("This export exceeds the bounded 10,000-entry export. No partial export was produced.");
        var exportZone = timeZones.Resolve(HttpContext);
        rows = rows.OrderByDescending(x => EventDay(x.OccurredUtc, x.OccurredDate, exportZone), StringComparer.Ordinal)
            .ThenByDescending(x => x.OccurredUtc.HasValue).ThenByDescending(x => x.OccurredUtc)
            .ThenByDescending(x => x.RecordedUtc).ThenBy(x => x.Id).ToList();
        var submissionIds = rows.Where(x => x.SubmissionId.HasValue).Select(x => x.SubmissionId!.Value).Distinct().ToArray();
        var submissions = new List<WebsiteLeadIntakeLink>();
        if (submissionIds.Length != 0)
            submissions = await new CrmHistoryAccess(db).Submissions(relationship).Where(x => submissionIds.Contains(x.Id)).ToListAsync(HttpContext.RequestAborted);
        var included = submissions.Select(x => x.Id).ToHashSet();
        var exportId = Guid.NewGuid();
        await new CrmHistoryStore(db).StageAsync(new() { RelationshipKey = relationship.Key, Source = "HistoryExport",
            SourceId = exportId.ToString("N"), Type = "Export", RecordedBy = User.GetStableUserId() ?? Agent,
            RecordedUtc = DateTime.UtcNow, OccurredUtc = DateTime.UtcNow, TimePrecision = "instant",
            ContentJson = JsonSerializer.Serialize(new { exportId, count = rows.Count, submissionCount = submissions.Count,
                omittedSubmissionCount = submissionIds.Count(x => !included.Contains(x)), scope = "Relationship chronology and authorized immutable submission evidence" }) }, HttpContext.RequestAborted);
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        var chronology = string.Join("\n\n", rows.Select(x => $"{x.OccurredUtc?.ToString("O") ?? x.OccurredDate ?? "Unknown event date"} | {x.Type} | {x.Id}\nRecorded {x.RecordedUtc:O} by {x.RecordedBy}\n{x.ContentJson}"));
        return File(JsonSerializer.SerializeToUtf8Bytes(new { exportId, generatedUtc = DateTime.UtcNow,
            relationship = relationship.Key, chronology, entries = rows,
            evidenceIndex = submissions.Select(x => new { x.Id, x.WebsiteLeadPublicId, x.SourcePageKey, x.SubmittedUtc, x.CapturedUtc,
                x.ProductType, x.SnapshotJson, completeness = "Original captured fields; historical gaps and browser-reported presentation retain their recorded labels." }),
            omittedSubmissions = submissionIds.Where(x => !included.Contains(x)).Select(x => new { id = x, reason = "Not available under this relationship's current association and permissions." }),
            detailLinks = rows.Select(x => new { x.Id, path = "/CrmHistory/Entry/" + x.Id + "?kind=" + Uri.EscapeDataString(kind) + "&id=" + Uri.EscapeDataString(id) }),
            protectedDocuments = rows.Where(x => x.Source == "ProtectedDocument").Select(x => new { x.Id, x.RelatedEntryId,
                receipt = JsonSerializer.Deserialize<CrmEvidenceController.Receipt>(x.ContentJson),
                downloadPath = "/CrmHistory/Evidence/" + x.Id + "?kind=" + Uri.EscapeDataString(kind) + "&id=" + Uri.EscapeDataString(id),
                exportState = "Protected version reference included; bytes are verified on authenticated download, not embedded in this JSON export." }),
            limitations = "Binary bytes and external reference contents are not embedded; protected document versions include authenticated access and integrity receipts. Manual evidence is the recorder's account, not an independently captured client signature. Protected detail links require current authorization." }), "application/json", "case-history-" + exportId.ToString("N") + ".json");
    }

    public sealed record Interaction(Guid RequestId, string Type, string Account, DateTime? OccurredUtc,
        string? OccurredDate, string TimeZoneId, string? Channel, string? Direction, string? Participants,
        string? Outcome, string? NextAction, string? AssignedOwner, DateTime? DueUtc, string? CaseReference,
        Guid? RelatedEntryId, string? Product, string? EvidenceSource = null, string? EvidenceReference = null,
        string? EvidenceState = null);

    [HttpPost("Entries")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Append(string kind, string id, [FromBody] Interaction request)
    {
        if (!ModelState.IsValid || request is null) return BadRequest("The submitted fields are invalid.");
        var relationship = await Resolve(kind, id); if (relationship is null) return NotFound();
        string[] types = ["Call", "AttemptedCall", "Meeting", "Email", "Message", "Note", "Document", "Milestone", "FollowUp", "Complaint", "Addendum", "TaskCompleted", "TaskCanceled"];
        if (request.RequestId == Guid.Empty || !types.Contains(request.Type) || string.IsNullOrWhiteSpace(request.Account) || request.Account.Length > 20000 || string.IsNullOrWhiteSpace(request.TimeZoneId) || request.TimeZoneId.Length > 100) return BadRequest("A stable request ID, interaction type and factual account are required.");
        try { TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId); } catch (TimeZoneNotFoundException) { return BadRequest("Unknown timezone."); }
        if (request.OccurredDate is not null && !DateOnly.TryParseExact(request.OccurredDate, "yyyy-MM-dd", out _) ||
            request.OccurredUtc.HasValue && request.OccurredDate is not null ||
            request.OccurredUtc.HasValue && request.OccurredUtc.Value.Kind != DateTimeKind.Utc ||
            request.DueUtc.HasValue && request.DueUtc.Value.Kind != DateTimeKind.Utc)
            return BadRequest("Supply one valid event date or UTC instant; due times must include UTC timezone.");
        if (request.Channel?.Length > 80 || request.Direction?.Length > 80 || request.Participants?.Length > 2000 ||
            request.Outcome?.Length > 4000 || request.NextAction?.Length > 4000 || request.AssignedOwner?.Length > 450 ||
            request.CaseReference?.Length > 200 || request.Product?.Length > 100 || request.EvidenceSource?.Length > 500 ||
            request.EvidenceReference?.Length > 2000) return BadRequest("A field exceeds its allowed length.");
        string[] evidenceStates = ["Evidence unavailable", "Manual source reference", "Human-reported source inspection", "Later recollection"];
        if (request.EvidenceState is not null && !evidenceStates.Contains(request.EvidenceState)) return BadRequest("Unknown evidence state.");
        if (request.Type == "Document" && string.IsNullOrWhiteSpace(request.EvidenceSource)) return BadRequest("Identify the external evidence source; a manual entry does not attest to the client's signature.");
        if (request.Type is "FollowUp" or "Complaint" && (string.IsNullOrWhiteSpace(request.AssignedOwner) || !request.DueUtc.HasValue))
            return BadRequest("A follow-up or complaint needs an authorized responsible owner and review due time.");
        if (request.Type is "Addendum" or "TaskCompleted" or "TaskCanceled" && !request.RelatedEntryId.HasValue)
            return BadRequest("Select the original entry for this change.");
        if (request.RelatedEntryId.HasValue && !await db.Set<CrmHistoryEntry>().AnyAsync(x => x.Id == request.RelatedEntryId && x.RelationshipKey == relationship.Key, HttpContext.RequestAborted)) return BadRequest("Related record is unavailable.");
        if (request.Type is "TaskCompleted" or "TaskCanceled" && !await db.Set<CrmHistoryEntry>().AnyAsync(x =>
            x.Id == request.RelatedEntryId && x.RelationshipKey == relationship.Key && (x.Type == "FollowUp" || x.Type == "Complaint"), HttpContext.RequestAborted))
            return BadRequest("The related record must be a follow-up or complaint.");
        if (!string.IsNullOrWhiteSpace(request.AssignedOwner) && request.AssignedOwner != Agent) return BadRequest("Assign follow-up to the current authorized owner.");
        var payload = JsonSerializer.Serialize(request);
        var sourceId = request.RequestId.ToString("N");
        var existing = await db.Set<CrmHistoryEntry>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == CrmHistoryStore.EntryId(relationship.Key, "HumanInteraction", sourceId), HttpContext.RequestAborted);
        if (existing is not null) return existing.ContentJson == payload ? Ok(new { saved = true, id = existing.Id }) : Conflict("This request ID already identifies different content.");
        var entry = await new CrmHistoryStore(db).StageAsync(new()
        {
            RelationshipKey = relationship.Key, Source = "HumanInteraction", SourceId = sourceId, Type = request.Type,
            RecordedBy = User.GetStableUserId() ?? Agent, RecordedUtc = DateTime.UtcNow,
            OccurredUtc = request.OccurredUtc, OccurredDate = request.OccurredDate, TimeZoneId = request.TimeZoneId,
            TimePrecision = request.OccurredUtc.HasValue ? "instant" : request.OccurredDate is not null ? "date" : "unknown",
            ContentJson = payload, RelatedEntryId = request.RelatedEntryId, Product = request.Product,
            CaseReference = request.CaseReference, AssignedOwner = request.AssignedOwner, DueUtc = request.DueUtc,
            Status = request.Type is "TaskCompleted" or "TaskCanceled" ? request.Type : "Filed", EvidenceStatus = request.EvidenceState ?? "Evidence unavailable"
        }, HttpContext.RequestAborted);
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(new { saved = true, id = entry.Id });
    }
}
