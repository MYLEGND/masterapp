using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgentPortal.Helpers;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Leads;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
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
    private readonly MetaSignalCrmOutcomeService _metaSignalOutcomes;
    private const string CommitmentsUnavailableMessage = "Commitments are not live yet in this environment. Apply the latest migrations to enable them.";
    private static readonly string[] ProductBuckets = WorkstationLeadBuckets.ProductBuckets;

    private static readonly string[] PipelineStages =
    {
        WorkstationLeadBuckets.MortgageProtection,
        WorkstationLeadBuckets.LifeInsurance,
        WorkstationLeadBuckets.TermLife,
        WorkstationLeadBuckets.WholeLife,
        WorkstationLeadBuckets.Iul,
        WorkstationLeadBuckets.FinalExpense,
        WorkstationLeadBuckets.DisabilityInsurance,
        WorkstationLeadBuckets.AutoInsurance,
        WorkstationLeadBuckets.HomeInsurance,
        WorkstationLeadBuckets.HealthInsurance,
        WorkstationLeadBuckets.CommercialInsurance,
        "CallBack",
        "Contacted",
        "Booked",
        "FollowUp",
        "NeedsDocs",
        "PolicyPlaced",
        "Voicemail",
        "NotInterested",
        "Nurture",
        "NoAnswer",
        "Lost",
        "AIReception",
        "DoNotCallList"
    };

    private static readonly IReadOnlyDictionary<string, string> PipelineStageMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["mortgageprotection"] = WorkstationLeadBuckets.MortgageProtection,
        ["mortgageprotectionleads"] = WorkstationLeadBuckets.MortgageProtection,
        ["mortgageprotectionrebuttals"] = WorkstationLeadBuckets.MortgageProtection,
        ["lifeinsurance"] = WorkstationLeadBuckets.LifeInsurance,
        ["lifeinsuranceleads"] = WorkstationLeadBuckets.LifeInsurance,
        ["lifeinsurancerebuttals"] = WorkstationLeadBuckets.LifeInsurance,
        ["termlife"] = WorkstationLeadBuckets.TermLife,
        ["termlifeleads"] = WorkstationLeadBuckets.TermLife,
        ["termliferebuttals"] = WorkstationLeadBuckets.TermLife,
        ["wholelife"] = WorkstationLeadBuckets.WholeLife,
        ["wholelifeleads"] = WorkstationLeadBuckets.WholeLife,
        ["wholeliferebuttals"] = WorkstationLeadBuckets.WholeLife,
        ["iul"] = WorkstationLeadBuckets.Iul,
        ["iulleads"] = WorkstationLeadBuckets.Iul,
        ["iulrebuttals"] = WorkstationLeadBuckets.Iul,
        ["indexeduniversallife"] = WorkstationLeadBuckets.Iul,
        ["indexeduniversallifeleads"] = WorkstationLeadBuckets.Iul,
        ["indexeduniversalliferebuttals"] = WorkstationLeadBuckets.Iul,
        ["finalexpense"] = WorkstationLeadBuckets.FinalExpense,
        ["finalexpenseleads"] = WorkstationLeadBuckets.FinalExpense,
        ["finalexpenserebuttals"] = WorkstationLeadBuckets.FinalExpense,
        ["medicare"] = WorkstationLeadBuckets.MortgageProtection,
        ["disabilityinsurance"] = WorkstationLeadBuckets.DisabilityInsurance,
        ["disabilityinsuranceleads"] = WorkstationLeadBuckets.DisabilityInsurance,
        ["disabilityinsurancerebuttals"] = WorkstationLeadBuckets.DisabilityInsurance,
        ["autoinsurance"] = WorkstationLeadBuckets.AutoInsurance,
        ["autoinsuranceleads"] = WorkstationLeadBuckets.AutoInsurance,
        ["homeinsurance"] = WorkstationLeadBuckets.HomeInsurance,
        ["homeinsuranceleads"] = WorkstationLeadBuckets.HomeInsurance,
        ["healthinsurance"] = WorkstationLeadBuckets.HealthInsurance,
        ["healthinsuranceleads"] = WorkstationLeadBuckets.HealthInsurance,
        ["commercialinsurance"] = WorkstationLeadBuckets.CommercialInsurance,
        ["commercialinsuranceleads"] = WorkstationLeadBuckets.CommercialInsurance,
        ["callback"] = "CallBack",
        ["donotcall"] = "DoNotCallList",
        ["donotcalllist"] = "DoNotCallList",
        ["dnc"] = "DoNotCallList",
        ["contacted"] = "Contacted",
        ["booked"] = "Booked",
        ["followup"] = "FollowUp",
        ["needsdocs"] = "NeedsDocs",
        ["policyplaced"] = "PolicyPlaced",
        ["voicemail"] = "Voicemail",
        ["leftvm"] = "Voicemail",
        ["leftvoicemail"] = "Voicemail",
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
        ["Voicemail"] = "Voicemail",
        ["LeftVM"] = "Voicemail",
        ["NotInterested"] = "NotInterested",
        ["Nurture"] = "Nurture",
        ["NoAnswer"] = "NoAnswer",
        ["Lost"] = "Lost",
        ["AIReception"] = "AIReception",
        ["CallBack"] = "CallBack",
        ["DoNotCallList"] = "DoNotCallList"
    };

    private static string? ResolveOriginalLeadType(string? originalLeadType, string? currentBucket, string? fallbackBucket = null)
        => NormalizeBucket(originalLeadType)
           ?? NormalizeBucket(currentBucket)
           ?? fallbackBucket;

    private static bool IsDerivedPipelineFilterStage(string? stage)
        => string.Equals(stage, "CalledToday", StringComparison.OrdinalIgnoreCase);

    private static string ResolveEffectivePipelineStage(WorkstationLeadProfile lead, string? fallbackStage = null)
    {
        var bucketStage = NormalizePipelineStage(lead.Bucket);
        if (!string.IsNullOrWhiteSpace(bucketStage) && !IsDerivedPipelineFilterStage(bucketStage))
            return bucketStage;

        var crmStage = NormalizePipelineStage(lead.CrmStage);
        if (!string.IsNullOrWhiteSpace(crmStage) && !IsDerivedPipelineFilterStage(crmStage))
            return crmStage;

        if (IsDerivedPipelineFilterStage(lead.Bucket) || IsDerivedPipelineFilterStage(lead.CrmStage))
            return ResolveOriginalLeadType(lead.OriginalLeadType, lead.Bucket, fallbackStage) ?? fallbackStage ?? "MortgageProtection";

        return fallbackStage
            ?? ResolveOriginalLeadType(lead.OriginalLeadType, lead.Bucket)
            ?? "Contacted";
    }

    private static void PreserveOriginalLeadType(WorkstationLeadProfile lead)
    {
        var resolved = ResolveOriginalLeadType(lead.OriginalLeadType, lead.Bucket);
        if (!string.IsNullOrWhiteSpace(resolved))
            lead.OriginalLeadType = resolved;
    }

    private static string? NormalizeBucket(string? bucket)
        => WorkstationLeadBuckets.NormalizeBucket(bucket);

    private static string[] ExpandProductBucketValues(string normalizedBucket)
        => WorkstationLeadBuckets.ExpandProductBucketValues(normalizedBucket);

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

    public LeadsController(MasterAppDbContext db, IAgentTimeZoneResolver agentTimeZoneResolver, ProductionService production, EffectiveAgentContext agentContext, IExecutionEngine execution, ICommitmentService commitments, ILogger<LeadsController> logger, Microsoft.Extensions.Options.IOptions<AgentPortal.Models.AppFeatureFlags> featureFlags, AgentPortal.Services.ImportValidation.LeadImportValidator leadImportValidator, MetaSignalCrmOutcomeService metaSignalOutcomes)
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
        _metaSignalOutcomes = metaSignalOutcomes;
    }

    // Centralized canonical selector to avoid drift across endpoints.
    private async Task<WorkstationLeadProfile?> LoadCanonicalLeadAsync(string agentId, string leadId, string context)
    {
        var rows = await _db.WorkstationLeadProfiles
            .Where(x => x.AgentUserId == agentId && x.LeadId == leadId)
            .ToListAsync();
        return LeadCanonicalizer.SelectCanonical(rows, _logger, context);
    }

    private async Task<int> DeleteLeadProfilesAsync(IEnumerable<WorkstationLeadProfile> leads)
    {
        var ownedLeads = leads
            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.LeadId))
            .GroupBy(x => x.LeadId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.UpdatedUtc).First())
            .ToList();

        if (ownedLeads.Count == 0)
            return 0;

        var leadIds = ownedLeads
            .Select(x => x.LeadId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (leadIds.Length == 0)
            return 0;

        var appointments = await _db.LeadAppointments
            .Where(x => leadIds.Contains(x.WorkstationLeadId))
            .ToListAsync();
        if (appointments.Count != 0)
            _db.LeadAppointments.RemoveRange(appointments);

        var intakeLinks = await _db.WebsiteLeadIntakeLinks
            .Where(x => leadIds.Contains(x.WorkstationLeadId))
            .ToListAsync();
        if (intakeLinks.Count != 0)
            _db.WebsiteLeadIntakeLinks.RemoveRange(intakeLinks);

        _db.WorkstationLeadProfiles.RemoveRange(ownedLeads);
        await _db.SaveChangesAsync();
        return ownedLeads.Count;
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
                .Select(x => new
                {
                    Lead = x,
                    Paid = 0m, // placeholder; filled from lookup below
                    Personal = 0m // placeholder; filled from lookup below
                })
                .ToListAsync();

            // Canonicalize by LeadId in case older duplicates surface.
            // Use the canonical entity directly — do NOT re-look-up from leadsRaw by CrmOrder,
            // which could return a different (stale) duplicate with incorrect dial counts.
            var leads = LeadCanonicalizer.Canonicalize(leadsRaw.Select(r => r.Lead), _logger, "Leads/Index preload")
                .OrderByDescending(WorkstationLeadOrder.ResolveSortValue)
                .Select(c => new { Lead = c, Paid = 0m, Personal = 0m })
                .ToList();

            var leadIds = leads.Select(l => l.Lead.LeadId).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();

            var productionLookup = leadIds.Count > 0
                ? await _production.GetContactSnapshotsAsync(agentId, ProductionSide.Lead, leadIds, HttpContext.RequestAborted)
                : new Dictionary<string, ProductionContactSnapshot>(StringComparer.OrdinalIgnoreCase);

            var intakeSummaryLookup = await LoadLeadIntakeSummariesAsync(leadIds);
            var appointmentSummaries = await LoadLeadAppointmentSummariesAsync(leadIds, HttpContext.RequestAborted);
            ViewData["ProductionTotals"] = await _production.GetAgentTotalsAsync(agentId, ProductionSide.Lead);

            var vm = leads.Select(l =>
            {
                var lead = l.Lead;
                var stage = ResolveEffectivePipelineStage(lead, "Contacted");
                var attempts = CrmAttemptTracking.GetLeadAttemptCounts(lead, nowUtc, dialTimeZone);
                var originalLeadType = ResolveOriginalLeadType(lead.OriginalLeadType, lead.Bucket);
                var crmMeta = ReadLeadMeta(lead);
                var contactStatus = ClientCrmMetaSerializer.NormalizeContactStatus(crmMeta.ContactStatus);
                var stageEnteredUtc = crmMeta.StageEnteredUtc == default ? lead.CreatedUtc : crmMeta.StageEnteredUtc;
                intakeSummaryLookup.TryGetValue(lead.LeadId, out var intakeSummary);
                appointmentSummaries.TryGetValue(lead.LeadId, out var appointmentSummary);
                var origin = intakeSummary == null
                    ? ResolveLeadOriginInfo(null, null, null, null, null, null, hasWebsiteIntake: false, email: lead.Email)
                    : ResolveLeadOriginInfo(
                        intakeSummary.Latest.PageMode,
                        intakeSummary.Latest.UtmMedium,
                        intakeSummary.Latest.Fbclid,
                        intakeSummary.Latest.MetaCampaignId,
                        intakeSummary.Latest.MetaAdSetId,
                        intakeSummary.Latest.MetaAdId,
                        hasWebsiteIntake: true,
                        email: lead.Email);
                var productInterest = intakeSummary == null
                    ? ResolveBucketDisplayLabel(originalLeadType ?? stage)
                    : ResolveIntakeInterestLabel(intakeSummary.Latest.InterestType, intakeSummary.Latest.OfferKey, intakeSummary.Latest.ProductType);
                var quoteType = intakeSummary == null
                    ? ResolveBucketDisplayLabel(originalLeadType ?? stage)
                    : ResolveIntakeQuoteTypeLabel(intakeSummary.Latest.OfferKey, intakeSummary.Latest.ProductType);
                var recommendationSummary = intakeSummary == null
                    ? string.Empty
                    : BuildRecommendationSummary(
                        intakeSummary.Latest.EstimateSummary,
                        intakeSummary.Latest.RecommendationPrimaryTitle,
                        intakeSummary.Latest.RecommendationSecondaryTitle);
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
                    ContactStatus = contactStatus,
                    PipelineStage = stage,
                    PipelineOrder = lead.CrmOrder,
                    MeetingLocation = crmMeta.MeetingLocation,
                    ZoomJoinUrl = crmMeta.ZoomJoinUrl,
                    UsePersonalZoomLink = crmMeta.UsePersonalZoomLink,
                    MeetingTime = crmMeta.MeetingTime,
                    MeetingDurationMinutes = crmMeta.MeetingDurationMinutes,
                    LatestAppointmentStatus = appointmentSummary?.Latest?.Status.ToString(),
                    LatestAppointmentStatusLabel = appointmentSummary?.Latest == null ? null : HumanizeAppointmentStatus(appointmentSummary.Latest.Status),
                    LatestAppointmentConfirmationStateLabel = appointmentSummary?.Latest == null ? null : BuildAppointmentConfirmationStateLabel(appointmentSummary.Latest),
                    LatestAppointmentScheduledStartUtc = appointmentSummary?.Latest?.ScheduledStartUtc,
                    LatestAppointmentScheduledEndUtc = appointmentSummary?.Latest?.ScheduledEndUtc,
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
                    LatestSubmissionUtc = intakeSummary?.Latest.SubmittedUtc,
                    IntakeHistoryCount = intakeSummary?.HistoryCount ?? 0,
                    LeadOriginLabel = origin.Label,
                    LeadOriginTone = origin.Tone,
                    ProductInterestLabel = productInterest,
                    QuoteTypeLabel = quoteType,
                    AttributionSource = intakeSummary?.Latest.UtmSource,
                    AttributionMedium = intakeSummary?.Latest.UtmMedium,
                    AttributionCampaign = intakeSummary?.Latest.UtmCampaign,
                    LatestRecommendationSummary = recommendationSummary,
                    IntakePageVariant = intakeSummary?.Latest.PageVariant,
                    IntakePageMode = intakeSummary?.Latest.PageMode,
                    PaidAmount = prod?.Paid ?? 0,
                    PersonalAmount = prod?.Personal ?? 0,
                    ProductionStatus = prod?.Status.ToString() ?? "",
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

    private static string ResolveLeadAge(DateTime? dob, string? existingAge, DateTime utcNow, TimeZoneInfo? timeZone = null)
    {
        if (!dob.HasValue)
            return Clean(existingAge) ?? "";

        var localToday = timeZone is null
            ? utcNow.Date
            : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), timeZone).Date;

        var dobDate = dob.Value.Date;
        var age = localToday.Year - dobDate.Year;
        if (dobDate > localToday.AddYears(-age))
            age--;

        return age >= 0
            ? age.ToString(CultureInfo.InvariantCulture)
            : (Clean(existingAge) ?? "");
    }

    private static string NormalizeStateValue(string? state)
        => (state ?? "").Trim().ToUpperInvariant();

    private sealed record LeadOriginInfo(string Label, string Tone);
    private sealed record LeadIntakeListRow
    {
        public string WorkstationLeadId { get; init; } = "";
        public string? AgentUserId { get; init; }
        public string? Bucket { get; init; }
        public DateTime SubmittedUtc { get; init; }
        public DateTime CapturedUtc { get; init; }
        public string? SourcePageKey { get; init; }
        public string? SourceCtaKey { get; init; }
        public string? PageMode { get; init; }
        public string? PageVariant { get; init; }
        public string? PagePath { get; init; }
        public string? LandingPageUrl { get; init; }
        public string? ReferrerUrl { get; init; }
        public string? InterestType { get; init; }
        public string? OfferKey { get; init; }
        public string? ProductType { get; init; }
        public string? UtmSource { get; init; }
        public string? UtmMedium { get; init; }
        public string? UtmCampaign { get; init; }
        public string? UtmId { get; init; }
        public string? UtmTerm { get; init; }
        public string? UtmContent { get; init; }
        public string? EstimateSummary { get; init; }
        public string? RecommendationPrimaryKey { get; init; }
        public string? RecommendationPrimaryTitle { get; init; }
        public string? RecommendationSecondaryKey { get; init; }
        public string? RecommendationSecondaryTitle { get; init; }
        public string? SessionId { get; init; }
        public string? VisitorId { get; init; }
        public string? DiscoverySummaryJson { get; init; }
        public string? SnapshotJson { get; init; }
        public string? Fbclid { get; init; }
        public string? MetaCampaignId { get; init; }
        public string? MetaAdSetId { get; init; }
        public string? MetaAdId { get; init; }
    }
    private sealed record LeadIntakeSummary(LeadIntakeListRow Latest, int HistoryCount);
    private sealed record LeadAppointmentListRow
    {
        public Guid Id { get; init; }
        public string WorkstationLeadId { get; init; } = "";
        public string OwnerAgentUserId { get; init; } = "";
        public Guid? WebsiteLeadIntakeLinkId { get; init; }
        public LeadAppointmentStatus Status { get; init; }
        public string BookingSource { get; init; } = LeadAppointmentBookingSources.InternalManual;
        public string RequestedBookingSource { get; init; } = LeadAppointmentBookingSources.InternalManual;
        public string? ConfirmationSource { get; init; }
        public string? BookingConfigurationSource { get; init; }
        public Guid? BookingTrackingProfileId { get; init; }
        public string? BookingAgentSlug { get; init; }
        public string? BookingAgentUserId { get; init; }
        public string? BookingCalendarUserId { get; init; }
        public string? BookingCalendarEmail { get; init; }
        public string? BookingPageIdOrMailbox { get; init; }
        public string? CalendarEventId { get; init; }
        public string? CalendarEventWebLink { get; init; }
        public DateTime? ScheduledStartUtc { get; init; }
        public DateTime? ScheduledEndUtc { get; init; }
        public string? MeetingUrl { get; init; }
        public DateTime CreatedUtc { get; init; }
        public DateTime UpdatedUtc { get; init; }
        public DateTime? LastStatusChangedUtc { get; init; }
        public DateTime? RequestedUtc { get; init; }
        public DateTime? BookedUtc { get; init; }
        public DateTime? ConfirmedUtc { get; init; }
        public DateTime? CompletedUtc { get; init; }
        public DateTime? NoShowUtc { get; init; }
        public DateTime? CancelledUtc { get; init; }
        public DateTime? RescheduledUtc { get; init; }
    }
    private sealed record LeadAppointmentSummary(LeadAppointmentListRow Latest);

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

    private static bool LooksLikePlaceholderEmail(string? email)
    {
        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return (normalized.StartsWith("no-email@", StringComparison.OrdinalIgnoreCase) && normalized.EndsWith(".com", StringComparison.OrdinalIgnoreCase))
            || (normalized.StartsWith("lead-", StringComparison.OrdinalIgnoreCase) && normalized.EndsWith("@scripts.local", StringComparison.OrdinalIgnoreCase))
            || normalized.EndsWith("@leads.local", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveBucketDisplayLabel(string? bucket)
        => NormalizeBucket(bucket) switch
        {
            "MortgageProtection" => "Mortgage Protection",
            "LifeInsurance" => "Life Insurance",
            "TermLife" => "Term Life",
            "WholeLife" => "Whole Life",
            "IUL" => "IUL",
            "FinalExpense" => "Final Expense",
            "DisabilityInsurance" => "Disability Insurance",
            "AutoInsurance" => "Auto Insurance",
            "HomeInsurance" => "Home Insurance",
            "HealthInsurance" => "Health Insurance",
            "CommercialInsurance" => "Commercial Insurance",
            _ => string.IsNullOrWhiteSpace(bucket) ? "Lead" : bucket.Trim()
        };

    private static string ResolveIntakeQuoteTypeLabel(string? offerKey, string? productType)
    {
        var key = Norm(offerKey);
        if (string.IsNullOrWhiteSpace(key))
            key = Norm(productType);

        return key switch
        {
            "life" or "lifegeneral" => "Life",
            "term" or "termlife" or "lifeterm" => "Term Life",
            "wholelife" or "lifewhole" => "Whole Life",
            "finalexpense" or "lifefinalexpense" => "Final Expense",
            "mortgage" or "lifemp" => "Mortgage Protection",
            "iul" or "lifeiul" => "IUL",
            "autoinsurance" or "auto" => "Auto Insurance",
            "homeinsurance" or "home" => "Home Insurance",
            "commercialinsurance" or "commercial" => "Commercial Insurance",
            "disabilityinsurance" or "disability" => "Disability Insurance",
            "healthinsurance" or "health" => "Health Insurance",
            _ => string.IsNullOrWhiteSpace(key) ? string.Empty : key
        };
    }

    private static LeadOriginInfo ResolveLeadOriginInfo(
        string? pageMode,
        string? utmMedium,
        string? fbclid,
        string? metaCampaignId,
        string? metaAdSetId,
        string? metaAdId,
        bool hasWebsiteIntake,
        string? email)
    {
        if (hasWebsiteIntake)
        {
            var normalizedMedium = Norm(utmMedium);
            var isPaidMedium = normalizedMedium is "cpc" or "ppc" or "paid" or "paidsocial" or "paid_social" or "display";
            var hasPaidSignals =
                string.Equals(pageMode, "paid_landing", StringComparison.OrdinalIgnoreCase)
                || isPaidMedium
                || !string.IsNullOrWhiteSpace(fbclid)
                || !string.IsNullOrWhiteSpace(metaCampaignId)
                || !string.IsNullOrWhiteSpace(metaAdSetId)
                || !string.IsNullOrWhiteSpace(metaAdId);

            return hasPaidSignals
                ? new LeadOriginInfo("Paid Landing", "paid")
                : new LeadOriginInfo("Website Intake", "site");
        }

        if (LooksLikePlaceholderEmail(email))
            return new LeadOriginInfo("Imported Lead", "import");

        return new LeadOriginInfo("Manual Lead", "manual");
    }

    private static string BuildRecommendationSummary(string? estimateSummary, string? primaryTitle, string? secondaryTitle)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(estimateSummary))
            parts.Add(estimateSummary.Trim());
        if (!string.IsNullOrWhiteSpace(primaryTitle))
            parts.Add($"Primary: {primaryTitle.Trim()}");
        if (!string.IsNullOrWhiteSpace(secondaryTitle))
            parts.Add($"Secondary: {secondaryTitle.Trim()}");
        return string.Join(" • ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string HumanizeAppointmentStatus(LeadAppointmentStatus status)
    {
        return status switch
        {
            LeadAppointmentStatus.NoShow => "No Show",
            _ => status.ToString()
        };
    }

    private static string HumanizeAppointmentSource(string? source)
    {
        var normalized = (source ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            LeadAppointmentBookingSources.InternalManual => "Internal manual",
            LeadAppointmentBookingSources.InternalCalendar => "Internal calendar",
            LeadAppointmentBookingSources.WebsiteEmbed => "Website embed",
            LeadAppointmentBookingSources.WebsiteModal => "Website modal",
            LeadAppointmentBookingSources.ExternalRedirectFallback => "External redirect fallback",
            LeadAppointmentBookingSources.MicrosoftGraphConfirmation => "Microsoft Graph confirmation",
            LeadAppointmentBookingSources.ManualVerified => "Manual verified",
            _ when string.IsNullOrWhiteSpace(source) => "Unknown source",
            _ => source!.Trim().Replace('_', ' ')
        };
    }

    private static string HumanizeBookingConfigurationSource(string? source)
    {
        var normalized = (source ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "agent_profile" => "Agent profile",
            "slug_override" => "Slug override",
            "global_fallback" => "Global fallback",
            _ when string.IsNullOrWhiteSpace(source) => "Not recorded",
            _ => source!.Trim().Replace('_', ' ')
        };
    }

    private static bool IsTrustedAppointment(LeadAppointmentListRow appointment)
    {
        var trustedSource = appointment.ConfirmationSource ?? appointment.BookingSource;
        return appointment.Status is LeadAppointmentStatus.Booked or LeadAppointmentStatus.Confirmed or LeadAppointmentStatus.Completed or LeadAppointmentStatus.Rescheduled &&
            (string.Equals(trustedSource, LeadAppointmentBookingSources.InternalCalendar, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(trustedSource, LeadAppointmentBookingSources.MicrosoftGraphConfirmation, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(trustedSource, "microsoft_graph_webhook", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(trustedSource, LeadAppointmentBookingSources.ManualVerified, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildBookingConfigurationLabel(LeadAppointmentListRow appointment)
    {
        var parts = new List<string>();
        var sourceLabel = HumanizeBookingConfigurationSource(appointment.BookingConfigurationSource);
        if (!string.Equals(sourceLabel, "Not recorded", StringComparison.OrdinalIgnoreCase))
            parts.Add(sourceLabel);
        if (!string.IsNullOrWhiteSpace(appointment.BookingAgentSlug))
            parts.Add($"slug {appointment.BookingAgentSlug.Trim()}");
        if (!string.IsNullOrWhiteSpace(appointment.BookingCalendarEmail))
            parts.Add(appointment.BookingCalendarEmail.Trim());
        else if (!string.IsNullOrWhiteSpace(appointment.BookingPageIdOrMailbox))
            parts.Add(appointment.BookingPageIdOrMailbox.Trim());
        return parts.Count == 0
            ? (string.Equals(appointment.BookingSource, LeadAppointmentBookingSources.InternalCalendar, StringComparison.OrdinalIgnoreCase)
                ? "Internal calendar path"
                : "Not recorded")
            : string.Join(" • ", parts);
    }

    private static string BuildAppointmentConfirmationStateLabel(LeadAppointmentListRow appointment)
    {
        if (IsTrustedAppointment(appointment))
            return "Booked / verified";
        if (appointment.Status == LeadAppointmentStatus.Requested)
            return "Requested / awaiting verification";
        if (appointment.Status is LeadAppointmentStatus.Booked or LeadAppointmentStatus.Confirmed or LeadAppointmentStatus.Completed or LeadAppointmentStatus.Rescheduled)
            return $"{HumanizeAppointmentStatus(appointment.Status)} / source not verified";
        return HumanizeAppointmentStatus(appointment.Status);
    }

    private static DateTime? ResolveAppointmentStatusTimestamp(LeadAppointmentListRow appointment)
    {
        return appointment.Status switch
        {
            LeadAppointmentStatus.Requested => appointment.RequestedUtc,
            LeadAppointmentStatus.Booked => appointment.BookedUtc,
            LeadAppointmentStatus.Confirmed => appointment.ConfirmedUtc,
            LeadAppointmentStatus.Completed => appointment.CompletedUtc,
            LeadAppointmentStatus.NoShow => appointment.NoShowUtc,
            LeadAppointmentStatus.Cancelled => appointment.CancelledUtc,
            LeadAppointmentStatus.Rescheduled => appointment.RescheduledUtc,
            _ => appointment.LastStatusChangedUtc
        };
    }

    private static string FormatRawMetadataJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return raw.Trim();
        }
    }

    private static bool IsMissingWebsiteLeadIntakeLinksTable(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqliteEx &&
                sqliteEx.SqliteErrorCode == 1 &&
                sqliteEx.Message.Contains("WebsiteLeadIntakeLinks", StringComparison.OrdinalIgnoreCase) &&
                sqliteEx.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (current.Message.Contains("WebsiteLeadIntakeLinks", StringComparison.OrdinalIgnoreCase) &&
                (current.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("invalid object name", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMissingLeadAppointmentsTable(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqliteEx &&
                sqliteEx.SqliteErrorCode == 1 &&
                sqliteEx.Message.Contains("LeadAppointments", StringComparison.OrdinalIgnoreCase) &&
                sqliteEx.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (current.Message.Contains("LeadAppointments", StringComparison.OrdinalIgnoreCase) &&
                (current.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("invalid object name", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<Dictionary<string, LeadIntakeSummary>> LoadLeadIntakeSummariesAsync(IEnumerable<string> leadIds, CancellationToken ct = default)
    {
        var ids = leadIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .SelectMany(id =>
            {
                var key = id.Trim();
                var noDash = key.Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase);
                return new[] { key, noDash };
            })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<string, LeadIntakeSummary>(StringComparer.OrdinalIgnoreCase);

        List<LeadIntakeListRow> rows;
        try
        {
            rows = await _db.WebsiteLeadIntakeLinks
                .AsNoTracking()
                .Where(x => ids.Contains(x.WorkstationLeadId))
                .OrderByDescending(x => x.SubmittedUtc)
                .ThenByDescending(x => x.CapturedUtc)
                .Select(x => new LeadIntakeListRow
                {
                    WorkstationLeadId = x.WorkstationLeadId,
                    AgentUserId = x.AgentUserId,
                    Bucket = x.Bucket,
                    SubmittedUtc = x.SubmittedUtc,
                    CapturedUtc = x.CapturedUtc,
                    SourcePageKey = x.SourcePageKey,
                    SourceCtaKey = x.SourceCtaKey,
                    PageMode = x.PageMode,
                    PageVariant = x.PageVariant,
                    PagePath = x.PagePath,
                    LandingPageUrl = x.LandingPageUrl,
                    ReferrerUrl = x.ReferrerUrl,
                    InterestType = x.InterestType,
                    OfferKey = x.OfferKey,
                    ProductType = x.ProductType,
                    UtmSource = x.UtmSource,
                    UtmMedium = x.UtmMedium,
                    UtmCampaign = x.UtmCampaign,
                    UtmId = x.UtmId,
                    UtmTerm = x.UtmTerm,
                    UtmContent = x.UtmContent,
                    EstimateSummary = x.EstimateSummary,
                    RecommendationPrimaryKey = x.RecommendationPrimaryKey,
                    RecommendationPrimaryTitle = x.RecommendationPrimaryTitle,
                    RecommendationSecondaryKey = x.RecommendationSecondaryKey,
                    RecommendationSecondaryTitle = x.RecommendationSecondaryTitle,
                    SessionId = x.SessionId,
                    VisitorId = x.VisitorId,
                    DiscoverySummaryJson = x.DiscoverySummaryJson,
                    SnapshotJson = x.SnapshotJson,
                    Fbclid = x.Fbclid,
                    MetaCampaignId = x.MetaCampaignId,
                    MetaAdSetId = x.MetaAdSetId,
                    MetaAdId = x.MetaAdId
                })
                .ToListAsync(ct);
        }
        catch (Exception ex) when (IsMissingWebsiteLeadIntakeLinksTable(ex))
        {
            _logger.LogWarning(ex, "WebsiteLeadIntakeLinks table is unavailable; Leads will render without intake summary enrichment.");
            return new Dictionary<string, LeadIntakeSummary>(StringComparer.OrdinalIgnoreCase);
        }

        return rows
            .GroupBy(x => x.WorkstationLeadId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => new LeadIntakeSummary(g.First(), g.Count()),
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<string, LeadAppointmentSummary>> LoadLeadAppointmentSummariesAsync(IEnumerable<string> leadIds, CancellationToken ct = default)
    {
        var ids = leadIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .SelectMany(id =>
            {
                var key = id.Trim();
                var noDash = key.Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase);
                return new[] { key, noDash };
            })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<string, LeadAppointmentSummary>(StringComparer.OrdinalIgnoreCase);

        List<LeadAppointmentListRow> rows;
        try
        {
            rows = await _db.LeadAppointments
                .AsNoTracking()
                .Where(x => ids.Contains(x.WorkstationLeadId))
                .OrderByDescending(x => x.UpdatedUtc)
                .ThenByDescending(x => x.ScheduledStartUtc)
                .ThenByDescending(x => x.CreatedUtc)
                .Select(x => new LeadAppointmentListRow
                {
                    Id = x.Id,
                    WorkstationLeadId = x.WorkstationLeadId,
                    OwnerAgentUserId = x.OwnerAgentUserId,
                    WebsiteLeadIntakeLinkId = x.WebsiteLeadIntakeLinkId,
                    Status = x.Status,
                    BookingSource = x.BookingSource,
                    RequestedBookingSource = x.RequestedBookingSource,
                    ConfirmationSource = x.ConfirmationSource,
                    BookingConfigurationSource = x.BookingConfigurationSource,
                    BookingTrackingProfileId = x.BookingTrackingProfileId,
                    BookingAgentSlug = x.BookingAgentSlug,
                    BookingAgentUserId = x.BookingAgentUserId,
                    BookingCalendarUserId = x.BookingCalendarUserId,
                    BookingCalendarEmail = x.BookingCalendarEmail,
                    BookingPageIdOrMailbox = x.BookingPageIdOrMailbox,
                    CalendarEventId = x.CalendarEventId,
                    CalendarEventWebLink = x.CalendarEventWebLink,
                    ScheduledStartUtc = x.ScheduledStartUtc,
                    ScheduledEndUtc = x.ScheduledEndUtc,
                    MeetingUrl = x.MeetingUrl,
                    CreatedUtc = x.CreatedUtc,
                    UpdatedUtc = x.UpdatedUtc,
                    LastStatusChangedUtc = x.LastStatusChangedUtc,
                    RequestedUtc = x.RequestedUtc,
                    BookedUtc = x.BookedUtc,
                    ConfirmedUtc = x.ConfirmedUtc,
                    CompletedUtc = x.CompletedUtc,
                    NoShowUtc = x.NoShowUtc,
                    CancelledUtc = x.CancelledUtc,
                    RescheduledUtc = x.RescheduledUtc
                })
                .ToListAsync(ct);
        }
        catch (Exception ex) when (IsMissingLeadAppointmentsTable(ex))
        {
            _logger.LogWarning(ex, "LeadAppointments table is unavailable; Leads will render without appointment enrichment.");
            return new Dictionary<string, LeadAppointmentSummary>(StringComparer.OrdinalIgnoreCase);
        }

        return rows
            .GroupBy(x => x.WorkstationLeadId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => new LeadAppointmentSummary(g.First()),
                StringComparer.OrdinalIgnoreCase);
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
        var bucketValues = string.IsNullOrWhiteSpace(normalizedBucket)
            ? Array.Empty<string>()
            : ExpandProductBucketValues(normalizedBucket);

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
                (x.OriginalLeadType != null && bucketValues.Contains(x.OriginalLeadType)) ||
                ((x.OriginalLeadType == null || x.OriginalLeadType == "") && x.Bucket != null && bucketValues.Contains(x.Bucket)));
        }

        var rawLeads = await query.ToListAsync();

        rawLeads = LeadCanonicalizer.Canonicalize(rawLeads, _logger, "Leads/Leads api")
            .OrderBy(x => x.CallCount)                               // fewest calls first
            .ThenByDescending(WorkstationLeadOrder.ResolveSortValue) // then newest/priority
            .ToList();

        var intakeSummaries = await LoadLeadIntakeSummariesAsync(rawLeads.Select(x => x.LeadId), HttpContext.RequestAborted);
        var appointmentSummaries = await LoadLeadAppointmentSummariesAsync(rawLeads.Select(x => x.LeadId), HttpContext.RequestAborted);
        var fallbackStatesByPhone = await GetFallbackStatesByPhoneAsync(agentId);

        var leads = rawLeads.Select(x =>
        {
            var attempts = CrmAttemptTracking.GetLeadAttemptCounts(x, nowUtc, dialTimeZone);
            var originalLeadType = ResolveOriginalLeadType(x.OriginalLeadType, x.Bucket, normalizedBucket);
            var stage = ResolveEffectivePipelineStage(x, originalLeadType ?? "Contacted");
            var state = ResolveLeadState(x, fallbackStatesByPhone);
            var crmMeta = ReadLeadMeta(x);
            intakeSummaries.TryGetValue(x.LeadId, out var intakeSummary);
            appointmentSummaries.TryGetValue(x.LeadId, out var appointmentSummary);
            return new
            {
            x.LeadId,
            x.FirstName,
            x.LastName,
            x.Email,
            Phone = FormatPhoneDisplay(x.Phone),
            Phone2 = FormatPhoneDisplay(x.Phone2),
            Bucket = stage,
            OriginalLeadType = originalLeadType,
            CrmStage = stage,
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
            Age = ResolveLeadAge(x.DOB, x.Age, nowUtc, dialTimeZone),
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
            DialsWeekAgentWide = agentWideDialTotals.Week,
            intakeSnapshot = BuildIntakeSnapshotPayload(intakeSummary?.Latest, intakeSummary?.HistoryCount ?? 0),
            latestAppointment = BuildLeadAppointmentPayload(appointmentSummary?.Latest)
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
                "Leads with a live booked appointment that needs preparation or follow-through.",
                "Leads whose latest appointment is Booked, Confirmed, or Rescheduled with a scheduled meeting time."
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

    private static bool HasBookedMeetingAppointment(LeadAppointmentListRow? latestAppointment)
    {
        if (latestAppointment?.ScheduledStartUtc == null)
            return false;

        return latestAppointment.Status is LeadAppointmentStatus.Booked
            or LeadAppointmentStatus.Confirmed
            or LeadAppointmentStatus.Rescheduled;
    }

    private static bool MatchesLeadQueue(WorkstationLeadProfile lead, string queueKey, LeadAppointmentListRow? latestAppointment = null)
    {
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
            "meetings" => HasBookedMeetingAppointment(latestAppointment),
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

        var appointmentSummaries = await LoadLeadAppointmentSummariesAsync(leads.Select(x => x.LeadId), HttpContext.RequestAborted);

        var queueKeys = new[] { "callsnow", "today", "overdue", "meetings", "waitingclient", "waitingcarrier" };
        var queues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in queueKeys)
        {
            var matchingIds = leads
                .Where(x =>
                {
                    appointmentSummaries.TryGetValue(x.LeadId, out var appointmentSummary);
                    return MatchesLeadQueue(x, key, appointmentSummary?.Latest);
                })
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
            .ToListAsync();

        leads = LeadCanonicalizer.Canonicalize(leads, _logger, "Leads/Queue api")
            .OrderByDescending(WorkstationLeadOrder.ResolveSortValue)
            .ToList();

        var leadIds = leads.Select(x => x.LeadId).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();

        var productionLookup = leadIds.Count > 0
            ? await _production.GetContactSnapshotsAsync(agentId, ProductionSide.Lead, leadIds, HttpContext.RequestAborted)
            : new Dictionary<string, ProductionContactSnapshot>(StringComparer.OrdinalIgnoreCase);

        var intakeSummaryLookup = await LoadLeadIntakeSummariesAsync(leadIds);
        var appointmentSummaries = await LoadLeadAppointmentSummariesAsync(leadIds, HttpContext.RequestAborted);
        var items = leads
            .Where(x =>
            {
                appointmentSummaries.TryGetValue(x.LeadId, out var appointmentSummary);
                return MatchesLeadQueue(x, queueKey, appointmentSummary?.Latest);
            })
            .Select(l =>
            {
                var attempts = CrmAttemptTracking.GetLeadAttemptCounts(l, nowUtc, dialTimeZone);
                var originalLeadType = ResolveOriginalLeadType(l.OriginalLeadType, l.Bucket);
                var crmMeta = ReadLeadMeta(l);
                var contactStatus = ClientCrmMetaSerializer.NormalizeContactStatus(crmMeta.ContactStatus);
                var stageEnteredUtc = crmMeta.StageEnteredUtc == default ? l.CreatedUtc : crmMeta.StageEnteredUtc;
                intakeSummaryLookup.TryGetValue(l.LeadId, out var intakeSummary);
                appointmentSummaries.TryGetValue(l.LeadId, out var appointmentSummary);
                var origin = intakeSummary == null
                    ? ResolveLeadOriginInfo(null, null, null, null, null, null, hasWebsiteIntake: false, email: l.Email)
                    : ResolveLeadOriginInfo(
                        intakeSummary.Latest.PageMode,
                        intakeSummary.Latest.UtmMedium,
                        intakeSummary.Latest.Fbclid,
                        intakeSummary.Latest.MetaCampaignId,
                        intakeSummary.Latest.MetaAdSetId,
                        intakeSummary.Latest.MetaAdId,
                        hasWebsiteIntake: true,
                        email: l.Email);
                var productInterest = intakeSummary == null
                    ? ResolveBucketDisplayLabel(originalLeadType ?? ResolveEffectivePipelineStage(l, "Contacted"))
                    : ResolveIntakeInterestLabel(intakeSummary.Latest.InterestType, intakeSummary.Latest.OfferKey, intakeSummary.Latest.ProductType);
                var quoteType = intakeSummary == null
                    ? ResolveBucketDisplayLabel(originalLeadType ?? ResolveEffectivePipelineStage(l, "Contacted"))
                    : ResolveIntakeQuoteTypeLabel(intakeSummary.Latest.OfferKey, intakeSummary.Latest.ProductType);
                var recommendationSummary = intakeSummary == null
                    ? string.Empty
                    : BuildRecommendationSummary(
                        intakeSummary.Latest.EstimateSummary,
                        intakeSummary.Latest.RecommendationPrimaryTitle,
                        intakeSummary.Latest.RecommendationSecondaryTitle);
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
                    ContactStatus = contactStatus,
                    PipelineStage = ResolveEffectivePipelineStage(l, "Contacted"),
                    PipelineOrder = l.CrmOrder,
                    MeetingLocation = crmMeta.MeetingLocation,
                    ZoomJoinUrl = crmMeta.ZoomJoinUrl,
                    UsePersonalZoomLink = crmMeta.UsePersonalZoomLink,
                    MeetingTime = crmMeta.MeetingTime,
                    MeetingDurationMinutes = crmMeta.MeetingDurationMinutes,
                    LatestAppointmentStatus = appointmentSummary?.Latest?.Status.ToString(),
                    LatestAppointmentStatusLabel = appointmentSummary?.Latest == null ? null : HumanizeAppointmentStatus(appointmentSummary.Latest.Status),
                    LatestAppointmentConfirmationStateLabel = appointmentSummary?.Latest == null ? null : BuildAppointmentConfirmationStateLabel(appointmentSummary.Latest),
                    LatestAppointmentScheduledStartUtc = appointmentSummary?.Latest?.ScheduledStartUtc,
                    LatestAppointmentScheduledEndUtc = appointmentSummary?.Latest?.ScheduledEndUtc,
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
                    LatestSubmissionUtc = intakeSummary?.Latest.SubmittedUtc,
                    IntakeHistoryCount = intakeSummary?.HistoryCount ?? 0,
                    LeadOriginLabel = origin.Label,
                    LeadOriginTone = origin.Tone,
                    ProductInterestLabel = productInterest,
                    QuoteTypeLabel = quoteType,
                    AttributionSource = intakeSummary?.Latest.UtmSource,
                    AttributionMedium = intakeSummary?.Latest.UtmMedium,
                    AttributionCampaign = intakeSummary?.Latest.UtmCampaign,
                    LatestRecommendationSummary = recommendationSummary,
                    IntakePageVariant = intakeSummary?.Latest.PageVariant,
                    IntakePageMode = intakeSummary?.Latest.PageMode,
                    PaidAmount = prod?.Paid ?? 0,
                    PersonalAmount = prod?.Personal ?? 0,
                    ProductionStatus = prod?.Status.ToString() ?? "",
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
        return Json(await BuildLeadPayloadAsync(lead, nowUtc, agentWideDialTotals.Today, agentWideDialTotals.Week, dialTimeZone));
    }

    public record LeadOutcomeRequest(string clientUserId, string outcomeCode, string? customNote);
    public record LeadAppointmentStatusRequest(string clientUserId, Guid? appointmentId, string? status);
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
        string? contactStatus,
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

    private async Task<object> BuildLeadPayloadAsync(
        WorkstationLeadProfile lead,
        DateTime? utcNow = null,
        int? dialsTodayAgentWide = null,
        int? dialsWeekAgentWide = null,
        TimeZoneInfo? dialTimeZone = null)
    {
        var intakeContext = await LoadLeadIntakeSnapshotContextAsync(lead.LeadId);
        var appointmentContext = await LoadLeadAppointmentSnapshotContextAsync(lead.LeadId);
        return LeadPayload(lead, intakeContext.Latest, intakeContext.HistoryCount, appointmentContext, utcNow, dialsTodayAgentWide, dialsWeekAgentWide, dialTimeZone);
    }

    private async Task<(WebsiteLeadIntakeLink? Latest, int HistoryCount)> LoadLeadIntakeSnapshotContextAsync(string leadId)
    {
        List<WebsiteLeadIntakeLink> rows;
        try
        {
            rows = await _db.WebsiteLeadIntakeLinks
                .AsNoTracking()
                .Where(x => x.WorkstationLeadId == leadId)
                .OrderByDescending(x => x.SubmittedUtc)
                .ThenByDescending(x => x.CapturedUtc)
                .ToListAsync();
        }
        catch (Exception ex) when (IsMissingWebsiteLeadIntakeLinksTable(ex))
        {
            _logger.LogWarning(ex, "WebsiteLeadIntakeLinks table is unavailable; Leads quick view will render without intake snapshot context.");
            return (null, 0);
        }

        return (rows.FirstOrDefault(), rows.Count);
    }

    private async Task<LeadAppointmentListRow?> LoadLeadAppointmentSnapshotContextAsync(string leadId)
    {
        var leadKey = (leadId ?? string.Empty).Trim();
        var leadKeyNoDashes = leadKey.Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase);
        var leadKeys = new[] { leadKey, leadKeyNoDashes }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        try
        {
            return await _db.LeadAppointments
                .AsNoTracking()
                .Where(x => leadKeys.Contains(x.WorkstationLeadId))
                .OrderByDescending(x => x.UpdatedUtc)
                .ThenByDescending(x => x.ScheduledStartUtc)
                .ThenByDescending(x => x.CreatedUtc)
                .Select(x => new LeadAppointmentListRow
                {
                    Id = x.Id,
                    WorkstationLeadId = x.WorkstationLeadId,
                    OwnerAgentUserId = x.OwnerAgentUserId,
                    WebsiteLeadIntakeLinkId = x.WebsiteLeadIntakeLinkId,
                    Status = x.Status,
                    BookingSource = x.BookingSource,
                    RequestedBookingSource = x.RequestedBookingSource,
                    ConfirmationSource = x.ConfirmationSource,
                    BookingConfigurationSource = x.BookingConfigurationSource,
                    BookingTrackingProfileId = x.BookingTrackingProfileId,
                    BookingAgentSlug = x.BookingAgentSlug,
                    BookingAgentUserId = x.BookingAgentUserId,
                    BookingCalendarUserId = x.BookingCalendarUserId,
                    BookingCalendarEmail = x.BookingCalendarEmail,
                    BookingPageIdOrMailbox = x.BookingPageIdOrMailbox,
                    CalendarEventId = x.CalendarEventId,
                    CalendarEventWebLink = x.CalendarEventWebLink,
                    ScheduledStartUtc = x.ScheduledStartUtc,
                    ScheduledEndUtc = x.ScheduledEndUtc,
                    MeetingUrl = x.MeetingUrl,
                    CreatedUtc = x.CreatedUtc,
                    UpdatedUtc = x.UpdatedUtc,
                    LastStatusChangedUtc = x.LastStatusChangedUtc,
                    RequestedUtc = x.RequestedUtc,
                    BookedUtc = x.BookedUtc,
                    ConfirmedUtc = x.ConfirmedUtc,
                    CompletedUtc = x.CompletedUtc,
                    NoShowUtc = x.NoShowUtc,
                    CancelledUtc = x.CancelledUtc,
                    RescheduledUtc = x.RescheduledUtc
                })
                .FirstOrDefaultAsync();
        }
        catch (Exception ex) when (IsMissingLeadAppointmentsTable(ex))
        {
            _logger.LogWarning(ex, "LeadAppointments table is unavailable; Leads quick view will render without appointment context.");
            return null;
        }
    }

    private static object LeadPayload(
        WorkstationLeadProfile lead,
        WebsiteLeadIntakeLink? latestIntake,
        int intakeHistoryCount,
        LeadAppointmentListRow? latestAppointment,
        DateTime? utcNow = null,
        int? dialsTodayAgentWide = null,
        int? dialsWeekAgentWide = null,
        TimeZoneInfo? dialTimeZone = null)
    {
        var effectiveUtcNow = utcNow ?? DateTime.UtcNow;
        var attempts = CrmAttemptTracking.GetLeadAttemptCounts(lead, effectiveUtcNow, dialTimeZone);
        var effectiveStage = ResolveEffectivePipelineStage(lead, ResolveOriginalLeadType(lead.OriginalLeadType, lead.Bucket) ?? "Contacted");
        var crmMeta = ReadLeadMeta(lead);
        var stageEnteredUtc = crmMeta.StageEnteredUtc == default ? lead.CreatedUtc : crmMeta.StageEnteredUtc;
        var crmPriority = string.IsNullOrWhiteSpace(crmMeta.CrmPriority) ? "Normal" : crmMeta.CrmPriority;
        var contactStatus = ClientCrmMetaSerializer.NormalizeContactStatus(crmMeta.ContactStatus);
        var watchers = (crmMeta.Collaboration?.Watchers ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new
        {
            lead.LeadId,
            agentUserId = lead.AgentUserId,
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
            age = ResolveLeadAge(lead.DOB, lead.Age, effectiveUtcNow, dialTimeZone),
            btc = lead.Btc,
            crmStatus = lead.CrmStatus,
            crmPriority,
            contactStatus,
            crmLastTouch = lead.UpdatedUtc.ToString("yyyy-MM-dd"),
            updatedUtc = lead.UpdatedUtc,
            crmNextDate = crmMeta.CrmNextDate?.ToString("yyyy-MM-dd"),
            crmNextText = crmMeta.CrmNextText ?? "",
            crmTags = crmMeta.CrmTags ?? "",
            agentNotes = crmMeta.AgentNotes ?? "",
            crmNotes = crmMeta.AgentNotes ?? "",
            pipelineStage = effectiveStage,
            bucket = effectiveStage,
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
            originalLeadType = ResolveOriginalLeadType(lead.OriginalLeadType, lead.Bucket),
            intakeSnapshot = BuildIntakeSnapshotPayload(latestIntake, intakeHistoryCount),
            latestAppointment = BuildLeadAppointmentPayload(latestAppointment)
        };
    }

    private static object? BuildIntakeSnapshotPayload(WebsiteLeadIntakeLink? intake, int historyCount = 0)
    {
        if (intake == null)
            return null;

        return BuildIntakeSnapshotPayload(new LeadIntakeListRow
        {
            WorkstationLeadId = intake.WorkstationLeadId,
            AgentUserId = intake.AgentUserId,
            Bucket = intake.Bucket,
            SubmittedUtc = intake.SubmittedUtc,
            CapturedUtc = intake.CapturedUtc,
            SourcePageKey = intake.SourcePageKey,
            SourceCtaKey = intake.SourceCtaKey,
            PageMode = intake.PageMode,
            PageVariant = intake.PageVariant,
            PagePath = intake.PagePath,
            LandingPageUrl = intake.LandingPageUrl,
            ReferrerUrl = intake.ReferrerUrl,
            InterestType = intake.InterestType,
            OfferKey = intake.OfferKey,
            ProductType = intake.ProductType,
            UtmSource = intake.UtmSource,
            UtmMedium = intake.UtmMedium,
            UtmCampaign = intake.UtmCampaign,
            UtmId = intake.UtmId,
            UtmTerm = intake.UtmTerm,
            UtmContent = intake.UtmContent,
            EstimateSummary = intake.EstimateSummary,
            RecommendationPrimaryKey = intake.RecommendationPrimaryKey,
            RecommendationPrimaryTitle = intake.RecommendationPrimaryTitle,
            RecommendationSecondaryKey = intake.RecommendationSecondaryKey,
            RecommendationSecondaryTitle = intake.RecommendationSecondaryTitle,
            SessionId = intake.SessionId,
            VisitorId = intake.VisitorId,
            DiscoverySummaryJson = intake.DiscoverySummaryJson,
            SnapshotJson = intake.SnapshotJson,
            Fbclid = intake.Fbclid,
            MetaCampaignId = intake.MetaCampaignId,
            MetaAdSetId = intake.MetaAdSetId,
            MetaAdId = intake.MetaAdId
        }, historyCount);
    }

    private static object? BuildIntakeSnapshotPayload(LeadIntakeListRow? intake, int historyCount = 0)
    {
        if (intake == null)
            return null;

        var discoveryItems = ParseIntakeDiscoverySummary(intake.DiscoverySummaryJson);
        var origin = ResolveLeadOriginInfo(
            intake.PageMode,
            intake.UtmMedium,
            intake.Fbclid,
            intake.MetaCampaignId,
            intake.MetaAdSetId,
            intake.MetaAdId,
            hasWebsiteIntake: true,
            email: null);
        return new
        {
            submittedUtc = intake.SubmittedUtc,
            capturedUtc = intake.CapturedUtc,
            historyCount,
            agentUserId = intake.AgentUserId,
            bucket = intake.Bucket,
            originLabel = origin.Label,
            originTone = origin.Tone,
            sourcePageKey = intake.SourcePageKey,
            sourceCtaKey = intake.SourceCtaKey,
            pageVariant = intake.PageVariant,
            pageMode = intake.PageMode,
            pagePath = intake.PagePath,
            landingPageUrl = intake.LandingPageUrl,
            referrerUrl = intake.ReferrerUrl,
            interestType = intake.InterestType,
            interestLabel = ResolveIntakeInterestLabel(intake.InterestType, intake.OfferKey, intake.ProductType),
            quoteTypeLabel = ResolveIntakeQuoteTypeLabel(intake.OfferKey, intake.ProductType),
            offerKey = intake.OfferKey,
            productType = intake.ProductType,
            utmSource = intake.UtmSource,
            utmMedium = intake.UtmMedium,
            utmCampaign = intake.UtmCampaign,
            utmId = intake.UtmId,
            utmTerm = intake.UtmTerm,
            utmContent = intake.UtmContent,
            fbclid = intake.Fbclid,
            metaCampaignId = intake.MetaCampaignId,
            metaAdSetId = intake.MetaAdSetId,
            metaAdId = intake.MetaAdId,
            sessionId = intake.SessionId,
            visitorId = intake.VisitorId,
            estimateSummary = intake.EstimateSummary,
            recommendationSummary = BuildRecommendationSummary(intake.EstimateSummary, intake.RecommendationPrimaryTitle, intake.RecommendationSecondaryTitle),
            recommendationPrimaryKey = intake.RecommendationPrimaryKey,
            recommendationPrimaryTitle = intake.RecommendationPrimaryTitle,
            recommendationSecondaryKey = intake.RecommendationSecondaryKey,
            recommendationSecondaryTitle = intake.RecommendationSecondaryTitle,
            discoveryItems,
            rawMetadataJson = FormatRawMetadataJson(intake.SnapshotJson),
            snapshotJson = intake.SnapshotJson
        };
    }

    private static object? BuildLeadAppointmentPayload(LeadAppointment? appointment)
    {
        if (appointment == null)
            return null;

        return BuildLeadAppointmentPayload(new LeadAppointmentListRow
        {
            Id = appointment.Id,
            WorkstationLeadId = appointment.WorkstationLeadId,
            OwnerAgentUserId = appointment.OwnerAgentUserId,
            WebsiteLeadIntakeLinkId = appointment.WebsiteLeadIntakeLinkId,
            Status = appointment.Status,
            BookingSource = appointment.BookingSource,
            RequestedBookingSource = appointment.RequestedBookingSource,
            ConfirmationSource = appointment.ConfirmationSource,
            BookingConfigurationSource = appointment.BookingConfigurationSource,
            BookingTrackingProfileId = appointment.BookingTrackingProfileId,
            BookingAgentSlug = appointment.BookingAgentSlug,
            BookingAgentUserId = appointment.BookingAgentUserId,
            BookingCalendarUserId = appointment.BookingCalendarUserId,
            BookingCalendarEmail = appointment.BookingCalendarEmail,
            BookingPageIdOrMailbox = appointment.BookingPageIdOrMailbox,
            CalendarEventId = appointment.CalendarEventId,
            CalendarEventWebLink = appointment.CalendarEventWebLink,
            ScheduledStartUtc = appointment.ScheduledStartUtc,
            ScheduledEndUtc = appointment.ScheduledEndUtc,
            MeetingUrl = appointment.MeetingUrl,
            CreatedUtc = appointment.CreatedUtc,
            UpdatedUtc = appointment.UpdatedUtc,
            LastStatusChangedUtc = appointment.LastStatusChangedUtc,
            RequestedUtc = appointment.RequestedUtc,
            BookedUtc = appointment.BookedUtc,
            ConfirmedUtc = appointment.ConfirmedUtc,
            CompletedUtc = appointment.CompletedUtc,
            NoShowUtc = appointment.NoShowUtc,
            CancelledUtc = appointment.CancelledUtc,
            RescheduledUtc = appointment.RescheduledUtc
        });
    }

    private static object? BuildLeadAppointmentPayload(LeadAppointmentListRow? appointment)
    {
        if (appointment == null)
            return null;

        return new
        {
            id = appointment.Id,
            workstationLeadId = appointment.WorkstationLeadId,
            ownerAgentUserId = appointment.OwnerAgentUserId,
            websiteLeadIntakeLinkId = appointment.WebsiteLeadIntakeLinkId,
            status = appointment.Status.ToString(),
            statusLabel = HumanizeAppointmentStatus(appointment.Status),
            bookingSource = appointment.BookingSource,
            bookingSourceLabel = HumanizeAppointmentSource(appointment.BookingSource),
            requestedBookingSource = appointment.RequestedBookingSource,
            requestedBookingSourceLabel = HumanizeAppointmentSource(appointment.RequestedBookingSource),
            confirmationSource = appointment.ConfirmationSource,
            confirmationSourceLabel = HumanizeAppointmentSource(appointment.ConfirmationSource),
            confirmationVerified = IsTrustedAppointment(appointment),
            confirmationStateLabel = BuildAppointmentConfirmationStateLabel(appointment),
            bookingConfigurationSource = appointment.BookingConfigurationSource,
            bookingConfigurationSourceLabel = HumanizeBookingConfigurationSource(appointment.BookingConfigurationSource),
            bookingConfigurationLabel = BuildBookingConfigurationLabel(appointment),
            bookingTrackingProfileId = appointment.BookingTrackingProfileId,
            bookingAgentSlug = appointment.BookingAgentSlug,
            bookingAgentUserId = appointment.BookingAgentUserId,
            bookingCalendarEmail = appointment.BookingCalendarEmail,
            bookingPageIdOrMailbox = appointment.BookingPageIdOrMailbox,
            calendarEventId = appointment.CalendarEventId,
            calendarEventWebLink = appointment.CalendarEventWebLink,
            scheduledStartUtc = UtcDate(appointment.ScheduledStartUtc),
            scheduledEndUtc = UtcDate(appointment.ScheduledEndUtc),
            meetingUrl = appointment.MeetingUrl,
            createdUtc = UtcDate(appointment.CreatedUtc),
            updatedUtc = UtcDate(appointment.UpdatedUtc),
            lastStatusChangedUtc = UtcDate(appointment.LastStatusChangedUtc),
            statusTimestampUtc = UtcDate(ResolveAppointmentStatusTimestamp(appointment)),
            requestedUtc = UtcDate(appointment.RequestedUtc),
            bookedUtc = UtcDate(appointment.BookedUtc),
            confirmedUtc = UtcDate(appointment.ConfirmedUtc),
            completedUtc = UtcDate(appointment.CompletedUtc),
            noShowUtc = UtcDate(appointment.NoShowUtc),
            cancelledUtc = UtcDate(appointment.CancelledUtc),
            rescheduledUtc = UtcDate(appointment.RescheduledUtc)
        };
    }

    private static DateTime? UtcDate(DateTime? value)
        => value.HasValue
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
            : null;

    private static IReadOnlyList<object> ParseIntakeDiscoverySummary(string? discoverySummaryJson)
    {
        if (string.IsNullOrWhiteSpace(discoverySummaryJson))
            return Array.Empty<object>();

        try
        {
            using var doc = JsonDocument.Parse(discoverySummaryJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<object>();

            return doc.RootElement
                .EnumerateArray()
                .Select(item => new
                {
                    label = item.TryGetProperty("Label", out var label) ? label.GetString() ?? string.Empty : string.Empty,
                    value = item.TryGetProperty("Value", out var value) ? value.GetString() ?? string.Empty : string.Empty
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.label) && !string.IsNullOrWhiteSpace(item.value))
                .Cast<object>()
                .ToList();
        }
        catch
        {
            return Array.Empty<object>();
        }
    }

    private static string ResolveIntakeInterestLabel(string? interestType, string? offerKey, string? productType)
    {
        var key = Norm(offerKey);
        if (string.IsNullOrWhiteSpace(key))
            key = Norm(interestType);
        if (string.IsNullOrWhiteSpace(key))
            key = Norm(productType);

        return key switch
        {
            "life" or "lifegeneral" => "Life Insurance",
            "term" or "termlife" or "lifeterm" => "Term Life",
            "wholelife" or "lifewhole" => "Whole Life",
            "finalexpense" or "lifefinalexpense" => "Final Expense",
            "mortgage" or "lifemp" => "Mortgage Protection",
            "iul" or "lifeiul" => "IUL",
            "autoinsurance" or "auto" => "Auto Insurance",
            "homeinsurance" or "home" => "Home Insurance",
            "commercialinsurance" or "commercial" => "Commercial Insurance",
            "disabilityinsurance" or "disability" => "Disability Insurance",
            "healthinsurance" or "health" => "Health Insurance",
            _ => string.IsNullOrWhiteSpace(key) ? "Website Intake" : key
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

        var dialTimeZone = _agentTimeZoneResolver.Resolve(HttpContext);
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
        lead.Age = lead.DOB.HasValue
            ? ResolveLeadAge(lead.DOB, string.IsNullOrWhiteSpace(req.age) ? lead.Age : req.age, DateTime.UtcNow, dialTimeZone)
            : (string.IsNullOrWhiteSpace(req.age) ? lead.Age : req.age);
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

        var normalizedStage = NormalizePipelineStage(req.pipelineStage)
            ?? ResolveEffectivePipelineStage(lead, ResolveOriginalLeadType(lead.OriginalLeadType, lead.Bucket) ?? "Contacted");
        var currentStage = ResolveEffectivePipelineStage(lead, ResolveOriginalLeadType(lead.OriginalLeadType, lead.Bucket) ?? "Contacted");
        if (!string.Equals(currentStage, normalizedStage, StringComparison.OrdinalIgnoreCase))
            meta.StageEnteredUtc = DateTime.UtcNow;
        lead.CrmStage = normalizedStage;
        lead.Bucket = normalizedStage;

        meta.CrmPriority = normalizedPriority;
        meta.ContactStatus = ClientCrmMetaSerializer.NormalizeContactStatus(req.contactStatus);
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

        return Json(new { payload = await BuildLeadPayloadAsync(lead, dialTimeZone: dialTimeZone) });
    }

    [HttpPost]
    public async Task<IActionResult> UpdateLeadAppointmentStatus([FromBody] LeadAppointmentStatusRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.clientUserId)) return BadRequest("Lead id required");
        if (!Enum.TryParse<LeadAppointmentStatus>(req.status ?? string.Empty, ignoreCase: true, out var nextStatus))
            return BadRequest("Valid appointment status required");

        string agentId;
        try { agentId = GetAgentIdOrChallenge(); }
        catch { return Challenge(); }

        var lead = await LoadCanonicalLeadAsync(agentId, req.clientUserId, "UpdateLeadAppointmentStatus");
        if (lead == null) return NotFound();

        var nowUtc = DateTime.UtcNow;
        var dialTimeZone = _agentTimeZoneResolver.Resolve(HttpContext);

        LeadAppointment? appointment;
        try
        {
            appointment = req.appointmentId.HasValue
                ? await _db.LeadAppointments.FirstOrDefaultAsync(x => x.Id == req.appointmentId.Value && x.WorkstationLeadId == lead.LeadId)
                : await _db.LeadAppointments
                    .Where(x => x.WorkstationLeadId == lead.LeadId)
                    .OrderByDescending(x => x.UpdatedUtc)
                    .ThenByDescending(x => x.ScheduledStartUtc)
                    .ThenByDescending(x => x.CreatedUtc)
                    .FirstOrDefaultAsync();
        }
        catch (Exception ex) when (IsMissingLeadAppointmentsTable(ex))
        {
            _logger.LogWarning(ex, "LeadAppointments table is unavailable; appointment status updates are blocked.");
            return StatusCode(StatusCodes.Status409Conflict, "Lead appointments are unavailable until the latest migration is applied.");
        }

        Guid? latestIntakeLinkId = null;
        if (appointment?.WebsiteLeadIntakeLinkId == null)
        {
            try
            {
                latestIntakeLinkId = await _db.WebsiteLeadIntakeLinks
                    .AsNoTracking()
                    .Where(x => x.WorkstationLeadId == lead.LeadId)
                    .OrderByDescending(x => x.SubmittedUtc)
                    .ThenByDescending(x => x.CapturedUtc)
                    .Select(x => (Guid?)x.Id)
                    .FirstOrDefaultAsync();
            }
            catch (Exception ex) when (IsMissingWebsiteLeadIntakeLinksTable(ex))
            {
                _logger.LogWarning(ex, "WebsiteLeadIntakeLinks table is unavailable; appointment status update will persist without intake linkage.");
            }
        }

        if (appointment == null)
        {
            if (nextStatus != LeadAppointmentStatus.Requested)
                return BadRequest("Create a requested appointment first, or sync a calendar event before updating later statuses.");

            appointment = new LeadAppointment
            {
                Id = Guid.NewGuid(),
                WorkstationLeadId = lead.LeadId,
                OwnerAgentUserId = agentId,
                WebsiteLeadIntakeLinkId = latestIntakeLinkId,
                BookingSource = LeadAppointmentBookingSources.InternalManual,
                RequestedBookingSource = LeadAppointmentBookingSources.InternalManual,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc
            };
            _db.LeadAppointments.Add(appointment);
        }
        else
        {
            appointment.OwnerAgentUserId = agentId;
            if (appointment.WebsiteLeadIntakeLinkId == null)
                appointment.WebsiteLeadIntakeLinkId = latestIntakeLinkId;
        }

        if (nextStatus == LeadAppointmentStatus.Requested &&
            appointment.Status is LeadAppointmentStatus.Booked or LeadAppointmentStatus.Confirmed or LeadAppointmentStatus.Completed or LeadAppointmentStatus.Rescheduled)
        {
            return BadRequest("Booked, confirmed, completed, or rescheduled appointments cannot be downgraded back to Requested.");
        }

        if (nextStatus == LeadAppointmentStatus.Booked && !appointment.RequestedUtc.HasValue)
            appointment.RequestedUtc = nowUtc;
        if (string.IsNullOrWhiteSpace(appointment.RequestedBookingSource))
            appointment.RequestedBookingSource = appointment.BookingSource;
        if (nextStatus is LeadAppointmentStatus.Booked or LeadAppointmentStatus.Confirmed or LeadAppointmentStatus.Completed or LeadAppointmentStatus.Rescheduled)
        {
            if (string.IsNullOrWhiteSpace(appointment.ConfirmationSource))
            {
                if (string.Equals(appointment.BookingSource, LeadAppointmentBookingSources.InternalCalendar, StringComparison.OrdinalIgnoreCase))
                {
                    appointment.ConfirmationSource = LeadAppointmentBookingSources.InternalCalendar;
                }
                else if (!string.Equals(appointment.BookingSource, LeadAppointmentBookingSources.MicrosoftGraphConfirmation, StringComparison.OrdinalIgnoreCase))
                {
                    appointment.BookingSource = LeadAppointmentBookingSources.ManualVerified;
                    appointment.ConfirmationSource = LeadAppointmentBookingSources.ManualVerified;
                }
            }
        }
        appointment.ApplyStatus(nextStatus, nowUtc);

        if (nextStatus == LeadAppointmentStatus.Completed)
        {
            await _metaSignalOutcomes.RecordAppointmentCompletedAsync(appointment);
        }

        var meta = ReadLeadMeta(lead);
        meta.Activities ??= new List<ClientCrmActivity>();
        var localNow = dialTimeZone is null
            ? nowUtc
            : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), dialTimeZone);
        var scheduledDetail = appointment.ScheduledStartUtc.HasValue
            ? $" for {appointment.ScheduledStartUtc.Value:u}".Replace("Z", " UTC", StringComparison.Ordinal)
            : string.Empty;
        meta.Activities.Add(new ClientCrmActivity
        {
            Type = "Meeting",
            Date = localNow.ToString("yyyy-MM-dd"),
            Note = $"Appointment marked {HumanizeAppointmentStatus(nextStatus)}{scheduledDetail}.",
            MeetingLink = appointment.MeetingUrl,
            CalendarEventId = appointment.CalendarEventId,
            CalendarWebLink = appointment.CalendarEventWebLink,
            IsSystem = true,
            CreatedBy = agentId
        });

        lead.CrmNotes = ClientCrmMetaSerializer.Serialize(meta);
        lead.AgentUserId = agentId;
        lead.UpdatedUtc = nowUtc;

        await _db.SaveChangesAsync();

        var agentWideDialTotals = await GetAgentWideDialTotalsAsync(agentId, nowUtc, dialTimeZone);
        return Json(new
        {
            payload = await BuildLeadPayloadAsync(lead, nowUtc, agentWideDialTotals.Today, agentWideDialTotals.Week, dialTimeZone)
        });
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
            payload = await BuildLeadPayloadAsync(lead, nowUtc, agentWideDialTotals.Today, agentWideDialTotals.Week, dialTimeZone),
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

        await DeleteLeadProfilesAsync(new[] { lead });
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

        var deletedCount = await DeleteLeadProfilesAsync(toDelete);
        return Json(new { deleted = deletedCount });
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
        var bucketValues = isProductBucket
            ? ExpandProductBucketValues(bucket)
            : Array.Empty<string>();

        var toDelete = isProductBucket
            ? await _db.WorkstationLeadProfiles
                .Where(x =>
                    x.AgentUserId == agentId &&
                    ((x.OriginalLeadType != null && bucketValues.Contains(x.OriginalLeadType)) ||
                     ((x.OriginalLeadType == null || x.OriginalLeadType == "") && x.Bucket != null && bucketValues.Contains(x.Bucket))))
                .OrderByDescending(x => x.CreatedUtc)
                .Take(batchSize)
                .ToListAsync()
            : await _db.WorkstationLeadProfiles
                .Where(x => x.AgentUserId == agentId && x.Bucket == bucket)
                .OrderByDescending(x => x.CreatedUtc)
                .Take(batchSize)
                .ToListAsync();

        if (toDelete.Count == 0) return Json(new { deleted = 0 });

        var deletedCount = await DeleteLeadProfilesAsync(toDelete);
        var remaining = isProductBucket
            ? await _db.WorkstationLeadProfiles
                .CountAsync(x =>
                    x.AgentUserId == agentId &&
                    ((x.OriginalLeadType != null && bucketValues.Contains(x.OriginalLeadType)) ||
                     ((x.OriginalLeadType == null || x.OriginalLeadType == "") && x.Bucket != null && bucketValues.Contains(x.Bucket))))
            : await _db.WorkstationLeadProfiles
                .CountAsync(x => x.AgentUserId == agentId && x.Bucket == bucket);
        return Json(new { deleted = deletedCount, remaining });
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
        long seed = WorkstationLeadOrder.Build(now);
        for (int i = 0; i < ids.Count; i++)
        {
            var lead = leads.FirstOrDefault(l => l.LeadId == ids[i]);
            if (lead == null) continue;
            lead.CrmOrder = seed - i;
            PreserveOriginalLeadType(lead);
            if (normalizedBucket != null)
            {
                lead.CrmStage = normalizedBucket;
                lead.Bucket = normalizedBucket;
            }
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

        var normalizedBucket = NormalizeBucket(bucket);
        if (string.IsNullOrWhiteSpace(normalizedBucket) || !ProductBuckets.Contains(normalizedBucket, StringComparer.OrdinalIgnoreCase))
            return BadRequest("Invalid bucket");

        bucket = normalizedBucket;

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
            var crmOrderSeed = WorkstationLeadOrder.Build(now);
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
                var usesRequestedAmountField = WorkstationLeadBuckets.UsesRequestedAmountField(bucket);
                var minimumColumns = usesRequestedAmountField ? 12 : 13;
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
                    if (usesRequestedAmountField)
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
                    if (usesRequestedAmountField)
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
                    existing.Btc ??= btc;
                    existing.DOB ??= dob;
                    existing.Age = existing.DOB.HasValue
                        ? ResolveLeadAge(existing.DOB, existing.Age ?? age, now)
                        : (existing.Age ?? age);
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
                    Age = ResolveLeadAge(dob, age, now),
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
