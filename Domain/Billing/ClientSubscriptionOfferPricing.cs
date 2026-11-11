namespace Domain.Billing;

public static class ClientSubscriptionOfferPricing
{
    /// <summary>
    /// Uses the primary's actual recurring amount after applicable discounts,
    /// including a retained legacy price, in the same currency and interval.
    /// Fractional cents round up per Founder policy. This calculation neither
    /// quotes provider proration nor authorizes a subscription change or charge.
    /// Trial dates and effective changes remain owned by the billing lifecycle.
    /// </summary>
    public static HouseholdRecurringPrice ResolveHouseholdRecurringPrice(int primaryRecurringAmountCents)
    {
        if (primaryRecurringAmountCents < 0) throw new ArgumentOutOfRangeException(nameof(primaryRecurringAmountCents));
        var partner = primaryRecurringAmountCents / 2 + primaryRecurringAmountCents % 2;
        return new(primaryRecurringAmountCents, partner, checked(primaryRecurringAmountCents + partner));
    }
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

public sealed record HouseholdRecurringPrice(int PrimaryAmountCents, int PartnerAmountCents, int CombinedAmountCents);
