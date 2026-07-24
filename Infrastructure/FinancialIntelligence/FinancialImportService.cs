using Domain.Entities.FinancialIntelligence;
using Domain.FinancialIntelligence;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.FinancialIntelligence;

internal sealed class FinancialImportService : IFinancialImportService
{
    private readonly MasterAppDbContext _db;
    private readonly IRecurringFinancialStreamService _recurringStreams;
    private readonly ILogger<FinancialImportService> _logger;

    public FinancialImportService(
        MasterAppDbContext db,
        IRecurringFinancialStreamService recurringStreams,
        ILogger<FinancialImportService> logger)
    {
        _db = db;
        _recurringStreams = recurringStreams;
        _logger = logger;
    }

    public async Task<FinancialImportResult> ImportAsync(
        FinancialImportCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command.ClientProfileId == Guid.Empty || command.FinancialDataConnectionId == Guid.Empty)
            return Failure("IMPORT_SCOPE_INVALID", "A client profile and financial connection are required.");

        var accounts = command.Accounts?.ToList();
        var transactions = command.Transactions?.ToList();
        if (accounts is null || transactions is null ||
            !ValidateAccounts(accounts) || !ValidateTransactions(transactions) ||
            HasDuplicateProviderAccountIds(accounts) || HasDuplicateProviderTransactionIds(transactions))
        {
            return Failure("IMPORT_INPUT_INVALID", "The financial import payload is invalid.");
        }

        var connection = await FinancialIntelligenceScope.FindConnectionAsync(
            _db,
            command.ClientProfileId,
            command.FinancialDataConnectionId,
            asNoTracking: false,
            cancellationToken);

        if (connection is null)
            return Failure("CONNECTION_NOT_FOUND", "The requested financial connection was not found for this client.");

