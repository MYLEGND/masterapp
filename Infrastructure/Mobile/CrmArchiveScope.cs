using Domain.Accounts;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
namespace Infrastructure.Mobile;

public static class CrmArchiveScope
{
    public static bool ArchivedStatus(string? status) => status?.Trim().ToLowerInvariant() is "deleted" or "archived" or "closed";
    public static async Task<HashSet<string>> ClosedClientKeysAsync(MasterAppDbContext db, CancellationToken ct)
    {
        var rows = await db.AccountLifecycleRecords.AsNoTracking().Where(a => a.ParticipantType == MessagingParticipantTypes.Client &&
            (a.State == AccountLifecycleStates.Closed || a.State == AccountLifecycleStates.DeletionRequested))
            .Select(a => new { a.ProfileId, a.UserId }).ToListAsync(ct);
        return rows.SelectMany(a => new[] { a.ProfileId.ToString(), a.UserId }).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
