namespace Domain.Entities;

public sealed class CommerceOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommerceBusinessId { get; set; }
    public CommerceBusiness? CommerceBusiness { get; set; }

    public string OrderNumber { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public DateTime? PaidUtc { get; set; }
    public DateTime? ShippedUtc { get; set; }
    public DateTime? FulfilledUtc { get; set; }

    public string Status { get; set; } = "Created";
    public string PaymentStatus { get; set; } = "Pending";
    public string FulfillmentStatus { get; set; } = "Unfulfilled";
    public string ReturnStatus { get; set; } = "None";
    public string? CheckoutAttemptId { get; set; }
    public bool IsPaymentProcessing { get; set; }
    public DateTime? PaymentProcessingStartedUtc { get; set; }

    public string? SquarePaymentId { get; set; }
    public string? SquareError { get; set; }
    public string? TrackingCarrier { get; set; }
    public string? TrackingNumber { get; set; }
    public string? AdminNotes { get; set; }

    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;

    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;

    public string Source { get; set; } = "Public Store";
    public string? UserAgent { get; set; }
    public string? RequestIp { get; set; }

    public int SubtotalCents { get; set; }
    public string? DiscountCode { get; set; }
    public string? DiscountLabel { get; set; }
    public int DiscountCents { get; set; }
    public int RefundedCents { get; set; }
    public int ShippingCents { get; set; }
    public int TaxCents { get; set; }
    public int TotalCents { get; set; }

    public List<CommerceOrderLine> Lines { get; set; } = [];
}
