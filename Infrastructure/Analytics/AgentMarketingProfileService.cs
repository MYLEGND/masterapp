using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;
using Shared.Meta;

namespace Infrastructure.Analytics;

/// <summary>Moves legacy agent marketing fields into the common connection authority exactly once.</summary>
public sealed class AgentMarketingProfileService(MasterAppDbContext db, MarketingConnectionStore connections,
    IDataProtectionProvider legacyProvider)
{
    public async Task SavePixelAsync(AgentTrackingProfile tracking, string? pixelId, Guid revision, CancellationToken ct = default)
    {
        var row = await GetAsync(tracking, ct);
        await connections.SaveSettingsAsync(MarketingOwnerScope.Agent(tracking.Id), pixelId, row.TestEventCode, null, revision, ct);
    }

    public async Task<MarketingConnection> GetAsync(AgentTrackingProfile tracking, CancellationToken ct = default)
    {
        var owner = MarketingOwnerScope.Agent(tracking.Id);
        var row = await connections.GetStatusAsync(owner, ct);
        if (row?.LegacyProfileImportedUtc is not null || row?.DisconnectedUtc is not null) return row!;
        var upn = tracking.AgentUpn?.Trim().ToUpperInvariant();
        var profiles = await db.AgentProfiles.AsNoTracking().Where(p =>
            (!string.IsNullOrEmpty(tracking.AgentUserId) && p.AgentUserId == tracking.AgentUserId) ||
            (!string.IsNullOrEmpty(upn) && (p.NormalizedEmail == upn || p.AgentUpn == tracking.AgentUpn))).ToListAsync(ct);
        var profile = profiles.OrderByDescending(p => !string.IsNullOrWhiteSpace(p.MetaPixelId))
            .ThenByDescending(p => !string.IsNullOrWhiteSpace(p.MetaCapiAccessToken))
            .ThenByDescending(p => p.AgentUserId == tracking.AgentUserId).ThenByDescending(p => p.UpdatedUtc).FirstOrDefault();
        var token = string.IsNullOrWhiteSpace(profile?.MetaCapiAccessToken) ? null :
            legacyProvider.CreateProtector(MetaCapiCredentialProtection.Purpose).Unprotect(profile.MetaCapiAccessToken);
        await connections.ImportProfileAsync(owner, profile?.MetaPixelId, token, profile?.MetaTestEventCode, ct);
        return (await connections.GetStatusAsync(owner, ct))!;
    }
}
