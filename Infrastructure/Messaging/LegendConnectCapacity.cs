using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

internal enum TranslationCapacityPurpose
{
    Live,
    Bootstrap
}

internal sealed record TranslationCapacityReservation(
    string Provider,
    DateOnly BillingPeriodStart,
    long Characters,
    TranslationCapacityPurpose Purpose,
    Guid ReservationId);

internal interface ITranslationCapacityAuthority
{
    Task<TranslationCapacityReservation?> TryReserveAsync(
        string provider,
        int characters,
        TranslationCapacityPurpose purpose,
        string? reservationReference = null,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        TranslationCapacityReservation reservation,
        bool providerSucceeded,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Central monthly provider capacity ledger. Conditional database updates and
/// durable, bounded reservation leases make capacity decisions safe across web
/// and worker instances. The reservation rows remain part of this authority;
/// they are not an alternate worker, queue, or billing model.
/// </summary>
internal sealed class TranslationCapacityAuthority : ITranslationCapacityAuthority
{
    private const string ReservedState = "Reserved";
    private const string CompletedState = "Completed";
    private const string ReleasedState = "Released";

    private readonly MasterAppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILegendConnectRuntimePolicyAuthority? _runtimePolicy;
    private readonly ILogger<TranslationCapacityAuthority> _logger;

    public TranslationCapacityAuthority(
        MasterAppDbContext db,
        IConfiguration configuration,
        ILogger<TranslationCapacityAuthority> logger,
        ILegendConnectRuntimePolicyAuthority? runtimePolicy = null)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
        _runtimePolicy = runtimePolicy;
    }

    public async Task<TranslationCapacityReservation?> TryReserveAsync(
        string provider,
        int characters,
        TranslationCapacityPurpose purpose,
        string? reservationReference = null,
        CancellationToken cancellationToken = default)
    {
        var period = CurrentPeriod();
        if (characters <= 0)
            return new TranslationCapacityReservation(provider, period, 0, purpose, Guid.Empty);

        var settings = await SettingsForAsync(provider, cancellationToken);
        await EnsureLedgerAsync(provider, period, settings, cancellationToken);
        var reference = NormalizeReference(reservationReference);
        var now = DateTime.UtcNow;

        if (!_db.Database.IsRelational())
            return await TryReserveInMemoryAsync(provider, period, characters, purpose, reference, settings, now, cancellationToken);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await ReleaseExpiredReservationsAsync(provider, period, now, cancellationToken);

        var existing = await _db.Set<LegendTranslationProviderReservation>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Provider == provider && item.ReservationReference == reference, cancellationToken);
        if (existing is not null && existing.State != ReleasedState)
            return null;

        var capacity = settings.CapacityCharacters;
        var reserve = settings.LiveReserveCharacters;
        if (purpose == TranslationCapacityPurpose.Bootstrap &&
            (capacity <= 0 || settings.MaximumSafeCorpusCharacters <= 0))
            return null;

        var permitted = await _db.Set<LegendTranslationProviderCapacity>()
            .Where(item => item.Provider == provider && item.BillingPeriodStart == period)
            .Where(item => capacity <= 0 ||
                item.LiveCharactersConsumed + item.BootstrapCharactersConsumed + item.TrainingCharactersConsumed + item.ReservedLiveCharacters + characters <=
                capacity - (purpose == TranslationCapacityPurpose.Bootstrap ? Math.Max(0, reserve) : 0))
            .Where(item => purpose != TranslationCapacityPurpose.Bootstrap ||
                item.BootstrapCharactersConsumed + item.ReservedLiveCharacters + characters <= settings.MaximumSafeCorpusCharacters)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ReservedLiveCharacters, item => item.ReservedLiveCharacters + characters)
                .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
        if (permitted != 1)
            return null;

