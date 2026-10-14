using Microsoft.Extensions.Logging;
using Shared.Analytics;
using System.Text.Json;
using Domain.Entities;
using System.Collections.Concurrent;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Analytics;

public interface IMetaSendAuthority
{
    Task<MetaSendAuthorityDecision> TrySendAsync(MetaSendAuthorityRequest request, CancellationToken cancellationToken = default);
    void Complete(MetaSendAuthorityDecision decision, MetaConversionsApiResult? result, MetaConversionsApiEventRequest request);
}

public sealed class MetaSendAuthority : IMetaSendAuthority
{
    private static readonly SemaphoreSlim ReservationGate = new(1, 1);
    private const int ReservationTtlMinutes = 10;
    private const int SentTtlHours = 6;
    private static readonly ConcurrentDictionary<string, AuthorityReservation> Reservations = new(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, int> SourcePriorities =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [MetaSendAuthoritySources.MetaSignalOutcomeDispatcherHostedService] = 400,
            [MetaSendAuthoritySources.MetaSignalAnalyticsBridge] = 300,
            [MetaSendAuthoritySources.Controllers] = 100
        };

    private readonly MasterAppDbContext _db;
    private readonly ILogger<MetaSendAuthority> _logger;

    public MetaSendAuthority(MasterAppDbContext db, ILogger<MetaSendAuthority> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<MetaSendAuthorityDecision> TrySendAsync(MetaSendAuthorityRequest request, CancellationToken cancellationToken = default)
    {
        await ReservationGate.WaitAsync(cancellationToken);
        try { return await ReserveAsync(request, cancellationToken); }
        finally { ReservationGate.Release(); }
    }

    private async Task<MetaSendAuthorityDecision> ReserveAsync(
        MetaSendAuthorityRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var normalized = Normalize(request);
            PruneExpiredReservations(DateTime.UtcNow);

            if (!string.Equals(normalized.Source, MetaSendAuthoritySources.MetaSignalOutcomeDispatcherHostedService, StringComparison.OrdinalIgnoreCase) &&
                TryAllowNestedReservation(normalized, out var nestedDecision))
                return nestedDecision;

            if (TryBlockOrReplaceActiveReservation(normalized, out var activeDecision))
                return activeDecision;

            if (await HasSentMatchAsync(normalized, cancellationToken))
            {
                _logger.LogInformation(
                    "MetaSendAuthority blocked duplicate event {EventType} lead {LeadId}",
                    normalized.EventType,
                    normalized.LeadId);

                return new MetaSendAuthorityDecision(
                    Allowed: false,
                    EventType: normalized.EventType,
                    LeadId: normalized.LeadId,
                    Source: normalized.Source,
                    DedupeKey: normalized.DedupeKey,
                    ReservationToken: null,
                    Status: "blocked_duplicate",
                    Note: "meta_signal_event_already_sent");
            }

            var token = string.IsNullOrWhiteSpace(normalized.ReservationToken)
                ? Guid.NewGuid().ToString("N")
                : normalized.ReservationToken!;
            var nowUtc = DateTime.UtcNow;

            var persistedRowId = await TryClaimPersistedSignalAsync(
                normalized,
                token,
                nowUtc,
                cancellationToken);
            if (persistedRowId is DurableClaimBlocked or DurableClaimMissing)
            {
                return new MetaSendAuthorityDecision(
                    Allowed: false,
                    EventType: normalized.EventType,
                    LeadId: normalized.LeadId,
                    Source: normalized.Source,
                    DedupeKey: normalized.DedupeKey,
                    ReservationToken: null,
                    Status: "blocked_duplicate",
                    Note: persistedRowId == DurableClaimMissing ? "canonical_dispatch_signal_missing" : "durable_dispatch_claim_active");
            }

            Reservations[normalized.DedupeKey] = new AuthorityReservation(
                Token: token,
                DedupeKey: normalized.DedupeKey,
                EventType: normalized.EventType,
                LeadId: normalized.LeadId,
                SessionId: normalized.SessionId,
                VisitorId: normalized.VisitorId,
                EventId: normalized.EventId,
                ExplicitDedupeKey: normalized.ExplicitDedupeKey,
                Source: normalized.Source,
                Priority: normalized.Priority,
                ReservedUtc: nowUtc,
                ExpiresUtc: nowUtc.AddMinutes(ReservationTtlMinutes),
                Sent: false,
                RowId: persistedRowId > 0 ? persistedRowId : null);

            _logger.LogInformation(
                "MetaSendAuthority allowed event {EventType} source {Source}",
                normalized.EventType,
                normalized.Source);

            return new MetaSendAuthorityDecision(
                Allowed: true,
                EventType: normalized.EventType,
                LeadId: normalized.LeadId,
                Source: normalized.Source,
                DedupeKey: normalized.DedupeKey,
                ReservationToken: token,
                Status: "allowed",
                Note: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var source = NormalizeSource(request.Source);
            var eventType = NormalizeText(request.EventType) ?? "unknown";
            _logger.LogWarning(
                ex,
                "MetaSendAuthority unavailable for event {EventType} source {Source}",
                eventType,
                source);

            return new MetaSendAuthorityDecision(
                Allowed: false,
                EventType: eventType,
                LeadId: request.LeadId,
                Source: source,
                DedupeKey: BuildDedupeKey(
                    eventType,
                    request.LeadId,
                    request.SessionId,
                    request.VisitorId,
                    request.EventUtc,
                    request.DeduplicationKey,
                    request.EventId,
                    request.CommerceBusinessId,
                    request.AgentTrackingProfileId),
                ReservationToken: null,
                Status: "authority_unavailable",
                Note: "authority_unavailable");
        }
    }

    public void Complete(MetaSendAuthorityDecision decision, MetaConversionsApiResult? result, MetaConversionsApiEventRequest request)
    {
        // No response means interrupted transport. Keep the durable lease recoverable.
        if (result is null || !decision.Allowed || string.IsNullOrWhiteSpace(decision.DedupeKey) ||
            string.IsNullOrWhiteSpace(decision.ReservationToken) ||
            !Reservations.TryGetValue(decision.DedupeKey, out var reservation) ||
            !string.Equals(reservation.Token, decision.ReservationToken, StringComparison.Ordinal)) return;
        try
        {
            if (reservation.RowId is { } rowId)
            {
                var current = _db.MetaSignalEvents.AsNoTracking().SingleOrDefault(row =>
                    row.Id == rowId && row.MetaDispatchClaimToken == reservation.Token && !row.MetaServerSent);
                if (current is null) return;
                var metadata = MergeDispatchMetadata(current.MetadataJson, result);
                var updated = _db.MetaSignalEvents.Where(row => row.Id == rowId &&
                    row.MetaDispatchClaimToken == reservation.Token && !row.MetaServerSent && row.MetadataJson == current.MetadataJson)
                    .ExecuteUpdate(setters => setters
                        .SetProperty(row => row.MetaServerSent, result.Sent)
                        .SetProperty(row => row.MetadataJson, metadata)
                        .SetProperty(row => row.FbclidPresent, !string.IsNullOrWhiteSpace(request.Fbclid))
                        .SetProperty(row => row.FbcPresent, !string.IsNullOrWhiteSpace(request.Fbc))
                        .SetProperty(row => row.FbpPresent, !string.IsNullOrWhiteSpace(request.Fbp))
                        .SetProperty(row => row.MetaDispatchClaimToken, (string?)null)
                        .SetProperty(row => row.MetaDispatchClaimedUtc, (DateTime?)null)
                        .SetProperty(row => row.MetaDispatchClaimExpiresUtc, (DateTime?)null));
                if (updated != 1) return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MetaSendAuthority could not persist completion for event {EventType} row {RowId}. The durable lease remains fail-closed until expiry.", decision.EventType, reservation.RowId);
            return;
        }
        if (result.Sent)
            Reservations[decision.DedupeKey] = reservation with { Sent = true, ExpiresUtc = DateTime.UtcNow.AddHours(SentTtlHours) };
        else
            Reservations.TryRemove(decision.DedupeKey, out _);
    }

    internal static Task<int> PersistPreDispatchDecisionAsync(MasterAppDbContext db, MetaSignalEvent row,
        MetaConversionsApiResult result, CancellationToken ct = default)
    {
        if (result.Attempted) throw new ArgumentException("Provider receipts require an owned dispatch claim.", nameof(result));
        var now = DateTime.UtcNow;
        var metadata = MergeDispatchMetadata(row.MetadataJson, result);
        return db.MetaSignalEvents.Where(current => current.Id == row.Id && !current.MetaServerSent &&
            current.MetadataJson == row.MetadataJson && (current.MetaDispatchClaimToken == null ||
            current.MetaDispatchClaimExpiresUtc == null || current.MetaDispatchClaimExpiresUtc <= now))
            .ExecuteUpdateAsync(setters => setters.SetProperty(current => current.MetadataJson, metadata), ct);
    }

    internal static string MergeDispatchMetadata(string? existingJson, MetaConversionsApiResult result)
    {
        Dictionary<string, object?> metadata;

        try
        {
            metadata = string.IsNullOrWhiteSpace(existingJson)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(existingJson) ?? new Dictionary<string, object?>();
        }
        catch
        {
            metadata = new Dictionary<string, object?>
            {
                ["original_metadata_json"] = existingJson
            };
        }

        metadata.TryGetValue("metaServerAttemptCount", out var priorValue);
        var attempt = (int.TryParse(priorValue?.ToString(), out var prior) ? prior : 0) + (result.Attempted ? 1 : 0);
        var retryable = result.Retryable && !result.Sent && attempt < 8;
        metadata["metaServerAttemptCount"] = attempt;
        metadata["metaServerRetryable"] = retryable;
        metadata["metaServerRetryExhausted"] = result.Retryable && !result.Sent && !retryable;
        metadata["metaServerHttpStatusCode"] = result.HttpStatusCode;
        metadata["metaServerNextAttemptUtc"] = retryable
            ? DateTime.UtcNow.AddSeconds(Math.Min(3600, 30 * Math.Pow(2, attempt - 1)))
            : (DateTime?)null;
        metadata["metaServerEventsReceived"] = result.EventsReceived;
        metadata["metaServerTraceId"] = result.TraceId;
        metadata["metaServerStatus"] = result.Status;
        metadata["metaServerNote"] = result.Note;
        metadata["metaServerAttempted"] = result.Attempted;
        metadata["metaServerSent"] = result.Sent;
        metadata["metaServerPixelId"] = result.PixelId;
        metadata["metaServerPixelOwnerType"] = result.PixelOwnerType;
        metadata["metaServerDispatchedUtc"] = DateTime.UtcNow;

        return JsonSerializer.Serialize(metadata);
    }

    private const long DurableClaimBlocked = -1;
    private const long DurableClaimMissing = -2;

    private async Task<long> TryClaimPersistedSignalAsync(
        NormalizedAuthorityRequest request,
        string token,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Source, MetaSendAuthoritySources.MetaSignalOutcomeDispatcherHostedService, StringComparison.OrdinalIgnoreCase))
            return 0;

        var canonicalNames = CanonicalEventNames(request.EventType);
        var query = _db.MetaSignalEvents
            .AsNoTracking()
            .Where(x => !x.MetaServerSent && canonicalNames.Contains(x.EventName));

        if (request.CommerceBusinessId.HasValue)
            query = query.Where(x => x.CommerceBusinessId == request.CommerceBusinessId && x.AgentTrackingProfileId == null);
        else if (request.AgentTrackingProfileId.HasValue)
            query = query.Where(x => x.CommerceBusinessId == null && x.AgentTrackingProfileId == request.AgentTrackingProfileId);
        else
            query = query.Where(x => x.CommerceBusinessId == null && x.AgentTrackingProfileId == null);

        if (!string.IsNullOrWhiteSpace(request.ExplicitDedupeKey))
            query = query.Where(x => x.MetaDeduplicationKey == request.ExplicitDedupeKey);
        else if (!string.IsNullOrWhiteSpace(request.EventId))
            query = query.Where(x => x.EventId == request.EventId);
        else
            return DurableClaimMissing;

        var rowId = await query
            .OrderBy(x => x.CreatedUtc)
            .Select(x => (long?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (!rowId.HasValue)
            return DurableClaimMissing;

        var expiresUtc = nowUtc.AddMinutes(ReservationTtlMinutes);
        var claimed = await _db.MetaSignalEvents
            .Where(x => x.Id == rowId.Value &&
                        !x.MetaServerSent &&
                        (x.MetaDispatchClaimToken == null ||
                         x.MetaDispatchClaimExpiresUtc == null ||
                         x.MetaDispatchClaimExpiresUtc <= nowUtc ||
                         x.MetaDispatchClaimToken == token))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.MetaDispatchClaimToken, token)
                .SetProperty(x => x.MetaDispatchClaimedUtc, nowUtc)
                .SetProperty(x => x.MetaDispatchClaimExpiresUtc, expiresUtc),
                cancellationToken);

        return claimed == 1 ? rowId.Value : DurableClaimBlocked;
    }

