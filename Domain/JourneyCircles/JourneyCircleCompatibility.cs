using System.Text.Json;
using Domain.Entities;

namespace Domain.JourneyCircles;

/// <summary>
/// The normalized, comparable traits of one Journey Circles profile.
/// </summary>
public sealed class JourneyCircleTraits
{
    public required HashSet<string> Goals { get; init; }
    public required HashSet<string> Interests { get; init; }
    public required HashSet<string> Circles { get; init; }
    public required HashSet<string> LifeStages { get; init; }
    public required HashSet<string> Locations { get; init; }
    public required HashSet<string> ConnectionTypes { get; init; }
    public required HashSet<string> CommunicationStyles { get; init; }
    public required HashSet<string> AccountabilityFrequencies { get; init; }
}

/// <summary>
/// The outcome of comparing two profiles. Callers decide what to do with it:
/// Journey Circles suggestions apply a minimum bar, while Discover uses the score
/// purely to order results and never to exclude them.
/// </summary>
public sealed record JourneyCircleCompatibility(
    int Score,
    int ComparableCategoryCount,
    bool HasMeaningfulAnchor,
    IReadOnlyList<string> Reasons,
    string MatchStrength)
{
    public static readonly JourneyCircleCompatibility None =
        new(0, 0, false, Array.Empty<string>(), "Good match");

    /// <summary>The suggestion-feed sentence. Kept identical to the original wording.</summary>
    public string RecommendationExplanation =>
        Reasons.Count == 0
            ? $"Recommended for you. {MatchStrength}."
            : "Recommended for you. " + MatchStrength + ". " + string.Join("; ", Reasons.Take(3)) + ".";

    /// <summary>A neutral sentence for search results, where "recommended" would be wrong.</summary>
    public string? DiscoveryExplanation =>
        Reasons.Count == 0 ? null : string.Join("; ", Reasons.Take(3)) + ".";
}

/// <summary>
/// Shared compatibility scoring for every surface that ranks Journey Circles members.
/// Weights live here once so Discover and the suggestion feed can never drift apart.
/// </summary>
public static class JourneyCircleCompatibilityScorer
{
    private const double GoalWeight = 25d;
    private const double ConnectionTypeWeight = 15d;
    private const double CircleWeight = 15d;
    private const double LifeStageWeight = 10d;
    private const double InterestWeight = 10d;
    private const double CommunicationStyleWeight = 10d;
    private const double AccountabilityWeight = 10d;
    private const double LocationWeight = 5d;

    public static JourneyCircleTraits Traits(JourneyCircleProfile profile) => new()
    {
        Goals = Normalize(FromJson(profile.GoalsJson)),
        Interests = Normalize(FromJson(profile.InterestsJson)),
        Circles = Normalize(FromJson(profile.CircleCodesJson)),
        LifeStages = Normalize(FromDelimited(profile.LifeStage)),
        Locations = Normalize(FromDelimited(profile.LocationLabel)),
        ConnectionTypes = Normalize(FromJson(profile.ConnectionTypesJson)),
        CommunicationStyles = Normalize(FromDelimited(profile.CommunicationStyle)),
        AccountabilityFrequencies = Normalize(FromDelimited(profile.AccountabilityFrequency))
    };

