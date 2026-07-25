namespace Domain.FinancialIntelligence;

/// <summary>
/// Central, deterministic weighting for Financial Intelligence. The values are
/// deliberately visible and bounded so feedback can adjust ordering without
/// changing a finding's evidence or suppressing an urgent risk.
/// </summary>
public static class FinancialIntelligencePrioritization
{
    private const decimal MaximumImpactPoints = 30m;
    private const decimal ImpactReferenceAmount = 1_000m;
    private const decimal MaximumFeedbackAdjustment = 8m;

    public static decimal Calculate(
        decimal? estimatedImpact,
        string urgency,
        decimal confidence,
        decimal dataCompleteness,
        string difficulty,
        IEnumerable<string>? priorFeedbackTypes)
    {
        var impactPoints = estimatedImpact is null
            ? 5m
            : Math.Min(
                MaximumImpactPoints,
                Math.Abs(estimatedImpact.Value) / ImpactReferenceAmount * MaximumImpactPoints);

        var urgencyPoints = Normalize(urgency) switch
        {
            "high" => 30m,
            "medium" => 20m,
            _ => 10m
        };

        var confidencePoints = ClampUnit(confidence) * 25m;
        var completenessPoints = ClampUnit(dataCompleteness) * 10m;
        var difficultyPoints = Normalize(difficulty) switch
        {
            "quick" => 5m,
            "moderate" => 3m,
            _ => 1m
        };

        var score = impactPoints
                    + urgencyPoints
                    + confidencePoints
                    + completenessPoints
                    + difficultyPoints
                    + CalculateFeedbackAdjustment(priorFeedbackTypes);

        // An urgent risk remains visible even after prior negative feedback.
        if (Normalize(urgency) == "high")
            score = Math.Max(score, 70m);

        return Math.Round(Math.Clamp(score, 0m, 100m), 2, MidpointRounding.AwayFromZero);
    }

    public static decimal CalculateFeedbackAdjustment(IEnumerable<string>? feedbackTypes)
    {
        if (feedbackTypes == null)
            return 0m;

        var adjustment = 0m;
        foreach (var feedbackType in feedbackTypes)
        {
            switch (Normalize(feedbackType))
            {
                case "helpful":
                case "accepted":
                case "actionstarted":
                case "completed":
                    adjustment += 2m;
                    break;
                case "nothelpful":
                case "dismissed":
                    adjustment -= 2m;
                    break;
            }
        }

        return Math.Clamp(adjustment, -MaximumFeedbackAdjustment, MaximumFeedbackAdjustment);
    }

    private static decimal ClampUnit(decimal value) => Math.Clamp(value, 0m, 1m);

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}
