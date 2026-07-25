using Domain.Billing;
using Domain.Entities;

namespace Infrastructure.Billing;

internal static class ClientPaymentMethodFactory
{
    public static ClientPaymentMethod Create(
        ClientSubscription subscription,
        BillingPaymentMethodAttachmentResult attachmentResult,
        BillingPostalAddress? billingAddress,
        string? displayName,
        DateTime nowUtc)
    {
        return new ClientPaymentMethod
        {
            ClientProfileId = subscription.ClientProfileId,
            Provider = subscription.Provider,
            ProviderEnvironment = subscription.ProviderEnvironment,
            ProviderPaymentMethodId = attachmentResult.ProviderPaymentMethodId
                ?? throw new InvalidOperationException("A successful payment-method attachment must include its provider ID."),
            DisplayName = NormalizeDisplayName(displayName),
            CardBrand = attachmentResult.PaymentMethodBrand,
            Last4 = attachmentResult.PaymentMethodLast4,
            ExpirationMonth = attachmentResult.PaymentMethodExpirationMonth,
            ExpirationYear = attachmentResult.PaymentMethodExpirationYear,
            CardholderName = attachmentResult.PaymentMethodCardholderName,
            BillingAddressLine1 = NormalizeNullable(billingAddress?.AddressLine1),
            BillingAddressLine2 = NormalizeNullable(billingAddress?.AddressLine2),
            BillingCity = NormalizeNullable(billingAddress?.Locality),
            BillingState = NormalizeNullable(billingAddress?.AdministrativeDistrictLevel1),
            BillingPostalCode = NormalizeNullable(billingAddress?.PostalCode),
            BillingCountryCode = NormalizeNullable(billingAddress?.Country),
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        };
    }

    public static string? NormalizeDisplayName(string? value)
    {
        var normalized = NormalizeNullable(value);
        if (normalized is null)
            return null;

        return normalized.Length <= 80 ? normalized : normalized[..80];
    }

    private static string? NormalizeNullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
