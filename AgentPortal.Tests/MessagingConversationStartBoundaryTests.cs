using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Mobile;
using Domain.Entities;
using Domain.Messaging;
using Domain.Social;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Infrastructure.Mobile;
using Infrastructure.Moderation;
using Infrastructure.Social;
using Infrastructure.Social.OpenMusic;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shared.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class MessagingServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyCallStart_ReturnsAuthorizedMetadataWithoutHydratingOrTranslatingHistory(bool existing)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var reads = new StartHistoryReadProbe();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection).AddInterceptors(reads).Options);
        await db.Database.EnsureCreatedAsync();
        Guid? existingId = existing ? await SeedProjectionHistoryAsync(db, 3) : null;
        if (!existing) await SeedAgentAndClientAsync(db, true, false);
        await GrantStartReaderTranslationAsync(db);
        var translation = new Mock<ITranslationService>(MockBehavior.Strict);
        var service = CreateService(db, translation.Object);
        var messagesBefore = await db.InternalMessages.CountAsync();
        var notificationsBefore = await db.MobileActivityNotifications.CountAsync();
        db.ChangeTracker.Clear();
        reads.Armed = true;

        var result = await service.StartConversationAsync(new StartMessagingConversationCommand(
            new("client-1", "Client"), "agent-1", "Agent", IncludeMessages: false));

        Assert.True(result.Succeeded, result.ErrorCode + ": " + result.ErrorMessage);
        var detail = Assert.IsType<MessagingConversationDetail>(result.Conversation);
        if (existingId.HasValue) Assert.Equal(existingId, detail.Id);
        Assert.Equal("Agent One", detail.DisplayTitle);
        Assert.Equal(2, detail.Participants.Count);
        Assert.Contains(detail.Participants, p => p.UserId == "agent-1" && p.ParticipantType == "Agent");
        Assert.Empty(detail.Messages);
        Assert.Equal(existing, detail.HasOlderMessages);
        Assert.Equal(0, reads.HistoricalBodyReads);
        Assert.True(reads.TotalReads > 0, "The metadata path must execute the real relational authorization/projection.");
        translation.VerifyNoOtherCalls();
        Assert.Equal(messagesBefore, await db.InternalMessages.CountAsync());
        Assert.Equal(notificationsBefore, await db.MobileActivityNotifications.CountAsync());
        Assert.Empty(await db.MessageTranslations.ToListAsync());
    }

    [Fact]
    public async Task EmptyCallStart_DefaultNavigationStillReturnsTranslatedHistory()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var id = await SeedProjectionHistoryAsync(db, 2);
        await GrantStartReaderTranslationAsync(db);
        var translation = new DeferredTranslationProbe();
        var result = await CreateService(db, translation).StartConversationAsync(
            new StartMessagingConversationCommand(new("client-1", "Client"), "agent-1", "Agent"));

        Assert.True(result.Succeeded, result.ErrorCode + ": " + result.ErrorMessage);
        Assert.Equal(id, result.Conversation!.Id);
        Assert.Equal(2, result.Conversation.Messages.Count);
        Assert.Equal(2, translation.Calls);
        Assert.All(result.Conversation.Messages, message =>
        {
            Assert.Equal("Bonjou.", message.Body);
            Assert.NotNull(message.Translation);
            Assert.StartsWith("Message ", message.OriginalBody);
        });
        Assert.Equal(2, await db.MessageTranslations.CountAsync());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("wrong_type")]
    [InlineData("revoked_actor")]
    public async Task EmptyCallStart_CannotBypassRecipientOrActorAuthority(string denial)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedProjectionHistoryAsync(db, 1);
        if (denial == "revoked_actor")
        {
            (await db.AgentProfiles.SingleAsync(p => p.AgentUserId == "agent-1")).IsActive = false;
            await db.SaveChangesAsync();
        }
        var translation = new Mock<ITranslationService>(MockBehavior.Strict);
        var result = await CreateService(db, translation.Object).StartConversationAsync(new StartMessagingConversationCommand(
            new("agent-1", "Agent"), denial == "unknown" ? "outsider" : "client-1",
            denial == "wrong_type" ? "Agent" : "Client", IncludeMessages: false));

        Assert.False(result.Succeeded);
        Assert.Null(result.Conversation);
        Assert.Equal(denial == "revoked_actor" ? "MESSAGING_ACTOR_INVALID" : "MESSAGING_RECIPIENT_FORBIDDEN", result.ErrorCode);
        Assert.Equal(1, await db.MessageConversations.CountAsync());
        Assert.Equal(1, await db.InternalMessages.CountAsync());
        translation.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(null, false)]
    [InlineData(" \t", true)]
    [InlineData("New message", false)]
    public async Task EmptyCallStart_WebPublishesOnlyActualNewMessageIdentity(string? body, bool includeMessages)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var id = await SeedProjectionHistoryAsync(db, 2);
        var oldIds = await db.InternalMessages.Select(message => message.Id).ToArrayAsync();
        var service = CreateService(db);
        var actor = new MessagingActor("agent-1", "Agent");
        var recipient = (await service.GetAuthorizedParticipantAsync(actor, "client-1", "Client")).Recipient!;
        var keys = new MessagingContactKeyProtector(new EphemeralDataProtectionProvider());
        var events = new List<MessagingRealtimeEvent>();
        var publisher = new Mock<IMessagingRealtimePublisher>(MockBehavior.Strict);
        publisher.Setup(p => p.PublishAsync(It.IsAny<MessagingRealtimeEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MessagingRealtimeEvent, CancellationToken>((value, _) => events.Add(value))
            .Returns(Task.CompletedTask);
        var actors = new Mock<IMessagingActorContextResolver>(MockBehavior.Strict);
        actors.Setup(a => a.ResolveAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string UserId, string ParticipantType)?)(actor.UserId, actor.ParticipantType));
        using var controller = new MessagingController(service, Mock.Of<IMessageAttachmentStorage>(), publisher.Object,
            Mock.Of<IMessagingProfileImageResolver>(), keys, actors.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var result = Assert.IsType<OkObjectResult>(await controller.Start(new StartMessagingConversationRequest(
            keys.Protect(actor, recipient), null, body, "start-boundary-id", IncludeMessages: includeMessages)));
        var detail = Assert.IsType<MessagingConversationResult>(result.Value).Conversation!;
        Assert.Equal(id, detail.Id);
        var notification = Assert.Single(events);
        Assert.Equal(id, notification.ConversationId);
        Assert.Equal(2, notification.Recipients.Count);
        var hasNewMessage = !string.IsNullOrWhiteSpace(body);
        if (hasNewMessage)
        {
            var acknowledged = Assert.Single(detail.Messages);
            Assert.Equal(body, acknowledged.Body);
            Assert.DoesNotContain(acknowledged.Id, oldIds);
            Assert.Equal("messageReceived", notification.EventType);
            Assert.Equal(acknowledged.Id, notification.MessageId);
        }
        else
        {
            Assert.Equal(includeMessages ? 2 : 0, detail.Messages.Count);
            Assert.Equal("conversationUpdated", notification.EventType);
            Assert.Null(notification.MessageId);
        }
        Assert.Equal(hasNewMessage ? 3 : 2, await db.InternalMessages.CountAsync());
        Assert.Equal(hasNewMessage ? 1 : 0, await db.MobileActivityNotifications.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyCallStart_SharedContentStillReturnsItsActualAcknowledgement(bool existing)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        if (existing) await SeedProjectionHistoryAsync(db, 1);
        else await SeedAgentAndClientAsync(db, true, false);
        var author = await db.AgentProfiles.SingleAsync(p => p.AgentUserId == "agent-1");
        var post = new SocialPost
        {
            Id = Guid.NewGuid(), AuthorUserId = author.AgentUserId, AuthorParticipantType = "Agent",
            AuthorProfileId = author.Id, ContentType = SocialPostContentTypes.Post, Body = "Shared note",
            PublicationState = SocialPostPublicationStates.Published, PostedUtc = DateTime.UtcNow
        };
        db.SocialPosts.Add(post);
        await db.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var social = new SocialFeedService(db, Mock.Of<ISocialMediaStorage>(), new CuratedOpenMusicCatalog(), cache,
            new CommunityTextModerationService(new ConfigurationBuilder().Build()));
        var result = await CreateService(db, social: social).StartConversationAsync(new StartMessagingConversationCommand(
            new("agent-1", "Agent"), "client-1", "Client", SharedPostId: post.Id, IncludeMessages: false));

        Assert.True(result.Succeeded, result.ErrorCode + ": " + result.ErrorMessage);
        var message = Assert.Single(result.Conversation!.Messages);
        Assert.Equal(post.Id, message.SharedContent!.SourcePostId);
        Assert.Equal(post.Id, (await db.InternalMessages.SingleAsync(row => row.Id == message.Id)).SharedSocialPostId);
        Assert.Equal(existing ? 2 : 1, await db.InternalMessages.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyCallStart_MobileForwardsExplicitProjectionChoiceThroughExistingAuthority(bool includeMessages)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var id = await SeedProjectionHistoryAsync(db, 2);
        await GrantStartReaderTranslationAsync(db);
        var client = await db.ClientProfiles.SingleAsync(p => p.ClientUserId == "client-1");
        var resolved = new MobileResolvedActor(new("client-1", "Client"), client.Id, "Client");
        var actors = new Mock<IMobileActorResolver>(MockBehavior.Strict);
        actors.Setup(a => a.ResolveAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MobileActorResolution(true, null, null, [resolved], resolved, false));
        var translation = new DeferredTranslationProbe();
        var controller = new MobileMessagingController(actors.Object, CreateService(db, translation),
            Mock.Of<IMessageAttachmentStorage>(), Mock.Of<IMessagingRealtimePublisher>(),
            new MessagingProfileImageResolver(db, NullLogger<MessagingProfileImageResolver>.Instance), new ControlledResourceAccessService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var response = Assert.IsType<OkObjectResult>(await controller.StartConversation(
            new MobileStartConversationRequest("agent-1", "Agent", null, IncludeMessages: includeMessages), CancellationToken.None));
        var detail = Assert.IsType<MobileConversationDetailDto>(response.Value);
        Assert.Equal(id, detail.Id);
        Assert.Equal(includeMessages ? 2 : 0, detail.Messages.Count);
        Assert.Equal(includeMessages ? 2 : 0, translation.Calls);
        Assert.Equal(2, await db.InternalMessages.CountAsync());
        Assert.True(includeMessages || detail.HasOlderMessages);
    }

    private static async Task GrantStartReaderTranslationAsync(MasterAppDbContext db)
    {
        var profile = await db.ClientProfiles.SingleAsync(p => p.ClientUserId == "client-1");
        db.ControlledResourceGrants.Add(new ControlledResourceGrant
        {
            UserId = "client-1", ParticipantType = "Client", ResourceType = ControlledResourceTypes.LanguageTranslation,
            IsActive = true, GrantedUtc = DateTime.UtcNow, GrantedByUserId = "zac-founder-oid"
        });
        db.MobileProfileSettings.Add(new MobileProfileSettings
        {
            ProfileId = profile.Id, ParticipantType = "Client", PreferredCommunicationLanguage = "ht"
        });
        await db.SaveChangesAsync();
    }

    private sealed class StartHistoryReadProbe : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public int TotalReads { get; private set; }
        public int HistoricalBodyReads { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                TotalReads++;
                if (command.CommandText.Contains("InternalMessages", StringComparison.Ordinal) &&
                    command.CommandText.Contains("\"Body\"", StringComparison.Ordinal)) HistoricalBodyReads++;
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
