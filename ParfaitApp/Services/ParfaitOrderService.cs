using System.Text.Json;
using ParfaitApp.Models;

namespace ParfaitApp.Services;

public sealed class ParfaitOrderService
{
    private readonly IWebHostEnvironment _environment;
    private readonly object _lock = new();

    public ParfaitOrderService(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    private string DataPath => Path.Combine(_environment.ContentRootPath, "App_Data", "parfait-orders.json");

    public IReadOnlyList<ParfaitOrderRecord> GetAllOrders()
    {
        EnsureDataFile();

        lock (_lock)
        {
            var json = File.ReadAllText(DataPath);
            return JsonSerializer.Deserialize<List<ParfaitOrderRecord>>(json) ?? [];
        }
    }

    public ParfaitOrderRecord? GetOrder(string orderNumber)
    {
        return GetAllOrders()
            .FirstOrDefault(o => string.Equals(o.OrderNumber, orderNumber, StringComparison.OrdinalIgnoreCase));
    }

    public ParfaitOrderRecord CreatePendingOrder(
        ParfaitCheckoutCustomerRequest customer,
        IReadOnlyList<ParfaitValidatedCartItem> items,
        HttpContext httpContext)
    {
        var now = DateTime.UtcNow;
        var subtotal = items.Sum(i => i.LineTotalCents);
        var shipping = 0;
        var tax = 0;

        var order = new ParfaitOrderRecord
        {
            OrderNumber = GenerateOrderNumber(now),
            CreatedUtc = now,
            Status = "Payment Pending",
            PaymentStatus = "Pending",
            FulfillmentStatus = "Unfulfilled",

            FirstName = Clean(customer.FirstName),
            LastName = Clean(customer.LastName),
            Email = Clean(customer.Email).ToLowerInvariant(),
            Phone = Clean(customer.Phone),

            AddressLine1 = Clean(customer.AddressLine1),
            AddressLine2 = string.IsNullOrWhiteSpace(customer.AddressLine2) ? null : Clean(customer.AddressLine2),
            City = Clean(customer.City),
            State = Clean(customer.State).ToUpperInvariant(),
            PostalCode = Clean(customer.PostalCode),

            Source = "Public Store",
            UserAgent = httpContext.Request.Headers.UserAgent.ToString(),
            RequestIp = httpContext.Connection.RemoteIpAddress?.ToString(),

            Items = items.ToList(),
            SubtotalCents = subtotal,
            ShippingCents = shipping,
            TaxCents = tax,
            TotalCents = subtotal + shipping + tax
        };

        Upsert(order);
        return order;
    }

    public void MarkPaid(string orderNumber, string? squarePaymentId)
    {
        var orders = GetAllOrders().ToList();
        var order = orders.FirstOrDefault(o => string.Equals(o.OrderNumber, orderNumber, StringComparison.OrdinalIgnoreCase));

        if (order is null)
            return;

        order.Status = "Paid";
        order.PaymentStatus = "Paid";
        order.PaidUtc = DateTime.UtcNow;
        order.SquarePaymentId = squarePaymentId;

        SaveAll(orders);
    }

    public void MarkPaymentFailed(string orderNumber, string error)
    {
        var orders = GetAllOrders().ToList();
        var order = orders.FirstOrDefault(o => string.Equals(o.OrderNumber, orderNumber, StringComparison.OrdinalIgnoreCase));

        if (order is null)
            return;

        order.Status = "Payment Failed";
        order.PaymentStatus = "Failed";
        order.SquareError = error;

        SaveAll(orders);
    }

    private void Upsert(ParfaitOrderRecord order)
    {
        var orders = GetAllOrders().ToList();
        var index = orders.FindIndex(o => string.Equals(o.OrderNumber, order.OrderNumber, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
            orders[index] = order;
        else
            orders.Add(order);

        SaveAll(orders);
    }

    private void SaveAll(List<ParfaitOrderRecord> orders)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);

        var ordered = orders
            .OrderByDescending(o => o.CreatedUtc)
            .ToList();

        lock (_lock)
        {
            File.WriteAllText(
                DataPath,
                JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private void EnsureDataFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);

        if (!File.Exists(DataPath))
            File.WriteAllText(DataPath, "[]");
    }

    private static string GenerateOrderNumber(DateTime utc)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        return $"PF-{utc:yyyyMMdd}-{suffix}";
    }

    private static string Clean(string? value)
    {
        return (value ?? "").Trim();
    }
}
