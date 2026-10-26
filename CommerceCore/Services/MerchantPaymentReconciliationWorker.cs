using Microsoft.Extensions.Configuration;
using Domain.Billing;
using Microsoft.Extensions.Logging;
using Infrastructure.Commerce.Payments;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ParfaitApp.Services;

public sealed class MerchantPaymentReconciliationWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<MerchantPaymentReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scan = scopes.CreateScope();
                var db = scan.ServiceProvider.GetRequiredService<MasterAppDbContext>();
                await ResumeLegacyCompletionsAsync(db, stoppingToken);
                if (!configuration.GetValue<bool>("MerchantPayments:ReconciliationEnabled")) continue;
                var recent = DateTime.UtcNow.AddSeconds(-25);
                var capturedDue = DateTime.UtcNow.AddHours(-1);
                var ids = await db.CommercePaymentAttempts.AsNoTracking()
                    .Where(x => ((x.State == "Pending" || x.State == "Unknown") && x.UpdatedUtc <= recent)
                        || (x.State == "Captured" && (x.UpdatedUtc <= capturedDue || !db.CommercePaymentEffects.Any(e => e.AttemptId == x.Id)
                            || db.CommercePaymentEffects.Any(e => e.AttemptId == x.Id && e.State == "Pending"))))
                    .OrderBy(x => x.UpdatedUtc).Take(50).Select(x => new { x.Id, x.CommerceBusinessId, x.CommerceOrderId, x.State }).ToListAsync(stoppingToken);
                foreach (var item in ids)
                {
                    try
                    {
                        using var scope = scopes.CreateScope();
                        var checkout = scope.ServiceProvider.GetRequiredService<MerchantCheckoutService>();
                        await checkout.ReconcileAsync(item.CommerceBusinessId, item.CommerceOrderId, stoppingToken);
                        await scope.ServiceProvider.GetRequiredService<MerchantPaymentCompletionService>().CompleteAsync(item.Id, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch { logger.LogWarning("Merchant payment reconciliation requires another attempt."); }
                    finally
                    {
                        // Advance scan eligibility even when a provider is unavailable, so
                        // the same oldest failures cannot monopolize the bounded batch.
                        using var touch = scopes.CreateScope();
                        await touch.ServiceProvider.GetRequiredService<MasterAppDbContext>().CommercePaymentAttempts
                            .Where(x => x.Id == item.Id && x.UpdatedUtc <= recent)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UpdatedUtc, DateTime.UtcNow), stoppingToken);
                    }
                }
                var receiptIds = await db.CommercePaymentReceipts.AsNoTracking()
                    .Where(x => x.ProcessedUtc == null && x.State != "DeadLetter" && x.RetryCount < 10)
                    .OrderBy(x => x.CreatedUtc).Take(50).Select(x => x.Id).ToListAsync(stoppingToken);
                foreach (var receiptId in receiptIds)
                {
                    using var scope = scopes.CreateScope();
                    var receiptDb = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
                    var receipt = await receiptDb.CommercePaymentReceipts.SingleAsync(x => x.Id == receiptId, stoppingToken);
                    try
                    {
                        var attempt = await receiptDb.CommercePaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == receipt.AttemptId, stoppingToken);
                        await scope.ServiceProvider.GetRequiredService<MerchantCheckoutService>().ReconcileAsync(attempt.CommerceBusinessId, attempt.CommerceOrderId, stoppingToken);
                        await scope.ServiceProvider.GetRequiredService<MerchantPaymentCompletionService>().CompleteAsync(attempt.Id, stoppingToken);
                        // Completion clears tracking for execution-strategy safety.
                        receipt = await receiptDb.CommercePaymentReceipts.SingleAsync(x => x.Id == receiptId, stoppingToken);
                        receipt.State = "Processed";
                        receipt.ProcessedUtc = DateTime.UtcNow;
                        receipt.SafeErrorCode = null;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch
                    {
                        receiptDb.ChangeTracker.Clear();
                        receipt = await receiptDb.CommercePaymentReceipts.SingleAsync(x => x.Id == receiptId, stoppingToken);
                        receipt.RetryCount++;
                        receipt.State = receipt.RetryCount >= 10 ? "DeadLetter" : "Pending";
                        receipt.SafeErrorCode = "WEBHOOK_RECONCILIATION_REQUIRED";
                    }
                    await receiptDb.SaveChangesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Merchant payment reconciliation scan could not complete."); }
        }
    }
    private async Task ResumeLegacyCompletionsAsync(MasterAppDbContext db, CancellationToken ct)
    {
        var due = DateTime.UtcNow.AddSeconds(-25);
        var payments = await (from payment in db.SubscriptionPayments.AsNoTracking()
            join order in db.CommerceOrders.AsNoTracking() on payment.CommerceOrderId equals (Guid?)order.Id
            where payment.Kind == SubscriptionPaymentKind.CommerceOneTime && payment.ClientSubscriptionId == null
                && payment.CommerceCompletionContextJson != null && payment.CommerceCaptureVerifiedUtc != null
                && payment.UpdatedUtc <= due
                && (payment.Status == SubscriptionPaymentStatus.Completed || payment.Status == SubscriptionPaymentStatus.PartiallyRefunded || payment.Status == SubscriptionPaymentStatus.Refunded)
                && (!db.SubscriptionPaymentCompletionEffects.Any(e => e.SubscriptionPaymentId == payment.Id)
                    || db.SubscriptionPaymentCompletionEffects.Any(e => e.SubscriptionPaymentId == payment.Id && e.State == "Pending")
                    || (db.SubscriptionPaymentRefunds.Where(r => r.SubscriptionPaymentId == payment.Id && r.State == "Succeeded")
                        .Sum(r => (long?)r.AmountMinor) ?? 0) != order.RefundedCents)
            orderby payment.UpdatedUtc
            select new { payment.Id, order.CommerceBusinessId }).Take(50).ToListAsync(ct);
        foreach (var payment in payments)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MerchantPaymentCompletionService>()
                    .CompleteLegacyAsync(payment.Id, payment.CommerceBusinessId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { logger.LogWarning("Canonical commerce completion requires another attempt."); }
            finally
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MasterAppDbContext>().SubscriptionPayments
                    .Where(x => x.Id == payment.Id && x.UpdatedUtc <= due)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UpdatedUtc, DateTime.UtcNow), ct);
            }
        }
    }

}
