using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
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
        string? token = null;
        // A canonical OAuth Meta Ads connection supersedes legacy profile CAPI
        // storage. Never decrypt or re-import an obsolete legacy secret when a
        // durable OAuth credential already owns delivery.
        if (row is null || string.IsNullOrWhiteSpace(row.AdsAccessTokenCiphertext))
        {
            if (!string.IsNullOrWhiteSpace(profile?.MetaCapiAccessToken))
            {
                try
                {
                    token = legacyProvider.CreateProtector(MetaCapiCredentialProtection.Purpose)
                        .Unprotect(profile.MetaCapiAccessToken);
                }
                catch (CryptographicException)
                {
                    // Legacy protected values may be unreadable after key rotation.
                    // Pixel/test-code migration must still complete; CAPI is never
                    // reconstructed from an unreadable legacy secret.
                    token = null;
                }
            }
        }
        await connections.ImportProfileAsync(owner, profile?.MetaPixelId, token, profile?.MetaTestEventCode, ct);
        return (await connections.GetStatusAsync(owner, ct))!;
    }
}
