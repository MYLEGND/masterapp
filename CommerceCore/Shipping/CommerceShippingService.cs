using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Commerce.Payments;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Legend.Commerce.Shipping;

public sealed class ShippingSetupInput
{
    [Required] public string Provider { get; set; } = "";
    [StringLength(300)] public string? ApiKey { get; set; }
    public bool TestMode { get; set; } = true;
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [Required, StringLength(150)] public string Street1 { get; set; } = "";
    [StringLength(150)] public string? Street2 { get; set; }
    [Required, StringLength(100)] public string City { get; set; } = "";
    [Required, StringLength(100)] public string State { get; set; } = "";
    [Required, StringLength(20)] public string PostalCode { get; set; } = "";
    [Required, StringLength(2, MinimumLength = 2)] public string Country { get; set; } = "";
    [Required, StringLength(40)] public string Phone { get; set; } = "";
    public long ExpectedRevision { get; set; }
    public ShippingAddress Address => new(Name.Trim(), Street1.Trim(), Street2?.Trim(), City.Trim(), State.Trim(), PostalCode.Trim(), Country.Trim().ToUpperInvariant(), Phone.Trim());
}
public sealed class ShippingPackageInput
{
    [Required] public string OrderNumber { get; set; } = "";
    [Range(typeof(decimal), "0.1", "108")] public decimal Length { get; set; }
    [Range(typeof(decimal), "0.1", "108")] public decimal Width { get; set; }
    [Range(typeof(decimal), "0.1", "108")] public decimal Height { get; set; }
    [Range(typeof(decimal), "0.1", "2400")] public decimal WeightOunces { get; set; }
    [Required, StringLength(2, MinimumLength = 2)] public string DestinationCountry { get; set; } = "";
    public long ExpectedOrderRevision { get; set; }
}
public sealed record ShippingConnection(Guid Id, string Provider, bool TestMode, string ProtectedKey);
public sealed class ShippingConfiguration
{
    public long Revision { get; set; }
    public Guid SelectedConnection { get; set; }
    public ShippingAddress? From { get; set; }
    public List<ShippingConnection> Connections { get; set; } = [];
}
public sealed class ShippingOrderState
{
    public List<ShippingLabel> PreviousLabels { get; set; } = [];
    public Guid ConnectionId { get; set; }
    public string Status { get; set; } = "Quoted";
    public string ShipmentId { get; set; } = "";
    public string TransactionId { get; set; } = "";
    public string Reference { get; set; } = "";
    public DateTime QuotedUtc { get; set; }
    public List<ShippingRate> Rates { get; set; } = [];
    public ShippingRate? SelectedRate { get; set; }
    public string? LabelUrl { get; set; }
    public string? TrackingNumber { get; set; }
}
public sealed record ShippingSetupView(IReadOnlyList<ShippingProviderOption> Providers, string? ConnectedProvider, bool TestMode, ShippingAddress? From, long Revision);
public sealed record ShippingOrderView(ParfaitApp.Models.ParfaitOrderRecord Order, ShippingSetupView Setup, ShippingOrderState? Shipment, bool TestMode, string? Provider);

