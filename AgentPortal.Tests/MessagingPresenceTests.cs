using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Billing;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Shared.Calling;
using Shared.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class MessagingServiceTests
{
    [Fact]
    public async Task Presence_HubConnectionRegistersExistingCallRoutingWithoutAccessingPresenceStorage()
    {
        var connectionId = "presence-hub-" + Guid.NewGuid().ToString("N");
        var actorId = "presence-actor-" + Guid.NewGuid().ToString("N");
        var http = new DefaultHttpContext();
        var httpFeature = new Mock<IHttpContextFeature>();
        httpFeature.SetupGet(feature => feature.HttpContext).Returns(http);
        var features = new FeatureCollection();
        features.Set(httpFeature.Object);
        var context = new Mock<HubCallerContext>();
        context.SetupGet(caller => caller.ConnectionId).Returns(connectionId);
        context.SetupGet(caller => caller.ConnectionAborted).Returns(CancellationToken.None);
        context.SetupGet(caller => caller.Features).Returns(features);
        var actors = new Mock<IMessagingActorContextResolver>(MockBehavior.Strict);
        actors.Setup(resolver => resolver.ResolveAsync(http, CancellationToken.None))
            .ReturnsAsync(((string UserId, string ParticipantType)?)(actorId, "Client"));
        var group = MessagingHub.GroupName(actorId, "Client");
        var groups = new Mock<IGroupManager>(MockBehavior.Strict);
        groups.Setup(manager => manager.AddToGroupAsync(connectionId, group, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var presence = new Mock<IMessagingPresenceAuthority>(MockBehavior.Strict);
        using var hub = new MessagingHub(actors.Object, presence: presence.Object)
        {
            Context = context.Object, Groups = groups.Object
        };
        try
        {
            await hub.OnConnectedAsync();
            Assert.Contains(group, LegendCallConnections.Groups);
            groups.Verify(manager => manager.AddToGroupAsync(connectionId, group, It.IsAny<CancellationToken>()), Times.Once);
            presence.VerifyNoOtherCalls();
        }
        finally { LegendCallConnections.Remove(connectionId); }
    }

    [Fact]
    public async Task Presence_OnlyAuthorizedTypedContactsAndOpenConversationScopesAreProjected()
    {
        await using var fixture = await PresenceFixture.CreateAsync();
        var outsider = new ClientProfile
        {
            ClientUserId = "presence-outsider", ExternalIdentityObjectId = "presence-outsider",
            FirstName = "Outside", LastName = "Member", Email = "outside@example.test",
            CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}"
        };
        fixture.Db.ClientProfiles.Add(outsider);
        GrantClientAppAccess(fixture.Db, outsider);
        var privateGroup = PresenceGroup("presence-outsider");
        privateGroup.Participants.Remove(privateGroup.Participants.Single(row => row.UserId == "agent-1"));
        var closed = PresenceGroup("client-1");
        closed.IsClosed = true;
        var founder = PresenceGroup("client-1");
        founder.ConversationType = MessagingConversationTypes.Assistant;
        founder.Purpose = MessagingConversationPurposes.FounderAI;
        fixture.Db.MessageConversations.AddRange(privateGroup, closed, founder);
        await fixture.Db.SaveChangesAsync();
        await fixture.Service.TouchConnectionAsync("client-1", "Client", "visible-client", CancellationToken.None);
        await fixture.Service.TouchConnectionAsync("presence-outsider", "Client", "outside-client", CancellationToken.None);

        var result = await fixture.Service.ReadPresenceAsync("agent-1", "Agent", new(
            [new("client-1", "Client"), new("presence-outsider", "Client"), new("missing", "Agent"), new("client-1", "Agent")],
            [fixture.GroupId, privateGroup.Id, closed.Id, founder.Id, Guid.NewGuid()]), CancellationToken.None);

        var person = Assert.Single(result.Participants);
        Assert.Equal("client-1", person.UserId);
        Assert.Equal("Client", person.ParticipantType);
        Assert.True(person.IsOnline);
        var group = Assert.Single(result.Conversations);
        Assert.Equal(fixture.GroupId, group.ConversationId);
        Assert.True(group.IsOnline);
        Assert.InRange(result.RefreshSeconds, 1, 90);
        Assert.Equal(0, fixture.Translation.DetectionCallCount);
        Assert.Equal(0, fixture.Translation.TranslationCallCount);
    }

    [Fact]
    public async Task Presence_SharedExternalIdentityCannotBorrowAnotherRoleOrReassignAConnection()
    {
        await using var fixture = await PresenceFixture.CreateAsync();
        fixture.Db.AgentProfiles.Add(new() { AgentUserId = "dual-presence", AgentUpn = "dual@example.test", FullName = "Dual Agent" });
        var client = new ClientProfile
        {
            ClientUserId = "dual-client", ExternalIdentityObjectId = "dual-presence",
            FirstName = "Dual", LastName = "Client", Email = "dual-client@example.test",
            CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}"
        };
        fixture.Db.ClientProfiles.Add(client);
        GrantClientAppAccess(fixture.Db, client);
        fixture.Db.AgentClients.Add(new() { AgentUserId = "agent-1", AgentUpn = "agent.one@mylegnd.com", ClientUserId = "dual-client" });
        await fixture.Db.SaveChangesAsync();
        await fixture.Service.TouchConnectionAsync("dual-presence", "Client", "dual-connection", CancellationToken.None);

        var result = await fixture.Service.ReadPresenceAsync("agent-1", "Agent", new(
            [new("dual-presence", "Agent"), new("dual-client", "Client")]), CancellationToken.None);
        Assert.Equal(2, result.Participants.Length);
        Assert.False(Assert.Single(result.Participants, row => row.ParticipantType == "Agent").IsOnline);
        Assert.True(Assert.Single(result.Participants, row => row.ParticipantType == "Client").IsOnline);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.TouchConnectionAsync(
            "dual-presence", "Agent", "dual-connection", CancellationToken.None));
        var lease = await fixture.Db.MessagingConnectionLeases.AsNoTracking().SingleAsync();
        Assert.Equal(client.Id, lease.ProfileId);
        Assert.Equal("Client", lease.ParticipantType);
    }

    [Fact]
    public async Task Presence_OneDeviceDisconnectDoesNotOfflineAnotherAndOwnLeaseDoesNotOnlineAGroup()
    {
        await using var fixture = await PresenceFixture.CreateAsync();
        await fixture.Service.TouchConnectionAsync("client-1", "Client", "phone", CancellationToken.None);
        await fixture.Service.TouchConnectionAsync("client-1", "Client", "browser", CancellationToken.None);
        await fixture.Service.TouchConnectionAsync("agent-1", "Agent", "observer", CancellationToken.None);
        await fixture.Service.RemoveConnectionAsync("phone", CancellationToken.None);
        await fixture.Service.RemoveConnectionAsync("phone", CancellationToken.None);
        var retained = await fixture.ReadAsync();
        Assert.True(Assert.Single(retained.Participants).IsOnline);
        Assert.True(Assert.Single(retained.Conversations).IsOnline);

        await fixture.Service.RemoveConnectionAsync("browser", CancellationToken.None);
        var disconnected = await fixture.ReadAsync();
        Assert.False(Assert.Single(disconnected.Participants).IsOnline);
        Assert.False(Assert.Single(disconnected.Conversations).IsOnline);
        Assert.Equal("observer", (await fixture.Db.MessagingConnectionLeases.AsNoTracking().SingleAsync()).ConnectionId);
    }

    [Fact]
    public async Task Presence_ExpiryControlsObservationBeforeBoundedCleanupRuns()
    {
        await using var fixture = await PresenceFixture.CreateAsync();
        var client = await fixture.Db.ClientProfiles.SingleAsync();
        fixture.Db.MessagingConnectionLeases.AddRange(Enumerable.Range(0, 101).Select(index => new MessagingConnectionLease
        {
            ConnectionId = "expired-" + index, ProfileId = client.Id, ParticipantType = "Client",
            ExpiresUtc = DateTime.UtcNow.AddMinutes(-10)
        }));
        await fixture.Db.SaveChangesAsync();
        Assert.False(Assert.Single((await fixture.ReadAsync()).Participants).IsOnline);
        await fixture.Service.TouchConnectionAsync("agent-1", "Agent", "fresh-observer", CancellationToken.None);
        Assert.Equal(1, await fixture.Db.MessagingConnectionLeases.AsNoTracking().CountAsync(row => row.ConnectionId.StartsWith("expired-")));
        Assert.False(Assert.Single((await fixture.ReadAsync()).Conversations).IsOnline);
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("closed")]
    [InlineData("target_revoked")]
    public async Task Presence_RevocationRemovesVisibilityEvenWhileThePriorLeaseIsFresh(string revocation)
    {
        await using var fixture = await PresenceFixture.CreateAsync();
        await fixture.Service.TouchConnectionAsync("client-1", "Client", "still-connected", CancellationToken.None);
        Assert.True(Assert.Single((await fixture.ReadAsync()).Conversations).IsOnline);
        if (revocation == "membership")
            await fixture.Db.MessageConversationParticipants.Where(row => row.ConversationId == fixture.GroupId && row.UserId == "agent-1")
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
        else if (revocation == "closed")
            await fixture.Db.MessageConversations.Where(row => row.Id == fixture.GroupId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsClosed, true));
        else
            await fixture.Db.ClientEntitlements.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ClientEntitlementStatus.Revoked));

        var result = await fixture.ReadAsync();
        Assert.Empty(result.Conversations);
        if (revocation == "target_revoked") Assert.Empty(result.Participants);
        Assert.True(await fixture.Db.MessagingConnectionLeases.AsNoTracking().AnyAsync(row => row.ExpiresUtc > DateTime.UtcNow));
    }

    [Fact]
    public async Task Presence_RevokedActorCannotReadOrExtendAnExistingLease()
    {
        await using var fixture = await PresenceFixture.CreateAsync();
        await fixture.Service.TouchConnectionAsync("agent-1", "Agent", "revoked-observer", CancellationToken.None);
        var before = await fixture.Db.MessagingConnectionLeases.AsNoTracking().SingleAsync();
        await fixture.Db.AgentProfiles.Where(row => row.AgentUserId == "agent-1")
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.ReadAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.TouchConnectionAsync(
            "agent-1", "Agent", "revoked-observer", CancellationToken.None));
        Assert.Equal(before.ExpiresUtc, (await fixture.Db.MessagingConnectionLeases.AsNoTracking().SingleAsync()).ExpiresUtc);
    }

    [Fact]
    public async Task Presence_ExpiredLeaseRefreshedOnAnotherHostSurvivesAnAlreadyStartedCollector()
    {
        await using var fixture = await PresenceFixture.CreateAsync();
        await fixture.AddExpiredClientLeaseAsync("refresh-race");
        var pause = new PresenceDeletePause();
        await using var collectorDb = fixture.Sibling(pause);
        var collecting = CreateService(collectorDb).TouchConnectionAsync("agent-1", "Agent", "collector", CancellationToken.None);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var refreshDb = fixture.Sibling();
            await CreateService(refreshDb).TouchConnectionAsync("client-1", "Client", "refresh-race", CancellationToken.None);
        }
        finally { pause.Release.TrySetResult(); }
        await collecting;
        Assert.True(Assert.Single((await fixture.ReadAsync()).Participants).IsOnline);
        Assert.True(await fixture.Db.MessagingConnectionLeases.AsNoTracking().AnyAsync(row => row.ConnectionId == "refresh-race"));
    }

    [Fact]
    public async Task Presence_TwoHostCollectorsDoNotTurnAlreadyDeletedExpiredRowsIntoHeartbeatFailures()
    {
        await using var fixture = await PresenceFixture.CreateAsync();
        await fixture.AddExpiredClientLeaseAsync("cleanup-race");
        var pause = new PresenceDeletePause();
        await using var firstDb = fixture.Sibling(pause);
        var first = CreateService(firstDb).TouchConnectionAsync("agent-1", "Agent", "first-host", CancellationToken.None);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var secondDb = fixture.Sibling();
            await CreateService(secondDb).TouchConnectionAsync("client-1", "Client", "second-host", CancellationToken.None);
        }
        finally { pause.Release.TrySetResult(); }
        await first;
        Assert.Equal(2, await fixture.Db.MessagingConnectionLeases.AsNoTracking().CountAsync());
        Assert.True(Assert.Single((await fixture.ReadAsync()).Participants).IsOnline);
    }

    [Fact]
    public async Task Presence_StorageFailurePropagatesInsteadOfFabricatingOfflineResults()
    {
        await using var fixture = await PresenceFixture.CreateAsync();
        await fixture.Service.TouchConnectionAsync("client-1", "Client", "online-before-outage", CancellationToken.None);
        await using var failedDb = fixture.Sibling(new PresenceReadFailure());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(failedDb).ReadPresenceAsync(
            "agent-1", "Agent", new([new("client-1", "Client")], [fixture.GroupId]), CancellationToken.None));
        Assert.Equal("presence_store_unavailable", error.Message);
        Assert.True(Assert.Single((await fixture.ReadAsync()).Participants).IsOnline);
    }

    [Fact]
    public async Task Presence_RejectsOversizedQueriesAndHonorsCancellation()
    {
        await using var fixture = await PresenceFixture.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ReadPresenceAsync("agent-1", "Agent",
            new(Enumerable.Repeat(new MessagingPresenceParticipant("client-1", "Client"), 51).ToArray()), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ReadPresenceAsync("agent-1", "Agent",
            new(ConversationIds: Enumerable.Repeat(fixture.GroupId, 51).ToArray()), CancellationToken.None));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.TouchConnectionAsync(
            "agent-1", "Agent", "must-not-persist", canceled.Token));
        Assert.Empty(await fixture.Db.MessagingConnectionLeases.AsNoTracking().ToArrayAsync());
    }

    private static MessageConversation PresenceGroup(string clientUserId) => new()
    {
        Id = Guid.NewGuid(), ConversationType = MessagingConversationTypes.Group,
        CreatedByUserId = "agent-1", OwnerUserId = "agent-1", OwnerParticipantType = "Agent",
        Subject = "Presence scope", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
        Participants = [
            new() { Id = Guid.NewGuid(), UserId = "agent-1", ParticipantType = "Agent", IsActive = true, JoinedUtc = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), UserId = clientUserId, ParticipantType = "Client", IsActive = true, JoinedUtc = DateTime.UtcNow }
        ]
    };

    private sealed class PresenceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _keeper;
        private readonly string _connectionString;
        public MasterAppDbContext Db { get; }
        public Guid GroupId { get; }
        public TestTranslationService Translation { get; } = new();
        public MessagingService Service { get; }

        private PresenceFixture(SqliteConnection keeper, string connectionString, MasterAppDbContext db, Guid groupId)
        {
            _keeper = keeper; _connectionString = connectionString; Db = db; GroupId = groupId;
            Service = CreateService(Db, Translation);
        }

        public static async Task<PresenceFixture> CreateAsync()
        {
            var connectionString = $"Data Source=presence-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var keeper = new SqliteConnection(connectionString);
            await keeper.OpenAsync();
            var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connectionString).Options);
            await db.Database.EnsureCreatedAsync();
            await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
            var group = PresenceGroup("client-1");
            db.MessageConversations.Add(group);
            await db.SaveChangesAsync();
            return new(keeper, connectionString, db, group.Id);
        }

        public MasterAppDbContext Sibling(DbCommandInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(_connectionString);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new(options.Options);
        }

        public Task<MessagingPresenceResult> ReadAsync() => Service.ReadPresenceAsync("agent-1", "Agent",
            new([new("client-1", "Client")], [GroupId]), CancellationToken.None);

        public async Task AddExpiredClientLeaseAsync(string connectionId)
        {
            Db.MessagingConnectionLeases.Add(new()
            {
                ConnectionId = connectionId, ProfileId = (await Db.ClientProfiles.SingleAsync()).Id,
                ParticipantType = "Client", ExpiresUtc = DateTime.UtcNow.AddMinutes(-5)
            });
            await Db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _keeper.DisposeAsync();
        }
    }

    private sealed class PresenceDeletePause : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _paused;
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DELETE", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("MessagingConnectionLeases", StringComparison.Ordinal) && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private sealed class PresenceReadFailure : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("MessagingConnectionLeases", StringComparison.Ordinal))
                throw new InvalidOperationException("presence_store_unavailable");
            return ValueTask.FromResult(result);
        }
    }
}
