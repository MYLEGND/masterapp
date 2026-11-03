using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Legend.Commerce.Shipping;

public sealed record ShippingAddress(string Name, string Street1, string? Street2, string City, string State, string PostalCode, string Country, string Phone);
public sealed record ShippingParcel(decimal Length, decimal Width, decimal Height, decimal WeightOunces);
public sealed record ShippingRate(string Id, string Carrier, string Service, decimal Amount, string Currency, int? EstimatedDays);
public sealed record ShippingQuote(string ShipmentId, IReadOnlyList<ShippingRate> Rates);
public sealed record ShippingLabel(string TransactionId, string Status, string? LabelUrl, string? TrackingNumber, string? Carrier);
public sealed record ShippingProviderOption(string Id, string Name, string Description, string AccountUrl);

public interface ICommerceShippingProvider
{
    ShippingProviderOption Option { get; }
    Task VerifyAsync(string token, bool test, CancellationToken ct);
    Task<ShippingQuote> QuoteAsync(string token, bool test, ShippingAddress from, ShippingAddress to, ShippingParcel parcel, string reference, CancellationToken ct);
    Task<ShippingLabel> BuyAsync(string token, bool test, string shipmentId, ShippingRate rate, string reference, CancellationToken ct);
    Task<ShippingLabel> RetrieveAsync(string token, bool test, string shipmentId, string transactionId, string reference, CancellationToken ct);
}

public abstract class ShippingHttpProvider(IHttpClientFactory clients) : ICommerceShippingProvider
{
    public abstract ShippingProviderOption Option { get; }
    protected abstract string Origin { get; }
    protected abstract AuthenticationHeaderValue Authorization(string token);
    protected async Task<JsonDocument> SendAsync(string token, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Origin + path);
        request.Headers.Authorization = Authorization(token);
        if (body is not null) request.Content = JsonContent.Create(body);
        // No automatic retry of a non-idempotent label purchase.
        using var response = await clients.CreateClient("CommerceShipping").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new ShippingProviderException((int)response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    protected static string S(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
    protected static decimal Money(JsonElement value, string name) => decimal.Parse(S(value, name), CultureInfo.InvariantCulture);
    protected static int? Days(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var days) ? days : null;
    protected static string Id(string value) => Uri.EscapeDataString(value);
    public abstract Task VerifyAsync(string token, bool test, CancellationToken ct);
    public abstract Task<ShippingQuote> QuoteAsync(string token, bool test, ShippingAddress from, ShippingAddress to, ShippingParcel parcel, string reference, CancellationToken ct);
    public abstract Task<ShippingLabel> BuyAsync(string token, bool test, string shipmentId, ShippingRate rate, string reference, CancellationToken ct);
    public abstract Task<ShippingLabel> RetrieveAsync(string token, bool test, string shipmentId, string transactionId, string reference, CancellationToken ct);
}

public sealed class ShippingProviderException(int status) : Exception("The shipping provider could not complete this request. Verify the connection or reconcile an existing label before trying again.")
{
    public int StatusCode { get; } = status;
}

public sealed class EasyPostShippingProvider(IHttpClientFactory clients) : ShippingHttpProvider(clients)
{
    public override ShippingProviderOption Option => new("easypost", "EasyPost", "Compare carrier services through your EasyPost account.", "https://www.easypost.com/");
    protected override string Origin => "https://api.easypost.com/v2/";
    protected override AuthenticationHeaderValue Authorization(string token) => new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(token + ":")));
    public override async Task VerifyAsync(string token, bool test, CancellationToken ct)
    {
        using var result = await SendAsync(token, HttpMethod.Post, "parcels", new { parcel = new { length = 1, width = 1, height = 1, weight = 1 } }, ct);
        VerifyMode(result.RootElement, test);
    }
    private static object Address(ShippingAddress address) => new { name = address.Name, street1 = address.Street1, street2 = address.Street2, city = address.City, state = address.State, zip = address.PostalCode, country = address.Country, phone = address.Phone };
    public override async Task<ShippingQuote> QuoteAsync(string token, bool test, ShippingAddress from, ShippingAddress to, ShippingParcel parcel, string reference, CancellationToken ct)
    {
        using var result = await SendAsync(token, HttpMethod.Post, "shipments", new { shipment = new { reference, from_address = Address(from), to_address = Address(to), parcel = new { length = parcel.Length, width = parcel.Width, height = parcel.Height, weight = parcel.WeightOunces }, options = new { label_format = "PNG", label_size = "4x6", print_custom_1 = reference } } }, ct);
        var root = result.RootElement;
        VerifyMode(root, test);
        return new(S(root, "id"), root.GetProperty("rates").EnumerateArray().Select(rate => new ShippingRate(S(rate, "id"), S(rate, "carrier"), S(rate, "service"), Money(rate, "rate"), S(rate, "currency"), Days(rate, "delivery_days"))).ToArray());
    }
    public override async Task<ShippingLabel> BuyAsync(string token, bool test, string shipmentId, ShippingRate rate, string reference, CancellationToken ct)
    {
        using var result = await SendAsync(token, HttpMethod.Post, "shipments/" + Id(shipmentId) + "/buy", new { rate = new { id = rate.Id } }, ct);
        return Label(result.RootElement, test);
    }
    public override async Task<ShippingLabel> RetrieveAsync(string token, bool test, string shipmentId, string transactionId, string reference, CancellationToken ct)
    {
        using var result = await SendAsync(token, HttpMethod.Get, "shipments/" + Id(shipmentId), null, ct);
        return Label(result.RootElement, test);
    }
    private static void VerifyMode(JsonElement root, bool test)
    {
        if (S(root, "mode") != (test ? "test" : "production")) throw new InvalidOperationException("The shipping key does not match the selected test/live environment.");
    }
    private static ShippingLabel Label(JsonElement root, bool test)
    {
        VerifyMode(root, test);
        var label = root.TryGetProperty("postage_label", out var value) && value.ValueKind == JsonValueKind.Object ? S(value, "label_url") : "";
        var carrier = root.TryGetProperty("selected_rate", out var rate) && rate.ValueKind == JsonValueKind.Object ? S(rate, "carrier") : "";
        return new(S(root, "id"), string.IsNullOrWhiteSpace(label) ? "Pending" : "Purchased", label, S(root, "tracking_code"), carrier);
    }
}

