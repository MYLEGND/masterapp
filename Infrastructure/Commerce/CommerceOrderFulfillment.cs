using Domain.Entities;

namespace Infrastructure.Commerce;

/// <summary>Shared order fulfillment rules for website and native transports.</summary>
public static class CommerceOrderFulfillment
{
    public static readonly IReadOnlyList<string> States = Array.AsReadOnly(new[]
    { "Unfulfilled", "Processing", "Packed", "Shipped", "Fulfilled", "On Hold", "Cancelled", "Returned" });

    public static string Normalize(string? value) => States.FirstOrDefault(state =>
        string.Equals(state, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "Unfulfilled";

    public static string? Validate(CommerceOrder order, string status, string? carrier, string? tracking,
        string? paymentStatus = null, int? refundedCents = null)
    {
        var normalized = Normalize(status);
        if (!string.Equals(normalized, status?.Trim(), StringComparison.OrdinalIgnoreCase))
            return "Select a valid fulfillment status.";
        if (normalized != order.FulfillmentStatus && normalized is ("Processing" or "Packed" or "Shipped" or "Fulfilled") &&
            ((paymentStatus ?? order.PaymentStatus) != "Paid" || (refundedCents ?? order.RefundedCents) >= order.TotalCents && order.TotalCents > 0))
            return "Confirm payment before processing or fulfilling this order.";
        if (normalized == "Shipped" && (string.IsNullOrWhiteSpace(carrier) || string.IsNullOrWhiteSpace(tracking)))
            return "Add the carrier and tracking number before marking this order shipped.";
        if ((carrier?.Trim().Length ?? 0) > 120 || (tracking?.Trim().Length ?? 0) > 160)
            return "Carrier or tracking exceeds the allowed length.";
        return null;
    }

    public static void Apply(CommerceOrder order, string status, string? carrier, string? tracking, DateTime now)
    {
        order.FulfillmentStatus = Normalize(status);
        order.TrackingCarrier = string.IsNullOrWhiteSpace(carrier) ? null : carrier.Trim();
        order.TrackingNumber = string.IsNullOrWhiteSpace(tracking) ? null : tracking.Trim();
        if (order.FulfillmentStatus == "Shipped") order.ShippedUtc ??= now;
        if (order.FulfillmentStatus == "Fulfilled") order.FulfilledUtc ??= now;
        order.UpdatedUtc = now;
        order.Status = Status(order.IsPaymentProcessing, order.PaymentStatus, order.ReturnStatus, order.FulfillmentStatus);
    }

    public static string Status(bool processing, string payment, string returns, string fulfillment)
    {
        if (processing) return "Payment Processing";
        if (payment.Equals("Failed", StringComparison.OrdinalIgnoreCase)) return "Payment Failed";
        if (payment.Equals("Pending", StringComparison.OrdinalIgnoreCase)) return "Payment Pending";
        if (payment.Equals("Refunded", StringComparison.OrdinalIgnoreCase)) return "Refunded";
        if (!returns.Equals("None", StringComparison.OrdinalIgnoreCase) && !returns.Equals("Closed", StringComparison.OrdinalIgnoreCase)) return $"Return {returns}";
        if (fulfillment.Equals("Fulfilled", StringComparison.OrdinalIgnoreCase)) return "Fulfilled";
        if (fulfillment.Equals("Shipped", StringComparison.OrdinalIgnoreCase)) return "Shipped";
        return "Paid";
    }
}
