using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using AgentPortal.Models;
using AgentPortal.Services;
using System.Text.Json.Nodes;

namespace AgentPortal.Controllers.API
{
    [Authorize]
    [ApiController]
    [Route("api/finance-state")]
    public class FinanceToolStatesController : ControllerBase
    {
        private static readonly HashSet<string> BusinessOnlyToolIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "BusinessExpenseLens",
            "BusinessSavingsAccelerator"
        };

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

        private static bool IsBusinessOnlyTool(string? toolId)
            => !string.IsNullOrWhiteSpace(toolId) && BusinessOnlyToolIds.Contains(toolId.Trim());

        private static bool IsAgentWorkspaceRequest(Guid clientProfileId, string? clientUserId)
            => clientProfileId == Guid.Empty && string.IsNullOrWhiteSpace(clientUserId);

        private string GetAgentStateOwnerKey()
        {
            var primary = Norm(_agentContext.EffectiveAgentOid);
            if (!string.IsNullOrWhiteSpace(primary))
                return primary;

            return GetCurrentUserKeys().FirstOrDefault() ?? string.Empty;
        }

        private async Task<bool> IsBusinessClientProfileAsync(Guid clientProfileId)
        {
            var crmNotes = await _db.ClientProfiles
                .AsNoTracking()
                .Where(x => x.Id == clientProfileId)
                .Select(x => x.CrmNotes)
                .FirstOrDefaultAsync();

            var meta = ClientCrmMetaSerializer.Deserialize(crmNotes);
            var recordType = ClientCrmMetaSerializer.NormalizeRecordType(meta.RecordType, defaultToLead: false);
            return string.Equals(recordType, "BusinessClient", StringComparison.OrdinalIgnoreCase);
        }

        public class SaveFinanceStateRequest
        {
            public Guid ClientProfileId { get; set; }
            public string ClientUserId { get; set; } = "";
            public string ToolId { get; set; } = "";
            public string JsonState { get; set; } = "{}";
        }

        private string? ValidateDistributionCanonical(JsonObject canonical)
        {
            double GetD(string name, double def = 0)
            {
                if (canonical[name] is JsonValue v && v.TryGetValue<double>(out var d)) return d;
                return def;
            }
            bool InRange(double v, double min, double max) => v >= min && v <= max;
            var retireAge = GetD("retireAge");
            var endAge = GetD("endAge");
            if (retireAge <= 0) return "retireAge must be > 0";
            if (endAge <= retireAge) return "endAge must be greater than retireAge";
            if (GetD("retirementBase") < 0) return "retirementBase must be >= 0";
            if (GetD("desiredIncome") < 0) return "desiredIncome must be >= 0";
            if (GetD("guaranteedIncome") < 0) return "guaranteedIncome must be >= 0";
            if (GetD("emergencyReserve") < 0) return "emergencyReserve must be >= 0";
            double inv = GetD("invAllocPct"), li = GetD("liAllocPct"), ann = GetD("annAllocPct");
            if (!InRange(inv,0,100) || !InRange(li,0,100) || !InRange(ann,0,100))
                return "Allocation percents must be between 0 and 100";
            if (Math.Abs(inv + li + ann - 100) > 0.001)
                return "Allocation percents must total 100%";
            double rtnMin=-50, rtnMax=20;
            if (!InRange(GetD("invReturnPct"), rtnMin, rtnMax)) return "invReturnPct out of range";
            if (!InRange(GetD("liReturnPct"), rtnMin, rtnMax)) return "liReturnPct out of range";
            if (!InRange(GetD("annReturnPct"), rtnMin, rtnMax)) return "annReturnPct out of range";
            double taxMin=0, taxMax=100;
            if (!InRange(GetD("invTaxPct"), taxMin, taxMax)) return "invTaxPct out of range";
            if (!InRange(GetD("liTaxPct"), taxMin, taxMax)) return "liTaxPct out of range";
            if (!InRange(GetD("annTaxPct"), taxMin, taxMax)) return "annTaxPct out of range";
            return null;
        }

        [HttpGet("load")]
        public async Task<IActionResult> Load(Guid clientProfileId, string? clientUserId, string toolId)
        {
            if (string.IsNullOrWhiteSpace(toolId))
                return BadRequest();

            var normalizedToolId = toolId.Trim();

            if (IsAgentWorkspaceRequest(clientProfileId, clientUserId))
            {
                if (IsBusinessOnlyTool(normalizedToolId))
                    return Forbid();

                var agentUserId = GetAgentStateOwnerKey();
                if (string.IsNullOrWhiteSpace(agentUserId))
                    return Forbid();

                var agentRow = await _db.AgentFinanceToolStates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x =>
                        x.AgentUserId == agentUserId &&
                        x.ToolId == normalizedToolId);

                return Ok(new
                {
                    found = agentRow != null,
                    jsonState = agentRow?.JsonState ?? "{}",
                    clientProfileId = Guid.Empty
                });
            }

