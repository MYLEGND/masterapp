using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MarketingDeliveryLeaseTests
{
    public static IEnumerable<object[]> Races => from provider in new[] { "openai", "google" }
        from accepted in new[] { false, true }
        from state in new[] { "sent", "retryable", "permanent_failure", "active" }
        select new object[] { provider, accepted, state };

    [Theory, MemberData(nameof(Races))]
    public async Task LateResponseCannotOverwriteNewOwner(string provider, bool accepted, string state)
    {
        await using var f = await Fixture.CreateAsync(provider);
        string? newToken = state == "active" ? "new-claim" : null;
        f.Send = async ct => {
            await using var winner = f.Db();
            var row = await winner.MarketingDestinationDeliveries.SingleAsync();
            Assert.Equal(1, row.AttemptCount); Assert.NotNull(row.LastAttemptUtc); Assert.NotNull(row.ClaimToken);
            row.Status = state == "active" ? "pending" : state;
            row.ClaimToken = newToken; row.ClaimExpiresUtc = newToken is null ? null : DateTime.UtcNow.AddMinutes(2);
            row.AttemptCount = 2; row.ProviderReceiptJson = "new-receipt"; row.ErrorCode = "new-evidence";
            await winner.SaveChangesAsync();
            return accepted;
        };
        await f.DispatchAsync();
        await using var read = f.Db(); var current = await read.MarketingDestinationDeliveries.SingleAsync();
        Assert.Equal(state == "active" ? "pending" : state, current.Status);
        Assert.Equal(newToken, current.ClaimToken); Assert.Equal(2, current.AttemptCount);
        Assert.Equal("new-receipt", current.ProviderReceiptJson); Assert.Equal("new-evidence", current.ErrorCode);
        Assert.Contains(f.Sql, command => command.Contains("UPDATE \"MarketingDestinationDeliveries\"") &&
            command.Contains("WHERE \"Id\"") && command.Contains("AND \"ClaimToken\""));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("google")]
    public async Task OwnedRetryKeepsIdentityAndDestination(string provider)
    {
        await using var f = await Fixture.CreateAsync(provider);
        f.Send = _ => Task.FromResult(false);
        await f.DispatchAsync();
        await using var db = f.Db(); var first = await db.MarketingDestinationDeliveries.SingleAsync();
        Assert.Equal("retryable", first.Status); Assert.Equal(1, first.AttemptCount); Assert.Null(first.ClaimToken);
        var identity = first.CanonicalEventId; var destination = first.ConversionDataSourceId;
        first.NextAttemptUtc = DateTime.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
        f.Send = _ => Task.FromResult(true); await f.DispatchAsync();
        await db.Entry(first).ReloadAsync();
        Assert.Equal("sent", first.Status); Assert.Equal(2, first.AttemptCount); Assert.Null(first.ClaimToken);
        Assert.Equal(identity, first.CanonicalEventId); Assert.Equal(destination, first.ConversionDataSourceId);
        Assert.Equal(2, f.Calls);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("google")]
    public async Task ActiveLeaseIsSkippedBeforeConsentMutations(string provider)
    {
        await using var f = await Fixture.CreateAsync(provider);
        f.Send = async ct => {
            await using var db = f.Db(); var row = await db.MarketingDestinationDeliveries.SingleAsync();
            var token = row.ClaimToken;
            await db.AnalyticsEvents.ExecuteUpdateAsync(set => set.SetProperty(x => x.MetadataJson,
                "{\"isServerAuthority\":true,\"measurementServerAuthorityEligible\":true,\"measurementConsentAllowed\":false,\"gclid\":\"synthetic-click\"}"));
            await f.DispatchAsync(); await db.Entry(row).ReloadAsync();
            Assert.Equal("pending", row.Status); Assert.Equal(token, row.ClaimToken); Assert.Equal(1, row.AttemptCount);
            return true;
        };
        await f.DispatchAsync(); Assert.Equal(1, f.Calls);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("google")]
    public async Task ExpiryInvalidatesOutstandingResponse(string provider)
    {
        await using var f = await Fixture.CreateAsync(provider);
        f.Send = async ct => {
            await using var db = f.Db();
            await db.AnalyticsEvents.ExecuteUpdateAsync(set => set.SetProperty(x => x.EventUtc, DateTime.UtcNow.AddDays(-8)));
            await f.DispatchAsync(); return true;
        };
        await f.DispatchAsync();
        await using var read = f.Db(); var current = await read.MarketingDestinationDeliveries.SingleAsync();
        Assert.Equal("permanent_failure", current.Status); Assert.Equal("event_delivery_window_expired", current.ErrorCode);
        Assert.Null(current.ClaimToken); Assert.Null(current.ClaimExpiresUtc); Assert.Null(current.ProviderReceiptJson);
        Assert.Equal(1, current.AttemptCount);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("google")]
    public async Task CancellationRetainsRecoverableAttempt(string provider)
    {
        await using var f = await Fixture.CreateAsync(provider); using var cancellation = new CancellationTokenSource();
        f.Send = ct => { cancellation.Cancel(); throw new OperationCanceledException(ct); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.DispatchAsync(cancellation.Token));
        await using var db = f.Db(); var row = await db.MarketingDestinationDeliveries.SingleAsync();
        Assert.Equal(1, row.AttemptCount); Assert.NotNull(row.LastAttemptUtc); Assert.NotNull(row.ClaimToken);
        var identity = row.CanonicalEventId;
        row.ClaimExpiresUtc = DateTime.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
        f.Send = _ => Task.FromResult(true); await f.DispatchAsync(); await db.Entry(row).ReloadAsync();
        Assert.Equal(2, row.AttemptCount); Assert.Equal("sent", row.Status); Assert.Equal(identity, row.CanonicalEventId);
    }

    [Fact]
    public void SqlServerRuntimeMappingUsesExistingClaimColumnAsConcurrencyFence()
    {
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer("Server=synthetic;Database=synthetic;Integrated Security=true;TrustServerCertificate=true").Options);
        Assert.True(db.Model.FindEntityType(typeof(MarketingDestinationDelivery))!.FindProperty(nameof(MarketingDestinationDelivery.ClaimToken))!.IsConcurrencyToken);
    }

    [Fact]
    public void ClaimConcurrencyMappingDoesNotChangeSqlServerSchema()
    {
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer("Server=synthetic;Database=synthetic;Integrated Security=true;TrustServerCertificate=true").Options;
        using var current = new MasterAppDbContext(options);
        using var baseline = new UnfencedSchemaContext(options);
        Assert.Equal(baseline.Database.GenerateCreateScript(), current.Database.GenerateCreateScript());
    }

    private sealed class UnfencedSchemaContext(DbContextOptions<MasterAppDbContext> options) : MasterAppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<MarketingDestinationDelivery>().Property(x => x.ClaimToken).IsConcurrencyToken(false);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=False");
        private readonly string provider;
        private readonly EphemeralDataProtectionProvider protection = new();
        private readonly IConfiguration config = new ConfigurationBuilder().Build();
        public readonly List<string> Sql = new();
        public Func<CancellationToken, Task<bool>> Send = _ => Task.FromResult(true);
        public int Calls;
        private Fixture(string provider) => this.provider = provider;
        public MasterAppDbContext Db() => new(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).LogTo(Sql.Add).Options);
        public static async Task<Fixture> CreateAsync(string provider)
        {
            var f = new Fixture(provider); await f.connection.OpenAsync();
            await using var db = f.Db(); await db.Database.EnsureCreatedAsync();
            var owner = MarketingOwnerScope.Business(Guid.NewGuid());
            db.CommerceBusinesses.Add(new CommerceBusiness { Id = owner.CommerceBusinessId!.Value, Key = "synthetic", DisplayName = "Synthetic", IsActive = true, Status = "Active" });
            db.AnalyticsEvents.Add(new AnalyticsEvent { EventId = Guid.NewGuid(), EventType = "Lead", CommerceBusinessId = owner.CommerceBusinessId,
                EventUtc = DateTime.UtcNow.AddMinutes(-1), ReceivedUtc = DateTime.UtcNow, Host = "synthetic.example", Environment = "Production", UserAgent = "Mozilla/5.0",
                HumanInteractionCount = 3, EngagedMilliseconds = 15000, DwellMilliseconds = 20000, ScrollPercent = 70, MouseMoveCount = 5,
                MetadataJson = "{\"isServerAuthority\":true,\"measurementServerAuthorityEligible\":true,\"measurementConsentAllowed\":true,\"gclid\":\"synthetic-click\"}" });
            await db.SaveChangesAsync();
            using var protector = new MarketingCredentialProtector(f.protection);
            if (provider == "openai")
                await new OpenAiAdsAccountConnectionAuthority(db, protector).BindVerifiedAsync(owner,
                    new("synthetic-account", "Synthetic", "admin", "approved", "api_key", PixelId: "synthetic-pixel", ConversionDataSourceId: "synthetic-source"), new("synthetic-key", "synthetic-capi"));
            else
            {
                var store = new MarketingConnectionStore(db, protector);
                await store.SaveProviderCredentialAsync(owner, "google", "synthetic-refresh", MarketingProviderAuthorizationMethods.GoogleOAuthRefreshToken,
                    "{}", null, "1111111111", "Synthetic");
                var connection = await store.GetProviderConnectionAsync(owner, "google");
                await store.SaveProviderMeasurementConfigurationAsync(owner, new("google", null, "click", [new("Lead", "Lead", "customers/1111111111/conversionActions/77")], connection.Revision));
            }
            return f;
        }
        public async Task DispatchAsync(CancellationToken ct = default)
        {
            var services = new ServiceCollection().AddScoped(_ => Db()).AddSingleton(config)
                .AddScoped(_ => new MarketingCredentialProtector(protection)).AddScoped<MarketingConnectionStore>()
                .AddScoped<IOpenAiAdsAccountConnectionAuthority, OpenAiAdsAccountConnectionAuthority>();
            var openAi = new Mock<IOpenAiConversionsApiService>();
            openAi.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OpenAiConversionEvent>(), false, It.IsAny<CancellationToken>()))
                .Returns(async (string pixel, string key, OpenAiConversionEvent evt, bool validate, CancellationToken token) => {
                    Calls++; var accepted = await Send(token); return new OpenAiConversionsApiResult(true, accepted, !accepted, accepted ? 200 : 503, accepted ? "sent" : "retryable_failure", ProviderReceiptJson: "old-receipt"); });
            var external = new Mock<IExternalAdsConversionApiService>();
            external.Setup(x => x.SendAsync(It.IsAny<MarketingOwnerScope>(), It.IsAny<MarketingProviderConnectionSnapshot>(), It.IsAny<MarketingProviderMeasurementConfiguration>(), It.IsAny<MarketingProviderEventMapping>(), It.IsAny<AnalyticsEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async (MarketingOwnerScope owner, MarketingProviderConnectionSnapshot connection, MarketingProviderMeasurementConfiguration measurement, MarketingProviderEventMapping mapping, AnalyticsEvent source, string click, CancellationToken token) => {
                    Calls++; var accepted = await Send(token); return new ExternalAdsConversionApiResult(accepted, !accepted, accepted ? 200 : 503, accepted ? "sent" : "retryable_failure", ProviderReceiptJson: "old-receipt"); });
            services.AddSingleton(openAi.Object).AddSingleton(external.Object);
            await using var container = services.BuildServiceProvider();
            if (provider == "openai") await new OpenAiConversionDispatcherHostedService(container.GetRequiredService<IServiceScopeFactory>(), NullLogger<OpenAiConversionDispatcherHostedService>.Instance).DispatchBatchAsync(ct);
            else await new ExternalAdsConversionDispatcherHostedService(container.GetRequiredService<IServiceScopeFactory>(), NullLogger<ExternalAdsConversionDispatcherHostedService>.Instance).DispatchBatchAsync(ct);
        }
        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
