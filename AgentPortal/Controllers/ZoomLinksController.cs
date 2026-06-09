using System.Security.Claims;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace AgentPortal.Controllers;

[Authorize]
[Route("api/zoom-links")]
[ApiController]
[IgnoreAntiforgeryToken]
public class ZoomLinksController : ControllerBase
{
    private readonly MasterAppDbContext _db;

    public ZoomLinksController(MasterAppDbContext db)
    {
        _db = db;
    }

    private string CurrentUserId =>
        (User.GetStableUserId() ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty).Trim();

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var userId = CurrentUserId;
        if (string.IsNullOrWhiteSpace(userId)) return Challenge();

        var links = await _db.AgentZoomLinks
            .AsNoTracking()
            .Where(z => z.AgentUserId == userId)
            .OrderBy(z => z.SortOrder)
            .ThenBy(z => z.CreatedUtc)
            .ToListAsync();

        return Ok(links);
    }

    public class ZoomLinkDto
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ZoomLinkDto dto)
    {
        var userId = CurrentUserId;
        if (string.IsNullOrWhiteSpace(userId)) return Challenge();

        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest("Name is required.");
        if (string.IsNullOrWhiteSpace(dto.Url)) return BadRequest("URL is required.");

        var maxSort = await _db.AgentZoomLinks
            .Where(z => z.AgentUserId == userId)
            .Select(z => (int?)z.SortOrder)
            .MaxAsync() ?? -1;

        var link = new AgentZoomLink
        {
            AgentUserId = userId,
            Name = dto.Name.Trim(),
            Url = dto.Url.Trim(),
            SortOrder = maxSort + 1
        };

        _db.AgentZoomLinks.Add(link);
        await _db.SaveChangesAsync();

        return Ok(link);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = CurrentUserId;
        if (string.IsNullOrWhiteSpace(userId)) return Challenge();

        var link = await _db.AgentZoomLinks
            .FirstOrDefaultAsync(z => z.Id == id && z.AgentUserId == userId);

        if (link == null) return NotFound();

        _db.AgentZoomLinks.Remove(link);
        await _db.SaveChangesAsync();

        return Ok();
    }
}
