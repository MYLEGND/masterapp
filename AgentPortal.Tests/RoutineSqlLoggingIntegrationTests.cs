using System;
using System.Linq;
using AgentPortal.Services.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentPortal.Tests;

public class RoutineSqlLoggingIntegrationTests
{
    [Fact]
    public void Installed_ef_emitter_retains_slow_commands_and_filters_only_fast_success()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config["TelemetryCostControl:SuppressRoutineEfCommandLogs"] = "false";
        var capture = new RoutineSqlLoggingTests.CaptureProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(capture));
        services.AddRoutineSqlLogReduction(config);
        using var provider = services.BuildServiceProvider();
        using var context = new DbContext(new DbContextOptionsBuilder()
            .UseSqlServer("Server=127.0.0.1;Database=offline-fixture;Integrated Security=true;Encrypt=false")
            .UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>()).Options);
        var connection = context.GetService<IRelationalConnection>();
        var diagnostics = context.GetService<IRelationalCommandDiagnosticsLogger>();
        using var command = connection.DbConnection.CreateCommand();
        command.CommandText = "SELECT 1";
        void Emit(int milliseconds) => diagnostics.CommandScalarExecuted(connection, command, command.CommandText,
            context, Guid.NewGuid(), connection.ConnectionId, 1, DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(milliseconds), CommandSource.ExecuteSqlRaw);
        Emit(1);
        Assert.Single(capture.Records.Where(x => x.Category == RoutineSqlLogging.Category));
        config["TelemetryCostControl:SuppressRoutineEfCommandLogs"] = "true";
        config.Reload();
        Emit(1);
        Assert.Single(capture.Records.Where(x => x.Category == RoutineSqlLogging.Category));
        Emit(1000);
        Assert.Equal(2, capture.Records.Count(x => x.Category == RoutineSqlLogging.Category));
        Assert.Equal(System.Data.ConnectionState.Closed, connection.DbConnection.State);
    }
    [Fact]
    public void Application_insights_registration_uses_one_existing_factory()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(
            new Microsoft.AspNetCore.Builder.WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddApplicationInsightsTelemetry(o =>
            o.ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001");
        var originalProviders = builder.Services.Count(x => x.ServiceType == typeof(ILoggerProvider));
        builder.Services.AddRoutineSqlLogReduction(builder.Configuration);
        builder.Services.AddRoutineSqlLogReduction(builder.Configuration);
        Assert.Single(builder.Services.Where(x => x.ServiceType == typeof(ILoggerFactory)));
        Assert.Equal(originalProviders, builder.Services.Count(x => x.ServiceType == typeof(ILoggerProvider)));
        // Build without starting hosted exporters or a server; no telemetry is sent.
        using var app = builder.Build();
        var factory = app.Services.GetRequiredService<ILoggerFactory>();
        Assert.NotNull(factory.CreateLogger(RoutineSqlLogging.Category));
        Assert.NotNull(factory.CreateLogger("Legend.Payments"));
    }

}
