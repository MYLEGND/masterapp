using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Regression coverage for the one shared provider-quality authority. These
/// tests intentionally use canonical text units, alignments, context, and
/// curriculum evidence rather than language-specific rules or string guesses.
/// </summary>
public sealed class LegendConnectTranslationQualityTests
{
    [Fact]
    public async Task CompatibleProviderObservation_SystemValidatesFromIndependentFounderEvidenceWithoutHumanRelabeling()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var pair = await PairAsync(fixture.Registry, "ht");
        var source = Unit("en", "This is a source with trusted context.", "FounderApproved");
        var target = Unit("ht", "Sa se yon rezilta founisè.", "ProviderDerived");
        var contextSource = Unit("en", "Another source with the same approved context.", "FounderApproved");
        var observation = ProviderObservation(pair, source, target);
        db.AddRange(source, target, contextSource, observation, new LegendLanguageContextRelationship
        {
            Id = Guid.NewGuid(), PairKey = pair.PairKey, SourceTextUnitId = contextSource.Id,
            RelatedTextUnitId = target.Id, RelationshipKind = "ContextualExample",
            ContextSignature = "trusted", SourcePatternSignature = LegendLanguageIdentity.ContextPatternSignature(source.Text),
            Confidence = 1m, QualityState = "Verified", Provenance = "FounderApproved", ObservationCount = 1
        });
        await db.SaveChangesAsync();

        await fixture.Intelligence.EvaluateProviderObservationAsync(observation.Id);

