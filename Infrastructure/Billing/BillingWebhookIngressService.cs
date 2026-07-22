using System.Text.Json;
using Domain.Billing;

namespace Infrastructure.Billing;

internal sealed class BillingWebhookIngressService : IBillingWebhookIngressService
{
    private readonly IBillingWebhookSignatureValidator _signatureValidator;
    private readonly IBillingProviderEventProcessor _eventProcessor;
    private readonly IBillingGateway _gateway;

    public BillingWebhookIngressService(
        IBillingWebhookSignatureValidator signatureValidator,
        IBillingProviderEventProcessor eventProcessor,
        IBillingGateway gateway)
    {
        _signatureValidator = signatureValidator;
        _eventProcessor = eventProcessor;
        _gateway = gateway;
    }

    public async Task<BillingWebhookIngressResult> IngestAsync(BillingWebhookIngressCommand command, CancellationToken cancellationToken = default)
    {
        var validation = await _signatureValidator.ValidateAsync(
            new BillingWebhookSignatureValidationRequest(
                command.Provider,
                command.NotificationUrl,
                command.PayloadJson,
                command.Signature),
            cancellationToken);

        if (!validation.Success)
        {
            return new BillingWebhookIngressResult(
                false,
                401,
                validation.SafeErrorCode,
                validation.SanitizedSummary);
        }

        BillingProviderEventEnvelope envelope;
        try
        {
            envelope = BuildEnvelope(command);
        }
        catch (Exception ex)
        {
            return new BillingWebhookIngressResult(
                false,
                400,
                "WEBHOOK_PAYLOAD_INVALID",
                $"The webhook payload could not be parsed safely: {ex.Message}");
        }

        var processed = await _eventProcessor.ProcessAsync(envelope, cancellationToken);
        if (!processed.Success)
        {
            return new BillingWebhookIngressResult(
                false,
                processed.Retryable ? 503 : 500,
                processed.SafeErrorCode,
                processed.SanitizedSummary,
                processed.EventRecord);
        }

        return new BillingWebhookIngressResult(
            true,
            200,
            null,
            processed.SanitizedSummary,
            processed.EventRecord);
    }

    private BillingProviderEventEnvelope BuildEnvelope(BillingWebhookIngressCommand command)
    {
        using var document = JsonDocument.Parse(command.PayloadJson);
        var root = document.RootElement;

        var providerEventId =
            TryGetString(root, "event_id") ??
            TryGetString(root, "eventId");
        var eventType = TryGetString(root, "type");

        if (string.IsNullOrWhiteSpace(providerEventId))
            throw new InvalidOperationException("Square webhook payload is missing event_id.");

        if (string.IsNullOrWhiteSpace(eventType))
            throw new InvalidOperationException("Square webhook payload is missing type.");

        var providerObjectId = ResolveProviderObjectId(root);

        return new BillingProviderEventEnvelope(
            command.Provider,
            _gateway.Environment,
            providerEventId,
            eventType,
            command.PayloadJson,
            DateTime.UtcNow,
            providerObjectId,
            command.Signature,
            command.CorrelationId);
    }

    private static string? ResolveProviderObjectId(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data))
        {
            if (TryGetString(data, "id") is { Length: > 0 } directId)
                return directId;

            if (data.TryGetProperty("object", out var objectElement))
            {
                if (TryGetNestedId(objectElement, "subscription") is { Length: > 0 } subscriptionId)
                    return subscriptionId;
                if (TryGetNestedId(objectElement, "payment") is { Length: > 0 } paymentId)
                    return paymentId;
                if (TryGetNestedId(objectElement, "refund") is { Length: > 0 } refundId)
                    return refundId;
                if (TryGetNestedId(objectElement, "invoice") is { Length: > 0 } invoiceId)
                    return invoiceId;
            }
        }

        return TryGetString(root, "entity_id");
    }

    private static string? TryGetNestedId(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var node))
            return null;

        return TryGetString(node, "id");
    }

    private static string? TryGetString(JsonElement node, string propertyName)
    {
        if (!node.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return null;

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
