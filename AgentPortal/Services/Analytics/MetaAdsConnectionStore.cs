using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Models.Analytics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;

namespace AgentPortal.Services.Analytics;

/// <summary>Compatibility adapter for agent callers; SQL is the sole active connection store.</summary>
public sealed class MetaAdsConnectionStore : IMetaAdsConnectionStore
{
    private readonly IDistributedCache _legacyCache;
    private readonly IDataProtector _legacyProtector;
    private readonly MarketingConnectionStore _connections;

    public MetaAdsConnectionStore(IDistributedCache cache, IDataProtectionProvider dataProtectionProvider,
        MarketingConnectionStore connections)
    {
        _legacyCache = cache;
        _legacyProtector = dataProtectionProvider.CreateProtector("MetaAds.ConnectionStore.v1");
        _connections = connections;
    }

    private static string CacheKey(Guid id) => $"metaads:connection:{id:D}";

    public async Task<MetaAdsConnectionRecord?> GetAsync(Guid agentTrackingProfileId, CancellationToken ct = default)
    {
        var owner = MarketingOwnerScope.Agent(agentTrackingProfileId);
        if ((await _connections.GetStatusAsync(owner, ct))?.LegacyAdsImportedUtc is null)
        {
            var cipher = await _legacyCache.GetStringAsync(CacheKey(agentTrackingProfileId), ct);
            var legacy = string.IsNullOrWhiteSpace(cipher) ? null :
                JsonSerializer.Deserialize<MetaAdsConnectionRecord>(_legacyProtector.Unprotect(cipher));
            if (legacy is not null && legacy.AgentTrackingProfileId != agentTrackingProfileId)
                throw new InvalidOperationException("Legacy Meta connection owner mismatch.");
            await _connections.ImportAsync(owner, legacy, ct: ct);
            // Removal follows the durable commit. A failed removal cannot revive this cache record.
            await _legacyCache.RemoveAsync(CacheKey(agentTrackingProfileId), ct);
        }
        return await _connections.GetAdsAsync(owner, ct);
    }

    public Task SaveAsync(MetaAdsConnectionRecord record, CancellationToken ct = default) =>
        _connections.SaveAdsAsync(MarketingOwnerScope.Agent(record.AgentTrackingProfileId), record, ct);

    public async Task DeleteAsync(Guid agentTrackingProfileId, CancellationToken ct = default)
    {
        await _connections.DisconnectAsync(MarketingOwnerScope.Agent(agentTrackingProfileId), ct);
        await _legacyCache.RemoveAsync(CacheKey(agentTrackingProfileId), ct);
    }
}
