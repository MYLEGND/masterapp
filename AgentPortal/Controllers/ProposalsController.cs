using System.Security.Claims;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AgentPortal.Services;
using Shared.Auth;

namespace AgentPortal.Controllers;

[Authorize]
[Route("api/[controller]")]
[ApiController]
[IgnoreAntiforgeryToken] // Workstation AJAX uses authenticated same-origin fetch; avoid stale token 400s.
public class ProposalsController : ControllerBase
{
    private readonly MasterAppDbContext _db;
    private readonly IDecisionService _decisions;
    private readonly IPlaybookEngine _playbook;

    public ProposalsController(MasterAppDbContext db, IDecisionService decisions, IPlaybookEngine playbook)
    {
        _db = db;
        _decisions = decisions;
        _playbook = playbook;
    }

    private string CurrentUserId =>
        (User.GetStableUserId() ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty).Trim();

    private IActionResult ChallengeIfMissingUser()
    {
        if (string.IsNullOrWhiteSpace(CurrentUserId))
            return Challenge();
        return null!;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? leadId, [FromQuery] bool includeDrafts = false)
    {
        var userId = CurrentUserId;
        if (string.IsNullOrWhiteSpace(userId)) return Challenge();

        var query = _db.Proposals.AsNoTracking().Where(p => p.AgentUserId == userId);
        if (!string.IsNullOrWhiteSpace(leadId)) query = query.Where(p => p.LeadId == leadId);
        if (!includeDrafts) query = query.Where(p => !p.IsDraft);

        var list = await query.OrderByDescending(p => p.UpdatedUtc).ToListAsync();
        return Ok(list);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetOne(Guid id)
    {
        var userId = CurrentUserId;
        if (string.IsNullOrWhiteSpace(userId)) return Challenge();

        var proposal = await _db.Proposals.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.AgentUserId == userId);
        if (proposal == null) return NotFound();
        return Ok(proposal);
    }

    public class ProposalDto
    {
        public Guid? Id { get; set; }
        public string LeadId { get; set; } = string.Empty;
        public string LeadName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string BucketsJson { get; set; } = string.Empty;
        public string QueueKey { get; set; } = string.Empty;
        public string ScopeKey { get; set; } = string.Empty;
        public string LeadKey { get; set; } = string.Empty;
        public string PageTitle { get; set; } = string.Empty;
        public bool IsDraft { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ProposalDto dto)
    {
        var userId = CurrentUserId;
        if (string.IsNullOrWhiteSpace(userId)) return Challenge();

        var requestedId = dto.Id.GetValueOrDefault();
        if (requestedId != Guid.Empty)
        {
            var existing = await _db.Proposals.FirstOrDefaultAsync(p => p.Id == requestedId && p.AgentUserId == userId);
            if (existing != null)
            {
                // If a client generated id collides with an existing proposal for another lead,
                // create a new record instead of blocking all saves/migrations.
                if (!SameLead(existing, dto))
                {
                    requestedId = Guid.Empty;
                }
                else
                {
                    Apply(existing, dto);
                    existing.UpdatedUtc = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                    return Ok(existing);
                }
            }
        }

        var now = DateTime.UtcNow;
        var entity = new Proposal
        {
            Id = requestedId != Guid.Empty ? requestedId : Guid.NewGuid(),
            LeadId = (dto.LeadId ?? "").Trim(),
            LeadName = dto.LeadName?.Trim() ?? string.Empty,
            AgentUserId = userId,
            Name = dto.Name?.Trim() ?? "Proposal",
            BucketsJson = string.IsNullOrWhiteSpace(dto.BucketsJson) ? "[]" : dto.BucketsJson,
            QueueKey = dto.QueueKey?.Trim() ?? string.Empty,
            ScopeKey = dto.ScopeKey?.Trim() ?? string.Empty,
            LeadKey = dto.LeadKey?.Trim() ?? string.Empty,
            PageTitle = dto.PageTitle?.Trim() ?? string.Empty,
            IsDraft = dto.IsDraft,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        _db.Proposals.Add(entity);
        await _db.SaveChangesAsync();
        return Ok(entity);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ProposalDto dto)
    {
        var userId = CurrentUserId;
        if (string.IsNullOrWhiteSpace(userId)) return Challenge();

        var entity = await _db.Proposals.FirstOrDefaultAsync(p => p.Id == id && p.AgentUserId == userId);
        if (entity == null) return NotFound();
        if (!SameLead(entity, dto)) return BadRequest("LeadId cannot be changed");

        Apply(entity, dto);
        entity.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(entity);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = CurrentUserId;
        if (string.IsNullOrWhiteSpace(userId)) return Challenge();

        var entity = await _db.Proposals.FirstOrDefaultAsync(p => p.Id == id && p.AgentUserId == userId);
        if (entity == null) return NotFound();

        _db.Proposals.Remove(entity);
        await _db.SaveChangesAsync();
        return Ok();
    }

    public class DecisionDto
    {
        public string Title { get; set; } = string.Empty;
        public string Rationale { get; set; } = string.Empty;
        public DecisionType RecommendationType { get; set; } = DecisionType.ProposalRecommendation;
    }

    [HttpGet("{id:guid}/decision/latest")]
    public async Task<IActionResult> GetLatestDecision(Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (string.IsNullOrWhiteSpace(userId)) return Challenge();

        var proposal = await _db.Proposals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.AgentUserId == userId, ct);
        if (proposal == null) return NotFound();

        var decision = await _decisions.GetLatestByEntityAsync(proposal.Id.ToString(), RelatedEntityType.Proposal.ToString(), ct);
        if (decision == null) return NotFound();
        return Ok(decision);
    }

