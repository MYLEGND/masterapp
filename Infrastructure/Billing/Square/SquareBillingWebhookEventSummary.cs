using System.Globalization;
using System.Text.Json;

namespace Infrastructure.Billing.Square;

internal enum SquareBillingWebhookEventFamily
{
    Unknown = 0,
    Subscription = 1,
    Payment = 2,
    Invoice = 3,
    Refund = 4,
    Dispute = 5
}

internal sealed record SquareBillingWebhookEventSummary(
    string EventType,
    string ObjectType,
    string? ObjectId,
    string? SubscriptionId,
    string? PaymentId,
    string? InvoiceId,
    string? RefundId,
    string? DisputeId,
    string? CustomerId,
    string? NormalizedStatus,
    DateTime? ProviderOccurredUtc)
{
    public SquareBillingWebhookEventFamily Family =>
        SquareBillingWebhookEventParser.ResolveFamily(EventType, ObjectType);

    public string ToSanitizedJson()
    {
        var payload = new Dictionary<string, object?>
        {
            ["eventType"] = EventType,
            ["objectType"] = ObjectType,
            ["objectId"] = ObjectId,
            ["subscriptionId"] = SubscriptionId,
            ["paymentId"] = PaymentId,
            ["invoiceId"] = InvoiceId,
            ["refundId"] = RefundId,
            ["disputeId"] = DisputeId,
            ["customerId"] = CustomerId,
            ["normalizedStatus"] = NormalizedStatus,
            ["providerOccurredUtc"] = ProviderOccurredUtc?.ToString("O", CultureInfo.InvariantCulture)
        };

        return JsonSerializer.Serialize(payload);
    }
}

