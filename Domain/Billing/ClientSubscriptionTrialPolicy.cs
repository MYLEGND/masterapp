namespace Domain.Billing;

/// <summary>
/// Central validation for founder-controlled introductory trials. An offer is
/// always explicit: zero means no trial, while a positive value is the exact
/// number of full days before the first premium charge is due.
/// </summary>
public static class ClientSubscriptionTrialPolicy
{
    public const int MaximumFreeTrialDays = 3_650;

    public static int ResolveFreeTrialDays(
        bool hasFreeTrial,
        int? requestedDays,
        bool founderAuthorized)
    {
        if (!hasFreeTrial)
            return 0;

        if (!founderAuthorized)
        {
            throw new InvalidOperationException(
                "Only the founder can add a free trial to a client subscription.");
        }

        if (!requestedDays.HasValue || requestedDays.Value < 1 || requestedDays.Value > MaximumFreeTrialDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedDays),
                $"Free trials must be between 1 and {MaximumFreeTrialDays} days.");
        }

        return requestedDays.Value;
    }
}
