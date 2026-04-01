using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using AgentPortal.Services;

namespace AgentPortal.Controllers.API
{
    [Authorize]
    [ApiController]
    [Route("api/finance-state")]
    public class FinanceToolStatesController : ControllerBase
    {
        private readonly MasterAppDbContext _db;
        private readonly EffectiveAgentContext _agentContext;

        public FinanceToolStatesController(MasterAppDbContext db, EffectiveAgentContext agentContext)
        {
            _db = db;
            _agentContext = agentContext;
        }

        private static string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant();

        private List<string> GetCurrentUserKeys()
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? v)
            {
                var n = Norm(v);
                if (!string.IsNullOrWhiteSpace(n)) keys.Add(n);
            }

            Add(_agentContext.EffectiveAgentOid);
            Add(User.FindFirstValue("oid"));
            Add(User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier"));
            Add(User.FindFirstValue(ClaimTypes.NameIdentifier));
            Add(User.FindFirstValue(ClaimTypes.Upn));
            Add(User.FindFirstValue(ClaimTypes.Email));
            Add(User.FindFirstValue("preferred_username"));
            Add(User.Identity?.Name);

            return keys.ToList();
        }

        private async Task<bool> UserCanAccessClientProfileAsync(Guid clientProfileId)
        {
            var client = await _db.ClientProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == clientProfileId);

            if (client == null) return false;

            var normalizedUserKeys = GetCurrentUserKeys(); // already lowercased
            var normalizedClientUserId = Norm(client.ClientUserId);

            if (normalizedUserKeys.Contains(normalizedClientUserId))
                return true;

            // Avoid custom helpers inside the EF expression so it can translate.
            var clientUserIdLower = normalizedClientUserId;
            return await _db.AgentClients.AnyAsync(x =>
                normalizedUserKeys.Contains((x.AgentUserId ?? string.Empty).ToLower()) &&
                (x.ClientUserId ?? string.Empty).ToLower() == clientUserIdLower);
        }

        private async Task<Guid?> ResolveAccessibleClientProfileIdAsync(Guid clientProfileId, string? clientUserId)
        {
            var normalizedClientUserId = Norm(clientUserId);

            if (!string.IsNullOrWhiteSpace(normalizedClientUserId))
            {
                // Avoid client-side helper in the query: compare using ToLower which EF can translate.
                var profile = await _db.ClientProfiles
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => (x.ClientUserId ?? "").ToLower() == normalizedClientUserId);

                if (profile == null)
                    return null;

                if (!await UserCanAccessClientProfileAsync(profile.Id))
                    return null;

                return profile.Id;
            }

            if (clientProfileId == Guid.Empty)
                return null;

            return await UserCanAccessClientProfileAsync(clientProfileId)
                ? clientProfileId
                : null;
        }

        public class SaveFinanceStateRequest
        {
            public Guid ClientProfileId { get; set; }
            public string ClientUserId { get; set; } = "";
            public string ToolId { get; set; } = "";
            public string JsonState { get; set; } = "{}";
        }

        [HttpGet("load")]
        public async Task<IActionResult> Load(Guid clientProfileId, string? clientUserId, string toolId)
        {
            if (string.IsNullOrWhiteSpace(toolId))
                return BadRequest();

            var resolvedClientProfileId = await ResolveAccessibleClientProfileIdAsync(clientProfileId, clientUserId);
            if (resolvedClientProfileId == null)
                return Forbid();

            var row = await _db.FinanceToolStates
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.ClientProfileId == resolvedClientProfileId.Value &&
                    x.ToolId == toolId);

            return Ok(new
            {
                found = row != null,
                jsonState = row?.JsonState ?? "{}",
                clientProfileId = resolvedClientProfileId.Value
            });
        }

        [HttpPost("save")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save([FromBody] SaveFinanceStateRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ToolId))
                return BadRequest();

            var resolvedClientProfileId = await ResolveAccessibleClientProfileIdAsync(req.ClientProfileId, req.ClientUserId);
            if (resolvedClientProfileId == null)
                return Forbid();

            var row = await _db.FinanceToolStates
                .FirstOrDefaultAsync(x =>
                    x.ClientProfileId == resolvedClientProfileId.Value &&
                    x.ToolId == req.ToolId);

            if (row == null)
            {
                row = new FinanceToolState
                {
                    ClientProfileId = resolvedClientProfileId.Value,
                    ToolId = req.ToolId.Trim(),
                    JsonState = string.IsNullOrWhiteSpace(req.JsonState) ? "{}" : req.JsonState,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                };

                _db.FinanceToolStates.Add(row);
            }
            else
            {
                row.JsonState = string.IsNullOrWhiteSpace(req.JsonState) ? "{}" : req.JsonState;
                row.UpdatedUtc = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync();
            return Ok(new { ok = true });
        }

        [HttpDelete("clear")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Clear(Guid clientProfileId, string? clientUserId, string toolId)
        {
            if (string.IsNullOrWhiteSpace(toolId))
                return BadRequest();

            var resolvedClientProfileId = await ResolveAccessibleClientProfileIdAsync(clientProfileId, clientUserId);
            if (resolvedClientProfileId == null)
                return Forbid();

            var row = await _db.FinanceToolStates
                .FirstOrDefaultAsync(x =>
                    x.ClientProfileId == resolvedClientProfileId.Value &&
                    x.ToolId == toolId);

            if (row != null)
            {
                _db.FinanceToolStates.Remove(row);
                await _db.SaveChangesAsync();
            }

            return Ok(new { ok = true });
        }
    }
}
