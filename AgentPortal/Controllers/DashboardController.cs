using Microsoft.AspNetCore.Authorization;
using AgentPortal.Filters;
using Microsoft.AspNetCore.Mvc;
using AgentPortal.Services;
using AgentPortal.Models;
using Shared.Auth;
using Domain.Enums;
using Infrastructure.Data;
using AgentPortal.Services.Analytics;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentPortal.Controllers;

[Authorize]
[AssistantBlock]
public class DashboardController : Controller
{
    private const string CarrierSettingsToolId = "DashboardCarrierSettings";
    private static readonly JsonSerializerOptions CarrierSettingsJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IExecutionEngine _execution;
    private readonly IBlockerService _blockers;
    private readonly MasterAppDbContext _db;
    private readonly EffectiveAgentContext _agentContext;
    private readonly DerivedAnalyticsService _derivedAnalytics;
    private readonly AppFeatureFlags _featureFlags;

    public DashboardController(IExecutionEngine execution, IBlockerService blockers, MasterAppDbContext db, EffectiveAgentContext agentContext, DerivedAnalyticsService derivedAnalytics, Microsoft.Extensions.Options.IOptions<AppFeatureFlags> featureFlags)
    {
        _execution = execution;
        _blockers = blockers;
        _db = db;
        _agentContext = agentContext;
        _derivedAnalytics = derivedAnalytics;
        _featureFlags = featureFlags.Value;
    }

