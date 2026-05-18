using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AgentPortal.Models.Analytics;
using Microsoft.Extensions.Logging;

namespace AgentPortal.Services.Analytics;

/// <summary>
/// Whitelist-based redactor for the AI analytics payload.
/// Only explicitly allowed fields are permitted. Strips any value that
/// looks like PII (email addresses, phone numbers, or fields named after
/// personal identifiers).
/// </summary>
public static class WebsiteAnalyticsAiRedactor
{
    // Patterns used to detect PII-shaped strings
    private static readonly Regex EmailPattern =
        new(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PhonePattern =
        new(@"(\+?1[\s\-.]?)?\(?\d{3}\)?[\s\-.]?\d{3}[\s\-.]?\d{4}", RegexOptions.Compiled);

    // Field names that must never appear in the payload regardless of casing
    private static readonly HashSet<string> PiiFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "email", "phone", "firstname", "lastname", "first_name", "last_name",
        "address", "zip", "zipcode", "ssn", "dob", "dateofbirth", "birthdate"
    };

    /// <summary>
    /// Returns a sanitised copy of the payload. Mutates a copy only — never the original.
    /// Logs a warning (if a logger is provided) when any unexpected field values are redacted.
    /// </summary>
    public static AiSafeAnalyticsPayload Redact(AiSafeAnalyticsPayload payload, ILogger? logger = null)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));

        // Return a new object built from only the whitelisted scalar fields.
        var safe = new AiSafeAnalyticsPayload
        {
            // ── Allowed context labels ─────────────────────────────────────────
            RangeLabel = CleanLabel(payload.RangeLabel, logger, "RangeLabel"),
            ScopeLabel = CleanLabel(payload.ScopeLabel, logger, "ScopeLabel"),
            TrafficFilter = CleanLabel(payload.TrafficFilter, logger, "TrafficFilter"),
            Warnings = (payload.Warnings ?? new List<string>())
                .Select(w => CleanLabel(w, logger, "Warnings"))
                .Where(w => !string.IsNullOrWhiteSpace(w) && w != "[redacted]")
                .ToList(),

            // ── Allowed numeric aggregates ────────────────────────────────────
            PageViews = payload.PageViews,
            UniqueVisitors = payload.UniqueVisitors,
            Sessions = payload.Sessions,
            VerifiedLeads = payload.VerifiedLeads,
            SessionConversionRate = payload.SessionConversionRate,
            IntentConversionRate = payload.IntentConversionRate,
            IntentAvailable = payload.IntentAvailable,
            QuoteStarts = payload.QuoteStarts,
            QuoteFormStarts = payload.QuoteFormStarts,
            QuoteFormSubmits = payload.QuoteFormSubmits,
            DropOffStartsToFormStarts = payload.DropOffStartsToFormStarts,
            DropOffFormStartsToSubmits = payload.DropOffFormStartsToSubmits,
            TotalConversions = payload.TotalConversions,
            AvgSessionDurationMs = payload.AvgSessionDurationMs,
            QuickExitRate = payload.QuickExitRate,
            EngagedSessionRate = payload.EngagedSessionRate,

            // ── Allowed label-only string fields ──────────────────────────────
            TopPage = CleanLabel(payload.TopPage, logger, "TopPage"),
            TopCta = CleanLabel(payload.TopCta, logger, "TopCta"),
            TopSource = CleanLabel(payload.TopSource, logger, "TopSource"),
            TopCampaign = CleanLabel(payload.TopCampaign, logger, "TopCampaign"),

            // ── Allowed aggregate collections ─────────────────────────────────
            TopPages = RedactLabelCounts(payload.TopPages, "TopPages", logger),
            TopSources = RedactLabelCounts(payload.TopSources, "TopSources", logger),
            TopCampaigns = RedactLabelCounts(payload.TopCampaigns, "TopCampaigns", logger),
            EntryPages = RedactLabelCounts(payload.EntryPages, "EntryPages", logger),

            PagePerformance = RedactPagePerf(payload.PagePerformance, logger),
            CtaPerformance = RedactCtaPerf(payload.CtaPerformance, logger),
            TopDwellPages = RedactDwell(payload.TopDwellPages, logger),
            TopExitPages = RedactExit(payload.TopExitPages, logger),
            SourcePerformance = RedactSources(payload.SourcePerformance, logger),
            FormAbandonment = RedactAbandonment(payload.FormAbandonment, logger),
            TopAbandonedFields = RedactLabelCounts(payload.TopAbandonedFields, "TopAbandonedFields", logger),
            ActiveCampaigns = RedactCampaigns(payload.ActiveCampaigns, logger),
            MetaSignal = RedactMetaSignal(payload.MetaSignal, logger)
        };

        return safe;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string CleanLabel(string? value, ILogger? logger, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        if (LooksPii(value))
        {
            logger?.LogWarning(
                "AI redactor stripped PII-shaped value from field {Field}. Value type: {Type}",
                fieldName, DetectPiiType(value));
            return "[redacted]";
        }

        return value.Trim();
    }

    private static List<LabelCount> RedactLabelCounts(
        List<LabelCount>? items, string fieldName, ILogger? logger)
    {
        if (items == null) return new List<LabelCount>();
        return items
            .Select(x => new LabelCount
            {
                Label = CleanLabel(x.Label, logger, fieldName),
                Count = x.Count
            })
            .Where(x => x.Label != "[redacted]")
            .ToList();
    }

    private static List<PagePerfRow> RedactPagePerf(List<PagePerfRow>? rows, ILogger? logger)
    {
        if (rows == null) return new List<PagePerfRow>();
        return rows.Select(x => new PagePerfRow
        {
            PageKey = CleanLabel(x.PageKey, logger, "PagePerformance.PageKey"),
            Views = x.Views,
            CtaClicks = x.CtaClicks,
            Leads = x.Leads,
            ConversionRate = x.ConversionRate
        }).ToList();
    }

    private static List<CtaPerfRow> RedactCtaPerf(List<CtaPerfRow>? rows, ILogger? logger)
    {
        if (rows == null) return new List<CtaPerfRow>();
        return rows.Select(x => new CtaPerfRow
        {
            PageKey = CleanLabel(x.PageKey, logger, "CtaPerformance.PageKey"),
            ElementKey = CleanLabel(x.ElementKey, logger, "CtaPerformance.ElementKey"),
            Clicks = x.Clicks
        }).ToList();
    }

    private static List<DwellRow> RedactDwell(List<DwellRow>? rows, ILogger? logger)
    {
        if (rows == null) return new List<DwellRow>();
        return rows.Select(x => new DwellRow
        {
            PageKey = CleanLabel(x.PageKey, logger, "DwellPages.PageKey"),
            AvgDwellMs = x.AvgDwellMs,
            Samples = x.Samples
        }).ToList();
    }

    private static List<ExitRow> RedactExit(List<ExitRow>? rows, ILogger? logger)
    {
        if (rows == null) return new List<ExitRow>();
        return rows.Select(x => new ExitRow
        {
            PageKey = CleanLabel(x.PageKey, logger, "ExitPages.PageKey"),
            Exits = x.Exits,
            ExitRate = x.ExitRate
        }).ToList();
    }

    private static List<SourceRow> RedactSources(List<SourceRow>? rows, ILogger? logger)
    {
        if (rows == null) return new List<SourceRow>();
        return rows.Select(x => new SourceRow
        {
            Source = CleanLabel(x.Source, logger, "SourcePerf.Source"),
            Medium = string.IsNullOrWhiteSpace(x.Medium) ? null : CleanLabel(x.Medium, logger, "SourcePerf.Medium"),
            Campaign = string.IsNullOrWhiteSpace(x.Campaign) ? null : CleanLabel(x.Campaign, logger, "SourcePerf.Campaign"),
            Sessions = x.Sessions,
            VerifiedLeads = x.VerifiedLeads,
            SessionConversionRate = x.SessionConversionRate
        }).ToList();
    }

    private static List<AbandonRow> RedactAbandonment(List<AbandonRow>? rows, ILogger? logger)
    {
        if (rows == null) return new List<AbandonRow>();
        return rows.Select(x => new AbandonRow
        {
            QuoteType = CleanLabel(x.QuoteType, logger, "FormAbandonment.QuoteType"),
            Abandons = x.Abandons,
            Starts = x.Starts,
            AbandonRate = x.AbandonRate
        }).ToList();
    }

    private static List<AiCampaignRow> RedactCampaigns(List<AiCampaignRow>? rows, ILogger? logger)
    {
        if (rows == null) return new List<AiCampaignRow>();
        return rows.Select(x => new AiCampaignRow
        {
            CampaignName = CleanLabel(x.CampaignName, logger, "ActiveCampaigns.CampaignName"),
            Spend        = x.Spend,
            Impressions  = x.Impressions,
            Clicks       = x.Clicks,
            Ctr          = x.Ctr,
            Cpc          = x.Cpc,
            Leads        = x.Leads
        })
        .Where(x => x.CampaignName != "[redacted]")
        .ToList();
    }

    private static MetaSignalAiPayload? RedactMetaSignal(MetaSignalAiPayload? payload, ILogger? logger)
    {
        if (payload == null) return null;

        return new MetaSignalAiPayload
        {
            TotalSignalEvents = payload.TotalSignalEvents,
            TotalVisitors = payload.TotalVisitors,
            HighIntentVisitors = payload.HighIntentVisitors,
            LeadReadyVisitors = payload.LeadReadyVisitors,
            SubmittedLeads = payload.SubmittedLeads,
            SubmitAttemptsWithoutLead = payload.SubmitAttemptsWithoutLead,
            HighIntentAbandons = payload.HighIntentAbandons,
            ContactStepAbandons = payload.ContactStepAbandons,
            SignalToLeadConversionRate = payload.SignalToLeadConversionRate,
            RecommendedOptimizationEvent = CleanLabel(payload.RecommendedOptimizationEvent, logger, "MetaSignal.RecommendedOptimizationEvent"),
            BestPerformingLandingPageVersion = CleanLabel(payload.BestPerformingLandingPageVersion, logger, "MetaSignal.BestPerformingLandingPageVersion"),
            WorstFrictionStep = CleanLabel(payload.WorstFrictionStep, logger, "MetaSignal.WorstFrictionStep"),
            VisitorsByScoreTier = (payload.VisitorsByScoreTier ?? new List<MetaSignalTierAiRow>())
                .Select(x => new MetaSignalTierAiRow
                {
                    ScoreTier = CleanLabel(x.ScoreTier, logger, "MetaSignal.VisitorsByScoreTier.ScoreTier"),
                    Visitors = x.Visitors
                }).ToList(),
            AverageScoreByCampaign = (payload.AverageScoreByCampaign ?? new List<MetaSignalAverageAiRow>())
                .Select(x => new MetaSignalAverageAiRow
                {
                    Label = CleanLabel(x.Label, logger, "MetaSignal.AverageScoreByCampaign.Label"),
                    AverageScore = x.AverageScore
                })
                .Where(x => x.Label != "[redacted]")
                .ToList(),
            AverageScoreByPageVariant = (payload.AverageScoreByPageVariant ?? new List<MetaSignalAverageAiRow>())
                .Select(x => new MetaSignalAverageAiRow
                {
                    Label = CleanLabel(x.Label, logger, "MetaSignal.AverageScoreByPageVariant.Label"),
                    AverageScore = x.AverageScore
                })
                .Where(x => x.Label != "[redacted]")
                .ToList(),
            EventLadder = (payload.EventLadder ?? new List<MetaSignalLadderAiRow>())
                .Select(x => new MetaSignalLadderAiRow
                {
                    StepLabel = CleanLabel(x.StepLabel, logger, "MetaSignal.EventLadder.StepLabel"),
                    Visitors = x.Visitors,
                    ProgressionRate = x.ProgressionRate
                })
                .Where(x => x.StepLabel != "[redacted]")
                .ToList()
        };
    }

    // ── PII Detection ─────────────────────────────────────────────────────────

    public static bool LooksPii(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        // Check if the field name itself indicates PII
        if (PiiFieldNames.Contains(value.Trim())) return true;

        // Email pattern
        if (EmailPattern.IsMatch(value)) return true;

        // Phone pattern
        if (PhonePattern.IsMatch(value)) return true;

        return false;
    }

    private static string DetectPiiType(string value)
    {
        if (EmailPattern.IsMatch(value)) return "email";
        if (PhonePattern.IsMatch(value)) return "phone";
        return "pii-field-name";
    }
}
