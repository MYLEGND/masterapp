using Domain.Entities.FinancialIntelligence;

namespace Domain.FinancialIntelligence;

public record FinancialIntelligenceResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedSummary);

public sealed record FinancialDataConnectionUpsertCommand(
    Guid ClientProfileId,
    string ProviderKey,
    string ProviderItemId,
    string? ProviderInstitutionId = null,
    string? DisplayName = null,
    string? EncryptedAccessToken = null,
    string? Status = null,
    Guid? ConnectionId = null);

public sealed record FinancialDataConnectionStatusCommand(
    Guid ClientProfileId,
    Guid ConnectionId,
    string Status,
    string? LastErrorCode = null,
    string? LastErrorMessage = null);

public sealed record FinancialDataConnectionResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedSummary,
    FinancialDataConnection? Connection = null)
    : FinancialIntelligenceResult(Success, SafeErrorCode, SanitizedSummary);

public sealed record FinancialAccountImport(
    string ProviderAccountId,
    string Name,
    string AccountType,
    string? PersistentAccountKey = null,
    string? OfficialName = null,
    string? Mask = null,
    string? AccountSubtype = null,
    string CurrencyCode = "USD",
    long? CurrentBalanceCents = null,
    long? AvailableBalanceCents = null,
    bool IsClosed = false);

public sealed record FinancialTransactionImport(
    string ProviderTransactionId,
    string ProviderAccountId,
    string OriginalName,
    DateTime PostedUtc,
    long AmountCents,
    string CurrencyCode = "USD",
    string? ProviderPendingTransactionId = null,
    string? OriginalMerchantName = null,
    DateTime? AuthorizedUtc = null,
    bool IsPending = false,
    bool IsRemoved = false,
    string? ProviderCategoryJson = null,
    string ProviderPayloadJson = "{}");

public sealed record FinancialImportCommand(
    Guid ClientProfileId,
    Guid FinancialDataConnectionId,
    IReadOnlyCollection<FinancialAccountImport> Accounts,
    IReadOnlyCollection<FinancialTransactionImport> Transactions,
    string? NextSyncCursor = null,
    DateTime? ImportedUtc = null);

public sealed record FinancialImportResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedSummary,
    int ImportedAccountCount,
    int ImportedTransactionCount,
    int RefreshedStreamCount)
    : FinancialIntelligenceResult(Success, SafeErrorCode, SanitizedSummary);

public sealed record RefreshRecurringFinancialStreamsCommand(
    Guid ClientProfileId,
    Guid? FinancialDataConnectionId = null);

public sealed record RefreshRecurringFinancialStreamsResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedSummary,
    int CreatedCount,
    int UpdatedCount,
    int InactivatedCount,
    IReadOnlyList<RecurringFinancialStream> Streams)
    : FinancialIntelligenceResult(Success, SafeErrorCode, SanitizedSummary);

public sealed record ExpenseLensStreamLinkCommand(
    Guid ClientProfileId,
    Guid RecurringFinancialStreamId,
    string ExpenseLensItemId,
    bool Confirmed,
    string? ConfirmedByUserId = null,
    string ExpenseLensToolId = "ExpenseLens");

public sealed record ExpenseLensStreamLinkResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedSummary,
    ExpenseLensStreamLink? Link = null)
    : FinancialIntelligenceResult(Success, SafeErrorCode, SanitizedSummary);

public sealed record ExpenseLensSynchronizationResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedSummary,
    int ValidLinkCount,
    int StaleLinkCount)
    : FinancialIntelligenceResult(Success, SafeErrorCode, SanitizedSummary);
