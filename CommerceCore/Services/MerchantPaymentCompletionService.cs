using System.Data;
using System.Text.Json;
using Domain.Entities;
using Domain.Billing;
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
    public Task CompleteAsync(Guid attemptId, CancellationToken ct = default) => CompleteOwnerAsync(attemptId, null, ct);
    public Task CompleteLegacyAsync(Guid paymentId, Guid businessId, CancellationToken ct = default) => CompleteOwnerAsync(paymentId, businessId, ct);

    private sealed record CompletionOwner(Guid CommerceBusinessId, Guid CommerceOrderId, string State,
        long AmountMinor, string Currency, long RefundedMinor, string? PaymentReference,
        string Provider, string? Environment, string? SignalContextJson);

    private async Task<CompletionOwner> ReadOwnerAsync(Guid id, Guid? legacyBusiness, CancellationToken ct)
    {
        if (legacyBusiness is null)
        {
            var merchant = await db.CommercePaymentAttempts.SingleAsync(x => x.Id == id, ct);
            return new(merchant.CommerceBusinessId, merchant.CommerceOrderId, merchant.State, merchant.AmountMinor,
                merchant.Currency, merchant.RefundedMinor, merchant.PaymentReference, merchant.Provider, merchant.Environment, merchant.SignalContextJson);
        }
        var payment = await db.SubscriptionPayments.SingleAsync(x => x.Id == id, ct);
        var order = await db.CommerceOrders.SingleAsync(x => x.Id == payment.CommerceOrderId && x.CommerceBusinessId == legacyBusiness.Value, ct);
        if (payment.Kind != SubscriptionPaymentKind.CommerceOneTime || payment.ClientSubscriptionId is not null ||
            string.IsNullOrWhiteSpace(payment.CommerceCompletionContextJson) || payment.CommerceCaptureVerifiedUtc is null ||
            await db.CommercePaymentAttempts.AnyAsync(x => x.CommerceOrderId == order.Id, ct) ||
            await db.SubscriptionPayments.AnyAsync(x => x.Id != payment.Id && x.CommerceOrderId == order.Id &&
                (x.CommerceCaptureVerifiedUtc != null || x.Status == SubscriptionPaymentStatus.Completed ||
                    x.Status == SubscriptionPaymentStatus.PartiallyRefunded || x.Status == SubscriptionPaymentStatus.Refunded), ct))
            throw new InvalidOperationException("Legacy completion authority is not established.");
        var eligible = payment.Status is SubscriptionPaymentStatus.Completed or SubscriptionPaymentStatus.PartiallyRefunded or SubscriptionPaymentStatus.Refunded;
        var refunds = await db.SubscriptionPaymentRefunds.Where(x => x.SubscriptionPaymentId == id).ToListAsync(ct);
        if (refunds.Any(x => x.State != "Succeeded" || !string.Equals(x.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(payment.ProviderRefundId) && !refunds.Any(x => x.ProviderReference == payment.ProviderRefundId)))
            throw new InvalidOperationException("Legacy refund history requires reconciliation.");
        return new(order.CommerceBusinessId, order.Id, eligible ? "Captured" : "Pending", payment.AmountCents,
            payment.Currency, refunds.Sum(x => x.AmountMinor), payment.ProviderPaymentId,
            payment.Provider.ToString(), payment.ProviderEnvironment.ToString(), payment.CommerceCompletionContextJson);
    }

    public async Task<bool> ResumeLegacyOrderAsync(Guid businessId, Guid orderId, CancellationToken ct = default)
    {
        var ids = await db.SubscriptionPayments.Where(x => x.CommerceOrderId == orderId &&
            x.CommerceCompletionContextJson != null && x.CommerceCaptureVerifiedUtc != null)
            .Select(x => x.Id).Take(2).ToListAsync(ct);
        if (ids.Count != 1) return false;
        await CompleteLegacyAsync(ids[0], businessId, ct);
        return await db.SubscriptionPaymentCompletionEffects.AnyAsync(x => x.SubscriptionPaymentId == ids[0] && x.Kind == "OrderAndInventory" && x.State == "Completed", ct);
    }

    private async Task CompleteOwnerAsync(Guid attemptId, Guid? legacyBusiness, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var attempt = await ReadOwnerAsync(attemptId, legacyBusiness, ct);
            if (attempt.State != "Captured") return;
            var entity = await db.CommerceOrders.SingleAsync(x => x.Id == attempt.CommerceOrderId && x.CommerceBusinessId == attempt.CommerceBusinessId, ct);
            if (attempt.AmountMinor != entity.TotalCents || !string.Equals(attempt.Currency, "USD", StringComparison.OrdinalIgnoreCase)
                || attempt.RefundedMinor < 0 || attempt.RefundedMinor > entity.TotalCents
                || string.IsNullOrWhiteSpace(attempt.PaymentReference))
                throw new InvalidOperationException("Verified payment does not match order.");
            var applied = (await FindEffectAsync(attemptId, legacyBusiness, "OrderAndInventory", ct))?.State == "Completed";
            if (!applied)
            {
                var order = orders.GetOrder(attempt.CommerceBusinessId, entity.OrderNumber)!;
                orders.MarkPaymentCaptured(attempt.CommerceBusinessId, entity.OrderNumber, attempt.PaymentReference, attempt.Provider, attempt.Environment);
                products.CommitPaidInventory(attempt.CommerceBusinessId, order.Items);
                AddEffect(attemptId, legacyBusiness, "OrderAndInventory", "Completed");
                AddEffect(attemptId, legacyBusiness, "Purchase", "Pending");
                AddEffect(attemptId, legacyBusiness, "Automation", "Pending");
                AddEffect(attemptId, legacyBusiness, "Receipt", "Pending");
                AddEffect(attemptId, legacyBusiness, "Notification", "Pending");
                await db.SaveChangesAsync(ct);
            }
            entity.RefundedCents = (int)attempt.RefundedMinor;
            if (entity.RefundedCents == entity.TotalCents) { entity.PaymentStatus = "Refunded"; entity.Status = "Refunded"; }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        await RetryOwnerEffectsAsync(attemptId, legacyBusiness, ct);
    }

    public Task RetryEffectsAsync(Guid attemptId, CancellationToken ct = default) => RetryOwnerEffectsAsync(attemptId, null, ct);

    private async Task RetryOwnerEffectsAsync(Guid attemptId, Guid? legacyBusiness, CancellationToken ct)
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
                var effect = await FindEffectAsync(attemptId, legacyBusiness, kind, ct);
                if (effect is null || effect.State == "Completed") return;
                var attempt = await ReadOwnerAsync(attemptId, legacyBusiness, ct);
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
        await SendMailOnceAsync(attemptId, legacyBusiness, "Receipt", ct);
        await SendMailOnceAsync(attemptId, legacyBusiness, "Notification", ct);
    }

    private async Task SendMailOnceAsync(Guid attemptId, Guid? legacyBusiness, string kind, CancellationToken ct)
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
            var effect = await FindEffectAsync(attemptId, legacyBusiness, kind, ct);
            if (effect is null || effect.State != "Pending") return;
            var attempt = await ReadOwnerAsync(attemptId, legacyBusiness, ct);
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
        var sent = await FindEffectAsync(attemptId, legacyBusiness, kind, CancellationToken.None)
            ?? throw new InvalidOperationException("Completion effect disappeared.");
        sent.State = succeeded ? "Completed" : "Unknown";
        sent.CompletedUtc = succeeded ? DateTime.UtcNow : null;
        sent.SafeErrorCode = succeeded ? null : "MAIL_DISPATCH_RECONCILIATION_REQUIRED";
        sent.Version++;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<EffectHandle?> FindEffectAsync(Guid id, Guid? legacyBusiness, string kind, CancellationToken ct)
    {
        object? row = legacyBusiness is null
            ? await db.CommercePaymentEffects.SingleOrDefaultAsync(x => x.AttemptId == id && x.Kind == kind, ct)
            : await db.SubscriptionPaymentCompletionEffects.SingleOrDefaultAsync(x => x.SubscriptionPaymentId == id && x.Kind == kind, ct);
        return row is null ? null : new EffectHandle(row);
    }

    private void AddEffect(Guid id, Guid? legacyBusiness, string kind, string state)
    {
        if (legacyBusiness is null) db.CommercePaymentEffects.Add(new CommercePaymentEffect
        {
            AttemptId=id, Kind=kind, State=state, CreatedUtc=DateTime.UtcNow,
            CompletedUtc=state=="Completed" ? DateTime.UtcNow : null
        });
        else db.SubscriptionPaymentCompletionEffects.Add(new SubscriptionPaymentCompletionEffect
        {
            SubscriptionPaymentId=id, Kind=kind, State=state, CreatedUtc=DateTime.UtcNow,
            CompletedUtc=state=="Completed" ? DateTime.UtcNow : null
        });
    }

    // One reducer and mail dispatcher; each genuine payment authority retains
    // its own tracked storage row. Merchant schema and history stay unchanged.
    private sealed class EffectHandle(object row)
    {
        public string State
        {
            get => row is CommercePaymentEffect merchant ? merchant.State : ((SubscriptionPaymentCompletionEffect)row).State;
            set { if (row is CommercePaymentEffect merchant) merchant.State=value; else ((SubscriptionPaymentCompletionEffect)row).State=value; }
        }
        public DateTime? CompletedUtc
        {
            get => row is CommercePaymentEffect merchant ? merchant.CompletedUtc : ((SubscriptionPaymentCompletionEffect)row).CompletedUtc;
            set { if (row is CommercePaymentEffect merchant) merchant.CompletedUtc=value; else ((SubscriptionPaymentCompletionEffect)row).CompletedUtc=value; }
        }
        public string? SafeErrorCode
        {
            get => row is CommercePaymentEffect merchant ? merchant.SafeErrorCode : ((SubscriptionPaymentCompletionEffect)row).SafeErrorCode;
            set { if (row is CommercePaymentEffect merchant) merchant.SafeErrorCode=value; else ((SubscriptionPaymentCompletionEffect)row).SafeErrorCode=value; }
        }
        public int RetryCount
        {
            get => row is CommercePaymentEffect merchant ? merchant.RetryCount : ((SubscriptionPaymentCompletionEffect)row).RetryCount;
            set { if (row is CommercePaymentEffect merchant) merchant.RetryCount=value; else ((SubscriptionPaymentCompletionEffect)row).RetryCount=value; }
        }
        public int Version
        {
            get => row is CommercePaymentEffect merchant ? merchant.Version : ((SubscriptionPaymentCompletionEffect)row).Version;
            set { if (row is CommercePaymentEffect merchant) merchant.Version=value; else ((SubscriptionPaymentCompletionEffect)row).Version=value; }
        }
    }
}
