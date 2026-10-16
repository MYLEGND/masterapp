using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AgentPortal.Services.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentPortal.Tests;

public class RoutineSqlLoggingTests
{
    private static readonly EventId Executed = new(20101, RoutineSqlLogging.EventName);
    private static KeyValuePair<string, object?>[] State(object? elapsed) =>
        new[] { new KeyValuePair<string, object?>("elapsed", elapsed) };

    private static (ServiceProvider Services, IConfigurationRoot Config, CaptureProvider Capture) Create(
        bool enabled = true, string? providerName = null)
    {
        var data = new Dictionary<string, string?> {
            ["TelemetryCostControl:SuppressRoutineEfCommandLogs"] = enabled.ToString()
        };
        if (providerName is not null) data["Logging:" + providerName + ":LogLevel:Default"] = "Warning";
        var config = new ConfigurationBuilder().AddInMemoryCollection(data).Build();
        var capture = new CaptureProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddConfiguration(config.GetSection("Logging")).AddProvider(capture));
        services.AddRoutineSqlLogReduction(config);
        services.AddRoutineSqlLogReduction(config);
        Assert.Single(services.Where(s => s.ServiceType == typeof(ILoggerFactory)));
        return (services.BuildServiceProvider(), config, capture);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("999")]
    public void Only_known_fast_success_is_suppressed(string elapsed)
    {
        var setup = Create();
        using var services = setup.Services;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(RoutineSqlLogging.Category);
        logger.Log(LogLevel.Information, Executed, State(elapsed), null,
            (s, e) => throw new InvalidOperationException("Suppressed records must not be formatted."));
        Assert.Empty(setup.Capture.Records);
    }

    [Theory]
    [InlineData("1,000")]
    [InlineData("1000")]
    [InlineData("1001")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("0.5")]
    [InlineData("١")]
    [InlineData("01")]
    [InlineData("")]
    [InlineData(null)]
    public void Slow_or_unclassified_duration_is_preserved(string? elapsed)
    {
        var setup = Create();
        using var services = setup.Services;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(RoutineSqlLogging.Category);
        logger.Log(LogLevel.Information, Executed, State(elapsed), null, (s, e) => "original");
        Assert.Equal("original", Assert.Single(setup.Capture.Records).Message);
    }

    [Fact]
    public void Unknown_state_duplicate_duration_exception_and_other_events_are_preserved()
    {
        var setup = Create();
        using var services = setup.Services;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(RoutineSqlLogging.Category);
        logger.Log(LogLevel.Information, Executed, "unknown", null, (s, e) => s);
        logger.Log(LogLevel.Information, Executed, State("1").Concat(State("2")).ToArray(), null, (s,e) => "duplicate");
        logger.Log(LogLevel.Information, Executed, State(2), null, (s,e) => "numeric-unknown");
        var error = new Exception("fixture");
        logger.Log(LogLevel.Information, Executed, State("1"), error, (s,e) => "exception");
        logger.Log(LogLevel.Information, new EventId(20102, "CommandError"), State("1"), null, (s,e) => "error-event");
        logger.Log(LogLevel.Information, new EventId(20101), State("1"), null, (s,e) => "unknown-name");
        foreach (var level in new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Warning, LogLevel.Error, LogLevel.Critical })
            logger.Log(level, Executed, State("1"), null, (s,e) => "level");
        Assert.Equal(11, setup.Capture.Records.Count);
        Assert.Same(error, setup.Capture.Records[3].Exception);
    }

    [Theory]
    [InlineData("Legend.Security")]
    [InlineData("Legend.Audit")]
    [InlineData("Legend.Payments")]
    [InlineData("Infrastructure.Diagnostics.RuntimeDiagnosticStore")]
    public void Non_ef_evidence_is_untouched(string category)
    {
        var setup = Create();
        using var services = setup.Services;
        services.GetRequiredService<ILoggerFactory>().CreateLogger(category)
            .Log(LogLevel.Information, Executed, State("1"), null, (s,e) => "essential");
        Assert.Equal(category, Assert.Single(setup.Capture.Records).Category);
    }

    [Fact]
    public void Scopes_trace_correlation_state_and_formatter_are_forwarded_once()
    {
        var setup = Create();
        using var services = setup.Services;
        using var activity = new Activity("test-operation").Start();
        var factory = services.GetRequiredService<ILoggerFactory>();
        var logger = factory.CreateLogger(RoutineSqlLogging.Category);
        using var scope = logger.BeginScope("evidence-scope");
        var state = State("1000");
        logger.Log(LogLevel.Information, Executed, state, null, (s,e) => "slow");
        var record = Assert.Single(setup.Capture.Records);
        Assert.Same(state, record.State);
        Assert.Equal(activity.TraceId.ToString(), record.TraceId);
        Assert.Contains("evidence-scope", record.Scopes);
        Assert.True(logger.IsEnabled(LogLevel.Information));
        var extra = new CaptureProvider();
        factory.AddProvider(extra);
        logger.Log(LogLevel.Warning, Executed, state, null, (s,e) => "warning");
        Assert.Single(extra.Records);
        Assert.Equal(2, setup.Capture.Records.Count);
    }

    [Theory]
    [InlineData("CostTest")]
    [InlineData("AgentPortal.Tests.RoutineSqlLoggingTests+CaptureProvider")]
    public void Provider_alias_type_filters_and_reload_are_preserved(string providerName)
    {
        var setup = Create(providerName: providerName);
        using var services = setup.Services;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(RoutineSqlLogging.Category);
        Assert.False(logger.IsEnabled(LogLevel.Information));
        logger.Log(LogLevel.Information, Executed, State("1000"), null, (s,e) => "filtered-by-original-provider");
        Assert.Empty(setup.Capture.Records);
        setup.Config["Logging:" + providerName + ":LogLevel:Default"] = "Information";
        setup.Config.Reload();
        Assert.True(logger.IsEnabled(LogLevel.Information));
        logger.Log(LogLevel.Information, Executed, State("1000"), null, (s,e) => "retained");
        Assert.Single(setup.Capture.Records);
    }

    [Fact]
    public void Rollback_flag_reloads_without_replacing_pipeline()
    {
        var setup = Create();
        using var services = setup.Services;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(RoutineSqlLogging.Category);
        logger.Log(LogLevel.Information, Executed, State("1"), null, (s,e) => "suppressed");
        setup.Config["TelemetryCostControl:SuppressRoutineEfCommandLogs"] = "false";
        setup.Config.Reload();
        logger.Log(LogLevel.Information, Executed, State("1"), null, (s,e) => "retained");
        Assert.Equal("retained", Assert.Single(setup.Capture.Records).Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Factory_ownership_is_preserved(bool suppliedInstance)
    {
        var inner = new TrackingFactory();
        var services = new ServiceCollection();
        if (suppliedInstance) services.AddSingleton<ILoggerFactory>(inner);
        else services.AddSingleton<ILoggerFactory>(_ => inner);
        services.AddRoutineSqlLogReduction(new ConfigurationBuilder().Build());
        var sp = services.BuildServiceProvider();
        var decorated = sp.GetRequiredService<ILoggerFactory>();
        decorated.Dispose();
        sp.Dispose();
        Assert.Equal(suppliedInstance ? 0 : 1, inner.Disposals);
    }

    [Fact]
    public void Missing_configuration_and_unknown_factory_composition_retain_logging()
    {
        var setup = Create(enabled:false);
        using var sp = setup.Services;
        sp.GetRequiredService<ILoggerFactory>().CreateLogger(RoutineSqlLogging.Category)
            .Log(LogLevel.Information, Executed, State("1"), null, (s,e) => "retained");
        Assert.Single(setup.Capture.Records);
        var services = new ServiceCollection();
        services.AddRoutineSqlLogReduction(new ConfigurationBuilder().Build());
        Assert.Empty(services);
        services.AddSingleton<ILoggerFactory>(new TrackingFactory());
        services.AddSingleton<ILoggerFactory>(new TrackingFactory());
        services.AddRoutineSqlLogReduction(new ConfigurationBuilder().Build());
        Assert.Equal(2, services.Count);
    }

    private sealed class TrackingFactory : ILoggerFactory
    {
        public int Disposals;
        public void Dispose() => Disposals++;
        public ILogger CreateLogger(string categoryName) => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) { }
    }

    public sealed record Captured(string Category, object? State, Exception? Exception, string Message,
        string? TraceId, List<string> Scopes);

    [ProviderAlias("CostTest")]
    public sealed class CaptureProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
        public List<Captured> Records { get; } = new();
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;
        public void Dispose() { }
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);
        private sealed class CaptureLogger(CaptureProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.scopes.Push(state);
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var scopeValues = new List<string>();
                owner.scopes.ForEachScope((s, list) => list.Add(s?.ToString() ?? ""), scopeValues);
                owner.Records.Add(new(category,state,exception,formatter(state,exception),
                    Activity.Current?.TraceId.ToString(),scopeValues));
            }
        }
    }
}
