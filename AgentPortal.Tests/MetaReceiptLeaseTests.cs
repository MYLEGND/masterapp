using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
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
using Microsoft.Extensions.Options;
using Moq;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MetaReceiptLeaseTests
{
    public static IEnumerable<object[]> Races => from success in new[] { false, true }
        from winner in new[] { "accepted", "retry", "active" } select new object[] { success, winner };

    [Theory, MemberData(nameof(Races))]
    public async Task ActualDispatcherCannotOverwriteNewerReceipt(bool success, string winner)
    {
        await using var f = await Fixture.CreateAsync();
        f.Response = async ct => {
            await using var db = f.Db(); var row = await db.MetaSignalEvents.SingleAsync();
            Assert.NotNull(row.MetaDispatchClaimToken);
            row.MetaServerSent = winner == "accepted";
            row.MetaDispatchClaimToken = winner == "active" ? "new-token" : null;
            row.MetaDispatchClaimExpiresUtc = winner == "active" ? DateTime.UtcNow.AddMinutes(10) : null;
            row.MetadataJson = "{\"newWinner\":\"" + winner + "\",\"metaServerAttemptCount\":2}";
            row.FbcPresent = false; row.FbpPresent = false; row.FbclidPresent = false;
            await db.SaveChangesAsync();
            return Reply(success ? 200 : 503, success ? "{\"events_received\":1}" : "{}");
        };
        await f.DispatchAsync(); Assert.Equal(1, f.Calls);
        await using var read = f.Db(); var current = await read.MetaSignalEvents.SingleAsync();
        Assert.Equal(winner == "accepted", current.MetaServerSent);
        Assert.Equal(winner == "active" ? "new-token" : null, current.MetaDispatchClaimToken);
        Assert.Equal(winner, CanonicalAdvertisingEventProjection.ReadString(current.MetadataJson, "newWinner"));
        Assert.Equal(2, CanonicalAdvertisingEventProjection.ReadInt64(current.MetadataJson, "metaServerAttemptCount"));
        Assert.False(current.FbcPresent); Assert.False(current.FbpPresent); Assert.False(current.FbclidPresent);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("42", false)]
    [InlineData("{\"events_received\":0}", false)]
    [InlineData("{\"events_received\":1}", true)]
    public async Task ActualMappedPolicyPaidReceiptUsesProviderAcknowledgment(string body, bool accepted)
    {
        await using var f = await Fixture.CreateAsync(); f.Response = _ => Task.FromResult(Reply(200, body));
        await f.DispatchAsync(); Assert.Equal(1, f.Calls);
        await using var db = f.Db(); var row = await db.MetaSignalEvents.SingleAsync();
        Assert.Equal(accepted, row.MetaServerSent); Assert.Null(row.MetaDispatchClaimToken);
        Assert.Equal(accepted, CanonicalAdvertisingEventProjection.ReadBoolean(row.MetadataJson, "metaServerSent"));
        Assert.Equal(1, CanonicalAdvertisingEventProjection.ReadInt64(row.MetadataJson, "metaServerAttemptCount"));
        Assert.Equal("synthetic-pixel", CanonicalAdvertisingEventProjection.ReadString(row.MetadataJson, "metaServerPixelId"));
        Assert.Equal("PolicyPaid", row.EventName);
    }

    [Fact]
    public async Task OwnedRetryKeepsIdentityAndMergesReceiptOnce()
    {
        await using var f = await Fixture.CreateAsync(); f.Response = _ => Task.FromResult(Reply(503, "{}"));
        await f.DispatchAsync();
        await using var db = f.Db(); var row = await db.MetaSignalEvents.SingleAsync(); var identity = row.EventId;
        Assert.False(row.MetaServerSent); Assert.Null(row.MetaDispatchClaimToken);
        Assert.True(CanonicalAdvertisingEventProjection.ReadBoolean(row.MetadataJson, "metaServerRetryable"));
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(row.MetadataJson!)!;
        metadata["metaServerNextAttemptUtc"] = DateTime.UtcNow.AddMinutes(-1); row.MetadataJson = metadata.ToJsonString(); await db.SaveChangesAsync();
        f.Response = _ => Task.FromResult(Reply(200, "{\"events_received\":1}")); await f.DispatchAsync(); await db.Entry(row).ReloadAsync();
        Assert.True(row.MetaServerSent); Assert.Equal(identity, row.EventId);
        Assert.Equal(2, CanonicalAdvertisingEventProjection.ReadInt64(row.MetadataJson, "metaServerAttemptCount"));
        Assert.NotNull(CanonicalAdvertisingEventProjection.ReadInt64(row.MetadataJson, "sourceAnalyticsEventId"));
        await f.DispatchAsync(); Assert.Equal(2, f.Calls);
    }

    [Fact]
    public async Task InterruptedTransportLeavesRecoverableLeaseWithoutInventedReceipt()
    {
        await using var f = await Fixture.CreateAsync(); using var cancellation = new CancellationTokenSource();
        f.Response = ct => { cancellation.Cancel(); throw new OperationCanceledException(ct); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.DispatchAsync(cancellation.Token));
        await using var db = f.Db(); var row = await db.MetaSignalEvents.SingleAsync();
        Assert.False(row.MetaServerSent); Assert.NotNull(row.MetaDispatchClaimToken); Assert.NotNull(row.MetaDispatchClaimExpiresUtc);
        Assert.Null(CanonicalAdvertisingEventProjection.ReadString(row.MetadataJson, "metaServerStatus"));
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("active")]
    [InlineData("metadata")]
    [InlineData("unchanged")]
    public async Task PredispatchDecisionCannotOverwriteChangedOrOwnedReceipt(string change)
    {
        await using var f = await Fixture.CreateAsync(); await using var old = f.Db();
        var stale = await old.MetaSignalEvents.AsNoTracking().SingleAsync();
        await using var newer = f.Db(); var current = await newer.MetaSignalEvents.SingleAsync();
        if (change == "accepted") current.MetaServerSent = true;
        if (change == "active") { current.MetaDispatchClaimToken = "active"; current.MetaDispatchClaimExpiresUtc = DateTime.UtcNow.AddMinutes(5); }
        if (change == "metadata") current.MetadataJson = "{\"newer\":true}";
        await newer.SaveChangesAsync();
        var original = current.MetadataJson;
        var affected = await MetaSendAuthority.PersistPreDispatchDecisionAsync(old, stale, new MetaConversionsApiResult { Status = "blocked_test" });
        Assert.Equal(change == "unchanged" ? 1 : 0, affected);
        await newer.Entry(current).ReloadAsync();
        if (change != "unchanged") Assert.Equal(original, current.MetadataJson);
    }

    [Fact]
    public async Task ActiveLeasesDoNotOccupyTheEligibleBatch()
    {
        await using var f = await Fixture.CreateAsync();
        await using var db = f.Db(); var ready = await db.MetaSignalEvents.SingleAsync();
        for (var index = 0; index < 26; index++)
            db.MetaSignalEvents.Add(new MetaSignalEvent { EventId = Guid.NewGuid().ToString("N"), EventName = ready.EventName,
                CommerceBusinessId = ready.CommerceBusinessId, CreatedUtc = ready.CreatedUtc.AddMinutes(-5), TrafficType = "crm",
                MetadataJson = ready.MetadataJson, MetaDispatchClaimToken = "active-" + index, MetaDispatchClaimExpiresUtc = DateTime.UtcNow.AddMinutes(5) });
        await db.SaveChangesAsync(); await f.DispatchAsync();
        Assert.Equal(1, f.Calls);
        Assert.Equal(26, await db.MetaSignalEvents.CountAsync(row => row.MetaDispatchClaimToken != null));
        Assert.Equal(1, await db.MetaSignalEvents.CountAsync(row => row.MetaServerSent));
    }

    [Fact]
    public void RetryCounterPreservesRootMetadataAuthority()
    {
        var metadata = MetaSendAuthority.MergeDispatchMetadata("{\"payload\":{\"metaServerAttemptCount\":9999},\"other\":\"kept\"}",
            new MetaConversionsApiResult { Attempted = true, Retryable = true });
        Assert.Equal(1, CanonicalAdvertisingEventProjection.ReadInt64(metadata, "metaServerAttemptCount"));
        Assert.True(CanonicalAdvertisingEventProjection.ReadBoolean(metadata, "metaServerRetryable"));
        Assert.Equal("kept", CanonicalAdvertisingEventProjection.ReadString(metadata, "other"));
    }

    private static HttpResponseMessage Reply(int code, string body) => new((HttpStatusCode)code) { Content = new StringContent(body) };
    private sealed class Handler(Fixture fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            fixture.Calls++;
            var form = await request.Content!.ReadAsStringAsync(ct);
            Assert.Contains("Purchase", Uri.UnescapeDataString(form));
            return await fixture.Response(ct);
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=False");
        public Func<CancellationToken, Task<HttpResponseMessage>> Response = _ => Task.FromResult(Reply(200, "{\"events_received\":1}"));
        public int Calls;
        public MasterAppDbContext Db() => new(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(); await f.connection.OpenAsync(); await using var db = f.Db(); await db.Database.EnsureCreatedAsync();
            var businessId = Guid.NewGuid(); db.CommerceBusinesses.Add(new CommerceBusiness { Id = businessId, Key = "synthetic", IsActive = true, Status = "Active" });
            var source = new AnalyticsEvent { EventId = Guid.NewGuid(), EventType = "PolicyPaid", CommerceBusinessId = businessId,
                EventUtc = DateTime.UtcNow.AddMinutes(-1), ReceivedUtc = DateTime.UtcNow, Host = "synthetic.example", Environment = "Production", UserAgent = "Mozilla/5.0",
                HumanInteractionCount = 3, EngagedMilliseconds = 15000, DwellMilliseconds = 20000, ScrollPercent = 70,
                MetadataJson = "{\"isServerAuthority\":true,\"measurementServerAuthorityEligible\":true,\"measurementConsentAllowed\":true}" };
            db.AnalyticsEvents.Add(source); await db.SaveChangesAsync();
            db.MetaSignalEvents.Add(new MetaSignalEvent { EventId = CanonicalAdvertisingEventProjection.ResolveEventId(source), EventName = "PolicyPaid", CommerceBusinessId = businessId,
                MetaDeduplicationKey = Guid.NewGuid().ToString("N"), CreatedUtc = source.EventUtc, Host = source.Host, TrafficType = "crm", UserAgent = "Mozilla/5.0",
                MetadataJson = MetaSignalSingleTruthPolicy.BuildMetadataJson("PolicyPaid", null, null, new { sourceAnalyticsEventId = source.Id,
                    canonicalSourceIdentity = true, sourceClientIpAddress = "203.0.113.1", sourceClientUserAgent = "Mozilla/5.0", sourceFbclid = "synthetic-click", sourceFbp = "synthetic-fbp", sourceFbc = "synthetic-fbc" }, false, true, true, true, "synthetic") });
            await db.SaveChangesAsync(); return f;
        }
        public async Task DispatchAsync(CancellationToken ct = default)
        {
            using var http = new HttpClient(new Handler(this));
            var pixels = new Mock<IMetaPixelResolutionService>();
            pixels.Setup(x => x.ResolveForOwnerAsync(It.IsAny<MarketingOwnerScope>(), It.IsAny<CancellationToken>())).ReturnsAsync(
                new ResolvedMetaPixelContext { PixelId = "synthetic-pixel", AccessToken = "synthetic-key", PixelOwnerType = "business" });
            await using var services = new ServiceCollection().AddScoped(_ => Db()).AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
                .AddSingleton(pixels.Object)
                .AddScoped<IMetaConversionsApiService>(sp => new MetaConversionsApiService(http, Options.Create(new MetaOptions()),
                    new MetaSendAuthority(sp.GetRequiredService<MasterAppDbContext>(), NullLogger<MetaSendAuthority>.Instance), NullLogger<MetaConversionsApiService>.Instance))
                .BuildServiceProvider();
            var dispatcher = new MetaSignalOutcomeDispatcherHostedService(services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new MetaSignalIntelligenceOptions { Enabled = true, SendServerEvents = true }), NullLogger<MetaSignalOutcomeDispatcherHostedService>.Instance);
            await (Task)typeof(MetaSignalOutcomeDispatcherHostedService).GetMethod("DispatchBatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dispatcher, [ct])!;
        }
        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
