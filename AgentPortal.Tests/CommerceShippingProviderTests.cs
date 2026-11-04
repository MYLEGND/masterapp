using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Legend.Commerce.Shipping;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceShippingProviderTests
{
    private static readonly ShippingAddress From = new("Sender", "1 Test St", "Suite 2", "Phoenix", "AZ", "85001", "US", "5550100");
    private static readonly ShippingAddress To = new("Buyer", "2 Test St", null, "Austin", "TX", "78701", "US", "5550200");
    private static readonly ShippingParcel Parcel = new(10.5m, 8m, 2m, 12.25m);
    private static readonly ShippingRate Rate = new("rate_1", "USPS", "Ground", 4.25m, "USD", 3);

    [Fact]
    public async Task ConnectionVerificationDoesNotPurchasePostage()
    {
        using var easyHttp = new FakeHttp("""{"mode":"test","id":"parcel_1"}""");
        await new EasyPostShippingProvider(easyHttp).VerifyAsync("test-key", true, default);
        var easyRequest = Assert.Single(easyHttp.Requests);
        Assert.Equal("https://api.easypost.com/v2/parcels", easyRequest.Url);
        Assert.Equal("POST", easyRequest.Method);
        using var parcel = JsonDocument.Parse(easyRequest.Body!);
        Assert.Equal(1, parcel.RootElement.GetProperty("parcel").GetProperty("weight").GetInt32());
        using var shippoHttp = new FakeHttp("""{"results":[]}""");
        await new ShippoShippingProvider(shippoHttp).VerifyAsync("shippo_live_fake", false, default);
        var shippoRequest = Assert.Single(shippoHttp.Requests);
        Assert.Equal("https://api.goshippo.com/carrier_accounts/?results=1", shippoRequest.Url);
        Assert.Equal("GET", shippoRequest.Method);
        Assert.Equal("ShippoToken shippo_live_fake", shippoRequest.Authorization);
        Assert.Null(shippoRequest.Body);
    }

    [Fact]
    public async Task EasyPostQuoteUsesBasicAuthInchOunceParcelAndThermalReference()
    {
        using var http = new FakeHttp("""{"id":"shp_1","mode":"test","rates":[{"id":"rate_1","carrier":"USPS","service":"Ground","rate":"4.25","currency":"USD","delivery_days":3}]}""");
        var quote = await new EasyPostShippingProvider(http).QuoteAsync("test-key", true, From, To, Parcel, "order-ref", default);
        var request = Assert.Single(http.Requests);
        Assert.Equal("CommerceShipping", http.ClientName);
        Assert.Equal("POST", request.Method);
        Assert.Equal("https://api.easypost.com/v2/shipments", request.Url);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("test-key:")), request.Authorization);
        using var body = JsonDocument.Parse(request.Body!);
        var shipment = body.RootElement.GetProperty("shipment");
        Assert.Equal("order-ref", shipment.GetProperty("reference").GetString());
        Assert.Equal("85001", shipment.GetProperty("from_address").GetProperty("zip").GetString());
        Assert.Equal("Buyer", shipment.GetProperty("to_address").GetProperty("name").GetString());
        Assert.Equal(10.5m, shipment.GetProperty("parcel").GetProperty("length").GetDecimal());
        Assert.Equal(12.25m, shipment.GetProperty("parcel").GetProperty("weight").GetDecimal());
        Assert.Equal("4x6", shipment.GetProperty("options").GetProperty("label_size").GetString());
        Assert.Equal("PNG", shipment.GetProperty("options").GetProperty("label_format").GetString());
        Assert.Equal("order-ref", shipment.GetProperty("options").GetProperty("print_custom_1").GetString());
        Assert.Equal("shp_1", quote.ShipmentId);
        Assert.Equal(Rate, Assert.Single(quote.Rates));
    }

    [Fact]
    public async Task EasyPostBuyAndRetrieveUseShipmentAndNeverRepeatPurchase()
    {
        const string response = """{"id":"shp_1","mode":"test","postage_label":{"label_url":"https://labels.example.test/label.png"},"selected_rate":{"carrier":"USPS"},"tracking_code":"tracking_1"}""";
        using var http = new FakeHttp(response);
        var provider = new EasyPostShippingProvider(http);
        var bought = await provider.BuyAsync("test-key", true, "shp_1", Rate, "order-ref", default);
        var retrieved = await provider.RetrieveAsync("test-key", true, "shp_1", "shp_1", "order-ref", default);
        Assert.Equal(bought, retrieved);
        Assert.Equal("Purchased", bought.Status);
        Assert.Equal("tracking_1", bought.TrackingNumber);
        Assert.Equal("USPS", bought.Carrier);
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal("POST", http.Requests[0].Method);
        Assert.EndsWith("/shipments/shp_1/buy", http.Requests[0].Url);
        using var body = JsonDocument.Parse(http.Requests[0].Body!);
        Assert.Equal("rate_1", body.RootElement.GetProperty("rate").GetProperty("id").GetString());
        Assert.Equal("GET", http.Requests[1].Method);
        Assert.EndsWith("/shipments/shp_1", http.Requests[1].Url);
        Assert.Null(http.Requests[1].Body);
    }

    [Theory]
    [InlineData("production")]
    [InlineData("")]
    public async Task EasyPostMismatchedOrMissingModeCannotReturnUsableLabel(string mode)
    {
        using var http = new FakeHttp(JsonSerializer.Serialize(new { id = "shp_1", mode, postage_label = new { label_url = "https://labels.example.test/label.png" } }));
        var provider = new EasyPostShippingProvider(http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.VerifyAsync("test-key", true, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.BuyAsync("test-key", true, "shp_1", Rate, "ref", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RetrieveAsync("test-key", true, "shp_1", "", "ref", default));
    }

    [Fact]
    public async Task EasyPostShipmentWithoutLabelRemainsPending()
    {
        using var http = new FakeHttp("""{"id":"shp_1","mode":"production","postage_label":null}""");
        var label = await new EasyPostShippingProvider(http).RetrieveAsync("live-key", false, "shp_1", "", "ref", default);
        Assert.Equal("Pending", label.Status);
        Assert.Equal("", label.LabelUrl);
    }

    [Fact]
    public async Task ShippoQuoteUsesTokenAuthInvariantParcelUnitsAndReference()
    {
        using var http = new FakeHttp("""{"object_id":"shipment_1","rates":[{"object_id":"rate_1","provider":"USPS","servicelevel":{"name":"Ground"},"amount":"4.25","currency":"USD","estimated_days":3}]}""");
        var quote = await new ShippoShippingProvider(http).QuoteAsync("shippo_test_fake", true, From, To, Parcel, "order-ref", default);
        var request = Assert.Single(http.Requests);
        Assert.Equal("ShippoToken shippo_test_fake", request.Authorization);
        Assert.Equal("https://api.goshippo.com/shipments/", request.Url);
        Assert.Equal("POST", request.Method);
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.False(root.GetProperty("async").GetBoolean());
        Assert.Equal("order-ref", root.GetProperty("metadata").GetString());
        Assert.Equal("Sender", root.GetProperty("address_from").GetProperty("name").GetString());
        var parcel = root.GetProperty("parcels")[0];
        Assert.Equal("10.5", parcel.GetProperty("length").GetString());
        Assert.Equal("12.25", parcel.GetProperty("weight").GetString());
        Assert.Equal("in", parcel.GetProperty("distance_unit").GetString());
        Assert.Equal("oz", parcel.GetProperty("mass_unit").GetString());
        Assert.Equal("shipment_1", quote.ShipmentId);
        Assert.Equal(Rate, Assert.Single(quote.Rates));
    }

    [Theory]
    [InlineData("SUCCESS", "Purchased")]
    [InlineData("ERROR", "Rejected")]
    [InlineData("WAITING", "Pending")]
    [InlineData("QUEUED", "Pending")]
    public async Task ShippoPurchaseMapsProviderStatesAndRequestsThermalLabel(string status, string expected)
    {
        using var http = new FakeHttp(JsonSerializer.Serialize(new { object_id = "txn_1", status, label_url = "https://labels.example.test/label.pdf", tracking_number = "track_1" }));
        var result = await new ShippoShippingProvider(http).BuyAsync("shippo_test_fake", true, "shipment_1", Rate, "order-ref", default);
        Assert.Equal(expected, result.Status);
        Assert.Equal("txn_1", result.TransactionId);
        Assert.Equal("USPS", result.Carrier);
        var request = Assert.Single(http.Requests);
        Assert.Equal("https://api.goshippo.com/transactions/", request.Url);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("rate_1", body.RootElement.GetProperty("rate").GetString());
        Assert.Equal("order-ref", body.RootElement.GetProperty("metadata").GetString());
        Assert.Equal("PDF_4x6", body.RootElement.GetProperty("label_file_type").GetString());
        Assert.False(body.RootElement.GetProperty("async").GetBoolean());
    }

    [Theory]
    [InlineData("shippo_live_fake", true)]
    [InlineData("shippo_test_fake", false)]
    [InlineData("invalid", true)]
    public async Task ShippoEnvironmentMismatchSendsNoRequest(string token, bool test)
    {
        using var http = new FakeHttp("{}");
        var provider = new ShippoShippingProvider(http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.VerifyAsync(token, test, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.QuoteAsync(token, test, From, To, Parcel, "ref", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.BuyAsync(token, test, "shipment_1", Rate, "ref", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RetrieveAsync(token, test, "shipment_1", "txn_1", "ref", default));
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("order-ref", true)]
    [InlineData("another-order", false)]
    [InlineData("", false)]
    public async Task ShippoRetrievalRequiresExactOrderMetadata(string metadata, bool accepted)
    {
        using var http = new FakeHttp(JsonSerializer.Serialize(new { object_id = "txn_1", status = "SUCCESS", metadata, label_url = "https://labels.example.test/label.pdf", tracking_number = "track_1" }));
        var provider = new ShippoShippingProvider(http);
        if (accepted)
            Assert.Equal("Purchased", (await provider.RetrieveAsync("shippo_test_fake", true, "shipment_1", "txn_1", "order-ref", default)).Status);
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RetrieveAsync("shippo_test_fake", true, "shipment_1", "txn_1", "order-ref", default));
        var request = Assert.Single(http.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("https://api.goshippo.com/transactions/txn_1/", request.Url);
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task ShippoMissingTransactionCannotSearchOrRepurchase()
    {
        using var http = new FakeHttp("{}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ShippoShippingProvider(http).RetrieveAsync("shippo_test_fake", true, "shipment_1", "", "ref", default));
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData(true, 429)]
    [InlineData(true, 500)]
    [InlineData(false, 401)]
    [InlineData(false, 503)]
    public async Task ProviderHttpFailureIsSanitizedAndPurchaseIsNeverRetried(bool easyPost, int status)
    {
        const string secret = "shippo_test_private-key";
        using var http = new FakeHttp("provider diagnostic contains " + secret, (HttpStatusCode)status);
        ICommerceShippingProvider provider = easyPost ? new EasyPostShippingProvider(http) : new ShippoShippingProvider(http);
        var error = await Assert.ThrowsAsync<ShippingProviderException>(() => provider.BuyAsync(secret, true, "shipment_1", Rate, "ref", default));
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain(secret, error.ToString());
        Assert.DoesNotContain("provider diagnostic", error.ToString());
        Assert.Single(http.Requests);
    }

    private sealed record Request(string Method, string Url, string? Authorization, string? Body);
    private sealed class FakeHttp(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler, IHttpClientFactory
    {
        public List<Request> Requests { get; } = [];
        public string? ClientName { get; private set; }
        public HttpClient CreateClient(string name) { ClientName = name; return new HttpClient(this, disposeHandler: false); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
