using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Analytics;

public interface IMetaSendAuthority
{
    Task<MetaSendAuthorityDecision> TrySendAsync(MetaSendAuthorityRequest request, CancellationToken cancellationToken = default);
    void Complete(MetaSendAuthorityDecision decision, bool sent);
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

            if (TryAllowNestedReservation(normalized, out var nestedDecision))
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
            if (persistedRowId == DurableClaimBlocked)
            {
                return new MetaSendAuthorityDecision(
                    Allowed: false,
                    EventType: normalized.EventType,
                    LeadId: normalized.LeadId,
                    Source: normalized.Source,
                    DedupeKey: normalized.DedupeKey,
                    ReservationToken: null,
                    Status: "blocked_duplicate",
                    Note: "durable_dispatch_claim_active");
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

    public void Complete(MetaSendAuthorityDecision decision, bool sent)
    {
        if (!decision.Allowed ||
            string.IsNullOrWhiteSpace(decision.DedupeKey) ||
            string.IsNullOrWhiteSpace(decision.ReservationToken))
        {
            return;
        }

        if (!Reservations.TryGetValue(decision.DedupeKey, out var reservation) ||
            !string.Equals(reservation.Token, decision.ReservationToken, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (reservation.RowId.HasValue)
            {
                var rowId = reservation.RowId.Value;
                if (sent)
                {
                    _db.MetaSignalEvents
                        .Where(x => x.Id == rowId && x.MetaDispatchClaimToken == reservation.Token)
                        .ExecuteUpdate(setters => setters
                            .SetProperty(x => x.MetaServerSent, true)
                            .SetProperty(x => x.MetaDispatchClaimToken, (string?)null)
                            .SetProperty(x => x.MetaDispatchClaimedUtc, (DateTime?)null)
                            .SetProperty(x => x.MetaDispatchClaimExpiresUtc, (DateTime?)null));
                }
                else
                {
                    _db.MetaSignalEvents
                        .Where(x => x.Id == rowId && x.MetaDispatchClaimToken == reservation.Token && !x.MetaServerSent)
                        .ExecuteUpdate(setters => setters
                            .SetProperty(x => x.MetaDispatchClaimToken, (string?)null)
                            .SetProperty(x => x.MetaDispatchClaimedUtc, (DateTime?)null)
                            .SetProperty(x => x.MetaDispatchClaimExpiresUtc, (DateTime?)null));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "MetaSendAuthority could not persist completion for event {EventType} row {RowId}. The durable lease remains fail-closed until expiry.",
                decision.EventType,
                reservation.RowId);
        }

        if (sent)
        {
            Reservations[decision.DedupeKey] = reservation with
            {
                Sent = true,
                ExpiresUtc = DateTime.UtcNow.AddHours(SentTtlHours)
            };
        }
        else
        {
            Reservations.TryRemove(decision.DedupeKey, out _);
        }
    }

    private const long DurableClaimBlocked = -1;

    private async Task<long> TryClaimPersistedSignalAsync(
        NormalizedAuthorityRequest request,
        string token,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Source, MetaSendAuthoritySources.MetaSignalOutcomeDispatcherHostedService, StringComparison.OrdinalIgnoreCase))
            return 0;

        var query = _db.MetaSignalEvents
            .AsNoTracking()
            .Where(x => !x.MetaServerSent && x.EventName == request.EventType);

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
            return 0;

        var rowId = await query
            .OrderBy(x => x.CreatedUtc)
            .Select(x => (long?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (!rowId.HasValue)
            return 0;

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
        var sentRows = _db.MetaSignalEvents
            .AsNoTracking()
            .Where(x => x.MetaServerSent && x.EventName == request.EventType);
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

    private bool TryAllowNestedReservation(
        NormalizedAuthorityRequest request,
        out MetaSendAuthorityDecision decision)
    {
        decision = default!;

        if (string.IsNullOrWhiteSpace(request.ReservationToken))
            return false;

        if (!Reservations.TryGetValue(request.DedupeKey, out var reservation))
            return false;

        if (string.Equals(reservation.Token, request.ReservationToken, StringComparison.Ordinal))
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

        if (leadId.HasValue && leadId.Value != Guid.Empty)
            return ownerPrefix + eventType + ":" + leadId.Value.ToString("N") + ":" + roundedMinute;

        if (!string.IsNullOrWhiteSpace(sessionId))
            return ownerPrefix + eventType + ":" + sessionId + ":" + roundedMinute;

        if (!string.IsNullOrWhiteSpace(visitorId))
            return ownerPrefix + eventType + ":" + visitorId + ":" + roundedMinute;

        if (!string.IsNullOrWhiteSpace(eventId))
            return ownerPrefix + eventType + ":" + eventId + ":" + roundedMinute;

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
