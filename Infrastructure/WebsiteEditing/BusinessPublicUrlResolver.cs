using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.WebsiteEditing;

public interface IBusinessPublicUrlResolver
{
    Task<string> ResolveAsync(Guid businessId, CancellationToken ct = default);
}

public sealed class BusinessPublicUrlResolver(MasterAppDbContext db) : IBusinessPublicUrlResolver
{
    public async Task<string> ResolveAsync(Guid businessId, CancellationToken ct = default)
    {
        if (businessId == Guid.Empty)
            throw new ArgumentException("A business owner is required.", nameof(businessId));

        var active = await db.CommerceBusinesses.AsNoTracking()
            .AnyAsync(x => x.Id == businessId && x.IsActive && x.Status == "Active", ct);
        if (!active)
            throw new InvalidOperationException("The business is not active.");

        var cutoff = DateTime.UtcNow.AddHours(-24);
        var hostname = await db.Set<WebsiteDomainBinding>().AsNoTracking()
            .Where(x => x.CommerceBusinessId == businessId &&
                        x.Status == "active" &&
                        x.CertificateStatus == "active" &&
                        x.LastCheckedUtc >= cutoff)
            .OrderBy(x => x.CreatedUtc)
            .Select(x => x.Hostname)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(hostname))
            throw new InvalidOperationException("A verified active custom domain is required for this business advertising destination.");

        return "https://" + hostname.Trim().TrimEnd('.');
    }
}
