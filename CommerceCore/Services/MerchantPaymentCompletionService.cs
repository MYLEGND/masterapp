using System.Data;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Commerce;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;

namespace ParfaitApp.Services;

/// <summary>Durable, replay-safe completion of independently verified merchant captures.</summary>
public sealed class MerchantPaymentCompletionService(
    MasterAppDbContext db, ParfaitOrderService orders, ParfaitProductService products,
    ParfaitCustomerAutomationService automations, CommerceSignalService signals, IGraphMailService mail)
{
    public async Task CompleteAsync(Guid attemptId, CancellationToken ct = default)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var attempt = await db.CommercePaymentAttempts.SingleAsync(x => x.Id == attemptId, ct);
            if (attempt.State != "Captured") return;
            var entity = await db.CommerceOrders.SingleAsync(x => x.Id == attempt.CommerceOrderId && x.CommerceBusinessId == attempt.CommerceBusinessId, ct);
            if (attempt.AmountMinor != entity.TotalCents || string.IsNullOrWhiteSpace(attempt.PaymentReference))
                throw new InvalidOperationException("Verified payment does not match order.");
            var applied = await db.CommercePaymentEffects.AnyAsync(x => x.AttemptId == attemptId && x.Kind == "OrderAndInventory" && x.State == "Completed", ct);
            if (!applied)
            {
                var order = orders.GetOrder(attempt.CommerceBusinessId, entity.OrderNumber)!;
                orders.MarkPaymentCaptured(attempt.CommerceBusinessId, entity.OrderNumber, attempt.PaymentReference, attempt.Provider, attempt.Environment);
                products.CommitPaidInventory(attempt.CommerceBusinessId, order.Items);
                AddEffect(attemptId, "OrderAndInventory", "Completed");
                AddEffect(attemptId, "Purchase", "Pending");
                AddEffect(attemptId, "Automation", "Pending");
                AddEffect(attemptId, "Receipt", "Pending");
                AddEffect(attemptId, "Notification", "Pending");
                await db.SaveChangesAsync(ct);
            }
            entity.RefundedCents = (int)Math.Clamp(attempt.RefundedMinor, 0, entity.TotalCents);
            if (entity.RefundedCents == entity.TotalCents) { entity.PaymentStatus = "Refunded"; entity.Status = "Refunded"; }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        await RetryEffectsAsync(attemptId, ct);
    }

    public async Task RetryEffectsAsync(Guid attemptId, CancellationToken ct = default)
    {
        foreach (var kind in new[] { "Purchase", "Automation" })
        {
            // Serializable isolation prevents concurrent webhook/reconciliation workers
            // applying an internal effect together.
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var effect = await db.CommercePaymentEffects.SingleOrDefaultAsync(x => x.AttemptId == attemptId && x.Kind == kind, ct);
                if (effect is null || effect.State == "Completed") return;
                var attempt = await db.CommercePaymentAttempts.SingleAsync(x => x.Id == attemptId, ct);
                if (attempt.State != "Captured") return;
                var orderNumber = await db.CommerceOrders.Where(x => x.Id == attempt.CommerceOrderId && x.CommerceBusinessId == attempt.CommerceBusinessId).Select(x => x.OrderNumber).SingleAsync(ct);
                var order = orders.GetOrder(attempt.CommerceBusinessId, orderNumber)!;
                try
                {
                    switch (kind)
                    {
                        case "Purchase":
                            var context = JsonSerializer.Deserialize<CommerceSignalContext>(attempt.SignalContextJson ?? "null")
                                ?? throw new InvalidOperationException("Missing payment attribution context.");
                            if (context.CommerceBusinessId != attempt.CommerceBusinessId) throw new InvalidOperationException("Payment attribution scope mismatch.");
                            var items = order.Items.Select(x => new CommerceSignalProduct(x.Id, x.Name, x.Slug, x.Size, x.Quantity, x.LineTotalCents)).ToArray();
                            await signals.RecordAsync("Purchase", order.OrderNumber, context with { EventUtc = order.PaidUtc ?? order.CreatedUtc, PurchaseId = attempt.PaymentReference, OrderValueCents = order.TotalCents }, items.FirstOrDefault(), new CommerceSignalCustomer(order.FirstName, order.LastName, order.Email, order.Phone, order.City, order.State, order.PostalCode), order.OrderNumber, items, ct);
                            break;
                        case "Automation": automations.MarkOrderConverted(attempt.CommerceBusinessId, order); break;
                    }
                    effect.State = "Completed";
                    effect.CompletedUtc = DateTime.UtcNow;
                    effect.SafeErrorCode = null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch
                {
                    effect.State = "Pending";
                    effect.RetryCount++;
                    effect.SafeErrorCode = "COMPLETION_EFFECT_RETRY_REQUIRED";
                }
                effect.Version++;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            });
        }
        await SendMailOnceAsync(attemptId, "Receipt", ct);
        await SendMailOnceAsync(attemptId, "Notification", ct);
    }

    private async Task SendMailOnceAsync(Guid attemptId, string kind, CancellationToken ct)
    {
        // Commit a durable claim before leaving SQL. Sending/Unknown is never replayed:
        // a process crash cannot establish whether the external mail system accepted it.
        var claimed = false;
        ParfaitOrderRecord? order = null;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            claimed = false;
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var effect = await db.CommercePaymentEffects.SingleOrDefaultAsync(x => x.AttemptId == attemptId && x.Kind == kind, ct);
            if (effect is null || effect.State != "Pending") return;
            var attempt = await db.CommercePaymentAttempts.SingleAsync(x => x.Id == attemptId, ct);
            if (attempt.State != "Captured") return;
            var number = await db.CommerceOrders.Where(x => x.Id == attempt.CommerceOrderId && x.CommerceBusinessId == attempt.CommerceBusinessId).Select(x => x.OrderNumber).SingleAsync(ct);
            order = orders.GetOrder(attempt.CommerceBusinessId, number)!;
            effect.State = "Sending";
            effect.SafeErrorCode = "MAIL_DISPATCH_OUTCOME_UNCONFIRMED";
            effect.Version++;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            claimed = true;
        });
        if (!claimed || order is null) return;
        var succeeded = false;
        try
        {
            if (kind == "Receipt") await mail.SendOrderReceiptAsync(order, ct);
            else await mail.SendOrderNotificationAsync(order, ct);
            succeeded = true;
        }
        catch { /* An exception cannot prove mail was not accepted. */ }
        db.ChangeTracker.Clear();
        var sent = await db.CommercePaymentEffects.SingleAsync(x => x.AttemptId == attemptId && x.Kind == kind, CancellationToken.None);
        sent.State = succeeded ? "Completed" : "Unknown";
        sent.CompletedUtc = succeeded ? DateTime.UtcNow : null;
        sent.SafeErrorCode = succeeded ? null : "MAIL_DISPATCH_RECONCILIATION_REQUIRED";
        sent.Version++;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private void AddEffect(Guid attemptId, string kind, string state) => db.CommercePaymentEffects.Add(new CommercePaymentEffect
    {
        AttemptId = attemptId, Kind = kind, State = state, CreatedUtc = DateTime.UtcNow,
        CompletedUtc = state == "Completed" ? DateTime.UtcNow : null
    });
}
