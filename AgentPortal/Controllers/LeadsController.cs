using System.Globalization;
using System.IO;
using System.Linq;
using AgentPortal.Helpers;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;
using Microsoft.Extensions.Logging;

namespace AgentPortal.Controllers;

[Authorize]
public class LeadsController : Controller
{
    private readonly MasterAppDbContext _db;
    private readonly IAgentTimeZoneResolver _agentTimeZoneResolver;
    private readonly ProductionService _production;
    private readonly EffectiveAgentContext _agentContext;
    private readonly IExecutionEngine _execution;
    private readonly ICommitmentService _commitments;
    private readonly ILogger<LeadsController> _logger;
    private readonly AgentPortal.Models.AppFeatureFlags _featureFlags;
    private readonly AgentPortal.Services.ImportValidation.LeadImportValidator _leadImportValidator;
    private const string CommitmentsUnavailableMessage = "Commitments are not live yet in this environment. Apply the latest migrations to enable them.";
    private static readonly string[] ProductBuckets =
    {
        "MortgageProtection",
        "FinalExpense",
        "LifeInsurance",
        "Medicare",
        "DisabilityInsurance"
    };

    private static readonly string[] PipelineStages =
    {
        "MortgageProtection",
        "LifeInsurance",
        "FinalExpense",
        "Medicare",
        "DisabilityInsurance",
        "Contacted",
        "Booked",
        "FollowUp",
        "NeedsDocs",
        "PolicyPlaced",
        "NotInterested",
        "Nurture",
        "NoAnswer",
        "Lost",
        "AIReception"
    };

