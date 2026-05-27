using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AgentPortal.Models.Analytics;
using AgentPortal.Security;
using AgentPortal.Services;
using AgentPortal.Services.Analytics;
using AgentPortal.Services.Tracking;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentPortal.Controllers;

/// <summary>
/// Serves AI-powered analytics insights. Scoped exclusively to Website Analytics data.
/// No PII reaches OpenAI — all payloads are redacted by <see cref="WebsiteAnalyticsAiRedactor"/>
/// before any external call.
/// </summary>
[Authorize]
[Route("website-analytics/ai")]
public sealed class WebsiteAnalyticsAiController : Controller
{
    private const int MaxFollowUpQuestionChars = 500;
    private static readonly Regex HtmlTagPattern = new(@"<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex EmailInQuestionPattern =
        new(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled);
    private static readonly Regex PhoneInQuestionPattern =
        new(@"(\+?1[\s\-.]?)?\(?\d{3}\)?[\s\-.]?\d{3}[\s\-.]?\d{4}", RegexOptions.Compiled);

    private readonly WebsiteAnalyticsAiDataBuilder _dataBuilder;
    private readonly OpenAiWebsiteAnalyticsReviewService _reviewService;
    private readonly EffectiveAgentContext _effectiveContext;
    private readonly IAgentTrackingService _tracking;
    private readonly MasterAppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<WebsiteAnalyticsAiController> _logger;
    private readonly string _founderUpn;

    public WebsiteAnalyticsAiController(
        WebsiteAnalyticsAiDataBuilder dataBuilder,
        OpenAiWebsiteAnalyticsReviewService reviewService,
        EffectiveAgentContext effectiveContext,
        IAgentTrackingService tracking,
        MasterAppDbContext db,
        IConfiguration config,
        ILogger<WebsiteAnalyticsAiController> logger)
    {
        _dataBuilder = dataBuilder;
        _reviewService = reviewService;
        _effectiveContext = effectiveContext;
        _tracking = tracking;
        _db = db;
        _config = config;
        _logger = logger;
        _founderUpn = config["Founder:Upn"]
            ?? throw new InvalidOperationException("Founder:Upn configuration is required");
    }

    private static TimeZoneInfo ResolveViewerTimeZone(string? timezoneId, int? timezoneOffsetMinutes)
    {
        if (!string.IsNullOrWhiteSpace(timezoneId))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(timezoneId.Trim()); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        if (timezoneOffsetMinutes.HasValue &&
            timezoneOffsetMinutes.Value >= -840 &&
            timezoneOffsetMinutes.Value <= 840)
        {
            try
            {
                return TimeZoneInfo.CreateCustomTimeZone(
                    $"viewer-offset-{timezoneOffsetMinutes.Value}",
                    TimeSpan.FromMinutes(-timezoneOffsetMinutes.Value),
                    "Viewer Local",
                    "Viewer Local");
            }
            catch
            {
                // Safe fallback: invalid browser offsets should not break analytics AI review.
            }
        }

        return TimeZoneInfo.Utc;
    }

    // ── POST /website-analytics/ai/review ────────────────────────────────────

    [HttpPost("review")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Review([FromBody] AiReviewRequestDto request)
    {
        var ct = HttpContext.RequestAborted;

        TimeRangeRequest range;
        try { range = TimeRangeRequest.FromPreset(request?.Preset, request?.FromUtc, request?.ToUtc, ResolveViewerTimeZone(request?.TimezoneId, request?.TimezoneOffsetMinutes)); }
        catch { range = TimeRangeRequest.FromPreset("today"); }

        var scope = await ResolveScopeAsync(request?.AgentProfileId, request?.Team ?? false);
        var scopeLabel = await ResolveScopeLabelAsync(scope, request?.Team ?? false);
        var trafficType = ParseTrafficType(request?.TrafficType);

        _logger.LogInformation(
            "AI review starting. Scope={Scope} Range={Range} Traffic={Traffic} User={User}",
            scopeLabel, range.Label, trafficType,
            _effectiveContext.ActualUserUpn ?? "(unknown)");

        try
        {
            var rawPayload = await _dataBuilder.BuildAsync(
                range, scope, range.Label, scopeLabel,
                TrafficAttribution.BucketLabel(trafficType), trafficType, ct);

            var safePayload = WebsiteAnalyticsAiRedactor.Redact(rawPayload, _logger);

            var result = await _reviewService.ReviewAsync(safePayload, ct);
            AppendPayloadWarnings(result, safePayload.Warnings);

            _logger.LogInformation(
                "AI review completed. Scope={Scope} Range={Range} IsError={IsError}",
                scopeLabel, range.Label, result.IsError);

            return Json(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI review endpoint failed unexpectedly. Scope={Scope}", scopeLabel);
            return Json(new AiInsightsResultDto
            {
                IsError = true,
                ErrorMessage = "AI review failed unexpectedly. Please try again.",
                Summary = "An unexpected error occurred.",
                ScaleReadinessVerdict = "DoNotScale",
                DataTrustWarning = "AI review failed unexpectedly.",
                DoNotScaleBecause = new List<string> { "AI review failed unexpectedly." },
                NextThreeActions = new List<string>()
            });
        }
    }

    // ── POST /website-analytics/ai/followup ──────────────────────────────────

    [HttpPost("followup")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FollowUp([FromBody] AiFollowUpRequestDto request)
    {
        var ct = HttpContext.RequestAborted;

        if (request == null)
            return Json(ErrorDto("Invalid request."));

        // Validate and sanitise the follow-up question
        var question = (request.FollowUpQuestion ?? "").Trim();
        question = HtmlTagPattern.Replace(question, "");  // strip HTML tags
        question = question.Trim();

        if (string.IsNullOrWhiteSpace(question))
            return Json(ErrorDto("Follow-up question cannot be empty."));

        if (question.Length > MaxFollowUpQuestionChars)
            return Json(ErrorDto($"Follow-up question exceeds {MaxFollowUpQuestionChars} character limit."));

        if (EmailInQuestionPattern.IsMatch(question))
            return Json(ErrorDto("Follow-up question may not contain email addresses."));

        if (PhoneInQuestionPattern.IsMatch(question))
            return Json(ErrorDto("Follow-up question may not contain phone numbers."));

        TimeRangeRequest range;
        try { range = TimeRangeRequest.FromPreset(request.Preset, request.FromUtc, request.ToUtc, ResolveViewerTimeZone(request.TimezoneId, request.TimezoneOffsetMinutes)); }
        catch { range = TimeRangeRequest.FromPreset("today"); }

        var scope = await ResolveScopeAsync(request.AgentProfileId, request.Team);
        var scopeLabel = await ResolveScopeLabelAsync(scope, request.Team);
        var trafficType = ParseTrafficType(request.TrafficType);

        _logger.LogInformation(
            "AI follow-up starting. Scope={Scope} Range={Range} QuestionLen={Len}",
            scopeLabel, range.Label, question.Length);

        try
        {
            var rawPayload = await _dataBuilder.BuildAsync(
                range, scope, range.Label, scopeLabel,
                TrafficAttribution.BucketLabel(trafficType), trafficType, ct);

            var safePayload = WebsiteAnalyticsAiRedactor.Redact(rawPayload, _logger);

            // Truncate prior summary to prevent abuse
            var priorSummary = request.PriorSummary;
            if (!string.IsNullOrWhiteSpace(priorSummary) && priorSummary.Length > 2000)
                priorSummary = priorSummary[..2000];

            var result = await _reviewService.FollowUpAsync(safePayload, question, priorSummary, ct);
            AppendPayloadWarnings(result, safePayload.Warnings);

            _logger.LogInformation(
                "AI follow-up completed. Scope={Scope} IsError={IsError}",
                scopeLabel, result.IsError);

            return Json(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI follow-up endpoint failed. Scope={Scope}", scopeLabel);
            return Json(ErrorDto("AI follow-up failed unexpectedly. Please try again."));
        }
    }

    // ── Scope resolution (mirrors WebsiteAnalyticsController exactly) ─────────

    private async Task<ScopeContext> ResolveScopeAsync(Guid? requestedAgentId, bool team = false)
    {
        var isFounder = FounderGuard.IsFounder(User);

        var effectiveProfile = await _effectiveContext.GetEffectiveTrackingProfileAsync();
        var effectiveProfileId = effectiveProfile?.Id;

        if (team && !isFounder)
        {
            _logger.LogWarning("WebsiteAnalyticsAi denied team scope elevation for non-founder caller.");
        }
        else if (team && isFounder)
        {
            return ScopeContext.Global;
        }

        if (isFounder)
        {
            if (requestedAgentId.HasValue) return ScopeContext.ForAgent(requestedAgentId.Value);
            if (_effectiveContext.IsViewingAsAgent)
                return await ResolveEffectiveImpersonatedAgentScopeAsync();
            return ScopeContext.Global;
        }

        if (_effectiveContext.IsViewingAsAgent)
            return await ResolveEffectiveImpersonatedAgentScopeAsync();

        if (effectiveProfileId.HasValue)
            return ScopeContext.ForAgent(effectiveProfileId.Value);

        _logger.LogWarning("WebsiteAnalyticsAi scope: no agent profile for caller; returning empty scope.");
        return ScopeContext.ForAgent(Guid.Empty);
    }

    private async Task<string> ResolveScopeLabelAsync(ScopeContext scope, bool team)
    {
        if (team && FounderGuard.IsFounder(User)) return "Founder Team";

        if (scope.ScopeType == ScopeType.Global)
            return "Global";

        var agentId = scope.AgentTrackingProfileId;
        if (!agentId.HasValue || agentId.Value == Guid.Empty)
            return "Agent Scope";

        var profile = await _db.AgentTrackingProfiles.AsNoTracking()
            .Where(p => p.Id == agentId.Value)
            .Select(p => new { p.DisplayName, p.AgentUpn, p.Slug })
            .FirstOrDefaultAsync();

        if (profile == null) return "Agent Scope";
        if (FounderGuard.IsFounder(User) &&
            !string.IsNullOrWhiteSpace(profile.AgentUpn) &&
            string.Equals(profile.AgentUpn, _founderUpn, StringComparison.OrdinalIgnoreCase))
        {
            return "Founder Personal";
        }

        var name = profile.DisplayName ?? profile.AgentUpn ?? profile.Slug;
        return string.IsNullOrWhiteSpace(name) ? "Agent Scope" : $"Agent: {name}";
    }

    private async Task<ScopeContext> ResolveEffectiveImpersonatedAgentScopeAsync()
    {
        var effectiveProfile = await _effectiveContext.GetEffectiveTrackingProfileAsync();
        var effectiveProfileId = effectiveProfile?.Id;
        if (effectiveProfileId.HasValue)
            return ScopeContext.ForAgent(effectiveProfileId.Value);

        var effectiveOid = (_effectiveContext.EffectiveAgentOid ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(effectiveOid))
        {
            var byOid = await _tracking.GetByUserIdAsync(effectiveOid);
            if (byOid != null)
                return ScopeContext.ForAgent(byOid.Id);

            var oidLower = effectiveOid.ToLowerInvariant();
            var agentProfile = await _db.AgentProfiles.AsNoTracking()
                .Where(a => a.AgentUserId != null && a.AgentUserId.ToLower() == oidLower)
                .OrderByDescending(a => a.UpdatedUtc)
                .FirstOrDefaultAsync();

            var upn = agentProfile?.AgentUpn
                ?? (HttpContext.Items.TryGetValue("ImpersonatedAgentEmail", out var emailObj) ? emailObj as string : null);
            var displayName = agentProfile?.FullName
                ?? (HttpContext.Items.TryGetValue("ImpersonatedAgentName", out var nameObj) ? nameObj as string : null);

            if (!string.IsNullOrWhiteSpace(upn))
            {
                var ensured = await _tracking.EnsureProfileAsync(effectiveOid, upn, displayName);
                return ScopeContext.ForAgent(ensured.Id);
            }
        }

        _logger.LogWarning(
            "WebsiteAnalyticsAi scope resolution failed for impersonated agent. effectiveOid={Oid}.",
            _effectiveContext.EffectiveAgentOid ?? "(null)");
        return ScopeContext.ForAgent(Guid.Empty);
    }

    private static TrafficType ParseTrafficType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return TrafficType.All;
        return Enum.TryParse<TrafficType>(value, ignoreCase: true, out var result)
            ? result
            : TrafficType.All;
    }

    private static AiInsightsResultDto ErrorDto(string message) => new()
    {
        IsError = true,
        ErrorMessage = message,
        Summary = message,
        ScaleReadinessVerdict = "DoNotScale",
        DataTrustWarning = message,
        DoNotScaleBecause = new List<string> { message },
        NextThreeActions = new List<string>()
    };

    private static void AppendPayloadWarnings(AiInsightsResultDto? result, IReadOnlyCollection<string>? warnings)
    {
        if (result == null || warnings == null || warnings.Count == 0) return;
        result.ConfidenceNotes ??= new List<string>();
        foreach (var warning in warnings)
        {
            if (string.IsNullOrWhiteSpace(warning)) continue;
            var note = $"Partial data warning: {warning}";
            if (!result.ConfidenceNotes.Contains(note, StringComparer.OrdinalIgnoreCase))
                result.ConfidenceNotes.Add(note);
        }
    }
}
