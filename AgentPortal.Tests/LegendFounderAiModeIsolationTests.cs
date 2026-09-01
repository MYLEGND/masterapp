using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Security;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Contract tests for the single Founder chat endpoint's responder boundary.
/// They deliberately use the real orchestration service with a recorded
/// ILegendConnectOperations adapter, rather than a second chat endpoint.
/// </summary>
[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendFounderAiModeIsolationTests
{
    [Fact]
    public async Task TeacherMode_CallsOpenAiDirectlyAndNeverCallsNativeLegendInference()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var handler = new FounderAiScenarioHandler(
            ProviderText("The OpenAI Teacher is responding directly."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            ControllerTestHelpers.BuildUser(),
            Request("teacher", "Explain the current repair boundary."));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("teacher", response.Mode);
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal("provider_response", response.Stage);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Fact]
    public async Task TeacherMode_ProgressResultStreamRemainsActiveBeyondFormerGatewayBoundary()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot(
                "bounded diagnostic",
                0,
                []));
        var handler = new FounderAiScenarioHandler(
            TimeSpan.FromSeconds(16),
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"bounded diagnostic\"}"),
            ProviderText("The OpenAI Teacher completed the bounded request after sustained progress."));
        var service = CreateService(db, operations.Object, handler);
        var body = new MemoryStream();
        var context = ControllerContextFor(founder);
        context.HttpContext.Request.Headers.Accept = "application/x-ndjson";
        context.HttpContext.Response.Body = body;
        var controller = new LegendFounderAiController(
            service,
            new LegendFounderAiProgressBroker(),
            NullLogger<LegendFounderAiController>.Instance)
        {
            ControllerContext = context
        };

        var result = await controller.Chat(
            Request("teacher", "Complete this bounded diagnostic with the required governed inspection."),
            CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        var transcript = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("\"type\":\"accepted\"", transcript, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"heartbeat\"", transcript, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"result\"", transcript, StringComparison.Ordinal);
        Assert.Contains("OpenAITeacher", transcript, StringComparison.Ordinal);
        Assert.Contains("provider_response", transcript, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestCount);
        operations.Verify(operation => operation.SearchRetainedKnowledgeAsync(
            "bounded diagnostic",
            null,
            null,
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Fact]
    public async Task LegendMode_ProgressResultStreamKeepsGovernedNativeFirstRequestActiveBeyondFormerGatewayBoundary()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                "Explain the governed gap.",
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(),
                "en"))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                false,
                0m,
                null,
                "insufficient_evidence",
                0,
                "External escalation is permitted after governed native inference.",
                true));
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot(
                "governed gap",
                0,
                []));

        var handler = new FounderAiScenarioHandler(
            TimeSpan.FromSeconds(31),
            ProviderText("The OpenAI Teacher completed the permitted escalation after LEGEND’s governed first refusal."));
        var service = CreateService(db, operations.Object, handler);
        var body = new MemoryStream();
        var context = ControllerContextFor(founder);
        context.HttpContext.Request.Headers.Accept = "application/x-ndjson";
        context.HttpContext.Response.Body = body;
        var controller = new LegendFounderAiController(
            service,
            new LegendFounderAiProgressBroker(),
            NullLogger<LegendFounderAiController>.Instance)
        {
            ControllerContext = context
        };

        var result = await controller.Chat(
            Request("legend", "Explain the governed gap."),
            CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        var transcript = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("\"type\":\"heartbeat\"", transcript, StringComparison.Ordinal);
        Assert.Contains("OpenAITeacher", transcript, StringComparison.Ordinal);
        Assert.Contains("provider_response", transcript, StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, NativeInferenceCalls(operations));
    }

    [Fact]
    public async Task TeacherMode_GovernedReadToolMayExecuteWithoutChangingResponderIdentity()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot(
                "authority",
                0,
                []));

        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"authority\"}"),
            ProviderText("The governed inspection completed; I remain the OpenAI Teacher responder."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
            Request("teacher", "Inspect the current authority."));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal(2, handler.RequestCount);
        operations.Verify(operation => operation.SearchRetainedKnowledgeAsync(
            "authority",
            null,
            null,
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Theory]
    [InlineData("Hello, can you help me?", "en", "en")]
    [InlineData("Bonjour, pouvez-vous m’aider?", "fr-FR", "fr")]
    [InlineData("Bonjou, èske ou ka ede m?", "ht", "ht")]
    public async Task MissingSourceLanguage_UsesGovernedIdentificationBeforeNativeMeaning(
        string prompt,
        string detectedLanguage,
        string expectedLanguage)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                prompt,
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(),
                expectedLanguage))
            .ReturnsAsync(NativeLanguageAnswer(expectedLanguage));
        var detector = new FounderAiLanguageDetector(
            new TranslationDetectionResult(
                true,
                detectedLanguage,
                Confidence: 1m));
        var service = CreateService(
            db,
            operations.Object,
            new FounderAiScenarioHandler(),
            detector);

        var response = await service.ReplyAsync(
            founder,
            Request(
                "legend",
                prompt,
                nativeOnly: true,
                sourceLanguageCode: null));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("native_response", response.Stage);
        Assert.Equal("Unavailable", response.ModelAssistanceState);
        Assert.Equal("active_reasoning_model_unavailable", response.ModelAssistanceReason);
        Assert.Equal(1, detector.DetectionCount);
        operations.Verify(operation => operation.TryInferConversationWithDiscourseAsync(
            prompt,
            It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
            It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
            It.IsAny<CancellationToken>(),
            expectedLanguage), Times.Once);
    }

    [Fact]
    public async Task DeclaredSourceLanguage_IsNormalizedByRegistryWithoutDetection()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                "Expliquez cette distinction.",
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(),
                "fr"))
            .ReturnsAsync(NativeLanguageAnswer("fr"));
        var detector = new FounderAiLanguageDetector(
            new TranslationDetectionResult(false, null, "must_not_detect"));
        var service = CreateService(
            db,
            operations.Object,
            new FounderAiScenarioHandler(),
            detector);

        var response = await service.ReplyAsync(
            founder,
            Request(
                "legend",
                "Expliquez cette distinction.",
                nativeOnly: true,
                sourceLanguageCode: " fr_fr "));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal(0, detector.DetectionCount);
        operations.Verify(operation => operation.TryInferConversationWithDiscourseAsync(
            "Expliquez cette distinction.",
            It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
            It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
            It.IsAny<CancellationToken>(),
            "fr"), Times.Once);
    }

    [Theory]
    [InlineData("translation_language_ambiguous", "source_language_ambiguous", 422)]
    [InlineData("translation_language_unsupported", "source_language_unsupported", 422)]
    [InlineData("translation_provider_failed", "source_language_identification_unavailable", 503)]
    public async Task MissingSourceLanguage_FailureFailsClosedWithExactDiagnostic(
        string detectorError,
        string expectedReason,
        int expectedStatus)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var detector = new FounderAiLanguageDetector(
            new TranslationDetectionResult(false, null, detectorError));
        var service = CreateService(
            db,
            operations.Object,
            new FounderAiScenarioHandler(),
            detector);

        var response = await service.ReplyAsync(
            founder,
            Request(
                "legend",
                "Ambiguous language sample",
                nativeOnly: true,
                sourceLanguageCode: null));

        Assert.False(response.Succeeded);
        Assert.Equal("language_identification", response.FailureKind);
        Assert.Equal("source_language_identification", response.Stage);
        Assert.Equal(expectedReason, response.Reason);
        Assert.Equal(expectedStatus, StatusFor(response));
        Assert.Contains(
            $"SourceLanguageFailure={expectedReason}",
            response.Error,
            StringComparison.Ordinal);
        Assert.Empty(operations.Invocations);
    }

    [Fact]
    public async Task DetectedButRegistryUnsupportedLanguage_FailsClosedBeforeMeaningGraph()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var detector = new FounderAiLanguageDetector(
            new TranslationDetectionResult(true, "it", Confidence: 1m));
        var service = CreateService(
            db,
            operations.Object,
            new FounderAiScenarioHandler(),
            detector);

        var response = await service.ReplyAsync(
            founder,
            Request(
                "legend",
                "Ciao, puoi aiutarmi?",
                nativeOnly: true,
                sourceLanguageCode: null));

        Assert.False(response.Succeeded);
        Assert.Equal("source_language_unsupported", response.Reason);
        Assert.Equal(1, detector.DetectionCount);
        Assert.Empty(operations.Invocations);
    }

    [Theory]
    [InlineData("en<script>", "source_language_code_invalid")]
    [InlineData("x-spoofed", "source_language_unsupported")]
    public async Task SpoofedDeclaredSourceLanguage_FailsClosedBeforeDetectionOrMeaningGraph(
        string declaredCode,
        string expectedReason)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var detector = new FounderAiLanguageDetector(
            new TranslationDetectionResult(true, "en", Confidence: 1m));
        var service = CreateService(
            db,
            operations.Object,
            new FounderAiScenarioHandler(),
            detector);

        var response = await service.ReplyAsync(
            founder,
            Request(
                "legend",
                "Bonjour",
                nativeOnly: true,
                sourceLanguageCode: declaredCode));

        Assert.False(response.Succeeded);
        Assert.Equal(expectedReason, response.Reason);
        Assert.Equal(0, detector.DetectionCount);
        Assert.Empty(operations.Invocations);
    }

    [Fact]
    public async Task SourceLanguageIdentification_DoesNotRunBeforeFounderAuthorization()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var detector = new FounderAiLanguageDetector(
            new TranslationDetectionResult(true, "en", Confidence: 1m));
        var service = CreateService(
            db,
            operations.Object,
            new FounderAiScenarioHandler(),
            detector);

        await Assert.ThrowsAsync<ForbidResultException>(() => service.ReplyAsync(
            ControllerTestHelpers.BuildUser("not-the-founder"),
            Request(
                "legend",
                "Hello",
                nativeOnly: true,
                sourceLanguageCode: null)));

        Assert.Equal(0, detector.DetectionCount);
        Assert.Empty(operations.Invocations);
    }

    [Fact]
    public async Task NativeReadOnlyContentBinding_UsesFounderToolAuthorityAndProducesZeroWriteProvenance()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var readRequest = ReadOnlyContentRequest();
        operations
            .Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                "What is the current open issue count?",
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(),
                "en"))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                false,
                0m,
                null,
                "read_only_content_binding_required",
                3,
                "One governed read is required.",
                false,
                ReadOnlyContentRequest: readRequest));
        operations
            .Setup(operation => operation.GetTranslationQualityAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectTranslationQualitySnapshot(
                4_458,
                2_999,
                300,
                12,
                8_796,
                []));
        LegendConnectReadOnlyContentBindingReceipt? observedReceipt = null;
        operations
            .Setup(operation => operation.TryInferConversationWithReadOnlyContentAsync(
                "What is the current open issue count?",
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<LegendConnectReadOnlyContentBindingReceipt>(),
                It.IsAny<CancellationToken>(),
                "en"))
            .Callback((string _input,
                IReadOnlyList<LegendConnectConversationContextItem> _context,
                LegendConnectDiscourseStateSnapshot? _discourseState,
                LegendConnectReadOnlyContentBindingReceipt receipt,
                CancellationToken _cancellationToken,
                string _language) => observedReceipt = receipt)
            .ReturnsAsync(() => new LegendConnectNativeInferenceSnapshot(
                true,
                1m,
                "Open issues 4,458.",
                "semantic_transition_governed_composed",
                4,
                "Founder-authorized read-only content was bound with provenance.",
                false,
                "HigherStandard",
                "OriginalComposition",
                ContentBindingProvenance: observedReceipt is null ? null : [observedReceipt]));
        var handler = new FounderAiScenarioHandler();
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
            Request(
                "legend",
                "What is the current open issue count?",
                nativeOnly: true));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("native_response", response.Stage);
        Assert.Equal("LegendAi", response.ResponseAuthority);
        Assert.Equal("Open issues 4,458.", response.Message);
        Assert.Equal(0, handler.RequestCount);
        var receipt = Assert.IsType<LegendConnectReadOnlyContentBindingReceipt>(observedReceipt);
        Assert.Equal("4458", receipt.SemanticValue);
        Assert.Equal(LegendConnectReadOnlyContentBindingContracts.Provenance, receipt.Provenance);
        Assert.True(receipt.IsReadOnly);
        Assert.True(receipt.ZeroWrite);
        var permittedReads = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(ILegendConnectOperations.AnalyzeReusableMeaningGraphAsync),
            nameof(ILegendConnectOperations.TryInferConversationWithDiscourseAsync),
            nameof(ILegendConnectOperations.GetTranslationQualityAsync),
            nameof(ILegendConnectOperations.TryInferConversationWithReadOnlyContentAsync)
        };
        Assert.All(operations.Invocations, invocation =>
            Assert.True(
                permittedReads.Contains(invocation.Method.Name),
                $"Unexpected non-read operation: {invocation.Method.Name}"));
    }

    [Fact]
    public async Task NativeReadOnlyContentBinding_DoesNotExecuteBeforeFounderAuthorization()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var legend = new FounderLegendConnectService(
            operations.Object,
            new AgentProfileAccessResolver(db));
        var authority = new LegendFounderToolAuthority(legend, null);

        await Assert.ThrowsAsync<ForbidResultException>(() =>
            authority.BindReadOnlyResultAsync(
                ControllerTestHelpers.BuildUser("not-the-founder"),
                ReadOnlyContentRequest(),
                CancellationToken.None));

        Assert.Empty(operations.Invocations);
    }

    [Fact]
    public async Task NativeReadOnlyContentBinding_RejectsUnavailableAndNonPermittedTools()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var authority = new LegendFounderToolAuthority(
            new FounderLegendConnectService(
                operations.Object,
                new AgentProfileAccessResolver(db)),
            null);

        Assert.True(authority.IsNativeContentBindingRead("legend_translation_quality"));
        Assert.False(authority.IsNativeContentBindingRead("legend_submit_founder_curriculum"));
        Assert.False(authority.IsNativeContentBindingRead("legend_inspect_repository"));
        Assert.False(authority.IsNativeContentBindingRead("legend_provider_capacity"));

        var unavailable = await authority.BindReadOnlyResultAsync(
            ControllerTestHelpers.BuildUser(),
            ReadOnlyContentRequest() with { ToolName = "legend_unknown_read" },
            CancellationToken.None);
        var mutation = await authority.BindReadOnlyResultAsync(
            ControllerTestHelpers.BuildUser(),
            ReadOnlyContentRequest() with { ToolName = "legend_submit_founder_curriculum" },
            CancellationToken.None);
        var repository = await authority.BindReadOnlyResultAsync(
            ControllerTestHelpers.BuildUser(),
            ReadOnlyContentRequest() with { ToolName = "legend_inspect_repository" },
            CancellationToken.None);
        var malformedArguments = await authority.BindReadOnlyResultAsync(
            ControllerTestHelpers.BuildUser(),
            ReadOnlyContentRequest() with { ArgumentsJson = "{\"extra\":true}" },
            CancellationToken.None);

        Assert.Equal("read_only_content_binding_tool_unavailable", unavailable.ReasonCode);
        Assert.Equal("read_only_content_binding_tool_not_read_only", mutation.ReasonCode);
        Assert.Equal("read_only_content_binding_tool_not_permitted", repository.ReasonCode);
        Assert.Equal(
            "read_only_content_binding_arguments_invalid",
            malformedArguments.ReasonCode);
        Assert.Empty(operations.Invocations);
    }

    [Fact]
    public void NativeReadOnlyContentBinding_RejectsMalformedAndStaleToolOutput()
    {
        var request = ReadOnlyContentRequest();
        var now = DateTime.UtcNow;

        Assert.False(LegendFounderToolAuthority.TryCreateReadOnlyContentBindingReceipt(
            request,
            "{\"needsReviewCount\":{\"unexpected\":4458}}",
            now,
            out var malformed,
            out var malformedReason));
        Assert.Null(malformed);
        Assert.Equal("read_only_content_binding_output_malformed", malformedReason);

        var freshnessRequest = request with
        {
            ObservedUtcPath = "refreshedUtc",
            MaximumAgeSeconds = 30
        };
        Assert.False(LegendFounderToolAuthority.TryCreateReadOnlyContentBindingReceipt(
            freshnessRequest,
            JsonSerializer.Serialize(new
            {
                needsReviewCount = 4458,
                refreshedUtc = now.AddMinutes(-5)
            }),
            now,
            out var stale,
            out var staleReason));
        Assert.Null(stale);
        Assert.Equal("read_only_content_binding_stale", staleReason);
    }

    [Fact]
    public async Task NativeGap_DoesNotSubmitMachineProposalWithoutFounderConfirmation()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                "Translate this unsupported distinction.",
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(),
                "en"))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                false, 0m, null, "meaning_graph_component_unknown", 0,
                "A reusable meaning distinction is missing.", true));
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot("gap", 0, []));
        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_submit_machine_learning_candidate", MachineProposalArguments()),
            ProviderText("I answered the request without making a durable learning mutation."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
            Request("legend", "Translate this unsupported distinction."));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.DoesNotContain("LEGEND_GOVERNED_LEARNING_RECEIPT", response.Message);
        operations.Verify(operation => operation.SubmitMachineTeachingProposalAsync(
            It.IsAny<LegendConnectMachineTeachingSubmission>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TeacherMode_ExplicitConfirmedTraining_ExecutesCanonicalProposalTool()
    {
        var candidateId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var proposalId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                "reusable distinction",
                null,
                null,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot(
                "reusable distinction", 0, []));
        operations
            .Setup(operation => operation.SubmitMachineTeachingProposalAsync(
                It.IsAny<LegendConnectMachineTeachingSubmission>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectMachineTeachingSubmissionResult(
                true, false, "AwaitingCritic", null,
                "Retained as MachineProposed.", candidateId, proposalId));

        var handler = new FounderAiScenarioHandler(
            ProviderTool(
                "legend_search_retained_knowledge",
                "{\"query\":\"reusable distinction\"}"),
            ProviderTool(
                "legend_submit_machine_learning_candidate",
                SameLanguageMachineProposalArguments()),
            ProviderText("The exact teaching family entered the governed critic lifecycle."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
            Request(
                "teacher",
                "Train LEGEND on this exact reusable distinction.",
                founderCommandConfirmed: true));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal(3, handler.RequestCount);
        Assert.Contains("LEGEND_GOVERNED_LEARNING_RECEIPT", response.Message);
        Assert.Contains(candidateId.ToString(), response.Message);
        Assert.Contains(proposalId.ToString(), response.Message);
        Assert.Contains("AwaitingCritic", response.Message);
        Assert.Contains("MachineProposed", response.Message);
        Assert.Contains("NonServing", response.Message);
        Assert.Contains("NonCanonical", response.Message);
        var responseMessage = Assert.IsType<string>(response.Message);
        var serializedReceipt = responseMessage[
            (responseMessage.IndexOf(
                "LEGEND_GOVERNED_LEARNING_RECEIPT",
                StringComparison.Ordinal) +
             "LEGEND_GOVERNED_LEARNING_RECEIPT".Length)..].Trim();
        var receipt = Assert.IsType<LegendConnectMachineTeachingMutationReceipt>(
            JsonSerializer.Deserialize<LegendConnectMachineTeachingMutationReceipt>(
                serializedReceipt,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.True(receipt.Succeeded);
        Assert.True(Guid.TryParseExact(receipt.AuthorizationCorrelation, "N", out _));
        operations.Verify(operation => operation.SearchRetainedKnowledgeAsync(
            "reusable distinction",
            null,
            null,
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
        operations.Verify(operation => operation.SubmitMachineTeachingProposalAsync(
            It.Is<LegendConnectMachineTeachingSubmission>(submission =>
                submission.SourceLanguageCode == "en" &&
                submission.TargetLanguageCode == "en" &&
                submission.CapabilityIdentity ==
                    LegendConnectMachineTeachingSubmission.SameLanguageSemanticCapability &&
                submission.CategoryIdentity ==
                    LegendConnectMachineTeachingSubmission.ReusableSemanticCategory),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Fact]
    public async Task MachineProposalMutation_RejectsReplayedAuthorizationCorrelation()
    {
        var candidateId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var proposalId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.SubmitMachineTeachingProposalAsync(
                It.IsAny<LegendConnectMachineTeachingSubmission>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectMachineTeachingSubmissionResult(
                true, false, "AwaitingCritic", null,
                "Retained as MachineProposed.", candidateId, proposalId));
        var authority = new LegendFounderToolAuthority(
            new FounderLegendConnectService(
                operations.Object,
                new AgentProfileAccessResolver(db)),
            null);
        var correlation = Guid.NewGuid().ToString("N");
        var call = new FounderAiToolCall(
            "machine-proposal-call",
            "legend_submit_machine_learning_candidate",
            MachineProposalArguments(),
            new FounderAiMutationAuthorization(correlation));

        var first = await authority.ExecuteAsync(
            founder,
            call,
            "teacher",
            CancellationToken.None);
        var replay = await authority.ExecuteAsync(
            founder,
            call,
            "teacher",
            CancellationToken.None);

        Assert.True(LegendFounderAiConversationService.TryReadMachineTeachingMutationReceipt(
            first,
            correlation,
            out var receipt));
        Assert.NotNull(receipt);
        Assert.Contains("founder_mutation_authorization_replayed", replay);
        operations.Verify(operation => operation.SubmitMachineTeachingProposalAsync(
            It.IsAny<LegendConnectMachineTeachingSubmission>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MachineProposalMutation_RejectsExistingDurableProposalAsReplay()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.SubmitMachineTeachingProposalAsync(
                It.IsAny<LegendConnectMachineTeachingSubmission>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectMachineTeachingSubmissionResult(
                true,
                true,
                "AwaitingCritic",
                null,
                "The exact proposal already exists.",
                Guid.NewGuid(),
                Guid.NewGuid(),
                ProposalAlreadyExisted: true));
        var authority = new LegendFounderToolAuthority(
            new FounderLegendConnectService(
                operations.Object,
                new AgentProfileAccessResolver(db)),
            null);
        var correlation = Guid.NewGuid().ToString("N");

        var output = await authority.ExecuteAsync(
            founder,
            new FounderAiToolCall(
                "existing-machine-proposal",
                "legend_submit_machine_learning_candidate",
                MachineProposalArguments(),
                new FounderAiMutationAuthorization(correlation)),
            "teacher",
            CancellationToken.None);

        Assert.Contains("machine_learning_mutation_replay", output);
        Assert.False(LegendFounderAiConversationService.TryReadMachineTeachingMutationReceipt(
            output,
            correlation,
            out var receipt));
        Assert.Null(receipt);
        operations.Verify(operation => operation.SubmitMachineTeachingProposalAsync(
            It.IsAny<LegendConnectMachineTeachingSubmission>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SameLanguageMachineProposal_RequiresAuthenticatedFounderAuthorization()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var authority = new LegendFounderToolAuthority(
            new FounderLegendConnectService(
                operations.Object,
                new AgentProfileAccessResolver(db)),
            null);
        var unconfirmed = new FounderAiToolCall(
            "unconfirmed-machine-proposal",
            "legend_submit_machine_learning_candidate",
            SameLanguageMachineProposalArguments());

        var unconfirmedOutput = await authority.ExecuteAsync(
            ControllerTestHelpers.BuildUser(),
            unconfirmed,
            "teacher",
            CancellationToken.None);
        Assert.Contains("founder_command_confirmation_required", unconfirmedOutput);

        var malformedAuthorization = unconfirmed with
        {
            MutationAuthorization = new FounderAiMutationAuthorization(
                "not-an-authorization-correlation")
        };
        var malformedAuthorizationOutput = await authority.ExecuteAsync(
            ControllerTestHelpers.BuildUser(),
            malformedAuthorization,
            "teacher",
            CancellationToken.None);
        Assert.Contains(
            "founder_mutation_authorization_invalid",
            malformedAuthorizationOutput);

        var unauthorized = unconfirmed with
        {
            MutationAuthorization = new FounderAiMutationAuthorization(
                Guid.NewGuid().ToString("N"))
        };
        await Assert.ThrowsAsync<ForbidResultException>(() => authority.ExecuteAsync(
            ControllerTestHelpers.BuildUser("not-the-founder"),
            unauthorized,
            "teacher",
            CancellationToken.None));
        Assert.Empty(operations.Invocations);
    }

    [Fact]
    public void MachineProposalReceipt_RejectsMalformedAndMissingIdentity()
    {
        var correlation = Guid.NewGuid().ToString("N");

        Assert.False(LegendFounderAiConversationService.TryReadMachineTeachingMutationReceipt(
            "not-json",
            correlation,
            out var malformed));
        Assert.Null(malformed);

        var completeReceipt = new
        {
            succeeded = true,
            candidateId = Guid.NewGuid(),
            proposalId = Guid.NewGuid(),
            durableState = "AwaitingCritic",
            provenance = "MachineProposed",
            authorizationCorrelation = correlation,
            servingStatus = "NonServing",
            canonicalStatus = "NonCanonical"
        };
        var missingCandidateId = JsonSerializer.Serialize(new
        {
            completeReceipt.succeeded,
            completeReceipt.proposalId,
            completeReceipt.durableState,
            completeReceipt.provenance,
            completeReceipt.authorizationCorrelation,
            completeReceipt.servingStatus,
            completeReceipt.canonicalStatus
        });
        Assert.False(LegendFounderAiConversationService.TryReadMachineTeachingMutationReceipt(
            missingCandidateId,
            correlation,
            out var missingIdentity));
        Assert.Null(missingIdentity);

        var missingProposalId = JsonSerializer.Serialize(new
        {
            completeReceipt.succeeded,
            completeReceipt.candidateId,
            completeReceipt.durableState,
            completeReceipt.provenance,
            completeReceipt.authorizationCorrelation,
            completeReceipt.servingStatus,
            completeReceipt.canonicalStatus
        });
        Assert.False(LegendFounderAiConversationService.TryReadMachineTeachingMutationReceipt(
            missingProposalId,
            correlation,
            out missingIdentity));
        Assert.Null(missingIdentity);
    }

    [Fact]
    public void MachineProposalReceipt_RejectsFalseSuccessAndReplayedCorrelation()
    {
        var correlation = Guid.NewGuid().ToString("N");
        var receipt = new LegendConnectMachineTeachingMutationReceipt(
            false,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "AwaitingCritic",
            LegendConnectMachineTeachingMutationReceipt.RequiredProvenance,
            correlation,
            LegendConnectMachineTeachingMutationReceipt.RequiredServingStatus,
            LegendConnectMachineTeachingMutationReceipt.RequiredCanonicalStatus);
        var output = JsonSerializer.Serialize(
            receipt,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.False(LegendFounderAiConversationService.TryReadMachineTeachingMutationReceipt(
            output,
            correlation,
            out var falseSuccess));
        Assert.Null(falseSuccess);

        output = JsonSerializer.Serialize(
            receipt with { Succeeded = true },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.False(LegendFounderAiConversationService.TryReadMachineTeachingMutationReceipt(
            output,
            Guid.NewGuid().ToString("N"),
            out var replayed));
        Assert.Null(replayed);
    }

    [Fact]
    public async Task ConfirmedMachineProposal_ProviderFailureCreatesNoMutationReceipt()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var rejected = ProviderResponse(new
        {
            error = new { message = "controlled teaching rejection" }
        }, HttpStatusCode.BadRequest);
        rejected.Headers.TryAddWithoutValidation("x-request-id", "teaching-provider-failure");
        var service = CreateService(
            db,
            operations.Object,
            new FounderAiScenarioHandler(rejected));

        var response = await service.ReplyAsync(
            ControllerTestHelpers.BuildUser(),
            Request(
                "teacher",
                "Train LEGEND on this exact reusable distinction.",
                founderCommandConfirmed: true));

        Assert.False(response.Succeeded);
        Assert.Equal("provider_http_400", response.Reason);
        Assert.Equal("teaching-provider-failure", response.Reference);
        Assert.DoesNotContain(
            "LEGEND_GOVERNED_LEARNING_RECEIPT",
            response.Message ?? string.Empty);
        Assert.Empty(operations.Invocations);
    }

    [Fact]
    public async Task TeacherMode_GovernedToolReadFailureIsStructuredAndNeverBecomesHttp500()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("read transport failed"));

        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"authority\"}"),
            ProviderText("This answer is unsupported because its only governed read failed."));
        var service = CreateService(db, operations.Object, handler);
        var controller = new LegendFounderAiController(
            service,
            new LegendFounderAiProgressBroker(),
            NullLogger<LegendFounderAiController>.Instance)
        {
            ControllerContext = ControllerContextFor(founder)
        };

        var result = await controller.Chat(
            Request("teacher", "Inspect the current authority."),
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        var response = Assert.IsType<LegendFounderAiChatResponse>(objectResult.Value);
        Assert.Equal(502, objectResult.StatusCode);
        Assert.False(response.Succeeded);
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal("governed_tool", response.Stage);
        Assert.Equal("required_governed_inspection_missing", response.Reason);
        Assert.Equal("governed_inspection", response.FailureKind);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Fact]
    public async Task TeacherMode_FailedGovernedReadContinuesToAWorkingGovernedRead()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .SetupSequence(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("first read transport failed"))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot(
                "authority",
                1,
                []));

        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"authority\"}"),
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"authority\"}"),
            ProviderText("The second governed read succeeded and supports this assessment."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
            Request("teacher", "Inspect the current authority."));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal(
            "The second governed read succeeded and supports this assessment.",
            response.Message);
        Assert.Equal(3, handler.RequestCount);
        operations.Verify(operation => operation.SearchRetainedKnowledgeAsync(
            "authority",
            null,
            null,
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Fact]
    public async Task TeacherMode_IncompleteProviderAnswersAreReturnedAsOneCompleteResponse()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var handler = new FounderAiScenarioHandler(
            ProviderIncompleteText("Steps 1-4\nStep 5"),
            ProviderText("Step 5\nSteps 6-12"));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            ControllerTestHelpers.BuildUser(),
            Request("teacher", "Give one complete clean answer."));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal(
            "Steps 1-4\nStep 5\nSteps 6-12",
            response.Message);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Fact]
    public async Task TeacherMode_ProviderRejectionPreservesSafeProviderStatusAndReference()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var rejected = ProviderResponse(new
        {
            error = new { message = "safe test rejection" }
        }, HttpStatusCode.BadRequest);
        rejected.Headers.TryAddWithoutValidation("x-request-id", "provider-test-reference");
        var handler = new FounderAiScenarioHandler(rejected);
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            ControllerTestHelpers.BuildUser(),
            Request("teacher", "Return a controlled provider failure."));

        Assert.False(response.Succeeded);
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal("provider", response.Stage);
        Assert.Equal("provider_http_400", response.Reason);
        Assert.Equal("provider_http", response.FailureKind);
        Assert.Equal(400, response.ProviderStatusCode);
        Assert.Equal("provider-test-reference", response.Reference);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Fact]
    public async Task TeacherMode_GovernedToolCancellationIsStructuredTimeoutAndNeverBecomesHttp500()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"authority\"}"));
        var service = CreateService(db, operations.Object, handler);
        var controller = new LegendFounderAiController(
            service,
            new LegendFounderAiProgressBroker(),
            NullLogger<LegendFounderAiController>.Instance)
        {
            ControllerContext = ControllerContextFor(founder)
        };

        var result = await controller.Chat(
            Request("teacher", "Inspect the current authority."),
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        var response = Assert.IsType<LegendFounderAiChatResponse>(objectResult.Value);
        Assert.Equal(504, objectResult.StatusCode);
        Assert.False(response.Succeeded);
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal("governed_tool", response.Stage);
        Assert.Equal("tool_timeout", response.Reason);
        Assert.Equal("timeout", response.FailureKind);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("native")]
    public async Task MissingOrInvalidMode_FailsClosedWithoutLegendOrProvider(string? mode)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var handler = new FounderAiScenarioHandler();
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            ControllerTestHelpers.BuildUser(),
            Request(mode, "Hello."));

        Assert.False(response.Succeeded);
        Assert.Equal("invalid", response.Mode);
        Assert.Equal("NoResponder", response.ResponseAuthority);
        Assert.Equal("mode_validation", response.Stage);
        Assert.Equal("invalid_mode", response.Reason);
        Assert.Equal("validation", response.FailureKind);
        Assert.Equal(400, StatusFor(response));
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Fact]
    public async Task LegendMode_StillAttemptsNativeLegendFirstAndLabelsNativeResponseCorrectly()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                "Hello.",
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(),
                "en"))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                true,
                1m,
                "Governed native reply.",
                "supported",
                1,
                "FounderApproved evidence",
                false));

        var handler = new FounderAiScenarioHandler();
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
            Request("legend", "Hello."));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("legend", response.Mode);
        Assert.Equal("LegendAi", response.ResponseAuthority);
        Assert.Equal("native_response", response.Stage);
        Assert.Equal("Governed native reply.", response.Message);
        Assert.Equal(1, NativeInferenceCalls(operations));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task LegendMode_ThreadsTheGovernedSourceLanguageThroughMeaningAndNativeInference()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.AnalyzeReusableMeaningGraphAsync(
                "Hola.",
                It.IsAny<CancellationToken>(),
                "es"))
            .ReturnsAsync(new LegendConnectUtteranceMeaningGraphSnapshot(
                false,
                [],
                [],
                ["hola"],
                "meaning_graph_component_unknown"));
        operations
            .Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                "Hola.",
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(),
                "es"))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                false,
                0m,
                null,
                "meaning_graph_component_unknown",
                0,
                "The Spanish evidence partition does not yet support this request.",
                true));

        var handler = new FounderAiScenarioHandler();
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
            Request("legend", "Hola.", nativeOnly: true, sourceLanguageCode: "es"));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("native_only_blocked", response.Stage);
        Assert.Equal("meaning_graph_component_unknown", response.Reason);
        Assert.Equal(0, handler.RequestCount);
        operations.VerifyAll();
    }

    [Fact]
    public async Task LegendMode_NativeOnlyReturnsNativeAnswerWithoutCallingOpenAi()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                "Answer directly.",
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(),
                "en"))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                true,
                1m,
                "Native-only answer.",
                "supported",
                3,
                "FounderApproved evidence",
                false));

        var handler = new FounderAiScenarioHandler(
            ProviderText("This provider response must never be requested."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
            Request("legend", "Answer directly.", nativeOnly: true));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("LegendAi", response.ResponseAuthority);
        Assert.Equal("native_response", response.Stage);
        Assert.Equal("Native-only answer.", response.Message);
        Assert.Equal(1, NativeInferenceCalls(operations));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task LegendMode_NativeOnlyBlocksPermittedEscalationBeforeOpenAiIsCalled()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                "Explain the unsupported gap.",
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(),
                "en"))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                false,
                0m,
                null,
                "insufficient_evidence",
                0,
                "External escalation would normally be permitted.",
                true));

        var handler = new FounderAiScenarioHandler(
            ProviderText("This provider response must never be requested."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
            Request("legend", "Explain the unsupported gap.", nativeOnly: true));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("SystemDiagnostic", response.ResponseAuthority);
        Assert.Equal("native_only_blocked", response.Stage);
        Assert.Equal("insufficient_evidence", response.Reason);
        Assert.Contains("OpenAIEscalation=blocked", response.Message, StringComparison.Ordinal);
        Assert.Equal(1, NativeInferenceCalls(operations));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task TeacherMode_NativeOnlyIsRejectedWithoutCallingNativeOrOpenAi()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var handler = new FounderAiScenarioHandler(
            ProviderText("This provider response must never be requested."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            ControllerTestHelpers.BuildUser(),
            Request("teacher", "Do not contact a provider.", nativeOnly: true));

        Assert.False(response.Succeeded);
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal("native_only_validation", response.Stage);
        Assert.Equal("native_only_requires_legend_mode", response.Reason);
        Assert.Equal(0, NativeInferenceCalls(operations));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ProviderResponseAfterLegendEscalation_IsLabeledOpenAiTeacherRatherThanLegendAi()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                "Explain the gap.",
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(),
                "en"))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                false,
                0m,
                null,
                "insufficient_evidence",
                0,
                "Escalation permitted.",
                true));
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot(
                "Explain the gap.",
                0,
                []));

        var handler = new FounderAiScenarioHandler(
            ProviderText("The OpenAI Teacher is handling this governed escalation."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
            Request("legend", "Explain the gap."));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("legend", response.Mode);
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal("provider_response", response.Stage);
        Assert.Equal(1, NativeInferenceCalls(operations));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ProviderCatalogAcceptanceRequest_SerializesCompleteValidatedCatalogAndDisablesToolExecution()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var handler = new FounderAiScenarioHandler(
            ProviderAccepted("PROVIDER_CATALOG_ACCEPTED"));
        var profiles = new AgentProfileAccessResolver(db);
        var legend = new FounderLegendConnectService(
            operations.Object,
            profiles);
        var service = new LegendFounderAiConversationService(
            new FounderAiHttpClientFactory(handler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenAI:ApiKey"] = "test-only-key",
                    ["OpenAI:LegendFounderAiModel"] = "gpt-5"
                })
                .Build(),
            legend,
            NullLogger<LegendFounderAiConversationService>.Instance,
            new LegendFounderAiDiscourseStateService(
                db,
                profiles,
                operations.Object),
            new LegendLanguageRegistry(
                db,
                new ConfigurationBuilder().Build()),
            ControllerTestHelpers.BuildTranslationService());

        var responseId =
            await service.VerifyProviderToolCatalogAcceptanceAsync();

        Assert.Equal("resp_provider_catalog_accepted", responseId);
        var requestBody = Assert.Single(handler.RequestBodies);
        using var requestDocument = JsonDocument.Parse(requestBody);
        var request = requestDocument.RootElement;
        Assert.False(request.GetProperty("store").GetBoolean());
        Assert.Equal("none", request.GetProperty("tool_choice").GetString());
        Assert.False(request.GetProperty("parallel_tool_calls").GetBoolean());

        var expectedTools = new LegendFounderToolAuthority(legend, null).Tools;
        using var expectedDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(expectedTools));
        Assert.Equal(
            expectedDocument.RootElement.GetRawText(),
            request.GetProperty("tools").GetRawText());
        Assert.Empty(operations.Invocations);
    }

    [Fact]
    public async Task ProviderAcceptanceCanary_LiveProviderAcceptsCompleteZeroWriteCatalog()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "LEGEND_FOUNDER_TOOL_CATALOG_PROVIDER_CANARY"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();
        Assert.False(string.IsNullOrWhiteSpace(configuration["OpenAI:ApiKey"]));

        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var profiles = new AgentProfileAccessResolver(db);
        var legend = new FounderLegendConnectService(
            operations.Object,
            profiles);
        using var factory = new LiveOpenAiHttpClientFactory();
        var service = new LegendFounderAiConversationService(
            factory,
            configuration,
            legend,
            NullLogger<LegendFounderAiConversationService>.Instance,
            new LegendFounderAiDiscourseStateService(
                db,
                profiles,
                operations.Object),
            new LegendLanguageRegistry(db, configuration),
            ControllerTestHelpers.BuildTranslationService());

        var responseId =
            await service.VerifyProviderToolCatalogAcceptanceAsync();

        Assert.False(string.IsNullOrWhiteSpace(responseId));
        Console.WriteLine($"ProviderAcceptanceResponseId={responseId}");
        Assert.Empty(operations.Invocations);
    }

    private static LegendFounderAiConversationService CreateService(
        Infrastructure.Data.MasterAppDbContext db,
        ILegendConnectOperations operations,
        FounderAiScenarioHandler handler,
        ITranslationService? translation = null) =>
        new(
            new FounderAiHttpClientFactory(handler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenAI:ApiKey"] = "test-only-key",
                    ["OpenAI:LegendFounderAiTimeoutSeconds"] = "45"
                })
                .Build(),
            new FounderLegendConnectService(
                operations,
                new AgentProfileAccessResolver(db)),
            NullLogger<LegendFounderAiConversationService>.Instance,
            new LegendFounderAiDiscourseStateService(
                db,
                new AgentProfileAccessResolver(db),
                operations),
            new LegendLanguageRegistry(
                db,
                new ConfigurationBuilder().Build()),
            translation ?? ControllerTestHelpers.BuildTranslationService());

    private static LegendConnectNativeInferenceSnapshot NativeLanguageAnswer(
        string languageCode) =>
        new(
            true,
            1m,
            $"Governed {languageCode} answer.",
            "semantic_transition_governed_composed",
            1,
            "Governed language-specific evidence was selected.",
            false,
            "HigherStandard",
            "OriginalComposition",
            ModelAssistance: new LegendConnectNativeModelAssistanceSnapshot(
                "Unavailable",
                "active_reasoning_model_unavailable",
                LegendConnectNativeModelAssistanceContracts.GovernedReasoningCapability,
                null,
                null,
                null));

    private static LegendConnectReadOnlyContentBindingRequest ReadOnlyContentRequest() =>
        new(
            "read-request-identity",
            "governed-transition-signature",
            "governed-result-frame-signature",
            "legend_translation_quality",
            "{}",
            "needsReviewCount",
            null,
            60,
            "$issuecount",
            "current_issue_count");

    private static LegendFounderAiChatRequest Request(
        string? mode,
        string prompt,
        bool nativeOnly = false,
        string? sourceLanguageCode = "en",
        bool founderCommandConfirmed = false) =>
        new()
        {
            Mode = mode,
            NativeOnly = nativeOnly,
            SourceLanguageCode = sourceLanguageCode,
            FounderCommandConfirmed = founderCommandConfirmed,
            Messages = [new LegendFounderAiChatMessage("user", prompt)]
        };

    private static string MachineProposalArguments() =>
        """
        {
          "source_language":"en",
          "target_language":"es",
          "capability_identity":"translation",
          "category_identity":"reusable_semantic",
          "observation_origin":"ConversationObservation",
          "research_observation_lineage":null,
          "family_key":"confirmed-machine-distinction",
          "semantic_category":"conversation_semantics",
          "rationale":"Retain one reusable distinction with machine provenance.",
          "confidence":0.7,
          "examples":[
            {
              "source_text":"How are you doing?",
              "target_text":"¿Cómo estás?",
              "components":[{"dimension":"conversation_function","value":"wellbeing_inquiry","surface_form":"How are you doing"}]
            },
            {
              "source_text":"I'm doing well.",
              "target_text":"Estoy bien.",
              "components":[{"dimension":"conversation_response","value":"wellbeing_positive","surface_form":"doing well"}]
            }
          ],
          "semantic_transitions":[
            {
              "source":{"dimensions":[{"dimension":"conversation_function","value":"wellbeing_inquiry"}]},
              "result":{"dimensions":[{"dimension":"conversation_response","value":"wellbeing_positive"}]}
            }
          ]
        }
        """;

    private static string SameLanguageMachineProposalArguments() =>
        MachineProposalArguments()
            .Replace(
                "\"target_language\":\"es\"",
                "\"target_language\":\"en\"",
                StringComparison.Ordinal)
            .Replace(
                "\"capability_identity\":\"translation\"",
                "\"capability_identity\":\"same_language_semantic\"",
                StringComparison.Ordinal)
            .Replace(
                "\"target_text\":\"¿Cómo estás?\"",
                "\"target_text\":null",
                StringComparison.Ordinal)
            .Replace(
                "\"target_text\":\"Estoy bien.\"",
                "\"target_text\":null",
                StringComparison.Ordinal);

    private static async Task<System.Security.Claims.ClaimsPrincipal> AddFounderProfileAsync(
        Infrastructure.Data.MasterAppDbContext db)
    {
        const string founderId = FounderEnvironmentScope.FounderId;
        db.AgentProfiles.Add(new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = founderId,
            AgentUpn = "mode-isolation-founder@legend.test",
            NormalizedEmail = "mode-isolation-founder@legend.test",
            IsActive = true
        });
        await db.SaveChangesAsync();
        return ControllerTestHelpers.BuildUser(founderId);
    }

    private static ControllerContext ControllerContextFor(
        System.Security.Claims.ClaimsPrincipal user) =>
        new()
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = user
            }
        };

    private static int NativeInferenceCalls(
        Mock<ILegendConnectOperations> operations) =>
        operations.Invocations.Count(invocation =>
            invocation.Method.Name is
                nameof(ILegendConnectOperations.TryInferConversationWithDiscourseAsync));

    private static int StatusFor(LegendFounderAiChatResponse response)
    {
        var mapper = typeof(LegendFounderAiController).GetMethod(
            "MapStatus",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(mapper);
        return Assert.IsType<int>(mapper!.Invoke(null, [response]));
    }

    private static string Describe(LegendFounderAiChatResponse response) =>
        $"mode={response.Mode}; authority={response.ResponseAuthority}; stage={response.Stage}; reason={response.Reason}; error={response.Error}";

    private static HttpResponseMessage ProviderText(string text) =>
        ProviderResponse(new
        {
            status = "completed",
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[]
                    {
                        new { type = "output_text", text }
                    }
                }
            }
        });

    private static HttpResponseMessage ProviderIncompleteText(string text) =>
        ProviderResponse(new
        {
            status = "incomplete",
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[]
                    {
                        new { type = "output_text", text }
                    }
                }
            }
        });

    private static HttpResponseMessage ProviderTool(
        string name,
        string arguments) =>
        ProviderResponse(new
        {
            status = "completed",
            output = new[]
            {
                new
                {
                    type = "function_call",
                    call_id = "tool-call-1",
                    name,
                    arguments
                }
            }
        });

    private static HttpResponseMessage ProviderAccepted(string text) =>
        ProviderResponse(new
        {
            id = "resp_provider_catalog_accepted",
            status = "completed",
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[]
                    {
                        new { type = "output_text", text }
                    }
                }
            }
        });

    private static HttpResponseMessage ProviderResponse(
        object payload,
        HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };

    private sealed class FounderAiHttpClientFactory(
        FounderAiScenarioHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("OpenAI", name);
            return new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://openai.test/")
            };
        }
    }

    private sealed class FounderAiScenarioHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        private readonly TimeSpan _responseDelay;

        public FounderAiScenarioHandler(params HttpResponseMessage[] responses)
            : this(TimeSpan.Zero, responses)
        {
        }

        public FounderAiScenarioHandler(
            TimeSpan responseDelay,
            params HttpResponseMessage[] responses)
        {
            _responseDelay = responseDelay;
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public int RequestCount { get; private set; }

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            RequestBodies.Add(
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken));
            if (_responses.Count == 0)
                throw new InvalidOperationException("No OpenAI response was queued for this test.");

            if (_responseDelay > TimeSpan.Zero)
                await Task.Delay(_responseDelay, cancellationToken);

            return _responses.Dequeue();
        }
    }

    private sealed class LiveOpenAiHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new()
        {
            BaseAddress = new Uri("https://api.openai.com/")
        };

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("OpenAI", name);
            return _client;
        }

        public void Dispose() => _client.Dispose();
    }

    private sealed class FounderEnvironmentScope : IDisposable
    {
        public const string FounderId = "11f6f9d9-0fe2-44c3-8cac-7d88d3fc3ac6";

        private readonly string? _previousFounderOid =
            Environment.GetEnvironmentVariable("FOUNDER_OID");

        public FounderEnvironmentScope() =>
            Environment.SetEnvironmentVariable("FOUNDER_OID", FounderId);

        public void Dispose() =>
            Environment.SetEnvironmentVariable("FOUNDER_OID", _previousFounderOid);
    }

    private sealed class FounderAiLanguageDetector(
        TranslationDetectionResult result) : ITranslationService
    {
        public int DetectionCount { get; private set; }

        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            DetectionCount++;
            return Task.FromResult(result);
        }

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Founder AI language identification must not translate text.");
    }
}
