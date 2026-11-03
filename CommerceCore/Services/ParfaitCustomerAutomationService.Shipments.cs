using System.Text.Encodings.Web;
using System.Text.Json;
using Domain.Entities;
using Domain.Billing;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;
using Shared.Analytics;

namespace ParfaitApp.Services;

public sealed partial class ParfaitCustomerAutomationService
{
    private const string ShipmentSource = "CommerceShipment";

    // Added to the caller's order transaction; this method never saves or sends.
    internal static void EnqueueShipmentNotice(MasterAppDbContext db, CommerceOrder order,
        string previousStatus, string? previousCarrier, string? previousTracking)
    {
        if (!CanNotifyShipment(order) || order.FulfillmentStatus is not ("Shipped" or "Fulfilled")) return;
        var correction = previousStatus == order.FulfillmentStatus;
        if (correction && previousCarrier == order.TrackingCarrier && previousTracking == order.TrackingNumber) return;
        var triggerKey = $"shipment:{order.Id:N}:{order.UpdatedUtc!.Value.Ticks}";
        var eventId = DeliveryEventId(Guid.Empty, triggerKey);
        if (db.MarketingDestinationDeliveries.Any(x => x.CommerceBusinessId == order.CommerceBusinessId
            && x.Provider == EmailProvider && x.Channel == EmailChannel && x.CanonicalEventId == eventId)) return;
        // A later transition replaces older unsent notices even if tracking later returns
        // to an earlier value. Already claimed/ambiguous receipts are never replayed.
        foreach (var pending in db.MarketingDestinationDeliveries.Where(x => x.CommerceBusinessId == order.CommerceBusinessId
            && x.Provider == EmailProvider && x.Channel == EmailChannel && x.CanonicalSource == ShipmentSource
            && (x.Status == "pending" || x.Status == "retry")).ToList())
        {
            var old = JsonSerializer.Deserialize<ParfaitAutomationDispatchLogRecord>(pending.ProviderReceiptJson ?? "null");
            if (old?.Shipment?.OrderId != order.Id) continue;
            pending.Status = "superseded";
            pending.ErrorMessage = "A newer shipment update replaced this notice before it was sent.";
            pending.NextAttemptUtc = null;
            pending.UpdatedUtc = DateTime.UtcNow;
        }
        var owner = MarketingOwnerScope.Business(order.CommerceBusinessId);
        var notice = new CommerceShipmentNotice(order.Id, order.FulfillmentStatus, order.TrackingCarrier,
            order.TrackingNumber, order.Email, order.FirstName, correction);
        db.MarketingDestinationDeliveries.Add(new MarketingDestinationDelivery {
            OwnerKey = owner.Key, OwnerType = owner.OwnerType, CommerceBusinessId = order.CommerceBusinessId,
            Provider = EmailProvider, Channel = EmailChannel, CanonicalSource = ShipmentSource,
            CanonicalEventId = eventId, CanonicalEventName = "ShipmentUpdate", ProviderEventName = "SendMail",
            Status = "pending", ProviderReceiptJson = JsonSerializer.Serialize(new ParfaitAutomationDispatchLogRecord {
                Shipment = notice, WorkflowName = "Shipment update", TriggerType = "ShipmentUpdate", TriggerKey = triggerKey,
                RecipientEmail = order.Email, RecipientFirstName = order.FirstName, OrderNumber = order.OrderNumber,
                Subject = ShipmentSubject(order.OrderNumber, notice)
            })
        });
    }

    private static bool CanNotifyShipment(CommerceOrder order) => order.PaymentStatus == "Paid"
        && !string.IsNullOrWhiteSpace(order.Email)
        && !string.Equals(order.PaymentEnvironment, BillingProviderEnvironment.Sandbox.ToString(), StringComparison.OrdinalIgnoreCase);

