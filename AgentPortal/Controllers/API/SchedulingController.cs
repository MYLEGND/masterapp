using Domain.Entities;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Infrastructure.Data;

[ApiController]
[Route("calendar")]
public class SchedulingController : ControllerBase
{
    private readonly MasterAppDbContext _db;

    public SchedulingController(MasterAppDbContext db)
    {
        _db = db;
    }

    // 1. AVAILABILITY (DB ONLY FIRST — Graph added later)
    [HttpGet("availability")]
    public async Task<IActionResult> Availability(string agentUserId, DateTime start, DateTime end)
    {
        var busy = await _db.LeadAppointments
            .Where(x => x.OwnerAgentUserId == agentUserId &&
                        x.ScheduledStartUtc < end &&
                        x.ScheduledEndUtc > start)
            .Select(x => new { x.ScheduledStartUtc, x.ScheduledEndUtc })
            .ToListAsync();

        return Ok(new { busy });
    }

    // 2. SLOT GENERATION (server-side source of truth)
    [HttpGet("slots")]
    public IActionResult Slots(DateTime start, DateTime end)
    {
        var slots = new List<DateTime>();
        var cursor = start;

        while (cursor < end)
        {
            slots.Add(cursor);
            cursor = cursor.AddMinutes(30);
        }

        return Ok(new { slots });
    }

    // 3. BOOKING ORCHESTRATOR (final write path)
    [HttpPost("book")]

    public async Task<IActionResult> Book([FromBody] BookRequest req)
    {
        Console.WriteLine("BOOK PAYLOAD: " + System.Text.Json.JsonSerializer.Serialize(req));
        var conflict = await _db.LeadAppointments.AnyAsync(x =>
            x.OwnerAgentUserId == req.OwnerAgentUserId &&
            x.ScheduledStartUtc < req.ScheduledEndUtc &&
            x.ScheduledEndUtc > req.ScheduledStartUtc);

        if (conflict)
            return BadRequest("Slot already booked");

        var appt = new LeadAppointment
        {
            Id = Guid.NewGuid(),
            OwnerAgentUserId = req.OwnerAgentUserId,
            ScheduledStartUtc = req.ScheduledStartUtc,
            ScheduledEndUtc = req.ScheduledEndUtc,
            
            Status = LeadAppointmentStatus.Booked,
            CreatedUtc = DateTime.UtcNow
        };

        _db.LeadAppointments.Add(appt);
        await _db.SaveChangesAsync();

        return Ok(appt);
    }

    public class BookRequest
    {
        public string OwnerAgentUserId { get; set; }
        public DateTime ScheduledStartUtc { get; set; }
        public DateTime ScheduledEndUtc { get; set; }
        public string Title { get; set; }
    }
}
