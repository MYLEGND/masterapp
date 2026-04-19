using ClientApp.Services;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.ClientExperience;
using System.Text.Json;

namespace ClientApp.Controllers.Api;

[ApiController]
[Route("api/finance-state")]
[Authorize]
public class FinanceToolStatesController : ControllerBase
{
    private const string ProtectionSnapshotToolId = "ProtectionSnapshot";
    private static readonly HashSet<string> BusinessOnlyToolIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "BusinessExpenseLens",
        "BusinessSavingsAccelerator"
    };

    private readonly MasterAppDbContext _db;
    private readonly EffectiveClientContextService _clientContext;

    public FinanceToolStatesController(MasterAppDbContext db, EffectiveClientContextService clientContext)
    {
        _db = db;
        _clientContext = clientContext;
    }

    public sealed class SaveFinanceStateRequest
    {
        public Guid ClientProfileId { get; set; }
        public string ClientUserId { get; set; } = "";
        public string ToolId { get; set; } = "";
        public string JsonState { get; set; } = "{}";
    }

    private async Task<EffectiveClientContext?> GetEffectiveClientContextAsync()
    {
        return await _clientContext.ResolveAsync(User, Request.Cookies);
    }

    private static bool IsBusinessOnlyTool(string? toolId)
        => !string.IsNullOrWhiteSpace(toolId) && BusinessOnlyToolIds.Contains(toolId.Trim());

    private static bool IsBusinessClient(string? crmNotes)
    {
        if (string.IsNullOrWhiteSpace(crmNotes))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(crmNotes);
            if (doc.RootElement.TryGetProperty("recordType", out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var value = (prop.GetString() ?? string.Empty).Trim();
                return value.Equals("BusinessClient", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("Business Client", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Treat malformed CRM metadata as a regular client.
        }

        return false;
    }

    private static string NormalizeProtectionSnapshotJson(string? jsonState)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<ProtectionSnapshotState>(jsonState ?? "{}") ?? new ProtectionSnapshotState();

            parsed.PriorityFocusAreas = (parsed.PriorityFocusAreas ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            parsed.ProtectionNeeds = (parsed.ProtectionNeeds ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            parsed.RecentLifeEvents = (parsed.RecentLifeEvents ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            parsed.DependentsCount = Math.Max(0, parsed.DependentsCount);
            parsed.EmergencyFundMonths = Math.Max(0, parsed.EmergencyFundMonths);
            parsed.IncomeProtectionYears = Math.Max(0, parsed.IncomeProtectionYears);

            return JsonSerializer.Serialize(parsed);
        }
        catch
        {
            return "{}";
        }
    }

    [HttpGet("load")]
    public async Task<IActionResult> Load(Guid clientProfileId, string? clientUserId, string toolId)
    {
        var context = await GetEffectiveClientContextAsync();
        if (context == null)
            return Forbid();

        var normalizedClientUserId = (clientUserId ?? "").Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedClientUserId))
        {
            if (!string.Equals(context.ClientUserId, normalizedClientUserId, StringComparison.Ordinal))
                return Forbid();
        }
        else if (context.ClientProfileId != clientProfileId)
            return Forbid();

        var normalizedToolId = (toolId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalizedToolId))
            return BadRequest("ToolId required.");

        if (IsBusinessOnlyTool(normalizedToolId) && !IsBusinessClient(context.Profile.CrmNotes))
            return Forbid();

        var row = await _db.FinanceToolStates
            .AsNoTracking()
            .FirstOrDefaultAsync(x =>
                x.ClientProfileId == context.ClientProfileId &&
                x.ToolId == normalizedToolId);

        return Ok(new
        {
            found = row != null,
            jsonState = row != null && string.Equals(normalizedToolId, ProtectionSnapshotToolId, StringComparison.OrdinalIgnoreCase)
                ? NormalizeProtectionSnapshotJson(row.JsonState)
                : row?.JsonState ?? "{}",
            clientProfileId = context.ClientProfileId
        });
    }

        [HttpPost("save")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save([FromBody] SaveFinanceStateRequest request)
        {
        if (request == null)
            return BadRequest();

        var context = await GetEffectiveClientContextAsync();
        if (context == null)
            return Forbid();

        var normalizedClientUserId = (request.ClientUserId ?? "").Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedClientUserId))
        {
            if (!string.Equals(context.ClientUserId, normalizedClientUserId, StringComparison.Ordinal))
                return Forbid();
        }
        else if (request.ClientProfileId == Guid.Empty || context.ClientProfileId != request.ClientProfileId)
            return Forbid();

        var normalizedToolId = (request.ToolId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalizedToolId))
            return BadRequest("ToolId required.");

        if (IsBusinessOnlyTool(normalizedToolId) && !IsBusinessClient(context.Profile.CrmNotes))
            return Forbid();

        if (context.IsAgentView && string.Equals(normalizedToolId, ProtectionSnapshotToolId, StringComparison.OrdinalIgnoreCase))
            return Forbid();

        var normalizedJsonState = string.Equals(normalizedToolId, ProtectionSnapshotToolId, StringComparison.OrdinalIgnoreCase)
            ? NormalizeProtectionSnapshotJson(request.JsonState)
            : (string.IsNullOrWhiteSpace(request.JsonState) ? "{}" : request.JsonState);

        var row = await _db.FinanceToolStates
            .FirstOrDefaultAsync(x =>
                x.ClientProfileId == context.ClientProfileId &&
                x.ToolId == normalizedToolId);

        if (row == null)
        {
            row = new FinanceToolState
            {
                ClientProfileId = context.ClientProfileId,
                ToolId = normalizedToolId,
                JsonState = normalizedJsonState,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

            _db.FinanceToolStates.Add(row);
        }
        else
        {
            row.JsonState = normalizedJsonState;
            row.UpdatedUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

        [HttpDelete("clear")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Clear(Guid clientProfileId, string? clientUserId, string toolId)
        {
        var context = await GetEffectiveClientContextAsync();
        if (context == null)
            return Forbid();

        var normalizedClientUserId = (clientUserId ?? "").Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedClientUserId))
        {
            if (!string.Equals(context.ClientUserId, normalizedClientUserId, StringComparison.Ordinal))
                return Forbid();
        }
        else if (context.ClientProfileId != clientProfileId)
            return Forbid();

        var normalizedToolId = (toolId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalizedToolId))
            return BadRequest("ToolId required.");

        if (IsBusinessOnlyTool(normalizedToolId) && !IsBusinessClient(context.Profile.CrmNotes))
            return Forbid();

        if (context.IsAgentView && string.Equals(normalizedToolId, ProtectionSnapshotToolId, StringComparison.OrdinalIgnoreCase))
            return Forbid();

        var row = await _db.FinanceToolStates
            .FirstOrDefaultAsync(x =>
                x.ClientProfileId == context.ClientProfileId &&
                x.ToolId == normalizedToolId);

        if (row != null)
        {
            _db.FinanceToolStates.Remove(row);
            await _db.SaveChangesAsync();
        }

        return Ok(new { ok = true });
    }
}
