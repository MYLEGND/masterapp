using System;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MetaSendIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctStableIdentitiesDoNotBorrowSentSessionEvidence(bool explicitKey)
    {
        await using var f = await Fixture.CreateAsync();
        var old = f.Row("old", true); var next = f.Row("next", false);
        f.Db.AddRange(old, next); await f.Db.SaveChangesAsync();
        var decision = await f.Authority.TrySendAsync(f.Request(next, explicitKey));
        Assert.True(decision.Allowed);
        await f.Db.Entry(next).ReloadAsync(); Assert.Equal(decision.ReservationToken, next.MetaDispatchClaimToken);
        f.Authority.Complete(decision, new MetaConversionsApiResult { Sent = false }, new MetaConversionsApiEventRequest());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StableIdentityOwnsMemoryReservationAcrossTime(bool explicitKey)
    {
        await using var f = await Fixture.CreateAsync();
        var first = f.Row("first", false); var second = f.Row("second", false);
        f.Db.AddRange(first, second); await f.Db.SaveChangesAsync();
        var request = f.Request(first, explicitKey);
        var firstDecision = await f.Authority.TrySendAsync(request);
        var secondDecision = await f.Authority.TrySendAsync(f.Request(second, explicitKey));
        Assert.True(firstDecision.Allowed); Assert.True(secondDecision.Allowed);
        Assert.NotEqual(firstDecision.DedupeKey, secondDecision.DedupeKey);
        var replay = await f.Authority.TrySendAsync(request with { EventUtc = request.EventUtc.AddHours(1) });
        Assert.False(replay.Allowed); Assert.Equal(firstDecision.DedupeKey, replay.DedupeKey);
        f.Authority.Complete(firstDecision, new MetaConversionsApiResult { Sent = false }, new MetaConversionsApiEventRequest()); f.Authority.Complete(secondDecision, new MetaConversionsApiResult { Sent = false }, new MetaConversionsApiEventRequest());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderPurchaseFindsCanonicalPolicyPaid(bool alreadySent)
    {
        await using var f = await Fixture.CreateAsync();
        var row = f.Row("policy", alreadySent); row.EventName = "PolicyPaid";
        f.Db.Add(row); await f.Db.SaveChangesAsync();
        var decision = await f.Authority.TrySendAsync(f.Request(row, true) with { EventType = "Purchase" });
        Assert.Equal(!alreadySent, decision.Allowed);
        await f.Db.Entry(row).ReloadAsync();
        if (!alreadySent) { Assert.Equal(decision.ReservationToken, row.MetaDispatchClaimToken); f.Authority.Complete(decision, new MetaConversionsApiResult { Sent = false }, new MetaConversionsApiEventRequest()); }
        else Assert.Equal("meta_signal_event_already_sent", decision.Note);
    }

    [Fact]
    public async Task SentStableIdentityIsBlockedWithoutSessionOrTimeAgreement()
    {
        await using var f = await Fixture.CreateAsync(); var row = f.Row("sent", true);
        f.Db.Add(row); await f.Db.SaveChangesAsync();
        var decision = await f.Authority.TrySendAsync(f.Request(row, false) with { SessionId = "different", EventUtc = DateTime.UtcNow.AddHours(1) });
        Assert.False(decision.Allowed); Assert.Equal("meta_signal_event_already_sent", decision.Note);
    }

    [Fact]
    public async Task ForeignOwnerCannotSupplyASentFenceOrClaim()
    {
        await using var f = await Fixture.CreateAsync();
        var other = f.Row("foreign", true); other.CommerceBusinessId = Guid.NewGuid();
        var own = f.Row("own", false); own.MetaDeduplicationKey = other.MetaDeduplicationKey;
        f.Db.AddRange(other, own); await f.Db.SaveChangesAsync();
        var decision = await f.Authority.TrySendAsync(f.Request(own, true));
        Assert.True(decision.Allowed);
        await f.Db.Entry(own).ReloadAsync(); await f.Db.Entry(other).ReloadAsync();
        Assert.Equal(decision.ReservationToken, own.MetaDispatchClaimToken); Assert.Null(other.MetaDispatchClaimToken);
        f.Authority.Complete(decision, new MetaConversionsApiResult { Sent = false }, new MetaConversionsApiEventRequest());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatcherCannotFallBackToMemoryWithoutDurableSource(bool noIdentity)
    {
        await using var f = await Fixture.CreateAsync(); var row = f.Row("missing", false);
        var request = f.Request(row, false);
        if (noIdentity) request = request with { EventId = null };
        var decision = await f.Authority.TrySendAsync(request);
        Assert.False(decision.Allowed); Assert.Equal("canonical_dispatch_signal_missing", decision.Note);
    }

    [Fact]
    public async Task LegacyControllerTemporalFallbackRemainsAvailable()
    {
        await using var f = await Fixture.CreateAsync(); var row = f.Row("legacy", true);
        f.Db.Add(row); await f.Db.SaveChangesAsync();
        var decision = await f.Authority.TrySendAsync(f.Request(row, false) with { EventId = null, Source = MetaSendAuthoritySources.Controllers });
        Assert.False(decision.Allowed); Assert.Equal("meta_signal_event_already_sent", decision.Note);
        var different = await f.Authority.TrySendAsync(f.Request(f.Row("new", false), false) with { Source = MetaSendAuthoritySources.Controllers });
        Assert.True(different.Allowed); f.Authority.Complete(different, new MetaConversionsApiResult { Sent = false }, new MetaConversionsApiEventRequest());
    }

    [Fact]
    public async Task DispatcherCannotUseControllerNestedTokenWithoutDurableSource()
    {
        await using var f = await Fixture.CreateAsync();
        var request = f.Request(f.Row("missing-nested", false), false) with { Source = MetaSendAuthoritySources.Controllers };
        var controller = await f.Authority.TrySendAsync(request);
        Assert.True(controller.Allowed);
        var dispatcher = await f.Authority.TrySendAsync(request with {
            Source = MetaSendAuthoritySources.MetaSignalOutcomeDispatcherHostedService, ReservationToken = controller.ReservationToken });
        Assert.False(dispatcher.Allowed); Assert.Equal("canonical_dispatch_signal_missing", dispatcher.Note);
        f.Authority.Complete(controller, new MetaConversionsApiResult { Sent = false }, new MetaConversionsApiEventRequest());
    }

    [Fact]
    public async Task CompletedReservationTokenCannotReauthorizeDuplicate()
    {
        await using var f = await Fixture.CreateAsync();
        var request = f.Request(f.Row("nested", false), false) with { Source = MetaSendAuthoritySources.Controllers };
        var controller = await f.Authority.TrySendAsync(request);
        Assert.True(controller.Allowed);
        var nested = await f.Authority.TrySendAsync(request with { ReservationToken = controller.ReservationToken });
        Assert.True(nested.Allowed); Assert.Equal("allowed_nested", nested.Status);
        f.Authority.Complete(controller, new MetaConversionsApiResult { Sent = true }, new MetaConversionsApiEventRequest());
        var replay = await f.Authority.TrySendAsync(request with { ReservationToken = controller.ReservationToken });
        Assert.False(replay.Allowed);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public MasterAppDbContext Db { get; }
        public MetaSendAuthority Authority { get; }
        private readonly Guid owner = Guid.NewGuid(), lead = Guid.NewGuid();
        private readonly string prefix = Guid.NewGuid().ToString("N");
        private Fixture(SqliteConnection connection)
        {
            this.connection = connection;
            Db = new(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
            Authority = new(Db, NullLogger<MetaSendAuthority>.Instance);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False"); await connection.OpenAsync();
            var f = new Fixture(connection); await f.Db.Database.EnsureCreatedAsync(); return f;
        }
        public MetaSignalEvent Row(string id, bool sent) => new() { EventId = prefix + id, EventName = "Lead", MetaServerSent = sent,
            CommerceBusinessId = owner, LeadId = lead, SessionId = prefix, VisitorId = prefix, CreatedUtc = DateTime.UtcNow,
            MetaDeduplicationKey = prefix + ":" + id };
        public MetaSendAuthorityRequest Request(MetaSignalEvent row, bool explicitKey) => new() { EventType = "Lead", EventId = row.EventId,
            DeduplicationKey = explicitKey ? row.MetaDeduplicationKey : null, CommerceBusinessId = owner, LeadId = lead,
            SessionId = prefix, VisitorId = prefix, EventUtc = DateTime.UtcNow, Source = MetaSendAuthoritySources.MetaSignalOutcomeDispatcherHostedService };
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
