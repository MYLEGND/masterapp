using Microsoft.AspNetCore.Mvc;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Route("calendar")]
public class CalendarAvailabilityController : ControllerBase
{
    private readonly MasterAppDbContext _db;

    public CalendarAvailabilityController(MasterAppDbContext db)
    {
        _db = db;
    }

    [HttpGet("availability")]
    public async Task<IActionResult> GetAvailability(string agentUserId, DateTime start, DateTime end)
    {
        var busy = await _db.LeadAppointments
            .Where(x => x.OwnerAgentUserId == agentUserId &&
                        x.ScheduledStartUtc < end &&
                        x.ScheduledEndUtc > start)
            .Select(x => new {
                x.ScheduledStartUtc,
                x.ScheduledEndUtc
            })
            .ToListAsync();

        return Ok(new { busyBlocks = busy });
    }
}
