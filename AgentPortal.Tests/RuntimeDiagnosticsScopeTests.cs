using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Diagnostics;
using Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Shared.Diagnostics;
using Xunit;
namespace AgentPortal.Tests;
public sealed class RuntimeDiagnosticsScopeTests
{
    [Fact]
    public async Task ConcurrentChildrenAggregateWithinFixedBoundsAndSuppressIsExecutionLocal()
    {
        using var scope = new RuntimeDiagnosticScope();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 100).Select(async _ =>
        {
            using var stage = scope.Stage(RuntimeDiagnosticStage.StateRead);
            await release.Task;
            RuntimeDiagnosticScope.Current!.Dependency(RuntimeDiagnosticDependency.DatabaseCommand, TimeSpan.FromMilliseconds(2), false);
            using (RuntimeDiagnosticScope.Suppress()) { await Task.Yield(); Assert.Null(RuntimeDiagnosticScope.Current); }
        }).ToArray();
        release.SetResult();
        await Task.WhenAll(tasks);
        var snapshot = scope.Snapshot("completed");
        Assert.True(snapshot.Truncated);
        Assert.Single(snapshot.Stages);
        Assert.Equal(100, Assert.Single(snapshot.Dependencies).Count);
        Assert.Same(scope, RuntimeDiagnosticScope.Current);
        Assert.NotNull(RuntimeDiagnosticSanitizer.SanitizePerformance(snapshot, true));
    }
    [Fact]
    public void ExcessDistinctStagesAndCommandsAreExplicitlyTruncated()
    {
        using var scope = new RuntimeDiagnosticScope();
        foreach (var stage in Enum.GetValues<RuntimeDiagnosticStage>()) using (scope.Stage(stage)) { }
        for (var i = 0; i < 10001; i++) scope.Dependency(RuntimeDiagnosticDependency.DatabaseCommand, TimeSpan.FromMilliseconds(1), true);
        var sample = scope.Snapshot("failed");
        Assert.True(sample.Truncated); Assert.Equal(16, sample.Stages.Count);
        Assert.Equal(10000, sample.Dependencies.Single().Count);
        Assert.NotNull(RuntimeDiagnosticSanitizer.SanitizePerformance(sample, true));
    }
    [Fact]
    public void ScopeLifetimeAndNestedSuppressionDoNotLeakToNextOperation()
    {
        Assert.Null(RuntimeDiagnosticScope.Current);
        using (var outer = new RuntimeDiagnosticScope())
        {
            using (var inner = new RuntimeDiagnosticScope()) Assert.Same(inner, RuntimeDiagnosticScope.Current);
            Assert.Same(outer, RuntimeDiagnosticScope.Current);
            using (RuntimeDiagnosticScope.Suppress()) { using (RuntimeDiagnosticScope.Suppress()) Assert.Null(RuntimeDiagnosticScope.Current); Assert.Null(RuntimeDiagnosticScope.Current); }
            Assert.Same(outer, RuntimeDiagnosticScope.Current);
        }
        Assert.Null(RuntimeDiagnosticScope.Current);
    }
    [Fact]
    public void BrowserDeserializationCannotAssertServerPerformance()
    {
        var input = JsonSerializer.Deserialize<RuntimeDiagnosticEvent>("{\"performance\":{\"elapsedMs\":123,\"outcome\":\"failed\"},\"appIdentifier\":\"spoofed\"}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(input.Performance);
        var row = RuntimeDiagnosticSanitizer.Sanitize(input with { Performance = new() }, false, new Host(), null, [], DateTime.UtcNow, new string('a',40));
        Assert.Null(row.PerformanceEvidenceJson); Assert.Equal("ProtectWebsite", row.AppIdentifier);
    }
    [Fact]
    public void ForgedTraceIsObservedOnlyAndCannotSetApplicationRevisionOrIncidentIdentity()
    {
        var parent = "00-11111111111111111111111111111111-2222222222222222-01";
        using var activity = new Activity("fixture").SetParentId(parent).Start();
        var value = new RuntimeDiagnosticEvent { AppIdentifier = "evil", GitCommitHash = new string('f',40), CorrelationId = "customer", Performance = new() { InstrumentedBoundaries = ["request"], ElapsedMs = 10 } };
        var now = DateTime.UtcNow;
        var first = RuntimeDiagnosticSanitizer.Sanitize(value, true, new Host(), new DefaultHttpContext(), [], now, new string('a',40));
        Assert.Equal("ProtectWebsite", first.AppIdentifier); Assert.Equal(new string('a',40), first.GitCommitHash);
        Assert.Equal("11111111111111111111111111111111", first.CorrelationId);
        activity.Stop();
        using var unrelated = new Activity("other").SetParentId("00-33333333333333333333333333333333-4444444444444444-01").Start();
        var next = RuntimeDiagnosticSanitizer.Sanitize(value with { Performance = value.Performance with { ElapsedMs = 900 } }, true, new Host(), null, [], now.AddSeconds(1), new string('a',40));
        Assert.Equal(first.DeduplicationKey, next.DeduplicationKey);
        Assert.NotEqual(first.PerformanceEvidenceJson, next.PerformanceEvidenceJson);
        Assert.NotEqual(first.DeduplicationKey, RuntimeDiagnosticSanitizer.Sanitize(value, true, new Host { ApplicationName = "AgentPortal" }, null, [], now, new string('a',40)).DeduplicationKey);
        Assert.NotEqual(first.DeduplicationKey, RuntimeDiagnosticSanitizer.Sanitize(value, true, new Host(), null, [], now, new string('b',40)).DeduplicationKey);
    }
    [Fact]
    public async Task BackgroundCaptureFailurePreservesOriginalException()
    {
        var original = new InvalidOperationException("private");
        var sink = new FailedSink();
        Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeDiagnosticOperation.ObserveAsync(RuntimeDiagnosticStage.BackgroundRun, () => Task.FromException(original), sink)));
        Assert.True(sink.Suppressed); Assert.Equal(1, sink.Attempts);
        Assert.Null(RuntimeDiagnosticScope.Current);
    }
    [Fact]
    public async Task HandledServerErrorCapturesFailedOutcomeWithoutChangingResponse()
    {
        var sink = new RecordingSink();
        using var services = new ServiceCollection().AddLogging().AddSingleton<IHostEnvironment>(new Host())
            .AddSingleton<IRuntimeDiagnosticSink>(sink).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        var app = new ApplicationBuilder(services); app.UseLegendFailureDiagnostics();
        app.Run(async current => { current.Response.StatusCode = 503; await current.Response.WriteAsync("original response"); });
        await app.Build()(context);
        Assert.Equal(503, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        Assert.Equal("original response", await new StreamReader(context.Response.Body).ReadToEndAsync());
        Assert.Equal(1, sink.Count); var captured = sink.Captured!;
        Assert.Equal("HttpFailure", captured.ErrorName); Assert.Equal("failed", captured.Performance!.Outcome);
        Assert.Equal(503, captured.StatusCode);
    }
    private sealed class RecordingSink : IRuntimeDiagnosticSink
    {
        internal int Count; internal RuntimeDiagnosticEvent? Captured;
        public Task RecordAsync(RuntimeDiagnosticEvent value, CancellationToken cancellationToken = default)
        { Count++; Captured = value; return Task.CompletedTask; }
    }
    private sealed class FailedSink : IRuntimeDiagnosticSink
    {
        internal bool Suppressed; internal int Attempts;
        public Task RecordAsync(RuntimeDiagnosticEvent value, CancellationToken cancellationToken = default)
        { Attempts++; Suppressed = RuntimeDiagnosticScope.Current is null; throw new Exception("sink unavailable"); }
    }
    private sealed class Host : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "ProtectWebsite";
        public string ContentRootPath { get; set; } = "/fixture";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
