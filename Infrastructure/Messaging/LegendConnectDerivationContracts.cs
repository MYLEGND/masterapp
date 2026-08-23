using Domain.Messaging;

namespace Infrastructure.Messaging;

/// <summary>
/// Deployment declarations for the one evaluator's derivation graph.  This
/// catalog does not execute, schedule, or interpret language.  It supplies
/// the contract identities that the existing runtime-policy authority and
/// durable historical-work authority use to calculate an invalidation
/// frontier.
///
/// A later evaluator changes this data declaration (for example, a contract
/// version or dependency edge); it does not add another replay path.  The
/// generic comparison logic intentionally has no evaluator-number branches.
/// </summary>
internal static class LegendConnectDerivationContracts
{
    internal const string SourceSemanticProjection = "source-semantic-projection";
    internal const string AlignmentSemanticProjection = "alignment-semantic-projection";
    internal const string ProviderObservationProjection = "provider-observation-projection";
    internal const string OperationalTranslationProjection = "operational-translation-projection";
    internal const string GovernedSemanticTransformation = "governed-semantic-transformation";
    internal const string GovernedContentBinding = "governed-content-binding";

    private static readonly IReadOnlyList<LegendConnectDerivationContractDefinition> Declarations =
    [
        new(
            SourceSemanticProjection,
            "1",
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            RequiresHistoricalWork: true,
            IntroducedEvaluatorVersion: 1,
            DependencyKinds: [],
            ArtifactKinds: ["compositional-anchor", "meaning-node", "meaning-relation", "semantic-transformation"],
            RequiresDependencyInventory: false),
        new(
            AlignmentSemanticProjection,
            "1",
            LegendConnectLanguageIntelligenceReevaluationPhases.Alignments,
            RequiresHistoricalWork: true,
            IntroducedEvaluatorVersion: 1,
            DependencyKinds: [SourceSemanticProjection],
            ArtifactKinds: ["translation-alignment"],
            RequiresDependencyInventory: false),
        new(
            ProviderObservationProjection,
            "1",
            LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
            RequiresHistoricalWork: true,
            IntroducedEvaluatorVersion: 1,
            DependencyKinds: [AlignmentSemanticProjection],
            ArtifactKinds: ["provider-observation"],
            RequiresDependencyInventory: false),
        new(
            OperationalTranslationProjection,
            "1",
            LegendConnectLanguageIntelligenceReevaluationPhases.OperationalTranslations,
            RequiresHistoricalWork: true,
            IntroducedEvaluatorVersion: 1,
            DependencyKinds: [ProviderObservationProjection],
            ArtifactKinds: ["operational-translation"],
            RequiresDependencyInventory: false),
        new(
            GovernedSemanticTransformation,
            "1",
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            RequiresHistoricalWork: true,
            IntroducedEvaluatorVersion: 19,
            DependencyKinds: [SourceSemanticProjection],
            ArtifactKinds: ["semantic-transformation"],
            RequiresDependencyInventory: false),
        // Stage 6 adds no persisted response cache or second evidence layer.
        // Its governed content binding reads mature existing primitives and
        // meaning relations at serving time, so it is a runtime contract with
        // no historical canonical evaluator work to enqueue.
        new(
            GovernedContentBinding,
            "1",
            LegendConnectLanguageIntelligenceReevaluationPhases.Complete,
            RequiresHistoricalWork: false,
            IntroducedEvaluatorVersion: 20,
            DependencyKinds: [GovernedSemanticTransformation],
            ArtifactKinds: [],
            RequiresDependencyInventory: true)
    ];

    internal static IReadOnlyList<LegendConnectDerivationContractDefinition> ForEvaluator(int evaluatorVersion) =>
        Declarations
            .Where(item => item.IntroducedEvaluatorVersion <= evaluatorVersion)
            .GroupBy(item => item.DerivationKind, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.IntroducedEvaluatorVersion)
                .ThenByDescending(item => item.ContractVersion, StringComparer.Ordinal)
                .First())
            .OrderBy(item => item.DerivationKind, StringComparer.Ordinal)
            .ToArray();

    internal static int PhaseRank(string phase) => phase switch
    {
        LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies => 0,
        LegendConnectLanguageIntelligenceReevaluationPhases.Alignments => 1,
        LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations => 2,
        LegendConnectLanguageIntelligenceReevaluationPhases.OperationalTranslations => 3,
        _ => int.MaxValue
    };

    internal static string ContractIdentityFor(
        int evaluatorVersion,
        string derivationKind) => ForEvaluator(evaluatorVersion)
        .Single(item => string.Equals(item.DerivationKind, derivationKind, StringComparison.Ordinal))
        .ContractIdentity;
}

/// <summary>
/// A declarative evaluator contract. Contract identity is content-addressed
/// from the contract's kind, version, work boundary, materialization rule,
/// and direct dependency kinds. It never contains Founder text or response
/// language.
/// </summary>
internal sealed record LegendConnectDerivationContractDefinition(
    string DerivationKind,
    string ContractVersion,
    string EarliestPhase,
    bool RequiresHistoricalWork,
    int IntroducedEvaluatorVersion,
    IReadOnlyList<string> DependencyKinds,
    IReadOnlyList<string> ArtifactKinds,
    bool RequiresDependencyInventory)
{
    internal string ContractIdentity => LegendLanguageIdentity.TextHash(string.Join("|",
        "legend-derivation-contract",
        DerivationKind,
        ContractVersion,
        EarliestPhase,
        RequiresHistoricalWork ? "materialized" : "runtime",
        string.Join(";", DependencyKinds.OrderBy(item => item, StringComparer.Ordinal)),
        string.Join(";", ArtifactKinds.OrderBy(item => item, StringComparer.Ordinal)),
        RequiresDependencyInventory ? "dependency-inventory" : "no-dependency-inventory"));
}

internal sealed record LegendConnectDerivationConvergencePlan(
    int TargetEvaluatorVersion,
    int BaselineEvaluatorVersion,
    IReadOnlyList<LegendConnectDerivationContractDefinition> CurrentContracts,
    IReadOnlyList<LegendConnectDerivationContractDefinition> ChangedContracts,
    IReadOnlyList<LegendConnectDerivationContractDefinition> ReusedContracts,
    string? EarliestAffectedPhase,
    long ExistingCanonicalArtifactCount,
    long ReusedCanonicalArtifactCount,
    long AffectedCanonicalArtifactCount)
{
    internal bool RequiresHistoricalWork => EarliestAffectedPhase is not null;
}
