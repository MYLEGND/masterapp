using Domain.Entities;

namespace Domain.Billing;

public sealed record TrainingPlanTerms(int MonthlyAmountCents, string Name, string Benefits, string? PromotionCode)
{
    public static TrainingPlanTerms FromCatalog(CommerceProduct product, string? promotionCode)
    {
        if (!product.IsActive || product.BillingKind != ClientSubscriptionScope.MonthlyTraining ||
            product.PriceCents <= 0 || product.PriceCents > ClientSubscriptionOfferPricing.CustomMaximumCents || string.IsNullOrWhiteSpace(product.Name) || string.IsNullOrWhiteSpace(product.Description) || product.Name.Length > 256 || product.Description.Length > 12000)
            throw new InvalidOperationException("The training plan is unavailable.");

        var code = promotionCode?.Trim().ToUpperInvariant();
        var amount = product.PriceCents;
        if (!string.IsNullOrWhiteSpace(code))
        {
            var promotion = product.Discounts.SingleOrDefault(x => x.IsActive &&
                string.Equals(x.Code.Trim(), code, StringComparison.OrdinalIgnoreCase));
            if (promotion is null || promotion.Amount <= 0)
                throw new InvalidOperationException("The recurring promotion is unavailable.");
            var discount = promotion.DiscountType switch
            {
                "Percent" when promotion.Amount < 100 => decimal.Round(amount * promotion.Amount / 100m, 0, MidpointRounding.AwayFromZero),
                "Fixed" => decimal.Round(promotion.Amount * 100m, 0, MidpointRounding.AwayFromZero),
                _ => throw new InvalidOperationException("The recurring promotion is invalid.")
            };
            if (discount <= 0 || discount >= amount)
                throw new InvalidOperationException("A training subscription must have a positive monthly price.");
            amount -= checked((int)discount);
        }
        return new(amount, product.Name.Trim(), product.Description.Trim(), string.IsNullOrWhiteSpace(code) ? null : code);
    }
}
