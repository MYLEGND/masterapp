using System.Text;
using System.Text.Json;
using ParfaitApp.Models;

namespace ParfaitApp.Services;

public enum CheckoutPaymentStartState
{
    Ready,
    AlreadyProcessing,
    AlreadyPaid
}

public sealed record CheckoutPaymentStartResult(CheckoutPaymentStartState State, ParfaitOrderRecord Order);

public sealed class ParfaitOrderService
{
    private static readonly HashSet<string> PaidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Paid",
        "Refunded"
    };
    private static readonly TimeSpan PaymentProcessingTimeout = TimeSpan.FromMinutes(10);

    private readonly IWebHostEnvironment _environment;
    private readonly object _lock = new();

    public ParfaitOrderService(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    private string DataPath => Path.Combine(_environment.ContentRootPath, "App_Data", "parfait-orders.json");

    public IReadOnlyList<ParfaitOrderRecord> GetAllOrders()
    {
        lock (_lock)
        {
            return ReadAllNormalizedUnsafe();
        }
    }

    public ParfaitOrderRecord? GetOrder(string orderNumber)
    {
        return GetAllOrders()
            .FirstOrDefault(order => string.Equals(order.OrderNumber, orderNumber, StringComparison.OrdinalIgnoreCase));
    }

    public ParfaitOrderRecord CreatePendingOrder(
        ParfaitCheckoutCustomerRequest customer,
        IReadOnlyList<ParfaitValidatedCartItem> items,
        int subtotalCents,
        string? discountCode,
        string? discountLabel,
        int discountCents,
        int shippingCents,
        int taxCents,
        HttpContext httpContext)
    {
        lock (_lock)
        {
            var orders = ReadAllNormalizedUnsafe();
            var order = CreatePendingOrderRecord(
                GenerateOrderNumber(DateTime.UtcNow),
                null,
                customer,
                items,
                subtotalCents,
                discountCode,
                discountLabel,
                discountCents,
                shippingCents,
                taxCents,
                httpContext,
                DateTime.UtcNow);

            UpsertUnsafe(orders, order);
            return order;
        }
    }

    public CheckoutPaymentStartResult BeginCheckoutPayment(
        string checkoutAttemptId,
        ParfaitCheckoutCustomerRequest customer,
        IReadOnlyList<ParfaitValidatedCartItem> items,
        int subtotalCents,
        string? discountCode,
        string? discountLabel,
        int discountCents,
        int shippingCents,
        int taxCents,
        HttpContext httpContext)
    {
        var normalizedAttemptId = Clean(checkoutAttemptId);
        if (string.IsNullOrWhiteSpace(normalizedAttemptId))
        {
            throw new ArgumentException("Checkout attempt ID is required.", nameof(checkoutAttemptId));
        }

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var orders = ReadAllNormalizedUnsafe();
            var order = orders.FirstOrDefault(existing =>
                string.Equals(existing.CheckoutAttemptId, normalizedAttemptId, StringComparison.OrdinalIgnoreCase));

            if (order is not null && order.IsPaid)
            {
                return new CheckoutPaymentStartResult(CheckoutPaymentStartState.AlreadyPaid, order);
            }

            if (order is not null && IsPaymentProcessingActive(order, now))
            {
                return new CheckoutPaymentStartResult(CheckoutPaymentStartState.AlreadyProcessing, order);
            }

            if (order is null)
            {
                order = CreatePendingOrderRecord(
                    GenerateOrderNumber(now),
                    normalizedAttemptId,
                    customer,
                    items,
                    subtotalCents,
                    discountCode,
                    discountLabel,
                    discountCents,
                    shippingCents,
                    taxCents,
                    httpContext,
                    now);
                orders.Add(order);
            }
            else
            {
                ApplyCheckoutSnapshot(
                    order,
                    normalizedAttemptId,
                    customer,
                    items,
                    subtotalCents,
                    discountCode,
                    discountLabel,
                    discountCents,
                    shippingCents,
                    taxCents,
                    httpContext,
                    now);
            }

            order.IsPaymentProcessing = true;
            order.PaymentProcessingStartedUtc = now;
            order.SquareError = null;
            StampOrder(order);

            SaveAllUnsafe(orders);
            return new CheckoutPaymentStartResult(CheckoutPaymentStartState.Ready, order);
        }
    }

    public void MarkPaid(string orderNumber, string? squarePaymentId)
    {
        lock (_lock)
        {
            var orders = ReadAllNormalizedUnsafe();
            var order = orders.FirstOrDefault(existing => string.Equals(existing.OrderNumber, orderNumber, StringComparison.OrdinalIgnoreCase));

            if (order is null)
            {
                return;
            }

            order.PaymentStatus = "Paid";
            order.PaidUtc = DateTime.UtcNow;
            order.SquarePaymentId = squarePaymentId;
            order.IsPaymentProcessing = false;
            order.PaymentProcessingStartedUtc = null;
            order.SquareError = null;
            StampOrder(order);

            SaveAllUnsafe(orders);
        }
    }

    public void MarkPaymentFailed(string orderNumber, string error)
    {
        lock (_lock)
        {
            var orders = ReadAllNormalizedUnsafe();
            var order = orders.FirstOrDefault(existing => string.Equals(existing.OrderNumber, orderNumber, StringComparison.OrdinalIgnoreCase));

            if (order is null)
            {
                return;
            }

            order.PaymentStatus = "Failed";
            order.IsPaymentProcessing = false;
            order.PaymentProcessingStartedUtc = null;
            order.SquareError = error;
            StampOrder(order);

            SaveAllUnsafe(orders);
        }
    }

    public bool UpdateOrder(ParfaitOrderAdminUpdateRequest request)
    {
        lock (_lock)
        {
            var orders = ReadAllNormalizedUnsafe();
            var order = orders.FirstOrDefault(existing => string.Equals(existing.OrderNumber, request.OrderNumber, StringComparison.OrdinalIgnoreCase));

            if (order is null)
            {
                return false;
            }

            var now = DateTime.UtcNow;

            order.PaymentStatus = NormalizePaymentStatus(request.PaymentStatus);
            order.FulfillmentStatus = NormalizeFulfillmentStatus(request.FulfillmentStatus);
            order.ReturnStatus = NormalizeReturnStatus(request.ReturnStatus);
            order.TrackingCarrier = NullIfEmpty(request.TrackingCarrier);
            order.TrackingNumber = NullIfEmpty(request.TrackingNumber);
            order.AdminNotes = NullIfEmpty(request.AdminNotes);
            order.RefundedCents = Math.Clamp(request.RefundedCents, 0, order.TotalCents);

            if (string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase) && order.PaidUtc is null)
            {
                order.PaidUtc = now;
            }

            if (!string.Equals(order.PaymentStatus, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                order.IsPaymentProcessing = false;
                order.PaymentProcessingStartedUtc = null;
            }

            if (string.Equals(order.FulfillmentStatus, "Shipped", StringComparison.OrdinalIgnoreCase))
            {
                order.ShippedUtc ??= now;
            }

            if (string.Equals(order.FulfillmentStatus, "Fulfilled", StringComparison.OrdinalIgnoreCase))
            {
                order.FulfilledUtc ??= now;
                order.ShippedUtc ??= now;
            }

            if (string.Equals(order.PaymentStatus, "Refunded", StringComparison.OrdinalIgnoreCase) && order.RefundedCents == 0)
            {
                order.RefundedCents = order.TotalCents;
            }

            StampOrder(order);
            SaveAllUnsafe(orders);
            return true;
        }
    }

    public int CountOpenFulfillment(IEnumerable<ParfaitOrderRecord> orders)
    {
        return orders.Count(order => order.IsFulfillmentOpen);
    }

    public int CountReturnQueue(IEnumerable<ParfaitOrderRecord> orders)
    {
        return orders.Count(order => order.HasReturnWork);
    }

    public int CountRefunded(IEnumerable<ParfaitOrderRecord> orders)
    {
        return orders.Count(order => order.IsRefundedPayment);
    }

    public int SumNetRevenueCents(IEnumerable<ParfaitOrderRecord> orders)
    {
        return orders
            .Where(order => PaidStatuses.Contains(order.PaymentStatus))
            .Sum(order => order.NetRevenueCents);
    }

    public int CalculateAverageNetOrderValueCents(IEnumerable<ParfaitOrderRecord> orders)
    {
        var paidOrders = orders
            .Where(order => PaidStatuses.Contains(order.PaymentStatus))
            .ToList();

        return paidOrders.Count == 0
            ? 0
            : (int)Math.Round(paidOrders.Average(order => order.NetRevenueCents));
    }

    private void Upsert(ParfaitOrderRecord order)
    {
        lock (_lock)
        {
            var orders = ReadAllNormalizedUnsafe();
            UpsertUnsafe(orders, order);
        }
    }

    private void SaveAll(List<ParfaitOrderRecord> orders)
    {
        lock (_lock)
        {
            SaveAllUnsafe(orders);
        }
    }

    private List<ParfaitOrderRecord> ReadAllNormalizedUnsafe()
    {
        EnsureDataFile();

        var json = File.ReadAllText(DataPath);
        var orders = JsonSerializer.Deserialize<List<ParfaitOrderRecord>>(json) ?? [];
        var requiresRewrite = false;
        var normalized = orders
            .Select(order => NormalizeOrder(order, ref requiresRewrite))
            .OrderByDescending(order => order.CreatedUtc)
            .ToList();

        if (requiresRewrite)
        {
            SaveAllUnsafe(normalized);
        }

        return normalized;
    }

    private void UpsertUnsafe(List<ParfaitOrderRecord> orders, ParfaitOrderRecord order)
    {
        var index = orders.FindIndex(existing => string.Equals(existing.OrderNumber, order.OrderNumber, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            orders[index] = NormalizeOrder(order);
        }
        else
        {
            orders.Add(NormalizeOrder(order));
        }

        SaveAllUnsafe(orders);
    }

    private void SaveAllUnsafe(List<ParfaitOrderRecord> orders)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);

        var ordered = orders
            .Select(NormalizeOrder)
            .OrderByDescending(order => order.CreatedUtc)
            .ToList();

        File.WriteAllText(
            DataPath,
            JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void EnsureDataFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);

        if (!File.Exists(DataPath))
        {
            File.WriteAllText(DataPath, "[]");
        }
    }

    private static void StampOrder(ParfaitOrderRecord order)
    {
        order.UpdatedUtc = DateTime.UtcNow;
        order.Status = BuildStatus(order);
    }

    private static ParfaitOrderRecord CreatePendingOrderRecord(
        string orderNumber,
        string? checkoutAttemptId,
        ParfaitCheckoutCustomerRequest customer,
        IReadOnlyList<ParfaitValidatedCartItem> items,
        int subtotalCents,
        string? discountCode,
        string? discountLabel,
        int discountCents,
        int shippingCents,
        int taxCents,
        HttpContext httpContext,
        DateTime now)
    {
        var order = new ParfaitOrderRecord
        {
            OrderNumber = orderNumber,
            CreatedUtc = now,
            UpdatedUtc = now,
            Status = "Payment Pending",
            PaymentStatus = "Pending",
            FulfillmentStatus = "Unfulfilled",
            ReturnStatus = "None",
            FirstName = "",
            LastName = "",
            Email = "",
            Phone = "",
            AddressLine1 = "",
            City = "",
            State = "",
            PostalCode = ""
        };

        ApplyCheckoutSnapshot(
            order,
            checkoutAttemptId,
            customer,
            items,
            subtotalCents,
            discountCode,
            discountLabel,
            discountCents,
            shippingCents,
            taxCents,
            httpContext,
            now);

        return order;
    }

    private static void ApplyCheckoutSnapshot(
        ParfaitOrderRecord order,
        string? checkoutAttemptId,
        ParfaitCheckoutCustomerRequest customer,
        IReadOnlyList<ParfaitValidatedCartItem> items,
        int subtotalCents,
        string? discountCode,
        string? discountLabel,
        int discountCents,
        int shippingCents,
        int taxCents,
        HttpContext httpContext,
        DateTime now)
    {
        var subtotal = Math.Max(0, subtotalCents);
        var normalizedDiscountCents = Math.Clamp(discountCents, 0, subtotal);
        var shipping = Math.Max(0, shippingCents);
        var tax = Math.Max(0, taxCents);

        order.UpdatedUtc = now;
        order.PaymentStatus = "Pending";
        order.FulfillmentStatus = string.IsNullOrWhiteSpace(order.FulfillmentStatus)
            ? "Unfulfilled"
            : NormalizeFulfillmentStatus(order.FulfillmentStatus);
        order.ReturnStatus = string.IsNullOrWhiteSpace(order.ReturnStatus)
            ? "None"
            : NormalizeReturnStatus(order.ReturnStatus);
        order.CheckoutAttemptId = NullIfEmpty(checkoutAttemptId);
        order.IsPaymentProcessing = false;
        order.PaymentProcessingStartedUtc = null;
        order.SquarePaymentId = null;
        order.SquareError = null;

        order.FirstName = Clean(customer.FirstName);
        order.LastName = Clean(customer.LastName);
        order.Email = Clean(customer.Email).ToLowerInvariant();
        order.Phone = Clean(customer.Phone);
        order.AddressLine1 = Clean(customer.AddressLine1);
        order.AddressLine2 = NullIfEmpty(customer.AddressLine2);
        order.City = Clean(customer.City);
        order.State = Clean(customer.State).ToUpperInvariant();
        order.PostalCode = Clean(customer.PostalCode);
        order.Source = "Public Store";
        order.UserAgent = httpContext.Request.Headers.UserAgent.ToString();
        order.RequestIp = httpContext.Connection.RemoteIpAddress?.ToString();

        order.Items = items.Select(NormalizeItem).ToList();
        order.SubtotalCents = subtotal;
        order.DiscountCode = string.IsNullOrWhiteSpace(discountCode) ? null : discountCode.Trim().ToUpperInvariant();
        order.DiscountLabel = string.IsNullOrWhiteSpace(discountLabel) ? null : discountLabel.Trim();
        order.DiscountCents = normalizedDiscountCents;
        order.RefundedCents = Math.Max(0, order.RefundedCents);
        order.ShippingCents = shipping;
        order.TaxCents = tax;
        order.TotalCents = Math.Max(0, subtotal - normalizedDiscountCents + shipping + tax);
        order.Status = "Payment Pending";
    }

    private static ParfaitOrderRecord NormalizeOrder(ParfaitOrderRecord order)
    {
        var requiresRewrite = false;
        return NormalizeOrder(order, ref requiresRewrite);
    }

    private static ParfaitOrderRecord NormalizeOrder(ParfaitOrderRecord order, ref bool requiresRewrite)
    {
        var createdUtc = order.CreatedUtc == default ? DateTime.UtcNow : order.CreatedUtc;
        var normalizedItems = new List<ParfaitValidatedCartItem>();
        foreach (var item in order.Items ?? [])
        {
            var normalizedItem = NormalizeItem(item);
            if (!string.Equals(normalizedItem.Slug, item.Slug, StringComparison.Ordinal)
                || !string.Equals(normalizedItem.Name, item.Name, StringComparison.Ordinal)
                || !string.Equals(normalizedItem.Size, item.Size, StringComparison.Ordinal))
            {
                requiresRewrite = true;
            }

            normalizedItems.Add(normalizedItem);
        }

        var normalized = new ParfaitOrderRecord
        {
            OrderNumber = Clean(order.OrderNumber),
            CreatedUtc = createdUtc,
            UpdatedUtc = order.UpdatedUtc == default ? createdUtc : order.UpdatedUtc,
            PaidUtc = order.PaidUtc,
            ShippedUtc = order.ShippedUtc,
            FulfilledUtc = order.FulfilledUtc,
            Status = order.Status,
            PaymentStatus = NormalizePaymentStatus(order.PaymentStatus),
            FulfillmentStatus = NormalizeFulfillmentStatus(order.FulfillmentStatus),
            ReturnStatus = NormalizeReturnStatus(order.ReturnStatus),
            CheckoutAttemptId = NullIfEmpty(order.CheckoutAttemptId),
            IsPaymentProcessing = order.IsPaymentProcessing
                && string.Equals(NormalizePaymentStatus(order.PaymentStatus), "Pending", StringComparison.OrdinalIgnoreCase)
                && order.PaymentProcessingStartedUtc is not null
                && order.PaymentProcessingStartedUtc.Value >= DateTime.UtcNow - PaymentProcessingTimeout,
            PaymentProcessingStartedUtc = order.IsPaymentProcessing
                && string.Equals(NormalizePaymentStatus(order.PaymentStatus), "Pending", StringComparison.OrdinalIgnoreCase)
                && order.PaymentProcessingStartedUtc is not null
                && order.PaymentProcessingStartedUtc.Value >= DateTime.UtcNow - PaymentProcessingTimeout
                    ? order.PaymentProcessingStartedUtc
                    : null,
            SquarePaymentId = NullIfEmpty(order.SquarePaymentId),
            SquareError = NullIfEmpty(order.SquareError),
            TrackingCarrier = NullIfEmpty(order.TrackingCarrier),
            TrackingNumber = NullIfEmpty(order.TrackingNumber),
            AdminNotes = NullIfEmpty(order.AdminNotes),
            FirstName = Clean(order.FirstName),
            LastName = Clean(order.LastName),
            Email = Clean(order.Email).ToLowerInvariant(),
            Phone = Clean(order.Phone),
            AddressLine1 = Clean(order.AddressLine1),
            AddressLine2 = NullIfEmpty(order.AddressLine2),
            City = Clean(order.City),
            State = Clean(order.State).ToUpperInvariant(),
            PostalCode = Clean(order.PostalCode),
            Source = string.IsNullOrWhiteSpace(order.Source) ? "Public Store" : Clean(order.Source),
            UserAgent = NullIfEmpty(order.UserAgent),
            RequestIp = NullIfEmpty(order.RequestIp),
            Items = normalizedItems,
            SubtotalCents = order.SubtotalCents > 0 ? order.SubtotalCents : normalizedItems.Sum(item => item.LineTotalCents),
            DiscountCode = NullIfEmpty(order.DiscountCode)?.ToUpperInvariant(),
            DiscountLabel = NullIfEmpty(order.DiscountLabel),
            DiscountCents = Math.Max(0, order.DiscountCents),
            RefundedCents = Math.Max(0, order.RefundedCents),
            ShippingCents = Math.Max(0, order.ShippingCents),
            TaxCents = Math.Max(0, order.TaxCents),
            TotalCents = Math.Max(0, order.TotalCents > 0
                ? order.TotalCents
                : (order.SubtotalCents > 0 ? order.SubtotalCents : normalizedItems.Sum(item => item.LineTotalCents))
                    - Math.Max(0, order.DiscountCents)
                    + Math.Max(0, order.ShippingCents)
                    + Math.Max(0, order.TaxCents))
        };

        normalized.Status = BuildStatus(normalized);

        if (!string.Equals(normalized.OrderNumber, order.OrderNumber, StringComparison.Ordinal)
            || !string.Equals(normalized.PaymentStatus, order.PaymentStatus, StringComparison.Ordinal)
            || !string.Equals(normalized.FulfillmentStatus, order.FulfillmentStatus, StringComparison.Ordinal)
            || !string.Equals(normalized.ReturnStatus, order.ReturnStatus, StringComparison.Ordinal)
            || !string.Equals(normalized.CheckoutAttemptId, order.CheckoutAttemptId, StringComparison.Ordinal)
            || normalized.IsPaymentProcessing != order.IsPaymentProcessing
            || normalized.SubtotalCents != order.SubtotalCents
            || normalized.TotalCents != order.TotalCents
            || normalized.RefundedCents != order.RefundedCents
            || normalized.UpdatedUtc != order.UpdatedUtc
            || !string.Equals(normalized.Status, order.Status, StringComparison.Ordinal))
        {
            requiresRewrite = true;
        }

        return normalized;
    }

    private static ParfaitValidatedCartItem NormalizeItem(ParfaitValidatedCartItem item)
    {
        var name = Clean(item.Name);
        var slug = string.IsNullOrWhiteSpace(item.Slug)
            ? Slugify(name)
            : Slugify(item.Slug);

        return new ParfaitValidatedCartItem
        {
            Id = Clean(item.Id),
            Name = name,
            Slug = slug,
            Size = string.IsNullOrWhiteSpace(item.Size) ? "N/A" : Clean(item.Size).ToUpperInvariant(),
            Quantity = Math.Clamp(item.Quantity, 1, 99),
            UnitPriceCents = Math.Max(0, item.UnitPriceCents),
            CompareAtPriceCents = Math.Max(0, item.CompareAtPriceCents),
            ImageUrl = NullIfEmpty(item.ImageUrl)
        };
    }

    private static string NormalizePaymentStatus(string? value)
    {
        var normalized = Clean(value).ToLowerInvariant();
        return normalized switch
        {
            "paid" => "Paid",
            "failed" => "Failed",
            "refunded" => "Refunded",
            _ => "Pending"
        };
    }

    private static string NormalizeFulfillmentStatus(string? value)
    {
        var normalized = Clean(value).ToLowerInvariant();
        return normalized switch
        {
            "processing" => "Processing",
            "packed" => "Packed",
            "shipped" => "Shipped",
            "fulfilled" => "Fulfilled",
            "on hold" => "On Hold",
            "cancelled" => "Cancelled",
            "returned" => "Returned",
            _ => "Unfulfilled"
        };
    }

    private static string NormalizeReturnStatus(string? value)
    {
        var normalized = Clean(value).ToLowerInvariant();
        return normalized switch
        {
            "requested" => "Requested",
            "approved" => "Approved",
            "received" => "Received",
            "refunded" => "Refunded",
            "closed" => "Closed",
            _ => "None"
        };
    }

    private static string BuildStatus(ParfaitOrderRecord order)
    {
        if (order.IsPaymentProcessing)
        {
            return "Payment Processing";
        }

        if (string.Equals(order.PaymentStatus, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return "Payment Failed";
        }

        if (string.Equals(order.PaymentStatus, "Pending", StringComparison.OrdinalIgnoreCase))
        {
            return "Payment Pending";
        }

        if (string.Equals(order.PaymentStatus, "Refunded", StringComparison.OrdinalIgnoreCase))
        {
            return "Refunded";
        }

        if (!string.Equals(order.ReturnStatus, "None", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(order.ReturnStatus, "Closed", StringComparison.OrdinalIgnoreCase))
        {
            return $"Return {order.ReturnStatus}";
        }

        if (string.Equals(order.FulfillmentStatus, "Fulfilled", StringComparison.OrdinalIgnoreCase))
        {
            return "Fulfilled";
        }

        if (string.Equals(order.FulfillmentStatus, "Shipped", StringComparison.OrdinalIgnoreCase))
        {
            return "Shipped";
        }

        return "Paid";
    }

    private static bool IsPaymentProcessingActive(ParfaitOrderRecord order, DateTime now)
    {
        if (!order.IsPaymentProcessing)
        {
            return false;
        }

        if (order.PaymentProcessingStartedUtc is null)
        {
            return false;
        }

        return order.PaymentProcessingStartedUtc.Value >= now - PaymentProcessingTimeout;
    }

    private static string GenerateOrderNumber(DateTime utc)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        return $"PF-{utc:yyyyMMdd}-{suffix}";
    }

    private static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var builder = new StringBuilder();
        var lastDash = false;

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastDash = false;
            }
            else if (!lastDash)
            {
                builder.Append('-');
                lastDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string Clean(string? value)
    {
        return (value ?? "").Trim();
    }

    private static string? NullIfEmpty(string? value)
    {
        var cleaned = Clean(value);
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }
}
