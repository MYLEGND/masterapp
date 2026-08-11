using System.Security.Cryptography;
using System.Text;

namespace Domain.Messaging;

/// <summary>
/// Server-issued account view of translation permission, entitlement, and
/// current-period consumption. The three values remain distinct.
/// </summary>
public sealed record TranslationAccountEntitlementSnapshot(
    string AccessState,
    bool CanManage,
    long CharacterAllowance,
    bool IsUnlimited,
    long ConsumedCharacters,
    long ReservedCharacters,
    long? RemainingCharacters,
    decimal PercentUsed,
    DateTime PeriodStartUtc,
    DateTime PeriodEndUtc,
    DateTime NextResetUtc,
    string EntitlementSource,
    bool IsFounderOverride,
    DateTime? LastTranslationActivityUtc);

public sealed record TranslationEntitlementMutation(
    MessagingActor Target,
    long CharacterAllowance,
    bool IsUnlimited,
    string EntitlementSource,
    bool IsFounderOverride);

public sealed record TranslationFounderAccountUsageSnapshot(
    MessagingActor Account,
    string DisplayName,
    string AccessState,
    string? PreferredLanguage,
    TranslationAccountEntitlementSnapshot Entitlement,
    TranslationAccountUsageMetrics Usage);

/// <summary>
/// Bounded, CRM-backed Founder account selection for translation management.
/// The account directory is intentionally not reconstructed from historic
/// grants, entitlements, or usage: only active, current-paying Client CRM
/// records are eligible for the operational surface.
/// </summary>
public sealed record TranslationFounderAccountSearchSnapshot(
    IReadOnlyList<TranslationFounderAccountUsageSnapshot> Accounts,
    string? Query,
    bool HasMore);

public sealed record TranslationAccountUsageMetrics(
    long ProviderOperationCount,
    long ProviderBillableCharacters,
    long SameLanguageCharactersAvoided,
    long TranslationMemoryCharactersAvoided,
    long ContextualCharactersAvoided,
    long QuotaDeniedRequestCount,
    long ProviderFailureCount,
    long GroupUniqueTargetReuseCount);

public sealed record TranslationFounderScaleSnapshot(
    long ProviderOperationCount,
    long ProviderBillableCharacters,
    long SameLanguageCharactersAvoided,
    long TranslationMemoryCharactersAvoided,
    long ContextualCharactersAvoided,
    long QuotaDeniedRequestCount,
    long ProviderFailureCount,
    long GroupUniqueTargetReuseCount,
    long HighConsumptionAccountCount);

/// <summary>
/// Server configuration can publish named allowance presets without moving an
/// entitlement decision into a web or mobile client.
/// </summary>
public sealed record TranslationEntitlementPreset(
    string Key,
    string DisplayName,
    long CharacterAllowance);

public sealed record TranslationQuotaReservationRequest(
    MessagingActor Account,
    string RequestReference,
    string SourceLanguageCode,
    string TargetLanguageCode,
    string Provider,
    int BillableCharacters);

public sealed record TranslationQuotaReservation(
    Guid LedgerId,
    MessagingActor Account,
    DateOnly PeriodStart,
    long Characters,
    string RequestReference);

public sealed record TranslationQuotaReservationResult(
    bool Succeeded,
    bool AlreadyCompleted,
    bool IsInProgress,
    string? ErrorCode,
    TranslationQuotaReservation? Reservation);

public enum TranslationAvoidedPath
{
    SameLanguage,
    TranslationMemory,
    ContextualComposition,
    GroupUniqueTargetReuse
}

/// <summary>
/// Single server authority for account translation entitlement, current-period
/// usage, and durable reservation/ledger finalization. It does not translate
/// text and it never stores message bodies.
/// </summary>
public interface ITranslationEntitlementAuthority
{
    Task<TranslationAccountEntitlementSnapshot> GetSnapshotAsync(
        MessagingActor account,
        CancellationToken cancellationToken = default);

    Task<TranslationFounderAccountSearchSnapshot> SearchFounderAccountsAsync(
        string? search,
        int take,
        CancellationToken cancellationToken = default);

    Task<bool> IsFounderEntitlementEligibleAsync(
        MessagingActor account,
        CancellationToken cancellationToken = default);

    Task<TranslationFounderScaleSnapshot> GetFounderScaleAsync(
        CancellationToken cancellationToken = default);

    IReadOnlyList<TranslationEntitlementPreset> GetFounderEntitlementPresets();

    Task<TranslationAccountEntitlementSnapshot> SetEntitlementAsync(
        string founderUserId,
        TranslationEntitlementMutation mutation,
        CancellationToken cancellationToken = default);

    Task<TranslationQuotaReservationResult> TryReserveAsync(
        TranslationQuotaReservationRequest request,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        TranslationQuotaReservation reservation,
        bool providerExecuted,
        bool providerSucceeded,
        string? failureCode,
        CancellationToken cancellationToken = default);

    Task RecordAvoidedAsync(
        MessagingActor account,
        TranslationAvoidedPath path,
        int characters,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stable one-way references protect both retry idempotency and message-body
/// privacy. A message ID is already server-owned; its hash is what reaches the
/// durable usage ledger.
/// </summary>
public static class TranslationUsageReference
{
    public static string ForMessage(Guid messageId, string targetLanguage, string privacyContext = "message")
    {
        var raw = string.Join(':', privacyContext, messageId.ToString("N"), targetLanguage.Trim().ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}

/// <summary>
/// Optional richer interface implemented by the existing router. Messaging
/// uses it when an account and a safe immutable message reference are known;
/// generic provider callers remain source-compatible with ITranslationService.
/// </summary>
public interface IAccountScopedTranslationService : ITranslationService
{
    Task<TranslationProviderResult> TranslateForAccountAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage,
        MessagingActor account,
        string requestReference,
        CancellationToken cancellationToken = default);
}