    private static string ShipmentSubject(string orderNumber, CommerceShipmentNotice notice) =>
        notice.TrackingCorrection ? $"Tracking updated for order {orderNumber}"
        : notice.Status == "Shipped" ? $"Order {orderNumber} has shipped" : $"Order {orderNumber} is fulfilled";

    private IEnumerable<ParfaitAutomationDispatchCandidate> GetShipmentCandidates(Guid businessId)
    {
        var now = DateTime.UtcNow;
        if (!_db.CommerceBusinesses.AsNoTracking().Any(x => x.Id == businessId && x.IsActive && x.Status == "Active")) yield break;
        var storeName = HtmlEncoder.Default.Encode(ResolveStoreName(businessId));
        foreach (var row in EmailDeliveries(businessId).AsNoTracking().Where(x => x.CanonicalSource == ShipmentSource
            && (x.Status == "pending" || x.Status == "retry" && x.NextAttemptUtc <= now)
            && x.AttemptCount < MaxDeliveryAttempts).ToList())
        {
            var activity = JsonSerializer.Deserialize<ParfaitAutomationDispatchLogRecord>(row.ProviderReceiptJson ?? "null");
            if (activity?.Shipment is not { } notice) continue;
            string Encode(string? value) => HtmlEncoder.Default.Encode(value ?? "");
            var update = notice.TrackingCorrection ? "Your tracking details have been updated."
                : notice.Status == "Shipped" ? "Your order has been handed to the carrier." : "Your order has been marked fulfilled.";
            yield return new ParfaitAutomationDispatchCandidate {
                CommerceBusinessId = businessId, WorkflowId = Guid.Empty, WorkflowName = "Shipment update",
                TriggerType = "ShipmentUpdate", TriggerKey = activity.TriggerKey, Shipment = notice,
                ToEmail = notice.Email, FirstName = notice.FirstName, OrderNumber = activity.OrderNumber,
                Subject = ShipmentSubject(activity.OrderNumber ?? "", notice),
                HtmlBody = $"<h1>{storeName}</h1><p>Hello {Encode(notice.FirstName)},</p><p>{update}</p>"
                    + $"<p>Order: {Encode(activity.OrderNumber)}</p><p>Carrier: {Encode(notice.Carrier)}<br>Tracking: {Encode(notice.Tracking)}</p>"
            };
        }
    }

    private bool RevalidateShipmentCandidate(ParfaitAutomationDispatchCandidate candidate)
    {
        var eventId = DeliveryEventId(Guid.Empty, candidate.TriggerKey);
        var receipt = EmailDeliveries(candidate.CommerceBusinessId).SingleOrDefault(x =>
            x.CanonicalEventId == eventId && x.CanonicalSource == ShipmentSource);
        if (receipt is null) return false;
        _db.Entry(receipt).Reload();
        if (receipt.Status is not ("pending" or "retry")) return false;
        var stored = JsonSerializer.Deserialize<ParfaitAutomationDispatchLogRecord>(receipt.ProviderReceiptJson ?? "null");
        if (stored?.Shipment is not { } notice || notice != candidate.Shipment) return false;
        var order = _db.CommerceOrders.AsNoTracking().SingleOrDefault(x => x.Id == notice.OrderId
            && x.CommerceBusinessId == candidate.CommerceBusinessId);
        if (order is not null && CanNotifyShipment(order) && order.FulfillmentStatus == notice.Status
            && order.TrackingCarrier == notice.Carrier && order.TrackingNumber == notice.Tracking
            && order.Email == notice.Email
            && _db.CommerceBusinesses.AsNoTracking().Any(x => x.Id == candidate.CommerceBusinessId && x.IsActive && x.Status == "Active")) return true;
        receipt.Status = "superseded";
        receipt.ErrorMessage = "Shipment details changed before this notice was sent.";
        receipt.NextAttemptUtc = null;
        receipt.UpdatedUtc = DateTime.UtcNow;
        _db.SaveChanges();
        return false;
    }
}
