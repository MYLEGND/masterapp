using System;
using System.Linq;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceFulfillmentTests
{
    [Theory]
    [InlineData("Processing")]
    [InlineData("Packed")]
    [InlineData("Shipped")]
    [InlineData("Fulfilled")]
    public void UnpaidOrderCannotAdvance(string status)
    {
        using var f = new Fixture();
        f.Order.PaymentStatus = "Pending";
        f.Db.SaveChanges();
        var request = f.Request(status);
        request.PaymentStatus = "Pending";
        Assert.False(f.Service.UpdateOrder(f.Business, request, out var error));
        Assert.Contains("payment", error!);
        Assert.Equal("Unfulfilled", f.Db.CommerceOrders.Single().FulfillmentStatus);
    }

    [Fact]
    public void ShippingRequiresBothCarrierAndTrackingAndPersistsThem()
    {
        using var f = new Fixture();
        var request = f.Request("Shipped");
        request.TrackingNumber = null;
        Assert.False(f.Service.UpdateOrder(f.Business, request, out _));
        request.TrackingNumber = "TEST-TRACKING";
        request.TrackingCarrier = null;
        Assert.False(f.Service.UpdateOrder(f.Business, request, out _));
        request.TrackingCarrier = "User selected carrier";
        Assert.True(f.Service.UpdateOrder(f.Business, request, out _));
        f.Db.ChangeTracker.Clear();
        var saved = f.Db.CommerceOrders.Single();
        Assert.Equal("Shipped", saved.FulfillmentStatus);
        Assert.Equal("TEST-TRACKING", saved.TrackingNumber);
        Assert.NotNull(saved.ShippedUtc);
    }

    [Fact]
    public void PickupCompletionDoesNotInventShipment()
    {
        using var f = new Fixture();
        Assert.True(f.Service.UpdateOrder(f.Business, f.Request("Fulfilled"), out _));
        f.Db.ChangeTracker.Clear();
        var saved = f.Db.CommerceOrders.Single();
        Assert.NotNull(saved.FulfilledUtc);
        Assert.Null(saved.ShippedUtc);
    }

    [Fact]
    public void StaleOrMissingRevisionCannotOverwriteNewerFulfillment()
    {
        using var f = new Fixture();
        var stale = f.Request("Packed");
        Assert.True(f.Service.UpdateOrder(f.Business, f.Request("Processing"), out _));
        Assert.False(f.Service.UpdateOrder(f.Business, stale, out var error));
        Assert.Contains("changed", error!);
        stale.ExpectedUpdatedTicks = null;
        Assert.False(f.Service.UpdateOrder(f.Business, stale, out _));
        Assert.Equal("Processing", f.Db.CommerceOrders.Single().FulfillmentStatus);
    }

    [Fact]
    public void RevisionIsReloadedWhenAnotherContextUpdatedTrackedOrder()
    {
        using var f = new Fixture();
        var request = f.Request("Packed");
        using (var other = new MasterAppDbContext(f.Options))
        {
            var order = other.CommerceOrders.Single();
            order.UpdatedUtc = DateTime.UtcNow.AddMinutes(1);
            order.AdminNotes = "New note from another operator";
            other.SaveChanges();
        }
        Assert.False(f.Service.UpdateOrder(f.Business, request, out _));
        Assert.Equal("New note from another operator", f.Db.CommerceOrders.Single().AdminNotes);
    }

    [Fact]
    public void OtherBusinessCannotUpdateOrReadPackingOrder()
    {
        using var f = new Fixture();
        Assert.False(f.Service.UpdateOrder(Guid.NewGuid(), f.Request("Packed"), out _));
        Assert.Null(f.Service.GetOrder(Guid.NewGuid(), "TEST-ORDER"));
    }

    [Fact]
    public void ProcessorFinancialEvidenceCannotBeEditedDuringFulfillment()
    {
        using var f = new Fixture();
        f.Db.CommercePaymentAttempts.Add(new() { CommerceBusinessId = f.Business, CommerceOrderId = f.Order.Id });
        f.Db.SaveChanges();
        var request = f.Request("Packed");
        request.RefundedCents = 100;
        Assert.False(f.Service.UpdateOrder(f.Business, request, out var error));
        Assert.Contains("provider", error!);
        Assert.Equal(0, f.Db.CommerceOrders.Single().RefundedCents);
    }

    [Fact]
    public void RefundedShipmentStillAllowsReturnAndNotesWithoutChangingPaymentEvidence()
    {
        using var f = new Fixture();
        f.Order.PaymentStatus = "Refunded";
        f.Order.RefundedCents = f.Order.TotalCents;
        f.Order.FulfillmentStatus = "Shipped";
        f.Order.ShippedUtc = DateTime.UtcNow.AddDays(-2);
        f.Db.CommercePaymentAttempts.Add(new() { CommerceBusinessId = f.Business, CommerceOrderId = f.Order.Id });
        f.Db.SaveChanges();
        var request = f.Request("Shipped");
        request.PaymentStatus = "Refunded";
        request.RefundedCents = f.Order.TotalCents;
        request.ReturnStatus = "Received";
        request.AdminNotes = "Returned goods inspected";
        Assert.True(f.Service.UpdateOrder(f.Business, request, out _));
        Assert.Equal("Received", f.Db.CommerceOrders.Single().ReturnStatus);
        Assert.Equal("Refunded", f.Db.CommerceOrders.Single().PaymentStatus);
    }

    [Fact]
    public void LegacyNullRevisionRoundTripsThroughTheDisplayedOrder()
    {
        using var f = new Fixture();
        f.Order.UpdatedUtc = null;
        f.Db.SaveChanges();
        var displayed = f.Service.GetOrder(f.Business, f.Order.OrderNumber)!;
        var input = f.Request("Packed");
        input.ExpectedUpdatedTicks = displayed.UpdatedUtc?.Ticks ?? 0;
        Assert.True(f.Service.UpdateOrder(f.Business, input, out _));
        Assert.Equal("Packed", f.Db.CommerceOrders.Single().FulfillmentStatus);
    }

    [Fact]
    public void LegacyProcessorPaymentCannotBeManuallyRewritten()
    {
        using var f = new Fixture();
        f.Db.SubscriptionPayments.Add(new() { CommerceOrderId = f.Order.Id, AmountCents = f.Order.TotalCents });
        f.Db.SaveChanges();
        Assert.True(f.Service.GetOrder(f.Business, f.Order.OrderNumber)!.PaymentManagedByProcessor);
        Assert.True(f.Service.GetAllOrders(f.Business).Single().PaymentManagedByProcessor);
        var request = f.Request("Packed"); request.RefundedCents = 1;
        Assert.False(f.Service.UpdateOrder(f.Business, request, out _));
        Assert.Equal(0, f.Db.CommerceOrders.Single().RefundedCents);
    }

    [Fact]
    public void UnresolvedPostagePreventsCancellationUntilReconciled()
    {
        using var f = new Fixture();
        f.Order.ShippingLabelStateJson = "{\"Status\":\"PurchasePending\"}";
        f.Db.SaveChanges();
        Assert.False(f.Service.UpdateOrder(f.Business, f.Request("Cancelled"), out var error));
        Assert.Contains("shipping label", error!);
        Assert.Equal("Unfulfilled", f.Db.CommerceOrders.Single().FulfillmentStatus);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=False");
        public readonly Guid Business = Guid.NewGuid();
        public readonly CommerceOrder Order;
        public readonly DbContextOptions<MasterAppDbContext> Options;
        public readonly MasterAppDbContext Db;
        public readonly ParfaitOrderService Service;
        public Fixture()
        {
            connection.Open();
            Options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options;
            Db = new(Options);
            Db.Database.EnsureCreated();
            Order = new() { CommerceBusinessId = Business, OrderNumber = "TEST-ORDER", PaymentStatus = "Paid", TotalCents = 1000, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
            Db.CommerceOrders.Add(Order);
            Db.SaveChanges();
            Service = new(Db);
        }
        public ParfaitOrderAdminUpdateRequest Request(string fulfillment) => new() {
            OrderNumber = Order.OrderNumber, ExpectedUpdatedTicks = Order.UpdatedUtc?.Ticks ?? 0,
            PaymentStatus = "Paid", FulfillmentStatus = fulfillment, TrackingCarrier = "Carrier", TrackingNumber = "TRACKING"
        };
        public void Dispose() { Db.Dispose(); connection.Dispose(); }
    }
}
