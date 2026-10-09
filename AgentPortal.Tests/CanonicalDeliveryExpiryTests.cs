using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CanonicalDeliveryExpiryTests
{
    [Fact]
    public async Task AgedCanonicalRetryExpiresWithoutRewritingTheHistoricalResendFence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var source = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext { EventName = "Lead", IsServerAuthority = true,
            EventUtc = DateTime.UtcNow.AddDays(-8), Host = "mylegnd.com" });
        UnifiedAnalyticsWriter.Write(db, source); await db.SaveChangesAsync();
        MarketingDestinationDelivery Receipt(string origin, string id) => new() {
            OwnerKey = MarketingOwnerScope.Founder.Key, Provider = "openai", Channel = "server", CanonicalSource = origin,
            CanonicalEventId = id, ProviderEventName = "lead_created", AnalyticsEventId = source.Id, Status = "retryable",
            NextAttemptUtc = DateTime.UtcNow.AddHours(-1)
        };
        var canonical = Receipt(nameof(AnalyticsEvent), "canonical-old");
        var historical = Receipt(nameof(MetaSignalEvent), "legacy-old");
        db.AddRange(canonical, historical); await db.SaveChangesAsync();
        using var services = new ServiceCollection().AddSingleton(db).AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton(new Mock<IOpenAiAdsAccountConnectionAuthority>(MockBehavior.Strict).Object)
            .AddSingleton(new Mock<IOpenAiConversionsApiService>(MockBehavior.Strict).Object).BuildServiceProvider();
        var dispatcher = new OpenAiConversionDispatcherHostedService(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OpenAiConversionDispatcherHostedService>.Instance);
        await dispatcher.DispatchBatchAsync(CancellationToken.None);
        await db.Entry(canonical).ReloadAsync(); await db.Entry(historical).ReloadAsync();
        Assert.Equal("permanent_failure", canonical.Status);
        Assert.Equal("event_delivery_window_expired", canonical.ErrorCode); Assert.Null(canonical.NextAttemptUtc);
        Assert.Equal("retryable", historical.Status); Assert.NotNull(historical.NextAttemptUtc);
    }
}