            var resolvedClientProfileId = await ResolveAccessibleClientProfileIdAsync(clientProfileId, clientUserId);
            if (resolvedClientProfileId == null)
                return Forbid();

            if (IsBusinessOnlyTool(normalizedToolId) && !await IsBusinessClientProfileAsync(resolvedClientProfileId.Value))
                return Forbid();

            var row = await _db.FinanceToolStates
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.ClientProfileId == resolvedClientProfileId.Value &&
                    x.ToolId == normalizedToolId);

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

            var normalizedToolId = req.ToolId.Trim();

            try
            {
                var root = JsonNode.Parse(req.JsonState) as JsonObject ?? new JsonObject();
                if (string.Equals(normalizedToolId, "DistributionPlanner", StringComparison.OrdinalIgnoreCase))
                {
                    var canonical = root["canonicalInput"] as JsonObject;
                    if (canonical != null)
                    {
                        var err = ValidateDistributionCanonical(canonical);
                        if (!string.IsNullOrWhiteSpace(err))
                            return BadRequest(err);
                    }
                }
            }
            catch
            {
                return BadRequest("Invalid JSON state.");
            }

            if (IsAgentWorkspaceRequest(req.ClientProfileId, req.ClientUserId))
            {
                if (IsBusinessOnlyTool(normalizedToolId))
                    return Forbid();

                var agentUserId = GetAgentStateOwnerKey();
                if (string.IsNullOrWhiteSpace(agentUserId))
                    return Forbid();

                var agentRow = await _db.AgentFinanceToolStates
                    .FirstOrDefaultAsync(x =>
                        x.AgentUserId == agentUserId &&
                        x.ToolId == normalizedToolId);

                if (agentRow == null)
                {
                    agentRow = new AgentFinanceToolState
                    {
                        AgentUserId = agentUserId,
                        ToolId = normalizedToolId,
                        JsonState = string.IsNullOrWhiteSpace(req.JsonState) ? "{}" : req.JsonState,
                        CreatedUtc = DateTime.UtcNow,
                        UpdatedUtc = DateTime.UtcNow
                    };

                    _db.AgentFinanceToolStates.Add(agentRow);
                }
                else
                {
                    agentRow.JsonState = string.IsNullOrWhiteSpace(req.JsonState) ? "{}" : req.JsonState;
                    agentRow.UpdatedUtc = DateTime.UtcNow;
                }

                await _db.SaveChangesAsync();
                return Ok(new { ok = true });
            }

            var resolvedClientProfileId = await ResolveAccessibleClientProfileIdAsync(req.ClientProfileId, req.ClientUserId);
            if (resolvedClientProfileId == null)
                return Forbid();

            if (IsBusinessOnlyTool(normalizedToolId) && !await IsBusinessClientProfileAsync(resolvedClientProfileId.Value))
                return Forbid();

            var row = await _db.FinanceToolStates
                .FirstOrDefaultAsync(x =>
                    x.ClientProfileId == resolvedClientProfileId.Value &&
                    x.ToolId == normalizedToolId);

            if (row == null)
            {
                row = new FinanceToolState
                {
                    ClientProfileId = resolvedClientProfileId.Value,
                    ToolId = normalizedToolId,
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

            var normalizedToolId = toolId.Trim();

            if (IsAgentWorkspaceRequest(clientProfileId, clientUserId))
            {
                if (IsBusinessOnlyTool(normalizedToolId))
                    return Forbid();

                var agentUserId = GetAgentStateOwnerKey();
                if (string.IsNullOrWhiteSpace(agentUserId))
                    return Forbid();

                var agentRow = await _db.AgentFinanceToolStates
                    .FirstOrDefaultAsync(x =>
                        x.AgentUserId == agentUserId &&
                        x.ToolId == normalizedToolId);

                if (agentRow != null)
                {
                    _db.AgentFinanceToolStates.Remove(agentRow);
                    await _db.SaveChangesAsync();
                }

                return Ok(new { ok = true });
            }

            var resolvedClientProfileId = await ResolveAccessibleClientProfileIdAsync(clientProfileId, clientUserId);
            if (resolvedClientProfileId == null)
                return Forbid();

            if (IsBusinessOnlyTool(normalizedToolId) && !await IsBusinessClientProfileAsync(resolvedClientProfileId.Value))
                return Forbid();

            var row = await _db.FinanceToolStates
                .FirstOrDefaultAsync(x =>
                    x.ClientProfileId == resolvedClientProfileId.Value &&
                    x.ToolId == normalizedToolId);

            if (row != null)
            {
                _db.FinanceToolStates.Remove(row);
                await _db.SaveChangesAsync();
            }

            return Ok(new { ok = true });
        }
    }
}