        var persisted = await db.LegendTranslationAlignments.SingleAsync(item => item.Id == observation.Id);
        Assert.Equal("ProviderDerived", persisted.Provenance);
        Assert.Equal("SystemValidated", persisted.QualityState);
        Assert.False(persisted.HumanVerified);
        Assert.Equal("ProviderDerived", persisted.Provenance);
        Assert.True(persisted.Confidence >= 0.98m);
        Assert.Contains(await db.LegendTranslationQualityEvidence.ToListAsync(), item =>
            item.ObservedAlignmentId == observation.Id && item.Signal == "Supported" &&
            item.ReasonCode == "trusted_target_context");
    }

    [Fact]
    public async Task ProviderCorpusPromotion_QueuesQualityEvaluationOffTheMessagingPath()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var pair = await PairAsync(fixture.Registry, "ht");
        var source = Unit("en", "Corpus provider contradiction source.", "FounderApproved");
        var verifiedTarget = Unit("ht", "Sib imen verifye.", "FounderApproved");
        db.AddRange(source, verifiedTarget, HumanAlignment(pair, source, verifiedTarget));
        await db.SaveChangesAsync();
        var eventItem = new LegendTranslationLearningEvent
        {
            Id = Guid.NewGuid(), IdempotencyKey = "quality-corpus-provider", SourceLanguageCode = "en", TargetLanguageCode = "ht",
            PairKey = pair.PairKey, SourceText = source.Text, TargetText = "Sib Azure ki diferan.",
            SourceTextHash = source.NormalizedHash, TargetTextHash = LegendLanguageIdentity.TextHash("Sib Azure ki diferan."),
            Provider = "AzureTranslator", Provenance = "ProviderDerived", EligibilityState = "Eligible", ProcessingState = "Pending"
        };
        db.Add(eventItem);
        await db.SaveChangesAsync();

        await fixture.Corpus.ProcessAsync(eventItem);

        var providerAlignment = await db.LegendTranslationAlignments.SingleAsync(item =>
            item.Provenance == "ProviderDerived" && item.SourceTextUnitId == source.Id);
        Assert.Equal("Observation", providerAlignment.QualityState);
        Assert.Contains(await db.LegendTranslationQualityEvidence.ToListAsync(), item =>
            item.ObservedAlignmentId == providerAlignment.Id && item.Signal == "Contradictory");
    }

    [Fact]
    public async Task ContradictoryProviderObservation_RemainsHistoricalAndAppearsInFounderReview()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var pair = await PairAsync(fixture.Registry, "ht");
        var source = Unit("en", "The child reads before dinner.", "FounderApproved");
        var providerTarget = Unit("ht", "Rezilta founisè ki pa matche.", "ProviderDerived");
        var verifiedTarget = Unit("ht", "Timoun nan li anvan dine.", "FounderApproved");
        var observation = ProviderObservation(pair, source, providerTarget);
        var verified = HumanAlignment(pair, source, verifiedTarget);
        db.AddRange(source, providerTarget, verifiedTarget, observation, verified);
        await db.SaveChangesAsync();
        var alignmentCount = await db.LegendTranslationAlignments.CountAsync();

        await fixture.Intelligence.EvaluateProviderObservationAsync(observation.Id);

        var provider = await db.LegendTranslationAlignments.SingleAsync(item => item.Id == observation.Id);
        Assert.Null(provider.SupersededUtc);
        Assert.False(provider.HumanVerified);
        Assert.Equal("Observation", provider.QualityState);
        Assert.Equal(alignmentCount, await db.LegendTranslationAlignments.CountAsync());
        Assert.Contains(await db.LegendTranslationQualityEvidence.ToListAsync(), item =>
            item.ObservedAlignmentId == observation.Id && item.Signal == "Contradictory" &&
            item.RelatedAlignmentId == verified.Id && item.ResolutionState == "Open");
        var review = await fixture.Intelligence.GetTranslationQualityAsync();
        var item = Assert.Single(review.ReviewItems);
        Assert.Equal(observation.Id, item.AlignmentId);
        Assert.Equal(source.Text, item.SourceText);
        Assert.Equal(providerTarget.Text, item.ProviderTargetText);
        Assert.Equal("ProviderDerived", item.Provenance);
    }

    [Fact]
    public async Task InsufficientEvidence_RemainsAnObservationWithoutFalseFailure()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var pair = await PairAsync(fixture.Registry, "ja");
        var source = Unit("en", "Novel example with no established pair evidence.", "FounderApproved");
        var target = Unit("ja", "十分な証拠がない結果です。", "ProviderDerived");
        var observation = ProviderObservation(pair, source, target);
        db.AddRange(source, target, observation);
        await db.SaveChangesAsync();

        await fixture.Intelligence.EvaluateProviderObservationAsync(observation.Id);

        var evidence = Assert.Single(await db.LegendTranslationQualityEvidence.ToListAsync());
        Assert.Equal("Insufficient", evidence.Signal);
        Assert.Equal("no_established_pair_specific_evidence", evidence.ReasonCode);
        Assert.Empty((await fixture.Intelligence.GetTranslationQualityAsync()).ReviewItems);
        Assert.False((await db.LegendTranslationAlignments.SingleAsync()).HumanVerified);
    }

    [Fact]
    public async Task HumanCorrection_PreservesProviderHistoryAndPrefersTheSeparateVerifiedAlignment()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var pair = await PairAsync(fixture.Registry, "ht");
        var source = Unit("en", "She reads before dinner.", "FounderApproved");
        var providerTarget = Unit("ht", "Li li anvan dine.", "ProviderDerived");
        var provider = ProviderObservation(pair, source, providerTarget);

        var conversation = new MessageConversation
        {
            Id = Guid.NewGuid(),
            ConversationType = MessagingConversationTypes.Group,
            Subject = "Correction projection regression",
            CreatedByUserId = "founder",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        var correctedSourceMessage = new InternalMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            SenderUserId = "founder",
            SenderType = MessagingParticipantTypes.Agent,
            Body = source.Text,
            OriginalLanguage = "en",
            SenderPreferredLanguage = "en",
            SentUtc = DateTime.UtcNow
        };
        var unrelatedSourceMessage = new InternalMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            SenderUserId = "founder",
            SenderType = MessagingParticipantTypes.Agent,
            Body = "A different English source.",
            OriginalLanguage = "en",
            SenderPreferredLanguage = "en",
            SentUtc = DateTime.UtcNow
        };

        var staleAzureProjection = new MessageTranslation
        {
            Id = Guid.NewGuid(),
            InternalMessageId = correctedSourceMessage.Id,
            TargetLanguage = "ht",
            TranslatedText = providerTarget.Text,
            Provider = "AzureTranslator",
            CreatedUtc = DateTime.UtcNow
        };
        var differentTargetProjection = new MessageTranslation
        {
            Id = Guid.NewGuid(),
            InternalMessageId = correctedSourceMessage.Id,
            TargetLanguage = "fr",
            TranslatedText = "Projection française distincte.",
            Provider = "AzureTranslator",
            CreatedUtc = DateTime.UtcNow
        };
        var differentSourceProjection = new MessageTranslation
        {
            Id = Guid.NewGuid(),
            InternalMessageId = unrelatedSourceMessage.Id,
            TargetLanguage = "ht",
            TranslatedText = "Yon lòt tradiksyon.",
            Provider = "AzureTranslator",
            CreatedUtc = DateTime.UtcNow
        };

        db.AddRange(
            source,
            providerTarget,
            provider,
            conversation,
            correctedSourceMessage,
            unrelatedSourceMessage,
            staleAzureProjection,
            differentTargetProjection,
            differentSourceProjection);
        await db.SaveChangesAsync();

        Assert.Equal(3, await db.MessageTranslations.CountAsync());

        var corrected = await fixture.Operations.CorrectFounderKnowledgeAsync(
            "founder",
            provider.Id,
            new LegendConnectKnowledgeSubmission(
                "en", source.Text, "ht", "Li konn li anvan dine.",
                "Training", null, null, "FounderApproved"));

        Assert.True(corrected.Succeeded, corrected.Message);
        var original = await db.LegendTranslationAlignments.SingleAsync(item => item.Id == provider.Id);
        var replacement = await db.LegendTranslationAlignments.SingleAsync(item => item.Id == corrected.AlignmentId);
        Assert.Equal("ProviderDerived", original.Provenance);
        Assert.NotNull(original.SupersededUtc);
        Assert.True(replacement.HumanVerified);
        Assert.Equal("FounderApproved", replacement.Provenance);
        Assert.Contains(await db.LegendTranslationQualityEvidence.ToListAsync(), item =>
            item.ObservedAlignmentId == provider.Id && item.Signal == "Contradictory" &&
            item.RelatedAlignmentId == replacement.Id && item.ResolutionState == "Corrected");

        // The matching operational projection is corrected immediately from
        // current trusted exact memory. Unrelated directional/source caches
        // remain untouched.
        var reconciledProjection = await db.MessageTranslations
            .SingleAsync(item => item.Id == staleAzureProjection.Id);

        Assert.Equal(
            "Li konn li anvan dine.",
            reconciledProjection.TranslatedText);
        Assert.Equal(
            "LegendConnectTranslationMemory",
            reconciledProjection.Provider);

        Assert.Contains(
            await db.MessageTranslations.ToListAsync(),
            item => item.Id == differentTargetProjection.Id &&
                    item.TranslatedText == "Projection française distincte.");
        Assert.Contains(
            await db.MessageTranslations.ToListAsync(),
            item => item.Id == differentSourceProjection.Id &&
                    item.TranslatedText == "Yon lòt tradiksyon.");
        Assert.Equal(3, await db.MessageTranslations.CountAsync());

        var memory = await fixture.Intelligence.TryGetTrustedExactMemoryAsync("en", "ht", source.Text);
        Assert.NotNull(memory);
        Assert.Equal("Li konn li anvan dine.", memory!.Text);
    }

    [Fact]
    public async Task HistoricalOperationalReplay_RewritesOldProviderProjectionFromCurrentTrustedMemory()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var pair = await PairAsync(fixture.Registry, "ht");

        var source = Unit(
            "en",
            "Historical correction source.",
            "FounderApproved");
        var target = Unit(
            "ht",
            "Koreksyon istorik verifye.",
            "FounderApproved");
        var alignment = HumanAlignment(pair, source, target);

        var conversation = new MessageConversation
        {
            Id = Guid.NewGuid(),
            ConversationType = MessagingConversationTypes.Group,
            Subject = "Historical correction replay",
            CreatedByUserId = "founder",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var message = new InternalMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            SenderUserId = "founder",
            SenderType = MessagingParticipantTypes.Agent,
            Body = "  Historical   correction source.  ",
            OriginalLanguage = "en",
            SenderPreferredLanguage = "en",
            SentUtc = DateTime.UtcNow
        };

        var oldProjection = new MessageTranslation
        {
            Id = Guid.NewGuid(),
            InternalMessageId = message.Id,
            TargetLanguage = "ht",
            TranslatedText = "Ansyen rezilta Azure ki pa kòrèk.",
            Provider = "AzureTranslator",
            CreatedUtc = DateTime.UtcNow
        };

        db.AddRange(
            source,
            target,
            alignment,
            conversation,
            message,
            oldProjection);
        await db.SaveChangesAsync();

        var first = await fixture.Operations
            .ReconcileHistoricalOperationalTranslationsAsync(
                100,
                null);

        Assert.True(first.PhaseComplete);

        var reconciled = await db.MessageTranslations
            .SingleAsync(item => item.Id == oldProjection.Id);

        Assert.Equal(target.Text, reconciled.TranslatedText);
        Assert.Equal(
            "LegendConnectTranslationMemory",
            reconciled.Provider);

        var second = await fixture.Operations
            .ReconcileHistoricalOperationalTranslationsAsync(
                100,
                null);

        Assert.True(second.PhaseComplete);

        var unchanged = await db.MessageTranslations
            .SingleAsync(item => item.Id == oldProjection.Id);

        Assert.Equal(target.Text, unchanged.TranslatedText);
        Assert.Equal(
            "LegendConnectTranslationMemory",
            unchanged.Provider);
    }

    [Fact]
    public async Task RepeatedProviderQualityEvaluation_IsIdempotentAndDoesNotInflateEvidence()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var pair = await PairAsync(fixture.Registry, "fr");
        var source = Unit("en", "One provider observation only.", "FounderApproved");
        var target = Unit("fr", "Une seule observation fournisseur.", "ProviderDerived");
        var observation = ProviderObservation(pair, source, target);
        db.AddRange(source, target, observation);
        await db.SaveChangesAsync();

        await fixture.Intelligence.EvaluateProviderObservationAsync(observation.Id);
        await fixture.Intelligence.EvaluateProviderObservationAsync(observation.Id);
        await fixture.Intelligence.EvaluateProviderObservationAsync(observation.Id);

        Assert.Single(await db.LegendTranslationQualityEvidence.ToListAsync());
        Assert.Equal(1, (await db.LegendTranslationAlignments.SingleAsync()).ObservationCount);
    }

    [Fact]
    public async Task FounderCurriculumEvidence_AccumulatesAcrossSeparateBatchesWithoutMergingExampleLineage()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var first = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(CurriculumBatch(
            ("I work.", "first", "work"),
            ("She works.", "third", "works")));
        var second = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(CurriculumBatch(
            ("I walk.", "first", "walk"),
            ("She walks.", "third", "walks")));

        Assert.True(first.Succeeded, first.Message);
        Assert.True(second.Succeeded, second.Message);
        var patterns = await db.LegendLanguageStructuralPatterns
            .Where(item => item.LanguageCode == "en" && item.VariationDimension == "person")
            .ToListAsync();
        Assert.Contains(patterns, item => item.SupportCount >= 2);
        var exampleIds = await db.LegendCurriculumExamples.Select(item => item.Id).ToListAsync();
        Assert.Equal(exampleIds.Count, exampleIds.Distinct().Count());
    }

    [Fact]
    public async Task PairSpecificEvidence_DoesNotLeakFromHaitianCreoleToSpanish()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var haitian = await PairAsync(fixture.Registry, "ht");
        var spanish = await PairAsync(fixture.Registry, "es");
        var haitianSource = Unit("en", "Pair isolation source Haitian.", "FounderApproved");
        var haitianProviderTarget = Unit("ht", "Sib founisè ayisyen.", "ProviderDerived");
        var haitianVerifiedTarget = Unit("ht", "Sib verifye ayisyen.", "FounderApproved");
        var spanishSource = Unit("en", "Pair isolation source Spanish.", "FounderApproved");
        var spanishProviderTarget = Unit("es", "Resultado del proveedor español.", "ProviderDerived");
        var haitianObservation = ProviderObservation(haitian, haitianSource, haitianProviderTarget);
        var spanishObservation = ProviderObservation(spanish, spanishSource, spanishProviderTarget);
        db.AddRange(
            haitianSource, haitianProviderTarget, haitianVerifiedTarget,
            spanishSource, spanishProviderTarget,
            haitianObservation, HumanAlignment(haitian, haitianSource, haitianVerifiedTarget), spanishObservation);
        await db.SaveChangesAsync();

        await fixture.Intelligence.EvaluateProviderObservationAsync(haitianObservation.Id);
        await fixture.Intelligence.EvaluateProviderObservationAsync(spanishObservation.Id);

        var haitianEvidence = await db.LegendTranslationQualityEvidence
            .Where(item => item.ObservedAlignmentId == haitianObservation.Id).ToListAsync();
        var spanishEvidence = await db.LegendTranslationQualityEvidence
            .Where(item => item.ObservedAlignmentId == spanishObservation.Id).ToListAsync();
        Assert.Contains(haitianEvidence, item => item.Signal == "Contradictory");
        Assert.DoesNotContain(spanishEvidence, item => item.Signal == "Contradictory");
        Assert.Contains(spanishEvidence, item => item.Signal == "Insufficient");
    }

    [Fact]
    public async Task CurrentRegisteredLanguages_UseTheSameQualityAuthority()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var languageCodes = new[] { "ht", "es", "fr", "ja" };
        var observations = new List<LegendTranslationAlignment>();
        foreach (var languageCode in languageCodes)
        {
            var pair = await PairAsync(fixture.Registry, languageCode);
            var source = Unit("en", $"Shared quality source for {languageCode}.", "FounderApproved");
            var target = Unit(languageCode, $"[{languageCode} provider] target", "ProviderDerived");
            var observation = ProviderObservation(pair, source, target);
            observations.Add(observation);
            db.AddRange(source, target, observation);
        }
        await db.SaveChangesAsync();

        foreach (var observation in observations)
            await fixture.Intelligence.EvaluateProviderObservationAsync(observation.Id);

        var evidence = await db.LegendTranslationQualityEvidence.ToListAsync();
        Assert.Equal(languageCodes.Length, evidence.Count(item => item.Signal == "Insufficient"));
        Assert.Equal(languageCodes.Length, evidence.Select(item => item.PairKey).Distinct().Count());
    }

    [Fact]
    public async Task FutureRegisteredLanguage_UsesTheSameCorrectionAndTrustedMemoryPath()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db, FutureLanguageConfiguration());
        Assert.Contains(await fixture.Registry.ListEnabledTranslationLanguagesAsync(), item => item.Code == "x-test");
        var pair = await PairAsync(fixture.Registry, "x-test");
        var source = Unit("en", "Future registry quality source.", "FounderApproved");
        var target = Unit("x-test", "[x-test provider] observation", "ProviderDerived");
        var provider = ProviderObservation(pair, source, target);
        db.AddRange(source, target, provider);
        await db.SaveChangesAsync();
        await fixture.Intelligence.EvaluateProviderObservationAsync(provider.Id);

        var corrected = await fixture.Operations.CorrectFounderKnowledgeAsync(
            "founder", provider.Id,
            new LegendConnectKnowledgeSubmission("en", source.Text, "x-test", "[x-test human] correction", null, null, null, "FounderApproved"));

        Assert.True(corrected.Succeeded, corrected.Message);
        Assert.Equal("ProviderDerived", (await db.LegendTranslationAlignments.SingleAsync(item => item.Id == provider.Id)).Provenance);
        Assert.Contains(await db.LegendTranslationQualityEvidence.ToListAsync(), item =>
            item.ObservedAlignmentId == provider.Id && item.ReasonCode == "human_verified_directional_correction");
        var memory = await fixture.Intelligence.TryGetTrustedExactMemoryAsync("en", "x-test", source.Text);
        Assert.Equal("[x-test human] correction", memory?.Text);
    }

    [Fact]
    public async Task ExactHumanVerifiedMemory_PrecedesProviderObservation_WhileObservationAloneRemainsReusableFallback()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var pair = await PairAsync(fixture.Registry, "ht");
        var trustedSource = Unit("en", "Trusted exact source.", "FounderApproved");
        var trustedTarget = Unit("ht", "Sib egzak verifye.", "FounderApproved");
        var observedSource = Unit("en", "Unverified provider source.", "FounderApproved");
        var observedTarget = Unit("ht", "Sib obsèvasyon sèlman.", "ProviderDerived");
        db.AddRange(
            trustedSource, trustedTarget, HumanAlignment(pair, trustedSource, trustedTarget),
            observedSource, observedTarget, ProviderObservation(pair, observedSource, observedTarget));
        await db.SaveChangesAsync();

        Assert.Equal("Sib egzak verifye.", (await fixture.Intelligence.TryGetTrustedExactMemoryAsync("en", "ht", trustedSource.Text))?.Text);
        var observedMemory =
            await fixture.Intelligence.TryGetTrustedExactMemoryAsync(
                "en",
                "ht",
                observedSource.Text);

        Assert.NotNull(observedMemory);
        Assert.Equal("Sib obsèvasyon sèlman.", observedMemory!.Text);
        Assert.Equal("ProviderDerived", observedMemory.Provenance);
        Assert.Equal("Observation", observedMemory.QualityState);

        var persistedObservation =
            await db.LegendTranslationAlignments.SingleAsync(item =>
                item.SourceTextUnitId == observedSource.Id &&
                item.Provenance ==
                    LegendConnectKnowledgeProvenance.ProviderDerived);

        Assert.False(persistedObservation.HumanVerified);
        Assert.Equal(
            "ProviderDerived",
            persistedObservation.Provenance);
        Assert.Equal(
            "Observation",
            persistedObservation.QualityState);
    }

    [Fact]
    public async Task RejectedProviderKnowledge_IsHistoricalOnlyAndContributesNoActiveQualityOrMemory()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var pair = await PairAsync(fixture.Registry, "ht");
        var source = Unit("en", "Reject this provider observation.", "FounderApproved");
        var target = Unit("ht", "Rejte obsèvasyon sa a.", "ProviderDerived");
        var observation = ProviderObservation(pair, source, target);
        db.AddRange(source, target, observation);
        await db.SaveChangesAsync();
        await fixture.Intelligence.EvaluateProviderObservationAsync(observation.Id);

        var result = await fixture.Operations.RejectProviderObservationAsync("founder", observation.Id);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull((await db.LegendTranslationAlignments.SingleAsync()).SupersededUtc);
        Assert.False((await db.LegendLanguageTextUnits.SingleAsync(item => item.Id == target.Id)).IsTrainingEligible);
        Assert.Equal(0, (await fixture.Intelligence.GetTranslationQualityAsync()).ProviderObservationCount);
        Assert.Null(await fixture.Intelligence.TryGetTrustedExactMemoryAsync("en", "ht", source.Text));
        Assert.NotEmpty(await db.LegendTranslationQualityEvidence.ToListAsync());
    }

    private static QualityFixture CreateFixture(MasterAppDbContext db, IConfiguration? configuration = null)
    {
        configuration ??= Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db, registry, NullLogger<LegendConnectCorpusService>.Instance, intelligence: intelligence);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(
            db, registry, corpus, configuration, curriculum: curriculum, intelligence: intelligence);
        return new QualityFixture(registry, intelligence, corpus, curriculum, operations);
    }

    private static async Task<LegendLanguagePairSnapshot> PairAsync(LegendLanguageRegistry registry, string targetLanguageCode) =>
        Assert.IsType<LegendLanguagePairSnapshot>(await registry.GetOrCreateEnabledPairAsync("en", targetLanguageCode));

    private static LegendLanguageTextUnit Unit(string languageCode, string text, string provenance) => new()
    {
        Id = Guid.NewGuid(),
        LanguageCode = languageCode,
        StoragePartition = LegendLanguageIdentity.DatasetNamespace(languageCode),
        NormalizedHash = LegendLanguageIdentity.TextHash(text),
        Text = LegendLanguageIdentity.NormalizeText(text),
        Provenance = provenance,
        IsTrainingEligible = true
    };

    private static LegendTranslationAlignment ProviderObservation(
        LegendLanguagePairSnapshot pair,
        LegendLanguageTextUnit source,
        LegendLanguageTextUnit target) => new()
    {
        Id = Guid.NewGuid(), PairKey = pair.PairKey, SourceTextUnitId = source.Id, TargetTextUnitId = target.Id,
        Provider = "AzureTranslator", Provenance = "ProviderDerived", QualityState = "Observation", ObservationCount = 1
    };

    private static LegendTranslationAlignment HumanAlignment(
        LegendLanguagePairSnapshot pair,
        LegendLanguageTextUnit source,
        LegendLanguageTextUnit target) => new()
    {
        Id = Guid.NewGuid(), PairKey = pair.PairKey, SourceTextUnitId = source.Id, TargetTextUnitId = target.Id,
        Provider = "FounderApproved", Provenance = "FounderApproved", Confidence = 1m,
        QualityState = "Verified", HumanVerified = true, ObservationCount = 1
    };

    private static LegendConnectCurriculumBatchSubmission CurriculumBatch(
        params (string Text, string Person, string Action)[] examples) => new(
        "quality.crossbatch.person",
        "Cross-batch Founder evidence",
        examples.Select(item => new LegendConnectCurriculumExampleSubmission(
            item.Text,
            new Dictionary<string, string>
            {
                ["person"] = item.Person,
                ["action"] = item.Action
            })).ToList());

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:CorpusAcquisition:Enabled"] = "true",
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "100000",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "0"
        }).Build();

    private static IConfiguration FutureLanguageConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:CorpusAcquisition:Enabled"] = "true",
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "100000",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "0",
            ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
            ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:1:Code"] = "x-test",
            ["LegendConnect:LanguageRegistry:Baseline:1:Name"] = "Future test language",
            ["LegendConnect:LanguageRegistry:Baseline:1:NativeName"] = "Future test language"
        }).Build();

    private sealed record QualityFixture(
        LegendLanguageRegistry Registry,
        LegendConnectTranslationIntelligence Intelligence,
        LegendConnectCorpusService Corpus,
        LegendConnectCurriculumService Curriculum,
        LegendConnectOperations Operations);

    [Fact]
    public async Task ExactMemory_PrefersFounderOverSystemValidatedAndProviderObservation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var pair = await PairAsync(fixture.Registry, "ht");

        var source = Unit("en", "Priority source.", "FounderApproved");
        var providerTarget = Unit("ht", "Rezilta Azure.", "ProviderDerived");
        var founderTarget = Unit("ht", "Rezilta Fondatè.", "FounderApproved");

        var provider = ProviderObservation(pair, source, providerTarget);
        provider.QualityState = "SystemValidated";
        provider.Confidence = 0.98m;

        var founder = HumanAlignment(pair, source, founderTarget);

        db.AddRange(source, providerTarget, founderTarget, provider, founder);
        await db.SaveChangesAsync();

        var memory = await fixture.Intelligence.TryGetTrustedExactMemoryAsync(
            "en", "ht", source.Text);

        Assert.NotNull(memory);
        Assert.Equal(founderTarget.Text, memory!.Text);
        Assert.Equal("FounderApproved", memory.Provenance);
    }


    [Fact]
    public async Task ContradictedProviderObservation_CannotBeServedAsReusableMemory()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var pair = await PairAsync(fixture.Registry, "ht");

        var source = Unit("en", "Contradicted reusable source.", "FounderApproved");
        var providerTarget = Unit("ht", "Move Azure.", "ProviderDerived");
        var founderTarget = Unit("ht", "Move Fondatè.", "FounderApproved");

        var provider = ProviderObservation(pair, source, providerTarget);
        var founder = HumanAlignment(pair, source, founderTarget);

        db.AddRange(source, providerTarget, founderTarget, provider, founder);
        await db.SaveChangesAsync();

        await fixture.Intelligence.EvaluateProviderObservationAsync(provider.Id);

        var memory = await fixture.Intelligence.TryGetTrustedExactMemoryAsync(
            "en", "ht", source.Text);

        Assert.NotNull(memory);
        Assert.Equal(founderTarget.Text, memory!.Text);
    }


}
