using Domain.Billing;

namespace Domain.Entities;

public sealed class ClientPaymentMethod
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientProfileId { get; set; }
    public ClientProfile? ClientProfile { get; set; }

    public BillingProvider Provider { get; set; } = BillingProvider.Square;
    public BillingProviderEnvironment ProviderEnvironment { get; set; } = BillingProviderEnvironment.Sandbox;
    public string ProviderPaymentMethodId { get; set; } = string.Empty;

    // Safe display metadata only. Full card numbers, CVV values, and raw
    // payment tokens must never be persisted in the application database.
    public string? DisplayName { get; set; }
    public string? CardBrand { get; set; }
    public string? Last4 { get; set; }
    public int? ExpirationMonth { get; set; }
    public int? ExpirationYear { get; set; }
    public string? CardholderName { get; set; }

    // Billing addresses are not PCI card data. They are retained only to show
    // the client's saved billing details and to carry them through secure card
    // replacement with the payment provider.
    public string? BillingAddressLine1 { get; set; }
    public string? BillingAddressLine2 { get; set; }
    public string? BillingCity { get; set; }
    public string? BillingState { get; set; }
    public string? BillingPostalCode { get; set; }
    public string? BillingCountryCode { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RetiredUtc { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