    private static readonly IReadOnlyDictionary<string, string> BucketAliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["mortgageprotectionleads"] = "MortgageProtection",
        ["mortgageprotectionrebuttals"] = "MortgageProtection",
        ["mortgageprotection"] = "MortgageProtection",
        ["lifeinsuranceleads"] = "LifeInsurance",
        ["lifeinsurancerebuttals"] = "LifeInsurance",
        ["lifeinsurance"] = "LifeInsurance",
        ["finalexpenseleads"] = "FinalExpense",
        ["finalexpenserebuttals"] = "FinalExpense",
        ["finalexpense"] = "FinalExpense",
        ["medicareleads"] = "Medicare",
        ["medicarerebuttals"] = "Medicare",
        ["medicare"] = "Medicare",
        ["disabilityinsuranceleads"] = "DisabilityInsurance",
        ["disabilityinsurancerebuttals"] = "DisabilityInsurance",
        ["disabilityinsurance"] = "DisabilityInsurance"
    };

    private static readonly IReadOnlyDictionary<string, string> PipelineStageMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["mortgageprotection"] = "MortgageProtection",
        ["lifeinsurance"] = "LifeInsurance",
        ["finalexpense"] = "FinalExpense",
        ["medicare"] = "Medicare",
        ["disabilityinsurance"] = "DisabilityInsurance",
        ["contacted"] = "Contacted",
        ["booked"] = "Booked",
        ["followup"] = "FollowUp",
        ["needsdocs"] = "NeedsDocs",
        ["policyplaced"] = "PolicyPlaced",
        ["notinterested"] = "NotInterested",
        ["nurture"] = "Nurture",
        ["noanswer"] = "NoAnswer",
        ["lost"] = "Lost",
        ["aireception"] = "AIReception",
        ["aireceptionist"] = "AIReception",
        ["leads"] = "MortgageProtection"
    };

    private static readonly IReadOnlyDictionary<string, string> OutcomeStageMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Contacted"] = "Contacted",
        ["Booked"] = "Booked",
        ["FollowUp"] = "FollowUp",
        ["NeedsDocs"] = "NeedsDocs",
        ["PolicyPlaced"] = "PolicyPlaced",
        ["NotInterested"] = "NotInterested",
        ["Nurture"] = "Nurture",
        ["NoAnswer"] = "NoAnswer",
        ["Lost"] = "Lost",
        ["AIReception"] = "AIReception"
    };

    private static string? ResolveOriginalLeadType(string? originalLeadType, string? currentBucket, string? fallbackBucket = null)
        => NormalizeBucket(originalLeadType)
           ?? NormalizeBucket(currentBucket)
           ?? fallbackBucket;

    private static void PreserveOriginalLeadType(WorkstationLeadProfile lead)
    {
        var resolved = ResolveOriginalLeadType(lead.OriginalLeadType, lead.Bucket);
        if (!string.IsNullOrWhiteSpace(resolved))
            lead.OriginalLeadType = resolved;
    }

    private static string? NormalizeBucket(string? bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket)) return null;
        var key = bucket.Trim();
        key = key.Replace(" ", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("-", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("_", "", StringComparison.OrdinalIgnoreCase);
        foreach (var b in ProductBuckets)
        {
            if (key.Equals(b, StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"{b}Leads", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"{b}Rebuttals", StringComparison.OrdinalIgnoreCase))
                return b;
        }
        // handle spaced display labels
        return BucketAliasMap.TryGetValue(key.ToLowerInvariant(), out var val) ? val : null;
    }

    private static string? NormalizePipelineStage(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return null;
        var key = stage.Trim()
            .Replace(" ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("-", "", StringComparison.OrdinalIgnoreCase)
            .Replace("_", "", StringComparison.OrdinalIgnoreCase);

        if (PipelineStageMap.TryGetValue(key, out var val))
            return val;

        return PipelineStages.FirstOrDefault(x => x.Equals(stage.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeLeadMetaJson(string? raw)
        => !string.IsNullOrWhiteSpace(raw) && raw.TrimStart().StartsWith("{", StringComparison.Ordinal);

    private static ClientCrmMeta ReadLeadMeta(WorkstationLeadProfile lead)
    {
        var meta = ClientCrmMetaSerializer.Deserialize(lead.CrmNotes);
        if (!LooksLikeLeadMetaJson(lead.CrmNotes))
        {
            var legacyNote = (lead.CrmNotes ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(legacyNote) && string.IsNullOrWhiteSpace(meta.AgentNotes))
                meta.AgentNotes = legacyNote;
        }

        if (string.IsNullOrWhiteSpace(meta.CrmPriority))
            meta.CrmPriority = "Normal";
        if (meta.StageEnteredUtc == default)
            meta.StageEnteredUtc = lead.CreatedUtc;
        if (string.IsNullOrWhiteSpace(meta.CrmTags))
            meta.CrmTags = lead.Bucket;
        if (meta.MeetingDurationMinutes <= 0)
            meta.MeetingDurationMinutes = 30;
        if (string.IsNullOrWhiteSpace(meta.MeetingTime))
            meta.MeetingTime = "09:00";

        return meta;
    }

    private static int CompletedLeadDocCount(ClientCrmDocChecklist? checklist)
    {
        if (checklist == null) return 0;
        var count = 0;
        if (checklist.IdReceived) count++;
        if (checklist.AppSent) count++;
        if (checklist.AppSigned) count++;
        if (checklist.PolicyDelivered) count++;
        if (checklist.ReviewBooked) count++;
        return count;
    }

    private static string LeadWatchersCsv(ClientCrmMeta meta)
        => string.Join(", ", (meta.Collaboration?.Watchers ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    public sealed class ReorderRequest
    {
        public string? Bucket { get; set; }
        public List<string>? Ids { get; set; }
    }

    public LeadsController(MasterAppDbContext db, IAgentTimeZoneResolver agentTimeZoneResolver, ProductionService production, EffectiveAgentContext agentContext, IExecutionEngine execution, ICommitmentService commitments, ILogger<LeadsController> logger, Microsoft.Extensions.Options.IOptions<AgentPortal.Models.AppFeatureFlags> featureFlags, AgentPortal.Services.ImportValidation.LeadImportValidator leadImportValidator)
    {
        _db = db;
        _agentTimeZoneResolver = agentTimeZoneResolver;
        _production = production;
        _agentContext = agentContext;
        _execution = execution;
        _commitments = commitments;
        _logger = logger;
        _featureFlags = featureFlags.Value;
        _leadImportValidator = leadImportValidator;
    }

    // Centralized canonical selector to avoid drift across endpoints.
    private async Task<WorkstationLeadProfile?> LoadCanonicalLeadAsync(string agentId, string leadId, string context)
    {
        var rows = await _db.WorkstationLeadProfiles
            .Where(x => x.AgentUserId == agentId && x.LeadId == leadId)
            .ToListAsync();
        return LeadCanonicalizer.SelectCanonical(rows, _logger, context);
    }

    private static string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant();

    private string GetAgentIdOrChallenge()
    {
        var eff = _agentContext.EffectiveAgentOid;
        if (!string.IsNullOrWhiteSpace(eff))
            return Norm(eff);

        var oid = Norm(User.GetStableUserId());
        if (string.IsNullOrWhiteSpace(oid))
            throw new InvalidOperationException("Missing agent id.");
        return oid;
    }

    private Task<bool> AgentOwnsLeadAsync(string agentId, string leadId, CancellationToken ct = default)
        => _db.AgentOwnsLeadAsync(agentId, leadId, ct);

    private bool IsAdminUser()
    {
        var oid = (User?.FindFirst("oid")?.Value ?? "").Trim().ToLowerInvariant();
        var upn = (User?.Identity?.Name ?? "").Trim().ToLowerInvariant();
        bool matches(string rawList, string value)
        {
            if (string.IsNullOrWhiteSpace(rawList) || string.IsNullOrWhiteSpace(value)) return false;
            return rawList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Any(x => x.Equals(value, StringComparison.OrdinalIgnoreCase));
        }
        return matches(Environment.GetEnvironmentVariable("LEGEND_ADMIN_OIDS") ?? "", oid)
            || matches(Environment.GetEnvironmentVariable("LEGEND_ADMIN_UPNS") ?? "", upn);
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        try
        {
            ViewData["Title"] = "Leads CRM";

            string agentId;
            try { agentId = GetAgentIdOrChallenge(); }
            catch { return Challenge(); }

            var nowUtc = DateTime.UtcNow;
            var dialTimeZone = _agentTimeZoneResolver.Resolve(HttpContext);

            var leadsRaw = await _db.WorkstationLeadProfiles
                .AsNoTracking()
                .Where(x => x.AgentUserId == agentId
                    && (x.Bucket == null || x.Bucket.ToLower() != "notinterested")
                    && (x.CrmStage == null || x.CrmStage.ToLower() != "notinterested"))
                .OrderByDescending(x => x.CrmOrder)
                .Select(x => new
                {
                    Lead = x,
                    Paid = 0m, // placeholder; filled from lookup below
                    Personal = 0m // placeholder; filled from lookup below
                })
                .ToListAsync();

            // Canonicalize by LeadId in case older duplicates surface.
            var leads = LeadCanonicalizer.Canonicalize(leadsRaw.Select(r => r.Lead), _logger, "Leads/Index preload")
                .Select(c => leadsRaw.First(r => r.Lead.LeadId.Equals(c.LeadId, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var leadIds = leads.Select(l => l.Lead.LeadId).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();

            var productionLookup = new Dictionary<string, ProductionSnapshot>(StringComparer.OrdinalIgnoreCase);
            if (leadIds.Count > 0)
            {
                var prevTimeout = _db.Database.GetCommandTimeout();
                _db.Database.SetCommandTimeout(TimeSpan.FromSeconds(12));
                try
                {
                    var prodRows = await _db.ProductionRecords
                        .AsNoTracking()
                        .Where(p => p.AgentUserId == agentId && p.Side == ProductionSide.Lead && p.LeadId != null && leadIds.Contains(p.LeadId))
                        .OrderByDescending(p => p.UpdatedUtc)
                        .Select(p => new { p.LeadId, p.Status, p.Amount, p.PersonalAmount, p.UpdatedUtc })
                        .ToListAsync();

                    productionLookup = prodRows
                        .GroupBy(p => p.LeadId!, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            g => g.Key!,
                            g =>
                            {
                                var latest = g.First(); // already ordered desc
                                var submittedSum = g.Where(x => x.Status == ProductionStatus.Submitted).Sum(x => x.Amount);
                                var issuedSum = g.Where(x => x.Status == ProductionStatus.Issued).Sum(x => x.Amount);
                                var paidSum = g.Where(x => x.Status == ProductionStatus.Paid).Sum(x => x.Amount);
                                var personalSum = g.Sum(x => (decimal?)x.PersonalAmount ?? 0m);
                                return new ProductionSnapshot(latest.Status, latest.Amount, submittedSum, issuedSum, paidSum, personalSum);
                            },
                            StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    // Do not block the page if production lookup is slow; log and continue with empty production.
                    Console.WriteLine($"Leads/Index production lookup skipped: {ex.Message}");
                    productionLookup = new Dictionary<string, ProductionSnapshot>(StringComparer.OrdinalIgnoreCase);
                }
                finally
                {
                    _db.Database.SetCommandTimeout(prevTimeout);
                }
            }

            ViewData["ProductionTotals"] = await _production.GetAgentTotalsAsync(agentId, ProductionSide.Lead);

            var vm = leads.Select(l =>
            {
                var lead = l.Lead;
                var stage = string.IsNullOrWhiteSpace(lead.Bucket) ? "Contacted" : lead.Bucket;
                var attempts = CrmAttemptTracking.GetLeadAttemptCounts(lead, nowUtc, dialTimeZone);
                var originalLeadType = ResolveOriginalLeadType(lead.OriginalLeadType, lead.Bucket);
                var crmMeta = ReadLeadMeta(lead);
                var stageEnteredUtc = crmMeta.StageEnteredUtc == default ? lead.CreatedUtc : crmMeta.StageEnteredUtc;
                productionLookup.TryGetValue(lead.LeadId, out var prod);
                return new ClientListItemViewModel
                {
                    Id = Guid.NewGuid(),
                    ClientUserId = lead.LeadId,
                    FirstName = lead.FirstName ?? "",
                    LastName = lead.LastName ?? "",
                    Email = lead.Email ?? "",
                    Phone = FormatPhoneDisplay(lead.Phone),
                    Phone2 = FormatPhoneDisplay(lead.Phone2),
                    RecordType = "Lead",
                    CrmStatus = string.IsNullOrWhiteSpace(lead.CrmStatus) ? "Lead" : lead.CrmStatus!,
                    CrmPriority = string.IsNullOrWhiteSpace(crmMeta.CrmPriority) ? "Normal" : crmMeta.CrmPriority!,
                    CrmLastTouch = lead.UpdatedUtc,
                    CrmNextDate = crmMeta.CrmNextDate,
                    CrmNextText = crmMeta.CrmNextText,
                    CrmTags = crmMeta.CrmTags ?? lead.Bucket,
                    AgentNotes = crmMeta.AgentNotes ?? "",
                    AddressLine = lead.AddressLine,
                    City = lead.City,
                    State = lead.State,
                    ZipCode = lead.ZipCode,
                    County = lead.County,
                    Gender = lead.Gender,
                    DOB = lead.DOB,
                    MortgageLender = lead.MortgageLender,
                    LoanAmount = lead.LoanAmount,
                    OriginalLeadType = originalLeadType,
                    PipelineStage = stage,
                    PipelineOrder = lead.CrmOrder,
                    MeetingLocation = crmMeta.MeetingLocation,
                    ZoomJoinUrl = crmMeta.ZoomJoinUrl,
                    UsePersonalZoomLink = crmMeta.UsePersonalZoomLink,
                    MeetingTime = crmMeta.MeetingTime,
                    MeetingDurationMinutes = crmMeta.MeetingDurationMinutes,
                    WaitingOn = string.IsNullOrWhiteSpace(crmMeta.WaitingOn) ? ClientCrmMeta.DefaultWaitingOn : crmMeta.WaitingOn,
                    PinnedBrief = crmMeta.PinnedBrief,
                    StageEnteredUtc = stageEnteredUtc,
                    StageAgeDays = Math.Max(0, (DateTime.UtcNow.Date - stageEnteredUtc.Date).Days),
                    AttemptsToday = attempts.Today,
                    AttemptsThisWeek = attempts.Week,
                    AttemptsThisMonth = attempts.Month,
                    AttemptsYear = attempts.Year,
                    AttemptsLifetime = attempts.Lifetime,
                    LastContactChannel = string.IsNullOrWhiteSpace(crmMeta.LastContactChannel) ? "Call" : crmMeta.LastContactChannel,
                    DocChecklistCompletedCount = CompletedLeadDocCount(crmMeta.DocChecklist),
                    HasDuplicateEmail = false,
                    HasDuplicatePhone = false,
                    HasDuplicateHousehold = false,
                    AssignedOwner = crmMeta.Collaboration?.Owner ?? "",
                    WatchersCsv = LeadWatchersCsv(crmMeta),
                    PaidAmount = prod?.Paid ?? 0,
                    PersonalAmount = prod?.Personal ?? 0,
                    ProductionStatus = prod?.Status?.ToString() ?? "",
                    ProductionAmount = prod?.Amount ?? 0,
                    ProductionSubmittedAmount = prod?.Submitted ?? 0,
                    ProductionIssuedAmount = prod?.Issued ?? 0,
                    ProductionPaidAmount = prod?.Paid ?? 0
                };
            }).ToList();

            // Use the Leads-specific view so buckets/stages use the Lead CRM labels.
            return View(vm);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Leads/Index failed: {ex}");
            throw;
        }
    }

    private static string NormalizePhoneKey(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "";
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && digits.StartsWith("1")) digits = digits[1..];
        return digits;
    }

    private static string NormalizeStateValue(string? state)
        => (state ?? "").Trim().ToUpperInvariant();

    private sealed record ProductionSnapshot(ProductionStatus? Status, decimal Amount, decimal Submitted, decimal Issued, decimal Paid, decimal Personal);

    private static string ResolveLeadState(WorkstationLeadProfile lead, IReadOnlyDictionary<string, string>? fallbackStatesByPhone)
    {
        var direct = NormalizeStateValue(lead.State);
        if (!string.IsNullOrWhiteSpace(direct)) return direct;

        if (fallbackStatesByPhone == null || fallbackStatesByPhone.Count == 0) return "";

        var key1 = NormalizePhoneKey(lead.Phone);
        if (!string.IsNullOrWhiteSpace(key1) && fallbackStatesByPhone.TryGetValue(key1, out var state1))
            return state1;

        var key2 = NormalizePhoneKey(lead.Phone2);
        if (!string.IsNullOrWhiteSpace(key2) && fallbackStatesByPhone.TryGetValue(key2, out var state2))
            return state2;

        return "";
    }

    private async Task<Dictionary<string, string>> GetFallbackStatesByPhoneAsync(string agentId, CancellationToken ct = default)
    {
        var linked = await (
            from ac in _db.AgentClients.AsNoTracking()
            join cp in _db.ClientProfiles.AsNoTracking() on ac.ClientUserId equals cp.ClientUserId
            where ac.AgentUserId == agentId
            select new { cp.Phone, cp.CrmNotes }
        ).ToListAsync(ct);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in linked)
        {
            var key = NormalizePhoneKey(row.Phone);
            if (string.IsNullOrWhiteSpace(key) || map.ContainsKey(key))
                continue;

            var meta = ClientCrmMetaSerializer.Deserialize(row.CrmNotes);
            var state = NormalizeStateValue(meta?.State);
            if (string.IsNullOrWhiteSpace(state))
                continue;

            map[key] = state;
        }

        return map;
    }

    private static string FormatPhoneDisplay(string? phone)
    {
        var d = NormalizePhoneKey(phone);
        if (d.Length == 10)
            return $"({d[..3]}){d.Substring(3,3)}-{d.Substring(6,4)}";
        return phone ?? "";
    }

    private static string? Clean(string? v) => (v ?? "").Trim();

    private async Task<(int Today, int Week)> GetAgentWideDialTotalsAsync(string agentId, DateTime utcNow, TimeZoneInfo? dialTimeZone = null)
    {
        var tz = dialTimeZone ?? CrmAttemptTracking.DialTimeZone;
        var dayStart = CrmAttemptTracking.StartOfUtcDay(utcNow, tz);
        var weekStart = CrmAttemptTracking.StartOfUtcWeek(utcNow, tz);

        var today = await _db.WorkstationLeadProfiles
            .AsNoTracking()
            .Where(x => x.AgentUserId == agentId && x.CallsTodayDateUtc == dayStart)
            .SumAsync(x => (int?)x.CallsToday) ?? 0;

        var week = await _db.WorkstationLeadProfiles
            .AsNoTracking()
            .Where(x => x.AgentUserId == agentId && x.CallsWeekStartUtc == weekStart)
            .SumAsync(x => (int?)x.CallsWeek) ?? 0;

        return (today, week);
    }

    private static string NormalizePlaceholderEmailSlug(string? lastName)
    {
        var normalized = new string((lastName ?? "")
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetter)
            .ToArray());

        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static Dictionary<string,int> BuildHeaderMap(string[] headerRow)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headerRow.Length; i++)
        {
            var key = new string((headerRow[i] ?? "")
                .Where(char.IsLetterOrDigit)
                .ToArray())
                .ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(key) && !map.ContainsKey(key))
                map[key] = i;
        }
        return map;
    }

    private static string? GetField(string[] row, Dictionary<string,int> map, bool hasHeader, int fallback, params string[] aliases)
    {
        if (hasHeader)
        {
            foreach (var alias in aliases)
            {
                var key = new string((alias ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                if (map.TryGetValue(key, out var idx) && idx < row.Length)
                    return Clean(row[idx]);
            }
        }
        else if (fallback < row.Length)
        {
            return Clean(row[fallback]);
        }
        return null;
    }

    [HttpGet]
    public async Task<IActionResult> Leads(string? bucket)
    {
        string agentId;
        try { agentId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        var nowUtc = DateTime.UtcNow;
        var dialTimeZone = _agentTimeZoneResolver.Resolve(HttpContext);

        var normalizedBucket = NormalizeBucket(bucket);
        var agentWideDialTotals = await GetAgentWideDialTotalsAsync(agentId, nowUtc, dialTimeZone);

        var query = _db.WorkstationLeadProfiles
            .AsNoTracking()
            .Where(x => x.AgentUserId == agentId);

        // Default lists should not surface NotInterested unless explicitly filtered for that bucket.
        if (string.IsNullOrWhiteSpace(normalizedBucket))
        {
            query = query.Where(x =>
                (x.Bucket == null || x.Bucket.ToLower() != "notinterested") &&
                (x.CrmStage == null || x.CrmStage.ToLower() != "notinterested"));
        }

        if (!string.IsNullOrWhiteSpace(normalizedBucket))
        {
            query = query.Where(x =>
                (x.OriginalLeadType != null && x.OriginalLeadType == normalizedBucket) ||
                ((x.OriginalLeadType == null || x.OriginalLeadType == "") && x.Bucket == normalizedBucket));
        }

        var rawLeads = await query
            .OrderBy(x => x.CallCount)           // fewest calls first
            .ThenByDescending(x => x.CrmOrder)   // then newest/priority
            .ToListAsync();

        rawLeads = LeadCanonicalizer.Canonicalize(rawLeads, _logger, "Leads/Leads api")
            .ToList();

        var fallbackStatesByPhone = await GetFallbackStatesByPhoneAsync(agentId);

        var leads = rawLeads.Select(x =>
        {
            var attempts = CrmAttemptTracking.GetLeadAttemptCounts(x, nowUtc, dialTimeZone);
            var originalLeadType = ResolveOriginalLeadType(x.OriginalLeadType, x.Bucket, normalizedBucket);
            var state = ResolveLeadState(x, fallbackStatesByPhone);
            var crmMeta = ReadLeadMeta(x);
            return new
            {
            x.LeadId,
            x.FirstName,
            x.LastName,
            x.Email,
            Phone = FormatPhoneDisplay(x.Phone),
            Phone2 = FormatPhoneDisplay(x.Phone2),
            Bucket = x.Bucket,
            OriginalLeadType = originalLeadType,
            x.CrmStage,
            x.CrmStatus,
            CrmPriority = crmMeta.CrmPriority ?? "Normal",
            CrmNextDate = crmMeta.CrmNextDate,
            CrmNextText = crmMeta.CrmNextText ?? "",
            CrmTags = crmMeta.CrmTags ?? x.Bucket ?? "",
            x.CallCount,
            x.CrmOrder,
            CrmNotes = crmMeta.AgentNotes ?? "",
            x.AddressLine,
            x.City,
            State = state,
            x.County,
            x.ZipCode,
            x.MortgageLender,
            x.LoanAmount,
            x.DOB,
            DobFormatted = x.DOB?.ToString("MM-dd-yyyy"),
            x.Gender,
            x.Age,
            x.Btc,
            x.CreatedUtc,
            x.UpdatedUtc,
            AttemptsToday = attempts.Today,
            AttemptsThisWeek = attempts.Week,
            AttemptsThisMonth = attempts.Month,
            AttemptsThisYear = attempts.Year,
            AttemptsLifetime = attempts.Lifetime,
            WaitingOn = crmMeta.WaitingOn,
            PinnedBrief = crmMeta.PinnedBrief,
            MeetingLocation = crmMeta.MeetingLocation,
            ZoomJoinUrl = crmMeta.ZoomJoinUrl,
            UsePersonalZoomLink = crmMeta.UsePersonalZoomLink,
            MeetingTime = crmMeta.MeetingTime,
            MeetingDurationMinutes = crmMeta.MeetingDurationMinutes,
            DialsToday = attempts.Today,
            DialsWeek = attempts.Week,
            DialsTodayAgentWide = agentWideDialTotals.Today,
            DialsWeekAgentWide = agentWideDialTotals.Week
        };
        }).ToList();

        return Json(leads);
    }

    [HttpGet]
    public async Task<IActionResult> StateOptions()
    {
        string agentId;
        try { agentId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        var states = await _db.WorkstationLeadProfiles
            .AsNoTracking()
            .Where(x => x.AgentUserId == agentId && x.State != null && x.State.Trim() != "")
            .Select(x => x.State!)
            .Distinct()
            .ToListAsync();

        var distinct = states
            .Select(x => x.Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var fallbackStates = await GetFallbackStatesByPhoneAsync(agentId);
        foreach (var state in fallbackStates.Values)
        {
            var normalized = NormalizeStateValue(state);
            if (!string.IsNullOrWhiteSpace(normalized) && !distinct.Contains(normalized, StringComparer.Ordinal))
                distinct.Add(normalized);
        }
        distinct = distinct.OrderBy(x => x, StringComparer.Ordinal).ToList();

        return Json(distinct);
    }

    private static bool IsToday(DateTime? value)
        => value.HasValue && value.Value.Date == DateTime.UtcNow.Date;

    private static bool IsOverdue(DateTime? value)
        => value.HasValue && value.Value.Date < DateTime.UtcNow.Date;

    private static (string Title, string Description, string Rule) LeadQueueMeta(string queueKey)
        => queueKey switch
        {
            "callsnow" => (
                "Calls Now",
                "Priority follow-up calls that should happen immediately.",
                "High/Urgent next-action leads that are due today or overdue."
            ),
            "today" => (
                "Due Today",
                "Touches due today and ready for execution.",
                "Leads with next action date set to today."
            ),
            "overdue" => (
                "Overdue",
                "Rescue this list before it gets stale.",
                "Leads with next action date in the past."
            ),
            "meetings" => (
                "Meetings",
                "Booked leads with event execution pressure.",
                "Leads currently in the Booked stage."
            ),
            "waitingclient" => (
                "Waiting On Client",
                "Leads who owe the next move back to you.",
                "Leads with Waiting On = Waiting On Client."
            ),
            "waitingcarrier" => (
                "Waiting On Carrier",
                "Cases blocked externally and needing visibility.",
                "Leads with Waiting On = Waiting On Carrier."
            ),
            _ => (
                "Queue",
                "Manage this queue directly.",
                "Filtered lead records assigned to this queue."
            )
        };

    private static bool MatchesLeadQueue(WorkstationLeadProfile lead, string queueKey)
    {
        var stage = NormalizePipelineStage(lead.Bucket) ?? lead.Bucket;
        var meta = ReadLeadMeta(lead);
        var waitingOn = (meta.WaitingOn ?? ClientCrmMeta.DefaultWaitingOn).Trim();
        var nextDate = meta.CrmNextDate?.Date;
        var priority = (meta.CrmPriority ?? "Normal").Trim();
        var isHighPriority = priority.Equals("High", StringComparison.OrdinalIgnoreCase)
            || priority.Equals("Urgent", StringComparison.OrdinalIgnoreCase);

        return queueKey switch
        {
            "callsnow" => isHighPriority && (IsToday(nextDate) || IsOverdue(nextDate)),
            "today" => IsToday(nextDate),
            "overdue" => IsOverdue(nextDate),
            "meetings" => string.Equals(stage, "Booked", StringComparison.OrdinalIgnoreCase),
            "waitingclient" => string.Equals(waitingOn, "WaitingOnClient", StringComparison.OrdinalIgnoreCase),
            "waitingcarrier" => string.Equals(waitingOn, "WaitingOnCarrier", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    [HttpGet]
    public async Task<IActionResult> MyDaySnapshot()
    {
        string agentId;
        try { agentId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        var leads = await _db.WorkstationLeadProfiles
            .AsNoTracking()
            .Where(x => x.AgentUserId == agentId)
            .ToListAsync();

        var queueKeys = new[] { "callsnow", "today", "overdue", "meetings", "waitingclient", "waitingcarrier" };
        var queues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in queueKeys)
        {
            var matchingIds = leads
                .Where(x => MatchesLeadQueue(x, key))
                .Select(x => x.LeadId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            queues[key] = new
            {
                count = matchingIds.Count,
                ids = matchingIds
            };
        }

        return Json(new
        {
            generatedUtc = DateTime.UtcNow,
            queues
        });
    }

    [HttpGet]
    public async Task<IActionResult> Queue(string queue)
    {
        string agentId;
        try { agentId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        var queueKey = Norm(queue);
        var meta = LeadQueueMeta(queueKey);
        var nowUtc = DateTime.UtcNow;
        var dialTimeZone = _agentTimeZoneResolver.Resolve(HttpContext);

        var leads = await _db.WorkstationLeadProfiles
            .AsNoTracking()
            .Where(x => x.AgentUserId == agentId)
            .OrderByDescending(x => x.CrmOrder)
            .ToListAsync();

        var leadIds = leads.Select(x => x.LeadId).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();

        var productionLookup = new Dictionary<string, ProductionSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (leadIds.Count > 0)
        {
            var prevTimeout = _db.Database.GetCommandTimeout();
            _db.Database.SetCommandTimeout(TimeSpan.FromSeconds(30));
            try
            {
                var prodRows = await _db.ProductionRecords
                    .AsNoTracking()
                    .Where(p => p.AgentUserId == agentId && p.Side == ProductionSide.Lead && p.LeadId != null && leadIds.Contains(p.LeadId))
                    .OrderByDescending(p => p.UpdatedUtc)
                    .Select(p => new { p.LeadId, p.Status, p.Amount, p.PersonalAmount, p.UpdatedUtc })
                    .ToListAsync();

                productionLookup = prodRows
                    .GroupBy(p => p.LeadId!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key!,
                        g =>
                        {
                            var latest = g.First();
                            var submittedSum = g.Where(x => x.Status == ProductionStatus.Submitted).Sum(x => x.Amount);
                            var issuedSum = g.Where(x => x.Status == ProductionStatus.Issued).Sum(x => x.Amount);
                            var paidSum = g.Where(x => x.Status == ProductionStatus.Paid).Sum(x => x.Amount);
                            var personalSum = g.Sum(x => (decimal?)(x.PersonalAmount) ?? 0m);
                            return new ProductionSnapshot(latest.Status, latest.Amount, submittedSum, issuedSum, paidSum, personalSum);
                        },
                        StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                _db.Database.SetCommandTimeout(prevTimeout);
            }
        }

        var items = leads
            .Where(x => MatchesLeadQueue(x, queueKey))
            .Select(l =>
            {
                var attempts = CrmAttemptTracking.GetLeadAttemptCounts(l, nowUtc, dialTimeZone);
                var originalLeadType = ResolveOriginalLeadType(l.OriginalLeadType, l.Bucket);
                var crmMeta = ReadLeadMeta(l);
                var stageEnteredUtc = crmMeta.StageEnteredUtc == default ? l.CreatedUtc : crmMeta.StageEnteredUtc;
                productionLookup.TryGetValue(l.LeadId, out var prod);

                return new ClientListItemViewModel
                {
                    Id = Guid.NewGuid(),
                    ClientUserId = l.LeadId,
                    FirstName = l.FirstName ?? "",
                    LastName = l.LastName ?? "",
                    Email = l.Email ?? "",
                    Phone = FormatPhoneDisplay(l.Phone),
                    Phone2 = FormatPhoneDisplay(l.Phone2),
                    RecordType = "Lead",
                    CrmStatus = string.IsNullOrWhiteSpace(l.CrmStatus) ? "Lead" : l.CrmStatus!,
                    CrmPriority = string.IsNullOrWhiteSpace(crmMeta.CrmPriority) ? "Normal" : crmMeta.CrmPriority!,
                    CrmLastTouch = l.UpdatedUtc,
                    CrmNextDate = crmMeta.CrmNextDate,
                    CrmNextText = crmMeta.CrmNextText,
                    CrmTags = crmMeta.CrmTags ?? l.Bucket,
                    AgentNotes = crmMeta.AgentNotes ?? "",
                    AddressLine = l.AddressLine,
                    City = l.City,
                    State = l.State,
                    ZipCode = l.ZipCode,
                    County = l.County,
                    Gender = l.Gender,
                    DOB = l.DOB,
                    MortgageLender = l.MortgageLender,
                    LoanAmount = l.LoanAmount,
                    OriginalLeadType = originalLeadType,
                    PipelineStage = string.IsNullOrWhiteSpace(l.Bucket) ? "Contacted" : l.Bucket,
                    PipelineOrder = l.CrmOrder,
                    MeetingLocation = crmMeta.MeetingLocation,
                    ZoomJoinUrl = crmMeta.ZoomJoinUrl,
                    UsePersonalZoomLink = crmMeta.UsePersonalZoomLink,
                    MeetingTime = crmMeta.MeetingTime,
                    MeetingDurationMinutes = crmMeta.MeetingDurationMinutes,
                    WaitingOn = string.IsNullOrWhiteSpace(crmMeta.WaitingOn) ? ClientCrmMeta.DefaultWaitingOn : crmMeta.WaitingOn,
                    PinnedBrief = crmMeta.PinnedBrief,
                    StageEnteredUtc = stageEnteredUtc,
                    StageAgeDays = Math.Max(0, (DateTime.UtcNow.Date - stageEnteredUtc.Date).Days),
                    AttemptsToday = attempts.Today,
                    AttemptsThisWeek = attempts.Week,
                    AttemptsThisMonth = attempts.Month,
                    AttemptsYear = attempts.Year,
                    AttemptsLifetime = attempts.Lifetime,
                    LastContactChannel = string.IsNullOrWhiteSpace(crmMeta.LastContactChannel) ? "Call" : crmMeta.LastContactChannel,
                    DocChecklistCompletedCount = CompletedLeadDocCount(crmMeta.DocChecklist),
                    HasDuplicateEmail = false,
                    HasDuplicatePhone = false,
                    HasDuplicateHousehold = false,
                    AssignedOwner = crmMeta.Collaboration?.Owner ?? "",
                    WatchersCsv = LeadWatchersCsv(crmMeta),
                    PaidAmount = prod?.Paid ?? 0,
                    PersonalAmount = prod?.Personal ?? 0,
                    ProductionStatus = prod?.Status?.ToString() ?? "",
                    ProductionAmount = prod?.Amount ?? 0,
                    ProductionSubmittedAmount = prod?.Submitted ?? 0,
                    ProductionIssuedAmount = prod?.Issued ?? 0,
                    ProductionPaidAmount = prod?.Paid ?? 0
                };
            })
            .OrderBy(x => x.LastName)
            .ThenBy(x => x.FirstName)
            .ToList();

        return View(new ClientQueuePageViewModel
        {
            QueueKey = queueKey,
            QueueTitle = meta.Title,
            QueueDescription = meta.Description,
            QueueRule = meta.Rule,
            Items = items
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> IncrementCall(string id)
    {
        string agentId;
        try { agentId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        var lead = await LoadCanonicalLeadAsync(agentId, id, "Lead detail");
        if (lead == null) return NotFound();
        var nowUtc = DateTime.UtcNow;
        var dialTimeZone = _agentTimeZoneResolver.Resolve(HttpContext);
        CrmAttemptTracking.RollLeadAttemptWindows(lead, nowUtc, dialTimeZone);
        lead.CallCount += 1;
        lead.CallsToday += 1;
        lead.CallsWeek += 1;
        lead.CallsMonth += 1;
        lead.CallsYear += 1;
        lead.UpdatedUtc = nowUtc;
        await _db.SaveChangesAsync();
        var attempts = CrmAttemptTracking.GetLeadAttemptCounts(lead, nowUtc, dialTimeZone);
        var agentWideDialTotals = await GetAgentWideDialTotalsAsync(agentId, nowUtc, dialTimeZone);
        return Ok(new
        {
            callCount = lead.CallCount,
            attemptsToday = attempts.Today,
            attemptsThisWeek = attempts.Week,
            attemptsThisMonth = attempts.Month,
            attemptsThisYear = attempts.Year,
            attemptsLifetime = attempts.Lifetime,
            dialsToday = attempts.Today,
            dialsWeek = attempts.Week,
            dialsTodayAgentWide = agentWideDialTotals.Today,
            dialsWeekAgentWide = agentWideDialTotals.Week
        });
    }

    [HttpPost]
    [Route("Leads/Admin/ResetCounters")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminResetCounters([FromBody] ResetLeadCountersRequest req)
    {
        if (!IsAdminUser()) return Forbid();
        if (req == null || req.LeadIds == null || req.LeadIds.Count == 0)
            return BadRequest("LeadIds required");

        var normIds = req.LeadIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(Norm).ToList();
        if (normIds.Count == 0) return BadRequest("LeadIds required");

        var leads = await _db.WorkstationLeadProfiles.Where(x => normIds.Contains(Norm(x.LeadId))).ToListAsync();
        var nowUtc = DateTime.UtcNow;
        var dialTimeZone = _agentTimeZoneResolver.Resolve(HttpContext);
        var dayStart = CrmAttemptTracking.StartOfUtcDay(nowUtc, dialTimeZone);
        var weekStart = CrmAttemptTracking.StartOfUtcWeek(nowUtc, dialTimeZone);
        var monthStart = CrmAttemptTracking.StartOfUtcMonth(nowUtc, dialTimeZone);
        var yearStart = CrmAttemptTracking.StartOfUtcYear(nowUtc, dialTimeZone);

        foreach (var lead in leads)
        {
            if (req.CallsToday.HasValue) lead.CallsToday = Math.Max(0, req.CallsToday.Value);
            if (req.CallsWeek.HasValue) lead.CallsWeek = Math.Max(0, req.CallsWeek.Value);
            if (req.CallsMonth.HasValue) lead.CallsMonth = Math.Max(0, req.CallsMonth.Value);
            if (req.CallsYear.HasValue) lead.CallsYear = Math.Max(0, req.CallsYear.Value);
            if (req.CallCount.HasValue) lead.CallCount = Math.Max(0, req.CallCount.Value);

            // reset window anchors to current boundaries
            lead.CallsTodayDateUtc = dayStart;
            lead.CallsWeekStartUtc = weekStart;
            lead.CallsMonthStartUtc = monthStart;
            lead.CallsYearStartUtc = yearStart;
            lead.UpdatedUtc = nowUtc;
        }

        if (!req.DryRun)
            await _db.SaveChangesAsync();

        return Json(new
        {
            ok = true,
            dryRun = req.DryRun,
            updated = leads.Count,
            leadIds = leads.Select(l => l.LeadId).ToList(),
            values = new
            {
                callsToday = req.CallsToday,
                callsWeek = req.CallsWeek,
                callsMonth = req.CallsMonth,
                callsYear = req.CallsYear,
                callCount = req.CallCount
            }
        });
    }

    [HttpGet]
    public async Task<IActionResult> Lead(string id)
    {
        string agentId;
        try { agentId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        var lead = await LoadCanonicalLeadAsync(agentId, id, "IncrementCall");
        if (lead == null) return NotFound();
        var nowUtc = DateTime.UtcNow;
        var dialTimeZone = _agentTimeZoneResolver.Resolve(HttpContext);
        var agentWideDialTotals = await GetAgentWideDialTotalsAsync(agentId, nowUtc, dialTimeZone);
        return Json(LeadPayload(lead, nowUtc, agentWideDialTotals.Today, agentWideDialTotals.Week, dialTimeZone));
    }

    public record LeadOutcomeRequest(string clientUserId, string outcomeCode, string? customNote);
    public record LeadQuickViewRequest(
        string clientUserId,
        string? firstName,
        string? lastName,
        string? email,
        string? phone,
        string? phone2,
        string? dob,
        string? gender,
        string? addressLine,
        string? city,
        string? state,
        string? county,
        string? zipCode,
        string? age,
        string? mortgageLender,
        string? loanAmount,
        string? crmStatus,
        string? crmPriority,
        string? crmLastTouch,
        string? crmNextDate,
        string? crmNextText,
        string? crmTags,
        string? agentNotes,
        string? pipelineStage,
        string? meetingLocation,
        string? zoomJoinUrl,
        bool? usePersonalZoomLink,
        string? meetingTime,
        int? meetingDurationMinutes,
        string? waitingOn,
        string? pinnedBrief,
        bool? docIdReceived,
        bool? docAppSent,
        bool? docAppSigned,
        bool? docPolicyDelivered,
        bool? docReviewBooked,
        string? watchers,
        string? mentionNote,
        string? btc
    );

    public record ResetLeadCountersRequest
    {
        public List<string> LeadIds { get; set; } = new();
        public int? CallsToday { get; set; }
        public int? CallsWeek { get; set; }
        public int? CallsMonth { get; set; }
        public int? CallsYear { get; set; }
        public int? CallCount { get; set; }
        public bool DryRun { get; set; } = false;
    }

    private static object LeadPayload(
        WorkstationLeadProfile lead,
        DateTime? utcNow = null,
        int? dialsTodayAgentWide = null,
        int? dialsWeekAgentWide = null,
        TimeZoneInfo? dialTimeZone = null)
    {
        var effectiveUtcNow = utcNow ?? DateTime.UtcNow;
        var attempts = CrmAttemptTracking.GetLeadAttemptCounts(lead, effectiveUtcNow, dialTimeZone);
        var crmMeta = ReadLeadMeta(lead);
        var stageEnteredUtc = crmMeta.StageEnteredUtc == default ? lead.CreatedUtc : crmMeta.StageEnteredUtc;
        var crmPriority = string.IsNullOrWhiteSpace(crmMeta.CrmPriority) ? "Normal" : crmMeta.CrmPriority;
        var watchers = (crmMeta.Collaboration?.Watchers ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new
        {
            lead.LeadId,
            firstName = lead.FirstName,
            lastName = lead.LastName,
            email = lead.Email,
            phone = lead.Phone,
            phone2 = lead.Phone2,
            dob = lead.DOB?.ToString("yyyy-MM-dd"),
            dobFormatted = lead.DOB?.ToString("MM-dd-yyyy"),
            gender = lead.Gender,
            addressLine = lead.AddressLine,
            city = lead.City,
            state = lead.State,
            county = lead.County,
            zipCode = lead.ZipCode,
            mortgageLender = lead.MortgageLender,
            loanAmount = lead.LoanAmount,
            age = lead.Age,
            btc = lead.Btc,
            crmStatus = lead.CrmStatus,
            crmPriority,
            crmLastTouch = lead.UpdatedUtc.ToString("yyyy-MM-dd"),
            updatedUtc = lead.UpdatedUtc,
            crmNextDate = crmMeta.CrmNextDate?.ToString("yyyy-MM-dd"),
            crmNextText = crmMeta.CrmNextText ?? "",
            crmTags = crmMeta.CrmTags ?? "",
            agentNotes = crmMeta.AgentNotes ?? "",
            crmNotes = crmMeta.AgentNotes ?? "",
            pipelineStage = lead.Bucket,
            bucket = lead.Bucket,
            waitingOn = string.IsNullOrWhiteSpace(crmMeta.WaitingOn) ? ClientCrmMeta.DefaultWaitingOn : crmMeta.WaitingOn,
            meetingLocation = crmMeta.MeetingLocation ?? lead.AddressLine,
            zoomJoinUrl = crmMeta.ZoomJoinUrl ?? "",
            usePersonalZoomLink = crmMeta.UsePersonalZoomLink,
            meetingTime = crmMeta.MeetingTime ?? "09:00",
            meetingDurationMinutes = crmMeta.MeetingDurationMinutes <= 0 ? 30 : crmMeta.MeetingDurationMinutes,
            pinnedBrief = crmMeta.PinnedBrief ?? "",
            docChecklist = new
            {
                idReceived = crmMeta.DocChecklist?.IdReceived ?? false,
                appSent = crmMeta.DocChecklist?.AppSent ?? false,
                appSigned = crmMeta.DocChecklist?.AppSigned ?? false,
                policyDelivered = crmMeta.DocChecklist?.PolicyDelivered ?? false,
                reviewBooked = crmMeta.DocChecklist?.ReviewBooked ?? false,
                completedCount = CompletedLeadDocCount(crmMeta.DocChecklist)
            },
            collaboration = new
            {
                owner = crmMeta.Collaboration?.Owner ?? "",
                watchers,
                mentionNotes = crmMeta.Collaboration?.MentionNotes ?? new List<ClientCrmMentionNote>()
            },
            activities = crmMeta.Activities ?? new List<ClientCrmActivity>(),
            stageEnteredUtc,
            createdUtc = lead.CreatedUtc,
            stageAgeDays = Math.Max(0, (effectiveUtcNow.Date - stageEnteredUtc.Date).Days),
            attemptsToday = attempts.Today,
            attemptsThisWeek = attempts.Week,
            attemptsThisMonth = attempts.Month,
            attemptsThisYear = attempts.Year,
            attemptsLifetime = attempts.Lifetime,
            dialsToday = attempts.Today,
            dialsWeek = attempts.Week,
            dialsTodayAgentWide,
            dialsWeekAgentWide,
            callCount = lead.CallCount,
            lastContactChannel = string.IsNullOrWhiteSpace(crmMeta.LastContactChannel) ? "Call" : crmMeta.LastContactChannel,
            originalLeadType = ResolveOriginalLeadType(lead.OriginalLeadType, lead.Bucket)
        };
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveQuickView([FromBody] LeadQuickViewRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.clientUserId)) return BadRequest("Lead id required");
        string agentId;
        try { agentId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        var lead = await LoadCanonicalLeadAsync(agentId, req.clientUserId, "ApplyOutcome");
        if (lead == null) return NotFound();

        PreserveOriginalLeadType(lead);
        var meta = ReadLeadMeta(lead);
        lead.Email = string.IsNullOrWhiteSpace(req.email) ? lead.Email : req.email;
        lead.Phone = string.IsNullOrWhiteSpace(req.phone) ? lead.Phone : req.phone;
        lead.Phone2 = string.IsNullOrWhiteSpace(req.phone2) ? lead.Phone2 : req.phone2;
        lead.FirstName = string.IsNullOrWhiteSpace(req.firstName) ? lead.FirstName : req.firstName.Trim();
        lead.LastName = string.IsNullOrWhiteSpace(req.lastName) ? lead.LastName : req.lastName.Trim();
        lead.DOB = string.IsNullOrWhiteSpace(req.dob)
    ? lead.DOB
    : DateTime.TryParse(req.dob, out var parsedDob)
        ? parsedDob
        : lead.DOB;
        lead.Gender = req.gender;
        lead.AddressLine = req.addressLine ?? lead.AddressLine;
        lead.City = req.city ?? lead.City;
        lead.State = req.state ?? lead.State;
        lead.County = req.county ?? lead.County;
        lead.ZipCode = req.zipCode ?? lead.ZipCode;
        lead.MortgageLender = req.mortgageLender ?? lead.MortgageLender;
        lead.LoanAmount = req.loanAmount ?? lead.LoanAmount;
        lead.Age = string.IsNullOrWhiteSpace(req.age) ? lead.Age : req.age;
        lead.Btc = string.IsNullOrWhiteSpace(req.btc) ? lead.Btc : req.btc;
        lead.CrmStatus = req.crmStatus ?? lead.CrmStatus ?? "Lead";
        var allowedPriority = new[] { "Low", "Normal", "High", "Urgent" };
        var normalizedPriority = (req.crmPriority ?? "").Trim();
        if (!allowedPriority.Contains(normalizedPriority, StringComparer.OrdinalIgnoreCase))
            normalizedPriority = string.IsNullOrWhiteSpace(meta.CrmPriority) ? "Normal" : meta.CrmPriority!;
        else
            normalizedPriority = allowedPriority.First(x => x.Equals(normalizedPriority, StringComparison.OrdinalIgnoreCase));

        DateTime? normalizedNextDate = meta.CrmNextDate;
        if (string.IsNullOrWhiteSpace(req.crmNextDate))
        {
            normalizedNextDate = null;
        }
        else if (DateTime.TryParse(req.crmNextDate, out var parsedNextDate))
        {
            normalizedNextDate = parsedNextDate.Date;
        }

        var normalizedNextText = string.IsNullOrWhiteSpace(req.crmNextText) ? null : req.crmNextText.Trim();
        if (normalizedNextDate.HasValue && string.IsNullOrWhiteSpace(normalizedNextText))
            normalizedNextDate = null;
        if (!normalizedNextDate.HasValue && !string.IsNullOrWhiteSpace(normalizedNextText))
            normalizedNextText = null;

        var normalizedStage = NormalizePipelineStage(req.pipelineStage) ?? lead.Bucket;
        if (!string.Equals(lead.Bucket, normalizedStage, StringComparison.OrdinalIgnoreCase))
            meta.StageEnteredUtc = DateTime.UtcNow;
        lead.Bucket = normalizedStage;

        meta.CrmPriority = normalizedPriority;
        meta.CrmNextDate = normalizedNextDate;
        meta.CrmNextText = normalizedNextText;
        meta.CrmTags = string.IsNullOrWhiteSpace(req.crmTags) ? null : req.crmTags.Trim();
        meta.AgentNotes = string.IsNullOrWhiteSpace(req.agentNotes) ? null : req.agentNotes.Trim();
        meta.WaitingOn = ClientCrmMetaSerializer.NormalizeWaitingOn(req.waitingOn);
        meta.PinnedBrief = string.IsNullOrWhiteSpace(req.pinnedBrief) ? null : req.pinnedBrief.Trim();
        if (req.meetingLocation != null)
            meta.MeetingLocation = string.IsNullOrWhiteSpace(req.meetingLocation) ? null : req.meetingLocation.Trim();
        if (req.zoomJoinUrl != null)
            meta.ZoomJoinUrl = string.IsNullOrWhiteSpace(req.zoomJoinUrl) ? null : req.zoomJoinUrl.Trim();
        if (req.usePersonalZoomLink.HasValue)
            meta.UsePersonalZoomLink = req.usePersonalZoomLink.Value;
        if (req.meetingTime != null)
            meta.MeetingTime = string.IsNullOrWhiteSpace(req.meetingTime) ? "09:00" : req.meetingTime.Trim();
        if (req.meetingDurationMinutes.HasValue)
            meta.MeetingDurationMinutes = req.meetingDurationMinutes.Value <= 0 ? 30 : req.meetingDurationMinutes.Value;

        meta.DocChecklist ??= new ClientCrmDocChecklist();
        meta.DocChecklist.IdReceived = req.docIdReceived ?? meta.DocChecklist.IdReceived;
        meta.DocChecklist.AppSent = req.docAppSent ?? meta.DocChecklist.AppSent;
        meta.DocChecklist.AppSigned = req.docAppSigned ?? meta.DocChecklist.AppSigned;
        meta.DocChecklist.PolicyDelivered = req.docPolicyDelivered ?? meta.DocChecklist.PolicyDelivered;
        meta.DocChecklist.ReviewBooked = req.docReviewBooked ?? meta.DocChecklist.ReviewBooked;

        meta.Collaboration ??= new ClientCrmCollaboration();
        meta.Collaboration.Watchers = (req.watchers ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        meta.Collaboration.MentionNotes ??= new List<ClientCrmMentionNote>();
        if (!string.IsNullOrWhiteSpace(req.mentionNote))
        {
            meta.Collaboration.MentionNotes.Insert(0, new ClientCrmMentionNote
            {
                Note = req.mentionNote.Trim(),
                MentionedUser = meta.Collaboration.Watchers.FirstOrDefault(),
                CreatedBy = agentId
            });
        }
        lead.CrmNotes = ClientCrmMetaSerializer.Serialize(meta);
        lead.AgentUserId = agentId;
        lead.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        var dialTimeZone = _agentTimeZoneResolver.Resolve(HttpContext);
        return Json(new { payload = LeadPayload(lead, dialTimeZone: dialTimeZone) });
    }

    [HttpPost]
    public async Task<IActionResult> ApplyOutcome([FromBody] LeadOutcomeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.clientUserId)) return BadRequest("Lead id required");
        string agentId;
        try { agentId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        var lead = await LoadCanonicalLeadAsync(agentId, req.clientUserId, "SaveQuickView");
        if (lead == null) return NotFound();

        if (!OutcomeStageMap.TryGetValue(req.outcomeCode ?? "", out var stage))
            return BadRequest("Invalid outcomeCode");

        PreserveOriginalLeadType(lead);
        var meta = ReadLeadMeta(lead);
        lead.CrmStage = stage;
        if (!string.Equals(lead.Bucket, stage, StringComparison.OrdinalIgnoreCase))
            meta.StageEnteredUtc = DateTime.UtcNow;
        lead.Bucket = stage; // keep pipeline bucket in sync with outcome
        if (!string.IsNullOrWhiteSpace(req.customNote))
            meta.AgentNotes = req.customNote.Trim();
        lead.CrmNotes = ClientCrmMetaSerializer.Serialize(meta);
        lead.AgentUserId = agentId;
        lead.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var nowUtc = DateTime.UtcNow;
        var dialTimeZone = _agentTimeZoneResolver.Resolve(HttpContext);
        var agentWideDialTotals = await GetAgentWideDialTotalsAsync(agentId, nowUtc, dialTimeZone);

        return Json(new
        {
            payload = LeadPayload(lead, nowUtc, agentWideDialTotals.Today, agentWideDialTotals.Week, dialTimeZone),
            suggestion = new { nextDate = (string?)null, nextText = req.customNote }
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string clientUserId)
    {
        if (string.IsNullOrWhiteSpace(clientUserId)) return BadRequest("Lead id required");

        string agentId;
        try { agentId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        var lead = await _db.WorkstationLeadProfiles.FirstOrDefaultAsync(x =>
            x.LeadId == clientUserId && x.AgentUserId == agentId);
        if (lead == null) return NotFound();

        _db.WorkstationLeadProfiles.Remove(lead);
        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteBulk([FromBody] string[] ids)
    {
        if (ids == null || ids.Length == 0) return BadRequest("No ids provided");
        if (ids.Length > 500) return BadRequest("Too many ids in one request. Limit is 500.");
        string agentId;
        try { agentId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        var toDelete = await _db.WorkstationLeadProfiles
            .Where(x => ids.Contains(x.LeadId) && x.AgentUserId == agentId)
            .ToListAsync();
        if (toDelete.Count == 0) return Json(new { deleted = 0 });

        _db.WorkstationLeadProfiles.RemoveRange(toDelete);
        await _db.SaveChangesAsync();
        return Json(new { deleted = toDelete.Count });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteBucket([FromBody] DeleteBucketRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Bucket))
            return BadRequest("Bucket required");

        var bucket = NormalizePipelineStage(req.Bucket);
        if (string.IsNullOrWhiteSpace(bucket))
            return BadRequest("Invalid bucket");

        string agentId;
        try { agentId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        var batchSize = Math.Clamp(req.BatchSize ?? 100, 1, 200);

        var isProductBucket = ProductBuckets.Contains(bucket, StringComparer.OrdinalIgnoreCase);

        var toDelete = isProductBucket
            ? await _db.WorkstationLeadProfiles
                .Where(x =>
                    x.AgentUserId == agentId &&
                    (x.OriginalLeadType == bucket ||
                     ((x.OriginalLeadType == null || x.OriginalLeadType == "") && x.Bucket == bucket)))
                .OrderByDescending(x => x.CreatedUtc)
                .Take(batchSize)
                .ToListAsync()
            : await _db.WorkstationLeadProfiles
                .Where(x => x.AgentUserId == agentId && x.Bucket == bucket)
                .OrderByDescending(x => x.CreatedUtc)
                .Take(batchSize)
                .ToListAsync();

        if (toDelete.Count == 0) return Json(new { deleted = 0 });

        _db.WorkstationLeadProfiles.RemoveRange(toDelete);
        await _db.SaveChangesAsync();
        var remaining = isProductBucket
            ? await _db.WorkstationLeadProfiles
                .CountAsync(x =>
                    x.AgentUserId == agentId &&
                    (x.OriginalLeadType == bucket ||
                     ((x.OriginalLeadType == null || x.OriginalLeadType == "") && x.Bucket == bucket)))
            : await _db.WorkstationLeadProfiles
                .CountAsync(x => x.AgentUserId == agentId && x.Bucket == bucket);
        return Json(new { deleted = toDelete.Count, remaining });
    }

    public sealed class DeleteBucketRequest
    {
        public string Bucket { get; set; } = "";
        public int? BatchSize { get; set; }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reorder([FromBody] ReorderRequest req)
    {
        if (req?.Ids == null || req.Ids.Count == 0) return BadRequest("No ids provided");
        var ids = req.Ids.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (ids.Count == 0) return BadRequest("No ids provided");
        var normalizedBucket = NormalizePipelineStage(req.Bucket);

        string agentId;
        try { agentId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        var leads = await _db.WorkstationLeadProfiles
            .Where(x => ids.Contains(x.LeadId) && x.AgentUserId == agentId)
            .ToListAsync();

        var now = DateTime.UtcNow;
        long seed = now.Ticks * 10;
        for (int i = 0; i < ids.Count; i++)
        {
            var lead = leads.FirstOrDefault(l => l.LeadId == ids[i]);
            if (lead == null) continue;
            lead.CrmOrder = seed - i;
            PreserveOriginalLeadType(lead);
            if (normalizedBucket != null)
                lead.Bucket = normalizedBucket;
            lead.AgentUserId = agentId;
            lead.UpdatedUtc = now;
        }

        await _db.SaveChangesAsync();
        return Json(new { ok = true, updated = leads.Count });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
        public async Task<IActionResult> Import(IFormFile? file, string bucket)
        {
            if (file == null || file.Length == 0)
                return BadRequest("File required");
            if (!ProductBuckets.Contains(bucket, StringComparer.OrdinalIgnoreCase))
                return BadRequest("Invalid bucket");

            bucket = NormalizeBucket(bucket) ?? bucket;

            const long maxUploadBytes = 5 * 1024 * 1024; // 5 MB ceiling to avoid OOM
            if (file.Length > maxUploadBytes)
                return BadRequest("File too large. Limit is 5 MB.");

            string agentId;
            try { agentId = GetAgentIdOrChallenge(); }
            catch { return Challenge(); }

            var now = DateTime.UtcNow;
            var imported = 0; var updated = 0; var skipped = 0;
            var errors = new List<string>();
            var generatedPlaceholderEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var crmOrderSeed = now.Ticks * 1000L;
            var importedRowOffset = 0;

            var existingLeads = await _db.WorkstationLeadProfiles
                .Where(x => x.AgentUserId == agentId)
                .ToListAsync();

            var existingLeadByPhone = new Dictionary<string, WorkstationLeadProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var lead in existingLeads)
            {
                var key = NormalizePhoneKey(lead.Phone);
                if (!string.IsNullOrWhiteSpace(key) && !existingLeadByPhone.ContainsKey(key))
                    existingLeadByPhone[key] = lead;
            }

            var existingWorkstationEmails = await _db.WorkstationLeadProfiles
                .AsNoTracking()
                .Where(x => x.Email != null && x.Email != "")
                .Select(x => x.Email!)
                .ToListAsync();

            var existingClientEmailRows = await _db.ClientProfiles
                .AsNoTracking()
                .Select(x => new { x.Email, x.NormalizedEmail })
                .ToListAsync();

            var existingEmailSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var emailVal in existingWorkstationEmails)
            {
                var normalized = (emailVal ?? "").Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(normalized))
                    existingEmailSet.Add(normalized);
            }
            foreach (var row in existingClientEmailRows)
            {
                var normalizedField = (row.NormalizedEmail ?? "").Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(normalizedField))
                    existingEmailSet.Add(normalizedField);

                var emailField = (row.Email ?? "").Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(emailField))
                    existingEmailSet.Add(emailField);
            }

            using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream);

            static string[] ParseCsvLine(string line)
            {
                var result = new List<string>();
                if (line == null) return Array.Empty<string>();
            var sb = new System.Text.StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (ch == '\"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '\"')
                    {
                        sb.Append('\"');
                        i++; // skip escaped quote
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (ch == ',' && !inQuotes)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(ch);
                }
                }
                result.Add(sb.ToString());
                return result.ToArray();
            }

            var header = await reader.ReadLineAsync();
            if (header == null) return BadRequest("CSV empty");
            var headerCells = ParseCsvLine(header);
            var headerMap = BuildHeaderMap(headerCells);
            var hasHeader = headerMap.Count > 0;

            string GetCell(string[] cells, int index)
                => (index >= 0 && index < cells.Length) ? cells[index].Trim() : "";

            bool IsRowEmpty(string[] cells) => cells.All(c => string.IsNullOrWhiteSpace(c));

            string GeneratePlaceholderEmail(string? lastName)
            {
                var slug = NormalizePlaceholderEmailSlug(lastName);

                for (var suffix = 1; ; suffix++)
                {
                    var candidate = suffix == 1
                        ? $"no-email@{slug}.com"
                        : $"no-email@{slug}{suffix}.com";

                    if (generatedPlaceholderEmails.Contains(candidate))
                    {
                        continue;
                    }

                    if (existingEmailSet.Contains(candidate))
                    {
                        continue;
                    }

                    generatedPlaceholderEmails.Add(candidate);
                    existingEmailSet.Add(candidate);
                    return candidate;
                }
            }

            async Task HandleRowAsync(string[] cells, int rowNumber)
            {
                if (cells.Length == 0 || IsRowEmpty(cells))
                {
                    return;
                }
                var isLifeOrFinalExpense = bucket == "LifeInsurance" || bucket == "FinalExpense";
                var minimumColumns = isLifeOrFinalExpense ? 12 : 13;
                if (cells.Length < minimumColumns)
                {
                    skipped++;
                    errors.Add($"Row {rowNumber}: not enough columns (need {minimumColumns}+).");
                    return;
                }
                string? first, last, address, city, state, county, zip, age, dobRaw, gender, lender, loan, phoneRaw, phone2Raw, btc, notes, email;

                if (hasHeader)
                {
                    first     = GetField(cells, headerMap, true, 0, "firstname","first","fname");
                    last      = GetField(cells, headerMap, true, 1, "lastname","last","lname");
                    address   = GetField(cells, headerMap, true, 2, "address","street","addressline");
                    city      = GetField(cells, headerMap, true, 3, "city","town");
                    state     = GetField(cells, headerMap, true, 4, "state","st");
                    county    = GetField(cells, headerMap, true, 5, "county","parish");
                    zip       = GetField(cells, headerMap, true, 6, "zip","zipcode","postal");
                    age       = GetField(cells, headerMap, true, 7, "age");
                    dobRaw    = GetField(cells, headerMap, true, 8, "dob","birthdate","dateofbirth");
                    gender    = GetField(cells, headerMap, true, 9, "mf","gender","sex");
                    if (isLifeOrFinalExpense)
                    {
                        lender    = "";
                        loan      = GetField(cells, headerMap, true, 10, "requested","loan","loanamount","amount");
                        phoneRaw  = GetField(cells, headerMap, true, 11, "phone","phone1","primaryphone","mobile","cell");
                        phone2Raw = GetField(cells, headerMap, true, 12, "phone2","altphone","secondaryphone","mobile2","cell2");
                        btc       = GetField(cells, headerMap, true, 13, "btc","bitcoin");
                        notes     = GetField(cells, headerMap, true, 14, "notes","crmnotes","comments") ?? "";
                        email     = GetField(cells, headerMap, true, 15, "email","mail") ?? "";
                    }
                    else
                    {
                        lender    = GetField(cells, headerMap, true, 10, "lender","bank");
                        loan      = GetField(cells, headerMap, true, 11, "loan","loanamount","amount");
                        phoneRaw  = GetField(cells, headerMap, true, 12, "phone","phone1","primaryphone","mobile","cell");
                        phone2Raw = GetField(cells, headerMap, true, 13, "phone2","altphone","secondaryphone","mobile2","cell2");
                        btc       = GetField(cells, headerMap, true, 14, "btc","bitcoin");
                        notes     = GetField(cells, headerMap, true, 15, "notes","crmnotes","comments") ?? "";
                        email     = GetField(cells, headerMap, true, 16, "email","mail") ?? "";
                    }
                }
                else
                {
                    // Strict positional mapping: 0..14 as provided by user layout
                    first     = GetCell(cells, 0);
                    last      = GetCell(cells, 1);
                    address   = GetCell(cells, 2);
                    city      = GetCell(cells, 3);
                    state     = GetCell(cells, 4);
                    county    = GetCell(cells, 5);
                    zip       = GetCell(cells, 6);
                    age       = GetCell(cells, 7);
                    dobRaw    = GetCell(cells, 8);
                    gender    = GetCell(cells, 9);
                    if (isLifeOrFinalExpense)
                    {
                        lender    = "";
                        loan      = GetCell(cells, 10);
                        phoneRaw  = GetCell(cells, 11);
                        phone2Raw = GetCell(cells, 12);
                        btc       = GetCell(cells, 13);
                        // Optional trailing columns (safe defaults)
                        notes = GetCell(cells, 14);
                        email = GetCell(cells, 15);
                    }
                    else
                    {
                        lender    = GetCell(cells, 10);
                        loan      = GetCell(cells, 11);
                        phoneRaw  = GetCell(cells, 12);
                        phone2Raw = GetCell(cells, 13);
                        btc       = GetCell(cells, 14);
                        // Optional trailing columns (safe defaults)
                        notes = GetCell(cells, 15);
                        email = GetCell(cells, 16);
                    }
                }

                var phoneKey = NormalizePhoneKey(phoneRaw);
                var phone2Key = NormalizePhoneKey(phone2Raw);
                // Fallback: use Phone #2 as key if primary missing/invalid
                if (string.IsNullOrWhiteSpace(phoneKey) && !string.IsNullOrWhiteSpace(phone2Key))
                    phoneKey = phone2Key;

                if (string.IsNullOrWhiteSpace(phoneKey))
                {
                    skipped++;
                    errors.Add($"Row {rowNumber}: missing phone (and phone #2).");
                    return;
                }

                if (_featureFlags.ImportValidatorEnabled)
                {
                    var validation = _leadImportValidator.Validate(first, last, email, phoneRaw);
                    if (!validation.IsValid)
                    {
                        skipped++;
                        errors.Add($"Row {rowNumber}: {string.Join("; ", validation.Errors)}");
                        return;
                    }
                }

                DateTime? dob = null;
                if (DateTime.TryParse(dobRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dobParsed))
                    dob = dobParsed.Date;

                existingLeadByPhone.TryGetValue(phoneKey, out var existing);
                if (existing != null)
                {
                    PreserveOriginalLeadType(existing);
                    existing.FirstName = first ?? existing.FirstName;
                    existing.LastName = last ?? existing.LastName;
                    existing.Email = string.IsNullOrWhiteSpace(email) ? existing.Email : email;
                    existing.CrmNotes = string.IsNullOrWhiteSpace(notes) ? existing.CrmNotes : notes;
                    if (string.IsNullOrWhiteSpace(existing.Bucket))
                        existing.Bucket = bucket;
                    existing.AddressLine ??= address;
                    existing.City ??= city;
                    existing.State ??= state;
                    existing.County ??= county;
                    existing.ZipCode ??= zip;
                    existing.MortgageLender ??= lender;
                    existing.LoanAmount ??= loan;
                    existing.Age ??= age;
                    existing.Btc ??= btc;
                    existing.DOB ??= dob;
                    existing.Gender ??= gender;
                    existing.Phone2 = string.IsNullOrWhiteSpace(existing.Phone2) ? phone2Key : existing.Phone2;
                    existing.AgentUserId = agentId;
                    existing.UpdatedUtc = now;
                    updated++;
                    return;
                }

                string importEmail = string.IsNullOrWhiteSpace(email)
                    ? GeneratePlaceholderEmail(last)
                    : (email ?? "");

                var normalizedImportEmail = (importEmail ?? "").Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(normalizedImportEmail))
                    existingEmailSet.Add(normalizedImportEmail);

                var lead = new WorkstationLeadProfile
                {
                    LeadId = Guid.NewGuid().ToString("N"),
                    AgentUserId = agentId,
                    Bucket = bucket,
                    OriginalLeadType = bucket,
                    FirstName = first ?? "",
                    LastName = last ?? "",
                    Phone = phoneKey,
                    Phone2 = phone2Key,
                    Email = importEmail!,
                    CrmStage = "New",
                    CrmStatus = "Lead",
                    CrmOrder = crmOrderSeed - importedRowOffset++,
                    CrmNotes = notes ?? "",
                    AddressLine = address,
                    City = city,
                    State = state,
                    County = county,
                    ZipCode = zip,
                    MortgageLender = lender,
                    LoanAmount = loan,
                    Age = age,
                    Btc = btc,
                    DOB = dob,
                    Gender = gender,
                    CreatedUtc = now,
                    UpdatedUtc = now
                };
                _db.WorkstationLeadProfiles.Add(lead);
                existingLeadByPhone[phoneKey] = lead;
                imported++;
            }

            var rowNumber = hasHeader ? 2 : 1;
            if (!hasHeader)
            {
                try { await HandleRowAsync(headerCells, rowNumber); }
                catch (Exception ex) { skipped++; errors.Add($"Row {rowNumber}: {ex.Message}"); }
                rowNumber++;
            }

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                try { await HandleRowAsync(ParseCsvLine(line), rowNumber); }
                catch (Exception ex) { skipped++; errors.Add($"Row {rowNumber}: {ex.Message}"); }
                finally { rowNumber++; }
            }

            await _db.SaveChangesAsync();
            return Json(new { imported, updated, skipped, errors = errors.Take(5).ToArray(), bucket });
        }

    public record CreateLeadActionRequest
    {
        public string LeadId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public ActionPriority Priority { get; set; } = ActionPriority.P2;
        public bool ShowInCommandCenter { get; set; }
        // Backward compatibility for stale cached clients posting older field names.
        public bool ShowInDashboard { get; set; }
        public bool IncludeInDashboard { get; set; }
    }

    public record CreateCommitmentRequest
    {
        public string LeadId { get; set; } = string.Empty;
        public string PromiseText { get; set; } = string.Empty;
        public DateTimeOffset? DueDateUtc { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> Actions(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return BadRequest("Lead id required");
        string agentId;
        try { agentId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        if (!await AgentOwnsLeadAsync(agentId, id))
            return Forbid();

        var actions = await _execution.GetByRelatedAsync(RelatedEntityType.Lead, id, agentId);
        ViewBag.LeadId = id;
        return PartialView("~/Views/Leads/_ActionsTab.cshtml", actions);
    }

    [HttpGet]
    public async Task<IActionResult> Commitments(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return BadRequest("Lead id required");
        string agentId;
        try { agentId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        if (!await AgentOwnsLeadAsync(agentId, id))
            return Forbid();

        try
        {
            var commitments = await _commitments.GetByEntityForActorAsync(RelatedEntityType.Lead, id, agentId);
            ViewBag.LeadId = id;
            ViewBag.AgentId = agentId;
            return PartialView("~/Views/Leads/_CommitmentsTab.cshtml", commitments);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load commitments for lead {LeadId}", id);
            ViewBag.LeadId = id;
            ViewBag.AgentId = agentId;
            ViewBag.CommitmentsError = CommitmentsUnavailableMessage;
            return PartialView("~/Views/Leads/_CommitmentsTab.cshtml", Enumerable.Empty<Commitment>());
        }
    }

    [HttpPost]
    public async Task<IActionResult> CreateAction([FromForm] CreateLeadActionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LeadId) || string.IsNullOrWhiteSpace(req.Title))
            return BadRequest("LeadId and Title required");

        string ownerId;
        try { ownerId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        if (!await AgentOwnsLeadAsync(ownerId, req.LeadId))
            return Forbid();

        var action = BuildLeadAction(req, ownerId);

        await _execution.CreateActionAsync(action);
        return RedirectToAction(nameof(Actions), new { id = req.LeadId });
    }

    [HttpPost]
    public async Task<IActionResult> CreateCommitment([FromForm] CreateCommitmentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LeadId) || string.IsNullOrWhiteSpace(req.PromiseText))
            return BadRequest("LeadId and Promise are required");

        if (req.DueDateUtc == null)
            return BadRequest("Due date is required");

        string agentId;
        try { agentId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        if (!await AgentOwnsLeadAsync(agentId, req.LeadId))
            return Forbid();

        var createRequest = new CommitmentCreateRequest(
            RelatedEntityType.Lead,
            req.LeadId.Trim(),
            ActionOwnerType.Agent,
            agentId,
            ActionOwnerType.Client,
            req.LeadId.Trim(),
            req.PromiseText.Trim(),
            req.DueDateUtc.Value.ToUniversalTime(),
            agentId
        );

        try
        {
            await _commitments.CreateCommitmentAsync(createRequest);
            return RedirectToAction(nameof(Commitments), new { id = req.LeadId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create commitment for lead {LeadId}", req.LeadId);
            ViewBag.LeadId = req.LeadId;
            ViewBag.AgentId = agentId;
            ViewBag.CommitmentsError = CommitmentsUnavailableMessage;
            return PartialView("~/Views/Leads/_CommitmentsTab.cshtml", Enumerable.Empty<Commitment>());
        }
    }

    private static ActionItem BuildLeadAction(CreateLeadActionRequest req, string ownerId)
        => new ActionItem
        {
            RelatedEntityType = RelatedEntityType.Lead,
            RelatedEntityId = req.LeadId.Trim(),
            Title = req.Title.Trim(),
            Description = req.Description?.Trim() ?? string.Empty,
            OwnerType = ActionOwnerType.Agent,
            OwnerId = ownerId,
            EffectiveAgentOid = ownerId,
            DueDateUtc = req.DueDateUtc,
            Status = ActionStatus.Planned,
            Priority = req.Priority,
            ActionSurface = (req.ShowInCommandCenter || req.ShowInDashboard || req.IncludeInDashboard)
                ? ActionSurface.CommandCenter
                : ActionSurface.CrmOnly,
            Source = "lead-manual",
            SourceRef = $"{req.LeadId}-manual",
            CreatedBy = ownerId,
            CreatedUtc = DateTime.UtcNow
        };

    [HttpPost]
    public async Task<IActionResult> FulfillCommitment(Guid id)
    {
        if (id == Guid.Empty) return BadRequest("Commitment id required");

        string actorId;
        try { actorId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        try
        {
            var commit = await _commitments.GetByIdForActorAsync(id, actorId);
            if (commit == null) return NotFound();
            if (commit.RelatedEntityType != RelatedEntityType.Lead) return BadRequest("Only lead commitments are supported here.");

            var updated = await _commitments.FulfillCommitmentAsync(id, actorId);
            if (updated == null) return NotFound();

            var refreshed = await _commitments.GetByEntityForActorAsync(RelatedEntityType.Lead, commit.RelatedEntityId, actorId);
            ViewBag.LeadId = commit.RelatedEntityId;
            ViewBag.AgentId = actorId;
            return PartialView("~/Views/Leads/_CommitmentsTab.cshtml", refreshed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fulfill commitment {CommitmentId}", id);
            ViewBag.LeadId = id;
            ViewBag.AgentId = actorId;
            ViewBag.CommitmentsError = CommitmentsUnavailableMessage;
            return PartialView("~/Views/Leads/_CommitmentsTab.cshtml", Enumerable.Empty<Commitment>());
        }
    }

    [HttpPost]
    public async Task<IActionResult> BreakCommitment(Guid id)
    {
        if (id == Guid.Empty) return BadRequest("Commitment id required");

        string actorId;
        try { actorId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        try
        {
            var commit = await _commitments.GetByIdForActorAsync(id, actorId);
            if (commit == null) return NotFound();
            if (commit.RelatedEntityType != RelatedEntityType.Lead) return BadRequest("Only lead commitments are supported here.");

            var updated = await _commitments.BreakCommitmentAsync(id, actorId);
            if (updated == null) return NotFound();

            var refreshed = await _commitments.GetByEntityForActorAsync(RelatedEntityType.Lead, commit.RelatedEntityId, actorId);
            ViewBag.LeadId = commit.RelatedEntityId;
            ViewBag.AgentId = actorId;
            return PartialView("~/Views/Leads/_CommitmentsTab.cshtml", refreshed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to break commitment {CommitmentId}", id);
            ViewBag.LeadId = id;
            ViewBag.AgentId = actorId;
            ViewBag.CommitmentsError = CommitmentsUnavailableMessage;
            return PartialView("~/Views/Leads/_CommitmentsTab.cshtml", Enumerable.Empty<Commitment>());
        }
    }
}
