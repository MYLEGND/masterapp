using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentPortal.Services.Telemetry;

/// <summary>Content-aware filtering at the existing logger factory, without a second exporter.</summary>
public static class RoutineSqlLogging
{
    public const string Category = "Microsoft.EntityFrameworkCore.Database.Command";
    public const string EventName = Category + ".CommandExecuted";

    public static IServiceCollection AddRoutineSqlLogReduction(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(x => x.ServiceType == typeof(Registration))) return services;
        var registrations = services.Where(x => x.ServiceType == typeof(ILoggerFactory)).ToArray();
        // Unknown factory composition must retain telemetry, not break application startup.
        if (registrations.Length != 1 || registrations[0].IsKeyedService ||
            registrations[0].Lifetime != ServiceLifetime.Singleton) return services;
        var original = registrations[0];
        services.Configure<RoutineSqlTelemetryOptions>(configuration.GetSection("TelemetryCostControl"));
        services.Remove(original);
        services.AddSingleton<ILoggerFactory>(sp =>
        {
            var inner = original.ImplementationInstance as ILoggerFactory
                ?? (original.ImplementationFactory is { } factory
                    ? (ILoggerFactory)factory(sp)
                    : (ILoggerFactory)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!));
            return new FilteringFactory(inner, sp.GetRequiredService<IOptionsMonitor<RoutineSqlTelemetryOptions>>(),
                original.ImplementationInstance is null);
        });
        services.AddSingleton(new Registration());
        return services;
    }

    private sealed class Registration { }

    private sealed class FilteringFactory(ILoggerFactory inner,
        IOptionsMonitor<RoutineSqlTelemetryOptions> options, bool ownsInner) : ILoggerFactory
    {
        private int disposed;
        public ILogger CreateLogger(string categoryName)
        {
            var logger = inner.CreateLogger(categoryName);
            return categoryName == Category ? new FilteringLogger(logger, options) : logger;
        }
        public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0 && ownsInner) inner.Dispose();
        }
    }

    private sealed class FilteringLogger(ILogger inner,
        IOptionsMonitor<RoutineSqlTelemetryOptions> options) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (options.CurrentValue.SuppressRoutineEfCommandLogs &&
                IsRoutineSuccess(logLevel, eventId, state, exception)) return;
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }

    private static bool IsRoutineSuccess<TState>(LogLevel level, EventId eventId, TState state, Exception? exception)
    {
        if (level != LogLevel.Information || exception is not null ||
            eventId.Id != 20101 || eventId.Name != EventName ||
            state is not IReadOnlyList<KeyValuePair<string, object?>> values ||
            values.Count is < 1 or > 32) return false;
        string? elapsed = null;
        var found = false;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].Key != "elapsed") continue;
            if (found || values[i].Value is not string duration) return false;
            found = true;
            elapsed = duration;
        }
        // EF emits invariant N0 milliseconds. Retain every unfamiliar shape,
        // non-ASCII digit, negative value, separator, and rounded one-second boundary.
        return found && elapsed is { Length: >= 1 and <= 3 } &&
            elapsed.All(c => c is >= '0' and <= '9') &&
            (elapsed.Length == 1 || elapsed[0] != '0') &&
            int.TryParse(elapsed, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) && ms < 1000;
    }
}

public sealed class RoutineSqlTelemetryOptions
{
    // Missing configuration retains all telemetry. Production's appsettings owns opt-in.
    public bool SuppressRoutineEfCommandLogs { get; set; }
    public bool SampleRoutineSqlDependencies { get; set; }
}
