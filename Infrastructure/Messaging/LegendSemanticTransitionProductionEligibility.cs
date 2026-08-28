using System.Text.Json;

namespace Infrastructure.Messaging;

/// <summary>
/// The single production-admissibility predicate for governed semantic
/// transition observations. Runtime inference and intelligence evaluation
/// consume this same authority so their eligibility answers cannot drift.
/// </summary>
public static class LegendSemanticTransitionProductionEligibility
{
    public static bool IsEligible(IEnumerable<LegendSemanticTransitionEligibilityObservation> source)
        => Assess(source).Tier == LegendSemanticTransitionEvidenceTier.ProductionEligible;

    /// <summary>
    /// Classifies one contradiction-free Founder transition without changing
    /// its evidence. Runtime inference can therefore prefer the production
    /// tier while still retaining a bounded Founder-observed tier when no
    /// mature alternative exists. Evaluation continues to count only the
    /// production tier through <see cref="IsEligible"/>.
    /// </summary>
    public static LegendSemanticTransitionEvidenceAssessment Assess(
        IEnumerable<LegendSemanticTransitionEligibilityObservation> source)
    {
        var observations = source as IReadOnlyList<LegendSemanticTransitionEligibilityObservation> ?? source.ToArray();
        if (observations.Count == 0 || observations.Any(item => item.ContributionState == "Contradictory"))
            return LegendSemanticTransitionEvidenceAssessment.None;

        var representative = observations[0];
        if (!observations.All(item =>
                item.SourceFrame == representative.SourceFrame &&
                item.ResultFrame == representative.ResultFrame) ||
            !IsCanonicalFrame(representative.SourceFrame) ||
            !IsCanonicalFrame(representative.ResultFrame))
        {
            return LegendSemanticTransitionEvidenceAssessment.None;
        }

        var independentSourceCount = observations
            .Where(item => item.IsHumanVerifiedSupport && item.ContributionState == "Supported")
            .Select(item => item.IndependentSourceIdentity)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var tier = independentSourceCount switch
        {
            >= 3 => LegendSemanticTransitionEvidenceTier.ProductionEligible,
            >= 1 => LegendSemanticTransitionEvidenceTier.FounderObserved,
            _ => LegendSemanticTransitionEvidenceTier.None
        };
        return new(tier, independentSourceCount);
    }

    private static bool IsCanonicalFrame(string serialized)
    {
        try
        {
            var dimensions = JsonSerializer.Deserialize<SortedDictionary<string, string>>(serialized);
            return dimensions is { Count: >= 1 and <= 12 } &&
                dimensions.All(item => !string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value)) &&
                string.Equals(JsonSerializer.Serialize(dimensions), serialized, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public enum LegendSemanticTransitionEvidenceTier
{
    None = 0,
    FounderObserved = 1,
    ProductionEligible = 2
}

public sealed record LegendSemanticTransitionEvidenceAssessment(
    LegendSemanticTransitionEvidenceTier Tier,
    int IndependentSourceCount)
{
    public static LegendSemanticTransitionEvidenceAssessment None { get; } =
        new(LegendSemanticTransitionEvidenceTier.None, 0);
}

public sealed record LegendSemanticTransitionEligibilityObservation(
    string SourceFrame,
    string ResultFrame,
    string IndependentSourceIdentity,
    string ContributionState,
    bool IsHumanVerifiedSupport);
