using System.Collections.Concurrent;

namespace Shared.Calling;

// Process-local routing only. Authorization and call state remain in the database.
public static class LegendCallConnections
{
    private static readonly ConcurrentDictionary<string, string> Connections = new();
    public static void Add(string connectionId, string group) => Connections[connectionId] = group;
    public static void Remove(string connectionId) => Connections.TryRemove(connectionId, out _);
    public static string[] Groups => Connections.Values.Distinct(StringComparer.Ordinal).ToArray();
}
