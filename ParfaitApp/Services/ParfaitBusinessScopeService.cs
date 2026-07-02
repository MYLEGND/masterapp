using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ParfaitApp.Services;

/// <summary>
/// Resolves and guarantees the first-party Parfait commerce scope.
/// This is the only Phase 1 resolver; no existing commerce data is changed here.
/// </summary>
public sealed class ParfaitBusinessScopeService
{
    public const string ParfaitBusinessKey = "parfait";

    private readonly MasterAppDbContext _db;

    public ParfaitBusinessScopeService(MasterAppDbContext db)
    {
        _db = db;
    }

    public async Task<CommerceBusiness> GetParfaitAsync(CancellationToken ct = default)
    {
        var business = await _db.CommerceBusinesses
            .SingleOrDefaultAsync(x => x.Key == ParfaitBusinessKey, ct);

        if (business is not null)
            return business;

        business = new CommerceBusiness
        {
            Key = ParfaitBusinessKey,
            DisplayName = "Parfait",
            LegalName = "MyLegnd LLC",
            BusinessType = "Apparel / Ecommerce",
            PrimaryDomain = "shopparfait.com",
            Status = "Active",
            IsActive = true,
            OwnerEmail = "parfait@mylegnd.com"
        };

        _db.CommerceBusinesses.Add(business);
        await _db.SaveChangesAsync(ct);

        return business;
    }
}
