namespace Domain.Billing;

public static class ClientSubscriptionOfferPricing
{
    public const int Fixed50Cents = 5_000;
    public const int Fixed75Cents = 7_500;
    public const int Fixed100Cents = 10_000;
    public const int Fixed150Cents = 15_000;
    public const int FounderCustomMinimumCents = 0;
    public const int CustomMinimumCents = Fixed50Cents;
    public const int CustomMaximumCents = 250_000;

    public static int ResolveAuthoritativeMonthlyAmountCents(
        ClientSubscriptionOfferPriceType priceType,
        int? customMonthlyAmountCents,
        int customMinimumCents = CustomMinimumCents)
    {
        if (customMinimumCents < FounderCustomMinimumCents || customMinimumCents > CustomMinimumCents)
        {
            throw new ArgumentOutOfRangeException(
                nameof(customMinimumCents),
                "The custom subscription minimum must be either $0.00 or $50.00.");
        }

        return priceType switch
        {
            ClientSubscriptionOfferPriceType.Fixed50 => Fixed50Cents,
            ClientSubscriptionOfferPriceType.Fixed75 => Fixed75Cents,
            ClientSubscriptionOfferPriceType.Fixed100 => Fixed100Cents,
            ClientSubscriptionOfferPriceType.Fixed150 => Fixed150Cents,
            ClientSubscriptionOfferPriceType.Custom when customMonthlyAmountCents.HasValue &&
                                                        customMonthlyAmountCents.Value >= customMinimumCents &&
                                                        customMonthlyAmountCents.Value <= CustomMaximumCents
                => customMonthlyAmountCents.Value,
            ClientSubscriptionOfferPriceType.Custom => throw new ArgumentOutOfRangeException(
                nameof(customMonthlyAmountCents),
                $"Custom offers must provide a monthly amount between {customMinimumCents} and {CustomMaximumCents} cents."),
            _ => throw new ArgumentOutOfRangeException(nameof(priceType), priceType, "Unsupported subscription offer price type.")
        };
    }

    public static int? ResolveBillingAnchorDay(BillingAnchorSelectionMode mode, int? selectedBillingAnchorDay)
    {
        return mode switch
        {
            BillingAnchorSelectionMode.FirstOfMonth => 1,
            BillingAnchorSelectionMode.FifteenthOfMonth => 15,
            BillingAnchorSelectionMode.SpecificDayOfMonth when selectedBillingAnchorDay is >= 1 and <= 31 => selectedBillingAnchorDay,
            BillingAnchorSelectionMode.SpecificDayOfMonth => throw new ArgumentOutOfRangeException(
                nameof(selectedBillingAnchorDay),
                "Specific-day billing anchors must use a day between 1 and 31."),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported billing anchor selection mode.")
        };
    }
}
