using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectDetectionCancellationBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyCancelled_DoesNotInvokeAnyAuthority(bool nativeOnly)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = new Mock<ITranslationProvider>(MockBehavior.Strict);
        var registry = new Mock<ILegendLanguageRegistry>(MockBehavior.Strict);
        var gate = new Mock<ILegendConnectStructuralCompositionGate>(MockBehavior.Strict);
        var router = Router(provider, registry, gate);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.DetectLanguageAsync(
            "governed surface", cancellation.Token, Policy(nativeOnly)));

        Assert.Empty(provider.Invocations);
        Assert.Empty(registry.Invocations);
        Assert.Empty(gate.Invocations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GraphCompletesAfterCancellation_NeitherServesNorFallsBack(bool nativeOnly, bool composed)
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new Mock<ITranslationProvider>(MockBehavior.Strict);
        var registry = new Mock<ILegendLanguageRegistry>(MockBehavior.Strict);
        registry.Setup(value => value.ListEnabledTranslationLanguagesReadOnlyAsync(cancellation.Token))
            .ReturnsAsync(new[] { new LegendLanguageDefinitionSnapshot("es", "es", "es", "es", true, true, true, "es", "es") });
        var gate = new Mock<ILegendConnectStructuralCompositionGate>(MockBehavior.Strict);
        gate.Setup(value => value.GetReusableMeaningLanguageCandidatesAsync(
                It.IsAny<System.Collections.Generic.IReadOnlyList<string>>(), It.IsAny<string>(), cancellation.Token))
            .ReturnsAsync(new LegendConnectMeaningLanguageCandidates(true, ["es"], "completed"));
        gate.Setup(value => value.AnalyzeReusableMeaningGraphAsync("es", It.IsAny<string>(), cancellation.Token))
            .Returns(() =>
            {
                cancellation.Cancel();
                return Task.FromResult(composed
                    ? new LegendConnectUtteranceMeaningGraphSnapshot(true,
                        [new LegendConnectUtteranceMeaningNode("governed", "intent", "value", 0, 2, 2)], [], [], "meaning_graph_atomic_primitive_governed")
                    : new LegendConnectUtteranceMeaningGraphSnapshot(false, [], [], ["surface"], "meaning_graph_component_unknown"));
            });
        var router = Router(provider, registry, gate);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.DetectLanguageAsync(
            "governed surface", cancellation.Token, Policy(nativeOnly)));

        Assert.Empty(provider.Invocations);
        Assert.Single(registry.Invocations);
    }

    [Fact]
    public async Task ProviderCompletesAfterCancellation_DoesNotNormalizeOrServeItsLanguage()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new Mock<ITranslationProvider>(MockBehavior.Strict);
        provider.Setup(value => value.DetectLanguageAsync(It.IsAny<string>(), cancellation.Token,
                LegendConnectExternalProviderPolicy.ProviderEnabled))
            .Returns(() =>
            {
                cancellation.Cancel();
                return Task.FromResult(new TranslationDetectionResult(true, "es", Confidence: 1m));
            });
        var registry = new Mock<ILegendLanguageRegistry>(MockBehavior.Strict);
        var router = Router(provider, registry, null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.DetectLanguageAsync(
            "provider surface", cancellation.Token, LegendConnectExternalProviderPolicy.ProviderEnabled));

        Assert.Single(provider.Invocations);
        Assert.Empty(registry.Invocations);
    }

    private static LegendConnectExternalProviderPolicy Policy(bool nativeOnly) => nativeOnly
        ? LegendConnectExternalProviderPolicy.NativeOnly : LegendConnectExternalProviderPolicy.ProviderEnabled;

    private static LegendConnectTranslationRouter Router(Mock<ITranslationProvider> provider,
        Mock<ILegendLanguageRegistry> registry, Mock<ILegendConnectStructuralCompositionGate>? gate) =>
        new(provider.Object, registry.Object, Mock.Of<ITranslationCapacityAuthority>(MockBehavior.Strict),
            NullLogger<LegendConnectTranslationRouter>.Instance, structuralComposition: gate?.Object);
}
