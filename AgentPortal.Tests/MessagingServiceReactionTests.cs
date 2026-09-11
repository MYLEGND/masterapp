using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Infrastructure.Moderation;
using Infrastructure.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;
using System.Threading;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class MessagingServiceTests
{
    [Fact]
    public void Reactions_CanonicalPaletteContainsOnlySupportedDistinctEmoji()
    {
        Assert.Equal(6, MessagingReactionOptions.Defaults.Count);
        Assert.Equal(MessagingReactionOptions.Defaults.Count, MessagingReactionOptions.Defaults.Distinct().Count());
        Assert.All(MessagingReactionOptions.Defaults, emoji => Assert.True(MessagingService.IsSupportedReactionEmoji(emoji)));
    }

    [Theory]
    [InlineData("👍", true)]
    [InlineData("❤️", true)]
    [InlineData("👩🏽‍💻", true)]
    [InlineData("🇭🇹", true)]
    [InlineData("1️⃣", true)]
    [InlineData("‼️", true)]
    [InlineData("", false)]
    [InlineData("hello", false)]
    [InlineData("<script>", false)]
    [InlineData("👍👍", false)]
    [InlineData("👍\n", false)]
    [InlineData("\u200D👍", false)]
    [InlineData("👍\u200D", false)]
    [InlineData("🏽", false)]
    public void Reactions_AcceptOneBoundedEmojiAndRejectTextOrMalformedSequence(string emoji, bool allowed)
        => Assert.Equal(allowed, MessagingService.IsSupportedReactionEmoji(emoji));

    [Fact]
    public async Task Reactions_SetReplayReplaceAndRemoveUseCanonicalMessageProjection()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var client = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var opened = await service.StartConversationAsync(new StartMessagingConversationCommand(
            agent, client.UserId, client.ParticipantType, InitialMessageBody: "Reaction target"));
        Assert.Equal(MessagingReactionOptions.Defaults, opened.Conversation!.ReactionOptions);
        var id = opened.Conversation.Id;
        var messageId = Assert.Single(opened.Conversation.Messages).Id;
        var first = await service.SetMessageReactionAsync(client, id, messageId, "👍");
        Assert.True(first.Succeeded, first.ErrorCode);
        Assert.Equal(new MessagingReactionSummary("👍", 1, true), Assert.Single(first.Value!.Reactions));
        var replay = await service.SetMessageReactionAsync(client, id, messageId, "👍");
        Assert.True(replay.Succeeded);
        Assert.Single(await db.MessageReactions.ToListAsync());
        Assert.Single(await db.MessagingAuditEntries.Where(x => x.Action == "MessageReactionChanged").ToListAsync());
        Assert.True((await service.SetMessageReactionAsync(agent, id, messageId, "👍")).Succeeded);
        var visible = Assert.Single((await service.GetConversationAsync(client, id)).Conversation!.Messages);
        Assert.Equal(new MessagingReactionSummary("👍", 2, true), Assert.Single(visible.Reactions));
        var replaced = await service.SetMessageReactionAsync(client, id, messageId, "❤️");
        Assert.True(replaced.Succeeded);
        Assert.Contains(replaced.Value!.Reactions, x => x.Emoji == "👍" && x.Count == 1 && !x.ReactedByCurrentActor);
        Assert.Contains(replaced.Value.Reactions, x => x.Emoji == "❤️" && x.Count == 1 && x.ReactedByCurrentActor);
        var removed = await service.SetMessageReactionAsync(client, id, messageId, null);
        Assert.True(removed.Succeeded);
        Assert.Equal(new MessagingReactionSummary("👍", 1, false), Assert.Single(removed.Value!.Reactions));
        var auditCount = await db.MessagingAuditEntries.CountAsync();
        Assert.True((await service.SetMessageReactionAsync(client, id, messageId, null)).Succeeded);
        Assert.Equal(auditCount, await db.MessagingAuditEntries.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reactions_PublishCommittedChangeOnlyAndExcludeOutsidersFromRefresh(bool cancelRefresh)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var client = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var opened = await CreateService(db).StartConversationAsync(new StartMessagingConversationCommand(
            agent, client.UserId, client.ParticipantType, InitialMessageBody: "Realtime reaction"));
        var conversationId = opened.Conversation!.Id;
        var messageId = Assert.Single(opened.Conversation.Messages).Id;
        var published = new System.Collections.Generic.List<MessagingRealtimeEvent>();
        var realtime = new Mock<IMessagingRealtimePublisher>();
        realtime.Setup(x => x.PublishAsync(It.IsAny<MessagingRealtimeEvent>(), It.IsAny<System.Threading.CancellationToken>()))
            .Callback<MessagingRealtimeEvent, System.Threading.CancellationToken>((value, _) =>
            {
                Assert.Single(db.MessageReactions.AsNoTracking().ToList());
                published.Add(value);
            }).Returns(() => cancelRefresh ? Task.FromException(new OperationCanceledException()) : Task.CompletedTask);
        var images = new MessagingProfileImageResolver(db, NullLogger<MessagingProfileImageResolver>.Instance);
        var service = new MessagingService(db, NullLogger<MessagingService>.Instance,
            new CommunityTextModerationService(new ConfigurationBuilder().Build()), images,
            new ControlledResourceAccessService(db), new TestTranslationService(),
            new NotificationEngine(db, images, new NoopNotificationRealtimePublisher(), new ApplePushDeliverySignal(),
                NullLogger<NotificationEngine>.Instance), realtime: realtime.Object);
        Assert.True((await service.SetMessageReactionAsync(client, conversationId, messageId, "👍")).Succeeded);
        Assert.True((await service.SetMessageReactionAsync(client, conversationId, messageId, "👍")).Succeeded);
        var refresh = Assert.Single(published);
        Assert.Equal("conversationUpdated", refresh.EventType);
        Assert.Equal(conversationId, refresh.ConversationId);
        Assert.Equal(2, refresh.Recipients.Count);
        Assert.All(refresh.Recipients, recipient => Assert.Contains(recipient.UserId, new[] { agent.UserId, client.UserId }));
    }

    [Fact]
    public async Task Reactions_RejectOutsiderWrongConversationDeletedClosedAndInvalidEmoji()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var client = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var opened = await service.StartConversationAsync(new StartMessagingConversationCommand(
            agent, client.UserId, client.ParticipantType, InitialMessageBody: "Authorized message"));
        var id = opened.Conversation!.Id;
        var messageId = Assert.Single(opened.Conversation.Messages).Id;
        Assert.False((await service.SetMessageReactionAsync(new MessagingActor("outsider", MessagingParticipantTypes.Client), id, messageId, "👍")).Succeeded);
        Assert.False((await service.SetMessageReactionAsync(client, Guid.NewGuid(), messageId, "👍")).Succeeded);
        Assert.False((await service.SetMessageReactionAsync(client, id, Guid.NewGuid(), "👍")).Succeeded);
        Assert.False((await service.SetMessageReactionAsync(client, id, messageId, "not emoji")).Succeeded);
        var conversation = await db.MessageConversations.SingleAsync(x => x.Id == id);
        conversation.IsClosed = true;
        await db.SaveChangesAsync();
        Assert.Equal("MESSAGING_CONVERSATION_CLOSED", (await service.SetMessageReactionAsync(client, id, messageId, "👍")).ErrorCode);
        conversation.IsClosed = false;
        await db.SaveChangesAsync();
        Assert.True((await service.SetMessageReactionAsync(client, id, messageId, "👍")).Succeeded);
        Assert.True((await service.DeleteMessageAsync(new DeleteMessagingMessageCommand(agent, id, messageId))).Succeeded);
        Assert.False((await service.SetMessageReactionAsync(client, id, messageId, "❤️")).Succeeded);
        Assert.Empty((await service.GetConversationAsync(client, id)).Conversation!.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reactions_RetryRolledBackOrUncertainCommitWithoutLosingStateOrDuplicatingAudit(bool failAfterCommit)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var fault = new ReactionCommitFault { FailAfterCommit = failAfterCommit };
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection).ReplaceService<IExecutionStrategyFactory, ReactionRetryStrategyFactory>()
            .AddInterceptors(fault).Options);
        await db.Database.EnsureCreatedAsync();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var client = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var opened = await service.StartConversationAsync(new StartMessagingConversationCommand(
            agent, client.UserId, client.ParticipantType, InitialMessageBody: "Retry transaction"));
        Assert.True(opened.Succeeded, opened.ErrorCode);
        var id = opened.Conversation!.Id;
        var messageId = Assert.Single(opened.Conversation.Messages).Id;
        fault.Armed = true;
        var result = await service.SetMessageReactionAsync(client, id, messageId, "👍");
        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.Equal(1, fault.Failures);
        Assert.Equal(new MessagingReactionSummary("👍", 1, true), Assert.Single(result.Value!.Reactions));
        Assert.Single(await db.MessageReactions.AsNoTracking().ToListAsync());
        Assert.Single(await db.MessagingAuditEntries.Where(x => x.Action == "MessageReactionChanged").ToListAsync());
    }

    public sealed class ReactionRetryStrategyFactory(ExecutionStrategyDependencies dependencies) : IExecutionStrategyFactory
    {
        public IExecutionStrategy Create() => new ReactionRetryStrategy(dependencies);
    }

    private sealed class ReactionRetryStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, 2, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TimeoutException;
    }

    private sealed class ReactionCommitFault : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool FailAfterCommit { get; init; }
        public int Failures { get; private set; }
        private void FailOnce(bool afterCommit)
        {
            if (!Armed || Failures != 0 || FailAfterCommit != afterCommit) return;
            Failures++;
            throw new TimeoutException("Controlled transaction acknowledgment fault.");
        }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            FailOnce(false);
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            FailOnce(true);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Reactions_SqliteExecutesGroupedProjectionAndEnforcesTypedActorUniqueness()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var client = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var opened = await service.StartConversationAsync(new StartMessagingConversationCommand(
            agent, client.UserId, client.ParticipantType, InitialMessageBody: "SQL reaction"));
        Assert.True(opened.Succeeded, opened.ErrorCode);
        var id = opened.Conversation!.Id;
        var messageId = Assert.Single(opened.Conversation.Messages).Id;
        var set = await service.SetMessageReactionAsync(client, id, messageId, "👍");
        Assert.True(set.Succeeded, set.ErrorCode);
        Assert.Single(Assert.Single((await service.GetConversationAsync(client, id)).Conversation!.Messages).Reactions);
        var stored = await db.MessageReactions.AsNoTracking().SingleAsync();
        db.ChangeTracker.Clear();
        db.MessageReactions.Add(new MessageReaction { InternalMessageId = messageId, ActorProfileId = stored.ActorProfileId,
            ParticipantType = stored.ParticipantType, Emoji = "❤️", UpdatedUtc = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        // A distinct participant type is a separate identity, even if profile GUIDs coincide.
        db.MessageReactions.Add(new MessageReaction { InternalMessageId = messageId, ActorProfileId = stored.ActorProfileId,
            ParticipantType = MessagingParticipantTypes.Agent, Emoji = "❤️", UpdatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var projected = Assert.Single((await service.GetConversationAsync(client, id)).Conversation!.Messages);
        Assert.Contains(projected.Reactions, x => x.Emoji == "❤️" && !x.ReactedByCurrentActor);
        Assert.Equal(2, projected.Reactions.Count);
    }
}
