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
    TranslationCapacityPurpose Purpose);

internal interface ITranslationCapacityAuthority
{
    Task<TranslationCapacityReservation?> TryReserveAsync(
        string provider,
        int characters,
        TranslationCapacityPurpose purpose,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        TranslationCapacityReservation reservation,
        bool providerSucceeded,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Central monthly provider capacity ledger. Conditional database updates make
/// reservations safe across concurrent web/worker instances; a zero configured
/// capacity means the provider owner has not imposed a server-side cap yet.
/// </summary>
internal sealed class TranslationCapacityAuthority : ITranslationCapacityAuthority
{
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
        CancellationToken cancellationToken = default)
    {
        if (characters <= 0)
            return new TranslationCapacityReservation(provider, CurrentPeriod(), 0, purpose);

        var period = CurrentPeriod();
        var settings = await SettingsForAsync(provider, cancellationToken);
        await EnsureLedgerAsync(provider, period, settings, cancellationToken);

        if (!_db.Database.IsRelational())
        {
            var ledger = await _db.Set<LegendTranslationProviderCapacity>()
                .SingleAsync(item => item.Provider == provider && item.BillingPeriodStart == period, cancellationToken);
            if (!CanReserve(ledger, characters, purpose, settings))
                return null;
            ledger.ReservedLiveCharacters += characters;
            ledger.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            LegendConnectTelemetry.CapacityReservation(provider, purpose.ToString(), characters);
            return new TranslationCapacityReservation(provider, period, characters, purpose);
        }

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
                .SetProperty(item => item.UpdatedUtc, DateTime.UtcNow), cancellationToken);
        if (permitted != 1)
            return null;
        LegendConnectTelemetry.CapacityReservation(provider, purpose.ToString(), characters);
        return new TranslationCapacityReservation(provider, period, characters, purpose);
    }

    public async Task CompleteAsync(
        TranslationCapacityReservation reservation,
        bool providerSucceeded,
        CancellationToken cancellationToken = default)
    {
        if (reservation.Characters <= 0)
            return;

        if (!_db.Database.IsRelational())
        {
            var ledger = await _db.Set<LegendTranslationProviderCapacity>()
                .SingleOrDefaultAsync(item => item.Provider == reservation.Provider && item.BillingPeriodStart == reservation.BillingPeriodStart, cancellationToken);
            if (ledger is null)
                return;
            ledger.ReservedLiveCharacters = Math.Max(0, ledger.ReservedLiveCharacters - reservation.Characters);
            if (providerSucceeded)
            {
                if (reservation.Purpose == TranslationCapacityPurpose.Live)
                    ledger.LiveCharactersConsumed += reservation.Characters;
                else
                    ledger.BootstrapCharactersConsumed += reservation.Characters;
            }
            ledger.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        var ledgerQuery = _db.Set<LegendTranslationProviderCapacity>()
            .Where(item => item.Provider == reservation.Provider && item.BillingPeriodStart == reservation.BillingPeriodStart);
        if (providerSucceeded)
        {
            await ledgerQuery.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ReservedLiveCharacters, item => item.ReservedLiveCharacters - reservation.Characters)
                .SetProperty(item => item.LiveCharactersConsumed, item => item.LiveCharactersConsumed + (reservation.Purpose == TranslationCapacityPurpose.Live ? reservation.Characters : 0))
                .SetProperty(item => item.BootstrapCharactersConsumed, item => item.BootstrapCharactersConsumed + (reservation.Purpose == TranslationCapacityPurpose.Bootstrap ? reservation.Characters : 0))
                .SetProperty(item => item.UpdatedUtc, DateTime.UtcNow), cancellationToken);
        }
        else
        {
            await ledgerQuery.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ReservedLiveCharacters, item => item.ReservedLiveCharacters - reservation.Characters)
                .SetProperty(item => item.UpdatedUtc, DateTime.UtcNow), cancellationToken);
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
