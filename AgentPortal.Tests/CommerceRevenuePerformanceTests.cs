using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceRevenuePerformanceTests
{
    [Theory]
    [InlineData(200, 100, "success", "Revenue increased", 100)]
    [InlineData(50, 100, "danger", "Revenue decreased", -50)]
    [InlineData(100, 100, "warning", "Revenue unchanged", 0)]
    public void DirectionAndTextUseActualNetChange(int current, int previous, string tone, string label, int percent)
    {
        var result = new CommerceRevenuePerformance(current, previous);
        Assert.Equal(tone, result.Tone);
        Assert.Equal(label, result.Label);
        Assert.Equal((decimal)percent, result.ChangePercent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void ZeroBaselineDoesNotInventGrowth(int current)
    {
        var result = new CommerceRevenuePerformance(current, 0);
        Assert.Equal("warning", result.Tone);
        Assert.Equal("No prior revenue baseline", result.Label);
        Assert.Null(result.ChangePercent);
    }

    [Fact]
    public void EqualWindowsUseCanonicalPaymentEligibilityRefundsAndExclusiveBoundaries()
    {
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var service = new ParfaitOrderService(db);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        ParfaitOrderRecord Order(int days, string status, int gross, int refund = 0) => new()
        { OrderNumber = Guid.NewGuid().ToString(), FirstName = "Test", LastName = "Buyer",
          Email = "buyer@example.test", Phone = "", AddressLine1 = "Test address",
          City = "Test city", State = "CA", PostalCode = "90001",
          CreatedUtc = now.AddDays(days), PaymentStatus = status, TotalCents = gross, RefundedCents = refund };
        var result = service.GetRevenuePerformance(new[] {
            Order(-60, "Paid", 1000), Order(-30, "Paid", 1500, 500),
            Order(-1, "Refunded", 500, 500), Order(-1, "Pending", 5000),
            Order(-1, "Failed", 5000), Order(0, "Paid", 5000), Order(-61, "Paid", 5000)
        }, now);
        Assert.Equal(1000, result.CurrentCents);
        Assert.Equal(1000, result.PreviousCents);
        Assert.Equal("warning", result.Tone);
    }
}
