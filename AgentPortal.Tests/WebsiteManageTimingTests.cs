using System;
using System.Collections.Generic;
using System.Linq;
using Infrastructure.WebsiteEditing.Controllers;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentPortal.Tests;
public sealed class WebsiteManageTimingTests
{
    private sealed class Clock : TimeProvider
    {
        internal long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }
    private sealed class CapturingLogger : ILogger
    {
        internal readonly List<string> Records = [];
        internal bool Throw;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            Assert.Equal(LogLevel.Warning, level);
            if (Throw) throw new InvalidOperationException("logger unavailable");
            Records.Add(formatter(state, exception));
        }
    }
    [Fact]
    public void FastSuccessProducesNoRecord()
    {
        var clock = new Clock(); var log = new CapturingLogger();
        using (var timing = new WebsiteManageTiming(log, clock, new())) { clock.Now = 9999; }
        Assert.Empty(log.Records);
    }
    [Fact]
    public void SlowReadRecordsOnlyBoundedFixedStagesAndNumbersOnce()
    {
        var clock = new Clock(); var log = new CapturingLogger();
        var timing = new WebsiteManageTiming(log, clock, new());
        clock.Now = 12; timing.Next(WebsiteManageTiming.Stage.StateRead);
        clock.Now = 12345; timing.Dispose(); timing.Dispose();
        var record = Assert.Single(log.Records);
        Assert.Contains("Outcome=returned LastStage=StateRead DurationMs=12345 StageMs=12,12333,0,0,0,0,0,0,0,0,0,0", record);
        Assert.True(record.Length < 256);
    }
    [Theory]
    [InlineData(false, "failed")]
    [InlineData(true, "canceled")]
    public void FailurePropagatesOriginalExceptionWithoutLoggingItsContent(bool canceled, string outcome)
    {
        var clock = new Clock(); var log = new CapturingLogger();
        Exception original = canceled ? new OperationCanceledException("PRIVATE-TICKET-CONTENT") : new InvalidOperationException("PRIVATE-TICKET-CONTENT");
        var actual = Record.Exception((Action)(() =>
        {
            using var timing = new WebsiteManageTiming(log, clock, new());
            try { throw original; }
            catch { timing.Failed(canceled); throw; }
        }));
        Assert.Same(original, actual);
        Assert.Contains("Outcome=" + outcome, Assert.Single(log.Records));
        Assert.DoesNotContain("PRIVATE", log.Records[0]);
    }
    [Fact]
    public void ProcessGuardSuppressesFurtherRequestsWithoutChangingTheirResults()
    {
        var clock = new Clock(); var log = new CapturingLogger(); var guard = new WebsiteManageTiming.RateGuard();
        int Run() { using var t = new WebsiteManageTiming(log, clock, guard); t.Failed(false); return 42; }
        Assert.Equal(42, Run());
        for (var i = 0; i < 1000; i++) Assert.Equal(42, Run());
        Assert.Single(log.Records);
        clock.Now = 59999; Assert.Equal(42, Run()); Assert.Single(log.Records);
        clock.Now = 60000; Assert.Equal(42, Run()); Assert.Equal(2, log.Records.Count);
    }
    [Fact]
    public void DisabledOrBrokenLoggerCannotReplaceBusinessFailure()
    {
        var original = new InvalidOperationException("business failure");
        var actual = Record.Exception((Action)(() =>
        {
            using var timing = new WebsiteManageTiming(new CapturingLogger { Throw = true }, new Clock(), new());
            try { throw original; } catch { timing.Failed(false); throw; }
        }));
        Assert.Same(original, actual);
        using var absent = new WebsiteManageTiming(null, new Clock(), new()); absent.Failed(false);
    }
    [Fact]
    public void DurationIsCapped()
    {
        var clock = new Clock(); var log = new CapturingLogger();
        using(var timing = new WebsiteManageTiming(log, clock, new())) clock.Now = long.MaxValue / 2;
        Assert.Contains("DurationMs=3600000", Assert.Single(log.Records));
        Assert.True(log.Records[0].Length < 256);
    }
}
