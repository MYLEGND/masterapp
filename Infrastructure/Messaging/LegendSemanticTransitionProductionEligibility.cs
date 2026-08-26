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
    {
        var observations = source as IReadOnlyList<LegendSemanticTransitionEligibilityObservation> ?? source.ToArray();
        if (observations.Count == 0 || observations.Any(item => item.ContributionState == "Contradictory"))
            return false;

        var representative = observations[0];
        return observations
                .Where(item => item.IsHumanVerifiedSupport && item.ContributionState == "Supported")
                .Select(item => item.IndependentSourceIdentity)
                .Distinct(StringComparer.Ordinal)
                .Count() >= 3 &&
            observations.All(item => item.SourceFrame == representative.SourceFrame && item.ResultFrame == representative.ResultFrame) &&
            IsCanonicalFrame(representative.SourceFrame) && IsCanonicalFrame(representative.ResultFrame);
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

public sealed record LegendSemanticTransitionEligibilityObservation(
    string SourceFrame,
    string ResultFrame,
    string IndependentSourceIdentity,
    string ContributionState,
    bool IsHumanVerifiedSupport);
