using Microsoft.Extensions.Configuration;

namespace Infrastructure.Billing;

public sealed class ClientSubscriptionActivationPolicyOptions
{
    public string BusinessTimeZoneId { get; init; } = "America/Phoenix";
    public int SameDayAnchorCutoffHourLocal { get; init; } = 17;
    public int MinimumDaysBeforeAnchoredRenewal { get; init; } = 14;
    public int GracePeriodDays { get; init; } = 7;
    public IReadOnlyList<int> RenewalRetryDelayMinutes { get; init; } = [60, 1_440];

    public static ClientSubscriptionActivationPolicyOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Billing:ClientSubscriptions");
        var retryDelays = section.GetSection("RenewalRetryDelayMinutes")
            .GetChildren()
            .Select(x => int.TryParse(x.Value, out var minutes) && minutes > 0 ? minutes : 0)
            .Where(x => x > 0)
            .ToArray();

        return new ClientSubscriptionActivationPolicyOptions
        {
            BusinessTimeZoneId = Normalize(section["BusinessTimeZoneId"]) ?? "America/Phoenix",
            SameDayAnchorCutoffHourLocal = int.TryParse(section["SameDayAnchorCutoffHourLocal"], out var cutoffHour) && cutoffHour is >= 0 and <= 23
                ? cutoffHour
                : 17,
            MinimumDaysBeforeAnchoredRenewal = int.TryParse(section["MinimumDaysBeforeAnchoredRenewal"], out var minDays) && minDays > 0
                ? minDays
                : 14,
            GracePeriodDays = int.TryParse(section["GracePeriodDays"], out var gracePeriodDays) && gracePeriodDays >= 0
                ? gracePeriodDays
                : 7,
            RenewalRetryDelayMinutes = retryDelays.Length > 0 ? retryDelays : [60, 1_440]
        };
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
