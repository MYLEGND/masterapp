using System.Diagnostics;

namespace Shared.Diagnostics;

// Code-owned vocabulary. No caller strings, SQL, URLs or identifiers enter a scope.
public enum RuntimeDiagnosticStage
{
    StartupConfiguration, StartupReadiness, Authorization, StateRead, BusinessFacts,
    DraftValidation, Collections, CommerceStore, PublicationHistory, CallToAction,
    PublishPermission, Media, NamedDrafts, ResponsePreparation, ResponseSerialization,
    StudioEdit, StudioSave, PublicationValidate, PublicationCompile, PublicationCommit,
    BackgroundScan, BackgroundRun, SnapshotRetention, ProviderRequest, RuntimeProvenance
}
public enum RuntimeDiagnosticDependency { DatabaseConnection, DatabaseCommand, DatabaseReaderLifetime, HttpClient }

// One bounded request/background observation. Child tasks share only its locked
// aggregate; no per-command collections or caller-controlled cardinality exist.
public sealed class RuntimeDiagnosticScope : IDisposable
{
    private static readonly AsyncLocal<RuntimeDiagnosticScope?> Ambient = new();
    private static readonly AsyncLocal<int> Suppression = new();
    private readonly RuntimeDiagnosticScope? _previous;
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly object _gate = new();
    private readonly Dictionary<RuntimeDiagnosticStage, int> _stages = new();
    private readonly Dictionary<RuntimeDiagnosticDependency, RuntimeDiagnosticDependencyTiming> _dependencies = new();
    private readonly HashSet<string> _boundaries = new(StringComparer.Ordinal);
    private bool _truncated, _disposed;
    private int _activeStages;
    public static RuntimeDiagnosticScope? Current => Suppression.Value == 0 ? Ambient.Value : null;

    public RuntimeDiagnosticScope(TimeProvider? clock = null, bool request = true)
    {
        _clock = clock ?? TimeProvider.System; _started = _clock.GetTimestamp();
        _previous = Ambient.Value; Ambient.Value = this;
        if (request) _boundaries.Add("request");
    }
    public static IDisposable Suppress()
    {
        Suppression.Value++;
        return new Once(() => Suppression.Value--);
    }
    public void MarkDependencyAvailable(RuntimeDiagnosticDependency kind)
    {
        if (!Enum.IsDefined(kind)) return;
        lock (_gate) if (!_disposed) _boundaries.Add(DependencyName(kind));
    }
    public IDisposable Stage(RuntimeDiagnosticStage stage)
    {
        lock (_gate)
        {
            if (_disposed || !Enum.IsDefined(stage)) return new Once(null);
            _boundaries.Add("stages");
            if (_activeStages >= 32) { _truncated = true; return new Once(null); }
            _activeStages++;
        }
        var start = _clock.GetTimestamp();
        return new Once(() =>
        {
            lock (_gate)
            {
                _activeStages--;
                if (_disposed) return;
                if (!_stages.ContainsKey(stage) && _stages.Count >= 16) { _truncated = true; return; }
                _stages[stage] = Sum(_stages.GetValueOrDefault(stage), Milliseconds(_clock.GetElapsedTime(start)));
            }
        });
    }
    public void Dependency(RuntimeDiagnosticDependency kind, TimeSpan elapsed, bool failed)
    {
        if (!Enum.IsDefined(kind) || elapsed < TimeSpan.Zero) return;
        lock (_gate)
        {
            if (_disposed) return;
            var name = DependencyName(kind); _boundaries.Add(name);
            var old = _dependencies.GetValueOrDefault(kind) ?? new() { Kind = name };
            if (old.Count >= 10_000) { _truncated = true; return; }
            _dependencies[kind] = old with { Count = old.Count + 1,
                FailedCount = old.FailedCount + (failed ? 1 : 0), ElapsedMs = Sum(old.ElapsedMs, Milliseconds(elapsed)) };
        }
    }
    public int ElapsedMs => Milliseconds(_clock.GetElapsedTime(_started));

