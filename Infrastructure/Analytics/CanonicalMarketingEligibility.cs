using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Analytics;

/// <summary>Destination-neutral decision over persisted, owner-matched first-party evidence.</summary>
public static class CanonicalMarketingEligibility
{
    public const int MinimumLikelyHumanScore = 80;
    public sealed record Decision(bool Eligible, string Reason, string Bucket, int Score);

    public static async Task RecordBlockAsync(MasterAppDbContext db, AnalyticsEvent source, string provider, string reason, CancellationToken ct)
    {
        var row = await db.AnalyticsEvents.SingleAsync(e => e.Id == source.Id, ct);
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(row.MetadataJson ?? "{}")!.AsObject();
        metadata[provider + "ProjectionBlock"] = reason;
        row.MetadataJson = metadata.ToJsonString();
        await db.SaveChangesAsync(ct);
    }

    public static async Task<Decision> ResolveAsync(MasterAppDbContext db, AnalyticsEvent source, CancellationToken ct = default)
    {
        var persisted = await db.AnalyticsEvents.AsNoTracking().SingleOrDefaultAsync(e => e.EventId == source.EventId, ct);
        if (persisted is null) return new(false, "canonical_source_missing", "reviewed_needed", 0);
        source = persisted;
        if (source.TrackingVersion == "crm-production-authority-v1" && Guid.TryParse(
            CanonicalAdvertisingEventProjection.ReadString(source.MetadataJson, "productionRecordId"), out var productionId))
        {
            var production = await db.ProductionRecords.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productionId, ct);
            var sourceStage = CanonicalAdvertisingEventProjection.ResolveEventName(source) switch
            { "ApplicationSubmitted" => ProductionStatus.Submitted, "PolicyIssued" => ProductionStatus.Issued, _ => ProductionStatus.Paid };
            if (production is null || production.Status < sourceStage ||
                CanonicalConversionValueProjection.Resolve(source.MetadataJson)?.AmountMinorUnits !=
                    decimal.ToInt64(decimal.Round(production.PersonalAmount * 100m, 0, MidpointRounding.AwayFromZero)))
                return new(false, "production_outcome_requires_reconciliation", "reviewed_needed", 0);
        }
        var evidence = new List<AnalyticsEvent> { source };
        // No visitor-only cross-session promotion. A downstream outcome must retain its acquisition session.
        if (!string.IsNullOrWhiteSpace(source.SessionId))
        {
            var query = db.AnalyticsEvents.AsNoTracking().Where(e =>
                e.AgentTrackingProfileId == source.AgentTrackingProfileId && e.CommerceBusinessId == source.CommerceBusinessId &&
                e.Host == source.Host && e.SessionId == source.SessionId &&
                e.VisitorId == source.VisitorId && e.EventUtc <= DateTime.UtcNow)
                .OrderByDescending(e => e.EventUtc).ThenByDescending(e => e.Id).Take(1001);
            evidence = await UnifiedEventMapper.ProjectBehaviorEvidence(query, includeMetadata: true).ToListAsync(ct);
            if (evidence.Count > 1000) return new(false, "session_evidence_limit_exceeded", "reviewed_needed", 0);
            if (!evidence.Any(e => e.EventId == source.EventId)) evidence.Add(source);
        }
        var bucket = TrafficQualityBucketFilters.Classify(evidence);
        var score = evidence.Select(TrafficQualityBucketFilters.HumanScore).DefaultIfEmpty().Max();
        var eligible = bucket == TrafficQualityMode.RealHumanTraffic ||
            bucket == TrafficQualityMode.LikelyHuman && score >= MinimumLikelyHumanScore;
        var consent = CanonicalAdvertisingEventProjection.ReadBoolean(source.MetadataJson, "measurementConsentAllowed") ??
            evidence.Where(e => e.EventUtc <= source.EventUtc)
                .Select(e => CanonicalAdvertisingEventProjection.ReadBoolean(e.MetadataJson, "measurementConsentAllowed"))
                .FirstOrDefault(value => value.HasValue);
        if (!string.IsNullOrWhiteSpace(source.VisitorId))
        {
            var latestChoice = await db.AnalyticsEvents.AsNoTracking().Where(e =>
                e.AgentTrackingProfileId == source.AgentTrackingProfileId && e.CommerceBusinessId == source.CommerceBusinessId &&
                e.Host == source.Host && e.VisitorId == source.VisitorId && e.EventType == "measurement_consent_changed" &&
                e.EventUtc <= DateTime.UtcNow).OrderByDescending(e => e.EventUtc).ThenByDescending(e => e.Id)
                .Select(e => e.MetadataJson).FirstOrDefaultAsync(ct);
            // A later denial applies across sessions; a new grant cannot invent missing acquisition consent.
            if (CanonicalAdvertisingEventProjection.ReadBoolean(latestChoice, "measurementConsentAllowed") == false) consent = false;
        }
        if (eligible && consent != true)
            return new(false, consent == false ? "measurement_consent_denied" : "measurement_consent_unavailable",
                TrafficQualityBucketFilters.ToClientValue(bucket), score);
        return new(eligible, eligible ? "human_evidence_eligible" : "human_evidence_" + TrafficQualityBucketFilters.ToClientValue(bucket),
            TrafficQualityBucketFilters.ToClientValue(bucket), score);
    }
}
