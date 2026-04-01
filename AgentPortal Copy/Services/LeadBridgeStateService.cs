using System.Collections.Concurrent;
using AgentPortal.Models;

namespace AgentPortal.Services;

/// <summary>
/// In-memory per-agent active lead state with optimistic concurrency via version token.
/// </summary>
public class LeadBridgeStateService : ILeadBridgeStateService
{
    private readonly ConcurrentDictionary<string, LeadBridgeActiveState> _states = new();

    // Monotonic counter — avoids Ticks collisions when two updates happen within 100 ns
    private static long _versionSeq;
    private static string NewVersion() => Interlocked.Increment(ref _versionSeq).ToString();

    // Evict entries that haven't been touched in 4 hours to prevent unbounded growth
    private static readonly TimeSpan EvictAfter = TimeSpan.FromHours(4);
    private readonly System.Timers.Timer _evictTimer;

    public LeadBridgeStateService()
    {
        _evictTimer = new System.Timers.Timer(TimeSpan.FromMinutes(30).TotalMilliseconds) { AutoReset = true };
        _evictTimer.Elapsed += (_, _) => EvictStale();
        _evictTimer.Start();
    }

    private void EvictStale()
    {
        var cutoff = DateTime.UtcNow - EvictAfter;
        foreach (var key in _states.Keys)
        {
            if (_states.TryGetValue(key, out var s) && s.UtcUpdated < cutoff)
                _states.TryRemove(key, out _);
        }
    }

    private static string BuildStateKey(string agentUserId, string? queueKey)
    {
        var queue = (queueKey ?? string.Empty).Trim().ToLowerInvariant();
        return $"{agentUserId}::{queue}";
    }

    public LeadBridgeActiveState GetOrCreate(string agentUserId, string? queueKey, Func<(string? leadId, int position, int total)> initializer)
    {
        var stateKey = BuildStateKey(agentUserId, queueKey);
        return _states.GetOrAdd(stateKey, _ =>
        {
            var seed = initializer();
            return new LeadBridgeActiveState
            {
                AgentUserId = agentUserId,
                ActiveLeadId = seed.leadId,
                Position = seed.position,
                Total = seed.total,
                QueueKey = queueKey,
                Version = NewVersion(),
                UtcUpdated = DateTime.UtcNow
            };
        });
    }

    public LeadBridgeActiveState Update(string agentUserId, string? queueKey, string? activeLeadId, int position, int total, string? expectedVersion = null, string? deletedLeadId = null)
    {
        var stateKey = BuildStateKey(agentUserId, queueKey);
        return _states.AddOrUpdate(stateKey,
            _ => new LeadBridgeActiveState
            {
                AgentUserId = agentUserId,
                ActiveLeadId = activeLeadId,
                Position = position,
                Total = total,
                QueueKey = queueKey,
                Version = NewVersion(),
                UtcUpdated = DateTime.UtcNow
            },
            (_, existing) =>
            {
                if (!string.IsNullOrEmpty(expectedVersion) && existing.Version != expectedVersion)
                    return existing; // Optimistic-concurrency mismatch — return stale state; caller will re-sync

                // Return a NEW instance instead of mutating the shared object.
                // ConcurrentDictionary's update factory may be retried; mutating
                // the existing reference causes data races visible to concurrent readers.
                return new LeadBridgeActiveState
                {
                    AgentUserId = agentUserId,
                    ActiveLeadId = activeLeadId,
                    Position = position,
                    Total = total,
                    QueueKey = queueKey ?? existing.QueueKey,
                    FilterState = existing.FilterState,
                    Version = NewVersion(),
                    UtcUpdated = DateTime.UtcNow
                };
            });
    }

    public LeadBridgeActiveState UpdateFilters(string agentUserId, string? queueKey, string? filterState, string? expectedVersion = null)
    {
        var stateKey = BuildStateKey(agentUserId, queueKey);
        return _states.AddOrUpdate(stateKey,
            _ => new LeadBridgeActiveState
            {
                AgentUserId = agentUserId,
                QueueKey = queueKey,
                FilterState = filterState,
                Version = NewVersion(),
                UtcUpdated = DateTime.UtcNow
            },
            (_, existing) =>
            {
                if (!string.IsNullOrEmpty(expectedVersion) && existing.Version != expectedVersion)
                    return existing;

                return new LeadBridgeActiveState
                {
                    AgentUserId = existing.AgentUserId,
                    ActiveLeadId = existing.ActiveLeadId,
                    Position = existing.Position,
                    Total = existing.Total,
                    QueueKey = queueKey ?? existing.QueueKey,
                    FilterState = filterState,
                    Version = NewVersion(),
                    UtcUpdated = DateTime.UtcNow
                };
            });
    }
}
