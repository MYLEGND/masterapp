using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AgentPortal.Services.Telemetry;
using Infrastructure.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit;

namespace AgentPortal.Tests;

[Collection("TelemetryPipeline")]
public class RoutineSqlDependencyRoutingTests
{
    private const string Source = "OpenTelemetry.Instrumentation.SqlClient";
    private const string Query = "SELECT [m].[Id] FROM [MobilePushDeliveries] AS [m] INNER JOIN [MobileActivityNotifications] AS [n] ON [m].[NotificationId]=[n].[Id] INNER JOIN [MobilePushDevices] AS [d] ON [m].[MobilePushDeviceId]=[d].[Id]";

    private sealed class Monitor : IOptionsMonitor<RoutineSqlTelemetryOptions>
    {
        public RoutineSqlTelemetryOptions CurrentValue { get; } = new() { SampleRoutineSqlDependencies = true };
        public RoutineSqlTelemetryOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<RoutineSqlTelemetryOptions,string?> listener) => null;
    }
    private sealed class CaptureExporter : BaseExporter<Activity>
    {
        public List<Activity> Items { get; } = new();
        public int Disposals;
        public override ExportResult Export(in Batch<Activity> batch)
        {
            lock (Items) foreach (var item in batch) Items.Add(item);
            return ExportResult.Success;
        }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
    private sealed class MetricObserver : BaseProcessor<Activity>
    {
        public int Count;
        public override void OnEnd(Activity data) => System.Threading.Interlocked.Increment(ref Count);
    }
    private sealed class Fixture : IDisposable
    {
        public Monitor Options { get; } = new();
        public CaptureExporter Original { get; } = new();
        public CaptureExporter Full { get; } = new();
        public CaptureExporter Sampled { get; } = new();
        public MetricObserver Metrics { get; } = new();
        public RoutineSqlDependencyRouting Routing { get; }
        public ActivitySource Activities { get; } = new(Source);
        public TracerProvider Provider { get; }
        public Fixture()
        {
            Routing = new(Options, Full, Sampled, "offline", "offline.invalid");
            Provider = Sdk.CreateTracerProviderBuilder().AddSource(Source).SetSampler(new AlwaysOnSampler())
                .AddProcessor(Routing.FullQueue).AddProcessor(Routing.SampledQueue)
                .AddProcessor(Routing.CaptureProcessor).AddProcessor(Metrics)
                .AddProcessor(new SimpleActivityExportProcessor(Original)).Build();
        }
        public Activity Emit(Action<Activity>? alter = null, string query = Query)
        {
            var a = Activities.StartActivity("poll", ActivityKind.Client)!;
            a.SetTag("db.system.name", "microsoft.sql_server");
            a.SetTag("db.namespace", "offline");
            a.SetTag("server.address", "offline.invalid");
            using var command = new SqlCommand(query);
            RoutineSqlDependencyRouting.ApproveQuery(a, command);
            alter?.Invoke(a);
            a.Stop();
            return a;
        }
        public void Flush() => Assert.True(Provider.ForceFlush(10000));
        public void Dispose() { Provider.Dispose(); Activities.Dispose(); }
    }

    [Fact]
    public async Task Routine_reads_are_exclusively_sampled_and_all_metric_observations_survive()
    {
        using var f = new Fixture();
        var originals = new HashSet<string>();
        for (var i=0; i<500; i++)
            await RoutineSqlPolling.ReadMobilePushOutboxAsync(() => Query, () =>
            {
                originals.Add(f.Emit().Id!);
                return Task.FromResult(1);
            });
        f.Flush();
        Assert.Empty(f.Original.Items);
        Assert.Empty(f.Full.Items);
        Assert.InRange(f.Sampled.Items.Count, 1, 150);
        Assert.Equal(500, f.Metrics.Count);
        Assert.All(f.Sampled.Items, a => Assert.Contains(a.Id!, originals));
        Assert.Equal(f.Sampled.Items.Count, f.Sampled.Items.Select(a => a.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("unscoped")]
    [InlineData("slow")]
    [InlineData("error")]
    [InlineData("event")]
    [InlineData("unknown-query")]
    [InlineData("unknown-db")]
    [InlineData("wrong-server")]
    [InlineData("wrong-database")]
    [InlineData("parent")]
    [InlineData("disabled")]
    public async Task Essential_or_unclassified_activity_keeps_original_export(string kind)
    {
        using var f = new Fixture();
        if (kind=="disabled") f.Options.CurrentValue.SampleRoutineSqlDependencies=false;
        using var parent = kind=="parent" ? new Activity("payment-or-audit").Start() : null;
        Task<int> Read()
        {
            f.Emit(a =>
            {
                if(kind=="slow") a.SetEndTime(a.StartTimeUtc.AddSeconds(2));
                if(kind=="error") a.SetStatus(ActivityStatusCode.Error);
                if(kind=="event") a.AddEvent(new ActivityEvent("audit"));
                if(kind=="unknown-db") a.SetTag("db.system.name", null);
                if(kind=="wrong-server") a.SetTag("server.address", "other.invalid");
                if(kind=="wrong-database") a.SetTag("db.namespace", "other");
            }, kind=="unknown-query" ? "SELECT 1" : Query);
            return Task.FromResult(1);
        }
        if(kind=="unscoped") await Read();
        else await RoutineSqlPolling.ReadMobilePushOutboxAsync(() => Query, Read);
        f.Flush();
        if (kind is "unscoped" or "parent" or "disabled")
        {
            Assert.Single(f.Original.Items);
            Assert.Empty(f.Full.Items);
        }
        else
        {
            Assert.Empty(f.Original.Items);
            Assert.Single(f.Full.Items);
        }
        Assert.Empty(f.Sampled.Items);
        Assert.Equal(1,f.Metrics.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_read_retains_deferred_sql_at_full_weight(bool cancel)
    {
        using var f = new Fixture();
        await Assert.ThrowsAnyAsync<Exception>(() => RoutineSqlPolling.ReadMobilePushOutboxAsync<int>(() => Query, () =>
        {
            f.Emit();
            return Task.FromException<int>(cancel ? new OperationCanceledException() : new InvalidOperationException());
        }));
        f.Flush();
        Assert.Single(f.Full.Items);
        Assert.Empty(f.Sampled.Items);
        Assert.Empty(f.Original.Items);
    }

    [Fact]
    public async Task Retry_or_buffer_overflow_preserves_all_records_without_duplicates()
    {
        using var f = new Fixture();
        await RoutineSqlPolling.ReadMobilePushOutboxAsync(() => Query, () =>
        {
            for(var i=0;i<10;i++) f.Emit();
            return Task.FromResult(1);
        });
        f.Flush();
        Assert.Equal(8,f.Full.Items.Count);
        Assert.Equal(2,f.Original.Items.Count);
        Assert.Empty(f.Sampled.Items);
        Assert.Equal(10,f.Metrics.Count);
        Assert.Equal(10,f.Full.Items.Concat(f.Original.Items).Select(a=>a.Id).Distinct().Count());
    }

    [Fact]
    public async Task Rollback_restores_inflight_reads_and_scope_does_not_escape()
    {
        using var f = new Fixture();
        await RoutineSqlPolling.ReadMobilePushOutboxAsync(() => Query, () =>
        {
            f.Emit();
            f.Options.CurrentValue.SampleRoutineSqlDependencies=false;
            return Task.FromResult(1);
        });
        f.Options.CurrentValue.SampleRoutineSqlDependencies=true;
        f.Emit();
        f.Flush();
        Assert.Single(f.Full.Items);
        Assert.Single(f.Original.Items);
        Assert.Empty(f.Sampled.Items);
    }

    [Fact]
    public async Task Concurrent_scopes_remain_isolated()
    {
        using var f = new Fixture();
        await Task.WhenAll(Enumerable.Range(0,100).Select(_ => Task.Run(() =>
            RoutineSqlPolling.ReadMobilePushOutboxAsync(() => Query, async () =>
            {
                await Task.Yield();
                f.Emit();
                return 1;
            }))));
        f.Flush();
        Assert.Empty(f.Full.Items);
        Assert.Empty(f.Original.Items);
        Assert.Equal(100,f.Metrics.Count);
        Assert.Equal(f.Sampled.Items.Count,f.Sampled.Items.Select(a=>a.Id).Distinct().Count());
    }
    [Theory]
    [InlineData(" UNION SELECT 1")]
    [InlineData(" INNER JOIN [PaymentEvidence] AS [p] ON 1=1")]
    [InlineData("; SELECT 1")]
    public async Task Any_query_change_keeps_original_full_export(string suffix)
    {
        using var f = new Fixture();
        await RoutineSqlPolling.ReadMobilePushOutboxAsync(() => Query, () =>
        {
            f.Emit(query: Query + suffix);
            return Task.FromResult(1);
        });
        f.Flush();
        Assert.Single(f.Full.Items);
        Assert.Empty(f.Original.Items);
        Assert.Empty(f.Sampled.Items);
    }

    [Fact]
    public async Task Failed_query_capture_does_not_affect_polling_or_drop_telemetry()
    {
        using var f = new Fixture();
        var result = await RoutineSqlPolling.ReadMobilePushOutboxAsync(() => throw new InvalidOperationException(), () =>
        {
            f.Emit();
            return Task.FromResult(42);
        });
        f.Flush();
        Assert.Equal(42,result);
        Assert.Single(f.Full.Items);
        Assert.Empty(f.Original.Items);
        Assert.Empty(f.Sampled.Items);
    }

}