public sealed class ShippoShippingProvider(IHttpClientFactory clients) : ShippingHttpProvider(clients)
{
    public override ShippingProviderOption Option => new("shippo", "Shippo", "Compare carrier services through your Shippo account.", "https://goshippo.com/");
    protected override string Origin => "https://api.goshippo.com/";
    protected override AuthenticationHeaderValue Authorization(string token) => new("ShippoToken", token);
    private static void VerifyMode(string token, bool test)
    {
        if (!token.StartsWith(test ? "shippo_test_" : "shippo_live_", StringComparison.Ordinal)) throw new InvalidOperationException("Use the shipping key for the selected test/live environment.");
    }
    public override async Task VerifyAsync(string token, bool test, CancellationToken ct)
    {
        VerifyMode(token, test);
        using var result = await SendAsync(token, HttpMethod.Get, "carrier_accounts/?results=1", null, ct);
    }
    private static object Address(ShippingAddress address) => new { name = address.Name, street1 = address.Street1, street2 = address.Street2, city = address.City, state = address.State, zip = address.PostalCode, country = address.Country, phone = address.Phone };
    public override async Task<ShippingQuote> QuoteAsync(string token, bool test, ShippingAddress from, ShippingAddress to, ShippingParcel parcel, string reference, CancellationToken ct)
    {
        VerifyMode(token, test);
        using var result = await SendAsync(token, HttpMethod.Post, "shipments/", new { address_from = Address(from), address_to = Address(to), parcels = new[] { new { length = parcel.Length.ToString(CultureInfo.InvariantCulture), width = parcel.Width.ToString(CultureInfo.InvariantCulture), height = parcel.Height.ToString(CultureInfo.InvariantCulture), distance_unit = "in", weight = parcel.WeightOunces.ToString(CultureInfo.InvariantCulture), mass_unit = "oz" } }, metadata = reference, @async = false }, ct);
        var root = result.RootElement;
        return new(S(root, "object_id"), root.GetProperty("rates").EnumerateArray().Select(rate => new ShippingRate(S(rate, "object_id"), S(rate, "provider"), S(rate.GetProperty("servicelevel"), "name"), Money(rate, "amount"), S(rate, "currency"), Days(rate, "estimated_days"))).ToArray());
    }
    public override async Task<ShippingLabel> BuyAsync(string token, bool test, string shipmentId, ShippingRate rate, string reference, CancellationToken ct)
    {
        VerifyMode(token, test);
        using var result = await SendAsync(token, HttpMethod.Post, "transactions/", new { rate = rate.Id, metadata = reference, label_file_type = "PDF_4x6", @async = false }, ct);
        return Label(result.RootElement, rate.Carrier);
    }
    public override async Task<ShippingLabel> RetrieveAsync(string token, bool test, string shipmentId, string transactionId, string reference, CancellationToken ct)
    {
        VerifyMode(token, test);
        if (string.IsNullOrWhiteSpace(transactionId)) throw new InvalidOperationException("The purchase response was not received. Check this order reference in Shippo before any new purchase; do not buy another label.");
        using var result = await SendAsync(token, HttpMethod.Get, "transactions/" + Id(transactionId) + "/", null, ct);
        if (S(result.RootElement, "metadata") != reference) throw new InvalidOperationException("That provider transaction does not belong to this order.");
        return Label(result.RootElement, null);
    }
    private static ShippingLabel Label(JsonElement root, string? carrier) => new(S(root, "object_id"), S(root, "status") == "SUCCESS" ? "Purchased" : S(root, "status") == "ERROR" ? "Rejected" : "Pending", S(root, "label_url"), S(root, "tracking_number"), carrier);
}
