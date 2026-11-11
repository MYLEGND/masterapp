using System;
using Domain.Billing;
using Xunit;

namespace AgentPortal.Tests;

public sealed class HouseholdRecurringPricingTests
{
    [Theory]
    [InlineData(10000, 5000, 15000)]
    [InlineData(9999, 5000, 14999)]
    [InlineData(7500, 3750, 11250)]
    [InlineData(1, 1, 2)]
    [InlineData(0, 0, 0)]
    [InlineData(120000, 60000, 180000)]
    public void UsesActualPrimaryAmountAndRoundsPartnerFractionalCentUp(int primary, int partner, int total)
    {
        var quote = ClientSubscriptionOfferPricing.ResolveHouseholdRecurringPrice(primary);
        Assert.Equal(primary, quote.PrimaryAmountCents);
        Assert.Equal(partner, quote.PartnerAmountCents);
        Assert.Equal(total, quote.CombinedAmountCents);
    }

    [Fact]
    public void InvalidOrOverflowingAmountsCannotBecomeCharges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClientSubscriptionOfferPricing.ResolveHouseholdRecurringPrice(-1));
        Assert.Throws<OverflowException>(() => ClientSubscriptionOfferPricing.ResolveHouseholdRecurringPrice(int.MaxValue));
    }
}
