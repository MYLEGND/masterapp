using System;
using System.Globalization;
using System.Linq;
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
        var bucket = WorkstationLeadBuckets.ResolveWebsiteLifeBucket(request.ProductType, request.OfferKey);
        var agentUserId = await ResolveAgentUserIdAsync(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(agentUserId))
        {
            _logger.LogWarning(
                "Website life lead {WebsiteLeadId} could not be attached to a workstation owner for bucket {Bucket}.",
                request.WebsiteLeadId,
                bucket);
            return new WebsiteLifeLeadCaptureResult(false, false, null, bucket, null, "NoAgentOwner");
        }

        var submittedUtc = request.SubmittedUtc == default ? DateTime.UtcNow : request.SubmittedUtc;
        var normalizedPhone = NormalizePhoneKey(request.Phone);
        var normalizedEmail = NormalizeEmailKey(request.Email);
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

        if (existing == null)
        {
            var leadId = request.WebsiteLeadId.ToString("N");
            var createdLead = new WorkstationLeadProfile
            {
                LeadId = leadId,
                AgentUserId = agentUserId,
                Bucket = bucket,
                OriginalLeadType = bucket,
                FirstName = Clean(request.FirstName) ?? "",
                LastName = Clean(request.LastName) ?? "",
                Email = Clean(request.Email) ?? "",
                Phone = !string.IsNullOrWhiteSpace(normalizedPhone) ? normalizedPhone : (Clean(request.Phone) ?? ""),
                Phone2 = null,
                State = NormalizeStateValue(request.State),
                Age = request.Age?.ToString(CultureInfo.InvariantCulture) ?? "",
                LoanAmount = requestedAmount,
                CrmStage = "New",
                CrmStatus = "Lead",
                CrmOrder = submittedUtc.Ticks * 1000L,
                CreatedUtc = submittedUtc,
                UpdatedUtc = submittedUtc
            };

            _db.WorkstationLeadProfiles.Add(createdLead);

            return new WebsiteLifeLeadCaptureResult(true, true, leadId, bucket, agentUserId, null);
        }

        existing.AgentUserId = agentUserId;
        existing.OriginalLeadType = bucket;
        existing.FirstName = Clean(request.FirstName) ?? existing.FirstName;
        existing.LastName = Clean(request.LastName) ?? existing.LastName;
        if (!string.IsNullOrWhiteSpace(request.Email))
            existing.Email = Clean(request.Email) ?? existing.Email;
        if (!string.IsNullOrWhiteSpace(normalizedPhone))
            existing.Phone = normalizedPhone;
        if (!string.IsNullOrWhiteSpace(request.State))
            existing.State = NormalizeStateValue(request.State);
        if (request.Age.HasValue)
            existing.Age = request.Age.Value.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(requestedAmount))
            existing.LoanAmount = requestedAmount;

        var currentBucket = WorkstationLeadBuckets.NormalizeBucket(existing.Bucket);
        if (!string.IsNullOrWhiteSpace(currentBucket))
            existing.Bucket = bucket;

        if (string.IsNullOrWhiteSpace(existing.CrmStage))
            existing.CrmStage = "New";
        if (string.IsNullOrWhiteSpace(existing.CrmStatus))
            existing.CrmStatus = "Lead";

        existing.CrmOrder = submittedUtc.Ticks * 1000L;
        existing.UpdatedUtc = submittedUtc;

        return new WebsiteLifeLeadCaptureResult(true, false, existing.LeadId, bucket, agentUserId, null);
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
}
