using System;
using System.Linq;
using Shared.Diagnostics;
using Xunit;
namespace AgentPortal.Tests;

// Regression for the former Manage-only warning: the same stage coverage now
// contributes to canonical request evidence rather than a second log pipeline.
public sealed class WebsiteManageTimingTests
{
    private sealed class Clock : TimeProvider
    {
        internal long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }
    [Fact]
    public void SequentialManageStagesPreserveMeasuredIntervals()
    {
        var clock = new Clock(); using var scope = new RuntimeDiagnosticScope(clock);
        using (var sequence = new RuntimeDiagnosticStageSequence())
        {
            clock.Now = 10; sequence.Next(RuntimeDiagnosticStage.StateRead);
            clock.Now = 12010; sequence.Next(RuntimeDiagnosticStage.BusinessFacts);
            clock.Now = 12030;
        }
        var snapshot = scope.Snapshot("completed");
        Assert.Equal(12030, snapshot.ElapsedMs);
        Assert.Equal(10, snapshot.Stages.Single(x => x.Stage == "authorization").ElapsedMs);
        Assert.Equal(12000, snapshot.Stages.Single(x => x.Stage == "state_read").ElapsedMs);
        Assert.Equal(20, snapshot.Stages.Single(x => x.Stage == "business_facts").ElapsedMs);
    }
    [Fact]
    public void DisabledCaptureDoesNotChangeBusinessExecution()
    {
        var result = 0;
        using (var sequence = new RuntimeDiagnosticStageSequence())
        { result++; sequence.Next(RuntimeDiagnosticStage.StateRead); result++; }
        Assert.Equal(2, result); Assert.Null(RuntimeDiagnosticScope.Current);
    }
    [Fact]
    public void ExceptionDisposesLastStageAndPreservesOriginalException()
    {
        var clock = new Clock(); using var scope = new RuntimeDiagnosticScope(clock);
        var original = new InvalidOperationException("private message");
        var actual = Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var sequence = new RuntimeDiagnosticStageSequence(RuntimeDiagnosticStage.StateRead);
            clock.Now = 20; throw original;
        }));
        Assert.Same(original, actual);
        Assert.Equal(20, scope.Snapshot("failed").Stages.Single().ElapsedMs);
    }
    [Fact]
    public void RepeatedStagesAggregateInsteadOfGrowingPayload()
    {
        var clock = new Clock(); using var scope = new RuntimeDiagnosticScope(clock);
        for (var i = 0; i < 100; i++) using (scope.Stage(RuntimeDiagnosticStage.StateRead)) clock.Now++;
        Assert.Equal(100, scope.Snapshot("completed").Stages.Single().ElapsedMs);
    }
    [Fact]
    public void RepeatedDisposeDoesNotDoubleCount()
    {
        var clock = new Clock(); using var scope = new RuntimeDiagnosticScope(clock);
        var stage = scope.Stage(RuntimeDiagnosticStage.StateRead); clock.Now = 10;
        stage.Dispose(); clock.Now = 20; stage.Dispose();
        Assert.Equal(10, scope.Snapshot("completed").Stages.Single().ElapsedMs);
    }
    [Fact]
    public void LongDurationIsBounded()
    {
        var clock = new Clock(); using var scope = new RuntimeDiagnosticScope(clock);
        using (scope.Stage(RuntimeDiagnosticStage.StateRead)) clock.Now = long.MaxValue / 2;
        Assert.Equal(3600000, scope.Snapshot("failed").Stages.Single().ElapsedMs);
    }
    [Fact]
    public void SuppressedCaptureCannotObserveDiagnosticsOwnStages()
    {
        using var scope = new RuntimeDiagnosticScope();
        using (RuntimeDiagnosticScope.Suppress())
        {
            Assert.Null(RuntimeDiagnosticScope.Current);
            using var sequence = new RuntimeDiagnosticStageSequence(RuntimeDiagnosticStage.StateRead);
        }
        Assert.Same(scope, RuntimeDiagnosticScope.Current);
        Assert.Empty(scope.Snapshot("completed").Stages);
    }
}
