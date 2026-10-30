using System.Text;
using System.Text.Json;
using Shared.Diagnostics;
namespace AgentPortal.Models;

// Bounded presentation of already-sanitized evidence, never new capture or
// authority. Historical/unknown/malformed data is unavailable, not healthy.
public sealed record RuntimeDiagnosticPerformanceView(DateTime SampledUtc, RuntimeDiagnosticPerformance Evidence,
    RuntimeDiagnosticStageTiming? LargestStage, RuntimeDiagnosticDependencyTiming? LargestDependency,
    string[] MissingBoundaries)
{
    private static readonly string[] Boundaries = ["request", "stages", "process", .. Enum.GetValues<RuntimeDiagnosticDependency>().Select(RuntimeDiagnosticScope.DependencyName)];
    public static RuntimeDiagnosticPerformanceView? Read(string? json)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > 4096) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("sampledUtc", out var timestamp) || timestamp.ValueKind != JsonValueKind.String ||
                !timestamp.TryGetDateTime(out var sampledUtc) || !document.RootElement.TryGetProperty("performance", out var data)) return null;
            var value = data.Deserialize<RuntimeDiagnosticPerformance>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (value is null || value.SchemaVersion != 1 || value.ElapsedMs is < 0 or > 3600000 ||
                value.Outcome is not ("completed" or "failed" or "caller_canceled" or "deadline_exceeded") ||
                value.Stages is null || value.Stages.Count > 16 || value.Dependencies is null || value.Dependencies.Count > 4 ||
                value.InstrumentedBoundaries is null || value.InstrumentedBoundaries.Count > Boundaries.Length ||
                value.InstrumentedBoundaries.Any(x => !Boundaries.Contains(x)) ||
                value.Stages.Any(x => x is null || x.ElapsedMs is < 0 or > 3600000 ||
                    !Enum.GetValues<RuntimeDiagnosticStage>().Select(RuntimeDiagnosticScope.StageName).Contains(x.Stage)) ||
                value.Dependencies.Any(x => x is null || x.ElapsedMs is < 0 or > 3600000 || x.Count is < 0 or > 10000 ||
                    x.FailedCount < 0 || x.FailedCount > x.Count || !Enum.GetValues<RuntimeDiagnosticDependency>().Select(RuntimeDiagnosticScope.DependencyName).Contains(x.Kind))) return null;
            return new(sampledUtc, value, value.Stages.OrderByDescending(x => x.ElapsedMs).FirstOrDefault(),
                value.Dependencies.OrderByDescending(x => x.ElapsedMs).FirstOrDefault(),
                Boundaries.Except(value.InstrumentedBoundaries).ToArray());
        }
        catch (JsonException) { return null; }
    }
}
