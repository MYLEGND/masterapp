
using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;



namespace ClientApp.Controllers
{
    [Authorize]
    [Route("production")]
    public class ProductionController : Controller
    {
        private readonly MasterAppDbContext _db;
        public ProductionController(MasterAppDbContext db)
        {
            _db = db;
        }

        // GET: /production/history/client?clientId=xxx
        [HttpGet("history/client")]
        public async Task<IActionResult> ClientHistory(string clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                return BadRequest("Missing clientId");
            var records = await _db.ProductionRecords
                .Where(x => x.ClientUserId == clientId)
                .OrderByDescending(x => x.CreatedUtc)
                .ToListAsync();
            return Json(records);
        }

        // POST: /production/add/client
        [HttpPost("add/client")]
        public async Task<IActionResult> AddClient([FromForm] string clientId, [FromForm] decimal amount, [FromForm] decimal? personalAmount, [FromForm] int status, [FromForm] string? notes)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                return BadRequest("Missing clientId");
            var record = new ProductionRecord
            {
                ClientUserId = clientId,
                Amount = amount,
                PersonalAmount = personalAmount ?? 0,
                Status = (ProductionStatus)status,
                Side = ProductionSide.Client,
                Notes = notes,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                AgentUserId = User.Identity?.Name ?? ""
            };
            _db.ProductionRecords.Add(record);
            await _db.SaveChangesAsync();
            return Ok();
        }

        // POST: /production/update
        [HttpPost("update")]
        public async Task<IActionResult> Update([FromForm] Guid id, [FromForm] decimal amount, [FromForm] decimal? personalAmount, [FromForm] int status, [FromForm] string? notes)
        {
            var record = await _db.ProductionRecords.FindAsync(id);
            if (record == null) return NotFound();
            record.Amount = amount;
            record.PersonalAmount = personalAmount ?? 0;
            record.Status = (ProductionStatus)status;
            record.Notes = notes;
            record.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok();
        }

        // POST: /production/delete
        [HttpPost("delete")]
        public async Task<IActionResult> Delete([FromForm] Guid id)
        {
            var record = await _db.ProductionRecords.FindAsync(id);
            if (record == null) return NotFound();
            _db.ProductionRecords.Remove(record);
            await _db.SaveChangesAsync();
            return Ok();
        }
    }
}
