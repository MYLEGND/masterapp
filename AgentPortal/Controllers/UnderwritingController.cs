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
[IgnoreAntiforgeryToken] // Workstation AJAX uses authenticated same-origin fetch; avoid stale token 400s.
public class UnderwritingController : ControllerBase
{
    private readonly MasterAppDbContext _db;

    public UnderwritingController(MasterAppDbContext db)
    {
        _db = db;
    }

    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? leadId, [FromQuery] string? productCode, [FromQuery] bool includeDrafts = true)
    {
        var userId = CurrentUserId;
        if (string.IsNullOrWhiteSpace(userId)) return Forbid();

        var query = _db.UnderwritingRecords.AsNoTracking().Where(x => x.AgentUserId == userId);
        if (!string.IsNullOrWhiteSpace(leadId)) query = query.Where(x => x.LeadId == leadId);
        if (!string.IsNullOrWhiteSpace(productCode)) query = query.Where(x => x.ProductCode == productCode);
        if (!includeDrafts) query = query.Where(x => !x.IsDraft);

        var list = await query.OrderByDescending(x => x.UpdatedUtc).ToListAsync();
        return Ok(list);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetOne(Guid id)
    {
        var userId = CurrentUserId;
        if (string.IsNullOrWhiteSpace(userId)) return Forbid();

        var record = await _db.UnderwritingRecords.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.AgentUserId == userId);
        if (record == null) return NotFound();
        return Ok(record);
    }

    public class UnderwritingDto
    {
        public Guid? Id { get; set; }
        public string LeadId { get; set; } = string.Empty;
        public string LeadName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        public string QueueKey { get; set; } = string.Empty;
        public string ScopeKey { get; set; } = string.Empty;
        public string PageTitle { get; set; } = string.Empty;
        public bool IsDraft { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UnderwritingDto dto)
    {
        var userId = CurrentUserId;
        if (string.IsNullOrWhiteSpace(userId)) return Forbid();

        var requestedId = dto.Id.GetValueOrDefault();
        if (requestedId != Guid.Empty)
        {
            var existing = await _db.UnderwritingRecords.FirstOrDefaultAsync(x => x.Id == requestedId && x.AgentUserId == userId);
            if (existing != null)
            {
                Apply(existing, dto);
                existing.UpdatedUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                return Ok(existing);
            }
        }

        var now = DateTime.UtcNow;
        var entity = new UnderwritingRecord
        {
            Id = requestedId != Guid.Empty ? requestedId : Guid.NewGuid(),
            LeadId = dto.LeadId?.Trim() ?? string.Empty,
            LeadName = dto.LeadName?.Trim() ?? string.Empty,
            AgentUserId = userId,
            Name = dto.Name?.Trim() ?? "Underwriting",
            PayloadJson = string.IsNullOrWhiteSpace(dto.PayloadJson) ? "{}" : dto.PayloadJson,
            ProductCode = dto.ProductCode?.Trim() ?? string.Empty,
            QueueKey = dto.QueueKey?.Trim() ?? string.Empty,
            ScopeKey = dto.ScopeKey?.Trim() ?? string.Empty,
            PageTitle = dto.PageTitle?.Trim() ?? string.Empty,
            IsDraft = dto.IsDraft,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        _db.UnderwritingRecords.Add(entity);
        await _db.SaveChangesAsync();
        return Ok(entity);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UnderwritingDto dto)
    {
        var userId = CurrentUserId;
        if (string.IsNullOrWhiteSpace(userId)) return Forbid();

        var entity = await _db.UnderwritingRecords.FirstOrDefaultAsync(x => x.Id == id && x.AgentUserId == userId);
        if (entity == null) return NotFound();

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

        var entity = await _db.UnderwritingRecords.FirstOrDefaultAsync(x => x.Id == id && x.AgentUserId == userId);
        if (entity == null) return NotFound();

        _db.UnderwritingRecords.Remove(entity);
        await _db.SaveChangesAsync();
        return Ok();
    }

    private static void Apply(UnderwritingRecord entity, UnderwritingDto dto)
    {
        entity.Name = dto.Name?.Trim() ?? entity.Name;
        entity.LeadId = dto.LeadId?.Trim() ?? entity.LeadId;
        entity.LeadName = dto.LeadName?.Trim() ?? entity.LeadName;
        entity.PayloadJson = string.IsNullOrWhiteSpace(dto.PayloadJson) ? entity.PayloadJson : dto.PayloadJson;
        entity.ProductCode = dto.ProductCode?.Trim() ?? entity.ProductCode;
        entity.QueueKey = dto.QueueKey?.Trim() ?? entity.QueueKey;
        entity.ScopeKey = dto.ScopeKey?.Trim() ?? entity.ScopeKey;
        entity.PageTitle = dto.PageTitle?.Trim() ?? entity.PageTitle;
        entity.IsDraft = dto.IsDraft;
    }
}
