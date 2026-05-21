using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Leads;

public sealed class WebsiteLifeLeadCaptureService : IWebsiteLifeLeadCaptureService
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<WebsiteLifeLeadCaptureService> _logger;

    public WebsiteLifeLeadCaptureService(MasterAppDbContext db, ILogger<WebsiteLifeLeadCaptureService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<WebsiteLifeLeadCaptureResult> UpsertAsync(WebsiteLifeLeadCaptureRequest request, CancellationToken cancellationToken = default)
    {
        var bucket = WorkstationLeadBuckets.ResolveWebsiteLeadBucket(request.ProductType, request.OfferKey);
        var agentUserId = await ResolveAgentUserIdAsync(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(agentUserId))
        {
            _logger.LogWarning(
                "Website life lead {WebsiteLeadId} could not be attached to a workstation owner for bucket {Bucket}.",
                request.WebsiteLeadId,
                bucket);
            return new WebsiteLifeLeadCaptureResult(false, false, null, bucket, null, "NoAgentOwner");
        }

        var websiteLead = await _db.WebsiteLeads
            .FirstOrDefaultAsync(x => x.LeadId == request.WebsiteLeadId, cancellationToken);

        if (websiteLead == null)
        {
            _logger.LogWarning(
                "Website lead {WebsiteLeadId} was not found during workstation capture; continuing with request fallback only.",
                request.WebsiteLeadId);
        }

        var submittedUtc = websiteLead?.CreatedUtc
            ?? (request.SubmittedUtc == default ? DateTime.UtcNow : request.SubmittedUtc);
        var normalizedPhone = NormalizePhoneKey(websiteLead?.Phone ?? request.Phone);
        var normalizedEmail = NormalizeEmailKey(websiteLead?.Email ?? request.Email);
        var requestedAmount = request.CoverageAmount.HasValue && request.CoverageAmount.Value > 0
            ? request.CoverageAmount.Value.ToString("N0", CultureInfo.InvariantCulture)
            : null;

        WorkstationLeadProfile? existing = null;
        if (!string.IsNullOrWhiteSpace(normalizedPhone) || !string.IsNullOrWhiteSpace(normalizedEmail))
        {
            var candidates = await _db.WorkstationLeadProfiles
                .Where(x =>
                    x.AgentUserId == agentUserId &&
                    ((x.OriginalLeadType != null && x.OriginalLeadType == bucket) ||
                     ((x.OriginalLeadType == null || x.OriginalLeadType == "") && x.Bucket == bucket)))
                .OrderByDescending(x => x.UpdatedUtc)
                .ThenByDescending(x => x.CreatedUtc)
                .ToListAsync(cancellationToken);

            existing = candidates.FirstOrDefault(x =>
                (!string.IsNullOrWhiteSpace(normalizedPhone) && NormalizePhoneKey(x.Phone) == normalizedPhone) ||
                (!string.IsNullOrWhiteSpace(normalizedEmail) && NormalizeEmailKey(x.Email) == normalizedEmail));
        }

        WorkstationLeadProfile lead;
        var created = false;
        if (existing == null)
        {
            var leadId = request.WebsiteLeadId.ToString("N");
            lead = new WorkstationLeadProfile
            {
                LeadId = leadId,
                AgentUserId = agentUserId,
                Bucket = bucket,
                OriginalLeadType = bucket,
                FirstName = Clean(websiteLead?.FirstName ?? request.FirstName) ?? "",
                LastName = Clean(websiteLead?.LastName ?? request.LastName) ?? "",
                Email = Clean(websiteLead?.Email ?? request.Email) ?? "",
                Phone = !string.IsNullOrWhiteSpace(normalizedPhone) ? normalizedPhone : (Clean(websiteLead?.Phone ?? request.Phone) ?? ""),
                Phone2 = null,
                State = NormalizeStateValue(ExtractLeadState(websiteLead, request.State)),
                Age = request.Age?.ToString(CultureInfo.InvariantCulture) ?? "",
                LoanAmount = requestedAmount,
                CrmStage = "New",
                CrmStatus = "Lead",
                CrmOrder = submittedUtc.Ticks * 1000L,
                CreatedUtc = submittedUtc,
                UpdatedUtc = submittedUtc
            };

            _db.WorkstationLeadProfiles.Add(lead);
            created = true;
        }
        else
        {
            lead = existing;
            lead.AgentUserId = agentUserId;
            lead.OriginalLeadType = bucket;
            lead.FirstName = Clean(websiteLead?.FirstName ?? request.FirstName) ?? lead.FirstName;
            lead.LastName = Clean(websiteLead?.LastName ?? request.LastName) ?? lead.LastName;
            var email = Clean(websiteLead?.Email ?? request.Email);
            if (!string.IsNullOrWhiteSpace(email))
                lead.Email = email;
            if (!string.IsNullOrWhiteSpace(normalizedPhone))
                lead.Phone = normalizedPhone;
            var extractedState = ExtractLeadState(websiteLead, request.State);
            if (!string.IsNullOrWhiteSpace(extractedState))
                lead.State = NormalizeStateValue(extractedState);
            if (request.Age.HasValue)
                lead.Age = request.Age.Value.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(requestedAmount))
                lead.LoanAmount = requestedAmount;

            var currentBucket = WorkstationLeadBuckets.NormalizeBucket(lead.Bucket);
            if (!string.IsNullOrWhiteSpace(currentBucket))
                lead.Bucket = bucket;

            if (string.IsNullOrWhiteSpace(lead.CrmStage))
                lead.CrmStage = "New";
            if (string.IsNullOrWhiteSpace(lead.CrmStatus))
                lead.CrmStatus = "Lead";

            lead.CrmOrder = submittedUtc.Ticks * 1000L;
            lead.UpdatedUtc = submittedUtc;
        }

        if (websiteLead != null)
        {
            await UpsertIntakeLinkAsync(websiteLead, lead, agentUserId, bucket, submittedUtc, cancellationToken);
        }

        return new WebsiteLifeLeadCaptureResult(true, created, lead.LeadId, bucket, agentUserId, null);
    }

    private async Task UpsertIntakeLinkAsync(
        WebsiteLead websiteLead,
        WorkstationLeadProfile lead,
        string agentUserId,
        string bucket,
        DateTime submittedUtc,
        CancellationToken cancellationToken)
    {
        var metadata = ParseMetadata(websiteLead.MetadataJson);
        var intakeLink = await _db.WebsiteLeadIntakeLinks
            .FirstOrDefaultAsync(x => x.WebsiteLeadRowId == websiteLead.Id, cancellationToken);

        if (intakeLink == null)
        {
            intakeLink = new WebsiteLeadIntakeLink
            {
                Id = Guid.NewGuid(),
                WebsiteLeadRowId = websiteLead.Id,
                WebsiteLeadPublicId = websiteLead.LeadId
            };
            _db.WebsiteLeadIntakeLinks.Add(intakeLink);
        }

        intakeLink.WorkstationLeadId = lead.LeadId;
        intakeLink.AgentUserId = agentUserId;
        intakeLink.Bucket = bucket;
        intakeLink.SubmittedUtc = submittedUtc;
        intakeLink.CapturedUtc = DateTime.UtcNow;
        intakeLink.SourcePageKey = Clean(websiteLead.SourcePageKey);
        intakeLink.SourceCtaKey = Clean(websiteLead.SourceCtaKey);
        intakeLink.PageVariant = ReadMetadataString(metadata, "PageVariant");
        intakeLink.PageMode = ReadMetadataString(metadata, "PageMode");
        intakeLink.PagePath = ReadMetadataString(metadata, "PagePath");
        intakeLink.LandingPageUrl = ReadMetadataString(metadata, "LandingPageUrl");
        intakeLink.ReferrerUrl = ReadMetadataString(metadata, "ReferrerUrl");
        intakeLink.InterestType = Clean(websiteLead.InterestType);
        intakeLink.OfferKey = ReadMetadataString(metadata, "OfferKey");
        intakeLink.ProductType = ReadMetadataString(metadata, "ProductType");
        intakeLink.UtmSource = Clean(websiteLead.UtmSource);
        intakeLink.UtmMedium = Clean(websiteLead.UtmMedium);
        intakeLink.UtmCampaign = Clean(websiteLead.UtmCampaign);
        intakeLink.UtmId = Clean(websiteLead.UtmId) ?? ReadMetadataString(metadata, "UtmId");
        intakeLink.UtmTerm = ReadMetadataString(metadata, "UtmTerm");
        intakeLink.UtmContent = ReadMetadataString(metadata, "UtmContent");
        intakeLink.Fbclid = Clean(websiteLead.Fbclid) ?? ReadMetadataString(metadata, "Fbclid");
        intakeLink.MetaCampaignId = Clean(websiteLead.MetaCampaignId) ?? ReadMetadataString(metadata, "MetaCampaignId");
        intakeLink.MetaAdSetId = Clean(websiteLead.MetaAdSetId) ?? ReadMetadataString(metadata, "MetaAdSetId");
        intakeLink.MetaAdId = Clean(websiteLead.MetaAdId) ?? ReadMetadataString(metadata, "MetaAdId");
        intakeLink.SessionId = Clean(websiteLead.SessionId);
        intakeLink.VisitorId = Clean(websiteLead.VisitorId);
        intakeLink.DiscoverySummaryJson = BuildDiscoverySummaryJson(metadata);
        intakeLink.EstimateSummary = BuildEstimateSummary(metadata);
        intakeLink.RecommendationPrimaryKey = ReadMetadataString(metadata, "RecommendationPrimaryKey");
        intakeLink.RecommendationPrimaryTitle = ReadMetadataString(metadata, "RecommendationPrimaryTitle");
        intakeLink.RecommendationSecondaryKey = ReadMetadataString(metadata, "RecommendationSecondaryKey");
        intakeLink.RecommendationSecondaryTitle = ReadMetadataString(metadata, "RecommendationSecondaryTitle");
        intakeLink.SnapshotJson = websiteLead.MetadataJson;
    }

    private async Task<string?> ResolveAgentUserIdAsync(WebsiteLifeLeadCaptureRequest request, CancellationToken cancellationToken)
    {
        var normalizedSlug = Clean(request.AgentSlug);
        var normalizedRecipientEmail = NormalizeEmailKey(request.RecipientEmail);
        var normalizedTrackingEmail = string.Empty;

        var trackingCandidates = await _db.AgentTrackingProfiles
            .AsNoTracking()
            .Where(x =>
                (request.AgentTrackingProfileId.HasValue && x.Id == request.AgentTrackingProfileId.Value) ||
                (!string.IsNullOrWhiteSpace(normalizedSlug) && x.Slug == normalizedSlug) ||
                (!string.IsNullOrWhiteSpace(normalizedRecipientEmail) && x.AgentUpn.ToLower() == normalizedRecipientEmail))
            .ToListAsync(cancellationToken);

        var trackingProfile = trackingCandidates
            .OrderByDescending(x => request.AgentTrackingProfileId.HasValue && x.Id == request.AgentTrackingProfileId.Value)
            .ThenByDescending(x => !string.IsNullOrWhiteSpace(normalizedSlug) && x.Slug == normalizedSlug)
            .ThenByDescending(x => !string.IsNullOrWhiteSpace(normalizedRecipientEmail) && x.AgentUpn.ToLower() == normalizedRecipientEmail)
            .ThenByDescending(x => x.UpdatedUtc)
            .FirstOrDefault();

        if (trackingProfile != null)
        {
            var trackingOwnerId = NormalizeAgentUserId(trackingProfile.AgentUserId);
            if (!string.IsNullOrWhiteSpace(trackingOwnerId))
                return trackingOwnerId;

            normalizedTrackingEmail = NormalizeEmailKey(trackingProfile.AgentUpn);
        }

        var agentEmailKey = !string.IsNullOrWhiteSpace(normalizedTrackingEmail)
            ? normalizedTrackingEmail
            : normalizedRecipientEmail;

        if (string.IsNullOrWhiteSpace(agentEmailKey))
            return null;

        var profile = await _db.AgentProfiles
            .AsNoTracking()
            .Where(x =>
                x.NormalizedEmail == agentEmailKey ||
                x.AgentUpn.ToLower() == agentEmailKey)
            .OrderByDescending(x => !string.IsNullOrWhiteSpace(x.AgentUserId))
            .ThenByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return NormalizeAgentUserId(profile?.AgentUserId);
    }

    private static string ExtractLeadState(WebsiteLead? websiteLead, string? fallbackState)
    {
        if (!string.IsNullOrWhiteSpace(fallbackState))
            return fallbackState;

        var metadata = ParseMetadata(websiteLead?.MetadataJson);
        return ReadMetadataString(metadata, "State")
            ?? ReadMetadataString(metadata, "AddressState")
            ?? string.Empty;
    }

    private static string BuildDiscoverySummaryJson(JsonElement? metadata)
    {
        if (metadata is not JsonElement root || root.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var summary = new List<IntakeSummaryItem>();
        AppendSummary(summary, root, "ProtectingWho", "Protecting");
        AppendSummary(summary, root, "CoverageGoal", "Goal");
        AppendSummary(summary, root, "CoverageAmountOption", "Coverage");
        AppendSummary(summary, root, "CoverageAmount", "Requested");
        AppendSummary(summary, root, "TobaccoUse", "Tobacco");
        AppendSummary(summary, root, "Age", "Age");
        AppendSummary(summary, root, "AgeRange", "Age Range");
        AppendSummary(summary, root, "State", "State");
        AppendSummary(summary, root, "Answer1", "Answer 1");
        AppendSummary(summary, root, "Answer2", "Answer 2");
        AppendSummary(summary, root, "Answer3", "Answer 3");
        AppendSummary(summary, root, "Answer4", "Answer 4");
        AppendSummary(summary, root, "PolicyFormType", "Policy Form");
        AppendSummary(summary, root, "DwellingType", "Dwelling");
        AppendSummary(summary, root, "AddressState", "State");
        AppendSummary(summary, root, "DriverCount", "Drivers");
        AppendSummary(summary, root, "VehicleCount", "Vehicles");
        AppendSummary(summary, root, "PriorCarrier", "Prior Carrier");
        AppendSummary(summary, root, "BusinessName", "Business");
        AppendSummary(summary, root, "EmploymentType", "Employment");
        AppendSummary(summary, root, "Occupation", "Occupation");
        AppendSummary(summary, root, "HouseholdSize", "Household Size");
        AppendSummary(summary, root, "PrimaryConcern", "Primary Concern");
        AppendSummary(summary, root, "CoverageType", "Coverage Type");

        return summary.Count == 0 ? string.Empty : JsonSerializer.Serialize(summary);
    }

    private static string? BuildEstimateSummary(JsonElement? metadata)
    {
        if (metadata is not JsonElement root || root.ValueKind != JsonValueKind.Object)
            return null;

        var parts = new List<string>();
        var primary = ReadMetadataString(root, "RecommendationPrimaryTitle");
        var secondary = ReadMetadataString(root, "RecommendationSecondaryTitle");
        var coverage = ReadMetadataString(root, "CoverageAmount") ?? ReadMetadataString(root, "CoverageAmountOption");

        if (!string.IsNullOrWhiteSpace(primary))
            parts.Add($"Best fit: {HumanizeSummaryValue(primary)}");
        if (!string.IsNullOrWhiteSpace(secondary))
            parts.Add($"Also consider: {HumanizeSummaryValue(secondary)}");
        if (!string.IsNullOrWhiteSpace(coverage))
            parts.Add($"Coverage target: {HumanizeSummaryValue(coverage)}");

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static void AppendSummary(List<IntakeSummaryItem> summary, JsonElement root, string propertyName, string label)
    {
        var value = ReadMetadataString(root, propertyName);
        if (string.IsNullOrWhiteSpace(value))
            return;

        summary.Add(new IntakeSummaryItem(label, HumanizeSummaryValue(value)));
    }

    private static JsonElement? ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadMetadataString(JsonElement? metadata, string propertyName)
        => metadata is JsonElement root ? ReadMetadataString(root, propertyName) : null;

    private static string? ReadMetadataString(JsonElement metadata, string propertyName)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var prop in metadata.EnumerateObject())
        {
            if (!string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            return prop.Value.ValueKind switch
            {
                JsonValueKind.String => Clean(prop.Value.GetString()),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => Clean(prop.Value.ToString())
            };
        }

        return null;
    }

    private static string HumanizeSummaryValue(string value)
    {
        var trimmed = Clean(value);
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        if (decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var numeric)
            && trimmed.All(ch => char.IsDigit(ch) || ch == '.' || ch == ','))
        {
            if (numeric >= 1000)
                return numeric.ToString("C0", CultureInfo.GetCultureInfo("en-US"));
            if (numeric % 1 == 0)
                return numeric.ToString("0", CultureInfo.InvariantCulture);
        }

        var normalized = trimmed
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Trim();

        if (string.Equals(normalized, "iul", StringComparison.OrdinalIgnoreCase))
            return "IUL";

        return CultureInfo.GetCultureInfo("en-US").TextInfo.ToTitleCase(normalized.ToLowerInvariant());
    }

    private static string NormalizePhoneKey(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return string.Empty;

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && digits.StartsWith("1", StringComparison.Ordinal))
            digits = digits[1..];
        return digits;
    }

    private static string NormalizeEmailKey(string? email)
        => (email ?? string.Empty).Trim().ToLowerInvariant();

    private static string NormalizeStateValue(string? state)
        => (state ?? string.Empty).Trim().ToUpperInvariant();

    private static string NormalizeAgentUserId(string? agentUserId)
        => (agentUserId ?? string.Empty).Trim().ToLowerInvariant();

    private static string? Clean(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private sealed record IntakeSummaryItem(string Label, string Value);
}
