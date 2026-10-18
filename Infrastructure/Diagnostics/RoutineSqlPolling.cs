using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace Infrastructure.Diagnostics;

/// <summary>Marks explicitly scoped read-only outbox discovery queries.</summary>
public static class RoutineSqlPolling
{
    private const string Key = "Legend.RoutineSqlPolling.Scope";
    private static readonly AsyncLocal<PollScope?> Current = new();

    public static Task<T> ReadMobilePushOutboxAsync<T>(Func<string> captureQuery, Func<Task<T>> read)
        => ReadAsync(captureQuery, read, _ => true);

    // Nonempty discoveries carry real call activity and retain full telemetry.
    public static Task<T[]> ReadEmptyCallSignalOutboxAsync<T>(Func<string> captureQuery, Func<Task<T[]>> read)
        => ReadAsync(captureQuery, read, result => result is { Length: 0 });

    private static async Task<T> ReadAsync<T>(Func<string> captureQuery, Func<Task<T>> read,
        Func<T, bool> eligibleResult)
    {
        var previous = Current.Value;
        string? expectedQuery;
        try { expectedQuery = captureQuery(); }
        catch { expectedQuery = null; } // Capture failure must not affect the actual read.
        var scope = new PollScope(expectedQuery);
        Current.Value = scope;
        var succeeded = false;
        try
        {
            var result = await read().ConfigureAwait(false);
            try { succeeded = eligibleResult(result); }
            catch { succeeded = false; } // Classification must not affect the actual read.
            return result;
        }
        finally
        {
            Current.Value = previous;
            scope.Complete(succeeded);
        }
    }

    public static void Capture(Activity activity)
    {
        var scope = Current.Value;
        if (scope is null) return;
        lock (scope)
        {
            if (scope.Closed) return;
            scope.Attempts++;
            activity.SetCustomProperty(Key, scope);
        }
    }

    public static bool IsExpectedCommand(Activity activity, string commandText)
    {
        if (activity.GetCustomProperty(Key) is not PollScope scope ||
            commandText.Length is <= 0 or > 16000 ||
            !commandText.StartsWith("SELECT ", StringComparison.Ordinal)) return false;
        return string.Equals(scope.ExpectedQuery, commandText, StringComparison.Ordinal);
    }

    public static bool Defer(Activity activity, Action<Activity, bool> export)
    {
        if (activity.GetCustomProperty(Key) is not PollScope scope) return false;
        lock (scope)
        {
            // Unknown concurrency or overflow stays in the original full-fidelity path.
            if (scope.Closed || scope.Pending.Count >= 8) return false;
            scope.Pending.Add((activity, export));
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            return true;
        }
    }

    private sealed class PollScope(string? expectedQuery)
    {
        public readonly string? ExpectedQuery = expectedQuery;
        public int Attempts;
        public bool Closed;
        public readonly List<(Activity Activity, Action<Activity, bool> Export)> Pending = new();

        public void Complete(bool succeeded)
        {
            (Activity Activity, Action<Activity, bool> Export)[] pending;
            bool sample;
            lock (this)
            {
                Closed = true;
                // Retries/multiple commands have ambiguous individual outcomes: keep every record.
                sample = succeeded && Attempts == 1;
                pending = Pending.ToArray();
                Pending.Clear();
            }
            // The awaited read has returned; the SDK's OnEnd metric enrichment is complete.
            foreach (var item in pending)
            {
                try { item.Export(item.Activity, sample); }
                catch { /* Diagnostics must not change the completed application result. */ }
            }
        }
    }
}