public sealed class CommerceShippingService(MasterAppDbContext db, IEnumerable<ICommerceShippingProvider> providers, MerchantPaymentProtection protection, ParfaitApp.Services.ParfaitOrderService orders)
{
    private ICommerceShippingProvider Provider(string id) => providers.SingleOrDefault(item => item.Option.Id == id) ?? throw new InvalidOperationException("Select a supported shipping provider.");
    private IDataProtector Protector(Guid business, Guid connection) => protection.Provider.CreateProtector("LEGEND.Commerce.ShippingCredential.v1", business.ToString("N"), connection.ToString("N"));
    private static ShippingConfiguration ParseConfiguration(string? json) => json is null ? new() : JsonSerializer.Deserialize<ShippingConfiguration>(json) ?? throw new InvalidOperationException("Shipping configuration is unreadable.");
    private static ShippingOrderState? ParseState(string? json) => json is null ? null : JsonSerializer.Deserialize<ShippingOrderState>(json) ?? throw new InvalidOperationException("Shipment record is unreadable.");
    private ShippingConfiguration Configuration(Guid business) => ParseConfiguration(db.CommerceBusinessSettings.AsNoTracking().SingleOrDefault(x => x.CommerceBusinessId == business)?.ShippingConfigurationJson);
    public ShippingSetupView Setup(Guid business)
    {
        var config = Configuration(business);
        var current = config.Connections.SingleOrDefault(x => x.Id == config.SelectedConnection);
        return new(providers.Select(x => x.Option).ToArray(), current?.Provider, current?.TestMode ?? true, config.From, config.Revision);
    }
    public ShippingOrderView? Order(Guid business, string number)
    {
        var order = orders.GetOrder(business, number);
        if (order is null) return null;
        var state = db.CommerceOrders.AsNoTracking().Where(x => x.CommerceBusinessId == business && x.OrderNumber == number).Select(x => x.ShippingLabelStateJson).Single();
        var shipment = ParseState(state);
        var config = Configuration(business);
        var connection = config.Connections.SingleOrDefault(x => x.Id == (shipment?.ConnectionId ?? config.SelectedConnection));
        return new(order, Setup(business), shipment, connection?.TestMode ?? true, connection?.Provider);
    }
    public async Task ConnectAsync(Guid business, ShippingSetupInput input, CancellationToken ct)
    {
        Validator.ValidateObject(input, new ValidationContext(input), true);
        var provider = Provider(input.Provider);
        var key = input.ApiKey?.Trim();
        var snapshot = Configuration(business);
        if (snapshot.Revision != input.ExpectedRevision) throw new InvalidOperationException("Shipping settings changed. Reload before saving.");
        var current = snapshot.Connections.SingleOrDefault(x => x.Id == snapshot.SelectedConnection);
        if (string.IsNullOrEmpty(key) && (current is null || current.Provider != provider.Option.Id || current.TestMode != input.TestMode))
            throw new InvalidOperationException("Enter an API key to connect a new provider or change its environment.");
        if (!string.IsNullOrEmpty(key)) await provider.VerifyAsync(key, input.TestMode, ct);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct) : null;
        var settings = await db.CommerceBusinessSettings.SingleOrDefaultAsync(x => x.CommerceBusinessId == business, ct);
        if (settings is null) { settings = new() { CommerceBusinessId = business }; db.CommerceBusinessSettings.Add(settings); }
        if (db.Entry(settings).State != EntityState.Added) await db.Entry(settings).ReloadAsync(ct);
        var config = ParseConfiguration(settings.ShippingConfigurationJson);
        if (config.Revision != input.ExpectedRevision) throw new InvalidOperationException("Shipping settings changed. Reload before saving.");
        if (!string.IsNullOrEmpty(key))
        {
            var id = Guid.NewGuid();
            config.Connections.Add(new(id, provider.Option.Id, input.TestMode, Protector(business, id).Protect(key)));
            config.SelectedConnection = id;
        }
        config.From = input.Address;
        config.Revision = DateTime.UtcNow.Ticks;
        settings.ShippingConfigurationJson = JsonSerializer.Serialize(config);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }
    public async Task QuoteAsync(Guid business, ShippingPackageInput input, CancellationToken ct)
    {
        Validator.ValidateObject(input, new ValidationContext(input), true);
        var order = await GetOrderAsync(business, input.OrderNumber, ct);
        RequirePaid(order);
        if ((order.UpdatedUtc?.Ticks ?? 0) != input.ExpectedOrderRevision) throw new InvalidOperationException("The order changed. Reload before preparing shipping.");
        var previous = ParseState(order.ShippingLabelStateJson);
        if (previous is not null && previous.Status is not ("Quoted" or "TestLabel" or "Rejected")) throw new InvalidOperationException("This order already has a label purchase. Reconcile or reprint that label.");
        var config = Configuration(business);
        var connection = config.Connections.SingleOrDefault(x => x.Id == config.SelectedConnection) ?? throw new InvalidOperationException("Connect shipping in Settings first.");
        if (config.From is null) throw new InvalidOperationException("Save your ship-from address first.");
        var destinationCountry = input.DestinationCountry.Trim().ToUpperInvariant();
        if (config.From.Country != destinationCountry) throw new InvalidOperationException("International shipping requires customs declarations. Use your provider's customs workflow for this shipment.");
        // Each quote is a distinct purchase attempt; an older transaction must never reconcile it.
        var reference = "LEGEND-" + order.Id.ToString("N") + "-" + Guid.NewGuid().ToString("N");
        var quote = await Provider(connection.Provider).QuoteAsync(Protector(business, connection.Id).Unprotect(connection.ProtectedKey), connection.TestMode, config.From,
            new(order.FirstName + " " + order.LastName, order.AddressLine1, order.AddressLine2, order.City, order.State, order.PostalCode, destinationCountry, order.Phone),
            new(input.Length, input.Width, input.Height, input.WeightOunces), reference, ct);
        if (string.IsNullOrWhiteSpace(quote.ShipmentId)) throw new InvalidOperationException("The provider did not return a shipment identity.");
        var history = previous?.PreviousLabels.ToList() ?? [];
        if (previous?.Status is "TestLabel" or "Rejected") history.Add(new(previous.TransactionId, previous.Status, previous.LabelUrl, previous.TrackingNumber, previous.SelectedRate?.Carrier));
        order.ShippingLabelStateJson = JsonSerializer.Serialize(new ShippingOrderState { PreviousLabels = history, ConnectionId = connection.Id, Reference = reference, ShipmentId = quote.ShipmentId, QuotedUtc = DateTime.UtcNow, Rates = quote.Rates.Where(x => x.Amount >= 0 && x.Currency == "USD").OrderBy(x => x.Amount).ToList() });
        await db.SaveChangesAsync(ct);
    }
    public async Task BuyAsync(Guid business, string number, string rateId, bool confirmed, CancellationToken ct)
    {
        if (!confirmed) throw new InvalidOperationException("Confirm the selected postage charge before purchasing.");
        var order = await GetOrderAsync(business, number, ct);
        RequirePaid(order);
        var state = ParseState(order.ShippingLabelStateJson) ?? throw new InvalidOperationException("Get rates first.");
        if (state.Status != "Quoted") throw new InvalidOperationException("A purchase already started. Check its status; do not purchase again.");
        if (state.QuotedUtc < DateTime.UtcNow.AddMinutes(-30)) throw new InvalidOperationException("These rates expired. Get fresh rates before purchasing.");
        var rate = state.Rates.SingleOrDefault(x => x.Id == rateId) ?? throw new InvalidOperationException("Select a quoted rate.");
        var connection = Configuration(business).Connections.Single(x => x.Id == state.ConnectionId);
        state.Status = "PurchasePending";
        state.SelectedRate = rate;
        var originalState = order.ShippingLabelStateJson;
        var claimedState = JsonSerializer.Serialize(state);
        var revision = order.UpdatedUtc;
        var claimed = await db.CommerceOrders.Where(x => x.Id == order.Id && x.CommerceBusinessId == business
            && x.ShippingLabelStateJson == originalState && x.UpdatedUtc == revision
            && x.PaymentStatus == "Paid" && x.RefundedCents < x.TotalCents
            && x.FulfillmentStatus != "Shipped" && x.FulfillmentStatus != "Fulfilled"
            && x.FulfillmentStatus != "Cancelled" && x.FulfillmentStatus != "Returned"
            && !db.CommercePaymentAttempts.Any(attempt => attempt.CommerceOrderId == x.Id
                && (attempt.State != "Captured" || attempt.RefundedMinor > x.RefundedCents
                    || db.CommercePaymentRefunds.Any(refund => refund.AttemptId == attempt.Id && refund.State != "Failed" && refund.State != "Succeeded")
                    || (db.CommercePaymentRefunds.Where(refund => refund.AttemptId == attempt.Id && refund.State == "Succeeded")
                        .Sum(refund => (long?)refund.AmountMinor) ?? 0) > x.RefundedCents)))
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.ShippingLabelStateJson, claimedState), ct);
        if (claimed != 1) throw new InvalidOperationException("The order changed before purchase. Reload and review its payment and shipment status.");
        await db.Entry(order).ReloadAsync(ct);
        RequirePaid(order);
        var label = await Provider(connection.Provider).BuyAsync(Protector(business, connection.Id).Unprotect(connection.ProtectedKey), connection.TestMode, state.ShipmentId, rate, state.Reference, ct);
        await SaveLabelAsync(order, state, label, connection.TestMode, ct);
    }
    public async Task ReconcileAsync(Guid business, string number, CancellationToken ct, string? providerTransactionId = null)
    {
        var order = await GetOrderAsync(business, number, ct);
        var state = ParseState(order.ShippingLabelStateJson) ?? throw new InvalidOperationException("No shipment exists.");
        if (state.Status == "Quoted") throw new InvalidOperationException("No label purchase has started.");
        var connection = Configuration(business).Connections.Single(x => x.Id == state.ConnectionId);
        var label = await Provider(connection.Provider).RetrieveAsync(Protector(business, connection.Id).Unprotect(connection.ProtectedKey), connection.TestMode, state.ShipmentId, string.IsNullOrWhiteSpace(state.TransactionId) ? providerTransactionId?.Trim() ?? "" : state.TransactionId, state.Reference, ct);
        await SaveLabelAsync(order, state, label, connection.TestMode, ct);
    }
    private async Task SaveLabelAsync(CommerceOrder order, ShippingOrderState state, ShippingLabel label, bool test, CancellationToken ct)
    {
        state.TransactionId = label.TransactionId;
        if (label.Status == "Rejected" && state.Status == "PurchasePending") state.Status = "Rejected";
        if (label.Status == "Purchased" && !string.IsNullOrWhiteSpace(label.TrackingNumber)
            && Uri.TryCreate(label.LabelUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo))
        {
            state.Status = test ? "TestLabel" : "Purchased";
            state.LabelUrl = label.LabelUrl;
            state.TrackingNumber = label.TrackingNumber;
            if (!test)
            {
                order.TrackingCarrier = label.Carrier ?? state.SelectedRate?.Carrier;
                order.TrackingNumber = label.TrackingNumber;
                order.UpdatedUtc = DateTime.UtcNow;
            }
        }
        order.ShippingLabelStateJson = JsonSerializer.Serialize(state);
        await db.SaveChangesAsync(ct);
    }
    private async Task<CommerceOrder> GetOrderAsync(Guid business, string number, CancellationToken ct) => await db.CommerceOrders.SingleOrDefaultAsync(x => x.CommerceBusinessId == business && x.OrderNumber == number, ct) ?? throw new InvalidOperationException("Order not found.");
    private static void RequirePaid(CommerceOrder order)
    {
        if (order.PaymentStatus != "Paid" || order.RefundedCents >= order.TotalCents || order.FulfillmentStatus is "Shipped" or "Fulfilled" or "Cancelled" or "Returned") throw new InvalidOperationException("Only paid, unshipped orders can purchase postage.");
    }
}
