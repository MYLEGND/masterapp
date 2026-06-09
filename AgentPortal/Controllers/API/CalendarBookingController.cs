using Domain.Entities;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Infrastructure.Data;

[ApiController]
[Route("calendar")]
public class CalendarBookingController : ControllerBase
{
    private readonly MasterAppDbContext _db;

    public CalendarBookingController(MasterAppDbContext db)
    {
        _db = db;
    }

    [HttpPost("book_DISABLED")]
    public async Task<IActionResult> Book([FromBody] BookRequest req)
    {
        var overlap = _db.LeadAppointments.Any(x =>
            x.OwnerAgentUserId == req.OwnerAgentUserId &&
            x.ScheduledStartUtc < req.ScheduledEndUtc &&
            x.ScheduledEndUtc > req.ScheduledStartUtc);

        if (overlap)
            return BadRequest("Time slot already booked");

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
        
    }
}
