using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Models.Analytics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;

namespace AgentPortal.Services.Analytics;

public sealed class MetaAdsConnectionStore : IMetaAdsConnectionStore
{
    private readonly IDistributedCache _cache;
    private readonly IDataProtector _protector;

    public MetaAdsConnectionStore(IDistributedCache cache, IDataProtectionProvider dataProtectionProvider)
    {
        _cache = cache;
        _protector = dataProtectionProvider.CreateProtector("MetaAds.ConnectionStore.v1");
    }

    private static string CacheKey(Guid agentTrackingProfileId) => $"metaads:connection:{agentTrackingProfileId:D}";

    public async Task<MetaAdsConnectionRecord?> GetAsync(Guid agentTrackingProfileId, CancellationToken ct = default)
    {
        var key = CacheKey(agentTrackingProfileId);
        var cipher = await _cache.GetStringAsync(key, ct);
        if (string.IsNullOrWhiteSpace(cipher)) return null;

        try
        {
            var json = _protector.Unprotect(cipher);
            return JsonSerializer.Deserialize<MetaAdsConnectionRecord>(json);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(MetaAdsConnectionRecord record, CancellationToken ct = default)
    {
        record.UpdatedUtc = DateTime.UtcNow;
        if (record.ConnectedUtc == default) record.ConnectedUtc = DateTime.UtcNow;

        var key = CacheKey(record.AgentTrackingProfileId);
        var json = JsonSerializer.Serialize(record);
        var cipher = _protector.Protect(json);

        var ttl = record.AccessTokenExpiresUtc.HasValue
            ? record.AccessTokenExpiresUtc.Value - DateTime.UtcNow
            : TimeSpan.FromDays(60);

        if (ttl < TimeSpan.FromHours(1)) ttl = TimeSpan.FromHours(1);

        await _cache.SetStringAsync(key, cipher, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl
        }, ct);
    }

    public async Task DeleteAsync(Guid agentTrackingProfileId, CancellationToken ct = default)
    {
        await _cache.RemoveAsync(CacheKey(agentTrackingProfileId), ct);
    }
}
