using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Billing;
using Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ParfaitApp.Controllers;
using ParfaitApp.Models;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ParfaitCheckoutBillingCutoverTests
{
    [Fact]
    public async Task Pay_UsesAuthoritativeQuote_StoresSharedPayment_AndKeepsCommerceOrderCompatible()
    {
        BillingOneTimePaymentRequest? capturedGatewayRequest = null;

        using var harness = new CheckoutHarness(
            configureGateway: gateway =>
            {
                gateway
                    .Setup(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
                    .Callback<BillingOneTimePaymentRequest, CancellationToken>((request, _) => capturedGatewayRequest = request)
                    .ReturnsAsync(new BillingOneTimePaymentResult(
                        true,
                        "pay_123",
                        "COMPLETED",
                        null,
                        "Payment completed.",
                        "req_123",
                        false,
                        2500,
                        "USD",
                        DateTime.UtcNow));
            },
            shippingFeeCents: 500,
            stockQuantity: 3);

        var result = await harness.Controller.Pay(harness.BuildPayRequest(), CancellationToken.None);

        var response = ReadResponse(result, StatusCodes.Status200OK);
        var order = Assert.Single(harness.Db.CommerceOrders);
        var payment = Assert.Single(harness.Db.SubscriptionPayments);
        var inventory = Assert.Single(harness.Db.CommerceProductInventoryItems);
        var loadedOrder = harness.Orders.GetOrder(order.OrderNumber);
        Assert.NotNull(loadedOrder);

        Assert.True(response.Success);
        Assert.Equal(order.OrderNumber, response.OrderNumber);
        Assert.Equal($"/store/success?orderNumber={Uri.EscapeDataString(order.OrderNumber)}", response.RedirectUrl);

        Assert.NotNull(capturedGatewayRequest);
        Assert.Equal(2500, capturedGatewayRequest!.AmountCents);
        Assert.Equal("USD", capturedGatewayRequest.Currency);
        Assert.Equal(order.OrderNumber, capturedGatewayRequest.IdempotencyKey);
        Assert.Equal(order.Id.ToString(), capturedGatewayRequest.OrderReference);

        Assert.Equal(order.Id, payment.CommerceOrderId);
        Assert.Equal(SubscriptionPaymentStatus.Completed, payment.Status);
        Assert.Equal(BillingProvider.Square, payment.Provider);
        Assert.Equal(BillingProviderEnvironment.Sandbox, payment.ProviderEnvironment);
        Assert.Equal("pay_123", payment.ProviderPaymentId);
        Assert.Null(payment.SafeFailureCode);

        Assert.Equal("Paid", order.PaymentStatus);
        Assert.Equal("pay_123", order.SquarePaymentId);
        Assert.False(order.IsPaymentProcessing);
        Assert.NotNull(order.PaidUtc);
        Assert.Equal(2, inventory.StockQuantity);

        Assert.Equal(order.Id, loadedOrder!.CommerceOrderId);
        Assert.Equal("pay_123", loadedOrder.PaymentReferenceId);
        Assert.Null(loadedOrder.PaymentFailureSummary);
        Assert.Equal("Paid", loadedOrder.PaymentStatus);
    }

    [Fact]
    public void BeginCheckoutPayment_WhenDuplicateAttemptIsActive_ReturnsAlreadyProcessingWithoutCreatingAnotherOrder()
    {
        using var harness = new CheckoutHarness(
            configureGateway: _ => { },
            shippingFeeCents: 0,
            stockQuantity: 3);

        var customer = harness.BuildCustomer();
        var items = harness.BuildValidatedItems(quantity: 1);

        var first = harness.Orders.BeginCheckoutPayment(
            "attempt-1",
            customer,
            items,
            2000,
            null,
            null,
            0,
            0,
            0,
            harness.Controller.HttpContext);

        var second = harness.Orders.BeginCheckoutPayment(
            "attempt-1",
            customer,
            items,
            2000,
            null,
            null,
            0,
            0,
            0,
            harness.Controller.HttpContext);

        Assert.Equal(CheckoutPaymentStartState.Ready, first.State);
        Assert.Equal(CheckoutPaymentStartState.AlreadyProcessing, second.State);
        Assert.Equal(first.Order.OrderNumber, second.Order.OrderNumber);
        Assert.Equal(first.Order.CommerceOrderId, second.Order.CommerceOrderId);
        Assert.Single(harness.Db.CommerceOrders);
    }

    [Fact]
    public async Task Pay_WhenGatewayDeclines_ReturnsSafeFailure_AndDoesNotCommitInventory()
    {
        using var harness = new CheckoutHarness(
            configureGateway: gateway =>
            {
                gateway
                    .Setup(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new BillingOneTimePaymentResult(
                        false,
                        null,
                        "FAILED",
                        "CARD_DECLINED",
                        "Card was declined.",
                        "req_declined",
                        false));
            },
            shippingFeeCents: 500,
            stockQuantity: 3);

        var result = await harness.Controller.Pay(harness.BuildPayRequest(), CancellationToken.None);

        var response = ReadResponse(result, StatusCodes.Status400BadRequest);
        var order = Assert.Single(harness.Db.CommerceOrders);
        var payment = Assert.Single(harness.Db.SubscriptionPayments);
        var inventory = Assert.Single(harness.Db.CommerceProductInventoryItems);

        Assert.False(response.Success);
        Assert.Equal("Card was declined.", response.Error);
        Assert.Equal("Failed", order.PaymentStatus);
        Assert.Equal("Card was declined.", order.SquareError);
        Assert.Null(order.PaidUtc);
        Assert.Equal(SubscriptionPaymentStatus.Failed, payment.Status);
        Assert.Equal("CARD_DECLINED", payment.SafeFailureCode);
        Assert.Equal(3, inventory.StockQuantity);
    }

    [Fact]
    public async Task Pay_WhenGatewayThrows_ReturnsGenericFailure_WithoutPersistingRawProviderError()
    {
        using var harness = new CheckoutHarness(
            configureGateway: gateway =>
            {
                gateway
                    .Setup(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidOperationException("raw upstream failure 4111111111111111"));
            },
            shippingFeeCents: 500,
            stockQuantity: 3);

        var result = await harness.Controller.Pay(harness.BuildPayRequest(), CancellationToken.None);

        var response = ReadResponse(result, StatusCodes.Status503ServiceUnavailable);
        var order = Assert.Single(harness.Db.CommerceOrders);
        var payment = Assert.Single(harness.Db.SubscriptionPayments);
        var inventory = Assert.Single(harness.Db.CommerceProductInventoryItems);

        Assert.False(response.Success);
        Assert.Equal("Payment could not be completed right now. Please try again.", response.Error);
        Assert.Equal("Failed", order.PaymentStatus);
        Assert.Equal("One-time payment failed before the provider returned a safe result.", order.SquareError);
        Assert.DoesNotContain("4111111111111111", order.SquareError!, StringComparison.Ordinal);
        Assert.Equal(SubscriptionPaymentStatus.Failed, payment.Status);
        Assert.Equal("UNHANDLED_BILLING_ERROR", payment.SafeFailureCode);
        Assert.Equal(3, inventory.StockQuantity);
    }

    [Fact]
    public async Task Pay_WhenReceiptEmailFails_StillReturnsSuccess_AndKeepsOrderPaid()
    {
        using var harness = new CheckoutHarness(
            configureGateway: gateway =>
            {
                gateway
                    .Setup(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new BillingOneTimePaymentResult(
                        true,
                        "pay_email",
                        "COMPLETED",
                        null,
                        "Payment completed.",
                        "req_email",
                        false));
            },
            configureMail: mail =>
            {
                mail.Setup(x => x.SendOrderReceiptAsync(It.IsAny<ParfaitOrderRecord>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidOperationException("receipt send failed"));
                mail.Setup(x => x.SendOrderNotificationAsync(It.IsAny<ParfaitOrderRecord>(), It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);
            },
            shippingFeeCents: 500,
            stockQuantity: 3);

        var result = await harness.Controller.Pay(harness.BuildPayRequest(), CancellationToken.None);

        var response = ReadResponse(result, StatusCodes.Status200OK);
        var order = Assert.Single(harness.Db.CommerceOrders);
        var inventory = Assert.Single(harness.Db.CommerceProductInventoryItems);

        Assert.True(response.Success);
        Assert.Equal("Paid", order.PaymentStatus);
        Assert.Equal("pay_email", order.SquarePaymentId);
        Assert.Equal(2, inventory.StockQuantity);
        harness.Mail.Verify(x => x.SendOrderReceiptAsync(It.IsAny<ParfaitOrderRecord>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Mail.Verify(x => x.SendOrderNotificationAsync(It.IsAny<ParfaitOrderRecord>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Pay_WhenAnalyticsFails_StillReturnsSuccess_AndKeepsOrderPaid()
    {
        using var harness = new CheckoutHarness(
            configureGateway: gateway =>
            {
                gateway
                    .Setup(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new BillingOneTimePaymentResult(
                        true,
                        "pay_analytics",
                        "COMPLETED",
                        null,
                        "Payment completed.",
                        "req_analytics",
                        false));
            },
            configureAnalytics: analytics =>
            {
                analytics
                    .Setup(x => x.TrackPurchaseAsync(It.IsAny<ParfaitOrderRecord>(), It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidOperationException("analytics unavailable"));
            },
            shippingFeeCents: 500,
            stockQuantity: 3);

        var result = await harness.Controller.Pay(harness.BuildPayRequest(), CancellationToken.None);

        var response = ReadResponse(result, StatusCodes.Status200OK);
        var order = Assert.Single(harness.Db.CommerceOrders);
        var inventory = Assert.Single(harness.Db.CommerceProductInventoryItems);

        Assert.True(response.Success);
        Assert.Equal("Paid", order.PaymentStatus);
        Assert.Equal("pay_analytics", order.SquarePaymentId);
        Assert.Equal(2, inventory.StockQuantity);
        harness.Analytics.Verify(x => x.TrackPurchaseAsync(It.IsAny<ParfaitOrderRecord>(), It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ParfaitCheckoutPayResponse ReadResponse(IActionResult result, int expectedStatusCode)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(expectedStatusCode, objectResult.StatusCode ?? StatusCodes.Status200OK);
        return Assert.IsType<ParfaitCheckoutPayResponse>(objectResult.Value);
    }

    private sealed class CheckoutHarness : IDisposable
    {
        private readonly string _tempRoot;
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;

        public CheckoutHarness(
            Action<Mock<IBillingGateway>> configureGateway,
            int shippingFeeCents,
            int stockQuantity,
            Action<Mock<IGraphMailService>>? configureMail = null,
            Action<Mock<IParfaitAnalyticsService>>? configureAnalytics = null)
        {
            Db = ControllerTestHelpers.BuildDb();
            _tempRoot = Path.Combine(Path.GetTempPath(), "masterapp-parfait-checkout-tests", Guid.NewGuid().ToString("N"));

            Configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Parfait:StorageRoot"] = _tempRoot,
                    ["Square:ApplicationId"] = "sq0idp-test",
                    ["Square:LocationId"] = "L12345",
                    ["Square:Environment"] = "Sandbox",
                    ["Square:AccessToken"] = "test-access-token",
                    ["Contact:WebsiteName"] = "Parfait"
                })
                .Build();

            var environment = new Mock<IWebHostEnvironment>();
            environment.SetupGet(x => x.ContentRootPath).Returns(_tempRoot);
            environment.SetupGet(x => x.WebRootPath).Returns(_tempRoot);

            StoragePaths = new ParfaitStoragePaths(environment.Object, Configuration);
            Products = new ParfaitProductService(StoragePaths, Db);
            Orders = new ParfaitOrderService(Db);
            Automations = new ParfaitCustomerAutomationService(StoragePaths, Configuration, Orders, Products);
            MetaSignalBridge = new ParfaitMetaSignalBridgeService(Db, NullLogger<ParfaitMetaSignalBridgeService>.Instance);

            Gateway = new Mock<IBillingGateway>(MockBehavior.Strict);
            Gateway.SetupGet(x => x.Provider).Returns(BillingProvider.Square);
            Gateway.SetupGet(x => x.Environment).Returns(BillingProviderEnvironment.Sandbox);
            configureGateway(Gateway);

            Mail = new Mock<IGraphMailService>(MockBehavior.Strict);
            Mail.Setup(x => x.SendOrderReceiptAsync(It.IsAny<ParfaitOrderRecord>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Mail.Setup(x => x.SendOrderNotificationAsync(It.IsAny<ParfaitOrderRecord>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            configureMail?.Invoke(Mail);

            Analytics = new Mock<IParfaitAnalyticsService>(MockBehavior.Strict);
            Analytics.Setup(x => x.TrackPurchaseAsync(It.IsAny<ParfaitOrderRecord>(), It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            configureAnalytics?.Invoke(Analytics);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Configuration);
            services.AddSingleton(Db);
            services.AddSingleton<MasterAppDbContext>(Db);
            services.AddMasterAppBilling(Configuration);
            services.AddScoped<IBillingGateway>(_ => Gateway.Object);
            services.AddScoped<IBillingEntitlementService>(_ => Mock.Of<IBillingEntitlementService>());
            _provider = services.BuildServiceProvider();
            _scope = _provider.CreateScope();

            BillingOrchestrator = _scope.ServiceProvider.GetRequiredService<IBillingOrchestrator>();

            Controller = new StoreCheckoutController(
                Configuration,
                Products,
                Orders,
                Automations,
                BillingOrchestrator,
                Mail.Object,
                Analytics.Object,
                MetaSignalBridge)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = BuildHttpContext()
                }
            };

            SeedCommerceCatalog(shippingFeeCents, stockQuantity);
        }

        public MasterAppDbContext Db { get; }
        public IConfiguration Configuration { get; }
        public ParfaitStoragePaths StoragePaths { get; }
        public ParfaitProductService Products { get; }
        public ParfaitOrderService Orders { get; }
        public ParfaitCustomerAutomationService Automations { get; }
        public ParfaitMetaSignalBridgeService MetaSignalBridge { get; }
        public Mock<IBillingGateway> Gateway { get; }
        public Mock<IGraphMailService> Mail { get; }
        public Mock<IParfaitAnalyticsService> Analytics { get; }
        public IBillingOrchestrator BillingOrchestrator { get; }
        public StoreCheckoutController Controller { get; }

        public ParfaitCheckoutPayRequest BuildPayRequest(int quantity = 1)
        {
            return new ParfaitCheckoutPayRequest
            {
                SourceId = "cnon:card-nonce-ok",
                CheckoutAttemptId = "attempt-1",
                Customer = BuildCustomer(),
                Items =
                [
                    new ParfaitCheckoutItemRequest
                    {
                        Id = "tee-1",
                        Size = "M",
                        Quantity = quantity
                    }
                ]
            };
        }

        public ParfaitCheckoutCustomerRequest BuildCustomer()
        {
            return new ParfaitCheckoutCustomerRequest
            {
                FirstName = "Jane",
                LastName = "Buyer",
                Email = "jane@example.com",
                Phone = "5551234567",
                AddressLine1 = "123 Main St",
                City = "Phoenix",
                State = "AZ",
                PostalCode = "85001"
            };
        }

        public IReadOnlyList<ParfaitValidatedCartItem> BuildValidatedItems(int quantity)
        {
            return
            [
                new ParfaitValidatedCartItem
                {
                    Id = "tee-1",
                    Name = "Lift Tee",
                    Slug = "lift-tee",
                    Size = "M",
                    Quantity = quantity,
                    UnitPriceCents = 2000,
                    CompareAtPriceCents = 2500
                }
            ];
        }

        public void Dispose()
        {
            _scope.Dispose();
            _provider.Dispose();
            Db.Dispose();

            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }

        private void SeedCommerceCatalog(int shippingFeeCents, int stockQuantity)
        {
            var business = new CommerceBusiness
            {
                Key = ParfaitBusinessScopeService.ParfaitBusinessKey,
                DisplayName = "Parfait",
                LegalName = "MyLegnd LLC",
                BusinessType = "Apparel / Ecommerce",
                PrimaryDomain = "shopparfait.com",
                Status = "Active",
                IsActive = true,
                OwnerEmail = "parfait@mylegnd.com"
            };

            Db.CommerceBusinesses.Add(business);
            Db.CommerceBusinessSettings.Add(new CommerceBusinessSettings
            {
                CommerceBusiness = business,
                ShippingFeeCents = shippingFeeCents,
                TaxPercent = 0m
            });

            Db.CommerceProducts.Add(new CommerceProduct
            {
                CommerceBusiness = business,
                ExternalProductKey = "tee-1",
                Name = "Lift Tee",
                Slug = "lift-tee",
                Description = "Training shirt",
                PriceLabel = "$20.00",
                Badge = "Parfait",
                PriceCents = 2000,
                CompareAtPriceCents = 2500,
                IsActive = true,
                DisplayOrder = 1,
                InventoryItems =
                [
                    new CommerceProductInventoryItem
                    {
                        ExternalInventoryKey = "tee-1-m",
                        Size = "M",
                        IsEnabled = true,
                        StockQuantity = stockQuantity,
                        LowStockThreshold = 1,
                        DisplayOrder = 1
                    }
                ]
            });

            Db.SaveChanges();
        }

        private static DefaultHttpContext BuildHttpContext()
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = "https";
            httpContext.Request.Host = new HostString("shopparfait.com");
            httpContext.Request.Path = "/store/checkout/pay";
            httpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
            httpContext.Request.Headers.UserAgent = "ParfaitCheckoutTests/1.0";
            return httpContext;
        }
    }
}
