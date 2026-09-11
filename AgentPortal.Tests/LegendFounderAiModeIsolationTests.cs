using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using AgentPortal.Services.Analytics;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
        var handler = new FounderAiScenarioHandler(
            ProviderText("The OpenAI Teacher is responding directly."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
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
        SetupUnclassifiedContentPlan(operations);
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
        SetupUnclassifiedContentPlan(operations);
        operations
            .Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                "Explain the governed gap.",
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(),
                "en",
                It.IsAny<LegendConnectExternalProviderPolicy?>()))
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
        SetupUnclassifiedContentPlan(operations);
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
                expectedLanguage,
                It.IsAny<LegendConnectExternalProviderPolicy?>()))
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
        Assert.Same(LegendConnectExternalProviderPolicy.NativeOnly, detector.ObservedPolicy);
        operations.Verify(operation => operation.TryInferConversationWithDiscourseAsync(
            prompt,
            It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
            It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
            It.IsAny<CancellationToken>(),
            expectedLanguage,
            It.IsAny<LegendConnectExternalProviderPolicy?>()), Times.Once);
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
                "fr",
                It.IsAny<LegendConnectExternalProviderPolicy?>()))
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
            "fr",
            It.IsAny<LegendConnectExternalProviderPolicy?>()), Times.Once);
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
                "en",
                It.Is<LegendConnectExternalProviderPolicy?>(policy =>
                    policy == LegendConnectExternalProviderPolicy.NativeOnly)))
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
                "en",
                It.Is<LegendConnectExternalProviderPolicy?>(policy =>
                    policy == LegendConnectExternalProviderPolicy.NativeOnly)))
            .Callback((string _input,
                IReadOnlyList<LegendConnectConversationContextItem> _context,
                LegendConnectDiscourseStateSnapshot? _discourseState,
                LegendConnectReadOnlyContentBindingReceipt receipt,
                CancellationToken _cancellationToken,
                string _language,
                LegendConnectExternalProviderPolicy? _providerPolicy) =>
                    observedReceipt = receipt)
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
    public async Task OperationalDiagnostics_ReturnsPartialCanonicalSnapshot_WhenOneStageTimesOut()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var runtime = new Mock<ILegendConnectRuntimePolicyAuthority>(MockBehavior.Strict);
        runtime
            .Setup(authority => authority.GetEffectiveAsync(
                It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken token) =>
            {
                Assert.True(token.CanBeCanceled);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return RuntimePolicy();
            });
        runtime
            .Setup(authority => authority.GetReadinessAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectProductionReadinessSnapshot(
                "READY",
                true,
                "Canonical readiness completed.",
                [],
                1,
                1,
                0,
                0,
                0));
        operations
            .Setup(operation => operation.GetProviderCapacityAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderCapacity());
        var authority = new LegendFounderToolAuthority(
            new FounderLegendConnectService(
                operations.Object,
                new AgentProfileAccessResolver(db),
                runtimePolicy: runtime.Object),
            null);

        var output = await authority.ExecuteAsync(
            founder,
            new FounderAiToolCall(
                "diagnostic-call",
                "legend_operational_diagnostics",
                "{}"),
            "teacher",
            CancellationToken.None);

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        Assert.Equal("READY", root.GetProperty("productionReadiness").GetProperty("state").GetString());
        Assert.Equal("Synchronized", root.GetProperty("providerCapacity").GetProperty("status").GetString());
        var stages = root.GetProperty("stages").EnumerateArray().ToArray();
        Assert.Equal(3, stages.Length);
        Assert.Equal("timed_out", stages[0].GetProperty("state").GetString());
        Assert.Equal("available", stages[1].GetProperty("state").GetString());
        Assert.Equal("available", stages[2].GetProperty("state").GetString());
        runtime.VerifyAll();
        operations.VerifyAll();
    }

    [Fact]
    public async Task OperationalDiagnostics_ReportsStageFailure_AndContinuesRemainingAuthorities()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var runtime = new Mock<ILegendConnectRuntimePolicyAuthority>(MockBehavior.Strict);
        runtime
            .Setup(authority => authority.GetEffectiveAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RuntimePolicy());
        runtime
            .Setup(authority => authority.GetReadinessAsync(
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test-only readiness failure"));
        operations
            .Setup(operation => operation.GetProviderCapacityAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderCapacity());
        var legend = new FounderLegendConnectService(
            operations.Object,
            new AgentProfileAccessResolver(db),
            runtimePolicy: runtime.Object);

        var snapshot = await legend.GetOperationalDiagnosticsAsync(founder);

        Assert.Equal("Active", snapshot.RuntimePolicy.ContextualCompositionMode);
        Assert.Equal("BLOCKED", snapshot.ProductionReadiness.State);
        Assert.Equal("Synchronized", snapshot.ProviderCapacity.Status);
        Assert.Collection(
            snapshot.Stages,
            stage => Assert.Equal("available", stage.State),
            stage => Assert.Equal("failed", stage.State),
            stage => Assert.Equal("available", stage.State));
        runtime.VerifyAll();
        operations.VerifyAll();
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
        Assert.True(authority.IsNativeContentBindingRead("legend_system_overview"));
        Assert.True(authority.IsNativeContentBindingRead("legend_operational_diagnostics"));
        Assert.True(authority.IsNativeContentBindingRead("legend_provider_capacity"));
        Assert.False(authority.IsNativeContentBindingRead("legend_submit_founder_curriculum"));
        Assert.False(authority.IsNativeContentBindingRead("legend_inspect_repository"));

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
        SetupUnclassifiedContentPlan(operations);
        operations
            .Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                "Translate this unsupported distinction.",
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(),
                "en",
                It.IsAny<LegendConnectExternalProviderPolicy?>()))
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
        SetupUnclassifiedContentPlan(operations);
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
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
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
            founder,
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
        operations.Verify(operation => operation.SubmitMachineTeachingProposalAsync(
            It.IsAny<LegendConnectMachineTeachingSubmission>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TeacherMode_GovernedToolReadFailureIsStructuredAndNeverBecomesHttp500()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
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
        SetupUnclassifiedContentPlan(operations);
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
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
        var handler = new FounderAiScenarioHandler(
            ProviderIncompleteText("Steps 1-4\nStep 5"),
            ProviderText("Step 5\nSteps 6-12"));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
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
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
        var rejected = ProviderResponse(new
        {
            error = new { message = "safe test rejection" }
        }, HttpStatusCode.BadRequest);
        rejected.Headers.TryAddWithoutValidation("x-request-id", "provider-test-reference");
        var handler = new FounderAiScenarioHandler(rejected);
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
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
        SetupUnclassifiedContentPlan(operations);
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
                "en",
                It.IsAny<LegendConnectExternalProviderPolicy?>()))
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
                "es",
                It.IsAny<LegendConnectExternalProviderPolicy?>()))
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
            Request("legend", "Hola.", nativeOnly: true, sourceLanguageCode: "es",
                conversationId: Guid.NewGuid().ToString("D")));

        Assert.False(response.Succeeded, Describe(response));
        Assert.Equal("native_inference", response.FailureKind);
        Assert.Equal(response.Message, response.Error);
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
                "en",
                It.IsAny<LegendConnectExternalProviderPolicy?>()))
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
                "en",
                It.IsAny<LegendConnectExternalProviderPolicy?>()))
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

        Assert.False(response.Succeeded, Describe(response));
        Assert.Equal("native_inference", response.FailureKind);
        Assert.Equal(response.Message, response.Error);
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
        SetupUnclassifiedContentPlan(operations);
        operations
            .Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                "Explain the gap.",
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(),
                "en",
                It.IsAny<LegendConnectExternalProviderPolicy?>()))
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
        var resourceMode = string.Equals(Environment.GetEnvironmentVariable("LEGEND_RESOURCE_DIAGNOSTICS_REQUIRED"),
            "true", StringComparison.OrdinalIgnoreCase);
        if (!resourceMode && !string.Equals(
                Environment.GetEnvironmentVariable(
                    "LEGEND_FOUNDER_TOOL_CATALOG_PROVIDER_CANARY"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var startedUtc = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var credentialConfigured = !string.IsNullOrWhiteSpace(resourceMode
            ? OpenAiKeyResolver.Resolve(configuration) : configuration["OpenAI:ApiKey"]);
        var candidateSha = Environment.GetEnvironmentVariable("LEGEND_VALIDATION_CANDIDATE_SHA");
        var runIdentity = Environment.GetEnvironmentVariable("LEGEND_VALIDATION_RUN_IDENTITY");
        var status = "NOT_CONFIGURED";
        var reason = "resource_configuration_missing";
        var stage = "configuration";
        var stageStarted = Stopwatch.GetTimestamp();
        var stages = new List<object>();
        string? failureType = null;
        var responsePresent = false;
        var writes = new LegendFounderAiComprehensiveDiagnosticContractTests.ResourceWriteGuard { Armed = resourceMode };
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        using var factory = new LiveOpenAiHttpClientFactory();
        try
        {
            Assert.True(credentialConfigured, "NOT_CONFIGURED: OpenAI API key absent.");
            if (resourceMode)
            {
                reason = "candidate_identity_missing";
                Assert.True(candidateSha is { Length: 40 } && candidateSha.All(Uri.IsHexDigit),
                    "NOT_CONFIGURED: exact candidate SHA absent.");
                reason = "run_identity_missing";
                Assert.True(IsBoundedResourceRunIdentity(runIdentity), "NOT_CONFIGURED: bounded run identity absent.");
                status = "FAILED";
                reason = "resource_probe_failed";
                stage = "candidate_assembly";
                stageStarted = Stopwatch.GetTimestamp();
                foreach (var assembly in new[] { typeof(LegendFounderAiModeIsolationTests).Assembly,
                             typeof(LegendConnectOperations).Assembly, typeof(FounderLegendConnectService).Assembly })
                    Assert.True(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?.EndsWith("+" + candidateSha, StringComparison.Ordinal) == true,
                        "Executed assembly is not bound to the requested candidate.");
                stages.Add(new { Stage = stage, Outcome = "VERIFIED", Reason = "exact_candidate_assembly_identity",
                    ElapsedMilliseconds = Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds });
            }
            stage = "provider_catalog_acceptance";
            stageStarted = Stopwatch.GetTimestamp();
            status = "FAILED";
            reason = "resource_probe_failed";
            await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .AddInterceptors(writes).Options);
            var profiles = new AgentProfileAccessResolver(db);
            var legend = new FounderLegendConnectService(operations.Object, profiles);
            var service = new LegendFounderAiConversationService(factory, configuration, legend,
                NullLogger<LegendFounderAiConversationService>.Instance,
                new LegendFounderAiDiscourseStateService(db, profiles, operations.Object),
                new LegendLanguageRegistry(db, configuration), ControllerTestHelpers.BuildTranslationService());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var responseId = await service.VerifyProviderToolCatalogAcceptanceAsync(deadline.Token);
            responsePresent = !string.IsNullOrWhiteSpace(responseId);
            Assert.True(responsePresent);
            Assert.Empty(operations.Invocations);
            Assert.Equal(0, writes.SaveChangesAttempts);
            Assert.NotEmpty(factory.HttpCalls);
            if (!resourceMode)
                Console.WriteLine($"ProviderAcceptanceResponseId={responseId}");
            status = "OBSERVED";
            reason = "resource_boundary_observed_not_production_data_proof";
            stages.Add(new { Stage = stage, Outcome = status, Reason = reason,
                ElapsedMilliseconds = Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds });
        }
        catch (Exception exception)
        {
            failureType = exception.GetType().Name;
            if (status != "NOT_CONFIGURED")
                reason = exception switch
                {
                    OperationCanceledException => "resource_cancelled_or_deadline_exceeded",
                    HttpRequestException => "resource_transport_failed",
                    Xunit.Sdk.XunitException => "resource_contract_assertion_failed",
                    _ => "resource_execution_failed"
                };
            stages.Add(new { Stage = stage, Outcome = status, Reason = reason, FailureType = failureType,
                ElapsedMilliseconds = Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds });
            if (!resourceMode) throw;
        }
        finally
        {
            if (resourceMode)
            {
                var report = JsonSerializer.Serialize(new
                {
                    CandidateSha = candidateSha is { Length: 40 } && candidateSha.All(Uri.IsHexDigit) ? candidateSha : null,
                    RunIdentity = IsBoundedResourceRunIdentity(runIdentity) ? runIdentity : null,
                    Authority = "NonAuthoritativeResourceBoundaryDiagnostic", Environment = "LocalInMemoryObservabilityWithLiveProvider",
                    Resource = "openai", Status = status, Reason = reason, CredentialConfigured = credentialConfigured,
                    EndpointConfigured = true, Stage = stage, Stages = stages, FailureType = failureType,
                    HttpCallCount = factory.HttpCalls.Count, HttpCalls = factory.HttpCalls.Take(64).ToArray(),
                    HttpCallsDropped = Math.Max(0, factory.HttpCalls.Count - 64),
                    ProviderClientCount = factory.CreateClientCount, CanonicalWriteAttempts = writes.BlockedWrites,
                    SaveChangesAttempts = writes.SaveChangesAttempts, OperationsInvocationCount = operations.Invocations.Count,
                    LocalObservabilityWrites = 0,
                    Outcome = new { Provider = "OpenAI", Policy = "ExplicitZeroWriteCatalogCanary",
                        ResponsePresent = responsePresent, ToolExecution = "Disabled", ProviderStore = false,
                        Provenance = "ProviderDerived", Serving = "NonServing", Canonical = "NonCanonical" },
                    StartedUtc = startedUtc, CompletedUtc = DateTime.UtcNow, ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds
                }, new JsonSerializerOptions { WriteIndented = true });
                var directory = new DirectoryInfo(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory());
                while (!File.Exists(Path.Combine(directory.FullName, "MASTERAPP.sln")))
                    directory = directory.Parent ?? throw new DirectoryNotFoundException("Repository root not found for resource receipt.");
                var destination = Path.Combine(directory.FullName, "diagnostics", "legend-shadow");
                Directory.CreateDirectory(destination);
                await File.WriteAllTextAsync(Path.Combine(destination, "resource-openai.json"), report);
            }
        }
        Assert.True(status == "OBSERVED", status + ": " + reason + " at " + stage);
    }

    private static bool IsBoundedResourceRunIdentity(string? value) =>
        value is { Length: > 0 and <= 100 } && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    [Theory]
    [InlineData("teacher")]
    [InlineData("legend")]
    public async Task OwnedRecordIntent_WithoutGovernedReadScope_RejectsProviderRecollection(string mode)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var graph = new LegendConnectUtteranceMeaningGraphSnapshot(
            true, [],
            [new LegendConnectUtteranceMeaningRelation(
                "owned-read", LegendConnectOwnedRecordRequest.RequiredRelationKind, 0, 1, 3)],
            [], "composed");
        operations.Setup(operation => operation.TryBindConversationContentAsync(
                It.IsAny<string>(), It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(), "en"))
            .ReturnsAsync(new LegendConnectContentBoundResponseMeaningPlanResult(
                false, "read_scope_unproven", null,
                OwnedRecordIntent: LegendConnectOwnedRecordRequest.Classify(graph)));
        if (mode == "legend")
        {
            operations.Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                    It.IsAny<string>(), It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                    It.IsAny<LegendConnectDiscourseStateSnapshot?>(), It.IsAny<CancellationToken>(), "en",
                    It.IsAny<LegendConnectExternalProviderPolicy?>()))
                .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                    false, 0m, null, "read_scope_unproven", 1,
                    "Owned-record meaning is proven but its result frame is missing.", true,
                    OwnedRecordIntent: LegendConnectOwnedRecordRequest.Classify(graph)));
        }
        var handler = new FounderAiScenarioHandler(ProviderText("An uninspected record count."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(founder, Request(mode, "Show the requested state."));

        Assert.False(response.Succeeded);
        Assert.Equal("owned_record_read_scope_unproven", response.Reason);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("en")]
    [InlineData(null)]
    public async Task TeacherMode_UnclassifiedWordingKeepsOptionalToolsAvailableWithoutForcingARead(
        string? sourceLanguageCode)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
        var detector = new FounderAiLanguageDetector(
            new TranslationDetectionResult(true, "en", Confidence: 0.9m));
        var handler = new FounderAiScenarioHandler(ProviderText("A conceptual response."));
        var service = CreateService(db, operations.Object, handler, detector);

        var response = await service.ReplyAsync(founder,
            Request("teacher", "Explain branches, canonical knowledge and production workflows.",
                sourceLanguageCode: sourceLanguageCode));

        Assert.True(response.Succeeded, Describe(response));
        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal("auto", body.RootElement.GetProperty("tool_choice").GetString());
        Assert.NotEmpty(body.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal(0, NativeInferenceCalls(operations));
        Assert.Equal(sourceLanguageCode is null ? 1 : 0, detector.DetectionCount);
        if (sourceLanguageCode is null)
            Assert.Same(LegendConnectExternalProviderPolicy.ProviderEnabled, detector.ObservedPolicy);
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal(LegendConnectResearchEvidenceOrigin.UnresolvedEvidence, response.EvidenceOrigin);
    }

    [Fact]
    public async Task TeacherMode_UsesDeclaredLanguageForTypedClassificationWithoutExternalDetection()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations.Setup(operation => operation.TryBindConversationContentAsync(
                "Expliquez cette idée.", It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(), "fr"))
            .ReturnsAsync(new LegendConnectContentBoundResponseMeaningPlanResult(
                false, "meaning_graph_component_unknown", null,
                OwnedRecordIntent: new LegendConnectOwnedRecordClassification(
                    LegendConnectOwnedRecordIntent.Unknown, false,
                    LegendConnectOwnedRecordRequest.RequiredRelationKind)));
        var detector = new FounderAiLanguageDetector(
            new TranslationDetectionResult(false, null, "must_not_detect"));
        var handler = new FounderAiScenarioHandler(ProviderText("Une réponse attribuée au fournisseur."));
        var service = CreateService(db, operations.Object, handler, detector);

        var response = await service.ReplyAsync(founder,
            Request("teacher", "Expliquez cette idée.", sourceLanguageCode: "fr-FR"));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal(0, detector.DetectionCount);
        Assert.Equal(0, NativeInferenceCalls(operations));
        operations.VerifyAll();
    }

    [Fact]
    public async Task TeacherMode_UnavailableMeaningAnalysisCannotBeTreatedAsNoOwnedRecordIntent()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations.Setup(operation => operation.TryBindConversationContentAsync(
                It.IsAny<string>(), It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(), "en"))
            .ThrowsAsync(new InvalidOperationException("controlled analysis outage"));
        var handler = new FounderAiScenarioHandler(ProviderText("No proof for this response."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(founder, Request("teacher", "Show the relevant information."));

        Assert.False(response.Succeeded);
        Assert.Equal("governed_request_classification", response.Stage);
        Assert.Contains("governed_meaning_graph_analysis_unavailable", response.Reason);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("legend", false)]
    [InlineData("legend", true)]
    [InlineData("teacher", false)]
    public async Task TransientLanguageOutage_UsesAttributedProviderOnlyWhenPolicyAllows(string mode, bool nativeOnly)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var detector = new FounderAiLanguageDetector(
            new TranslationDetectionResult(false, null, "translation_provider_failed"));
        var handler = new FounderAiScenarioHandler(ProviderText("No governed language was resolved."));
        var service = CreateService(db, operations.Object, handler, detector);

        var response = await service.ReplyAsync(founder,
            Request(mode, "Unidentified input", nativeOnly: nativeOnly, sourceLanguageCode: null));

        Assert.Equal(!nativeOnly, response.Succeeded);
        Assert.Equal("source_language_identification_unavailable", response.Reason);
        Assert.Equal(nativeOnly, Assert.IsType<LegendConnectExternalProviderPolicy>(detector.ObservedPolicy).ForbidsExternalProviders);
        Assert.Empty(operations.Invocations);
        Assert.Equal(nativeOnly ? 0 : 1, handler.RequestCount);
        if (nativeOnly)
            Assert.Equal(503, StatusFor(response));
        else
        {
            Assert.Equal("OpenAITeacher", response.ResponseAuthority);
            Assert.Equal(LegendConnectResearchEvidenceOrigin.UnresolvedEvidence, response.EvidenceOrigin);
        }
    }

    [Fact]
    public async Task TeacherMode_ClassificationCancellationPropagatesWithoutProviderFallback()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        operations.Setup(operation => operation.TryBindConversationContentAsync(
                It.IsAny<string>(), It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(), "en"))
            .Callback(() => cancellation.Cancel())
            .ThrowsAsync(new OperationCanceledException());
        var handler = new FounderAiScenarioHandler();
        var service = CreateService(db, operations.Object, handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReplyAsync(
            founder, Request("teacher", "Classify this request."), cancellation.Token));

        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("{ }", true)]
    [InlineData("{\"unexpected_scope\":true}", false)]
    public async Task TeacherMode_UsesExactGovernedReadScopeWithoutNativeAnswerGeneration(
        string providerArguments, bool expectedSuccess)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var readScope = ReadOnlyContentRequest();
        operations.Setup(operation => operation.TryBindConversationContentAsync(
                It.IsAny<string>(), It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(), "en"))
            .ReturnsAsync(new LegendConnectContentBoundResponseMeaningPlanResult(
                false, "read_only_content_binding_required", null, readScope,
                new LegendConnectOwnedRecordClassification(
                    LegendConnectOwnedRecordIntent.OwnedRecordStateInspection, true, null)));
        operations.Setup(operation => operation.GetTranslationQualityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectTranslationQualitySnapshot(7, 1, 1, 1, 10, []));
        var handler = new FounderAiScenarioHandler(
            ProviderTool(readScope.ToolName, providerArguments),
            ProviderText("The inspected count is 7."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(founder,
            Request("teacher", "Read the requested current value."));

        Assert.Equal(expectedSuccess, response.Succeeded);
        Assert.Equal(0, NativeInferenceCalls(operations));
        Assert.Equal(2, handler.RequestCount);
        using var initial = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.Equal("required", initial.RootElement.GetProperty("tool_choice").GetString());
        Assert.Contains("GOVERNED_READ_REQUIREMENT", handler.RequestBodies[0]);
        if (expectedSuccess)
        {
            Assert.Equal("OpenAITeacher", response.ResponseAuthority);
            Assert.Equal("The inspected count is 7.", response.Message);
        }
        else
            Assert.Equal("required_governed_inspection_missing", response.Reason);
    }

    [Fact]
    public async Task TeacherMode_UnrelatedSuccessfulReadCannotSatisfyGovernedResultFrame()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations.Setup(operation => operation.TryBindConversationContentAsync(
                It.IsAny<string>(), It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(), "en"))
            .ReturnsAsync(new LegendConnectContentBoundResponseMeaningPlanResult(
                false, "read_only_content_binding_required", null, ReadOnlyContentRequest(),
                new LegendConnectOwnedRecordClassification(
                    LegendConnectOwnedRecordIntent.OwnedRecordStateInspection, true, null)));
        operations.Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot("unrelated", 1, []));
        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"unrelated\"}"),
            ProviderText("An unsupported current value from an unrelated read."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(founder,
            Request("teacher", "Read the requested current value."));

        Assert.False(response.Succeeded);
        Assert.Equal("required_governed_inspection_missing", response.Reason);
        Assert.Equal(2, handler.RequestCount);
        operations.Verify(operation => operation.GetTranslationQualityAsync(
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("{\"query\":\"missing\"}", false)]
    [InlineData("{ \"query\": \"missing\" }", false)]
    [InlineData("{\"query\":\"available\"}", true)]
    public async Task OptionalReadRetries_PreserveExactFailedScopeAndDisclosePartialEvidence(
        string retryArguments, bool expectsPartial)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
        operations.SetupSequence(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("controlled missing read"))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot("retry", 1, []));
        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"missing\"}"),
            ProviderTool("legend_search_retained_knowledge", retryArguments),
            ProviderText("The completed read supports only its own scope."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(founder,
            Request("teacher", "Inspect the available evidence."));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(expectsPartial,
            response.Message!.Contains("LEGEND_GOVERNED_READ_DIAGNOSTICS", StringComparison.Ordinal));
        Assert.Equal(expectsPartial ? "partial_governed_inspection" : null, response.Reason);
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal(LegendConnectResearchEvidenceOrigin.UnresolvedEvidence, response.EvidenceOrigin);
    }

    [Theory]
    [InlineData("legend", "translation_language_ambiguous", "source_language_ambiguous")]
    [InlineData("legend", "translation_language_unsupported", "source_language_unsupported")]
    [InlineData("teacher", "translation_language_ambiguous", "source_language_ambiguous")]
    [InlineData("teacher", "translation_language_unsupported", "source_language_unsupported")]
    public async Task SemanticLanguageFailure_DoesNotUseProviderEnabledEscalation(
        string mode, string detectorError, string expectedReason)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var handler = new FounderAiScenarioHandler(ProviderText("No semantic authority."));
        var detector = new FounderAiLanguageDetector(new TranslationDetectionResult(false, null, detectorError));
        var service = CreateService(db, operations.Object, handler, detector);

        var response = await service.ReplyAsync(founder,
            Request(mode, "Unresolved source", sourceLanguageCode: null));

        Assert.False(response.Succeeded);
        Assert.Equal(expectedReason, response.Reason);
        Assert.Equal(0, handler.RequestCount);
        Assert.Empty(operations.Invocations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeCapacityRead_PropagatesTheRequestPolicyThroughTheFounderToolBoundary(bool nativeOnly)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var policy = nativeOnly
            ? LegendConnectExternalProviderPolicy.NativeOnly
            : LegendConnectExternalProviderPolicy.ProviderEnabled;
        var request = ReadOnlyContentRequest() with
        {
            ToolName = "legend_provider_capacity",
            ValuePath = "monthlyCharactersConsumed"
        };
        operations.Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(), It.IsAny<CancellationToken>(), "en", policy))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                false, 0m, null, "read_only_content_binding_required", 3,
                "One scoped read is required.", false, ReadOnlyContentRequest: request));
        operations.Setup(operation => operation.GetProviderCapacityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderCapacity());
        operations.Setup(operation => operation.GetProviderCapacityAsync(
                It.IsAny<CancellationToken>(), policy))
            .ReturnsAsync(ProviderCapacity());
        operations.Setup(operation => operation.TryInferConversationWithReadOnlyContentAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.Is<LegendConnectReadOnlyContentBindingReceipt>(receipt => receipt.SemanticValue == "100"),
                It.IsAny<CancellationToken>(), "en", policy))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                true, 1m, "The governed consumption is 100.", "governed", 4, "Scoped read receipt.", false));
        var handler = new FounderAiScenarioHandler();
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(founder,
            Request("legend", "Read the governed capacity value.", nativeOnly: nativeOnly));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("LegendAi", response.ResponseAuthority);
        Assert.Equal(0, handler.RequestCount);
        operations.Verify(operation => operation.GetProviderCapacityAsync(
            It.IsAny<CancellationToken>(), policy), nativeOnly ? Times.Once() : Times.Never());
        operations.Verify(operation => operation.GetProviderCapacityAsync(
            It.IsAny<CancellationToken>()), nativeOnly ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task ProviderToolChurn_StopsAtFinalSynthesisWithoutExecutingAnotherTool()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
        operations.Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot("inspection", 1, []));
        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"inspection\"}"),
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"inspection\"}"),
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"inspection\"}"));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(founder,
            Request("teacher", "Inspect the evidence and give the result."));

        Assert.False(response.Succeeded);
        Assert.Equal("provider_tool_execution_not_allowed", response.Reason);
        Assert.Equal(3, handler.RequestCount);
        operations.Verify(operation => operation.SearchRetainedKnowledgeAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        using var finalRound = JsonDocument.Parse(handler.RequestBodies[^1]);
        Assert.Equal("none", finalRound.RootElement.GetProperty("tool_choice").GetString());
        Assert.Empty(finalRound.RootElement.GetProperty("tools").EnumerateArray());
    }

    private static void SetupUnclassifiedContentPlan(
        Mock<ILegendConnectOperations> operations) =>
        operations.Setup(operation => operation.TryBindConversationContentAsync(
                It.IsAny<string>(), It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync(new LegendConnectContentBoundResponseMeaningPlanResult(
                false, "meaning_graph_component_unknown", null,
                OwnedRecordIntent: new LegendConnectOwnedRecordClassification(
                    LegendConnectOwnedRecordIntent.Unknown, false,
                    LegendConnectOwnedRecordRequest.RequiredRelationKind)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-conversation")]
    public async Task NativeWithoutConversationIdentity_SkipsUnusedDiscourseAnalysis(string? conversationId)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations.Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                null, It.IsAny<CancellationToken>(), "en", LegendConnectExternalProviderPolicy.NativeOnly))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                false, 0m, null, "meaning_graph_component_unknown", 0, "No admitted meaning.", true));
        var handler = new FounderAiScenarioHandler();

        var response = await CreateService(db, operations.Object, handler).ReplyAsync(founder,
            Request("legend", "An unseen request.", nativeOnly: true, conversationId: conversationId));

        Assert.Equal("native_only_blocked", response.Stage);
        Assert.Equal(0, handler.RequestCount);
        operations.Verify(operation => operation.AnalyzeReusableMeaningGraphAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()), Times.Never());
        Assert.Empty(db.LegendFounderAiDiscourseTurns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCurrentObservation_DoesNotTreatThePreviousTurnAsCurrent(bool cancelled)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var state = new LegendFounderAiDiscourseStateService(db, new AgentProfileAccessResolver(db), operations.Object);
        var conversationId = Guid.NewGuid().ToString("D");
        await state.RecordObservationAsync(founder, conversationId, "user",
            new LegendConnectUtteranceMeaningGraphSnapshot(false, [], [], [], "prior_observation"));
        operations.Setup(operation => operation.AnalyzeReusableMeaningGraphAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), "en"))
            .ThrowsAsync(cancelled ? new OperationCanceledException() : new InvalidOperationException("observation unavailable"));
        operations.Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                null, It.IsAny<CancellationToken>(), "en", LegendConnectExternalProviderPolicy.NativeOnly))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                false, 0m, null, "meaning_graph_component_unknown", 0, "No admitted current meaning.", true));
        var handler = new FounderAiScenarioHandler();

        var response = await CreateService(db, operations.Object, handler).ReplyAsync(founder,
            Request("legend", "Current request.", nativeOnly: true, conversationId: conversationId));

        Assert.Equal("native_only_blocked", response.Stage);
        Assert.Equal(1, NativeInferenceCalls(operations));
        Assert.Equal(0, handler.RequestCount);
        Assert.Single(db.LegendFounderAiDiscourseTurns);
        operations.VerifyAll();
    }

    [Fact]
    public async Task DiscourseReload_EndsAtThisRequestsCommittedTurnAndRejectsMissingSequence()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var state = new LegendFounderAiDiscourseStateService(db, new AgentProfileAccessResolver(db), operations.Object);
        var conversationId = Guid.NewGuid().ToString("D");
        var first = await state.RecordCurrentObservationAsync(founder, conversationId, "user",
            new LegendConnectUtteranceMeaningGraphSnapshot(false, [], [], [], "first_observation"));
        var later = await state.RecordCurrentObservationAsync(founder, conversationId, "user",
            new LegendConnectUtteranceMeaningGraphSnapshot(false, [], [], [], "later_observation"));

        var captured = await state.GetStateAsync(founder, conversationId, currentTurnSequence: first);
        var latest = await state.GetStateAsync(founder, conversationId, currentTurnSequence: later);
        var missing = await state.GetStateAsync(founder, conversationId, currentTurnSequence: 0);

        Assert.Equal(first, Assert.Single(Assert.IsType<LegendConnectDiscourseStateSnapshot>(captured).Turns).SequenceNumber);
        Assert.Equal(2, Assert.IsType<LegendConnectDiscourseStateSnapshot>(latest).Turns.Count);
        Assert.Null(missing);
    }

    [Theory]
    [InlineData("legend")]
    [InlineData("teacher")]
    public async Task CurrentObservation_IsScopedAndCarriedToTheExistingAuthorityInBothModes(string mode)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var conversationId = Guid.NewGuid().ToString("D");
        var graph = new LegendConnectUtteranceMeaningGraphSnapshot(false, [], [], ["unseen"], "meaning_graph_component_unknown");
        operations.Setup(operation => operation.AnalyzeReusableMeaningGraphAsync(
                "Unseen current request.", It.IsAny<CancellationToken>(), "en"))
            .ReturnsAsync(graph);
        LegendConnectDiscourseStateSnapshot? observed = null;
        if (mode == "legend")
        {
            operations.Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                    It.IsAny<string>(), It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                    It.IsAny<LegendConnectDiscourseStateSnapshot?>(), It.IsAny<CancellationToken>(), "en",
                    LegendConnectExternalProviderPolicy.NativeOnly))
                .Callback((string _, IReadOnlyList<LegendConnectConversationContextItem> _,
                    LegendConnectDiscourseStateSnapshot? snapshot, CancellationToken _, string _, LegendConnectExternalProviderPolicy? _) => observed = snapshot)
                .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                    false, 0m, null, "meaning_graph_component_unknown", 0, "No admitted meaning.", true));
        }
        else
        {
            operations.Setup(operation => operation.TryBindConversationContentAsync(
                    It.IsAny<string>(), It.IsAny<LegendConnectDiscourseStateSnapshot?>(), It.IsAny<CancellationToken>(), "en"))
                .Callback((string _, LegendConnectDiscourseStateSnapshot? snapshot, CancellationToken _, string _) => observed = snapshot)
                .ReturnsAsync(new LegendConnectContentBoundResponseMeaningPlanResult(false, "meaning_graph_component_unknown", null,
                    OwnedRecordIntent: new LegendConnectOwnedRecordClassification(LegendConnectOwnedRecordIntent.Unknown, false, null)));
        }
        var handler = new FounderAiScenarioHandler(ProviderText("Attributed external response."));

        var response = await CreateService(db, operations.Object, handler).ReplyAsync(founder,
            Request(mode, "Unseen current request.", nativeOnly: mode == "legend", conversationId: conversationId));

        Assert.Equal(mode == "teacher", response.Succeeded);
        if (mode == "legend")
        {
            Assert.Equal("native_only_blocked", response.Stage);
            Assert.Equal("meaning_graph_component_unknown", response.Reason);
            Assert.Equal("native_inference", response.FailureKind);
            Assert.Equal(response.Message, response.Error);
        }
        var captured = Assert.IsType<LegendConnectDiscourseStateSnapshot>(observed);
        var analysis = Assert.IsType<LegendConnectCurrentTurnMeaningAnalysis>(captured.CurrentTurnAnalysis);
        Assert.Same(graph, analysis.Graph);
        Assert.Equal("en", analysis.SourceLanguageCode);
        Assert.Equal(Assert.Single(captured.Turns).SequenceNumber, analysis.TurnSequence);
        Assert.Equal(LegendLanguageIdentity.TextHash(LegendLanguageIdentity.NormalizeText("Unseen current request.")), analysis.NormalizedInputHash);
        Assert.DoesNotContain("CurrentTurnAnalysis", JsonSerializer.Serialize(captured), StringComparison.OrdinalIgnoreCase);
        Assert.Null(Assert.IsType<LegendConnectDiscourseStateSnapshot>(JsonSerializer.Deserialize<LegendConnectDiscourseStateSnapshot>(
            "{\"Turns\":[],\"CurrentTurnAnalysis\":{\"NormalizedInputHash\":\"forged\",\"SourceLanguageCode\":\"en\",\"TurnSequence\":1}}")).CurrentTurnAnalysis);
        Assert.Equal(mode == "legend" ? 1 : 0, NativeInferenceCalls(operations));
        Assert.Equal(mode == "legend" ? 0 : 1, handler.RequestCount);
        operations.Verify(operation => operation.AnalyzeReusableMeaningGraphAsync(
            "Unseen current request.", It.IsAny<CancellationToken>(), "en"), Times.Once());
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

    private static LegendConnectRuntimePolicySnapshot RuntimePolicy() =>
        new(
            true,
            2_000_000,
            200_000,
            1_000_000,
            true,
            true,
            "Active",
            0.98m,
            DateTime.UtcNow,
            DateTime.UtcNow,
            DateTime.UtcNow);

    private static LegendConnectProviderCapacitySnapshot ProviderCapacity()
    {
        var now = DateTime.UtcNow;
        var periodStart = new DateOnly(now.Year, now.Month, 1);
        return new LegendConnectProviderCapacitySnapshot(
            "AzureTranslator",
            true,
            "Synchronized",
            "translator-test",
            "resource-test",
            "S1",
            periodStart,
            periodStart.AddMonths(1).AddDays(-1),
            2_000_000,
            100,
            0,
            1_999_900,
            200_000,
            1_000_000,
            60,
            now.AddHours(-1),
            now,
            2_000_000,
            100,
            0,
            1_999_900,
            200_000,
            1_000_000,
            now,
            "Canonical provider capacity completed.");
    }

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
        bool founderCommandConfirmed = false,
        string? conversationId = null) =>
        new()
        {
            Mode = mode,
            NativeOnly = nativeOnly,
            SourceLanguageCode = sourceLanguageCode,
            FounderCommandConfirmed = founderCommandConfirmed,
            ConversationId = conversationId,
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
        // Production seeds the governed language registry through migrations;
        // the Founder read path resolves language identity read-only.
        ControllerTestHelpers.SeedGovernedLanguageBaseline(
            db, "en", "fr", "es", "ht");
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
        public List<object> HttpCalls { get; } = [];
        public int CreateClientCount { get; private set; }
        private readonly HttpClient _client;

        public LiveOpenAiHttpClientFactory()
        {
            _client = new HttpClient(new LiveOpenAiObservationHandler(HttpCalls))
            {
                BaseAddress = new Uri("https://api.openai.com/")
            };
        }

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("OpenAI", name);
            CreateClientCount++;
            return _client;
        }

        public void Dispose() => _client.Dispose();
    }

    private sealed class LiveOpenAiObservationHandler(List<object> calls) : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            int? status = null;
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                status = (int)response.StatusCode;
                return response;
            }
            finally
            {
                calls.Add(new { Client = "OpenAI", StatusCode = status,
                    ElapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            }
        }
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
        public LegendConnectExternalProviderPolicy? ObservedPolicy { get; private set; }

        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken = default) =>
            DetectLanguageAsync(text, cancellationToken, null);

        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken,
            LegendConnectExternalProviderPolicy? providerPolicy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DetectionCount++;
            ObservedPolicy = providerPolicy;
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
