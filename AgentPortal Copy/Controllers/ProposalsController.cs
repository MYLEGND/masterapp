using System.Security.Claims;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Controllers;

[Authorize]
[Route("api/[controller]")]
[ApiController]
public class ProposalsController : ControllerBase
{
    private readonly MasterAppDbContext _db;

    public ProposalsController(MasterAppDbContext db)
    {
        _db = db;
    }

    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? leadId, [FromQuery] bool includeDrafts = false)
    {
        var userId = CurrentUserId;
        if (string.IsNullOrWhiteSpace(userId)) return Forbid();

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
        if (string.IsNullOrWhiteSpace(userId)) return Forbid();

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
        if (string.IsNullOrWhiteSpace(userId)) return Forbid();
        if (string.IsNullOrWhiteSpace(dto.LeadId)) return BadRequest("LeadId is required");

        var requestedId = dto.Id.GetValueOrDefault();
        if (requestedId != Guid.Empty)
        {
            var existing = await _db.Proposals.FirstOrDefaultAsync(p => p.Id == requestedId && p.AgentUserId == userId);
            if (existing != null)
            {
                if (!SameLead(existing, dto)) return BadRequest("LeadId cannot be changed");
                Apply(existing, dto);
                existing.UpdatedUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                return Ok(existing);
            }
        }

        var now = DateTime.UtcNow;
        var entity = new Proposal
        {
            Id = requestedId != Guid.Empty ? requestedId : Guid.NewGuid(),
            LeadId = dto.LeadId.Trim(),
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
        if (string.IsNullOrWhiteSpace(userId)) return Forbid();

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
        if (string.IsNullOrWhiteSpace(userId)) return Forbid();

        var entity = await _db.Proposals.FirstOrDefaultAsync(p => p.Id == id && p.AgentUserId == userId);
        if (entity == null) return NotFound();

        _db.Proposals.Remove(entity);
        await _db.SaveChangesAsync();
        return Ok();
    }

    private static bool SameLead(Proposal entity, ProposalDto dto)
        => string.Equals(entity.LeadId, dto.LeadId?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

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
