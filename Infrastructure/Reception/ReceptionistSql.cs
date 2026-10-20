using System.Data;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Infrastructure.Reception;

/// <summary>Uses the canonical context and database options, with a fresh unit of work per retry.</summary>
internal static class ReceptionistSql
{
    internal static DbContextOptions<MasterAppDbContext> Options(MasterAppDbContext context) =>
        context.GetService<IDbContextOptions>() as DbContextOptions<MasterAppDbContext>
        ?? throw new InvalidOperationException("Canonical database options are required.");

    internal static async Task<T> TransactionAsync<T>(DbContextOptions<MasterAppDbContext> options,
        IReadOnlyCollection<Guid> accountIds, Func<MasterAppDbContext, Task<T>> operation, CancellationToken ct)
    {
        await using var strategyContext = new MasterAppDbContext(options);
        if (!strategyContext.Database.IsRelational())
            throw new InvalidOperationException("Receptionist admission requires durable relational transactions.");
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var db = new MasterAppDbContext(options);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            // An actual UPDATE takes transaction-duration write locks in SQL Server and SQLite.
            // Every operation acquires the same dimensions in this order, before reading balances.
            foreach (var id in accountIds.Distinct().OrderBy(x => x))
            {
                if (await db.Set<ReceptionistBudgetAccount>().Where(x => x.Id == id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Revision, x => x.Revision), ct) != 1)
                    throw new InvalidOperationException("A required accounting dimension is absent.");
            }
            var result = await operation(db);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    // Keep ownership reconstruction exact; a mismatched stored key is corruption, not a new tenant.
    internal static Shared.Analytics.MarketingOwnerScope Owner(MarketingConnection row)
    {
        var owner = row.OwnerType switch
        {
            "founder" when row.AgentTrackingProfileId is null && row.CommerceBusinessId is null => Shared.Analytics.MarketingOwnerScope.Founder,
            "agent" when row.AgentTrackingProfileId is { } agent && row.CommerceBusinessId is null => Shared.Analytics.MarketingOwnerScope.Agent(agent),
            "business" when row.CommerceBusinessId is { } business && row.AgentTrackingProfileId is null => Shared.Analytics.MarketingOwnerScope.Business(business),
            _ => throw new InvalidOperationException("Receptionist owner identity is inconsistent.")
        };
        if (row.OwnerKey != owner.Key) throw new InvalidOperationException("Receptionist owner key is inconsistent.");
        return owner;
    }
}