    public RuntimeDiagnosticPerformance Snapshot(string outcome)
    {
        lock (_gate)
        {
            RuntimeDiagnosticProcessPressure? pressure = null;
            try
            {
                using var process = Process.GetCurrentProcess();
                ThreadPool.GetAvailableThreads(out var available, out _);
                pressure = new() { ManagedHeapMiB = (int)Math.Clamp(GC.GetTotalMemory(false) / 1048576, 0, 131072),
                    WorkingSetMiB = (int)Math.Clamp(process.WorkingSet64 / 1048576, 0, 131072),
                    PendingThreadPoolItems = (int)Math.Clamp(ThreadPool.PendingWorkItemCount, 0, 1000000),
                    AvailableWorkerThreads = Math.Clamp(available, 0, 100000) };
                _boundaries.Add("process");
            }
            catch (Exception) { /* Unsupported process capture is absent, not zero. */ }
            return new() { ElapsedMs = Milliseconds(_clock.GetElapsedTime(_started)), Outcome = outcome,
                Truncated = _truncated || _activeStages > 0, InstrumentedBoundaries = _boundaries.Order().ToArray(),
                Stages = _stages.OrderBy(x => x.Key).Select(x => new RuntimeDiagnosticStageTiming { Stage = StageName(x.Key), ElapsedMs = x.Value }).ToArray(),
                Dependencies = _dependencies.OrderBy(x => x.Key).Select(x => x.Value).ToArray(), Process = pressure };
        }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        if (ReferenceEquals(Ambient.Value, this)) Ambient.Value = _previous;
    }
    private static int Milliseconds(TimeSpan elapsed) => (int)Math.Clamp(elapsed.TotalMilliseconds, 0, 3_600_000);
    private int Sum(int first, int next)
    {
        if ((long)first + next > 3_600_000) _truncated = true;
        return (int)Math.Min((long)first + next, 3_600_000);
    }
    public static string StageName(RuntimeDiagnosticStage stage) => stage switch
    {
        RuntimeDiagnosticStage.StartupConfiguration => "startup_configuration", RuntimeDiagnosticStage.StartupReadiness => "startup_readiness",
        RuntimeDiagnosticStage.Authorization => "authorization", RuntimeDiagnosticStage.StateRead => "state_read",
        RuntimeDiagnosticStage.BusinessFacts => "business_facts", RuntimeDiagnosticStage.DraftValidation => "draft_validation",
        RuntimeDiagnosticStage.Collections => "collections", RuntimeDiagnosticStage.CommerceStore => "commerce_store",
        RuntimeDiagnosticStage.PublicationHistory => "publication_history", RuntimeDiagnosticStage.CallToAction => "call_to_action",
        RuntimeDiagnosticStage.PublishPermission => "publish_permission", RuntimeDiagnosticStage.Media => "media",
        RuntimeDiagnosticStage.NamedDrafts => "named_drafts", RuntimeDiagnosticStage.ResponsePreparation => "response_preparation",
        RuntimeDiagnosticStage.ResponseSerialization => "response_serialization", RuntimeDiagnosticStage.StudioEdit => "studio_edit",
        RuntimeDiagnosticStage.StudioSave => "studio_save", RuntimeDiagnosticStage.PublicationValidate => "publication_validate",
        RuntimeDiagnosticStage.PublicationCompile => "publication_compile", RuntimeDiagnosticStage.PublicationCommit => "publication_commit",
        RuntimeDiagnosticStage.BackgroundScan => "background_scan", RuntimeDiagnosticStage.BackgroundRun => "background_run",
        RuntimeDiagnosticStage.SnapshotRetention => "snapshot_retention", RuntimeDiagnosticStage.ProviderRequest => "provider_request",
        RuntimeDiagnosticStage.RuntimeProvenance => "runtime_provenance", _ => ""
    };
    public static string DependencyName(RuntimeDiagnosticDependency kind) => kind switch
    { RuntimeDiagnosticDependency.DatabaseConnection => "database_connection", RuntimeDiagnosticDependency.DatabaseCommand => "database_command", RuntimeDiagnosticDependency.DatabaseReaderLifetime => "database_reader_lifetime", RuntimeDiagnosticDependency.HttpClient => "http_client", _ => "" };
    private sealed class Once(Action? action) : IDisposable
    { private Action? _action = action; public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke(); }
}

// Sequential convenience for existing multistage authorities. All measurement,
// bounding, persistence and admission remain in the one canonical mechanism.
public sealed class RuntimeDiagnosticStageSequence : IDisposable
{
    private IDisposable? _stage;
    public RuntimeDiagnosticStageSequence(RuntimeDiagnosticStage first = RuntimeDiagnosticStage.Authorization) => Next(first);
    public void Next(RuntimeDiagnosticStage stage)
    { _stage?.Dispose(); _stage = RuntimeDiagnosticScope.Current?.Stage(stage); }
    public void Dispose() { _stage?.Dispose(); _stage = null; }
}

public static class RuntimeDiagnosticOperation
{
    // Reusable background boundary, using the same sink/admission as requests.
    // A pipeline failure never replaces the original operation or its exception.
    public static async Task ObserveAsync(RuntimeDiagnosticStage stage, Func<Task> operation,
        IRuntimeDiagnosticSink? sink, CancellationToken cancellationToken = default)
    {
        using var observation = new RuntimeDiagnosticScope(request: false);
        var outcome = "completed";
        try { using var timing = observation.Stage(stage); await operation(); }
        catch (OperationCanceledException) { outcome = cancellationToken.IsCancellationRequested ? "caller_canceled" : "deadline_exceeded"; throw; }
        catch { outcome = "failed"; throw; }
        finally
        {
            if (sink is not null && (observation.ElapsedMs >= 10_000 || outcome != "completed"))
            {
                try
                {
                    var evidence = observation.Snapshot(outcome);
                    using var suppression = RuntimeDiagnosticScope.Suppress();
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await sink.RecordAsync(new RuntimeDiagnosticEvent { Platform = "server",
                        ErrorName = outcome == "completed" ? "SlowOperation" : outcome == "failed" ? "OperationFailure" : "OperationCanceledException",
                        Performance = evidence }, deadline.Token);
                }
                catch { /* Existing business logging remains authoritative. */ }
            }
        }
    }
}
