using System.ComponentModel.DataAnnotations;

namespace ParfaitApp.Models;

public sealed class ParfaitCheckoutItemRequest
{
    public string? Id { get; set; }
    public string? Size { get; set; }
    public int Quantity { get; set; }
}

public sealed class ParfaitCheckoutCustomerRequest
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }

    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
}

public sealed class ParfaitCheckoutPayRequest
{
    public string? SourceId { get; set; }
    public string? DiscountCode { get; set; }
    public ParfaitCheckoutCustomerRequest Customer { get; set; } = new();
    public List<ParfaitCheckoutItemRequest> Items { get; set; } = [];
}

public sealed class ParfaitCartQuoteRequest
{
    public string? DiscountCode { get; set; }
    public List<ParfaitCheckoutItemRequest> Items { get; set; } = [];
}

public sealed class ParfaitCheckoutPayResponse
{
    public bool Success { get; set; }
    public string? OrderNumber { get; set; }
    public string? RedirectUrl { get; set; }
    public string? Error { get; set; }
}

public sealed class ParfaitValidatedCartItem
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public required string Size { get; set; }
    public int Quantity { get; set; }
    public int UnitPriceCents { get; set; }
    public int CompareAtPriceCents { get; set; }
    public string? ImageUrl { get; set; }
    public int LineTotalCents => UnitPriceCents * Quantity;
}

public sealed class ParfaitCartLineQuote
{
    public required string Key { get; set; }
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public required string Size { get; set; }
    public string Badge { get; set; } = "Parfait";
    public int RequestedQuantity { get; set; }
    public int Quantity { get; set; }
    public int UnitPriceCents { get; set; }
    public int CompareAtPriceCents { get; set; }
    public string? ImageUrl { get; set; }
    public bool IsAvailable { get; set; }
    public bool IsLowStock { get; set; }
    public string AvailabilityTone { get; set; } = "success";
    public string AvailabilityLabel { get; set; } = "Available";
    public string? Issue { get; set; }
    public int LineTotalCents => UnitPriceCents * Quantity;
}

public sealed class ParfaitCartQuoteResponse
{
    public bool Success { get; set; }
    public bool IsValid { get; set; }
    public string? Error { get; set; }
    public string? DiscountCode { get; set; }
    public string? DiscountLabel { get; set; }
    public List<string> Messages { get; set; } = [];
    public List<ParfaitCartLineQuote> Items { get; set; } = [];
    public int ItemCount { get; set; }
    public int SubtotalCents { get; set; }
    public int DiscountCents { get; set; }
    public int ShippingCents { get; set; }
    public int TaxCents { get; set; }
    public int TotalCents { get; set; }
}

public sealed class ParfaitOrderRecord
{
    public required string OrderNumber { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? PaidUtc { get; set; }

    public string Status { get; set; } = "Created";
    public string PaymentStatus { get; set; } = "Pending";
    public string FulfillmentStatus { get; set; } = "Unfulfilled";

    public string? SquarePaymentId { get; set; }
    public string? SquareError { get; set; }

    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public required string Email { get; set; }
    public required string Phone { get; set; }

    public required string AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public required string City { get; set; }
    public required string State { get; set; }
    public required string PostalCode { get; set; }

    public string Source { get; set; } = "Public Store";
    public string? UserAgent { get; set; }
    public string? RequestIp { get; set; }

    public List<ParfaitValidatedCartItem> Items { get; set; } = [];

    public int SubtotalCents { get; set; }
    public string? DiscountCode { get; set; }
    public string? DiscountLabel { get; set; }
    public int DiscountCents { get; set; }
    public int ShippingCents { get; set; }
    public int TaxCents { get; set; }
    public int TotalCents { get; set; }
}

public sealed class ParfaitOrderAdminViewModel
{
    public List<ParfaitOrderRecord> Orders { get; set; } = [];
    public int PaidOrderCount { get; set; }
    public int PendingOrderCount { get; set; }
    public int FailedOrderCount { get; set; }
    public int OpenFulfillmentCount { get; set; }
    public int RevenueCents { get; set; }
    public int AverageOrderValueCents { get; set; }
}

public sealed class ParfaitOrderSuccessViewModel
{
    public ParfaitOrderRecord? Order { get; set; }
}
