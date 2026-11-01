using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using AgentPortal.Services.Analytics;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class AnalyticsIncidentProjectionTests
{
    [Fact]
    public async Task Monitor_ReadsOnlyInteractionFields_AndPreservesCountsAndCanonicalFilters()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var reads = new EventReadCapture();
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection).AddInterceptors(reads).Options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        var landing = AnalyticsEventCatalog.Definitions.First(x => x.CountsAsLandingView).Name;
        var form = AnalyticsEventCatalog.Definitions.First(x => x.CountsAsFormStart).Name;
        var funnel = AnalyticsEventCatalog.Definitions.First(x => x.CountsAsFunnelStart).Name;
        AnalyticsEvent Event(string name, string? session, string? visitor = null, int hours = -1) => new()
        {
            EventId = Guid.NewGuid(), EventType = name, SessionId = session, VisitorId = visitor,
            EventUtc = now.AddHours(hours), ReceivedUtc = now.AddHours(hours),
            Environment = "production", Host = "example.invalid", UserAgent = "Synthetic browser",
            HumanInteractionCount = 3, EngagedMilliseconds = 15000, DwellMilliseconds = 15000,
            ScrollPercent = 50, MouseMoveCount = 5,
            MetadataJson = "{\"syntheticPayload\":\"" + new string('x', 65536) + "\"}"
        };
        var events = new List<AnalyticsEvent>
        {
            Event(landing, " Session-A ", "ignored"), Event(landing.ToUpperInvariant(), "session-a", "other"),
            Event(landing, " ", " Visitor-B "), Event(landing, null, "visitor-b"),
            Event(landing, null), Event(landing, null),
            Event(form, "form-a"), Event(form.ToUpperInvariant(), " FORM-A "),
            Event(funnel, "funnel-a"), Event(funnel, "funnel-old", hours: -25),
            Event(landing, "outside-window", hours: -49)
        };
        var internalEvent = Event(landing, "internal"); internalEvent.IsInternal = true; events.Add(internalEvent);
        var devEvent = Event(landing, "dev"); devEvent.Environment = "development"; events.Add(devEvent);
        var botEvent = Event(landing, "bot"); botEvent.UserAgent = "crawler bot"; events.Add(botEvent);
        db.AnalyticsEvents.AddRange(events);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        reads.Commands.Clear();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Analytics:EnvironmentFilter"] = "production"
        }).Build();
        var service = new AnalyticsIncidentQueryService(db, new AnalyticsQueryService(db, config),
            Mock.Of<IEmailSender>(), config, NullLogger<AnalyticsIncidentQueryService>.Instance);

        var result = await service.GetSystemMonitorAsync();

        Assert.True(result.IsAvailable, result.ErrorCode);
        Assert.Equal(4, result.FunnelHealth.PageViews);
        Assert.Equal(1, result.FunnelHealth.LeadFormStarts);
        var starts = Assert.Single(result.FocusMetrics, x => x.Key == "quote_start_events");
        Assert.Equal(2m, starts.CurrentValue); // Form starts also count as funnel starts.
        Assert.Equal(1m, starts.PreviousValue);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(2, reads.Commands.Count);
        foreach (var sql in reads.Commands)
        {
            // Only the outer SELECT is narrowed: canonical quality filtering may
            // still use other columns within its existing SQL predicates/joins.
            var columns = sql[..sql.IndexOf("\nFROM", StringComparison.Ordinal)];
            Assert.Equal(4, columns.Split(',').Length);
            foreach (var required in new[] { "EventType", "SessionId", "VisitorId", "EventId" })
                Assert.Contains("\"" + required + "\"", columns);
            foreach (var unused in new[] { "MetadataJson", "Url", "UserAgent", "ButtonLabel", "UtmCampaign", "Id" })
                Assert.DoesNotContain("\"" + unused + "\"", columns);
        }
    }

    [Fact]
    public async Task Monitor_PropagatesCallerCancellation()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var config = new ConfigurationBuilder().Build();
        var service = new AnalyticsIncidentQueryService(db, new AnalyticsQueryService(db, config),
            Mock.Of<IEmailSender>(), config, NullLogger<AnalyticsIncidentQueryService>.Instance);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetSystemMonitorAsync(canceled.Token));
    }

    private sealed class EventReadCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) &&
                command.CommandText.Contains("FROM \"AnalyticsEvents\"", StringComparison.Ordinal))
                Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