        Guid reservationId;
        try
        {
            if (existing is null)
            {
                var reservation = NewReservation(provider, period, characters, purpose, reference, now, ReservationExpiry(provider, now));
                reservationId = reservation.Id;
                _db.Set<LegendTranslationProviderReservation>().Add(reservation);
                await _db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                reservationId = existing.Id;
                var reactivated = await _db.Set<LegendTranslationProviderReservation>()
                    .Where(item => item.Id == existing.Id && item.State == ReleasedState)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.BillingPeriodStart, period)
                        .SetProperty(item => item.Purpose, purpose.ToString())
                        .SetProperty(item => item.Characters, (long)characters)
                        .SetProperty(item => item.State, ReservedState)
                        .SetProperty(item => item.ReservationExpiresUtc, ReservationExpiry(provider, now))
                        .SetProperty(item => item.CreatedUtc, now)
                        .SetProperty(item => item.CompletedUtc, (DateTime?)null), cancellationToken);
                if (reactivated != 1)
                    return null;
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            _logger.LogDebug(exception, "Legend Connect provider capacity reservation was claimed concurrently. Provider={Provider}", provider);
            return null;
        }

        LegendConnectTelemetry.CapacityReservation(provider, purpose.ToString(), characters);
        return new TranslationCapacityReservation(provider, period, characters, purpose, reservationId);
    }

    public async Task CompleteAsync(
        TranslationCapacityReservation reservation,
        bool providerSucceeded,
        CancellationToken cancellationToken = default)
    {
        if (reservation.Characters <= 0 || reservation.ReservationId == Guid.Empty)
            return;

        var now = DateTime.UtcNow;
        if (!_db.Database.IsRelational())
        {
            var durable = await _db.Set<LegendTranslationProviderReservation>()
                .SingleOrDefaultAsync(item => item.Id == reservation.ReservationId, cancellationToken);
            if (durable is null || durable.State != ReservedState)
                return;

            var ledger = await _db.Set<LegendTranslationProviderCapacity>()
                .SingleOrDefaultAsync(item => item.Provider == reservation.Provider && item.BillingPeriodStart == reservation.BillingPeriodStart, cancellationToken);
            if (ledger is null)
                return;

            durable.State = providerSucceeded ? CompletedState : ReleasedState;
            durable.CompletedUtc = now;
            ledger.ReservedLiveCharacters = Math.Max(0, ledger.ReservedLiveCharacters - reservation.Characters);
            if (providerSucceeded)
            {
                if (reservation.Purpose == TranslationCapacityPurpose.Live)
                    ledger.LiveCharactersConsumed += reservation.Characters;
                else
                    ledger.BootstrapCharactersConsumed += reservation.Characters;
            }
            ledger.UpdatedUtc = now;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var finalized = await _db.Set<LegendTranslationProviderReservation>()
            .Where(item => item.Id == reservation.ReservationId && item.State == ReservedState)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, providerSucceeded ? CompletedState : ReleasedState)
                .SetProperty(item => item.CompletedUtc, now), cancellationToken);
        if (finalized != 1)
            return;

        var ledgerQuery = _db.Set<LegendTranslationProviderCapacity>()
            .Where(item => item.Provider == reservation.Provider && item.BillingPeriodStart == reservation.BillingPeriodStart);
        await ledgerQuery.ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.ReservedLiveCharacters,
                item => item.ReservedLiveCharacters >= reservation.Characters
                    ? item.ReservedLiveCharacters - reservation.Characters
                    : 0)
            .SetProperty(item => item.LiveCharactersConsumed,
                item => item.LiveCharactersConsumed + (providerSucceeded && reservation.Purpose == TranslationCapacityPurpose.Live ? reservation.Characters : 0))
            .SetProperty(item => item.BootstrapCharactersConsumed,
                item => item.BootstrapCharactersConsumed + (providerSucceeded && reservation.Purpose == TranslationCapacityPurpose.Bootstrap ? reservation.Characters : 0))
            .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<TranslationCapacityReservation?> TryReserveInMemoryAsync(
        string provider,
        DateOnly period,
        int characters,
        TranslationCapacityPurpose purpose,
        string reference,
        CapacitySettings settings,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await ReleaseExpiredReservationsAsync(provider, period, now, cancellationToken);
        var existing = await _db.Set<LegendTranslationProviderReservation>()
            .SingleOrDefaultAsync(item => item.Provider == provider && item.ReservationReference == reference, cancellationToken);
        if (existing is not null && existing.State != ReleasedState)
            return null;

        var ledger = await _db.Set<LegendTranslationProviderCapacity>()
            .SingleAsync(item => item.Provider == provider && item.BillingPeriodStart == period, cancellationToken);
        if (!CanReserve(ledger, characters, purpose, settings))
            return null;

        ledger.ReservedLiveCharacters += characters;
        ledger.UpdatedUtc = now;
        LegendTranslationProviderReservation reservation;
        if (existing is null)
        {
            reservation = NewReservation(provider, period, characters, purpose, reference, now, ReservationExpiry(provider, now));
            _db.Set<LegendTranslationProviderReservation>().Add(reservation);
        }
        else
        {
            reservation = existing;
            reservation.BillingPeriodStart = period;
            reservation.Purpose = purpose.ToString();
            reservation.Characters = characters;
            reservation.State = ReservedState;
            reservation.ReservationExpiresUtc = ReservationExpiry(provider, now);
            reservation.CreatedUtc = now;
            reservation.CompletedUtc = null;
        }
        await _db.SaveChangesAsync(cancellationToken);
        LegendConnectTelemetry.CapacityReservation(provider, purpose.ToString(), characters);
        return new TranslationCapacityReservation(provider, period, characters, purpose, reservation.Id);
    }

    private async Task ReleaseExpiredReservationsAsync(
        string provider,
        DateOnly period,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (!_db.Database.IsRelational())
        {
            var expired = await _db.Set<LegendTranslationProviderReservation>()
                .Where(item => item.Provider == provider && item.BillingPeriodStart == period &&
                    item.State == ReservedState && item.ReservationExpiresUtc < now)
                .ToListAsync(cancellationToken);
            if (expired.Count == 0)
                return;

            var releasedCharacters = expired.Sum(item => item.Characters);
            foreach (var reservation in expired)
            {
                reservation.State = ReleasedState;
                reservation.CompletedUtc = now;
            }
            var ledger = await _db.Set<LegendTranslationProviderCapacity>()
                .SingleAsync(item => item.Provider == provider && item.BillingPeriodStart == period, cancellationToken);
            ledger.ReservedLiveCharacters = Math.Max(0, ledger.ReservedLiveCharacters - releasedCharacters);
            ledger.UpdatedUtc = now;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        while (true)
        {
            var expired = await _db.Set<LegendTranslationProviderReservation>()
                .AsNoTracking()
                .Where(item => item.Provider == provider && item.BillingPeriodStart == period &&
                    item.State == ReservedState && item.ReservationExpiresUtc < now)
                .OrderBy(item => item.ReservationExpiresUtc)
                .Select(item => new { item.Id, item.Characters })
                .Take(128)
                .ToListAsync(cancellationToken);
            if (expired.Count == 0)
                return;

            long releasedCharacters = 0;
            foreach (var expiredReservation in expired)
            {
                var released = await _db.Set<LegendTranslationProviderReservation>()
                    .Where(item => item.Id == expiredReservation.Id && item.State == ReservedState && item.ReservationExpiresUtc < now)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.State, ReleasedState)
                        .SetProperty(item => item.CompletedUtc, now), cancellationToken);
                if (released == 1)
                    releasedCharacters += expiredReservation.Characters;
            }
            if (releasedCharacters > 0)
            {
                await _db.Set<LegendTranslationProviderCapacity>()
                    .Where(item => item.Provider == provider && item.BillingPeriodStart == period)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.ReservedLiveCharacters,
                            item => item.ReservedLiveCharacters >= releasedCharacters
                                ? item.ReservedLiveCharacters - releasedCharacters
                                : 0)
                        .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
            }
        }
    }

    private async Task EnsureLedgerAsync(
        string provider,
        DateOnly period,
        CapacitySettings settings,
        CancellationToken cancellationToken)
    {
        var existing = await _db.Set<LegendTranslationProviderCapacity>()
            .SingleOrDefaultAsync(item => item.Provider == provider && item.BillingPeriodStart == period, cancellationToken);
        if (existing is not null)
        {
            if (existing.ConfiguredCapacityCharacters != settings.CapacityCharacters ||
                existing.ReservedLiveCapacityCharacters != settings.LiveReserveCharacters)
            {
                existing.ConfiguredCapacityCharacters = settings.CapacityCharacters;
                existing.ReservedLiveCapacityCharacters = settings.LiveReserveCharacters;
                existing.UpdatedUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
            }
            return;
        }

        var ledger = new LegendTranslationProviderCapacity
        {
            Id = Guid.NewGuid(),
            Provider = provider,
            BillingPeriodStart = period,
            ConfiguredCapacityCharacters = settings.CapacityCharacters,
            ReservedLiveCapacityCharacters = settings.LiveReserveCharacters,
            UpdatedUtc = DateTime.UtcNow
        };
        _db.Set<LegendTranslationProviderCapacity>().Add(ledger);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            _db.Entry(ledger).State = EntityState.Detached;
            _logger.LogDebug(exception, "Legend Connect provider capacity ledger was provisioned concurrently. Provider={Provider} Period={Period}", provider, period);
        }
    }

    private static LegendTranslationProviderReservation NewReservation(
        string provider,
        DateOnly period,
        int characters,
        TranslationCapacityPurpose purpose,
        string reference,
        DateTime now,
        DateTime expiresUtc) => new()
    {
        Id = Guid.NewGuid(),
        Provider = provider,
        BillingPeriodStart = period,
        ReservationReference = reference,
        Purpose = purpose.ToString(),
        Characters = characters,
        State = ReservedState,
        ReservationExpiresUtc = expiresUtc,
        CreatedUtc = now
    };

    private static bool CanReserve(
        LegendTranslationProviderCapacity ledger,
        int characters,
        TranslationCapacityPurpose purpose,
        CapacitySettings settings)
    {
        if (purpose == TranslationCapacityPurpose.Bootstrap &&
            (ledger.ConfiguredCapacityCharacters <= 0 || settings.MaximumSafeCorpusCharacters <= 0 ||
             ledger.BootstrapCharactersConsumed + ledger.ReservedLiveCharacters + characters > settings.MaximumSafeCorpusCharacters))
            return false;
        if (ledger.ConfiguredCapacityCharacters <= 0)
            return true;
        var limit = ledger.ConfiguredCapacityCharacters -
            (purpose == TranslationCapacityPurpose.Bootstrap ? Math.Max(0, ledger.ReservedLiveCapacityCharacters) : 0);
        return ledger.LiveCharactersConsumed + ledger.BootstrapCharactersConsumed + ledger.TrainingCharactersConsumed + ledger.ReservedLiveCharacters + characters <= limit;
    }

    private async Task<CapacitySettings> SettingsForAsync(string provider, CancellationToken cancellationToken)
    {
        if (string.Equals(provider, "AzureTranslator", StringComparison.OrdinalIgnoreCase) && _runtimePolicy is not null)
        {
            var policy = await _runtimePolicy.GetEffectiveAsync(cancellationToken);
            return new CapacitySettings(
                policy.MonthlyProviderCapacityCharacters,
                policy.LiveTranslationReserveCharacters,
                policy.MaximumSafeCorpusConsumptionCharacters);
        }
        var prefix = $"LegendConnect:Providers:{provider}";
        var capacity = Math.Max(0, _configuration.GetValue<long?>($"{prefix}:MonthlyCapacityCharacters") ?? 0);
        var reserve = Math.Max(0, _configuration.GetValue<long?>($"{prefix}:LiveReserveCharacters") ?? 0);
        return new CapacitySettings(capacity, reserve, Math.Max(0, capacity - reserve));
    }

    private DateTime ReservationExpiry(string provider, DateTime now) => now.AddSeconds(Math.Clamp(
        _configuration.GetValue<int?>($"LegendConnect:Providers:{provider}:ReservationLeaseSeconds") ??
        _configuration.GetValue<int?>("LegendConnect:Entitlements:ReservationLeaseSeconds") ?? 45,
        15,
        300));

    private static string NormalizeReference(string? reference)
    {
        var value = reference?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return "capacity:" + Guid.NewGuid().ToString("N");
        return value.Length <= 180
            ? value
            : "capacity-hash:" + LegendLanguageIdentity.TextHash(value);
    }

    private static DateOnly CurrentPeriod()
    {
        var now = DateTime.UtcNow;
        return new DateOnly(now.Year, now.Month, 1);
    }

    private sealed record CapacitySettings(
        long CapacityCharacters,
        long LiveReserveCharacters,
        long MaximumSafeCorpusCharacters);
}