        var importedAccountIds = accounts
            .Select(x => NormalizeRequired(x.ProviderAccountId))
            .Concat(transactions.Select(x => NormalizeRequired(x.ProviderAccountId)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var existingAccounts = importedAccountIds.Count == 0
            ? new List<ImportedFinancialAccount>()
            : await _db.ImportedFinancialAccounts
                .Where(x =>
                    x.ClientProfileId == command.ClientProfileId &&
                    x.FinancialDataConnectionId == command.FinancialDataConnectionId &&
                    importedAccountIds.Contains(x.ProviderAccountId))
                .ToListAsync(cancellationToken);

        var accountsByProviderId = existingAccounts.ToDictionary(
            x => x.ProviderAccountId,
            StringComparer.Ordinal);

        var nowUtc = command.ImportedUtc ?? DateTime.UtcNow;
        var importedAccountCount = 0;
        foreach (var accountImport in accounts)
        {
            var providerAccountId = NormalizeRequired(accountImport.ProviderAccountId);
            if (!accountsByProviderId.TryGetValue(providerAccountId, out var account))
            {
                account = new ImportedFinancialAccount
                {
                    ClientProfileId = command.ClientProfileId,
                    FinancialDataConnectionId = command.FinancialDataConnectionId,
                    ProviderAccountId = providerAccountId,
                    CreatedUtc = nowUtc
                };
                _db.ImportedFinancialAccounts.Add(account);
                accountsByProviderId.Add(providerAccountId, account);
            }

            ApplyAccountImport(account, accountImport, nowUtc);
            importedAccountCount += 1;
        }

        if (transactions.Any(x => !accountsByProviderId.ContainsKey(NormalizeRequired(x.ProviderAccountId))))
        {
            return Failure(
                "IMPORT_ACCOUNT_NOT_FOUND",
                "Each imported transaction must belong to an account on the same financial connection.");
        }

        var providerTransactionIds = transactions
            .Select(x => NormalizeRequired(x.ProviderTransactionId))
            .ToList();
        var existingTransactions = providerTransactionIds.Count == 0
            ? new List<ImportedFinancialTransaction>()
            : await _db.ImportedFinancialTransactions
                .Where(x =>
                    x.ClientProfileId == command.ClientProfileId &&
                    x.FinancialDataConnectionId == command.FinancialDataConnectionId &&
                    providerTransactionIds.Contains(x.ProviderTransactionId))
                .ToListAsync(cancellationToken);
        var transactionsByProviderId = existingTransactions.ToDictionary(
            x => x.ProviderTransactionId,
            StringComparer.Ordinal);

        var importedTransactionCount = 0;
        foreach (var transactionImport in transactions)
        {
            var providerTransactionId = NormalizeRequired(transactionImport.ProviderTransactionId);
            if (!transactionsByProviderId.TryGetValue(providerTransactionId, out var transaction))
            {
                transaction = new ImportedFinancialTransaction
                {
                    ClientProfileId = command.ClientProfileId,
                    FinancialDataConnectionId = command.FinancialDataConnectionId,
                    ImportedFinancialAccountId = accountsByProviderId[NormalizeRequired(transactionImport.ProviderAccountId)].Id,
                    ProviderTransactionId = providerTransactionId,
                    ImportedUtc = nowUtc
                };
                _db.ImportedFinancialTransactions.Add(transaction);
                transactionsByProviderId.Add(providerTransactionId, transaction);
            }

            ApplyTransactionImport(
                transaction,
                accountsByProviderId[NormalizeRequired(transactionImport.ProviderAccountId)],
                transactionImport,
                nowUtc);
            importedTransactionCount += 1;
        }

        connection.LastSyncStartedUtc = nowUtc;
        connection.LastSyncCompletedUtc = nowUtc;
        connection.SyncCursor = NormalizeOptional(command.NextSyncCursor) ?? connection.SyncCursor;
        connection.LastErrorCode = null;
        connection.LastErrorMessage = null;
        connection.UpdatedUtc = nowUtc;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(
                ex,
                "Financial import save failed. ClientProfileId={ClientProfileId} ConnectionId={ConnectionId}",
                command.ClientProfileId,
                command.FinancialDataConnectionId);
            return Failure("IMPORT_SAVE_FAILED", "The financial import could not be saved.");
        }

        var streamResult = await _recurringStreams.RefreshAsync(
            new RefreshRecurringFinancialStreamsCommand(
                command.ClientProfileId,
                command.FinancialDataConnectionId),
            cancellationToken);

        if (!streamResult.Success)
        {
            _logger.LogWarning(
                "Financial import completed but recurring stream refresh failed. ClientProfileId={ClientProfileId} ConnectionId={ConnectionId} ErrorCode={ErrorCode}",
                command.ClientProfileId,
                command.FinancialDataConnectionId,
                streamResult.SafeErrorCode);
        }

        _logger.LogInformation(
            "Financial import completed. ClientProfileId={ClientProfileId} ConnectionId={ConnectionId} Accounts={AccountCount} Transactions={TransactionCount} RefreshedStreams={RefreshedStreamCount}",
            command.ClientProfileId,
            command.FinancialDataConnectionId,
            importedAccountCount,
            importedTransactionCount,
            streamResult.Success ? streamResult.CreatedCount + streamResult.UpdatedCount : 0);

        return new FinancialImportResult(
            true,
            null,
            streamResult.Success ? null : "Financial data imported; recurring stream detection will retry on the next import.",
            importedAccountCount,
            importedTransactionCount,
            streamResult.Success ? streamResult.CreatedCount + streamResult.UpdatedCount : 0);
    }

    private static void ApplyAccountImport(
        ImportedFinancialAccount account,
        FinancialAccountImport source,
        DateTime nowUtc)
    {
        account.PersistentAccountKey = NormalizeOptional(source.PersistentAccountKey);
        account.Name = NormalizeRequired(source.Name);
        account.OfficialName = NormalizeOptional(source.OfficialName);
        account.Mask = NormalizeOptional(source.Mask);
        account.AccountType = NormalizeRequired(source.AccountType);
        account.AccountSubtype = NormalizeOptional(source.AccountSubtype);
        account.CurrencyCode = NormalizeCurrency(source.CurrencyCode);
        account.CurrentBalanceCents = source.CurrentBalanceCents;
        account.AvailableBalanceCents = source.AvailableBalanceCents;
        account.IsClosed = source.IsClosed;
        account.UpdatedUtc = nowUtc;
    }

