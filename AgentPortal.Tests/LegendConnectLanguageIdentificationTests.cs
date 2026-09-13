using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectLanguageIdentificationTests
{
    public static IEnumerable<object[]> IncompleteLanguageAnalyses()
    {
        string[] reasons =
        [
            "meaning_graph_authority_unavailable",
            "meaning_graph_processing_bound_exceeded",
            "meaning_graph_retrieval_bound_exceeded",
            "meaning_graph_projection_bound_exceeded",
            "meaning_graph_relation_bound_exceeded",
            "meaning_graph_source_slot_input_bound_exceeded",
            "meaning_graph_source_slot_retrieval_bound_exceeded",
            "meaning_graph_source_slot_context_bound_exceeded",
            "meaning_graph_source_slot_context_unproven",
            "meaning_graph_source_slot_roles_ambiguous",
            "meaning_graph_source_slot_value_ambiguous",
            "meaning_graph_source_slot_contradicted",
            "future_unclassified_analysis_failure"
        ];
        foreach (var reason in reasons)
        foreach (var nativeOnly in new[] { false, true })
        foreach (var reverse in new[] { false, true })
            yield return [reason, nativeOnly, reverse];
    }

    [Theory]
    [MemberData(nameof(IncompleteLanguageAnalyses))]
    public async Task IncompleteOtherLanguage_CannotEstablishUniqueGovernedMatch(
        string reason, bool nativeOnly, bool reverse)
    {
        var languages = Registry(reverse);
        var structural = StructuralGate();
        structural.Setup(value => value.AnalyzeReusableMeaningGraphAsync(
                "en", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComposedGraph());
        structural.Setup(value => value.AnalyzeReusableMeaningGraphAsync(
                "es", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectUtteranceMeaningGraphSnapshot(false, [], [], [], reason));
        var policy = nativeOnly
            ? LegendConnectExternalProviderPolicy.NativeOnly
            : LegendConnectExternalProviderPolicy.ProviderEnabled;
        var provider = new Mock<ITranslationProvider>(MockBehavior.Strict);
        if (!nativeOnly)
        {
            provider.Setup(value => value.DetectLanguageAsync(
                    It.IsAny<string>(), It.IsAny<CancellationToken>(), policy))
                .ReturnsAsync(new TranslationDetectionResult(true, "es", Confidence: 0.9m));
        }
        var router = new LegendConnectTranslationRouter(
            provider.Object, languages.Object,
            Mock.Of<ITranslationCapacityAuthority>(MockBehavior.Strict),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            structuralComposition: structural.Object);

        var result = await router.DetectLanguageAsync("Unclassified surface", CancellationToken.None, policy);

        Assert.Equal(!nativeOnly, result.Succeeded);
        Assert.Equal(nativeOnly ? null : "es", result.Language);
        Assert.Equal(nativeOnly ? "native_only_governed_source_language_undetermined" : null, result.ErrorCode);
        Assert.Equal(nativeOnly ? 0 : 1, provider.Invocations.Count);
        Assert.Equal(2, GraphCalls(structural));
    }

    [Theory]
    [InlineData("meaning_graph_component_unknown", false)]
    [InlineData("meaning_graph_component_unknown", true)]
    [InlineData("meaning_graph_relation_unproven", false)]
    [InlineData("meaning_graph_relation_unproven", true)]
    public async Task CompletedOtherLanguageAnalysis_PreservesUniqueGovernedMatch(
        string reason, bool reverse)
    {
        var structural = StructuralGate();
        structural.Setup(value => value.AnalyzeReusableMeaningGraphAsync(
                "en", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComposedGraph());
        structural.Setup(value => value.AnalyzeReusableMeaningGraphAsync(
                "es", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectUtteranceMeaningGraphSnapshot(false, [], [], ["surface"], reason));
        var provider = new Mock<ITranslationProvider>(MockBehavior.Strict);
        var router = new LegendConnectTranslationRouter(
            provider.Object, Registry(reverse).Object,
            Mock.Of<ITranslationCapacityAuthority>(MockBehavior.Strict),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            structuralComposition: structural.Object);

        var result = await router.DetectLanguageAsync(
            "Governed surface", CancellationToken.None, LegendConnectExternalProviderPolicy.NativeOnly);

        Assert.True(result.Succeeded);
        Assert.Equal("en", result.Language);
        Assert.Equal(1m, result.Confidence);
        Assert.Empty(provider.Invocations);
        Assert.Equal(2, GraphCalls(structural));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOtherLanguageAnalysis_DoesNotReturnPartialMatchOrContactProvider(bool nativeOnly)
    {
        using var cancellation = new CancellationTokenSource();
        var structural = StructuralGate();
        structural.Setup(value => value.AnalyzeReusableMeaningGraphAsync(
                "en", It.IsAny<string>(), cancellation.Token))
            .ReturnsAsync(ComposedGraph());
        structural.Setup(value => value.AnalyzeReusableMeaningGraphAsync(
                "es", It.IsAny<string>(), cancellation.Token))
            .Returns(() =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<LegendConnectUtteranceMeaningGraphSnapshot>(cancellation.Token);
            });
        var provider = new Mock<ITranslationProvider>(MockBehavior.Strict);
        var router = new LegendConnectTranslationRouter(
            provider.Object, Registry(reverse: false).Object,
            Mock.Of<ITranslationCapacityAuthority>(MockBehavior.Strict),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            structuralComposition: structural.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.DetectLanguageAsync(
            "Governed surface", cancellation.Token, nativeOnly
                ? LegendConnectExternalProviderPolicy.NativeOnly
                : LegendConnectExternalProviderPolicy.ProviderEnabled));

        Assert.Empty(provider.Invocations);
        Assert.Equal(2, GraphCalls(structural));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompleteCandidateExclusion_StillRequiresPositiveGraphProof(bool composed)
    {
        var structural = StructuralGate();
        structural.Setup(value => value.GetReusableMeaningLanguageCandidatesAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectMeaningLanguageCandidates(true, ["en"], "completed"));
        structural.Setup(value => value.AnalyzeReusableMeaningGraphAsync(
                "en", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(composed ? ComposedGraph() :
                new LegendConnectUtteranceMeaningGraphSnapshot(false, [], [], ["surface"], "meaning_graph_component_unknown"));
        var provider = new Mock<ITranslationProvider>(MockBehavior.Strict);
        var router = new LegendConnectTranslationRouter(
            provider.Object, Registry(reverse: false).Object,
            Mock.Of<ITranslationCapacityAuthority>(MockBehavior.Strict),
            NullLogger<LegendConnectTranslationRouter>.Instance, structuralComposition: structural.Object);

        var result = await router.DetectLanguageAsync(
            "Possible source", CancellationToken.None, LegendConnectExternalProviderPolicy.NativeOnly);

        Assert.Equal(composed, result.Succeeded);
        Assert.Equal(composed ? "en" : null, result.Language);
        Assert.Equal(1, GraphCalls(structural));
        Assert.Empty(provider.Invocations);
    }

    [Fact]
    public async Task CompleteEmptyCandidateSet_DoesNotIdentifyALanguageOrInvokeProvider()
    {
        var structural = StructuralGate();
        structural.Setup(value => value.GetReusableMeaningLanguageCandidatesAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectMeaningLanguageCandidates(true, [], "completed"));
        var provider = new Mock<ITranslationProvider>(MockBehavior.Strict);
        var router = new LegendConnectTranslationRouter(
            provider.Object, Registry(reverse: false).Object,
            Mock.Of<ITranslationCapacityAuthority>(MockBehavior.Strict),
            NullLogger<LegendConnectTranslationRouter>.Instance, structuralComposition: structural.Object);

        var result = await router.DetectLanguageAsync(
            "No governed candidate", CancellationToken.None, LegendConnectExternalProviderPolicy.NativeOnly);

        Assert.False(result.Succeeded);
        Assert.Null(result.Language);
        Assert.Equal("native_only_governed_source_language_undetermined", result.ErrorCode);
        Assert.Equal(0, GraphCalls(structural));
        Assert.Empty(provider.Invocations);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("null")]
    [InlineData("fault")]
    public async Task UnusableCandidateExclusion_PreservesCompetingLanguageProof(string state)
    {
        var structural = StructuralGate();
        var candidateRead = structural.Setup(value => value.GetReusableMeaningLanguageCandidatesAsync(
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()));
        if (state == "fault")
            candidateRead.ThrowsAsync(new InvalidOperationException("Controlled exclusion read failure"));
        else
            candidateRead.ReturnsAsync(state switch
            {
                "incomplete" => new LegendConnectMeaningLanguageCandidates(false, ["en"], "processing_bound_exceeded"),
                "unknown" => new LegendConnectMeaningLanguageCandidates(true, ["en", "xx"], "completed"),
                "duplicate" => new LegendConnectMeaningLanguageCandidates(true, ["en", "en"], "completed"),
                _ => null!
            });
        structural.Setup(value => value.AnalyzeReusableMeaningGraphAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComposedGraph());
        var provider = new Mock<ITranslationProvider>(MockBehavior.Strict);
        var router = new LegendConnectTranslationRouter(
            provider.Object, Registry(reverse: false).Object,
            Mock.Of<ITranslationCapacityAuthority>(MockBehavior.Strict),
            NullLogger<LegendConnectTranslationRouter>.Instance, structuralComposition: structural.Object);

        var result = await router.DetectLanguageAsync(
            "Shared governed surface", CancellationToken.None, LegendConnectExternalProviderPolicy.NativeOnly);

        Assert.False(result.Succeeded);
        Assert.Null(result.Language);
        Assert.Equal("native_only_governed_source_language_undetermined", result.ErrorCode);
        Assert.Equal(2, GraphCalls(structural));
        Assert.Empty(provider.Invocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CandidateExclusionCancellation_StopsBeforeGraphOrProvider(bool nativeOnly)
    {
        using var cancellation = new CancellationTokenSource();
        var structural = StructuralGate();
        structural.Setup(value => value.GetReusableMeaningLanguageCandidatesAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), cancellation.Token))
            .Returns(() =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<LegendConnectMeaningLanguageCandidates>(cancellation.Token);
            });
        var provider = new Mock<ITranslationProvider>(MockBehavior.Strict);
        var router = new LegendConnectTranslationRouter(
            provider.Object, Registry(reverse: false).Object,
            Mock.Of<ITranslationCapacityAuthority>(MockBehavior.Strict),
            NullLogger<LegendConnectTranslationRouter>.Instance, structuralComposition: structural.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.DetectLanguageAsync(
            "Unobserved surface", cancellation.Token, nativeOnly
                ? LegendConnectExternalProviderPolicy.NativeOnly
                : LegendConnectExternalProviderPolicy.ProviderEnabled));

        Assert.Equal(0, GraphCalls(structural));
        Assert.Empty(provider.Invocations);
    }

    private static Mock<ILegendConnectStructuralCompositionGate> StructuralGate()
    {
        var structural = new Mock<ILegendConnectStructuralCompositionGate>(MockBehavior.Strict);
        structural.Setup(value => value.GetReusableMeaningLanguageCandidatesAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectMeaningLanguageCandidates(false, [], "prefilter_not_available"));
        return structural;
    }

    private static int GraphCalls(Mock<ILegendConnectStructuralCompositionGate> structural) =>
        structural.Invocations.Count(invocation =>
            invocation.Method.Name == nameof(ILegendConnectStructuralCompositionGate.AnalyzeReusableMeaningGraphAsync));

    private static Mock<ILegendLanguageRegistry> Registry(bool reverse)
    {
        var registry = new Mock<ILegendLanguageRegistry>(MockBehavior.Strict);
        var languages = new[] { "en", "es" }.Select(code =>
            new LegendLanguageDefinitionSnapshot(code, code, code, code, true, true, true, code, code)).ToArray();
        registry.Setup(value => value.ListEnabledTranslationLanguagesReadOnlyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(reverse ? languages.Reverse().ToArray() : languages);
        registry.Setup(value => value.NormalizeEnabledTranslationLanguageReadOnlyAsync(
                "es", It.IsAny<CancellationToken>()))
            .ReturnsAsync("es");
        return registry;
    }

    private static LegendConnectUtteranceMeaningGraphSnapshot ComposedGraph() =>
        new(true, [new LegendConnectUtteranceMeaningNode("governed", "intent", "value", 0, 2, 2)], [], [],
            "meaning_graph_atomic_primitive_governed");
}
