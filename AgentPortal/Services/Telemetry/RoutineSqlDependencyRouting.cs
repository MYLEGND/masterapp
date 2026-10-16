using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using Microsoft.Data.SqlClient;
using Azure.Monitor.OpenTelemetry.Exporter;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.SqlClient;
using OpenTelemetry.Trace;

namespace AgentPortal.Services.Telemetry;

/// <summary>Exclusive, outcome-aware routing for explicitly scoped outbox discovery SQL.</summary>
public sealed class RoutineSqlDependencyRouting
{
    private const string ApprovedQuery = "Legend.RoutineSqlPolling.ApprovedQuery";
    private const string SqlSource = "OpenTelemetry.Instrumentation.SqlClient";
    private readonly IOptionsMonitor<RoutineSqlTelemetryOptions> options;
    private readonly string database;
    private readonly string server;
    public RoutedBatchProcessor FullQueue { get; }
    public RoutedBatchProcessor SampledQueue { get; }
    public BaseProcessor<Activity> CaptureProcessor { get; }

    public RoutineSqlDependencyRouting(IOptionsMonitor<RoutineSqlTelemetryOptions> options,
        BaseExporter<Activity> fullExporter, BaseExporter<Activity> sampledExporter,
        string database, string server)
    {
        this.options = options;
        this.database = database;
        this.server = NormalizeServer(server);
        FullQueue = new(fullExporter);
        SampledQueue = new(sampledExporter);
        CaptureProcessor = new Capture(this);
    }

    public static void Register(IServiceCollection services, string connectionString)
    {
        if (services.Any(x => x.ServiceType == typeof(RoutineSqlDependencyRouting))) return;
        // AI 3.0's reflection bridge uses the older exporter property name.
        // Preserve its configured counter setting after the public exporter upgrade.
        services.AddOptions<AzureMonitorExporterOptions>()
            .Configure<IOptions<Microsoft.ApplicationInsights.AspNetCore.Extensions.ApplicationInsightsServiceOptions>>(
                (o, ai) => o.EnablePerformanceCounters = ai.Value.EnablePerformanceCounterCollectionModule);
        services.Configure<SqlClientTraceInstrumentationOptions>(o =>
        {
            var existing = o.EnrichWithSqlCommand;
            o.EnrichWithSqlCommand = (activity, command) =>
            {
                existing?.Invoke(activity, command);
                try { ApproveQuery(activity, command); } catch { /* Unknown classification keeps original export. */ }
            };
        });
        var identity = new SqlConnectionStringBuilder(connectionString);
        services.AddSingleton(sp => new RoutineSqlDependencyRouting(
            sp.GetRequiredService<IOptionsMonitor<RoutineSqlTelemetryOptions>>(),
            CreateExporter(sp, 1f), CreateExporter(sp, .1f), identity.InitialCatalog, identity.DataSource));
        services.ConfigureOpenTelemetryTracerProvider((sp, tracing) =>
        {
            var routing = sp.GetRequiredService<RoutineSqlDependencyRouting>();
            tracing.AddProcessor(routing.FullQueue);
            tracing.AddProcessor(routing.SampledQueue);
            tracing.AddProcessor(routing.CaptureProcessor);
        });
    }

    private static AzureMonitorTraceExporter CreateExporter(IServiceProvider services, float ratio)
    {
        // Fresh configured instances retain the canonical connection, identity, transport,
        // retries and storage. Never mutate the options shared by the existing pipeline.
        var configured = services.GetRequiredService<IOptionsFactory<AzureMonitorExporterOptions>>()
            .Create(Microsoft.Extensions.Options.Options.DefaultName);
        configured.SamplingRatio = ratio;
        configured.TracesPerSecond = null;
        return new AzureMonitorTraceExporter(configured);
    }

    public static void ApproveQuery(Activity activity, object command)
    {
        if (command is not DbCommand { CommandType: CommandType.Text } sql) return;
        if (RoutineSqlPolling.IsExpectedCommand(activity, sql.CommandText))
            activity.SetCustomProperty(ApprovedQuery, true);
    }

    private static string NormalizeServer(string value) =>
        value.Trim().Replace("tcp:", "", StringComparison.OrdinalIgnoreCase).Split(',')[0];

    private bool Eligible(Activity a) =>
        options.CurrentValue.SampleRoutineSqlDependencies &&
        a.Source.Name == SqlSource && a.Kind == ActivityKind.Client &&
        a.ParentId is null && a.Status == ActivityStatusCode.Unset &&
        a.Duration >= TimeSpan.Zero && a.Duration < TimeSpan.FromSeconds(1) &&
        !a.Events.Any() && a.GetTagItem("error.type") is null &&
        a.GetTagItem("db.response.status_code") is null &&
        Equals(a.GetTagItem("db.system.name"), "microsoft.sql_server") &&
        !string.IsNullOrEmpty(database) && !string.IsNullOrEmpty(server) &&
        Equals(a.GetTagItem("db.namespace"), database) &&
        a.GetTagItem("server.address") is string address &&
        string.Equals(NormalizeServer(address), server, StringComparison.OrdinalIgnoreCase) &&
        a.GetCustomProperty(ApprovedQuery) is true;

    private void ExportDeferred(Activity activity, bool succeeded)
    {
        try
        {
        // Disabling the feature also restores full export for reads already in flight.
        if (!succeeded || !options.CurrentValue.SampleRoutineSqlDependencies)
        {
            FullQueue.Enqueue(activity);
            return;
        }
        // Trace IDs are uniformly random; deterministic selection preserves correlation.
        var bytes = Convert.FromHexString(activity.TraceId.ToHexString());
        var bucket = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        if (bucket < uint.MaxValue / 10u) SampledQueue.Enqueue(activity);
        }
        catch
        {
            // Telemetry failures cannot turn a successful discovery query into a failure.
            try { FullQueue.Enqueue(activity); } catch { /* Exporter unavailable; preserve application outcome. */ }
        }
    }

    private sealed class Capture(RoutineSqlDependencyRouting owner) : BaseProcessor<Activity>
    {
        public override void OnStart(Activity data)
        {
            try
            {
                if (owner.options.CurrentValue.SampleRoutineSqlDependencies && data.Source.Name == SqlSource)
                    RoutineSqlPolling.Capture(data);
            }
            catch { /* Unknown option state keeps the original path. */ }
        }
        public override void OnEnd(Activity data)
        {
            try
            {
                if (data.Source.Name != SqlSource || data.Kind != ActivityKind.Client || data.ParentId is not null)
                    return;
                // Azure's sampler retains rejected spans as RecordOnly. Select outcomes
                // from that existing stream; leave global head sampling unchanged.
                // Scoped protected/unknown work uses the full-weight route.
                var eligible = owner.Eligible(data);
                RoutineSqlPolling.Defer(data, (activity, succeeded) =>
                    owner.ExportDeferred(activity, succeeded && eligible));
            }
            catch { /* Classification failure leaves Recorded unchanged. */ }
        }
    }

    public sealed class RoutedBatchProcessor(BaseExporter<Activity> exporter) : BatchExportProcessor<Activity>(exporter)
    {
        // The provider calls this for all spans; only explicit routing may enqueue.
        public override void OnEnd(Activity activity) { }
        public void Enqueue(Activity activity) => base.OnEnd(activity);
    }
}
