using System;
using System.IO;
using System.Linq;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Moq;
using ParfaitApp.Models;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceShipmentNotificationTests
{
    [Fact]
    public void ShipmentWithoutMarketingConsentUsesExistingDurableClaimAndCompletion()
    {
        using var f = new Fixture();
        Assert.True(f.Update());
        Assert.Empty(f.Db.WebsiteLeads);
        Assert.Null(f.Db.CommerceBusinessSettings.Single().CustomerAutomationStateJson);
        var candidate = Assert.Single(f.Service.GetDueDispatchCandidatesForAllBusinesses());
        Assert.Contains("&lt;Buyer&gt;", candidate.HtmlBody);
        Assert.Contains("Carrier &amp; Co", candidate.HtmlBody);
        Assert.DoesNotContain("<Buyer>", candidate.HtmlBody);
        Assert.True(f.Service.TryBeginDispatch(candidate));
        Assert.False(f.Service.TryBeginDispatch(candidate));
        f.Service.MarkDispatchSent(candidate);
        Assert.Equal("accepted", f.Db.MarketingDestinationDeliveries.Single().Status);
        Assert.Empty(f.Service.GetDueDispatchCandidatesForAllBusinesses());
        var activity = Assert.Single(f.Service.GetWorkspaceViewModel(f.Business).Activity);
        Assert.Equal("Shipment update", activity.TriggerLabel);
        Assert.Equal("Accepted", activity.Status);
    }

    [Fact]
    public void NotesAndRepeatedTrackingDoNotDuplicateButCorrectionHasNewIdentity()
    {
        using var f = new Fixture();
        Assert.True(f.Update());
        var first = Assert.Single(f.Service.GetDueDispatchCandidates(f.Business));
        Assert.True(f.Update(notes: "Packed carefully"));
        Assert.Single(f.Db.MarketingDestinationDeliveries);
        Assert.True(f.Update(tracking: "NEW-TRACKING"));
        Assert.Equal(2, f.Db.MarketingDestinationDeliveries.Count());
        Assert.False(f.Service.TryBeginDispatch(first));
        Assert.Contains(f.Db.MarketingDestinationDeliveries, x => x.Status == "superseded");
        var next = Assert.Single(f.Service.GetDueDispatchCandidates(f.Business));
        Assert.NotEqual(first.TriggerKey, next.TriggerKey);
        Assert.Contains("Tracking updated", next.Subject);
        Assert.True(f.Service.TryBeginDispatch(next));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChangedRecipientOrStatusSupersedesQueuedNotice(bool changeRecipient)
    {
        using var f = new Fixture();
        Assert.True(f.Update());
        var candidate = Assert.Single(f.Service.GetDueDispatchCandidates(f.Business));
        if (changeRecipient) f.Order.Email = "changed@example.test";
        else f.Order.FulfillmentStatus = "Packed";
        f.Db.SaveChanges();
        Assert.False(f.Service.TryBeginDispatch(candidate));
        f.Db.ChangeTracker.Clear(); // Verify committed state, not the tracked mutation.
        Assert.Equal("superseded", f.Db.MarketingDestinationDeliveries.Single().Status);
        Assert.Equal(0, f.Db.MarketingDestinationDeliveries.Single().AttemptCount);
    }

    [Fact]
    public void WrongTenantCannotReadOrUpdateOrClaimShipment()
    {
        using var f = new Fixture();
        var other = Guid.NewGuid();
        Assert.False(f.Orders.UpdateOrder(other, f.Request()));
        Assert.Empty(f.Db.MarketingDestinationDeliveries);
        Assert.True(f.Update());
        Assert.Empty(f.Service.GetDueDispatchCandidates(other));
        var candidate = Assert.Single(f.Service.GetDueDispatchCandidates(f.Business));
        var foreign = new ParfaitAutomationDispatchCandidate {
            CommerceBusinessId = other, WorkflowId = candidate.WorkflowId, WorkflowName = candidate.WorkflowName,
            TriggerType = candidate.TriggerType, TriggerKey = candidate.TriggerKey, Shipment = candidate.Shipment,
            ToEmail = candidate.ToEmail, FirstName = candidate.FirstName, Subject = candidate.Subject, HtmlBody = candidate.HtmlBody
        };
        Assert.False(f.Service.TryBeginDispatch(foreign));
        Assert.Equal("pending", f.Db.MarketingDestinationDeliveries.Single().Status);
    }

    [Fact]
    public void UnknownSendIsHeldAndNeverAutomaticallyRetried()
    {
        using var f = new Fixture();
        Assert.True(f.Update());
        var candidate = Assert.Single(f.Service.GetDueDispatchCandidates(f.Business));
        Assert.True(f.Service.TryBeginDispatch(candidate));
        f.Service.MarkDispatchFailed(candidate, "Timeout");
        Assert.Empty(f.Service.GetDueDispatchCandidates(f.Business));
        Assert.False(f.Service.TryBeginDispatch(candidate));
        Assert.Equal("uncertain", f.Db.MarketingDestinationDeliveries.Single().Status);
    }

    [Fact]
    public void ShipmentAndReceiptRollbackTogether()
    {
        using var f = new Fixture();
        f.Failure.Armed = true;
        Assert.Throws<InvalidOperationException>(() => f.Update());
        f.Db.ChangeTracker.Clear();
        Assert.Equal("Unfulfilled", f.Db.CommerceOrders.Single().FulfillmentStatus);
        Assert.Empty(f.Db.MarketingDestinationDeliveries);
    }

    [Theory]
    [InlineData("Packed", null)]
    [InlineData("Shipped", "Sandbox")]
    public void PackingOrSandboxDoesNotQueueShipment(string status, string? environment)
    {
        using var f = new Fixture();
        f.Order.PaymentEnvironment = environment;
        f.Db.SaveChanges();
        Assert.True(f.Update(status: status));
        Assert.Empty(f.Db.MarketingDestinationDeliveries);
    }

    [Fact]
    public void FulfilledNoticeDoesNotClaimCarrierDelivery()
    {
        using var f = new Fixture();
        Assert.True(f.Update(status: "Fulfilled"));
        var candidate = Assert.Single(f.Service.GetDueDispatchCandidates(f.Business));
        Assert.Contains("marked fulfilled", candidate.HtmlBody);
        Assert.DoesNotContain("delivered", candidate.HtmlBody);
    }

    private sealed class FailReceiptSave : SaveChangesInterceptor
    {
        public bool Armed;
        public override int SavedChanges(SaveChangesCompletedEventData data, int result)
        {
            if (Armed) throw new InvalidOperationException("Synthetic failure after SQL write, before transaction commit");
            return result;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=False");
        public readonly Guid Business = Guid.NewGuid();
        public readonly MasterAppDbContext Db;
        public readonly ParfaitOrderService Orders;
        public readonly ParfaitCustomerAutomationService Service;
        public readonly CommerceOrder Order;
        public readonly FailReceiptSave Failure = new();
        public Fixture()
        {
            connection.Open();
            Db = new(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).AddInterceptors(Failure).Options);
            Db.Database.EnsureCreated();
            Db.CommerceBusinesses.Add(new() { Id = Business, Key = "parfait", DisplayName = "Store & Co", IsActive = true, Status = "Active" });
            Db.CommerceBusinessSettings.Add(new() { CommerceBusinessId = Business });
            Order = new() { CommerceBusinessId = Business, OrderNumber = "SHIP-1", PaymentStatus = "Paid", TotalCents = 2000,
                Email = "buyer@example.test", FirstName = "<Buyer>", CreatedUtc = DateTime.UtcNow, PaidUtc = DateTime.UtcNow };
            Db.CommerceOrders.Add(Order);
            Db.SaveChanges();
            var environment = new Mock<IWebHostEnvironment>();
            environment.SetupGet(x => x.ContentRootPath).Returns(Path.GetTempPath());
            environment.SetupGet(x => x.WebRootPath).Returns(Path.GetTempPath());
            var config = new ConfigurationBuilder().Build();
            var paths = new ParfaitStoragePaths(environment.Object, config);
            Orders = new(Db);
            Service = new(paths, config, Orders, new ParfaitProductService(paths, Db), Db);
        }
        public ParfaitOrderAdminUpdateRequest Request(string status = "Shipped", string tracking = "TRACK-1", string? notes = null) => new() {
            OrderNumber = Order.OrderNumber, ExpectedUpdatedTicks = Order.UpdatedUtc?.Ticks ?? 0,
            PaymentStatus = "Paid", FulfillmentStatus = status, TrackingCarrier = "Carrier & Co", TrackingNumber = tracking, AdminNotes = notes
        };
        public bool Update(string status = "Shipped", string tracking = "TRACK-1", string? notes = null) => Orders.UpdateOrder(Business, Request(status, tracking, notes));
        public void Dispose() { Db.Dispose(); connection.Dispose(); }
    }
}
