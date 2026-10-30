using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using Infrastructure.Diagnostics;
using Shared.Diagnostics;
using Xunit;
namespace AgentPortal.Tests;
public sealed class RuntimeDiagnosticsPerformanceTests
{
    private static RuntimeDiagnosticPerformance Valid() => new()
    {
        ElapsedMs = 12000, Outcome = "deadline_exceeded",
        InstrumentedBoundaries = ["request", "stages", "database_connection", "process"],
        Stages = [new() { Stage = "state_read", ElapsedMs = 11990 }],
        Dependencies = [new() { Kind = "database_connection", Count = 1, FailedCount = 1, ElapsedMs = 11980 }],
        Process = new() { ManagedHeapMiB = 1024, WorkingSetMiB = 1500, PendingThreadPoolItems = 3, AvailableWorkerThreads = 20 }
    };
    [Fact]
    public void ServerEvidenceIsTypedBoundedAndHasNoCallerIdentityFields()
    {
        var json = RuntimeDiagnosticSanitizer.SanitizePerformance(Valid(), true);
        Assert.NotNull(json); Assert.True(Encoding.UTF8.GetByteCount(json!) <= 4096);
        using var parsed = JsonDocument.Parse(json!);
        Assert.Equal(12000, parsed.RootElement.GetProperty("elapsedMs").GetInt32());
        Assert.Equal(new[] { "schemaVersion", "elapsedMs", "outcome", "truncated", "instrumentedBoundaries", "stages", "dependencies", "process" },
            parsed.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
    }
    [Fact]
    public void PublicClientCannotClaimVerifiedServerPerformance() =>
        Assert.Null(RuntimeDiagnosticSanitizer.SanitizePerformance(Valid(), false));
    [Theory]
    [InlineData(-1)] [InlineData(3600001)] [InlineData(int.MaxValue)]
    public void InvalidDurationsRejected(int duration) =>
        Assert.Null(RuntimeDiagnosticSanitizer.SanitizePerformance(Valid() with { ElapsedMs = duration }, true));
    [Theory]
    [InlineData("select customer from secret")] [InlineData("https://private/path?ticket=secret")]
    [InlineData("owner@example.test")] [InlineData("state_read\nPRIVATE")]
    public void ArbitraryStageStringsNeverRetained(string stage) =>
        Assert.Null(RuntimeDiagnosticSanitizer.SanitizePerformance(Valid() with { Stages = [new() { Stage = stage }] }, true));
    [Fact]
    public void UnknownOutcomeSchemaAndDependencyRejected()
    {
        Assert.Null(RuntimeDiagnosticSanitizer.SanitizePerformance(Valid() with { Outcome = "PRIVATE" }, true));
        Assert.Null(RuntimeDiagnosticSanitizer.SanitizePerformance(Valid() with { SchemaVersion = 2 }, true));
        Assert.Null(RuntimeDiagnosticSanitizer.SanitizePerformance(Valid() with { Dependencies = [new() { Kind = "sql:private" }] }, true));
    }
    [Fact]
    public void DuplicateExcessiveStagesAndUndeclaredInstrumentationRejected()
    {
        Assert.Null(RuntimeDiagnosticSanitizer.SanitizePerformance(Valid() with { Stages = Enumerable.Repeat(new RuntimeDiagnosticStageTiming { Stage = "state_read" }, 17).ToArray() }, true));
        Assert.Null(RuntimeDiagnosticSanitizer.SanitizePerformance(Valid() with { Stages = [new() { Stage = "state_read" }, new() { Stage = "state_read" }] }, true));
        Assert.Null(RuntimeDiagnosticSanitizer.SanitizePerformance(Valid() with { InstrumentedBoundaries = [] }, true));
        Assert.Null(RuntimeDiagnosticSanitizer.SanitizePerformance(Valid() with { InstrumentedBoundaries = ["stages", "stages"] }, true));
    }
    [Fact]
    public void DependencyCountsAndProcessBoundsRejected()
    {
        Assert.Null(RuntimeDiagnosticSanitizer.SanitizePerformance(Valid() with { Dependencies = [new() { Kind = "database_connection", Count = 1, FailedCount = 2 }] }, true));
        Assert.Null(RuntimeDiagnosticSanitizer.SanitizePerformance(Valid() with { Process = new() { WorkingSetMiB = 131073 } }, true));
    }
    [Fact]
    public void MissingInstrumentationRemainsExplicitNotZeroSuccess()
    {
        var json = RuntimeDiagnosticSanitizer.SanitizePerformance(new() { InstrumentedBoundaries = ["request"], Truncated = true }, true)!;
        using var parsed = JsonDocument.Parse(json);
        Assert.True(parsed.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("process").ValueKind);
        Assert.Equal(0, parsed.RootElement.GetProperty("dependencies").GetArrayLength());
    }
    [Theory]
    [InlineData("{\"schemaVersion\":1,\"owner\":\"private\"}")]
    [InlineData("{\"schemaVersion\":true}")]
    [InlineData("{\"elapsedMs\":NaN}")]
    public void UnknownFieldsAndInvalidTypesFailDeserialization(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RuntimeDiagnosticPerformance>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}
