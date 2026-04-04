
using System;
using System.Linq;
using System.Threading.Tasks;
using ClientApp.Services;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace ClientApp.Controllers
{
    [Authorize]
    [Route("production")]
    public class ProductionController : Controller
    {
        private readonly MasterAppDbContext _db;
        private readonly EffectiveClientContextService _clientContext;

        public ProductionController(MasterAppDbContext db, EffectiveClientContextService clientContext)
        {
            _db = db;
            _clientContext = clientContext;
        }

        private static string Norm(string? value) => IdentityKey.Normalize(value);

        // GET: /production/history/client?clientId=xxx
        [HttpGet("history/client")]
        public async Task<IActionResult> ClientHistory(string clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                return BadRequest("Missing clientId");

            var context = await _clientContext.ResolveAsync(User, Request.Cookies);
            if (context == null) return Forbid();

            var requestedClientId = Norm(clientId);
            if (!IdentityKey.EqualsNormalized(context.ClientUserId, requestedClientId))
                return Forbid();

            var records = await _db.ProductionRecords
                .Where(x => (x.ClientUserId ?? string.Empty).ToLower() == requestedClientId)
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

            var context = await _clientContext.ResolveAsync(User, Request.Cookies);
            if (context == null) return Forbid();

            var requestedClientId = Norm(clientId);
            if (!IdentityKey.EqualsNormalized(context.ClientUserId, requestedClientId))
                return Forbid();

            var actorId = Norm(User.GetStableUserId());
            if (string.IsNullOrWhiteSpace(actorId))
            {
                actorId = context.ClientUserId;
            }

            var record = new ProductionRecord
            {
                ClientUserId = requestedClientId,
                Amount = amount,
                PersonalAmount = personalAmount ?? 0,
                Status = (ProductionStatus)status,
                Side = ProductionSide.Client,
                Notes = notes,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                AgentUserId = actorId
            };
            _db.ProductionRecords.Add(record);
            await _db.SaveChangesAsync();
            return Ok();
        }

        // POST: /production/update
        [HttpPost("update")]
        public async Task<IActionResult> Update([FromForm] Guid id, [FromForm] decimal amount, [FromForm] decimal? personalAmount, [FromForm] int status, [FromForm] string? notes)
        {
            var context = await _clientContext.ResolveAsync(User, Request.Cookies);
            if (context == null) return Forbid();

            var record = await _db.ProductionRecords.FindAsync(id);
            if (record == null) return NotFound();

            if (!IdentityKey.EqualsNormalized(record.ClientUserId, context.ClientUserId))
                return NotFound();

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
            var context = await _clientContext.ResolveAsync(User, Request.Cookies);
            if (context == null) return Forbid();

            var record = await _db.ProductionRecords.FindAsync(id);
            if (record == null) return NotFound();

            if (!IdentityKey.EqualsNormalized(record.ClientUserId, context.ClientUserId))
                return NotFound();

            _db.ProductionRecords.Remove(record);
            await _db.SaveChangesAsync();
            return Ok();
        }
    }
}
