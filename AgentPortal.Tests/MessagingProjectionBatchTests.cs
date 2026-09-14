using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Generic;

namespace AgentPortal.Tests;

public sealed partial class MessagingServiceTests
{
    [Fact]
    public async Task ConversationDetail_BoundsOpenAndResumeWithoutDiscardingOlderHistory()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var id = await SeedProjectionHistoryAsync(db, 95);
        var service = CreateService(db);
        var sender = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var opened = (await service.GetConversationAsync(sender, id)).Conversation!;
        Assert.Equal(60, opened.Messages.Count);
        Assert.Equal("Message 35", opened.Messages[0].Body);
        Assert.Equal("Message 94", opened.Messages[^1].Body);
        Assert.True(opened.HasOlderMessages);
        var resumed = await service.StartConversationAsync(new StartMessagingConversationCommand(
            sender, "client-1", MessagingParticipantTypes.Client));
        Assert.Equal(id, resumed.Conversation!.Id);
        Assert.Equal(60, resumed.Conversation.Messages.Count);
        var older = (await service.GetConversationPageAsync(sender, id,
            new MessagingConversationMessagePageQuery(opened.Messages[0].SentUtc))).Conversation!;
        Assert.Equal(35, older.Messages.Count);
        Assert.False(older.HasOlderMessages);
        Assert.Equal(95, opened.Messages.Concat(older.Messages).Select(message => message.Id).Distinct().Count());
        var forbidden = await service.GetConversationAsync(new MessagingActor("other-agent", MessagingParticipantTypes.Agent), id);
        Assert.False(forbidden.Succeeded);
    }

    [Fact]
    public async Task ConversationPage_DoesNotTranslateLookaheadAndStillReflectsNewMessages()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var id = await SeedProjectionHistoryAsync(db, 4);
        var profile = await db.ClientProfiles.SingleAsync(row => row.ClientUserId == "client-1");
        db.ControlledResourceGrants.Add(new ControlledResourceGrant
        {
            UserId = "client-1", ParticipantType = MessagingParticipantTypes.Client,
            ResourceType = ControlledResourceTypes.LanguageTranslation, IsActive = true,
            GrantedUtc = DateTime.UtcNow, GrantedByUserId = "zac-founder-oid"
        });
        db.MobileProfileSettings.Add(new MobileProfileSettings
        {
            ProfileId = profile.Id, ParticipantType = MessagingParticipantTypes.Client,
            PreferredCommunicationLanguage = "ht"
        });
        await db.SaveChangesAsync();
        var translator = new DeferredTranslationProbe();
        var service = CreateService(db, translator);
        var recipient = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var page = (await service.GetConversationPageAsync(recipient, id,
            new MessagingConversationMessagePageQuery(Take: 2))).Conversation!;
        Assert.Equal(2, page.Messages.Count);
        Assert.True(page.HasOlderMessages);
        Assert.Equal(2, translator.Calls);
        Assert.Equal(2, await db.MessageTranslations.CountAsync());
        var sent = await service.SendMessageAsync(new SendMessagingMessageCommand(
            new MessagingActor("agent-1", MessagingParticipantTypes.Agent), id, "Newest message"));
        Assert.True(sent.Succeeded);
        var refreshed = (await service.GetConversationPageAsync(recipient, id,
            new MessagingConversationMessagePageQuery(Take: 2))).Conversation!;
        Assert.Contains(refreshed.Messages, message => message.Id == sent.Message!.Id);
        Assert.Equal(3, translator.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversationProjection_OneAdmissionBudgetPreservesSlowSuccessOriginalsAndLaterCache(bool detectionBlocks)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var id = await SeedProjectionHistoryAsync(db, 60);
        await EnableProjectionLanguageAsync(db);
        var rows = await db.InternalMessages.OrderBy(row => row.SentUtc).ToListAsync();
        if (detectionBlocks) rows[0].OriginalLanguage = null;
        rows[1].ReplyToMessageId = rows[0].Id;
        db.MessageTranslations.Add(new MessageTranslation
        {
            Id = Guid.NewGuid(), InternalMessageId = rows[^1].Id, TargetLanguage = "ht",
            TranslatedText = "Dènye mesaj", Provider = "retained-test", CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var translator = new ProjectionBudgetProbe(detectionBlocks);
        var page = await CreateService(db, translator).GetConversationAsync(
            new MessagingActor("client-1", MessagingParticipantTypes.Client), id).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(page.Succeeded, page.ErrorMessage);
        Assert.Equal(rows.Select(row => row.Id), page.Conversation!.Messages.Select(message => message.Id));
        Assert.Equal(60, page.Conversation.Messages.Count);
        Assert.All(page.Conversation.Messages.Skip(detectionBlocks ? 0 : 1).Take(detectionBlocks ? 59 : 58), message =>
        {
            Assert.Equal(rows.Single(row => row.Id == message.Id).Body, message.Body);
            Assert.Null(message.Translation);
            Assert.Null(message.OriginalBody);
            Assert.Equal(MessagingTranslationPresentation.UnavailableMessage, message.TranslationNotice);
        });
        Assert.Equal(detectionBlocks ? rows[0].Body : "Tradiksyon Message 0", page.Conversation.Messages[1].Reply!.Body);
        if (!detectionBlocks)
        {
            Assert.Equal("Tradiksyon Message 0", page.Conversation.Messages[0].Body);
            Assert.Equal(rows[0].Body, page.Conversation.Messages[0].OriginalBody);
            Assert.Null(page.Conversation.Messages[0].TranslationNotice);
        }
        Assert.Equal("Dènye mesaj", page.Conversation.Messages[^1].Body);
        Assert.Equal("retained-test", page.Conversation.Messages[^1].Translation!.Provider);
        Assert.Null(page.Conversation.Messages[^1].TranslationNotice);
        Assert.Equal(detectionBlocks ? 1 : 0, translator.DetectionCalls);
        Assert.Equal(detectionBlocks ? 0 : 1, translator.TranslationCalls);
        Assert.Equal(detectionBlocks ? 1 : 2, await db.MessageTranslations.CountAsync());
        Assert.Empty(await db.LegendTranslationUsageLedgers.ToListAsync());
        Assert.Null((await db.MessageConversationParticipants.SingleAsync(row => row.ConversationId == id && row.UserId == "client-1")).LastReadMessageId);
        Assert.Equal("en", rows[0].OriginalLanguage);
    }

    [Fact]
    public async Task ConversationProjection_CallerCancellationIsNeverConvertedToOriginalSuccess()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var id = await SeedProjectionHistoryAsync(db, 2);
        await EnableProjectionLanguageAsync(db);
        var translator = new ProjectionBudgetProbe(false);
        using var cancellation = new CancellationTokenSource();
        var pending = CreateService(db, translator).GetConversationAsync(
            new MessagingActor("client-1", MessagingParticipantTypes.Client), id, cancellation.Token);
        await translator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, translator.TranslationCalls);
        Assert.Empty(await db.MessageTranslations.ToListAsync());
    }

    [Fact]
    public async Task ConversationProjection_LatePaidSuccessIsRetainedBeforeCallerCancellationAndReused()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var id = await SeedProjectionHistoryAsync(db, 1);
        await EnableProjectionLanguageAsync(db);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:Entitlements:DefaultMonthlyCharacterAllowance"] = "1000",
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "100000",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "0"
        }).Build();
        var authority = new TranslationEntitlementAuthority(db, new ControlledResourceAccessService(db, configuration),
            configuration, NullLogger<TranslationEntitlementAuthority>.Instance);
        using var cancellation = new CancellationTokenSource();
        var provider = new LateProjectionSuccessProvider(cancellation);
        var router = new LegendConnectTranslationRouter(provider, new LegendLanguageRegistry(db, configuration),
            new TranslationCapacityAuthority(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance),
            NullLogger<LegendConnectTranslationRouter>.Instance, entitlements: authority);
        var service = CreateService(db, router, configuration: configuration);
        var recipient = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetConversationAsync(recipient, id, cancellation.Token));
        var saved = await db.MessageTranslations.SingleAsync();
        Assert.Equal("Tradiksyon ki fini", saved.TranslatedText);
        Assert.Equal("Message 0", (await db.InternalMessages.SingleAsync()).Body);
        var ledger = await db.LegendTranslationUsageLedgers.SingleAsync();
        Assert.True(ledger.Succeeded);
        Assert.True(ledger.ProviderExecuted);
        Assert.Equal("Succeeded", ledger.State);
        Assert.Equal(0, (await db.LegendTranslationUsagePeriods.SingleAsync()).ReservedCharacters);
        Assert.Equal(9, (await db.LegendTranslationUsagePeriods.SingleAsync()).ConsumedCharacters);
        var recovered = await service.GetConversationAsync(recipient, id);
        Assert.True(recovered.Succeeded, recovered.ErrorMessage);
        var message = Assert.Single(recovered.Conversation!.Messages);
        Assert.Equal("Tradiksyon ki fini", message.Body);
        Assert.Equal("Message 0", message.OriginalBody);
        Assert.Null(message.TranslationNotice);
        Assert.Equal(1, provider.Calls);
        Assert.Single(await db.MessageTranslations.ToListAsync());
        Assert.Single(await db.LegendTranslationUsageLedgers.ToListAsync());
    }

    private sealed class LateProjectionSuccessProvider(CancellationTokenSource cancellation) : ITranslationProvider
    {
        public string ProviderName => "AzureTranslator";
        public string ProviderVersion => "test";
        public int Calls { get; private set; }
        public Task<TranslationDetectionResult> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(new TranslationDetectionResult(true, "en"));
        public Task<TranslationProviderResult> TranslateAsync(string text, string targetLanguage,
            string? sourceLanguage = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellation.Cancel();
            return Task.FromResult(new TranslationProviderResult(true, "Tradiksyon ki fini", sourceLanguage, ProviderName));
        }
    }

    private static async Task EnableProjectionLanguageAsync(Infrastructure.Data.MasterAppDbContext db)
    {
        var profile = await db.ClientProfiles.SingleAsync(row => row.ClientUserId == "client-1");
        db.ControlledResourceGrants.Add(new ControlledResourceGrant
        {
            UserId = "client-1", ParticipantType = MessagingParticipantTypes.Client,
            ResourceType = ControlledResourceTypes.LanguageTranslation, IsActive = true,
            GrantedUtc = DateTime.UtcNow, GrantedByUserId = "zac-founder-oid"
        });
        db.MobileProfileSettings.Add(new MobileProfileSettings
        {
            ProfileId = profile.Id, ParticipantType = MessagingParticipantTypes.Client,
            PreferredCommunicationLanguage = "ht"
        });
        await db.SaveChangesAsync();
    }

    private sealed class ProjectionBudgetProbe(bool detectionBlocks) : ITranslationService
    {
        public int DetectionCalls { get; private set; }
        public int TranslationCalls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TranslationDetectionResult> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
        {
            DetectionCalls++;
            if (detectionBlocks) { Entered.TrySetResult(); await Task.Delay(TimeSpan.FromMilliseconds(2100), cancellationToken); }
            return new TranslationDetectionResult(true, "en");
        }
        public async Task<TranslationProviderResult> TranslateAsync(string text, string targetLanguage,
            string? sourceLanguage = null, CancellationToken cancellationToken = default)
        {
            TranslationCalls++;
            Entered.TrySetResult();
            await Task.Delay(TimeSpan.FromMilliseconds(2100), cancellationToken);
            return new TranslationProviderResult(true, "Tradiksyon " + text, sourceLanguage, "slow-provider");
        }
    }

    private static async Task<Guid> SeedProjectionHistoryAsync(Infrastructure.Data.MasterAppDbContext db, int count)
    {
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var started = await CreateService(db).StartConversationAsync(new StartMessagingConversationCommand(
            new MessagingActor("agent-1", MessagingParticipantTypes.Agent), "client-1", MessagingParticipantTypes.Client));
        var id = started.Conversation!.Id;
        var start = DateTime.UtcNow.AddDays(-1);
        db.InternalMessages.AddRange(Enumerable.Range(0, count).Select(index => new InternalMessage
        {
            Id = Guid.NewGuid(), ConversationId = id,
            SenderUserId = "agent-1", SenderType = MessagingParticipantTypes.Agent,
            Body = "Message " + index, OriginalLanguage = "en", SenderPreferredLanguage = "en",
            SentUtc = start.AddSeconds(index)
        }));
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Direct_chat_title_uses_the_counterpartys_current_profile_for_both_roles()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var client = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var started = await service.StartConversationAsync(new StartMessagingConversationCommand(agent, client.UserId, client.ParticipantType));
        var id = started.Conversation!.Id;
        Assert.True((await service.SendMessageAsync(new SendMessagingMessageCommand(agent, id, "Hello."))).Succeeded);
        var row = await db.MessageConversations.SingleAsync(c => c.Id == id);
        row.Subject = "Conversation";
        await db.SaveChangesAsync();
        Assert.Equal("Client One", (await service.GetConversationAsync(agent, id)).Conversation!.DisplayTitle);
        Assert.Equal("Agent One", (await service.GetConversationAsync(client, id)).Conversation!.DisplayTitle);
        Assert.Equal("Client One", (await service.ListConversationsAsync(agent, new MessagingConversationListQuery())).Conversations.Single().DisplayTitle);
        row.ConversationType = MessagingConversationTypes.Group;
        row.Subject = "Team discussion";
        await db.SaveChangesAsync();
        Assert.Equal("Team discussion", (await service.GetConversationAsync(agent, id)).Conversation!.DisplayTitle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversationProjection_ResolvesRepeatedQuotedOriginalOnlyOncePerPage(bool providerFails)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var profile = await db.ClientProfiles.SingleAsync(row => row.ClientUserId == "client-1");
        db.ControlledResourceGrants.Add(new ControlledResourceGrant
        {
            UserId = "client-1", ParticipantType = MessagingParticipantTypes.Client,
            ResourceType = ControlledResourceTypes.LanguageTranslation, IsActive = true,
            GrantedUtc = DateTime.UtcNow, GrantedByUserId = "zac-founder-oid"
        });
        db.MobileProfileSettings.Add(new MobileProfileSettings
        {
            ProfileId = profile.Id, ParticipantType = MessagingParticipantTypes.Client,
            PreferredCommunicationLanguage = "ht"
        });
        await db.SaveChangesAsync();
        var translator = new DeferredTranslationProbe { Fail = providerFails };
        var service = CreateService(db, translator);
        var sender = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var recipient = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var started = await service.StartConversationAsync(new StartMessagingConversationCommand(sender, recipient.UserId, recipient.ParticipantType));
        var conversationId = started.Conversation!.Id;
        var original = await service.SendMessageAsync(new SendMessagingMessageCommand(sender, conversationId, "Hello."));
        for (var i = 0; i < 5; i++)
            Assert.True((await service.SendMessageAsync(new SendMessagingMessageCommand(recipient,
                conversationId, "Reply " + i, ReplyToMessageId: original.Message!.Id))).Succeeded);
        Assert.Equal(0, translator.Calls);
        var page = await service.GetConversationPageAsync(recipient, conversationId, new MessagingConversationMessagePageQuery());
        Assert.Equal(1, translator.Calls);
        if (providerFails)
        {
            Assert.True(page.Succeeded, page.ErrorMessage);
            Assert.Equal(6, page.Conversation!.Messages.Count);
            Assert.All(page.Conversation.Messages, message => Assert.Equal(MessagingTranslationPresentation.UnavailableMessage, message.TranslationNotice));
            Assert.Equal("Hello.", Assert.Single(page.Conversation.Messages.Where(message => message.Id == original.Message!.Id)).Body);
            Assert.All(page.Conversation.Messages.Where(message => message.Reply is not null), message => Assert.Equal("Hello.", message.Reply!.Body));
            Assert.All(page.Conversation.Messages, message => Assert.Null(message.Translation));
            Assert.Empty(await db.MessageTranslations.ToListAsync());
            Assert.Equal("Hello.", (await db.InternalMessages.SingleAsync(row => row.Id == original.Message!.Id)).Body);
            var participant = await db.MessageConversationParticipants.SingleAsync(row =>
                row.ConversationId == conversationId && row.UserId == recipient.UserId);
            Assert.Null(participant.LastReadMessageId);

            // Repeated quotations share one attempt even when translation fails.
            // Original presentation does not cache a failed translation or mark a read.
            var failedRetry = await service.GetConversationPageAsync(recipient, conversationId, new MessagingConversationMessagePageQuery());
            Assert.True(failedRetry.Succeeded, failedRetry.ErrorMessage);
            Assert.Equal(page.Conversation.Messages.Select(message => message.Id), failedRetry.Conversation!.Messages.Select(message => message.Id));
            Assert.Equal(2, translator.Calls);
            Assert.Empty(await db.MessageTranslations.ToListAsync());
            translator.Fail = false;
            page = await service.GetConversationPageAsync(recipient, conversationId, new MessagingConversationMessagePageQuery());
            Assert.Equal(3, translator.Calls);
            Assert.Null(participant.LastReadMessageId);
        }
        Assert.True(page.Succeeded);
        Assert.Equal(6, page.Conversation!.Messages.Count);
        Assert.Equal(6, await db.InternalMessages.CountAsync());
        var quotes = page.Conversation.Messages.Where(message => message.Reply is not null).ToList();
        Assert.Equal(5, quotes.Count);
        Assert.All(quotes, message => Assert.Equal("Bonjou.", message.Reply!.Body));
        var presentedOriginal = Assert.Single(page.Conversation.Messages.Where(message => message.Id == original.Message!.Id));
        Assert.Equal("Bonjou.", presentedOriginal.Body);
        Assert.Equal("Hello.", presentedOriginal.OriginalBody);
        var cache = Assert.Single(await db.MessageTranslations.ToListAsync());
        Assert.Equal(original.Message!.Id, cache.InternalMessageId);
        Assert.Equal("Hello.", (await db.InternalMessages.SingleAsync(row => row.Id == original.Message.Id)).Body);
        var cachedPage = await service.GetConversationPageAsync(recipient, conversationId, new MessagingConversationMessagePageQuery());
        Assert.True(cachedPage.Succeeded);
        Assert.Equal(page.Conversation.Messages.Select(message => message.Id), cachedPage.Conversation!.Messages.Select(message => message.Id));
        Assert.Equal(providerFails ? 3 : 1, translator.Calls);
    }
}
