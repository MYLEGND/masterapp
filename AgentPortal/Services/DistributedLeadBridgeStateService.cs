using System.Text.Json;
using AgentPortal.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace AgentPortal.Services;

/// <summary>
/// Redis-backed implementation of <see cref="ILeadBridgeStateService"/>.
/// Replaces the in-memory <see cref="LeadBridgeStateService"/> when a Redis connection string is present,
/// making multi-instance deployments safe: all instances share the same lead-bridge state.
/// </summary>
public sealed class DistributedLeadBridgeStateService : ILeadBridgeStateService
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<DistributedLeadBridgeStateService> _logger;

    // Sliding expiry matches the in-memory EvictAfter of 4 hours.
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(4)
    };

    // Monotonic sequence for version tokens; safe per-process only.
    // For true cross-process monotonic ordering, the version is a UTC ticks string — good enough
    // for optimistic concurrency; ties are broken by the last writer winning.
    private static long _seq;
    private static string NewVersion() =>
        $"{DateTimeOffset.UtcNow.Ticks}-{Interlocked.Increment(ref _seq)}";

    public DistributedLeadBridgeStateService(
        IDistributedCache cache,
        ILogger<DistributedLeadBridgeStateService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    private static string CacheKey(string agentUserId, string? queueKey)
    {
        var q = (queueKey ?? string.Empty).Trim().ToLowerInvariant();
        return $"lbs:{agentUserId}::{q}";
    }

    private async Task<LeadBridgeActiveState?> GetAsync(string key)
    {
        try
        {
            var bytes = await _cache.GetAsync(key);
            if (bytes is null || bytes.Length == 0) return null;
            return JsonSerializer.Deserialize<LeadBridgeActiveState>(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LeadBridgeState cache read failed for key {Key}; treating as absent", key);
            return null;
        }
    }

    private async Task SetAsync(string key, LeadBridgeActiveState state)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
            await _cache.SetAsync(key, bytes, CacheOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LeadBridgeState cache write failed for key {Key}; state may be stale on restart", key);
        }
    }

    public LeadBridgeActiveState GetOrCreate(
        string agentUserId,
        string? queueKey,
        Func<(string? leadId, int position, int total)> initializer)
    {
        // Sync wrapper — IDistributedCache has no sync API, use GetAwaiter
        return GetOrCreateAsync(agentUserId, queueKey, initializer).GetAwaiter().GetResult();
    }

    private async Task<LeadBridgeActiveState> GetOrCreateAsync(
        string agentUserId,
        string? queueKey,
        Func<(string? leadId, int position, int total)> initializer)
    {
        var key = CacheKey(agentUserId, queueKey);
        var existing = await GetAsync(key);
        if (existing != null) return existing;

        var seed = initializer();
        var state = new LeadBridgeActiveState
        {
            AgentUserId = agentUserId,
            ActiveLeadId = seed.leadId,
            Position = seed.position,
            Total = seed.total,
            QueueKey = queueKey,
            Version = NewVersion(),
            UtcUpdated = DateTime.UtcNow
        };
        await SetAsync(key, state);
        return state;
    }

    public LeadBridgeActiveState Update(
        string agentUserId,
        string? queueKey,
        string? activeLeadId,
        int position,
        int total,
        string? expectedVersion = null,
        string? deletedLeadId = null)
    {
        return UpdateAsync(agentUserId, queueKey, activeLeadId, position, total, expectedVersion).GetAwaiter().GetResult();
    }

    private async Task<LeadBridgeActiveState> UpdateAsync(
        string agentUserId,
        string? queueKey,
        string? activeLeadId,
        int position,
        int total,
        string? expectedVersion)
    {
        var key = CacheKey(agentUserId, queueKey);
        var existing = await GetAsync(key);

        if (existing != null
            && !string.IsNullOrEmpty(expectedVersion)
            && existing.Version != expectedVersion)
        {
            // Optimistic-concurrency mismatch — return stale; caller will re-sync
            return existing;
        }

        var next = new LeadBridgeActiveState
        {
            AgentUserId = agentUserId,
            ActiveLeadId = activeLeadId,
            Position = position,
            Total = total,
            QueueKey = queueKey ?? existing?.QueueKey,
            FilterState = existing?.FilterState,
            Version = NewVersion(),
            UtcUpdated = DateTime.UtcNow
        };
        await SetAsync(key, next);
        return next;
    }

    public LeadBridgeActiveState UpdateFilters(
        string agentUserId,
        string? queueKey,
        string? filterState,
        string? expectedVersion = null)
    {
        return UpdateFiltersAsync(agentUserId, queueKey, filterState, expectedVersion).GetAwaiter().GetResult();
    }

    private async Task<LeadBridgeActiveState> UpdateFiltersAsync(
        string agentUserId,
        string? queueKey,
        string? filterState,
        string? expectedVersion)
    {
        var key = CacheKey(agentUserId, queueKey);
        var existing = await GetAsync(key);

        if (existing != null
            && !string.IsNullOrEmpty(expectedVersion)
            && existing.Version != expectedVersion)
        {
            return existing;
        }

        var next = new LeadBridgeActiveState
        {
            AgentUserId = existing?.AgentUserId ?? agentUserId,
            ActiveLeadId = existing?.ActiveLeadId,
            Position = existing?.Position ?? 0,
            Total = existing?.Total ?? 0,
            QueueKey = queueKey ?? existing?.QueueKey,
            FilterState = filterState,
            Version = NewVersion(),
            UtcUpdated = DateTime.UtcNow
        };
        await SetAsync(key, next);
        return next;
    }
}