    private async Task<bool> HasSentMatchAsync(NormalizedAuthorityRequest request, CancellationToken cancellationToken)
    {
        var canonicalNames = CanonicalEventNames(request.EventType);
        var sentRows = _db.MetaSignalEvents
            .AsNoTracking()
            .Where(x => x.MetaServerSent && canonicalNames.Contains(x.EventName));
        if (request.CommerceBusinessId.HasValue)
            sentRows = sentRows.Where(x => x.CommerceBusinessId == request.CommerceBusinessId);
        else if (request.AgentTrackingProfileId.HasValue)
            sentRows = sentRows.Where(x => x.CommerceBusinessId == null && x.AgentTrackingProfileId == request.AgentTrackingProfileId);
        else
            sentRows = sentRows.Where(x => x.CommerceBusinessId == null && x.AgentTrackingProfileId == null);

        if (!string.IsNullOrWhiteSpace(request.EventId) &&
            await sentRows.AnyAsync(x => x.EventId == request.EventId, cancellationToken))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(request.ExplicitDedupeKey) &&
            await sentRows.AnyAsync(x => x.MetaDeduplicationKey == request.ExplicitDedupeKey, cancellationToken))
        {
            return true;
        }

        // Stable identities distinguish legitimate conversions even within the same session/minute.
        if (!string.IsNullOrWhiteSpace(request.EventId) || !string.IsNullOrWhiteSpace(request.ExplicitDedupeKey))
            return false;

