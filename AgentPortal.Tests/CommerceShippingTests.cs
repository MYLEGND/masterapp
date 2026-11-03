using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Commerce.Payments;
using Infrastructure.Data;
using Legend.Commerce.Shipping;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceShippingTests
{
    [Fact]
    public async Task AddressOnlySaveRetainsConnectionWithoutVerifyingOrExposingKey()
    {
        using var f = new Fixture(); await f.Connect();
        var before = System.Text.Json.JsonSerializer.Deserialize<ShippingConfiguration>(f.Db.CommerceBusinessSettings.Single().ShippingConfigurationJson!)!;
        var input = f.AddressUpdate(); input.Street1 = "456 New Street";
        await f.Service.ConnectAsync(f.Business, input, default);
        var after = System.Text.Json.JsonSerializer.Deserialize<ShippingConfiguration>(f.Db.CommerceBusinessSettings.Single().ShippingConfigurationJson!)!;
        Assert.Equal(before.SelectedConnection, after.SelectedConnection);
        Assert.Equal(Assert.Single(before.Connections), Assert.Single(after.Connections));
        Assert.Equal("456 New Street", f.Service.Setup(f.Business).From!.Street1);
        Assert.False(f.Service.Setup(f.Business).TestMode);
        Assert.Equal("test", f.Service.Setup(f.Business).ConnectedProvider);
        Assert.DoesNotContain("synthetic-key", System.Text.Json.JsonSerializer.Serialize(f.Service.Setup(f.Business)));
        Assert.DoesNotContain(after.Connections.Single().ProtectedKey, System.Text.Json.JsonSerializer.Serialize(f.Service.Setup(f.Business)));
        f.Provider.Verify(x => x.VerifyAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("test", true)]
    [InlineData("other", false)]
    public async Task ChangingProviderOrEnvironmentWithoutKeyIsRejected(string provider, bool testMode)
    {
        using var f = new Fixture(); await f.Connect();
        var before = f.Db.CommerceBusinessSettings.Single().ShippingConfigurationJson;
        var input = f.AddressUpdate(); input.Provider = provider; input.TestMode = testMode;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConnectAsync(f.Business, input, default));
        Assert.Contains("API key", error.Message);
        Assert.Equal(before, f.Db.CommerceBusinessSettings.Single().ShippingConfigurationJson);
        f.OtherProvider.Verify(x => x.VerifyAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StaleAddressOnlySaveCannotOverwriteLatestAddress()
    {
        using var f = new Fixture(); await f.Connect();
        var stale = f.AddressUpdate();
        var latest = f.AddressUpdate(); latest.City = "New City";
        await f.Service.ConnectAsync(f.Business, latest, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConnectAsync(f.Business, stale, default));
        Assert.Equal("New City", f.Service.Setup(f.Business).From!.City);
    }

    [Fact]
    public async Task PurchasePersistsTrackingAndReprintDoesNotBuyAgain()
    {
        using var f = new Fixture();
        await f.Connect();
        await f.Service.QuoteAsync(f.Business, f.Package(), default);
        await f.Service.BuyAsync(f.Business, "ORDER-1", "rate-one", true, default);
        Assert.Equal("TRACK-1", f.Db.CommerceOrders.Single().TrackingNumber);
        Assert.Equal("Unfulfilled", f.Db.CommerceOrders.Single().FulfillmentStatus);
        Assert.NotNull(f.Service.Order(f.Business, "ORDER-1")!.Shipment!.LabelUrl);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.BuyAsync(f.Business, "ORDER-1", "rate-one", true, default));
        f.Provider.Verify(x => x.BuyAsync(It.IsAny<string>(), false, "shipment-one", It.IsAny<ShippingRate>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task UnknownPurchaseOutcomeCannotBeRepeated()
    {
        using var f = new Fixture(); await f.Connect();
        await f.Service.QuoteAsync(f.Business, f.Package(), default);
        f.Provider.Setup(x => x.BuyAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<ShippingRate>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new System.Net.Http.HttpRequestException());
        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => f.Service.BuyAsync(f.Business, "ORDER-1", "rate-one", true, default));
        Assert.Equal("PurchasePending", f.Service.Order(f.Business, "ORDER-1")!.Shipment!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.BuyAsync(f.Business, "ORDER-1", "rate-one", true, default));
    }
    [Fact]
    public async Task TestLabelsNeverBecomeLiveOrderTracking()
    {
        using var f = new Fixture(); await f.Connect(true);
        await f.Service.QuoteAsync(f.Business, f.Package(), default);
        await f.Service.BuyAsync(f.Business, "ORDER-1", "rate-one", true, default);
        Assert.Null(f.Db.CommerceOrders.Single().TrackingNumber);
        Assert.Equal("TestLabel", f.Service.Order(f.Business, "ORDER-1")!.Shipment!.Status);
    }
    [Fact]
    public async Task ForeignOrderAndUnpaidOrderCannotCreatePostage()
    {
        using var f = new Fixture(); await f.Connect();
        Assert.Null(f.Service.Order(Guid.NewGuid(), "ORDER-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.QuoteAsync(Guid.NewGuid(), f.Package(), default));
        f.Db.CommerceOrders.Single().PaymentStatus = "Pending"; f.Db.SaveChanges();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.QuoteAsync(f.Business, f.Package(), default));
        f.Provider.Verify(x => x.QuoteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<ShippingAddress>(), It.IsAny<ShippingAddress>(), It.IsAny<ShippingParcel>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task UnquotedRateAndMissingConfirmationCannotPurchase()
    {
        using var f = new Fixture(); await f.Connect(); await f.Service.QuoteAsync(f.Business, f.Package(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.BuyAsync(f.Business, "ORDER-1", "rate-one", false, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.BuyAsync(f.Business, "ORDER-1", "foreign-rate", true, default));
        Assert.DoesNotContain("synthetic-key", f.Db.CommerceBusinessSettings.Single().ShippingConfigurationJson!);
    }
    [Fact]
    public async Task QuoteModeStaysPinnedWhenSetupChanges()
    {
        using var f = new Fixture(); await f.Connect(false);
        await f.Service.QuoteAsync(f.Business, f.Package(), default);
        await f.Connect(true);
        var displayed = f.Service.Order(f.Business, "ORDER-1")!;
        Assert.True(displayed.Setup.TestMode);
        Assert.False(displayed.TestMode);
        await f.Service.BuyAsync(f.Business, "ORDER-1", "rate-one", true, default);
        f.Provider.Verify(x => x.BuyAsync(It.IsAny<string>(), false, "shipment-one", It.IsAny<ShippingRate>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task TestLabelCanTransitionToLiveQuoteWithEvidenceRetained()
    {
        using var f = new Fixture(); await f.Connect(true);
        await f.Service.QuoteAsync(f.Business, f.Package(), default);
        await f.Service.BuyAsync(f.Business, "ORDER-1", "rate-one", true, default);
        await f.Connect(false);
        await f.Service.QuoteAsync(f.Business, f.Package(), default);
        var displayed = f.Service.Order(f.Business, "ORDER-1")!;
        Assert.False(displayed.TestMode);
        Assert.Single(displayed.Shipment!.PreviousLabels);
        Assert.Equal("Quoted", displayed.Shipment.Status);
    }

    [Fact]
    public async Task ConfirmedRejectionAllowsFreshQuoteAndPreservesFailureReceipt()
    {
        using var f = new Fixture(); await f.Connect(); await f.Service.QuoteAsync(f.Business, f.Package(), default);
        f.Provider.Setup(x => x.BuyAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<ShippingRate>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ShippingLabel("rejected-transaction", "Rejected", null, null, null));
        await f.Service.BuyAsync(f.Business, "ORDER-1", "rate-one", true, default);
        Assert.Equal("Rejected", f.Service.Order(f.Business, "ORDER-1")!.Shipment!.Status);
        await f.Service.QuoteAsync(f.Business, f.Package(), default);
        Assert.Equal("Rejected", Assert.Single(f.Service.Order(f.Business, "ORDER-1")!.Shipment!.PreviousLabels).Status);
    }

    [Fact]
    public async Task SuccessfulRefundAwaitingOrderReconciliationBlocksPostage()
    {
        using var f = new Fixture(); await f.Connect(); await f.Service.QuoteAsync(f.Business, f.Package(), default);
        var attempt = new CommercePaymentAttempt { CommerceOrderId = f.Db.CommerceOrders.Single().Id, CommerceBusinessId = f.Business, State = "Captured", AmountMinor = 1000 };
        f.Db.CommercePaymentAttempts.Add(attempt);
        f.Db.CommercePaymentRefunds.Add(new() { AttemptId = attempt.Id, State = "Succeeded", AmountMinor = 1000 });
        f.Db.SaveChanges();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.BuyAsync(f.Business, "ORDER-1", "rate-one", true, default));
        f.Provider.Verify(x => x.BuyAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<ShippingRate>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PreviousRejectedTransactionCannotReleaseNewPendingPurchase()
    {
        using var f = new Fixture(); await f.Connect();
        await f.Service.QuoteAsync(f.Business, f.Package(), default);
        var oldReference = f.Service.Order(f.Business, "ORDER-1")!.Shipment!.Reference;
        f.Provider.Setup(x => x.BuyAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<ShippingRate>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShippingLabel("old-rejection", "Rejected", null, null, null));
        await f.Service.BuyAsync(f.Business, "ORDER-1", "rate-one", true, default);
        await f.Service.QuoteAsync(f.Business, f.Package(), default);
        var newReference = f.Service.Order(f.Business, "ORDER-1")!.Shipment!.Reference;
        Assert.NotEqual(oldReference, newReference);
        Assert.StartsWith("LEGEND-" + f.Db.CommerceOrders.Single().Id.ToString("N") + "-", newReference);
        Assert.True(newReference.Length <= 100);
        f.Provider.Setup(x => x.BuyAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<ShippingRate>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("Synthetic lost response"));
        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => f.Service.BuyAsync(f.Business, "ORDER-1", "rate-one", true, default));
        using var http = new StaleTransactionHttp(oldReference);
        var realAdapter = new ShippoShippingProvider(http);
        f.Provider.Setup(x => x.RetrieveAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string token, bool test, string shipment, string transaction, string reference, CancellationToken ct) =>
                realAdapter.RetrieveAsync("shippo_test_fake", true, shipment, transaction, reference, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReconcileAsync(f.Business, "ORDER-1", default, "old-rejection"));
        Assert.Equal("PurchasePending", f.Service.Order(f.Business, "ORDER-1")!.Shipment!.Status);
        Assert.Equal(newReference, f.Service.Order(f.Business, "ORDER-1")!.Shipment!.Reference);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.BuyAsync(f.Business, "ORDER-1", "rate-one", true, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.QuoteAsync(f.Business, f.Package(), default));
    }

    private sealed class StaleTransactionHttp(string reference) : System.Net.Http.HttpMessageHandler, System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) {
                Content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(new {
                    object_id = "old-rejection", status = "ERROR", metadata = reference
                }))
            });
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection sql = new("Data Source=:memory:;Foreign Keys=False");
        private readonly string root = Path.Combine(Path.GetTempPath(), "shipping-test-" + Guid.NewGuid().ToString("N"));
        private readonly MerchantPaymentProtection protection;
        public Guid Business { get; } = Guid.NewGuid();
        public MasterAppDbContext Db { get; }
        public Mock<ICommerceShippingProvider> Provider { get; } = new();
        public Mock<ICommerceShippingProvider> OtherProvider { get; } = new();
        public CommerceShippingService Service { get; }
        public Fixture()
        {
            sql.Open(); Db = new(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(sql).Options); Db.Database.EnsureCreated();
            Db.CommerceBusinesses.Add(new() { Id = Business, Key = "shipping-test", DisplayName = "Test Store" });
            Db.CommerceOrders.Add(new() { CommerceBusinessId = Business, OrderNumber = "ORDER-1", PaymentStatus = "Paid", TotalCents = 1000, Email = "customer@example.test" }); Db.SaveChanges();
            var environment = new Mock<IHostEnvironment>(); environment.SetupGet(x => x.EnvironmentName).Returns("Development"); environment.SetupGet(x => x.ContentRootPath).Returns(Path.Combine(root, "host"));
            protection = MerchantPaymentProtection.CreateShared(new ConfigurationBuilder().Build(), environment.Object);
            Provider.SetupGet(x => x.Option).Returns(new ShippingProviderOption("test", "Test", "Test", "https://example.test"));
            Provider.Setup(x => x.VerifyAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Provider.Setup(x => x.QuoteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<ShippingAddress>(), It.IsAny<ShippingAddress>(), It.IsAny<ShippingParcel>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ShippingQuote("shipment-one", [new("rate-one", "Carrier", "Ground", 5m, "USD", 3)]));
            Provider.Setup(x => x.BuyAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<ShippingRate>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ShippingLabel("transaction-one", "Purchased", "https://example.test/label.pdf", "TRACK-1", "Carrier"));
            OtherProvider.SetupGet(x => x.Option).Returns(new ShippingProviderOption("other", "Other", "Other", "https://example.test"));
            Service = new(Db, [Provider.Object, OtherProvider.Object], protection, new ParfaitOrderService(Db));
        }
        public Task Connect(bool test = false) => Service.ConnectAsync(Business, new ShippingSetupInput { ExpectedRevision = Service.Setup(Business).Revision, Provider = "test", ApiKey = "synthetic-key", TestMode = test, Name = "Test", Street1 = "123 Test Street", City = "Test City", State = "AZ", PostalCode = "85001", Country = "US", Phone = "5550100" }, default);
        public ShippingSetupInput AddressUpdate() => new() { ExpectedRevision = Service.Setup(Business).Revision, Provider = "test", TestMode = false, Name = "Test", Street1 = "123 Test Street", City = "Test City", State = "AZ", PostalCode = "85001", Country = "US", Phone = "5550100" };
        public ShippingPackageInput Package() => new() { OrderNumber = "ORDER-1", Length = 8, Width = 6, Height = 4, WeightOunces = 16, DestinationCountry = "US" };
        public void Dispose() { protection.Dispose(); Db.Dispose(); sql.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
