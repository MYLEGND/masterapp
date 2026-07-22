using Microsoft.Extensions.Configuration;

namespace Infrastructure.Billing;

public sealed class ClientSubscriptionActivationPolicyOptions
{
    public string BusinessTimeZoneId { get; init; } = "America/Phoenix";
    public int SameDayAnchorCutoffHourLocal { get; init; } = 17;
    public int MinimumDaysBeforeAnchoredRenewal { get; init; } = 14;
    public string? DefaultProviderPlanVariationId { get; init; }
    public IReadOnlyDictionary<string, string> PlanVariationIds { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static ClientSubscriptionActivationPolicyOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Billing:ClientSubscriptions");
        var mappings = section.GetSection("PlanVariationIds")
            .GetChildren()
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
            .ToDictionary(x => x.Key.Trim(), x => x.Value!.Trim(), StringComparer.OrdinalIgnoreCase);

        var defaultPlanVariationId =
            Normalize(section["DefaultProviderPlanVariationId"])
            ?? Normalize(configuration["Square:ClientSubscriptionPlanVariationId"]);

        return new ClientSubscriptionActivationPolicyOptions
        {
            BusinessTimeZoneId = Normalize(section["BusinessTimeZoneId"]) ?? "America/Phoenix",
            SameDayAnchorCutoffHourLocal = int.TryParse(section["SameDayAnchorCutoffHourLocal"], out var cutoffHour) && cutoffHour is >= 0 and <= 23
                ? cutoffHour
                : 17,
            MinimumDaysBeforeAnchoredRenewal = int.TryParse(section["MinimumDaysBeforeAnchoredRenewal"], out var minDays) && minDays > 0
                ? minDays
                : 14,
            DefaultProviderPlanVariationId = defaultPlanVariationId,
            PlanVariationIds = mappings
        };
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