    public static JourneyCircleCompatibility Evaluate(
        JourneyCircleTraits source,
        JourneyCircleTraits candidate)
    {
        var comparableCategoryCount =
            (IsComparable(source.Goals, candidate.Goals) ? 1 : 0) +
            (IsComparable(source.ConnectionTypes, candidate.ConnectionTypes) ? 1 : 0) +
            (IsComparable(source.Circles, candidate.Circles) ? 1 : 0) +
            (IsComparable(source.LifeStages, candidate.LifeStages) ? 1 : 0) +
            (IsComparable(source.Interests, candidate.Interests) ? 1 : 0) +
            (IsComparable(source.CommunicationStyles, candidate.CommunicationStyles) ? 1 : 0) +
            (IsComparable(source.AccountabilityFrequencies, candidate.AccountabilityFrequencies) ? 1 : 0) +
            (IsComparable(source.Locations, candidate.Locations) ? 1 : 0);

        var sharedGoal = FirstShared(source.Goals, candidate.Goals);
        var sharedConnectionType = FirstShared(source.ConnectionTypes, candidate.ConnectionTypes);
        var sharedCircle = FirstShared(source.Circles, candidate.Circles);
        var hasMeaningfulAnchor =
            sharedGoal is not null || sharedConnectionType is not null || sharedCircle is not null;

        double earnedWeight = 0d;
        double availableWeight = 0d;

        void ScoreCategory(HashSet<string> first, HashSet<string> second, double weight)
        {
            if (!IsComparable(first, second))
                return;

            availableWeight += weight;
            earnedWeight += DiceSimilarity(first, second) * weight;
        }

        ScoreCategory(source.Goals, candidate.Goals, GoalWeight);
        ScoreCategory(source.ConnectionTypes, candidate.ConnectionTypes, ConnectionTypeWeight);
        ScoreCategory(source.Circles, candidate.Circles, CircleWeight);
        ScoreCategory(source.LifeStages, candidate.LifeStages, LifeStageWeight);
        ScoreCategory(source.Interests, candidate.Interests, InterestWeight);
        ScoreCategory(source.CommunicationStyles, candidate.CommunicationStyles, CommunicationStyleWeight);
        ScoreCategory(source.AccountabilityFrequencies, candidate.AccountabilityFrequencies, AccountabilityWeight);
        ScoreCategory(source.Locations, candidate.Locations, LocationWeight);

        if (availableWeight <= 0d)
            return JourneyCircleCompatibility.None with { ComparableCategoryCount = comparableCategoryCount };

        var score = (int)Math.Round(
            earnedWeight / availableWeight * 100d,
            MidpointRounding.AwayFromZero);

        var reasons = new List<string>();
        if (sharedGoal is not null)
            reasons.Add($"shared goal: {sharedGoal}");
        if (sharedConnectionType is not null)
            reasons.Add("shared connection preference: " + sharedConnectionType);
        if (sharedCircle is not null)
            reasons.Add($"shared Journey Circle: {sharedCircle}");

        var sharedStage = FirstShared(source.LifeStages, candidate.LifeStages);
        if (sharedStage is not null)
            reasons.Add($"shared life stage: {sharedStage}");

        var sharedInterest = FirstShared(source.Interests, candidate.Interests);
        if (sharedInterest is not null)
            reasons.Add($"shared interest: {sharedInterest}");

        var sharedCommunicationStyle =
            FirstShared(source.CommunicationStyles, candidate.CommunicationStyles);
        if (sharedCommunicationStyle is not null)
            reasons.Add("shared communication style: " + sharedCommunicationStyle);

        var sharedFrequency =
            FirstShared(source.AccountabilityFrequencies, candidate.AccountabilityFrequencies);
        if (sharedFrequency is not null)
            reasons.Add("shared accountability preference: " + sharedFrequency);

        var sharedLocation = FirstShared(source.Locations, candidate.Locations);
        if (sharedLocation is not null)
            reasons.Add($"shared location: {sharedLocation}");

        return new JourneyCircleCompatibility(
            score,
            comparableCategoryCount,
            hasMeaningfulAnchor,
            reasons,
            MatchStrengthFor(score));
    }

    private static string MatchStrengthFor(int score) =>
        score >= 85 ? "Exceptional match"
        : score >= 70 ? "Excellent match"
        : score >= 60 ? "Strong match"
        : "Good match";

    public static HashSet<string> Normalize(IEnumerable<string> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static double DiceSimilarity(HashSet<string> first, HashSet<string> second)
    {
        if (first.Count == 0 || second.Count == 0)
            return 0d;

        var sharedCount = first.Count(second.Contains);
        return (2d * sharedCount) / (first.Count + second.Count);
    }

    private static bool IsComparable(HashSet<string> first, HashSet<string> second) =>
        first.Count > 0 && second.Count > 0;

    private static string? FirstShared(HashSet<string> first, HashSet<string> second) =>
        first.FirstOrDefault(second.Contains);

    public static IReadOnlyList<string> FromJson(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json ?? "[]")
                ?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToArray() ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static IReadOnlyList<string> FromDelimited(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
}
