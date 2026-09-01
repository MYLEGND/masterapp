using Domain.Billing;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

/// <summary>
/// The sole account-entitlement and usage-reservation authority for live
/// translation. It is intentionally separate from controlled-resource
/// permission and from the Azure/provider capacity ledger.
/// </summary>
internal sealed class TranslationEntitlementAuthority : ITranslationEntitlementAuthority
{
    private const string ReservingState = "Reserving";
    private const string ReservedState = "Reserved";
    private const string SucceededState = "Succeeded";
    private const string QuotaDeniedState = "QuotaDenied";

    private readonly MasterAppDbContext _db;
    private readonly IControlledResourceAccessService _access;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TranslationEntitlementAuthority> _logger;

    public TranslationEntitlementAuthority(
        MasterAppDbContext db,
        IControlledResourceAccessService access,
        IConfiguration configuration,
        ILogger<TranslationEntitlementAuthority> logger)
    {
        _db = db;
        _access = access;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<TranslationAccountEntitlementSnapshot> GetSnapshotAsync(
        MessagingActor account,
        CancellationToken cancellationToken = default)
    {
        account = Normalize(account);
        var access = await _access.GetAccessAsync(
            account,
            ControlledResourceTypes.LanguageTranslation,
            cancellationToken);
        var period = CurrentPeriod();
        var entitlement = await _db.Set<LegendTranslationEntitlement>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.UserId == account.UserId &&
                item.ParticipantType == account.ParticipantType,
                cancellationToken);
        var usage = await _db.Set<LegendTranslationUsagePeriod>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.UserId == account.UserId &&
                item.ParticipantType == account.ParticipantType &&
                item.PeriodStart == period,
                cancellationToken);
        return ToSnapshot(access, entitlement, usage, period);
    }

    public async Task<TranslationFounderAccountSearchSnapshot> SearchFounderAccountsAsync(
        string? search,
        int take,
        CancellationToken cancellationToken = default)
    {
        var currentPeriod = CurrentPeriod();
        var normalizedSearch = NormalizeSearch(search);
        var limit = Math.Clamp(take, 1, 8);
        var profiles = ActiveCurrentPayingClients();
        if (normalizedSearch is not null)
        {
            profiles = profiles.Where(profile =>
                (profile.FirstName ?? string.Empty).ToLower().Contains(normalizedSearch) ||
                (profile.LastName ?? string.Empty).ToLower().Contains(normalizedSearch) ||
                (profile.Email ?? string.Empty).ToLower().Contains(normalizedSearch) ||
                (profile.ClientUserId ?? string.Empty).ToLower().Contains(normalizedSearch));
        }

        var candidates = await profiles
            .OrderBy(profile => profile.FirstName)
            .ThenBy(profile => profile.LastName)
            .ThenBy(profile => profile.Id)
            .Select(profile => new AccountDirectoryRow(
                profile.ClientUserId,
                MessagingParticipantTypes.Client,
                (profile.FirstName + " " + profile.LastName).Trim(),
                profile.Id))
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var hasMore = candidates.Count > limit;
        var accounts = candidates
            .Take(limit)
            .Select(item => item with
            {
                UserId = Normalize(item.UserId),
                ParticipantType = MessagingParticipantTypes.Client,
                DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.UserId : item.DisplayName.Trim()
            })
            .Where(item => item.UserId.Length > 0)
            .ToArray();
        if (accounts.Length == 0)
        {
            return new TranslationFounderAccountSearchSnapshot(
                Array.Empty<TranslationFounderAccountUsageSnapshot>(),
                normalizedSearch,
                false);
        }

        var userIds = accounts.Select(item => item.UserId).Distinct(StringComparer.Ordinal).ToArray();
        var entitlements = await _db.Set<LegendTranslationEntitlement>().AsNoTracking()
            .Where(item => item.ParticipantType == MessagingParticipantTypes.Client && userIds.Contains(item.UserId))
            .ToListAsync(cancellationToken);
        var usage = await _db.Set<LegendTranslationUsagePeriod>().AsNoTracking()
            .Where(item => item.ParticipantType == MessagingParticipantTypes.Client &&
                           item.PeriodStart == currentPeriod &&
                           userIds.Contains(item.UserId))
            .ToListAsync(cancellationToken);
        var entitlementByAccount = entitlements.ToDictionary(
            item => (Normalize(item.UserId), item.ParticipantType),
            item => item);
        var usageByAccount = usage.ToDictionary(
            item => (Normalize(item.UserId), item.ParticipantType),
            item => item);
        var profileIds = accounts.Select(item => item.ProfileId!.Value).ToArray();
        var languagesByProfile = await _db.MobileProfileSettings.AsNoTracking()
            .Where(item => profileIds.Contains(item.ProfileId))
            .ToDictionaryAsync(item => item.ProfileId, item => item.PreferredCommunicationLanguage, cancellationToken);

        var result = new List<TranslationFounderAccountUsageSnapshot>(accounts.Length);
        foreach (var account in accounts)
        {
            var actor = new MessagingActor(account.UserId, account.ParticipantType);
            var access = await _access.GetAccessAsync(actor, ControlledResourceTypes.LanguageTranslation, cancellationToken);
            entitlementByAccount.TryGetValue((account.UserId, account.ParticipantType), out var entitlement);
            usageByAccount.TryGetValue((account.UserId, account.ParticipantType), out var currentUsage);
            var preferredLanguage = access.State == ControlledResourceAccessStates.Granted &&
                                    account.ProfileId.HasValue &&
                                    languagesByProfile.TryGetValue(account.ProfileId.Value, out var language)
                ? language
                : null;
            result.Add(new TranslationFounderAccountUsageSnapshot(
                actor,
                account.DisplayName,
                access.State,
                preferredLanguage,
                ToSnapshot(access, entitlement, currentUsage, currentPeriod),
                ToUsageMetrics(currentUsage)));
        }

        return new TranslationFounderAccountSearchSnapshot(result, normalizedSearch, hasMore);
    }

    public async Task<bool> IsFounderEntitlementEligibleAsync(
        MessagingActor account,
        CancellationToken cancellationToken = default)
    {
        account = Normalize(account);
        return account.ParticipantType == MessagingParticipantTypes.Client &&
               await ActiveCurrentPayingClients().AnyAsync(
                   profile => profile.ClientUserId.ToLower() == account.UserId,
                   cancellationToken);
    }

    public IReadOnlyList<TranslationEntitlementPreset> GetFounderEntitlementPresets()
    {
        var configured = _configuration.GetSection("LegendConnect:Entitlements:Presets")
            .Get<List<EntitlementPresetConfiguration>>() ?? [];
        return configured
            .Where(item => !string.IsNullOrWhiteSpace(item.Key) &&
                           !string.IsNullOrWhiteSpace(item.DisplayName) &&
                           item.CharacterAllowance >= 0)
            .Select(item => new TranslationEntitlementPreset(
                item.Key!.Trim(),
                item.DisplayName!.Trim(),
                item.CharacterAllowance))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.CharacterAllowance)
            .ToArray();
    }

    public async Task<TranslationFounderScaleSnapshot> GetFounderScaleAsync(
        CancellationToken cancellationToken = default)
    {
        var period = CurrentPeriod();
        var usage = await _db.Set<LegendTranslationUsagePeriod>().AsNoTracking()
            .Where(item => item.PeriodStart == period)
            .ToListAsync(cancellationToken);
        var entitlements = await _db.Set<LegendTranslationEntitlement>().AsNoTracking()
            .ToListAsync(cancellationToken);
        var entitlementByAccount = entitlements.ToDictionary(
            item => (item.UserId.Trim().ToLowerInvariant(), item.ParticipantType),
            item => item);
        var highConsumption = 0L;
        foreach (var item in usage)
        {
            entitlementByAccount.TryGetValue((item.UserId.Trim().ToLowerInvariant(), item.ParticipantType), out var entitlement);
            var allowance = Math.Max(0, entitlement?.MonthlyCharacterAllowance ?? DefaultAllowance());
            if (!(entitlement?.IsUnlimited ?? false) && allowance > 0 &&
                ((decimal)(Math.Max(0, item.ConsumedCharacters) + Math.Max(0, item.ReservedCharacters)) / allowance) >= 0.8m)
            {
                highConsumption++;
            }
        }

        return new TranslationFounderScaleSnapshot(
            usage.Sum(item => Math.Max(0, item.ProviderOperationCount)),
            usage.Sum(item => Math.Max(0, item.ProviderBillableCharacters)),
            usage.Sum(item => Math.Max(0, item.SameLanguageCharactersAvoided)),
            usage.Sum(item => Math.Max(0, item.TranslationMemoryCharactersAvoided)),
            usage.Sum(item => Math.Max(0, item.ContextualCharactersAvoided)),
            usage.Sum(item => Math.Max(0, item.QuotaDeniedRequestCount)),
            usage.Sum(item => Math.Max(0, item.ProviderFailureCount)),
            usage.Sum(item => Math.Max(0, item.GroupUniqueTargetReuseCount)),
            highConsumption,
            usage.Sum(item => Math.Max(0, item.StructuralCompositionCharactersAvoided)),
            usage.Sum(item => Math.Max(0, item.PromotedTranslationModelCharactersAvoided)),
            usage.Sum(item => Math.Max(0, item.ProviderObservationCharactersAvoided)));
    }

    public async Task<TranslationAccountEntitlementSnapshot> SetEntitlementAsync(
        string founderUserId,
        TranslationEntitlementMutation mutation,
        CancellationToken cancellationToken = default)
    {
        var founder = Normalize(new MessagingActor(founderUserId, MessagingParticipantTypes.Agent));
        if (!await _access.IsFounderManagerAsync(founder, cancellationToken))
            throw new UnauthorizedAccessException("Founder authority is required to manage translation entitlement.");

        var target = Normalize(mutation.Target);
        if (target.UserId.Length == 0 || target.ParticipantType.Length == 0 || mutation.CharacterAllowance < 0)
            throw new ArgumentException("The requested translation entitlement is invalid.", nameof(mutation));
        if (!await IsFounderEntitlementEligibleAsync(target, cancellationToken))
        {
            throw new ArgumentException(
                "Translation entitlement management is limited to active, current-paying Client CRM accounts.",
                nameof(mutation));
        }

        var source = Bound(mutation.EntitlementSource, 80) ?? "FounderManaged";
        var entitlement = await _db.Set<LegendTranslationEntitlement>()
            .SingleOrDefaultAsync(item =>
                item.UserId == target.UserId && item.ParticipantType == target.ParticipantType,
                cancellationToken);
        if (entitlement is null)
        {
            entitlement = new LegendTranslationEntitlement
            {
                Id = Guid.NewGuid(),
                UserId = target.UserId,
                ParticipantType = target.ParticipantType
            };
            _db.Set<LegendTranslationEntitlement>().Add(entitlement);
        }

        entitlement.MonthlyCharacterAllowance = Math.Max(0, mutation.CharacterAllowance);
        entitlement.IsUnlimited = mutation.IsUnlimited;
        entitlement.EntitlementSource = source;
        entitlement.IsFounderOverride = mutation.IsFounderOverride;
        entitlement.UpdatedByUserId = founder.UserId;
        entitlement.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return await GetSnapshotAsync(target, cancellationToken);
    }

    public async Task<TranslationQuotaReservationResult> TryReserveAsync(
        TranslationQuotaReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        var account = Normalize(request.Account);
        var characters = Math.Max(0, request.BillableCharacters);
        if (characters == 0)
        {
            return new TranslationQuotaReservationResult(
                true,
                false,
                false,
                null,
                new TranslationQuotaReservation(Guid.Empty, account, CurrentPeriod(), 0, request.RequestReference));
        }

        if (!IsRequestReference(request.RequestReference))
            return new TranslationQuotaReservationResult(false, false, false, "translation_request_invalid", null);

        var access = await _access.GetAccessAsync(
            account,
            ControlledResourceTypes.LanguageTranslation,
            cancellationToken);
        if (access.State != ControlledResourceAccessStates.Granted)
            return new TranslationQuotaReservationResult(false, false, false, "translation_access_revoked", null);

        var now = DateTime.UtcNow;
        var period = CurrentPeriod();
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var existing = await _db.Set<LegendTranslationUsageLedger>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.RequestReference == request.RequestReference, cancellationToken);
        if (existing is not null)
        {
            if (existing.Succeeded)
                return new TranslationQuotaReservationResult(false, true, false, "translation_result_already_exists", null);
            if (IsActiveReservation(existing, now))
                return new TranslationQuotaReservationResult(false, false, true, "translation_request_in_progress", null);
            if (!await TryClaimRetryAsync(existing, account, request, period, now, cancellationToken))
                return new TranslationQuotaReservationResult(false, false, true, "translation_request_in_progress", null);

            // A provider-ready reservation may outlive an interrupted process.
            // The atomic claim above elects exactly one retry owner; that owner
            // releases the old aggregate reservation before starting again.
            if (existing.State == ReservedState &&
                existing.ReservationExpiresUtc is { } expiry &&
                expiry < now &&
                existing.BillableCharacters > 0)
            {
                await ReleasePeriodAsync(
                    new MessagingActor(existing.UserId, existing.ParticipantType),
                    existing.PeriodStart,
                    existing.BillableCharacters,
                    providerExecuted: false,
                    providerSucceeded: false,
                    cancellationToken);
            }
        }
        else
        {
            var ledger = new LegendTranslationUsageLedger
            {
                Id = Guid.NewGuid(),
                RequestReference = request.RequestReference,
                UserId = account.UserId,
                ParticipantType = account.ParticipantType,
                PeriodStart = period,
                SourceLanguageCode = Bound(request.SourceLanguageCode, 32) ?? string.Empty,
                TargetLanguageCode = Bound(request.TargetLanguageCode, 32) ?? string.Empty,
                Provider = Bound(request.Provider, 80) ?? string.Empty,
                BillableCharacters = characters,
                State = ReservingState,
                ReservationExpiresUtc = ReservationExpiry(now),
                CreatedUtc = now
            };
            _db.Set<LegendTranslationUsageLedger>().Add(ledger);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                // Subsequent conditional updates in this request must read the
                // durable reservation state, not a stale tracked copy.
                _db.Entry(ledger).State = EntityState.Detached;
            }
            catch (DbUpdateException)
            {
                _db.Entry(ledger).State = EntityState.Detached;
                return new TranslationQuotaReservationResult(false, false, true, "translation_request_in_progress", null);
            }
        }

        var entitlement = await _db.Set<LegendTranslationEntitlement>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == account.UserId && item.ParticipantType == account.ParticipantType, cancellationToken);
        var allowance = entitlement?.MonthlyCharacterAllowance ?? DefaultAllowance();
        var isUnlimited = entitlement?.IsUnlimited ?? false;
        await EnsureUsagePeriodAsync(account, period, cancellationToken);
        if (!await ReservePeriodAsync(account, period, allowance, isUnlimited, characters, cancellationToken))
        {
            await MarkQuotaDeniedAsync(request.RequestReference, account, period, cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return new TranslationQuotaReservationResult(false, false, false, "translation_quota_exhausted", null);
        }

        var ledgerId = await MarkReservedAsync(request.RequestReference, account, period, cancellationToken);
        if (ledgerId is null)
        {
            // A reservation must never be left charged when its durable request
            // record cannot be moved into the provider-ready state. Relational
            // writes roll back together; the in-memory test path compensates.
            if (transaction is null)
                await ReleasePeriodAsync(account, period, characters, providerExecuted: false, providerSucceeded: false, cancellationToken);
            return new TranslationQuotaReservationResult(false, false, false, "translation_accounting_unavailable", null);
        }

        var result = new TranslationQuotaReservationResult(
            true,
            false,
            false,
            null,
            new TranslationQuotaReservation(ledgerId.Value, account, period, characters, request.RequestReference));
        await CommitAsync(transaction, cancellationToken);
        return result;
    }

    public async Task CompleteAsync(
        TranslationQuotaReservation reservation,
        bool providerExecuted,
        bool providerSucceeded,
        string? failureCode,
        CancellationToken cancellationToken = default)
    {
        if (reservation.LedgerId == Guid.Empty || reservation.Characters <= 0)
            return;

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var ledger = await _db.Set<LegendTranslationUsageLedger>()
            .SingleOrDefaultAsync(item => item.Id == reservation.LedgerId, cancellationToken);
        if (ledger is null || ledger.Succeeded || ledger.State != ReservedState)
            return;

        await ReleasePeriodAsync(
            reservation.Account,
            reservation.PeriodStart,
            reservation.Characters,
            providerExecuted,
            providerSucceeded,
            cancellationToken);

        ledger.ProviderExecuted = providerExecuted;
        ledger.Succeeded = providerSucceeded;
        ledger.State = providerSucceeded ? SucceededState : providerExecuted ? "ProviderFailed" : "Released";
        ledger.FailureCode = providerSucceeded ? null : Bound(failureCode, 80);
        ledger.ReservationExpiresUtc = null;
        ledger.CompletedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

    public async Task RecordAvoidedAsync(
        MessagingActor account,
        TranslationAvoidedPath path,
        int characters,
        CancellationToken cancellationToken = default)
    {
        account = Normalize(account);
        var amount = Math.Max(0, characters);
        if (amount == 0 && path != TranslationAvoidedPath.GroupUniqueTargetReuse)
            return;

        var period = CurrentPeriod();
        await EnsureUsagePeriodAsync(account, period, cancellationToken);
        var now = DateTime.UtcNow;
        if (!_db.Database.IsRelational())
        {
            var usage = await _db.Set<LegendTranslationUsagePeriod>().SingleAsync(item =>
                item.UserId == account.UserId && item.ParticipantType == account.ParticipantType && item.PeriodStart == period,
                cancellationToken);
            ApplyAvoided(usage, path, amount);
            usage.LastTranslationActivityUtc = now;
            usage.UpdatedUtc = now;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        var query = _db.Set<LegendTranslationUsagePeriod>()
            .Where(item => item.UserId == account.UserId && item.ParticipantType == account.ParticipantType && item.PeriodStart == period);
        switch (path)
        {
            case TranslationAvoidedPath.SameLanguage:
                await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.SameLanguageCharactersAvoided, item => item.SameLanguageCharactersAvoided + amount)
                    .SetProperty(item => item.LastTranslationActivityUtc, now)
                    .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
                break;
            case TranslationAvoidedPath.TranslationMemory:
                await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.TranslationMemoryCharactersAvoided, item => item.TranslationMemoryCharactersAvoided + amount)
                    .SetProperty(item => item.LastTranslationActivityUtc, now)
                    .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
                break;
            case TranslationAvoidedPath.StructuralComposition:
                await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.StructuralCompositionCharactersAvoided, item => item.StructuralCompositionCharactersAvoided + amount)
                    .SetProperty(item => item.LastTranslationActivityUtc, now)
                    .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
                break;
            case TranslationAvoidedPath.ContextualComposition:
                await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ContextualCharactersAvoided, item => item.ContextualCharactersAvoided + amount)
                    .SetProperty(item => item.LastTranslationActivityUtc, now)
                    .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
                break;
            case TranslationAvoidedPath.PromotedTranslationModel:
                await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.PromotedTranslationModelCharactersAvoided, item => item.PromotedTranslationModelCharactersAvoided + amount)
                    .SetProperty(item => item.LastTranslationActivityUtc, now)
                    .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
                break;
            case TranslationAvoidedPath.ProviderObservationReuse:
                await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ProviderObservationCharactersAvoided, item => item.ProviderObservationCharactersAvoided + amount)
                    .SetProperty(item => item.LastTranslationActivityUtc, now)
                    .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
                break;
            case TranslationAvoidedPath.GroupUniqueTargetReuse:
                await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.GroupUniqueTargetReuseCount, item => item.GroupUniqueTargetReuseCount + Math.Max(1, amount))
                    .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
                break;
        }
    }

    private async Task<bool> TryClaimRetryAsync(
        LegendTranslationUsageLedger existing,
        MessagingActor account,
        TranslationQuotaReservationRequest request,
        DateOnly period,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (!_db.Database.IsRelational())
        {
            var tracked = await _db.Set<LegendTranslationUsageLedger>().SingleOrDefaultAsync(item => item.Id == existing.Id, cancellationToken);
            if (tracked is null || IsActiveReservation(tracked, now))
                return false;
            ResetLedgerForAttempt(tracked, account, request, period, now);
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var affected = await _db.Set<LegendTranslationUsageLedger>()
            .Where(item => item.Id == existing.Id &&
                (!item.Succeeded &&
                 (item.State != ReservingState && item.State != ReservedState ||
                  item.ReservationExpiresUtc == null || item.ReservationExpiresUtc < now)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.UserId, account.UserId)
                .SetProperty(item => item.ParticipantType, account.ParticipantType)
                .SetProperty(item => item.PeriodStart, period)
                .SetProperty(item => item.SourceLanguageCode, Bound(request.SourceLanguageCode, 32) ?? string.Empty)
                .SetProperty(item => item.TargetLanguageCode, Bound(request.TargetLanguageCode, 32) ?? string.Empty)
                .SetProperty(item => item.Provider, Bound(request.Provider, 80) ?? string.Empty)
                .SetProperty(item => item.BillableCharacters, Math.Max(0, request.BillableCharacters))
                .SetProperty(item => item.ProviderExecuted, false)
                .SetProperty(item => item.Succeeded, false)
                .SetProperty(item => item.State, ReservingState)
                .SetProperty(item => item.FailureCode, (string?)null)
                .SetProperty(item => item.ReservationExpiresUtc, ReservationExpiry(now))
                .SetProperty(item => item.CompletedUtc, (DateTime?)null), cancellationToken);
        return affected == 1;
    }

    private async Task EnsureUsagePeriodAsync(
        MessagingActor account,
        DateOnly period,
        CancellationToken cancellationToken)
    {
        if (await _db.Set<LegendTranslationUsagePeriod>().AnyAsync(item =>
                item.UserId == account.UserId && item.ParticipantType == account.ParticipantType && item.PeriodStart == period,
                cancellationToken))
            return;

        var created = new LegendTranslationUsagePeriod
        {
            Id = Guid.NewGuid(),
            UserId = account.UserId,
            ParticipantType = account.ParticipantType,
            PeriodStart = period,
            UpdatedUtc = DateTime.UtcNow
        };
        _db.Set<LegendTranslationUsagePeriod>().Add(created);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _db.Entry(created).State = EntityState.Detached;
        }
    }

    private async Task<bool> ReservePeriodAsync(
        MessagingActor account,
        DateOnly period,
        long allowance,
        bool isUnlimited,
        long characters,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (!_db.Database.IsRelational())
        {
            var usage = await _db.Set<LegendTranslationUsagePeriod>().SingleAsync(item =>
                item.UserId == account.UserId && item.ParticipantType == account.ParticipantType && item.PeriodStart == period,
                cancellationToken);
            if (!isUnlimited && usage.ConsumedCharacters + usage.ReservedCharacters + characters > allowance)
                return false;
            usage.ReservedCharacters += characters;
            usage.LastTranslationActivityUtc = now;
            usage.UpdatedUtc = now;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var query = _db.Set<LegendTranslationUsagePeriod>()
            .Where(item => item.UserId == account.UserId && item.ParticipantType == account.ParticipantType && item.PeriodStart == period);
        if (!isUnlimited)
            query = query.Where(item => item.ConsumedCharacters + item.ReservedCharacters + characters <= allowance);
        var affected = await query.ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.ReservedCharacters, item => item.ReservedCharacters + characters)
            .SetProperty(item => item.LastTranslationActivityUtc, now)
            .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
        return affected == 1;
    }

    private async Task<Guid?> MarkReservedAsync(
        string requestReference,
        MessagingActor account,
        DateOnly period,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (!_db.Database.IsRelational())
        {
            var ledger = await _db.Set<LegendTranslationUsageLedger>().SingleOrDefaultAsync(item =>
                item.RequestReference == requestReference && item.UserId == account.UserId &&
                item.ParticipantType == account.ParticipantType && item.PeriodStart == period,
                cancellationToken);
            if (ledger is null || ledger.State != ReservingState)
                return null;
            ledger.State = ReservedState;
            ledger.ReservationExpiresUtc = ReservationExpiry(now);
            await _db.SaveChangesAsync(cancellationToken);
            return ledger.Id;
        }

        var ledgerId = await _db.Set<LegendTranslationUsageLedger>()
            .Where(item => item.RequestReference == requestReference && item.UserId == account.UserId &&
                item.ParticipantType == account.ParticipantType && item.PeriodStart == period && item.State == ReservingState)
            .Select(item => (Guid?)item.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (ledgerId is null)
            return null;
        var affected = await _db.Set<LegendTranslationUsageLedger>()
            .Where(item => item.Id == ledgerId.Value && item.State == ReservingState)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, ReservedState)
                .SetProperty(item => item.ReservationExpiresUtc, ReservationExpiry(now)), cancellationToken);
        return affected == 1 ? ledgerId : null;
    }

    private async Task MarkQuotaDeniedAsync(
        string requestReference,
        MessagingActor account,
        DateOnly period,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var ledger = await _db.Set<LegendTranslationUsageLedger>().SingleOrDefaultAsync(item =>
            item.RequestReference == requestReference && item.UserId == account.UserId &&
            item.ParticipantType == account.ParticipantType && item.PeriodStart == period,
            cancellationToken);
        if (ledger is not null)
        {
            ledger.State = QuotaDeniedState;
            ledger.FailureCode = "translation_quota_exhausted";
            ledger.ReservationExpiresUtc = null;
            ledger.CompletedUtc = now;
        }

        if (!_db.Database.IsRelational())
        {
            var usage = await _db.Set<LegendTranslationUsagePeriod>().SingleAsync(item =>
                item.UserId == account.UserId && item.ParticipantType == account.ParticipantType && item.PeriodStart == period,
                cancellationToken);
            usage.QuotaDeniedRequestCount++;
            usage.UpdatedUtc = now;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await _db.Set<LegendTranslationUsagePeriod>()
            .Where(item => item.UserId == account.UserId && item.ParticipantType == account.ParticipantType && item.PeriodStart == period)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.QuotaDeniedRequestCount, item => item.QuotaDeniedRequestCount + 1)
                .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
    }

    private async Task ReleasePeriodAsync(
        MessagingActor account,
        DateOnly period,
        long characters,
        bool providerExecuted,
        bool providerSucceeded,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (!_db.Database.IsRelational())
        {
            var usage = await _db.Set<LegendTranslationUsagePeriod>().SingleAsync(item =>
                item.UserId == account.UserId && item.ParticipantType == account.ParticipantType && item.PeriodStart == period,
                cancellationToken);
            usage.ReservedCharacters = Math.Max(0, usage.ReservedCharacters - characters);
            if (providerExecuted)
            {
                usage.ProviderOperationCount++;
                if (providerSucceeded)
                {
                    usage.ConsumedCharacters += characters;
                    usage.ProviderBillableCharacters += characters;
                }
                else
                    usage.ProviderFailureCount++;
            }
            usage.LastTranslationActivityUtc = now;
            usage.UpdatedUtc = now;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        await _db.Set<LegendTranslationUsagePeriod>()
            .Where(item => item.UserId == account.UserId && item.ParticipantType == account.ParticipantType && item.PeriodStart == period)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ReservedCharacters, item => item.ReservedCharacters - characters)
                .SetProperty(item => item.ProviderOperationCount, item => item.ProviderOperationCount + (providerExecuted ? 1 : 0))
                .SetProperty(item => item.ConsumedCharacters, item => item.ConsumedCharacters + (providerExecuted && providerSucceeded ? characters : 0))
                .SetProperty(item => item.ProviderBillableCharacters, item => item.ProviderBillableCharacters + (providerExecuted && providerSucceeded ? characters : 0))
                .SetProperty(item => item.ProviderFailureCount, item => item.ProviderFailureCount + (providerExecuted && !providerSucceeded ? 1 : 0))
                .SetProperty(item => item.LastTranslationActivityUtc, now)
                .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
    }

    private static void ApplyAvoided(LegendTranslationUsagePeriod usage, TranslationAvoidedPath path, long amount)
    {
        switch (path)
        {
            case TranslationAvoidedPath.SameLanguage:
                usage.SameLanguageCharactersAvoided += amount;
                break;
            case TranslationAvoidedPath.TranslationMemory:
                usage.TranslationMemoryCharactersAvoided += amount;
                break;
            case TranslationAvoidedPath.StructuralComposition:
                usage.StructuralCompositionCharactersAvoided += amount;
                break;
            case TranslationAvoidedPath.ContextualComposition:
                usage.ContextualCharactersAvoided += amount;
                break;
            case TranslationAvoidedPath.PromotedTranslationModel:
                usage.PromotedTranslationModelCharactersAvoided += amount;
                break;
            case TranslationAvoidedPath.ProviderObservationReuse:
                usage.ProviderObservationCharactersAvoided += amount;
                break;
            case TranslationAvoidedPath.GroupUniqueTargetReuse:
                usage.GroupUniqueTargetReuseCount += Math.Max(1, amount);
                break;
        }
    }

    private void ResetLedgerForAttempt(
        LegendTranslationUsageLedger ledger,
        MessagingActor account,
        TranslationQuotaReservationRequest request,
        DateOnly period,
        DateTime now)
    {
        ledger.UserId = account.UserId;
        ledger.ParticipantType = account.ParticipantType;
        ledger.PeriodStart = period;
        ledger.SourceLanguageCode = Bound(request.SourceLanguageCode, 32) ?? string.Empty;
        ledger.TargetLanguageCode = Bound(request.TargetLanguageCode, 32) ?? string.Empty;
        ledger.Provider = Bound(request.Provider, 80) ?? string.Empty;
        ledger.BillableCharacters = Math.Max(0, request.BillableCharacters);
        ledger.ProviderExecuted = false;
        ledger.Succeeded = false;
        ledger.State = ReservingState;
        ledger.FailureCode = null;
        ledger.ReservationExpiresUtc = ReservationExpiry(now);
        ledger.CompletedUtc = null;
    }

    private TranslationAccountEntitlementSnapshot ToSnapshot(
        ControlledResourceAccess access,
        LegendTranslationEntitlement? entitlement,
        LegendTranslationUsagePeriod? usage,
        DateOnly period)
    {
        var allowance = Math.Max(0, entitlement?.MonthlyCharacterAllowance ?? DefaultAllowance());
        var unlimited = entitlement?.IsUnlimited ?? false;
        var consumed = Math.Max(0, usage?.ConsumedCharacters ?? 0);
        var reserved = Math.Max(0, usage?.ReservedCharacters ?? 0);
        long? remaining = unlimited ? null : Math.Max(0, allowance - consumed - reserved);
        var percentUsed = unlimited || allowance == 0
            ? 0m
            : Math.Min(100m, Math.Round((decimal)(consumed + reserved) / allowance * 100m, 2));
        var start = DateTime.SpecifyKind(period.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var end = start.AddMonths(1);
        return new TranslationAccountEntitlementSnapshot(
            access.State,
            access.CanManage,
            allowance,
            unlimited,
            consumed,
            reserved,
            remaining,
            percentUsed,
            start,
            end,
            end,
            entitlement?.EntitlementSource ?? "DefaultPolicy",
            entitlement?.IsFounderOverride ?? false,
            usage?.LastTranslationActivityUtc);
    }

    private static TranslationAccountUsageMetrics ToUsageMetrics(LegendTranslationUsagePeriod? usage) => new(
        usage?.ProviderOperationCount ?? 0,
        usage?.ProviderBillableCharacters ?? 0,
        usage?.SameLanguageCharactersAvoided ?? 0,
        usage?.TranslationMemoryCharactersAvoided ?? 0,
        usage?.ContextualCharactersAvoided ?? 0,
        usage?.QuotaDeniedRequestCount ?? 0,
        usage?.ProviderFailureCount ?? 0,
        usage?.GroupUniqueTargetReuseCount ?? 0,
        usage?.StructuralCompositionCharactersAvoided ?? 0,
        usage?.PromotedTranslationModelCharactersAvoided ?? 0,
        usage?.ProviderObservationCharactersAvoided ?? 0);

    private long DefaultAllowance() => Math.Max(0,
        _configuration.GetValue<long?>("LegendConnect:Entitlements:DefaultMonthlyCharacterAllowance") ?? 0);

    private DateTime ReservationExpiry(DateTime now) => now.AddSeconds(Math.Clamp(
        _configuration.GetValue<int?>("LegendConnect:Entitlements:ReservationLeaseSeconds") ?? 45,
        15,
        300));

    private static bool IsActiveReservation(LegendTranslationUsageLedger ledger, DateTime now) =>
        ledger.State is ReservingState or ReservedState &&
        ledger.ReservationExpiresUtc is { } expiry && expiry >= now;

    private static bool IsRequestReference(string? reference) =>
        reference is { Length: 64 } && reference.All(character => char.IsAsciiHexDigit(character));

    private IQueryable<ClientProfile> ActiveCurrentPayingClients() =>
        _db.ClientProfiles
            .AsNoTracking()
            .Where(profile =>
                !string.IsNullOrWhiteSpace(profile.ClientUserId) &&
                profile.CrmStatus != null &&
                profile.CrmStatus.ToLower() == "active" &&
                !_db.AccountLifecycleRecords.Any(record =>
                    record.ProfileId == profile.Id &&
                    record.ParticipantType == MessagingParticipantTypes.Client &&
                    record.State == Domain.Accounts.AccountLifecycleStates.Closed) &&
                _db.ClientSubscriptions.Any(subscription =>
                    subscription.ClientProfileId == profile.Id &&
                    subscription.Status == ClientSubscriptionStatus.Active &&
                    subscription.PaymentStanding == ClientSubscriptionPaymentStanding.Current));

    private static DateOnly CurrentPeriod()
    {
        var now = DateTime.UtcNow;
        return new DateOnly(now.Year, now.Month, 1);
    }

    private static MessagingActor Normalize(MessagingActor actor) => new(
        actor.UserId?.Trim().ToLowerInvariant() ?? string.Empty,
        actor.ParticipantType?.Trim() ?? string.Empty);

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string? NormalizeSearch(string? value) => Bound(value, 120)?.ToLowerInvariant();

    private static string? Bound(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized[..Math.Min(normalized.Length, maximumLength)];
    }

    private static Task CommitAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken) =>
        transaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

    private sealed record AccountDirectoryRow(
        string UserId,
        string ParticipantType,
        string DisplayName,
        Guid? ProfileId);

    private sealed class EntitlementPresetConfiguration
    {
        public string? Key { get; init; }
        public string? DisplayName { get; init; }
        public long CharacterAllowance { get; init; }
    }
}
