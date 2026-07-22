using System.Security.Cryptography;
using System.Text;
using Domain.Billing;

namespace Infrastructure.Billing.Square;

internal sealed class SquareBillingWebhookSignatureValidator : IBillingWebhookSignatureValidator
{
    private readonly SquareBillingOptions _options;

    public SquareBillingWebhookSignatureValidator(SquareBillingOptions options)
    {
        _options = options;
    }

    public Task<BillingWebhookSignatureValidationResult> ValidateAsync(BillingWebhookSignatureValidationRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Provider != BillingProvider.Square)
        {
            return Task.FromResult(new BillingWebhookSignatureValidationResult(
                false,
                "UNSUPPORTED_PROVIDER",
                "This webhook validator only supports Square."));
        }

        if (string.IsNullOrWhiteSpace(_options.WebhookSignatureKey))
        {
            return Task.FromResult(new BillingWebhookSignatureValidationResult(
                false,
                "WEBHOOK_SIGNATURE_KEY_MISSING",
                "Square webhook signature validation is not configured."));
        }

        var signature = (request.Signature ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(signature))
        {
            return Task.FromResult(new BillingWebhookSignatureValidationResult(
                false,
                "WEBHOOK_SIGNATURE_MISSING",
                "The Square webhook signature header is missing."));
        }

        var notificationUrl = ResolveNotificationUrl(request.NotificationUrl);
        if (string.IsNullOrWhiteSpace(notificationUrl))
        {
            return Task.FromResult(new BillingWebhookSignatureValidationResult(
                false,
                "WEBHOOK_NOTIFICATION_URL_MISSING",
                "The Square webhook notification URL could not be resolved."));
        }

        var payload = request.PayloadJson ?? string.Empty;
        var signedPayload = $"{notificationUrl}{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSignatureKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
        var expected = Convert.ToBase64String(hash);

        if (!FixedTimeEquals(signature, expected))
        {
            return Task.FromResult(new BillingWebhookSignatureValidationResult(
                false,
                "WEBHOOK_SIGNATURE_INVALID",
                "The Square webhook signature did not match the payload."));
        }

        return Task.FromResult(new BillingWebhookSignatureValidationResult(true, null, null));
    }

    private string ResolveNotificationUrl(string? requestNotificationUrl)
    {
        var configured = (_options.WebhookNotificationUrl ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return (requestNotificationUrl ?? string.Empty).Trim();
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
