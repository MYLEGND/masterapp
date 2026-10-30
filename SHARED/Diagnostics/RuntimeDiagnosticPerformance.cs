using System.Text.Json.Serialization;

namespace Shared.Diagnostics;

// Server-only bounded evidence. The sink derives application/revision from the
// host. Distributed trace is server-observed, not proof of trusted origin.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeDiagnosticPerformance
{
    public int SchemaVersion { get; init; } = 1;
    public int ElapsedMs { get; init; }
    public string Outcome { get; init; } = "completed";
    public bool Truncated { get; init; }
    public IReadOnlyList<string> InstrumentedBoundaries { get; init; } = [];
    public IReadOnlyList<RuntimeDiagnosticStageTiming> Stages { get; init; } = [];
    public IReadOnlyList<RuntimeDiagnosticDependencyTiming> Dependencies { get; init; } = [];
    public RuntimeDiagnosticProcessPressure? Process { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeDiagnosticStageTiming
{
    public string Stage { get; init; } = "";
    public int ElapsedMs { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeDiagnosticDependencyTiming
{
    public string Kind { get; init; } = "";
    public int Count { get; init; }
    public int FailedCount { get; init; }
    public int ElapsedMs { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeDiagnosticProcessPressure
{
    public int ManagedHeapMiB { get; init; }
    public int WorkingSetMiB { get; init; }
    public int PendingThreadPoolItems { get; init; }
    public int AvailableWorkerThreads { get; init; }
}