internal static class SquareBillingWebhookEventParser
{
    public static SquareBillingWebhookEventSummary ParseRawPayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return ParseRawRoot(document.RootElement);
    }

    public static SquareBillingWebhookEventSummary ParseStoredSummaryOrLegacyPayload(
        string? storedJson,
        string fallbackEventType,
        string? fallbackProviderObjectId)
    {
        if (string.IsNullOrWhiteSpace(storedJson))
        {
            return new SquareBillingWebhookEventSummary(
                fallbackEventType,
                ResolveFallbackObjectType(fallbackEventType),
                fallbackProviderObjectId,
                fallbackProviderObjectId,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        using var document = JsonDocument.Parse(storedJson);
        var root = document.RootElement;

        if (root.TryGetProperty("eventType", out _))
        {
            return ParseSanitizedRoot(root, fallbackEventType, fallbackProviderObjectId);
        }

        return ParseRawRoot(root);
    }

    public static SquareBillingWebhookEventFamily ResolveFamily(string? eventType, string? objectType)
    {
        var normalizedEventType = NormalizeToken(eventType);
        if (normalizedEventType.StartsWith("subscription.", StringComparison.Ordinal))
            return SquareBillingWebhookEventFamily.Subscription;
        if (normalizedEventType.StartsWith("payment.", StringComparison.Ordinal))
            return SquareBillingWebhookEventFamily.Payment;
        if (normalizedEventType.StartsWith("invoice.", StringComparison.Ordinal))
            return SquareBillingWebhookEventFamily.Invoice;
        if (normalizedEventType.StartsWith("refund.", StringComparison.Ordinal))
            return SquareBillingWebhookEventFamily.Refund;
        if (normalizedEventType.StartsWith("dispute.", StringComparison.Ordinal))
            return SquareBillingWebhookEventFamily.Dispute;

        var normalizedObjectType = NormalizeToken(objectType);
        return normalizedObjectType switch
        {
            "subscription" => SquareBillingWebhookEventFamily.Subscription,
            "payment" => SquareBillingWebhookEventFamily.Payment,
            "invoice" => SquareBillingWebhookEventFamily.Invoice,
            "refund" => SquareBillingWebhookEventFamily.Refund,
            "dispute" => SquareBillingWebhookEventFamily.Dispute,
            _ => SquareBillingWebhookEventFamily.Unknown
        };
    }

    private static SquareBillingWebhookEventSummary ParseSanitizedRoot(
        JsonElement root,
        string fallbackEventType,
        string? fallbackProviderObjectId)
    {
        var eventType = GetString(root, "eventType") ?? fallbackEventType;
        var objectType = GetString(root, "objectType") ?? ResolveFallbackObjectType(eventType);
        var objectId = GetString(root, "objectId") ?? fallbackProviderObjectId;

        return new SquareBillingWebhookEventSummary(
            eventType,
            objectType,
            objectId,
            GetString(root, "subscriptionId"),
            GetString(root, "paymentId"),
            GetString(root, "invoiceId"),
            GetString(root, "refundId"),
            GetString(root, "disputeId"),
            GetString(root, "customerId"),
            BillingStateMapper.Normalize(GetString(root, "normalizedStatus")),
            ParseDateTime(GetString(root, "providerOccurredUtc")));
    }

    private static SquareBillingWebhookEventSummary ParseRawRoot(JsonElement root)
    {
        var eventType = GetString(root, "type")
            ?? throw new InvalidOperationException("Square webhook payload is missing type.");

        var data = TryGetProperty(root, "data");
        var objectElement = data.HasValue ? TryGetProperty(data.Value, "object") : null;
        var subscription = objectElement.HasValue ? TryGetProperty(objectElement.Value, "subscription") : null;
        var payment = objectElement.HasValue ? TryGetProperty(objectElement.Value, "payment") : null;
        var invoice = objectElement.HasValue ? TryGetProperty(objectElement.Value, "invoice") : null;
        var refund = objectElement.HasValue ? TryGetProperty(objectElement.Value, "refund") : null;
        var dispute = objectElement.HasValue ? TryGetProperty(objectElement.Value, "dispute") : null;

        var objectType = GetString(data, "type")
            ?? DetectEmbeddedObjectType(subscription, payment, invoice, refund, dispute)
            ?? ResolveFallbackObjectType(eventType);
        var objectId = GetString(data, "id")
            ?? GetString(subscription, "id")
            ?? GetString(payment, "id")
            ?? GetString(invoice, "id")
            ?? GetString(refund, "id")
            ?? GetString(dispute, "id");

        var subscriptionId =
            GetString(subscription, "id") ??
            GetString(invoice, "subscription_id") ??
            GetString(payment, "subscription_details", "subscription_id");

        var paymentId =
            GetString(payment, "id") ??
            GetString(refund, "payment_id") ??
            GetString(dispute, "payment_id") ??
            GetFirstArrayString(invoice, "payment_requests", "payment_id");

        var invoiceId =
            GetString(invoice, "id") ??
            GetString(payment, "invoice_id") ??
            GetString(refund, "invoice_id");

        var refundId = GetString(refund, "id");
        var disputeId = GetString(dispute, "id");

        var customerId =
            GetString(subscription, "customer_id") ??
            GetString(payment, "customer_id") ??
            GetString(invoice, "customer_id") ??
            GetString(invoice, "primary_recipient", "customer_id") ??
            GetString(refund, "customer_id") ??
            GetString(dispute, "customer_id");

        var normalizedStatus = ResolveNormalizedStatus(eventType, subscription, payment, invoice, refund, dispute);

        var providerOccurredUtc =
            ParseDateTime(GetString(subscription, "updated_at")) ??
            ParseDateTime(GetString(payment, "updated_at")) ??
            ParseDateTime(GetString(invoice, "updated_at")) ??
            ParseDateTime(GetString(refund, "updated_at")) ??
            ParseDateTime(GetString(dispute, "updated_at")) ??
            ParseDateTime(GetString(subscription, "created_at")) ??
            ParseDateTime(GetString(payment, "created_at")) ??
            ParseDateTime(GetString(invoice, "created_at")) ??
            ParseDateTime(GetString(refund, "created_at")) ??
            ParseDateTime(GetString(dispute, "created_at")) ??
            ParseDateTime(GetString(root, "created_at"));

        return new SquareBillingWebhookEventSummary(
            eventType.Trim(),
            objectType,
            objectId,
            subscriptionId,
            paymentId,
            invoiceId,
            refundId,
            disputeId,
            customerId,
            normalizedStatus,
            providerOccurredUtc);
    }

    private static string ResolveNormalizedStatus(
        string eventType,
        JsonElement? subscription,
        JsonElement? payment,
        JsonElement? invoice,
        JsonElement? refund,
        JsonElement? dispute)
    {
        var explicitStatus =
            GetString(subscription, "status") ??
            GetString(payment, "status") ??
            GetString(invoice, "status") ??
            GetString(refund, "status") ??
            GetString(dispute, "status") ??
            GetString(dispute, "state");

        if (!string.IsNullOrWhiteSpace(explicitStatus))
            return BillingStateMapper.Normalize(explicitStatus);

        var normalizedEventType = NormalizeToken(eventType);
        if (normalizedEventType.EndsWith(".payment_made", StringComparison.Ordinal))
            return "PAID";
        if (normalizedEventType.Contains("refund", StringComparison.Ordinal))
            return "REFUNDED";

        return string.Empty;
    }

    private static string DetectEmbeddedObjectType(
        JsonElement? subscription,
        JsonElement? payment,
        JsonElement? invoice,
        JsonElement? refund,
        JsonElement? dispute)
    {
        if (subscription.HasValue) return "subscription";
        if (payment.HasValue) return "payment";
        if (invoice.HasValue) return "invoice";
        if (refund.HasValue) return "refund";
        if (dispute.HasValue) return "dispute";
        return string.Empty;
    }

    private static string ResolveFallbackObjectType(string? eventType)
    {
        var normalizedEventType = NormalizeToken(eventType);
        var separatorIndex = normalizedEventType.IndexOf('.');
        return separatorIndex > 0
            ? normalizedEventType[..separatorIndex]
            : normalizedEventType;
    }

    private static string NormalizeToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static JsonElement? TryGetProperty(JsonElement? element, string propertyName)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object)
            return null;

        return element.Value.TryGetProperty(propertyName, out var property)
            ? property
            : null;
    }

    private static string? GetString(JsonElement? element, string propertyName)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.Value.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => NormalizeString(property.GetString()),
            JsonValueKind.Number => NormalizeString(property.GetRawText()),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string? GetString(JsonElement? element, string objectPropertyName, string propertyName)
    {
        var child = TryGetProperty(element, objectPropertyName);
        return GetString(child, propertyName);
    }

    private static string? GetFirstArrayString(JsonElement? element, string arrayPropertyName, string propertyName)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.Value.TryGetProperty(arrayPropertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in array.EnumerateArray())
        {
            var value = GetString(item, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string? NormalizeString(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
