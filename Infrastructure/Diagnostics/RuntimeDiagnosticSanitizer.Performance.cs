using System.Text;
using System.Text.Json;
using Shared.Diagnostics;

namespace Infrastructure.Diagnostics;

internal static partial class RuntimeDiagnosticSanitizer
{
    internal const int MaximumPerformanceBytes = 4096;
    internal const int MaximumPerformanceStages = 16;
    private static readonly HashSet<string> PerformanceOutcomes = new(StringComparer.Ordinal)
        { "completed", "failed", "deadline_exceeded", "caller_canceled" };
    private static readonly HashSet<string> PerformanceBoundaries = new(StringComparer.Ordinal)
        { "request", "stages", "database_connection", "database_command", "database_reader_lifetime", "http_client", "process" };
    private static readonly HashSet<string> PerformanceStages = new(StringComparer.Ordinal)
    {
        "startup_configuration", "startup_readiness", "authorization", "state_read", "business_facts",
        "draft_validation", "collections", "commerce_store", "publication_history", "call_to_action",
        "publish_permission", "media", "named_drafts", "response_preparation", "response_serialization",
        "studio_edit", "studio_save", "publication_validate", "publication_compile", "publication_commit",
        "background_scan", "background_run", "snapshot_retention", "provider_request", "runtime_provenance"
    };
    private static readonly HashSet<string> PerformanceDependencies = new(StringComparer.Ordinal)
        { "database_connection", "database_command", "database_reader_lifetime", "http_client" };
    private static bool Duration(int value) => value is >= 0 and <= 3_600_000;

    // The canonical store retains this server-only gate; public payloads cannot
    // confer verified timing, stage, app/revision or correlation authority.
    internal static string? SanitizePerformance(RuntimeDiagnosticPerformance? value, bool server)
    {
        if (!server || value is null || value.SchemaVersion != 1 || !Duration(value.ElapsedMs) ||
            !PerformanceOutcomes.Contains(value.Outcome) || value.InstrumentedBoundaries is null ||
            value.InstrumentedBoundaries.Count > PerformanceBoundaries.Count ||
            value.InstrumentedBoundaries.Any(x => !PerformanceBoundaries.Contains(x)) ||
            value.InstrumentedBoundaries.Distinct(StringComparer.Ordinal).Count() != value.InstrumentedBoundaries.Count ||
            value.Stages is null || value.Stages.Count > MaximumPerformanceStages ||
            value.Stages.Any(x => x is null || !PerformanceStages.Contains(x.Stage) || !Duration(x.ElapsedMs)) ||
            value.Stages.Select(x => x.Stage).Distinct(StringComparer.Ordinal).Count() != value.Stages.Count ||
            value.Dependencies is null || value.Dependencies.Count > PerformanceDependencies.Count ||
            value.Dependencies.Any(x => x is null || !PerformanceDependencies.Contains(x.Kind) ||
                x.Count is < 0 or > 10_000 || x.FailedCount < 0 || x.FailedCount > x.Count || !Duration(x.ElapsedMs)) ||
            value.Dependencies.Select(x => x.Kind).Distinct(StringComparer.Ordinal).Count() != value.Dependencies.Count)
            return null;
        if (value.Process is { } p && (p.ManagedHeapMiB is < 0 or > 131_072 || p.WorkingSetMiB is < 0 or > 131_072 ||
            p.PendingThreadPoolItems is < 0 or > 1_000_000 || p.AvailableWorkerThreads is < 0 or > 100_000)) return null;
        if (value.Stages.Count > 0 && !value.InstrumentedBoundaries.Contains("stages") ||
            value.Dependencies.Any(d => !value.InstrumentedBoundaries.Contains(d.Kind)) ||
            value.Process is not null && !value.InstrumentedBoundaries.Contains("process")) return null;
        // Serialize only the declared DTO, never caller JSON/extension data. All
        // strings above belong to closed code-owned vocabularies.
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Encoding.UTF8.GetByteCount(json) <= MaximumPerformanceBytes ? json : null;
    }
}