    [HttpGet]
    public async Task<IActionResult> Counts()
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        var today = await _execution.GetTodayAsync(ownerId);
        var overdue = await _execution.GetOverdueAsync(ownerId);
        var blockers = await _blockers.GetOpenByOwnerAsync(ownerId);
        return Json(new { today = today.Count, overdue = overdue.Count, blockers = blockers.Count });
    }

    [HttpGet]
    public async Task<IActionResult> CarrierSettings()
    {
        var ownerId = NormalizeOwnerKey(CurrentUserId());
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();

        var row = await _db.AgentFinanceToolStates
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AgentUserId == ownerId && x.ToolId == CarrierSettingsToolId);

        if (row == null || string.IsNullOrWhiteSpace(row.JsonState))
            return Json(new DashboardCarrierSettingsDto());

        try
        {
            var dto = JsonSerializer.Deserialize<DashboardCarrierSettingsDto>(row.JsonState, CarrierSettingsJsonOptions)
                ?? new DashboardCarrierSettingsDto();
            dto.SavedUtc = row.UpdatedUtc;
            return Json(dto);
        }
        catch (JsonException)
        {
            return Json(new DashboardCarrierSettingsDto());
        }
    }

    [HttpPost]
    public async Task<IActionResult> SaveCarrierSettings([FromBody] DashboardCarrierSettingsDto request)
    {
        var ownerId = NormalizeOwnerKey(CurrentUserId());
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();

        var normalized = NormalizeCarrierSettings(request);
        var jsonState = JsonSerializer.Serialize(normalized, CarrierSettingsJsonOptions);

        var row = await _db.AgentFinanceToolStates
            .FirstOrDefaultAsync(x => x.AgentUserId == ownerId && x.ToolId == CarrierSettingsToolId);

        if (row == null)
        {
            row = new Domain.Entities.AgentFinanceToolState
            {
                AgentUserId = ownerId,
                ToolId = CarrierSettingsToolId,
                JsonState = jsonState,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            };
            _db.AgentFinanceToolStates.Add(row);
        }
        else
        {
            row.JsonState = jsonState;
            row.UpdatedUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        return Json(new { success = true, savedUtc = row.UpdatedUtc });
    }

    private string CurrentUserId()
    {
        var effective = _agentContext.EffectiveAgentOid;
        if (!string.IsNullOrWhiteSpace(effective))
            return effective.Trim().ToLowerInvariant();
        return User.GetStableUserId();
    }

    private static string NormalizeOwnerKey(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string BuildCarrierSettingsStorageScope(string ownerId)
    {
        var normalized = NormalizeOwnerKey(ownerId);
        if (string.IsNullOrWhiteSpace(normalized))
            return "dashboard";

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    // GET: /Dashboard  (and /Dashboard/Index)
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        ViewData["CarrierSettingsStorageScope"] = BuildCarrierSettingsStorageScope(ownerId);

        var vm = new DashboardExecutionViewModel
        {
            Today = await _execution.GetTodayAsync(ownerId),
            Overdue = await _execution.GetOverdueAsync(ownerId),
            Blockers = await _blockers.GetOpenByOwnerAsync(ownerId)
        };

        if (_featureFlags.DerivedInsightsEnabled)
        {
            vm.DerivedAnalytics = await _derivedAnalytics.GetAsync(ownerId);
        }

        return View(vm); // renders Views/Dashboard/Index.cshtml
    }

    [HttpGet]
    public async Task<IActionResult> Today()
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        var items = await _execution.GetTodayAsync(ownerId);
        ViewData["RelatedEntityNames"] = await ResolveRelatedEntityNamesAsync(items.Select(x => (x.RelatedEntityType, x.RelatedEntityId)));
        ViewData["ShowActionControls"] = true;
        return PartialView("~/Views/Shared/_ActionList.cshtml", items);
    }

    [HttpGet]
    public async Task<IActionResult> Overdue()
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        var items = await _execution.GetOverdueAsync(ownerId);
        ViewData["RelatedEntityNames"] = await ResolveRelatedEntityNamesAsync(items.Select(x => (x.RelatedEntityType, x.RelatedEntityId)));
        ViewData["ShowActionControls"] = true;
        return PartialView("~/Views/Shared/_ActionList.cshtml", items);
    }

    [HttpGet]
    public async Task<IActionResult> Blockers()
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        var items = await _blockers.GetOpenByOwnerAsync(ownerId);
        ViewData["RelatedEntityNames"] = await ResolveRelatedEntityNamesAsync(items.Select(x => (x.RelatedEntityType, x.RelatedEntityId)));
        return PartialView("~/Views/Shared/_BlockerList.cshtml", items);
    }

    public record CompleteActionRequest(Guid Id);

    [HttpPost]
    [IgnoreAntiforgeryToken] // quick-view actions use authenticated AJAX and can outlive stale anti-forgery tokens
    public async Task<IActionResult> CompleteAction([FromBody] CompleteActionRequest request)
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        if (request == null || request.Id == Guid.Empty) return BadRequest();

        var updated = await _execution.CompleteActionAsync(request.Id, ownerId);
        if (updated == null) return NotFound();

        return Ok(new { success = true });
    }

    // Key format used by the dashboard partials: "{typeInt}:{relatedId-lower}".
    // This lets us enrich action/blocker rows with the originating Lead/Client card name.
    private async Task<Dictionary<string, string>> ResolveRelatedEntityNamesAsync(IEnumerable<(RelatedEntityType Type, string Id)> refs)
    {
        static string BuildKey(RelatedEntityType type, string id)
            => $"{(int)type}:{id.Trim().ToLowerInvariant()}";

        var normalized = refs
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .Select(x => (x.Type, Id: x.Id.Trim()))
            .Distinct()
            .ToList();

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (normalized.Count == 0) return result;

        var leadIdsLower = normalized
            .Where(x => x.Type == RelatedEntityType.Lead)
            .Select(x => x.Id.ToLowerInvariant())
            .Distinct()
            .ToList();
        if (leadIdsLower.Count > 0)
        {
            var leads = await _db.WorkstationLeadProfiles.AsNoTracking()
                .Where(l => l.LeadId != null && leadIdsLower.Contains(l.LeadId.ToLower()))
                .Select(l => new { l.LeadId, l.FirstName, l.LastName, l.Email })
                .ToListAsync();

            foreach (var lead in leads)
            {
                var name = string.Join(" ", new[] { lead.FirstName, lead.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                if (string.IsNullOrWhiteSpace(name)) name = lead.Email?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;
                result[BuildKey(RelatedEntityType.Lead, lead.LeadId)] = name;
            }
        }

        var clientIdsLower = normalized
            .Where(x => x.Type == RelatedEntityType.Client)
            .Select(x => x.Id.ToLowerInvariant())
            .Distinct()
            .ToList();
        if (clientIdsLower.Count > 0)
        {
            var clients = await _db.ClientProfiles.AsNoTracking()
                .Where(c => c.ClientUserId != null && clientIdsLower.Contains(c.ClientUserId.ToLower()))
                .Select(c => new { c.ClientUserId, c.FirstName, c.LastName, c.Email })
                .ToListAsync();

            foreach (var client in clients)
            {
                var name = string.Join(" ", new[] { client.FirstName, client.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                if (string.IsNullOrWhiteSpace(name)) name = client.Email?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;
                result[BuildKey(RelatedEntityType.Client, client.ClientUserId)] = name;
            }
        }

        return result;
    }

    private static DashboardCarrierSettingsDto NormalizeCarrierSettings(DashboardCarrierSettingsDto? request)
    {
        var result = new DashboardCarrierSettingsDto();
        if (request?.Items == null || request.Items.Count == 0)
            return result;

        foreach (var item in request.Items)
        {
            var categoryName = Limit(item.CategoryName, 120);
            var categoryKey = Limit(ChooseKey(item.CategoryKey, categoryName), 160);
            var carrierName = Limit(item.CarrierName, 160);
            var carrierKey = Limit(ChooseKey(item.CarrierKey, carrierName), 160);
            var entryKey = Limit(ChooseKey(item.EntryKey, $"{categoryKey}::{carrierKey}"), 320);

            var normalizedLines = (item.CompensationLines ?? new List<DashboardCarrierCompensationLineDto>())
                .Select(line => new DashboardCarrierCompensationLineDto
                {
                    ProductLine = Limit(line.ProductLine, 160),
                    CommissionPercent = Limit(line.CommissionPercent, 60),
                    EligibilityNotes = Limit(line.EligibilityNotes, 240),
                })
                .Where(line =>
                    !string.IsNullOrWhiteSpace(line.ProductLine) ||
                    !string.IsNullOrWhiteSpace(line.CommissionPercent) ||
                    !string.IsNullOrWhiteSpace(line.EligibilityNotes))
                .Take(20)
                .ToList();

            var normalizedItem = new DashboardCarrierSettingItemDto
            {
                EntryKey = entryKey,
                CategoryKey = categoryKey,
                CategoryName = categoryName,
                CarrierKey = carrierKey,
                CarrierName = carrierName,
                AgentNumber = Limit(item.AgentNumber, 120),
                EntityNumber = Limit(item.EntityNumber, 120),
                Notes = Limit(item.Notes, 2000),
                CompensationLines = normalizedLines,
            };

            var hasMeaningfulData =
                !string.IsNullOrWhiteSpace(normalizedItem.AgentNumber) ||
                !string.IsNullOrWhiteSpace(normalizedItem.EntityNumber) ||
                !string.IsNullOrWhiteSpace(normalizedItem.Notes) ||
                normalizedItem.CompensationLines.Count > 0;

            if (!hasMeaningfulData || string.IsNullOrWhiteSpace(normalizedItem.EntryKey))
                continue;

            result.Items.Add(normalizedItem);
        }

        result.Items = result.Items
            .GroupBy(x => x.EntryKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .OrderBy(x => x.CategoryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.CarrierName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return result;
    }

    private static string Limit(string? value, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length <= maxLength) return trimmed;
        return trimmed[..maxLength].Trim();
    }

    private static string ChooseKey(string? preferred, string fallback)
    {
        var candidate = Limit(preferred, 320);
        if (!string.IsNullOrWhiteSpace(candidate))
            return candidate;
        return Slugify(fallback);
    }

    private static string Slugify(string? value)
    {
        var input = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var sb = new StringBuilder(input.Length);
        var pendingDash = false;

        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingDash && sb.Length > 0)
                    sb.Append('-');
                sb.Append(ch);
                pendingDash = false;
            }
            else if (sb.Length > 0)
            {
                pendingDash = true;
            }
        }

        return sb.ToString();
    }
}