    [HttpPost("{id:guid}/decision")]
    public async Task<IActionResult> CaptureDecision(Guid id, [FromBody] DecisionDto dto)
    {
        if (dto == null) return BadRequest("Decision payload required.");
        var userId = CurrentUserId;
        if (string.IsNullOrWhiteSpace(userId)) return Challenge();

        var proposal = await _db.Proposals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.AgentUserId == userId);
        if (proposal == null) return NotFound();

        var decision = new DecisionRecord
        {
            RelatedEntityType = RelatedEntityType.Proposal,
            RelatedEntityId = proposal.Id.ToString(),
            Title = string.IsNullOrWhiteSpace(dto.Title) ? "Proposal Decision" : dto.Title.Trim(),
            Rationale = dto.Rationale?.Trim() ?? string.Empty,
            RecommendationType = dto.RecommendationType,
            CreatedBy = userId,
            CreatedUtc = DateTime.UtcNow
        };

        await _decisions.CreateDecisionAsync(decision);
        await _playbook.HandleAsync("proposal-finalized", $"proposal-finalized:{proposal.Id}", new { ProposalId = proposal.Id, proposal.AgentUserId }, HttpContext.RequestAborted);

        return Ok(decision);
    }

    private static bool SameLead(Proposal entity, ProposalDto dto)
    {
        var entityLeadId = (entity.LeadId ?? string.Empty).Trim();
        var dtoLeadId = (dto.LeadId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(entityLeadId) || !string.IsNullOrWhiteSpace(dtoLeadId))
            return string.Equals(entityLeadId, dtoLeadId, StringComparison.OrdinalIgnoreCase);

        var entityLeadKey = (entity.LeadKey ?? string.Empty).Trim();
        var dtoLeadKey = (dto.LeadKey ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(entityLeadKey) || !string.IsNullOrWhiteSpace(dtoLeadKey))
            return string.Equals(entityLeadKey, dtoLeadKey, StringComparison.OrdinalIgnoreCase);

        return string.Equals(
            (entity.ScopeKey ?? string.Empty).Trim(),
            (dto.ScopeKey ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void Apply(Proposal entity, ProposalDto dto)
    {
        entity.Name = dto.Name?.Trim() ?? entity.Name;
        entity.LeadName = dto.LeadName?.Trim() ?? entity.LeadName;
        entity.BucketsJson = string.IsNullOrWhiteSpace(dto.BucketsJson) ? entity.BucketsJson : dto.BucketsJson;
        entity.QueueKey = dto.QueueKey?.Trim() ?? entity.QueueKey;
        entity.ScopeKey = dto.ScopeKey?.Trim() ?? entity.ScopeKey;
        entity.LeadKey = dto.LeadKey?.Trim() ?? entity.LeadKey;
        entity.PageTitle = dto.PageTitle?.Trim() ?? entity.PageTitle;
        entity.IsDraft = dto.IsDraft;
    }
}