    private static void ApplyTransactionImport(
        ImportedFinancialTransaction transaction,
        ImportedFinancialAccount account,
        FinancialTransactionImport source,
        DateTime nowUtc)
    {
        transaction.ImportedFinancialAccountId = account.Id;
        transaction.ProviderPendingTransactionId = NormalizeOptional(source.ProviderPendingTransactionId);
        transaction.OriginalName = NormalizeRequired(source.OriginalName);
        transaction.OriginalMerchantName = NormalizeOptional(source.OriginalMerchantName);
        transaction.AuthorizedUtc = source.AuthorizedUtc;
        transaction.PostedUtc = source.PostedUtc;
        transaction.AmountCents = source.AmountCents;
        transaction.CurrencyCode = NormalizeCurrency(source.CurrencyCode);
        transaction.IsPending = source.IsPending;
        transaction.IsRemoved = source.IsRemoved;
        transaction.ProviderCategoryJson = NormalizeOptional(source.ProviderCategoryJson);
        transaction.ProviderPayloadJson = string.IsNullOrWhiteSpace(source.ProviderPayloadJson)
            ? "{}"
            : source.ProviderPayloadJson;
        transaction.UpdatedUtc = nowUtc;
    }

    private static bool ValidateAccounts(IReadOnlyCollection<FinancialAccountImport> accounts) =>
        accounts.All(x =>
            !string.IsNullOrWhiteSpace(x.ProviderAccountId) && Fits(x.ProviderAccountId, 200) &&
            !string.IsNullOrWhiteSpace(x.Name) && Fits(x.Name, 200) &&
            !string.IsNullOrWhiteSpace(x.AccountType) && Fits(x.AccountType, 50) &&
            Fits(x.PersistentAccountKey, 200) && Fits(x.OfficialName, 300) && Fits(x.Mask, 20) &&
            Fits(x.AccountSubtype, 80) && IsCurrencyCode(x.CurrencyCode));

    private static bool ValidateTransactions(IReadOnlyCollection<FinancialTransactionImport> transactions) =>
        transactions.All(x =>
            !string.IsNullOrWhiteSpace(x.ProviderTransactionId) && Fits(x.ProviderTransactionId, 200) &&
            !string.IsNullOrWhiteSpace(x.ProviderAccountId) && Fits(x.ProviderAccountId, 200) &&
            !string.IsNullOrWhiteSpace(x.OriginalName) && Fits(x.OriginalName, 500) &&
            x.PostedUtc != default &&
            Fits(x.ProviderPendingTransactionId, 200) && Fits(x.OriginalMerchantName, 500) &&
            IsCurrencyCode(x.CurrencyCode));

    private static bool HasDuplicateProviderAccountIds(IReadOnlyCollection<FinancialAccountImport> accounts) =>
        accounts.Select(x => NormalizeRequired(x.ProviderAccountId))
            .Distinct(StringComparer.Ordinal)
            .Count() != accounts.Count;

    private static bool HasDuplicateProviderTransactionIds(IReadOnlyCollection<FinancialTransactionImport> transactions) =>
        transactions.Select(x => NormalizeRequired(x.ProviderTransactionId))
            .Distinct(StringComparer.Ordinal)
            .Count() != transactions.Count;

    private static FinancialImportResult Failure(string errorCode, string summary) =>
        new(false, errorCode, summary, 0, 0, 0);

    private static bool Fits(string? value, int maximumLength) =>
        value is null || value.Trim().Length <= maximumLength;

    private static bool IsCurrencyCode(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length == 3;

    private static string NormalizeCurrency(string value) => value.Trim().ToUpperInvariant();

    private static string NormalizeRequired(string? value) => value?.Trim() ?? string.Empty;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
