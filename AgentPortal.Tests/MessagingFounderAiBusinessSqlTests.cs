using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Shared.Workspaces;
using Infrastructure.Businesses;
using Infrastructure.WebsiteEditing;
using Domain.Social;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class MessagingServiceTests
{
    [PersistentAssistantFact]
    public async Task BusinessHistoryIsScopedProviderBoundAndContinuousAcrossServiceInstances()
    {
        var connection = Environment.GetEnvironmentVariable("MASTERAPP_LOCAL_DB_CONNECTION") ?? throw new InvalidOperationException("Use the persistent local database owner.");
        var target = new SqlConnectionStringBuilder(connection);
        Assert.Equal("127.0.0.1,14339", target.DataSource); Assert.Equal("MasterAppLocalTest", target.InitialCatalog);
        await using (var sql = new SqlConnection(connection))
        {
            await sql.OpenAsync(); await using var command = sql.CreateCommand();
            command.CommandText = "SELECT CONVERT(nvarchar(100),value) FROM sys.extended_properties WHERE class=0 AND name=N'LegendLocalTestIdentity'";
            Assert.Equal(Environment.GetEnvironmentVariable("LEGEND_TEST_DB_IDENTITY"), await command.ExecuteScalarAsync());
        }
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlServer(connection).Options;
        await using var db = new MasterAppDbContext(options);
        var actorId = Guid.NewGuid().ToString("D");
        var foreignId = Guid.NewGuid().ToString("D");
        var actor = new MessagingActor(actorId, MessagingParticipantTypes.Agent);
        var agent = new AgentProfile { Id = Guid.NewGuid(), AgentUserId = actorId, AgentUpn = "synthetic@local-test.invalid", FullName = "Synthetic assistant", IsActive = true };
        var foreign = new AgentProfile { Id = Guid.NewGuid(), AgentUserId = foreignId, AgentUpn = "synthetic-other@local-test.invalid", FullName = "Synthetic other", IsActive = true };
        var tracking = new AgentTrackingProfile { AgentUserId = actorId, AgentUpn = agent.AgentUpn, Slug = "synthetic-" + Guid.NewGuid().ToString("N") };
        var foreignTracking = new AgentTrackingProfile { AgentUserId = foreignId, AgentUpn = foreign.AgentUpn, Slug = "synthetic-" + Guid.NewGuid().ToString("N") };
        var workspace = WorkspaceKey.ForAgentTrackingProfile(tracking.Id).Value;
        var conversationId = Guid.NewGuid();
        var mediaAsset = new WebsiteMediaAsset { OwnerKey = actorId, SourceUrl = "synthetic-reference", ContentType = "image/png",
            SizeBytes = 1, Sha256 = new string('a', 64), StorageKey = "synthetic-" + Guid.NewGuid().ToString("N") };
        db.AddRange(agent, foreign, tracking, foreignTracking, mediaAsset); await db.SaveChangesAsync();
        try
        {
            var service = CreateService(db, configuredFounderOid: actorId, configuration: FounderConfiguration(actorId));
            var request = new MessagingFounderAiBeginTurnCommand(actor, conversationId, Guid.NewGuid(), null,
                "Synthetic scoped request", "business", new string('a', 64), DateTime.UtcNow.AddMinutes(5),
                AssistantWorkspaceKey: workspace, AssistantConnectionId: Guid.NewGuid(), AssistantConfigurationRevision: Guid.NewGuid(), AssistantProvider: "openai");
            var started = await service.BeginFounderAiTurnAsync(request);
            Assert.True(started.Succeeded, started.ErrorMessage);
            var terminal = await service.CompleteFounderAiTurnAsync(new(actor, conversationId, request.OperationId, started.UserMessage!.Id,
                "Synthetic canonical result", MessagingAuthorKinds.Assistant, new(true, "business", ResponseAuthority: "SyntheticTest"), workspace));
            Assert.True(terminal.Succeeded, terminal.ErrorMessage);
            await using var otherDeviceDb = new MasterAppDbContext(options);
            var otherDevice = CreateService(otherDeviceDb, configuredFounderOid: actorId, configuration: FounderConfiguration(actorId));
            var page = await otherDevice.GetFounderAiConversationPageAsync(actor, conversationId, new(AssistantWorkspaceKey: workspace));
            Assert.True(page.Succeeded, page.ErrorMessage); Assert.Equal(2, page.Conversation!.Messages.Count);
            Assert.Empty((await otherDevice.ListFounderAiConversationsAsync(actor, new())).Conversations);
            Assert.Single((await otherDevice.ListFounderAiConversationsAsync(actor, new(AssistantWorkspaceKey: workspace))).Conversations);
            Assert.False((await otherDevice.GetFounderAiConversationPageAsync(actor, conversationId,
                new(AssistantWorkspaceKey: WorkspaceKey.ForAgentTrackingProfile(foreignTracking.Id).Value))).Succeeded);
            Assert.False((await otherDevice.GetFounderAiConversationPageAsync(new(actorId, MessagingParticipantTypes.Client), conversationId,
                new(AssistantWorkspaceKey: workspace))).Succeeded);
            var changedProvider = await otherDevice.BeginFounderAiTurnAsync(request with { OperationId = Guid.NewGuid(),
                ExpectedLastMessageId = terminal.Message!.Id, AssistantConfigurationRevision = Guid.NewGuid() });
            Assert.Equal("FOUNDER_HISTORY_PROVIDER_CHANGED", changedProvider.ErrorCode);
            Assert.True(await BusinessWorkspaceAccess.CanReadAssistantWorkspaceAsync(db, actorId, workspace, capability: "website"));
            Assert.False(await BusinessWorkspaceAccess.CanReadAssistantWorkspaceAsync(db, foreignId, workspace, capability: "website"));
            var storage = new Mock<ISocialMediaStorage>(MockBehavior.Strict);
            storage.Setup(x => x.OpenReadAsync(mediaAsset.StorageKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => SocialMediaReadResult.Available(new MemoryStream(new byte[] { 1 })));
            var media = new WebsiteMediaService(db, storage.Object);
            Assert.Null(await media.OpenAsync(foreignId, mediaAsset.Id));
            storage.Verify(x => x.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            var ownedMedia = await media.OpenAsync(actorId, mediaAsset.Id);
            Assert.NotNull(ownedMedia);
            await ownedMedia!.Value.Content.DisposeAsync();
            tracking.Status = "disabled"; await db.SaveChangesAsync();
            Assert.False(await BusinessWorkspaceAccess.CanReadAssistantWorkspaceAsync(db, actorId, workspace, capability: "website"));
            Assert.False((await otherDevice.GetFounderAiConversationPageAsync(actor, conversationId, new(AssistantWorkspaceKey: workspace))).Succeeded);
        }
        finally
        {
            await using var cleanup = new MasterAppDbContext(options);
            await cleanup.Set<WebsiteMediaAsset>().Where(x => x.Id == mediaAsset.Id).ExecuteDeleteAsync();
            await cleanup.InternalMessages.Where(x => x.ConversationId == conversationId).ExecuteDeleteAsync();
            await cleanup.MessageConversationParticipants.Where(x => x.ConversationId == conversationId).ExecuteDeleteAsync();
            await cleanup.MessageConversations.Where(x => x.Id == conversationId).ExecuteDeleteAsync();
            await cleanup.AgentTrackingProfiles.Where(x => x.Id == tracking.Id || x.Id == foreignTracking.Id).ExecuteDeleteAsync();
            await cleanup.AgentProfiles.Where(x => x.Id == agent.Id || x.Id == foreign.Id).ExecuteDeleteAsync();
        }
    }
}
