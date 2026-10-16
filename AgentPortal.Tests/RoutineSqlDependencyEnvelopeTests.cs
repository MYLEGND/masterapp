using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services.Telemetry;
using Azure.Core.Pipeline;
using Azure.Monitor.OpenTelemetry.Exporter;
using Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace AgentPortal.Tests;

[CollectionDefinition("TelemetryPipeline", DisableParallelization = true)]
public class TelemetryPipelineCollection { }

[Collection("TelemetryPipeline")]
public class RoutineSqlDependencyEnvelopeTests
{
    private sealed class Sink : HttpMessageHandler
    {
        public ConcurrentQueue<string> Payloads { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            if (request.Content.Headers.ContentEncoding.Contains("gzip"))
            {
                using var input = new MemoryStream(bytes);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                await gzip.CopyToAsync(output, cancellationToken);
                bytes = output.ToArray();
            }
            Payloads.Enqueue(Encoding.UTF8.GetString(bytes));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Installed_pipeline_exports_weighted_exclusive_correlated_dependencies(bool zeroHeadRatio)
    {
        using var sink = new Sink();
        using var client = new HttpClient(sink);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["TelemetryCostControl:SampleRoutineSqlDependencies"] = "true";
        builder.Services.AddApplicationInsightsTelemetry(o =>
        {
            o.ConnectionString = $"InstrumentationKey={Guid.NewGuid()};IngestionEndpoint=https://offline.invalid/";
            if (zeroHeadRatio) { o.SamplingRatio = 0f; o.TracesPerSecond = null; }
            o.EnableQuickPulseMetricStream = false;
            o.EnablePerformanceCounterCollectionModule = false;
        });
        builder.Services.Configure<AzureMonitorExporterOptions>(o =>
        {
            o.Transport = new HttpClientTransport(client);
            o.DisableOfflineStorage = true;
            o.EnableLiveMetrics = false;
            o.Retry.MaxRetries = 0;
        });
        builder.Services.AddRoutineSqlLogReduction(builder.Configuration);
        RoutineSqlDependencyRouting.Register(builder.Services, "Server=offline.invalid;Database=offline;Integrated Security=true");
        RoutineSqlDependencyRouting.Register(builder.Services, "Server=offline.invalid;Database=offline;Integrated Security=true");
        Assert.Single(builder.Services.Where(x => x.ServiceType == typeof(RoutineSqlDependencyRouting)));
        builder.Services.ConfigureOpenTelemetryTracerProvider((sp, tracing) =>
            tracing.ConfigureResource(r => r.AddService("sampling-fixture")));
        await using var app = builder.Build();
        Assert.False(app.Services.GetRequiredService<IOptionsFactory<AzureMonitorExporterOptions>>()
            .Create(Microsoft.Extensions.Options.Options.DefaultName).EnablePerformanceCounters);
        await app.StartAsync();
        var provider = app.Services.GetRequiredService<TracerProvider>();
        using var source = new ActivitySource("OpenTelemetry.Instrumentation.SqlClient");
        var expected = new System.Collections.Generic.Dictionary<string,string>();
        for (var i = 0; i < 500; i++)
            await RoutineSqlPolling.ReadMobilePushOutboxAsync(() => "SELECT [m].[Id] FROM [MobilePushDeliveries] AS [m] INNER JOIN [MobileActivityNotifications] AS [n] ON [m].[NotificationId]=[n].[Id] INNER JOIN [MobilePushDevices] AS [d] ON [m].[MobilePushDeviceId]=[d].[Id]", () =>
            {
                using var a = source.StartActivity("routine", ActivityKind.Client)!;
                Assert.NotNull(a);
                a.SetTag("db.system.name", "microsoft.sql_server");
                a.SetTag("db.namespace", "offline");
                a.SetTag("server.address", "offline.invalid");
                using var command = new SqlCommand("SELECT [m].[Id] FROM [MobilePushDeliveries] AS [m] INNER JOIN [MobileActivityNotifications] AS [n] ON [m].[NotificationId]=[n].[Id] INNER JOIN [MobilePushDevices] AS [d] ON [m].[MobilePushDeviceId]=[d].[Id]");
                RoutineSqlDependencyRouting.ApproveQuery(a, command);
                expected[a.SpanId.ToHexString()] = a.TraceId.ToHexString();
                return Task.FromResult(1);
            });
        await RoutineSqlPolling.ReadMobilePushOutboxAsync(() => "SELECT 1", () =>
        {
            using var failure = source.StartActivity("essential-failure", ActivityKind.Client);
            Assert.NotNull(failure);
            failure.SetTag("db.system.name", "microsoft.sql_server");
            failure.SetTag("db.namespace", "offline");
            failure.SetTag("server.address", "offline.invalid");
            failure.SetStatus(ActivityStatusCode.Error);
            return Task.FromResult(1);
        });
        using (var outside = source.StartActivity("baseline-policy", ActivityKind.Client))
        {
            Assert.NotNull(outside);
            outside.SetTag("db.system.name", "microsoft.sql_server");
        }
        Assert.True(provider.ForceFlush(10000));
        app.Services.GetRequiredService<OpenTelemetry.Metrics.MeterProvider>().ForceFlush(10000);
        await app.StopAsync();
        await app.DisposeAsync();
        var payload = string.Join("\n", sink.Payloads);
        Assert.False(string.IsNullOrEmpty(payload));

        // Retain the offline payload in failure output so SDK schema changes are diagnosable.
        var envelopes = sink.Payloads.SelectMany(p => p.TrimStart().StartsWith("[")
            ? JsonDocument.Parse(p).RootElement.EnumerateArray().Select(e => e.Clone()).ToArray()
            : p.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone())).ToArray();
        var dependencies = envelopes.Where(e => e.GetProperty("data").GetProperty("baseType").GetString() == "RemoteDependencyData").ToArray();
        var routine = dependencies.Where(e => e.GetProperty("data").GetProperty("baseData").GetProperty("name").GetString() == "routine").ToArray();
        Assert.InRange(routine.Length, 1, 150);
        Assert.All(routine, e =>
        {
            Assert.Equal(10, e.GetProperty("sampleRate").GetDouble());
            var data = e.GetProperty("data").GetProperty("baseData");
            var id = data.GetProperty("id").GetString()!;
            Assert.Equal(expected[id], e.GetProperty("tags").GetProperty("ai.operation.id").GetString());
            Assert.Contains("_MS.ProcessedByMetricExtractors", data.GetProperty("properties").ToString());
            Assert.Equal("sampling-fixture", e.GetProperty("tags").GetProperty("ai.cloud.role").GetString());
        });
        var metricPoints = envelopes.Where(e => e.GetProperty("data").GetProperty("baseType").GetString() == "MetricData")
            .SelectMany(e => e.GetProperty("data").GetProperty("baseData").GetProperty("metrics").EnumerateArray())
            .Where(m => m.GetProperty("name").GetString() == "dependencies/duration").ToArray();
        Assert.NotEmpty(metricPoints);
        Assert.Equal(502, metricPoints.Sum(m => m.GetProperty("count").GetInt32()));
        if (zeroHeadRatio)
            Assert.DoesNotContain(dependencies, e => e.GetProperty("data").GetProperty("baseData").GetProperty("name").GetString() == "baseline-policy");
        var protectedItem = Assert.Single(dependencies.Where(e => e.GetProperty("data").GetProperty("baseData").GetProperty("name").GetString() == "essential-failure"));
        Assert.True(!protectedItem.TryGetProperty("sampleRate", out var rate) || rate.GetDouble() == 100);
        Assert.Equal(dependencies.Length, dependencies.Select(e => e.GetProperty("data").GetProperty("baseData").GetProperty("id").GetString()).Distinct().Count());
    }
}
