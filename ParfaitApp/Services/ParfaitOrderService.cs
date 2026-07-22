using System.Text;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
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
    private static readonly HashSet<string> PaidStatuses = new(StringComparer.OrdinalIgnoreCase) { "Paid", "Refunded" };
    private static readonly TimeSpan PaymentProcessingTimeout = TimeSpan.FromMinutes(10);
    private static readonly object Lock = new();

    private readonly MasterAppDbContext _db;

    public ParfaitOrderService(MasterAppDbContext db)
    {
        _db = db;
    }

    public IReadOnlyList<ParfaitOrderRecord> GetAllOrders()
    {
        var businessId = GetBusinessId();
        return _db.CommerceOrders
            .AsNoTracking()
            .Include(x => x.Lines)
            .Where(x => x.CommerceBusinessId == businessId)
            .OrderByDescending(x => x.CreatedUtc)
            .ToList()
            .Select(MapOrder)
            .Select(NormalizeOrder)
            .OrderByDescending(x => x.CreatedUtc)
            .ToList();
    }

    public ParfaitOrderRecord? GetOrder(string orderNumber)
    {
        var businessId = GetBusinessId();
        var cleaned = Clean(orderNumber);

        var order = _db.CommerceOrders
            .AsNoTracking()
            .Include(x => x.Lines)
            .FirstOrDefault(x => x.CommerceBusinessId == businessId && x.OrderNumber == cleaned);

        return order is null ? null : NormalizeOrder(MapOrder(order));
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
        lock (Lock)
        {
            var businessId = GetBusinessId();
            var now = DateTime.UtcNow;
            var record = CreatePendingOrderRecord(
                GenerateOrderNumber(now),
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
                now);

            var entity = new CommerceOrder { CommerceBusinessId = businessId };
            ApplyRecordToEntity(entity, NormalizeOrder(record));
            _db.CommerceOrders.Add(entity);
            _db.SaveChanges();

            return NormalizeOrder(MapOrder(entity));
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
            throw new ArgumentException("Checkout attempt ID is required.", nameof(checkoutAttemptId));

        lock (Lock)
        {
            var businessId = GetBusinessId();
            var now = DateTime.UtcNow;

            var order = _db.CommerceOrders
                .Include(x => x.Lines)
                .Where(x => x.CommerceBusinessId == businessId && x.CheckoutAttemptId == normalizedAttemptId)
                .OrderByDescending(x => x.CreatedUtc)
                .FirstOrDefault();

            if (order is not null)
            {
                var existing = NormalizeOrder(MapOrder(order));

                if (existing.IsPaid)
                    return new CheckoutPaymentStartResult(CheckoutPaymentStartState.AlreadyPaid, existing);

                if (IsPaymentProcessingActive(existing, now))
                    return new CheckoutPaymentStartResult(CheckoutPaymentStartState.AlreadyProcessing, existing);
            }

            ParfaitOrderRecord record;

            if (order is null)
            {
                record = CreatePendingOrderRecord(
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

                order = new CommerceOrder { CommerceBusinessId = businessId };
                _db.CommerceOrders.Add(order);
            }
            else
            {
                record = MapOrder(order);
                ApplyCheckoutSnapshot(
                    record,
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

            record.IsPaymentProcessing = true;
            record.PaymentProcessingStartedUtc = now;
            record.SquareError = null;
            StampOrder(record);

            ApplyRecordToEntity(order, NormalizeOrder(record));
            _db.SaveChanges();

            return new CheckoutPaymentStartResult(CheckoutPaymentStartState.Ready, NormalizeOrder(MapOrder(order)));
        }
    }

    public void MarkPaymentCaptured(string orderNumber, string? paymentReferenceId)
    {
        lock (Lock)
        {
            var businessId = GetBusinessId();
            var cleaned = Clean(orderNumber);

            var order = _db.CommerceOrders
                .Include(x => x.Lines)
                .FirstOrDefault(x => x.CommerceBusinessId == businessId && x.OrderNumber == cleaned);

            if (order is null)
                return;

            order.PaymentStatus = "Paid";
            order.PaidUtc = DateTime.UtcNow;
            order.SquarePaymentId = NullIfEmpty(paymentReferenceId);
            order.IsPaymentProcessing = false;
            order.PaymentProcessingStartedUtc = null;
            order.SquareError = null;
            order.UpdatedUtc = DateTime.UtcNow;
            order.Status = BuildStatus(MapOrder(order));

            _db.SaveChanges();
        }
    }

    public void MarkPaymentFailed(string orderNumber, string safeFailureSummary)
    {
        lock (Lock)
        {
            var businessId = GetBusinessId();
            var cleaned = Clean(orderNumber);

            var order = _db.CommerceOrders
                .Include(x => x.Lines)
                .FirstOrDefault(x => x.CommerceBusinessId == businessId && x.OrderNumber == cleaned);

            if (order is null)
                return;

            order.PaymentStatus = "Failed";
            order.IsPaymentProcessing = false;
            order.PaymentProcessingStartedUtc = null;
            order.SquareError = NullIfEmpty(safeFailureSummary);
            order.UpdatedUtc = DateTime.UtcNow;
            order.Status = BuildStatus(MapOrder(order));

            _db.SaveChanges();
        }
    }

    public bool UpdateOrder(ParfaitOrderAdminUpdateRequest request)
    {
        lock (Lock)
        {
            var businessId = GetBusinessId();
            var cleaned = Clean(request.OrderNumber);

            var order = _db.CommerceOrders
                .Include(x => x.Lines)
                .FirstOrDefault(x => x.CommerceBusinessId == businessId && x.OrderNumber == cleaned);

            if (order is null)
                return false;

            var now = DateTime.UtcNow;

            order.PaymentStatus = NormalizePaymentStatus(request.PaymentStatus);
            order.FulfillmentStatus = NormalizeFulfillmentStatus(request.FulfillmentStatus);
            order.ReturnStatus = NormalizeReturnStatus(request.ReturnStatus);
            order.TrackingCarrier = NullIfEmpty(request.TrackingCarrier);
            order.TrackingNumber = NullIfEmpty(request.TrackingNumber);
            order.AdminNotes = NullIfEmpty(request.AdminNotes);
            order.RefundedCents = Math.Clamp(request.RefundedCents, 0, order.TotalCents);

            if (string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase) && order.PaidUtc is null)
                order.PaidUtc = now;

            if (!string.Equals(order.PaymentStatus, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                order.IsPaymentProcessing = false;
                order.PaymentProcessingStartedUtc = null;
            }

            if (string.Equals(order.FulfillmentStatus, "Shipped", StringComparison.OrdinalIgnoreCase))
                order.ShippedUtc ??= now;

            if (string.Equals(order.FulfillmentStatus, "Fulfilled", StringComparison.OrdinalIgnoreCase))
            {
                order.FulfilledUtc ??= now;
                order.ShippedUtc ??= now;
            }

            if (string.Equals(order.PaymentStatus, "Refunded", StringComparison.OrdinalIgnoreCase) && order.RefundedCents == 0)
                order.RefundedCents = order.TotalCents;

            order.UpdatedUtc = now;
            order.Status = BuildStatus(MapOrder(order));

            _db.SaveChanges();
            return true;
        }
    }

    public int CountOpenFulfillment(IEnumerable<ParfaitOrderRecord> orders) => orders.Count(order => order.IsFulfillmentOpen);
    public int CountReturnQueue(IEnumerable<ParfaitOrderRecord> orders) => orders.Count(order => order.HasReturnWork);
    public int CountRefunded(IEnumerable<ParfaitOrderRecord> orders) => orders.Count(order => order.IsRefundedPayment);
    public int SumNetRevenueCents(IEnumerable<ParfaitOrderRecord> orders) => orders.Where(order => PaidStatuses.Contains(order.PaymentStatus)).Sum(order => order.NetRevenueCents);

    public int CalculateAverageNetOrderValueCents(IEnumerable<ParfaitOrderRecord> orders)
    {
        var paidOrders = orders.Where(order => PaidStatuses.Contains(order.PaymentStatus)).ToList();
        return paidOrders.Count == 0 ? 0 : (int)Math.Round(paidOrders.Average(order => order.NetRevenueCents));
    }

    private Guid GetBusinessId()
    {
        var business = _db.CommerceBusinesses.SingleOrDefault(x => x.Key == ParfaitBusinessScopeService.ParfaitBusinessKey);

        if (business is not null)
            return business.Id;

        business = new CommerceBusiness
        {
            Key = ParfaitBusinessScopeService.ParfaitBusinessKey,
            DisplayName = "Parfait",
            LegalName = "MyLegnd LLC",
            BusinessType = "Apparel / Ecommerce",
            PrimaryDomain = "shopparfait.com",
            Status = "Active",
            IsActive = true,
            OwnerEmail = "parfait@mylegnd.com",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        _db.CommerceBusinesses.Add(business);
        _db.SaveChanges();

        return business.Id;
    }

    private static ParfaitOrderRecord MapOrder(CommerceOrder order)
    {
        return new ParfaitOrderRecord
        {
            CommerceOrderId = order.Id,
            OrderNumber = Clean(order.OrderNumber),
            CreatedUtc = order.CreatedUtc,
            UpdatedUtc = order.UpdatedUtc,
            PaidUtc = order.PaidUtc,
            ShippedUtc = order.ShippedUtc,
            FulfilledUtc = order.FulfilledUtc,
            Status = order.Status,
            PaymentStatus = order.PaymentStatus,
            FulfillmentStatus = order.FulfillmentStatus,
            ReturnStatus = order.ReturnStatus,
            CheckoutAttemptId = order.CheckoutAttemptId,
            IsPaymentProcessing = order.IsPaymentProcessing,
            PaymentProcessingStartedUtc = order.PaymentProcessingStartedUtc,
            SquarePaymentId = order.SquarePaymentId,
            SquareError = order.SquareError,
            TrackingCarrier = order.TrackingCarrier,
            TrackingNumber = order.TrackingNumber,
            AdminNotes = order.AdminNotes,
            FirstName = order.FirstName,
            LastName = order.LastName,
            Email = order.Email,
            Phone = order.Phone,
            AddressLine1 = order.AddressLine1,
            AddressLine2 = order.AddressLine2,
            City = order.City,
            State = order.State,
            PostalCode = order.PostalCode,
            Source = order.Source,
            UserAgent = order.UserAgent,
            RequestIp = order.RequestIp,
            Items = order.Lines
                .OrderBy(x => x.Id)
                .Select(line => new ParfaitValidatedCartItem
                {
                    Id = line.ProductExternalKey,
                    Name = line.ProductName,
                    Slug = line.ProductSlug,
                    Size = line.Size,
                    Quantity = line.Quantity,
                    UnitPriceCents = line.UnitPriceCents,
                    CompareAtPriceCents = line.CompareAtPriceCents,
                    ImageUrl = line.ImageUrl
                })
                .ToList(),
            SubtotalCents = order.SubtotalCents,
            DiscountCode = order.DiscountCode,
            DiscountLabel = order.DiscountLabel,
            DiscountCents = order.DiscountCents,
            RefundedCents = order.RefundedCents,
            ShippingCents = order.ShippingCents,
            TaxCents = order.TaxCents,
            TotalCents = order.TotalCents
        };
    }

    private static void ApplyRecordToEntity(CommerceOrder entity, ParfaitOrderRecord record)
    {
        if (record.CommerceOrderId != Guid.Empty)
            entity.Id = record.CommerceOrderId;

        entity.OrderNumber = record.OrderNumber;
        entity.CreatedUtc = record.CreatedUtc;
        entity.UpdatedUtc = record.UpdatedUtc;
        entity.PaidUtc = record.PaidUtc;
        entity.ShippedUtc = record.ShippedUtc;
        entity.FulfilledUtc = record.FulfilledUtc;
        entity.Status = record.Status;
        entity.PaymentStatus = record.PaymentStatus;
        entity.FulfillmentStatus = record.FulfillmentStatus;
        entity.ReturnStatus = record.ReturnStatus;
        entity.CheckoutAttemptId = record.CheckoutAttemptId;
        entity.IsPaymentProcessing = record.IsPaymentProcessing;
        entity.PaymentProcessingStartedUtc = record.PaymentProcessingStartedUtc;
        entity.SquarePaymentId = record.SquarePaymentId;
        entity.SquareError = record.SquareError;
        entity.TrackingCarrier = record.TrackingCarrier;
        entity.TrackingNumber = record.TrackingNumber;
        entity.AdminNotes = record.AdminNotes;
        entity.FirstName = record.FirstName;
        entity.LastName = record.LastName;
        entity.Email = record.Email;
        entity.Phone = record.Phone;
        entity.AddressLine1 = record.AddressLine1;
        entity.AddressLine2 = record.AddressLine2;
        entity.City = record.City;
        entity.State = record.State;
        entity.PostalCode = record.PostalCode;
        entity.Source = record.Source;
        entity.UserAgent = record.UserAgent;
        entity.RequestIp = record.RequestIp;
        entity.SubtotalCents = record.SubtotalCents;
        entity.DiscountCode = record.DiscountCode;
        entity.DiscountLabel = record.DiscountLabel;
        entity.DiscountCents = record.DiscountCents;
        entity.RefundedCents = record.RefundedCents;
        entity.ShippingCents = record.ShippingCents;
        entity.TaxCents = record.TaxCents;
        entity.TotalCents = record.TotalCents;

        entity.Lines.Clear();
        foreach (var item in record.Items.Select(NormalizeItem))
        {
            entity.Lines.Add(new CommerceOrderLine
            {
                ProductExternalKey = item.Id,
                ProductName = item.Name,
                ProductSlug = item.Slug,
                Size = item.Size,
                Quantity = item.Quantity,
                UnitPriceCents = item.UnitPriceCents,
                CompareAtPriceCents = item.CompareAtPriceCents,
                ImageUrl = item.ImageUrl
            });
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

        ApplyCheckoutSnapshot(order, checkoutAttemptId, customer, items, subtotalCents, discountCode, discountLabel, discountCents, shippingCents, taxCents, httpContext, now);
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
        order.FulfillmentStatus = string.IsNullOrWhiteSpace(order.FulfillmentStatus) ? "Unfulfilled" : NormalizeFulfillmentStatus(order.FulfillmentStatus);
        order.ReturnStatus = string.IsNullOrWhiteSpace(order.ReturnStatus) ? "None" : NormalizeReturnStatus(order.ReturnStatus);
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
        var createdUtc = order.CreatedUtc == default ? DateTime.UtcNow : order.CreatedUtc;
        var normalizedItems = (order.Items ?? []).Select(NormalizeItem).ToList();

        var normalized = new ParfaitOrderRecord
        {
            CommerceOrderId = order.CommerceOrderId,
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
        return normalized;
    }

    private static ParfaitValidatedCartItem NormalizeItem(ParfaitValidatedCartItem item)
    {
        var name = Clean(item.Name);
        var slug = string.IsNullOrWhiteSpace(item.Slug) ? Slugify(name) : Slugify(item.Slug);

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
        return Clean(value).ToLowerInvariant() switch
        {
            "paid" => "Paid",
            "failed" => "Failed",
            "refunded" => "Refunded",
            _ => "Pending"
        };
    }

    private static string NormalizeFulfillmentStatus(string? value)
    {
        return Clean(value).ToLowerInvariant() switch
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
        return Clean(value).ToLowerInvariant() switch
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
        if (order.IsPaymentProcessing) return "Payment Processing";
        if (string.Equals(order.PaymentStatus, "Failed", StringComparison.OrdinalIgnoreCase)) return "Payment Failed";
        if (string.Equals(order.PaymentStatus, "Pending", StringComparison.OrdinalIgnoreCase)) return "Payment Pending";
        if (string.Equals(order.PaymentStatus, "Refunded", StringComparison.OrdinalIgnoreCase)) return "Refunded";
        if (!string.Equals(order.ReturnStatus, "None", StringComparison.OrdinalIgnoreCase) && !string.Equals(order.ReturnStatus, "Closed", StringComparison.OrdinalIgnoreCase)) return $"Return {order.ReturnStatus}";
        if (string.Equals(order.FulfillmentStatus, "Fulfilled", StringComparison.OrdinalIgnoreCase)) return "Fulfilled";
        if (string.Equals(order.FulfillmentStatus, "Shipped", StringComparison.OrdinalIgnoreCase)) return "Shipped";
        return "Paid";
    }

    private static bool IsPaymentProcessingActive(ParfaitOrderRecord order, DateTime now)
    {
        return order.IsPaymentProcessing
            && order.PaymentProcessingStartedUtc is not null
            && order.PaymentProcessingStartedUtc.Value >= now - PaymentProcessingTimeout;
    }

    private static string GenerateOrderNumber(DateTime utc) => $"PF-{utc:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";

    private static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

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

    private static string Clean(string? value) => (value ?? "").Trim();

    private static string? NullIfEmpty(string? value)
    {
        var cleaned = Clean(value);
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }
}
