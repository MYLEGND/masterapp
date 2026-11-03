using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Commerce.Payments;
using Infrastructure.Commerce.Payments.Providers;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

public sealed class SquareEmbeddedCheckoutTests
{
    private static readonly MerchantAccount Account = new("Square", "Sandbox", "merchant", "location", new("private-test-token"));
    private static MerchantCheckoutRequest Request(string token = "test-card-token") => new(Guid.NewGuid(), new string('a', 40), 1234, "USD", "PF-TEST", "https://example.test/return", token,
        new("buyer@example.test", "Test", "Buyer", "1 Test Street", null, "Test City", "CA", "90210"));

    [Fact]
    public async Task EmbeddedPaymentUsesPinnedOrderAndDistinctBoundedKeysThenVerifiesCapture()
    {
        using var handler = new Handler(); var gateway = Gateway(handler); var request = Request();
        var result = await gateway.CreateCheckoutAsync(Account, request, default);
        Assert.Equal("Captured", result.State); Assert.Equal("payment", result.PaymentReference);
        Assert.Equal(1, handler.Orders); Assert.Equal(1, handler.Payments);
        var order = handler.OrderBody.GetProperty("order"); var payment = handler.PaymentBody;
        Assert.Equal("location", order.GetProperty("location_id").GetString());
        Assert.Equal(request.AttemptId.ToString("D"), order.GetProperty("metadata").GetProperty("attempt_id").GetString());
        Assert.Equal("PF-TEST", order.GetProperty("reference_id").GetString());
        Assert.Equal("order", payment.GetProperty("order_id").GetString());
        Assert.Equal("location", payment.GetProperty("location_id").GetString());
        Assert.Equal(1234, payment.GetProperty("amount_money").GetProperty("amount").GetInt64());
        Assert.Equal("test-card-token", payment.GetProperty("source_id").GetString());
        Assert.Equal("buyer@example.test", payment.GetProperty("buyer_email_address").GetString());
        Assert.True(payment.GetProperty("autocomplete").GetBoolean());
        Assert.NotEqual(handler.OrderBody.GetProperty("idempotency_key").GetString(), payment.GetProperty("idempotency_key").GetString());
        Assert.InRange(payment.GetProperty("idempotency_key").GetString()!.Length, 1, 45);
        Assert.InRange(handler.OrderBody.GetProperty("idempotency_key").GetString()!.Length, 1, 45);
        Assert.DoesNotContain("test-card-token", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("CASH")][InlineData(" cash ")][InlineData("ExTeRnAl")][InlineData("\tEXTERNAL\r\n")]
    public async Task OfflinePaymentSentinelsNeverReachProvider(string token)
    {
        using var handler = new Handler();
        await Assert.ThrowsAsync<MerchantPaymentException>(() => Gateway(handler).CreateCheckoutAsync(Account, Request(token), default));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("CARD_DECLINED", "PAYMENT_METHOD_ERROR", "Failed")]
    [InlineData("GENERIC_DECLINE", "PAYMENT_METHOD_ERROR", "Failed")]
    [InlineData("INTERNAL_SERVER_ERROR", "API_ERROR", "Unknown")]
    [InlineData("CARD_DECLINED", "API_ERROR", "Unknown")]
    public async Task OnlyExplicitPaymentDeclineIsTerminal(string code, string category, string expected)
    {
        using var handler = new Handler { ErrorCode = code, ErrorCategory = category };
        if (expected == "Failed")
        {
            var result = await Gateway(handler).CreateCheckoutAsync(Account, Request(), default);
            Assert.Equal("Failed", result.State); Assert.Equal("order", result.Reference); Assert.Null(result.PaymentReference);
        }
        else await Assert.ThrowsAsync<MerchantPaymentException>(() => Gateway(handler).CreateCheckoutAsync(Account, Request(), default));
        Assert.Equal(1, handler.Payments);
    }

    [Fact]
    public void BrowserConfigurationContainsOnlyPublicPinnedIdentifiers()
    {
        using var handler = new Handler(); var id = Guid.NewGuid(); var gateway = Gateway(handler);
        var config = gateway.BrowserConfiguration(Account, id);
        Assert.Equal(id, config.ConnectionRevisionId); Assert.Equal("test-app", config.ApplicationId); Assert.Equal("location", config.LocationId);
        Assert.DoesNotContain("private-test-token", JsonSerializer.Serialize(config));
        Assert.Throws<MerchantPaymentException>(() => gateway.BrowserConfiguration(Account with { Credential = new("secret", ApplicationId: "wrong-app") }, id));
    }

    [Theory]
    [InlineData("order", 0)]
    [InlineData("payment", 1)]
    public async Task LostResponseReconcilesSameDurableAttemptWithoutAnotherPayment(string loss, int paymentCalls)
    {
        using var f = new ServiceFixture(); f.Handler.Loss = loss;
        var first = await f.Checkout.BeginAsync(f.Business, f.Order, "https://example.test/return", sourceToken: "ephemeral", expectedConnectionRevision: f.Revision);
        Assert.Equal("Unknown", first.State);
        f.Db.ChangeTracker.Clear();
        var row = await f.Checkout.GetAttemptAsync(f.Business, f.Order);
        Assert.NotNull(row); Assert.DoesNotContain("ephemeral", JsonSerializer.Serialize(row));
        var next = await f.Checkout.BeginAsync(f.Business, f.Order, "https://example.test/return", sourceToken: "another-token", expectedConnectionRevision: f.Revision);
        Assert.Equal(loss == "payment" ? "Captured" : "Pending", next.State);
        Assert.Equal(1, f.Handler.Orders); Assert.Equal(paymentCalls, f.Handler.Payments);
        Assert.Equal(row!.Id, (await f.Checkout.GetAttemptAsync(f.Business, f.Order))!.Id);
    }

    [Theory]
    [InlineData("mixed")][InlineData("malformed")][InlineData("server")]
    public async Task AmbiguousErrorsNeverAuthorizeAnotherCardAttempt(string mode)
    {
        using var f = new ServiceFixture(); f.Handler.ErrorMode = mode;
        var result = await f.Checkout.BeginAsync(f.Business, f.Order, "https://example.test/return", sourceToken: "ephemeral", expectedConnectionRevision: f.Revision);
        Assert.Equal("Unknown", result.State);
        Assert.Equal(1, f.Handler.Payments); Assert.Single(f.Db.CommercePaymentAttempts);
    }

    [Fact]
    public async Task AttemptOnlyRecoveryUsesPreparedSnapshotAndNeverPreparesAnotherOrder()
    {
        using var f = new ServiceFixture();
        f.Db.CommerceBusinesses.Add(new() { Id = f.Business, Key = "parfait", DisplayName = "Parfait", IsActive = true, Status = "Active" });
        var order = await f.Db.CommerceOrders.SingleAsync(); order.CheckoutAttemptId = "opaque-recovery";
        (await f.Db.CommerceMerchantConnections.SingleAsync()).ReadinessJson = JsonSerializer.Serialize(new MerchantReadiness(true, true, new[] { "USD" }, new[] { "card" }, new[] { "location" }));
        await f.Db.SaveChangesAsync();
        var environment = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        environment.SetupGet(x => x.ContentRootPath).Returns(System.IO.Path.GetTempPath());
        environment.SetupGet(x => x.WebRootPath).Returns(System.IO.Path.GetTempPath());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["MerchantPayments:ParfaitCutoverEnabled"] = "true" }).Build();
        var storage = new ParfaitApp.Services.ParfaitStoragePaths(environment.Object, config);
        var products = new ParfaitApp.Services.ParfaitProductService(storage, f.Db);
        var orders = new ParfaitApp.Services.ParfaitOrderService(f.Db);
        var controller = new ParfaitApp.Controllers.StoreCheckoutController(new Infrastructure.Billing.Square.SquareBillingOptions(), products, orders,
            new ParfaitApp.Services.ParfaitCustomerAutomationService(storage, config, orders, products, f.Db), null!, null!,
            new Infrastructure.Commerce.CommerceSignalService(f.Db), null!, f.Checkout, null!, config);
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };
        controller.Request.Scheme = "https"; controller.Request.Host = new Microsoft.AspNetCore.Http.HostString("shopparfait.com");
        controller.Request.Headers.Accept = "application/json";
        var missing = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(await controller.MerchantReturn(null, null, "not-received", default));
        Assert.Equal(503, missing.StatusCode); Assert.Empty(f.Db.CommercePaymentAttempts);
        var historical = new Domain.Entities.SubscriptionPayment { CommerceOrderId = f.Order, Kind = Domain.Billing.SubscriptionPaymentKind.CommerceOneTime, Status = Domain.Billing.SubscriptionPaymentStatus.Completed, AmountCents = 1234, Currency = "USD", ProviderPaymentId = "historical" };
        f.Db.SubscriptionPayments.Add(historical); order.PaymentStatus = "Paid"; order.PaidUtc = DateTime.UtcNow; await f.Db.SaveChangesAsync();
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Pay(new() { CheckoutAttemptId = "opaque-recovery" }, default));
        order.PaymentStatus = "Pending"; order.PaidUtc = null; historical.Status = Domain.Billing.SubscriptionPaymentStatus.Pending; await f.Db.SaveChangesAsync();
        Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(await controller.Pay(new() { CheckoutAttemptId = "opaque-recovery" }, default));
        Assert.Empty(f.Db.CommercePaymentAttempts); Assert.Equal(0, f.Handler.Payments);
        f.Db.SubscriptionPayments.Remove(historical); await f.Db.SaveChangesAsync();
        var prepared = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.MerchantReturn(null, null, "opaque-recovery", default));
        var recovery = JsonSerializer.SerializeToElement(prepared.Value);
        Assert.True(recovery.GetProperty("paymentNotStarted").GetBoolean());
        Assert.Equal(1234, recovery.GetProperty("recovery").GetProperty("totalCents").GetInt32());
        Assert.Equal(0, f.Handler.Orders); Assert.Equal(0, f.Handler.Payments);
        var request = new ParfaitApp.Models.ParfaitCheckoutPayRequest { CheckoutAttemptId = "opaque-recovery", SourceId = "card-token", MerchantConnectionRevision = f.Revision,
            Customer = new() { Email = "changed@example.test" }, Items = [new() { Id = "different-current-cart", Quantity = 100 }] };
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Pay(request, default));
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.MerchantReturn(null, null, "opaque-recovery", default));
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Pay(request, default));
        Assert.Single(f.Db.CommerceOrders); Assert.Single(f.Db.CommercePaymentAttempts);
        Assert.Equal(1, f.Handler.Orders); Assert.Equal(1, f.Handler.Payments);
        Assert.Equal(1234, (await f.Db.CommercePaymentAttempts.SingleAsync()).AmountMinor);
        Assert.Equal("buyer@example.test", (await f.Db.CommerceOrders.SingleAsync()).Email);
    }

    private sealed class ServiceFixture : IDisposable
    {
        public readonly Guid Business = Guid.NewGuid(), Order = Guid.NewGuid(), Revision = Guid.NewGuid();
        public readonly Handler Handler = new();
        public readonly Infrastructure.Data.MasterAppDbContext Db;
        public readonly MerchantCheckoutService Checkout;
        public ServiceFixture()
        {
            var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Infrastructure.Data.MasterAppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            Db = new(options); var id = Guid.NewGuid();
            var protection = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
            var encrypted = protection.CreateProtector("Legend.Merchant.Credential.v1", id.ToString("N"), Business.ToString("N"), "Square", "Sandbox")
                .Protect(JsonSerializer.Serialize(Account.Credential));
            Db.CommerceOrders.Add(new() { Id = Order, CommerceBusinessId = Business, OrderNumber = "PF-TEST", TotalCents = 1234, Email = "buyer@example.test" });
            Db.CommerceMerchantConnections.Add(new() { Id = id, CommerceBusinessId = Business, Provider = "Square", Environment = "Sandbox", AccountId = "merchant", LocationId = "location", Revision = 1, State = "Active", ProtectedCredential = encrypted });
            Db.CommerceMerchantConnectionRevisions.Add(new() { Id = Revision, ConnectionId = id, CommerceBusinessId = Business, Provider = "Square", Environment = "Sandbox", AccountId = "merchant", LocationId = "location", Revision = 1 });
            Db.CommerceMerchantConfigurations.Add(new() { CommerceBusinessId = Business, Environment = "Sandbox", DefaultConnectionId = id }); Db.SaveChanges();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["MerchantPayments:CheckoutEnvironment"] = "Sandbox", ["MerchantPayments:Square:Sandbox:ApplicationId"] = "test-app", ["MerchantPayments:Square:Sandbox:Enabled"] = "true", ["MerchantPayments:Square:Sandbox:ApplicationSecret"] = "test-secret" }).Build();
            var factory = new Mock<IHttpClientFactory>(); factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(Handler, false));
            var gateway = new SquareMerchantPaymentGateway(factory.Object, config);
            Checkout = new(Db, new MerchantConnectionService(Db, protection, new[] { gateway }, config), new[] { gateway }, config);
        }
        public void Dispose() { Db.Dispose(); Handler.Dispose(); }
    }

    private static SquareMerchantPaymentGateway Gateway(Handler handler)
    {
        var factory = new Mock<IHttpClientFactory>(); factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        return new(factory.Object, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MerchantPayments:Square:Sandbox:ApplicationId"] = "test-app" }).Build());
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls, Orders, Payments;
        public string? ErrorCode, ErrorCategory, Loss, ErrorMode;
        public JsonElement OrderBody, PaymentBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Assert.Equal("connect.squareupsandbox.com", request.RequestUri!.Host);
            Assert.Equal("private-test-token", request.Headers.Authorization?.Parameter);
            var path = request.RequestUri.AbsolutePath;
            if (path == "/v2/merchants/merchant") return Reply(new { merchant = new { id = "merchant", status = "ACTIVE" } });
            if (path == "/v2/locations") return Reply(new { locations = new[] { new { id = "location", merchant_id = "merchant", status = "ACTIVE", currency = "USD", capabilities = new[] { "CREDIT_CARD_PROCESSING", "AUTOMATIC_TRANSFERS" } } } });
            if (path == "/v2/orders/search") return Reply(new { orders = new[] { new { id = "order", reference_id = "PF-TEST", metadata = OrderBody.GetProperty("order").GetProperty("metadata") } } });
            if (path == "/v2/online-checkout/payment-links") return Reply(new { payment_links = Array.Empty<object>() });
            if (request.Method == HttpMethod.Post && path == "/v2/orders")
            {
                Orders++; OrderBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                if (Loss == "order") throw new HttpRequestException("synthetic lost response");
            }
            if (request.Method == HttpMethod.Post && path == "/v2/payments")
            {
                Payments++; PaymentBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                if (Loss == "payment") throw new HttpRequestException("synthetic lost response");
                if (ErrorMode == "malformed") return new(HttpStatusCode.BadRequest) { Content = new StringContent("invalid-json") };
                if (ErrorMode == "server") return Reply(new { errors = new[] { new { code = "CARD_DECLINED", category = "PAYMENT_METHOD_ERROR" } } }, HttpStatusCode.InternalServerError);
                if (ErrorMode == "mixed") return Reply(new { errors = new[] { new { code = "CARD_DECLINED", category = "PAYMENT_METHOD_ERROR" }, new { code = "INTERNAL_SERVER_ERROR", category = "API_ERROR" } } }, HttpStatusCode.BadRequest);
                if (ErrorCode is not null) return Reply(new { errors = new[] { new { code = ErrorCode, category = ErrorCategory, detail = "private provider detail" } } }, HttpStatusCode.BadRequest);
            }
            if (path == "/v2/payments/payment") return Reply(new { payment = new { id = "payment", order_id = "order", location_id = "location", status = "COMPLETED", amount_money = new { amount = 1234, currency = "USD" } } });
            if (path == "/v2/payments") return Reply(new { payment = new { id = "payment" } });
            return Reply(new { order = new { id = "order", location_id = "location", total_money = new { amount = 1234, currency = "USD" }, tenders = Payments > 0 ? new[] { new { payment_id = "payment" } } : Array.Empty<object>() } });
        }
        private static HttpResponseMessage Reply(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(body)) };
    }
}
