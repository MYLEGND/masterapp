using Domain.Entities.FinancialIntelligence;

namespace Domain.FinancialIntelligence;

public interface IFinancialConnectionService
{
    Task<FinancialDataConnectionResult> GetAsync(
        Guid clientProfileId,
        Guid connectionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FinancialDataConnection>> ListAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default);

    Task<FinancialDataConnectionResult> UpsertAsync(
        FinancialDataConnectionUpsertCommand command,
        CancellationToken cancellationToken = default);

    Task<FinancialDataConnectionResult> UpdateStatusAsync(
        FinancialDataConnectionStatusCommand command,
        CancellationToken cancellationToken = default);
}

public interface IFinancialImportService
{
    Task<FinancialImportResult> ImportAsync(
        FinancialImportCommand command,
        CancellationToken cancellationToken = default);
}

public interface IRecurringFinancialStreamService
{
    Task<RefreshRecurringFinancialStreamsResult> RefreshAsync(
        RefreshRecurringFinancialStreamsCommand command,
        CancellationToken cancellationToken = default);
}

public interface IExpenseLensSynchronizationService
{
    Task<ExpenseLensStreamLinkResult> LinkStreamAsync(
        ExpenseLensStreamLinkCommand command,
        CancellationToken cancellationToken = default);

    Task<ExpenseLensSynchronizationResult> SynchronizeAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default);
}
