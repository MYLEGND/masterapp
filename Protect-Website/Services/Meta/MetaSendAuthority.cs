using System.Collections.Concurrent;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ProtectWebsite.Services.Meta;

public interface IMetaSendAuthority
{
    Task<MetaSendAuthorityDecision> TrySendAsync(MetaSendAuthorityRequest request, CancellationToken cancellationToken = default);
    void Complete(MetaSendAuthorityDecision decision, bool sent);
}

public sealed class MetaSendAuthority : IMetaSendAuthority
{
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

    public async Task<MetaSendAuthorityDecision> TrySendAsync(
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
                Sent: false);

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
        catch (Exception ex)
        {
            var source = NormalizeSource(request.Source);
            var eventType = NormalizeText(request.EventType) ?? "unknown";
            _logger.LogWarning(
                ex,
                "MetaSendAuthority fail-open event {EventType} source {Source}",
                eventType,
                source);

            return new MetaSendAuthorityDecision(
                Allowed: true,
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
                    request.EventId),
                ReservationToken: null,
                Status: "allowed_fail_open",
                Note: "authority_fail_open");
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

    private async Task<bool> HasSentMatchAsync(NormalizedAuthorityRequest request, CancellationToken cancellationToken)
    {
        var sentRows = _db.MetaSignalEvents
            .AsNoTracking()
            .Where(x => x.MetaServerSent && x.EventName == request.EventType);

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
            SessionId: sessionId,
            VisitorId: visitorId,
            EventId: eventId,
            ExplicitDedupeKey: explicitDedupeKey,
            EventUtc: request.EventUtc == default ? DateTime.UtcNow : request.EventUtc,
            RoundedEventUtc: roundedEventUtc,
            Source: source,
            Priority: ResolvePriority(source),
            DedupeKey: BuildDedupeKey(eventType, request.LeadId, sessionId, visitorId, roundedEventUtc, explicitDedupeKey, eventId),
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
        string? eventId)
    {
        var roundedMinute = RoundToMinute(eventUtc).ToString("yyyyMMddHHmm");

        if (!string.IsNullOrWhiteSpace(explicitDedupeKey))
            return explicitDedupeKey;

        if (leadId.HasValue && leadId.Value != Guid.Empty)
            return $"{eventType}:{leadId.Value:N}:{roundedMinute}";

        if (!string.IsNullOrWhiteSpace(sessionId))
            return $"{eventType}:{sessionId}:{roundedMinute}";

        if (!string.IsNullOrWhiteSpace(visitorId))
            return $"{eventType}:{visitorId}:{roundedMinute}";

        if (!string.IsNullOrWhiteSpace(eventId))
            return $"{eventType}:{eventId}:{roundedMinute}";

        return $"{eventType}:anonymous:{roundedMinute}";
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
        bool Sent);

    private sealed record NormalizedAuthorityRequest(
        string EventType,
        Guid? LeadId,
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