        var windowStartUtc = request.RoundedEventUtc.AddMinutes(-1);
        var windowEndUtc = request.RoundedEventUtc.AddMinutes(1);
        var scopedRows = sentRows.Where(x => x.CreatedUtc >= windowStartUtc && x.CreatedUtc < windowEndUtc);

        if (request.LeadId.HasValue)
        {
            return await scopedRows.AnyAsync(x => x.LeadId == request.LeadId, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            return await scopedRows.AnyAsync(x => x.SessionId == request.SessionId, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(request.VisitorId))
        {
            return await scopedRows.AnyAsync(x => x.VisitorId == request.VisitorId, cancellationToken);
        }

        return false;
    }

    private static string[] CanonicalEventNames(string providerEventName) =>
        MarketingConversionDestinationCatalog.Definitions
            .Where(definition => string.Equals(definition.Meta.EventName, providerEventName, StringComparison.OrdinalIgnoreCase))
            .Select(definition => definition.CanonicalEventName)
            .Append(providerEventName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private bool TryAllowNestedReservation(
        NormalizedAuthorityRequest request,
        out MetaSendAuthorityDecision decision)
    {
        decision = default!;

        if (string.IsNullOrWhiteSpace(request.ReservationToken))
            return false;

        if (!Reservations.TryGetValue(request.DedupeKey, out var reservation))
            return false;

        if (!reservation.Sent && string.Equals(reservation.Token, request.ReservationToken, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "MetaSendAuthority reused reservation event {EventType} source {Source}",
                request.EventType,
                request.Source);

            decision = new MetaSendAuthorityDecision(
                Allowed: true,
                EventType: request.EventType,
                LeadId: request.LeadId,
                Source: request.Source,
                DedupeKey: request.DedupeKey,
                ReservationToken: request.ReservationToken,
                Status: "allowed_nested",
                Note: null);
            return true;
        }

        if (reservation.Sent || reservation.Priority >= request.Priority)
        {
            _logger.LogInformation(
                "MetaSendAuthority blocked duplicate event {EventType} lead {LeadId}",
                request.EventType,
                request.LeadId);

            decision = new MetaSendAuthorityDecision(
                Allowed: false,
                EventType: request.EventType,
                LeadId: request.LeadId,
                Source: request.Source,
                DedupeKey: request.DedupeKey,
                ReservationToken: null,
                Status: "blocked_duplicate",
                Note: "reservation_superseded");
            return true;
        }

        return false;
    }

    private bool TryBlockOrReplaceActiveReservation(
        NormalizedAuthorityRequest request,
        out MetaSendAuthorityDecision decision)
    {
        decision = default!;

        if (!Reservations.TryGetValue(request.DedupeKey, out var reservation))
            return false;

        if (reservation.Sent || reservation.Priority >= request.Priority)
        {
            _logger.LogInformation(
                "MetaSendAuthority blocked duplicate event {EventType} lead {LeadId}",
                request.EventType,
                request.LeadId);

            decision = new MetaSendAuthorityDecision(
                Allowed: false,
                EventType: request.EventType,
                LeadId: request.LeadId,
                Source: request.Source,
                DedupeKey: request.DedupeKey,
                ReservationToken: null,
                Status: "blocked_duplicate",
                Note: reservation.Sent ? "already_sent_in_memory" : "blocked_by_higher_priority_source");
            return true;
        }

        return false;
    }

    private static NormalizedAuthorityRequest Normalize(MetaSendAuthorityRequest request)
    {
        var eventType = NormalizeText(request.EventType) ?? "unknown";
        var source = NormalizeSource(request.Source);
        var sessionId = NormalizeText(request.SessionId);
        var visitorId = NormalizeText(request.VisitorId);
        var eventId = NormalizeText(request.EventId);
        var explicitDedupeKey = NormalizeText(request.DeduplicationKey);
        var roundedEventUtc = RoundToMinute(request.EventUtc == default ? DateTime.UtcNow : request.EventUtc);

        return new NormalizedAuthorityRequest(
            EventType: eventType,
            LeadId: request.LeadId,
            CommerceBusinessId: request.CommerceBusinessId,
            AgentTrackingProfileId: request.AgentTrackingProfileId,
            SessionId: sessionId,
            VisitorId: visitorId,
            EventId: eventId,
            ExplicitDedupeKey: explicitDedupeKey,
            EventUtc: request.EventUtc == default ? DateTime.UtcNow : request.EventUtc,
            RoundedEventUtc: roundedEventUtc,
            Source: source,
            Priority: ResolvePriority(source),
            DedupeKey: BuildDedupeKey(eventType, request.LeadId, sessionId, visitorId, roundedEventUtc, explicitDedupeKey, eventId, request.CommerceBusinessId, request.AgentTrackingProfileId),
            ReservationToken: NormalizeText(request.ReservationToken));
    }

    private static int ResolvePriority(string source)
        => SourcePriorities.TryGetValue(source, out var priority) ? priority : 50;

    private static string NormalizeSource(string? source)
        => NormalizeText(source) ?? MetaSendAuthoritySources.Controllers;

    private static string BuildDedupeKey(
        string eventType,
        Guid? leadId,
        string? sessionId,
        string? visitorId,
        DateTime eventUtc,
        string? explicitDedupeKey,
        string? eventId,
        Guid? commerceBusinessId,
        Guid? agentTrackingProfileId)
    {
        var roundedMinute = RoundToMinute(eventUtc).ToString("yyyyMMddHHmm");
        var ownerPrefix = commerceBusinessId.HasValue && commerceBusinessId != Guid.Empty
            ? "business:" + commerceBusinessId.Value.ToString("N") + ":"
            : agentTrackingProfileId.HasValue && agentTrackingProfileId != Guid.Empty
                ? "agent:" + agentTrackingProfileId.Value.ToString("N") + ":"
                : string.Empty;

        if (!string.IsNullOrWhiteSpace(explicitDedupeKey))
            return ownerPrefix + explicitDedupeKey;

        if (!string.IsNullOrWhiteSpace(eventId))
            return ownerPrefix + eventType + ":" + eventId;

        if (leadId.HasValue && leadId.Value != Guid.Empty)
            return ownerPrefix + eventType + ":" + leadId.Value.ToString("N") + ":" + roundedMinute;

        if (!string.IsNullOrWhiteSpace(sessionId))
            return ownerPrefix + eventType + ":" + sessionId + ":" + roundedMinute;

        if (!string.IsNullOrWhiteSpace(visitorId))
            return ownerPrefix + eventType + ":" + visitorId + ":" + roundedMinute;

        return ownerPrefix + eventType + ":anonymous:" + roundedMinute;
    }

    private static DateTime RoundToMinute(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

        var rounded = utc.AddSeconds(30);
        return new DateTime(
            rounded.Year,
            rounded.Month,
            rounded.Day,
            rounded.Hour,
            rounded.Minute,
            0,
            DateTimeKind.Utc);
    }

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void PruneExpiredReservations(DateTime nowUtc)
    {
        foreach (var pair in Reservations)
        {
            if (pair.Value.ExpiresUtc <= nowUtc)
                Reservations.TryRemove(pair.Key, out _);
        }
    }

    private sealed record AuthorityReservation(
        string Token,
        string DedupeKey,
        string EventType,
        Guid? LeadId,
        string? SessionId,
        string? VisitorId,
        string? EventId,
        string? ExplicitDedupeKey,
        string Source,
        int Priority,
        DateTime ReservedUtc,
        DateTime ExpiresUtc,
        bool Sent,
        long? RowId);

    private sealed record NormalizedAuthorityRequest(
        string EventType,
        Guid? LeadId,
        Guid? CommerceBusinessId,
        Guid? AgentTrackingProfileId,
        string? SessionId,
        string? VisitorId,
        string? EventId,
        string? ExplicitDedupeKey,
        DateTime EventUtc,
        DateTime RoundedEventUtc,
        string Source,
        int Priority,
        string DedupeKey,
        string? ReservationToken);
}

public sealed record MetaSendAuthorityRequest
{
    public string EventType { get; init; } = string.Empty;
    public Guid? LeadId { get; init; }
    public Guid? CommerceBusinessId { get; init; }
    public Guid? AgentTrackingProfileId { get; init; }
    public DateTime EventUtc { get; init; }
    public string? EventId { get; init; }
    public string? DeduplicationKey { get; init; }
    public string? SessionId { get; init; }
    public string? VisitorId { get; init; }
    public string? Source { get; init; }
    public string? ReservationToken { get; init; }
}

public sealed record MetaSendAuthorityDecision(
    bool Allowed,
    string EventType,
    Guid? LeadId,
    string Source,
    string DedupeKey,
    string? ReservationToken,
    string Status,
    string? Note);

public static class MetaSendAuthoritySources
{
    public const string MetaSignalOutcomeDispatcherHostedService = "MetaSignalOutcomeDispatcherHostedService";
    public const string MetaSignalAnalyticsBridge = "MetaSignalAnalyticsBridge";
    public const string Controllers = "Controllers";
}
