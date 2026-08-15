using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Exercises the private-message boundary around the existing Legend Connect
/// learning event and corpus authorities. These tests never use a second
/// corpus path: a successfully persisted MessageTranslation is the only
/// source of a live-learning candidate.
/// </summary>
public sealed class ConsentedLiveTranslationLearningTests
{
    [Fact]
    public async Task ConsentOff_PersistsSafeMetadataWithoutRetainingConversationText()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = await CreateDirectMessageAsync(db, allowAgent: true, allowClient: false);
        var publisher = CreatePublisher(db);

        await publisher.TryPublishAsync(fixture.Candidate);

        var item = await db.LegendTranslationLearningEvents.SingleAsync();
        Assert.Equal("IneligibleConsent", item.EligibilityState);
        Assert.Equal("Skipped", item.ProcessingState);
        Assert.Equal("PrivateMessageOperationalTranslation", item.Provenance);
        Assert.Null(item.SourceText);
        Assert.Null(item.TargetText);
        Assert.NotEmpty(item.SourceTextHash);
        Assert.NotEmpty(item.TargetTextHash);
    }

    [Fact]
    public async Task UnpersistedOrFailedOperationalTranslation_CannotEnterTheLearningPipeline()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = await CreateDirectMessageAsync(db, allowAgent: true, allowClient: true);
        db.MessageTranslations.RemoveRange(await db.MessageTranslations.ToListAsync());
        await db.SaveChangesAsync();

        await CreatePublisher(db).TryPublishAsync(fixture.Candidate);

        var item = await db.LegendTranslationLearningEvents.SingleAsync();
        Assert.Equal("IneligibleUnpersistedTranslation", item.EligibilityState);
        Assert.Equal("Skipped", item.ProcessingState);
        Assert.Equal("NotEligible", item.PromotionOutcome);
        Assert.Null(item.SourceText);
        Assert.Null(item.TargetText);
    }

    [Fact]
    public async Task AllParticipantConsent_AfterPersistedTranslation_PromotesThroughTheExistingCorpus()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = await CreateDirectMessageAsync(db, allowAgent: true, allowClient: true);
        var registry = CreateRegistry(db);
        var publisher = new LegendTranslationLearningPublisher(
            db,
            registry,
            NullLogger<LegendTranslationLearningPublisher>.Instance);

        // The translation row is persisted by the operational messaging path
        // before the publisher is called. The candidate does not itself create
        // delivery output or a second translation record.
        Assert.Single(await db.MessageTranslations.ToListAsync());
        await publisher.TryPublishAsync(fixture.Candidate);

        var pending = await db.LegendTranslationLearningEvents.SingleAsync();
        Assert.Equal("Eligible", pending.EligibilityState);
        Assert.Equal("Pending", pending.ProcessingState);
        Assert.Equal("ConsentedLiveTranslation", pending.Provenance);
        Assert.Equal(fixture.Candidate.SourceText, pending.SourceText);
        Assert.Equal(fixture.Candidate.TargetText, pending.TargetText);

        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        await corpus.ProcessPendingAsync(10);

        var processed = await db.LegendTranslationLearningEvents.SingleAsync();
        Assert.Equal("Processed", processed.ProcessingState);
        Assert.Equal("Promoted", processed.PromotionOutcome);
        Assert.Equal(2, await db.LegendLanguageTextUnits.CountAsync());
        var alignment = await db.LegendTranslationAlignments.SingleAsync();
        Assert.Equal("en:ht", alignment.PairKey);
        Assert.Equal(1, alignment.ObservationCount);
        Assert.False(alignment.HumanVerified);
        Assert.Equal("ConsentedLive", alignment.QualityState);
        Assert.Equal(0.98m, alignment.Confidence);

        // Consent permits this successful live translation to contribute to
        // the one governed learning corpus. It does not make Azure authoritative.
        // Production exact-memory delivery remains owned by the existing
        // explicit verification boundary in LegendConnectTranslationIntelligence.
        var intelligence = new LegendConnectTranslationIntelligence(
            db,
            new ConfigurationBuilder().Build());
        var trustedMemory = await intelligence.TryGetTrustedExactMemoryAsync(
            "en",
            "ht",
            fixture.Candidate.SourceText);
        Assert.Null(trustedMemory);

        var context = await db.LegendLanguageContextRelationships.SingleAsync();
        Assert.Equal("ConsentedLiveTranslation", context.Provenance);

        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            new ConfigurationBuilder().Build());
        var dashboard = await operations.GetDashboardAsync();
        Assert.Equal(2, dashboard.ConsentedLiveLearningAccountCount);
        Assert.Equal(1, dashboard.EligibleConsentedLiveTranslationCount);
        Assert.Equal(1, dashboard.PromotedConsentedLiveTranslationCount);
        var languageKnowledge = await operations.GetLanguageKnowledgeAsync("en");
        Assert.NotNull(languageKnowledge);
        Assert.DoesNotContain(languageKnowledge!.CanonicalEntries, item => item.Text == fixture.Candidate.SourceText);
        Assert.Contains(languageKnowledge.RecentLearningActivity, item =>
            item.Provenance == "ConsentedLiveTranslation" &&
            item.PairKey == "en:ht" &&
            item.PromotionOutcome == "Promoted");
    }

    [Fact]
    public async Task RepeatedConsentedLiveTranslation_ReusesFounderAlignmentWithoutDuplicateKnowledge()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = CreateRegistry(db);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        var founderKnowledge = await corpus.SubmitApprovedKnowledgeAsync(new LegendConnectKnowledgeSubmission(
            "en",
            "Are you coming over tonight?",
            "ht",
            "Èske w ap vini aswè a?",
            "Everyday conversation",
            "Informal",
            null,
            "FounderApproved"));
        Assert.True(founderKnowledge.Succeeded);

        var fixture = await CreateDirectMessageAsync(
            db,
            allowAgent: true,
            allowClient: true,
            sourceText: "Are you coming over tonight?",
            targetText: "Èske w ap vini aswè a?");
        var publisher = new LegendTranslationLearningPublisher(
            db,
            registry,
            NullLogger<LegendTranslationLearningPublisher>.Instance);

        await publisher.TryPublishAsync(fixture.Candidate);
        await corpus.ProcessPendingAsync(10);

        var item = await db.LegendTranslationLearningEvents.SingleAsync();
        Assert.Equal("ConsentedLiveTranslation", item.Provenance);
        Assert.Equal("Reused", item.PromotionOutcome);
        Assert.Equal(2, await db.LegendLanguageTextUnits.CountAsync());
        var alignment = await db.LegendTranslationAlignments.SingleAsync();
        Assert.Equal(2, alignment.ObservationCount);
    }

    [Fact]
    public async Task DisablingConsent_PreventsFuturePromotion_AndAmbiguousGroupFailsClosed()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = await CreateDirectMessageAsync(db, allowAgent: true, allowClient: true);
        var clientConsent = await db.MobileProfileSettings.SingleAsync(item =>
            item.ParticipantType == MessagingParticipantTypes.Client);
        clientConsent.AllowsConsentedTranslationLearning = false;
        await db.SaveChangesAsync();

        var publisher = CreatePublisher(db);
        await publisher.TryPublishAsync(fixture.Candidate);
        Assert.Equal("IneligibleConsent", (await db.LegendTranslationLearningEvents.SingleAsync()).EligibilityState);

        db.LegendTranslationLearningEvents.RemoveRange(db.LegendTranslationLearningEvents);
        clientConsent.AllowsConsentedTranslationLearning = true;
        await db.SaveChangesAsync();
        var unknownParticipant = new MessageConversationParticipant
        {
            Id = Guid.NewGuid(),
            ConversationId = fixture.ConversationId,
            UserId = "unresolved-group-member",
            ParticipantType = MessagingParticipantTypes.Client,
            IsActive = true,
            JoinedUtc = fixture.SentUtc.AddSeconds(-1)
        };
        db.MessageConversationParticipants.Add(unknownParticipant);
        await db.SaveChangesAsync();

        // Use an actual persisted group message so the policy evaluates the
        // unresolved member rather than treating an unknown message as input.
        var grouped = await CreateMessageInConversationAsync(
            db,
            fixture.ConversationId,
            fixture.Agent,
            "A new group message",
            "Yon nouvo mesaj gwoup");
        await publisher.TryPublishAsync(grouped);
        var ambiguous = await db.LegendTranslationLearningEvents.SingleAsync();
        Assert.Equal("IneligibleConsentAmbiguous", ambiguous.EligibilityState);
        Assert.Null(ambiguous.SourceText);
        Assert.Null(ambiguous.TargetText);
    }

    private static LegendTranslationLearningPublisher CreatePublisher(
        Infrastructure.Data.MasterAppDbContext db) => new(
        db,
        CreateRegistry(db),
        NullLogger<LegendTranslationLearningPublisher>.Instance);

    private static LegendLanguageRegistry CreateRegistry(
        Infrastructure.Data.MasterAppDbContext db) => new(
        db,
        new ConfigurationBuilder().Build());

    private static async Task<LiveMessageFixture> CreateDirectMessageAsync(
        Infrastructure.Data.MasterAppDbContext db,
        bool allowAgent,
        bool allowClient,
        string sourceText = "Are you coming over tonight?",
        string targetText = "Èske w ap vini aswè a?")
    {
        var agent = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "consented-agent-" + Guid.NewGuid().ToString("N"),
            FullName = "Consented Agent",
            IsActive = true
        };
        var client = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "consented-client-" + Guid.NewGuid().ToString("N"),
            FirstName = "Consented",
            LastName = "Client",
            Email = "consented@example.test"
        };
        var now = DateTime.UtcNow;
        var conversation = new MessageConversation
        {
            Id = Guid.NewGuid(),
            ConversationType = "ClientAgent",
            CreatedByUserId = agent.AgentUserId,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var message = new InternalMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            SenderUserId = agent.AgentUserId,
            SenderType = MessagingParticipantTypes.Agent,
            Body = sourceText,
            OriginalLanguage = "en",
            SentUtc = now
        };
        db.AddRange(
            agent,
            client,
            conversation,
            new MessageConversationParticipant
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                UserId = agent.AgentUserId,
                ParticipantType = MessagingParticipantTypes.Agent,
                IsActive = true,
                JoinedUtc = now.AddSeconds(-1)
            },
            new MessageConversationParticipant
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                UserId = client.ClientUserId,
                ParticipantType = MessagingParticipantTypes.Client,
                IsActive = true,
                JoinedUtc = now.AddSeconds(-1)
            },
            new MobileProfileSettings
            {
                Id = Guid.NewGuid(),
                ProfileId = agent.Id,
                ParticipantType = MessagingParticipantTypes.Agent,
                AllowsConsentedTranslationLearning = allowAgent
            },
            new MobileProfileSettings
            {
                Id = Guid.NewGuid(),
                ProfileId = client.Id,
                ParticipantType = MessagingParticipantTypes.Client,
                AllowsConsentedTranslationLearning = allowClient
            },
            message,
            new MessageTranslation
            {
                Id = Guid.NewGuid(),
                InternalMessageId = message.Id,
                TargetLanguage = "ht",
                TranslatedText = targetText,
                Provider = "AzureTranslator",
                CreatedUtc = now
            });
        await db.SaveChangesAsync();
        return new LiveMessageFixture(
            conversation.Id,
            now,
            agent,
            new TranslationLearningCandidate(
                message.Id,
                "en",
                "ht",
                sourceText,
                targetText,
                "AzureTranslator"));
    }

    private static async Task<TranslationLearningCandidate> CreateMessageInConversationAsync(
        Infrastructure.Data.MasterAppDbContext db,
        Guid conversationId,
        AgentProfile sender,
        string sourceText,
        string targetText)
    {
        var message = new InternalMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SenderUserId = sender.AgentUserId,
            SenderType = MessagingParticipantTypes.Agent,
            Body = sourceText,
            OriginalLanguage = "en",
            SentUtc = DateTime.UtcNow
        };
        db.AddRange(message, new MessageTranslation
        {
            Id = Guid.NewGuid(),
            InternalMessageId = message.Id,
            TargetLanguage = "ht",
            TranslatedText = targetText,
            Provider = "AzureTranslator"
        });
        await db.SaveChangesAsync();
        return new TranslationLearningCandidate(
            message.Id,
            "en",
            "ht",
            sourceText,
            targetText,
            "AzureTranslator");
    }

    private sealed record LiveMessageFixture(
        Guid ConversationId,
        DateTime SentUtc,
        AgentProfile Agent,
        TranslationLearningCandidate Candidate);
}
