using Domain.Entities.FinancialIntelligence;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.FinancialIntelligence;

internal static class FinancialIntelligenceScope
{
    public static Task<bool> ClientProfileExistsAsync(
        MasterAppDbContext db,
        Guid clientProfileId,
        CancellationToken cancellationToken)
    {
        return clientProfileId == Guid.Empty
            ? Task.FromResult(false)
            : db.ClientProfiles.AsNoTracking().AnyAsync(x => x.Id == clientProfileId, cancellationToken);
    }

    public static Task<FinancialDataConnection?> FindConnectionAsync(
        MasterAppDbContext db,
        Guid clientProfileId,
        Guid connectionId,
        bool asNoTracking,
        CancellationToken cancellationToken)
    {
        var query = asNoTracking
            ? db.FinancialDataConnections.AsNoTracking()
            : db.FinancialDataConnections.AsQueryable();

        return query.FirstOrDefaultAsync(
            x => x.Id == connectionId && x.ClientProfileId == clientProfileId,
            cancellationToken);
    }
}
