using System.Data;
using System.Security.Cryptography;
using System.Text;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Commerce.Payments;

/// <summary>Only merchant routing authority. Platform billing credentials are never consulted.</summary>
public sealed class MerchantCheckoutService(MasterAppDbContext db, MerchantConnectionService connections,
    IEnumerable<IMerchantPaymentGateway> gateways, IConfiguration configuration)
{
    public Task<CommercePaymentAttempt?> GetAttemptAsync(Guid businessId, Guid orderId, CancellationToken ct = default) =>
        db.CommercePaymentAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.CommerceBusinessId == businessId && x.CommerceOrderId == orderId, ct);

    public async Task<MerchantPaymentEvidence> BeginAsync(Guid businessId, Guid orderId, string returnUrl,
        string? signalContextJson = null, CancellationToken ct = default)
    {
        // The environment is server owned; a browser can never select sandbox for a live store.
        var environment = configuration["MerchantPayments:CheckoutEnvironment"] ?? "Production";
        if (environment is not ("Production" or "Sandbox")) throw new MerchantPaymentException("PAYMENT_ENVIRONMENT_INVALID");
        var old = await GetAttemptAsync(businessId, orderId, ct);
        if (old is not null) return await ReconcileAsync(businessId, orderId, ct) ?? Evidence(old); // Never re-submit an interrupted checkout.
        var order = await db.CommerceOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == orderId && x.CommerceBusinessId == businessId, ct)
            ?? throw new MerchantPaymentException("ORDER_NOT_FOUND");
        if (order.PaidUtc.HasValue || order.TotalCents <= 0) throw new MerchantPaymentException("ORDER_NOT_PAYABLE");
        var connection = await connections.ResolveActiveAsync(businessId, environment, ct)
            ?? throw new MerchantPaymentException("MERCHANT_CONNECTION_REQUIRED");
        var revision = await db.CommerceMerchantConnectionRevisions.AsNoTracking().SingleAsync(x => x.ConnectionId == connection.Id && x.Revision == connection.Revision, ct);
        var gateway = Gateway(revision.Provider, environment);
        var account = await connections.LoadAccountAsync(revision, ct);
        var readiness = await gateway.GetReadinessAsync(account, ct);
        if (!readiness.PaymentsEnabled || !readiness.SettlementsEnabled || !readiness.Currencies.Contains("USD", StringComparer.OrdinalIgnoreCase) ||
            (revision.Provider == "Square" && (string.IsNullOrWhiteSpace(revision.LocationId) || !readiness.Locations.Contains(revision.LocationId))))
            throw new MerchantPaymentException("MERCHANT_NOT_READY");
        var attempt = new CommercePaymentAttempt
        {
            CommerceBusinessId = businessId, CommerceOrderId = orderId, ConnectionId = connection.Id,
            ConnectionRevisionId = revision.Id, Provider = revision.Provider, Environment = environment,
            AccountId = revision.AccountId, LocationId = revision.LocationId, AmountMinor = order.TotalCents,
            Currency = "USD", State = "Pending", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            SignalContextJson = signalContextJson
        };
        attempt.IdempotencyKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{businessId:N}:{attempt.Provider}:{attempt.AccountId}:{environment}:{orderId:N}"))).ToLowerInvariant()[..40];
        db.CommercePaymentAttempts.Add(attempt);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(attempt).State = EntityState.Detached;
            var winner = await GetAttemptAsync(businessId, orderId, ct);
            if (winner is null) throw;
            return Evidence(winner);
        }
        try
        {
            var result = await gateway.CreateCheckoutAsync(account,
                new(attempt.Id, attempt.IdempotencyKey, attempt.AmountMinor, attempt.Currency, order.OrderNumber, returnUrl), ct);
            await ApplyAsync(attempt, result, ct);
            return Evidence(attempt);
        }
        catch
        {
            // An exception does not prove the processor did not accept the operation.
            attempt.State = "Unknown"; attempt.UpdatedUtc = DateTime.UtcNow; attempt.Version++;
            await db.SaveChangesAsync(CancellationToken.None);
            return Evidence(attempt);
        }
    }

    public async Task<MerchantPaymentEvidence?> ReconcileAsync(Guid businessId, Guid orderId, CancellationToken ct = default)
    {
        var attempt = await db.CommercePaymentAttempts.SingleOrDefaultAsync(x => x.CommerceBusinessId == businessId && x.CommerceOrderId == orderId, ct);
        if (attempt is null) return null;
        var revision = await db.CommerceMerchantConnectionRevisions.AsNoTracking().SingleAsync(x => x.Id == attempt.ConnectionRevisionId && x.CommerceBusinessId == businessId, ct);
        var account = await connections.LoadAccountAsync(revision, ct);
        var gateway = Gateway(attempt.Provider, attempt.Environment);
        MerchantPaymentEvidence? evidence;
        if (string.IsNullOrWhiteSpace(attempt.ProviderReference))
        {
            var orderNumber = await db.CommerceOrders.Where(x => x.Id == orderId && x.CommerceBusinessId == businessId).Select(x => x.OrderNumber).SingleAsync(ct);
            evidence = await gateway.FindCheckoutAsync(account, new(attempt.Id, attempt.IdempotencyKey, attempt.AmountMinor, attempt.Currency, orderNumber, ""), ct);
            if (evidence is null) { attempt.UpdatedUtc = DateTime.UtcNow; attempt.Version++; await db.SaveChangesAsync(ct); return Evidence(attempt); }
            Validate(attempt, evidence);
            attempt.ProviderReference = evidence.Reference;
        }
        else evidence = await gateway.GetPaymentAsync(account, attempt.ProviderReference, ct);
        Validate(attempt, evidence);
        if (evidence.State == "Approved" && gateway.Capabilities.HasFlag(MerchantCapabilities.Capture))
        {
            if (attempt.CaptureRequestedUtc.HasValue) return Evidence(attempt);
            attempt.CaptureRequestedUtc = DateTime.UtcNow; attempt.State = "Unknown"; attempt.Version++;
            await db.SaveChangesAsync(ct); // concurrency token claims the monetary write before sending it
            try { evidence = await gateway.CaptureAsync(account, attempt.ProviderReference, attempt.IdempotencyKey + "-capture", ct); }
            catch { return Evidence(attempt); } // reconciliation reads only after an ambiguous capture
        }
        await ApplyAsync(attempt, evidence, ct);
        var pendingRefunds = await db.CommercePaymentRefunds.Where(x => x.AttemptId == attempt.Id && x.ProviderReference != null && (x.State == "Unknown" || x.State == "Pending")).ToListAsync(ct);
        foreach (var refund in pendingRefunds)
        {
            var result = await gateway.GetRefundAsync(account, refund.ProviderReference!, ct);
            ValidateRefund(attempt, refund, result);
            refund.State = result.State; refund.Version++;
        }
        await db.SaveChangesAsync(ct);
        return Evidence(attempt);
    }

    public async Task<Guid?> HandleWebhookAsync(string provider, string environment, string body,
        IReadOnlyDictionary<string,string> headers, string endpoint, CancellationToken ct = default)
    {
        var gateway = Gateway(provider, environment);
        var hook = await gateway.VerifyWebhookAsync(environment, body, headers, endpoint, ct)
            ?? throw new MerchantPaymentException("WEBHOOK_SIGNATURE_INVALID");
        if (hook.Environment != environment || hook.EventId.Length > 200 || hook.EventId.Length == 0)
            throw new MerchantPaymentException("WEBHOOK_INVALID");
        var attempt = await db.CommercePaymentAttempts.SingleOrDefaultAsync(x => x.Provider == provider && x.Environment == environment && x.AccountId == hook.AccountId &&
            (x.ProviderReference == hook.Reference || x.PaymentReference == hook.Reference), ct);
        if (attempt is null && hook.AttemptId is Guid recoveredId)
        {
            attempt = await db.CommercePaymentAttempts.SingleOrDefaultAsync(x => x.Id == recoveredId && x.Provider == provider &&
                x.Environment == environment && x.AccountId == hook.AccountId && x.ProviderReference == null, ct);
            if (attempt is not null)
            {
                var pinned = await db.CommerceMerchantConnectionRevisions.AsNoTracking().SingleAsync(x => x.Id == attempt.ConnectionRevisionId, ct);
                var account = await connections.LoadAccountAsync(pinned, ct);
                var recovered = await gateway.GetPaymentAsync(account, hook.Reference, ct);
                await ApplyAsync(attempt, recovered, ct); // authenticated provider evidence must match immutable attempt
            }
        }
        if (attempt is null) return null;
        var receipt = await db.CommercePaymentReceipts.SingleOrDefaultAsync(x => x.Provider == provider && x.Environment == environment && x.AccountId == hook.AccountId && x.EventId == hook.EventId, ct);
        if (receipt?.ProcessedUtc is not null) return attempt.Id;
        if (receipt is null)
        {
            receipt = new() { AttemptId = attempt.Id, Provider = provider, Environment = environment, AccountId = hook.AccountId, EventId = hook.EventId, State = "Pending", CreatedUtc = DateTime.UtcNow };
            db.CommercePaymentReceipts.Add(receipt);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.Entry(receipt).State = EntityState.Detached; return attempt.Id; }
        }
        try
        {
            await ReconcileAsync(attempt.CommerceBusinessId, attempt.CommerceOrderId, ct);
            receipt.State = "Processed"; receipt.ProcessedUtc = DateTime.UtcNow; receipt.SafeErrorCode = null;
            await db.SaveChangesAsync(ct);
            return attempt.Id;
        }
        catch
        {
            receipt.RetryCount++; receipt.State = receipt.RetryCount >= 10 ? "DeadLetter" : "Pending";
            receipt.SafeErrorCode = "PROVIDER_RECONCILIATION_FAILED";
            await db.SaveChangesAsync(CancellationToken.None);
            throw new MerchantPaymentException("PROVIDER_RECONCILIATION_FAILED");
        }
    }

    public async Task<MerchantRefundResult> RefundAsync(Guid businessId, Guid orderId, long amountMinor, string requestKey, CancellationToken ct = default)
    {
        if (amountMinor <= 0 || requestKey.Length is < 1 or > 100) throw new MerchantPaymentException("REFUND_INVALID");
        CommercePaymentAttempt attempt = null!;
        CommerceMerchantConnectionRevision revision = null!;
        IMerchantPaymentGateway gateway = null!;
        CommercePaymentRefund refund = null!;
        MerchantRefundResult? previous = null;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        attempt = await db.CommercePaymentAttempts.SingleOrDefaultAsync(x => x.CommerceBusinessId == businessId && x.CommerceOrderId == orderId, ct)
            ?? throw new MerchantPaymentException("PAYMENT_NOT_FOUND");
        var existing = await db.CommercePaymentRefunds.SingleOrDefaultAsync(x => x.AttemptId == attempt.Id && x.IdempotencyKey == requestKey, ct);
        if (existing is not null)
        {
            if (existing.AmountMinor != amountMinor) throw new MerchantPaymentException("REFUND_IDEMPOTENCY_MISMATCH");
            previous = new(existing.ProviderReference ?? existing.Id.ToString("N"), existing.State);
            return;
        }
        var refunds = await db.CommercePaymentRefunds.Where(x => x.AttemptId == attempt.Id && x.State != "Failed").ToListAsync(ct);
        var unresolved = refunds.Where(x => x.State != "Succeeded").Sum(x => x.AmountMinor);
        var succeeded = refunds.Where(x => x.State == "Succeeded").Sum(x => x.AmountMinor);
        if (attempt.State != "Captured" || string.IsNullOrWhiteSpace(attempt.PaymentReference) || amountMinor > attempt.AmountMinor - Math.Max(succeeded, attempt.RefundedMinor) - unresolved)
            throw new MerchantPaymentException("REFUND_AMOUNT_INVALID");
        revision = await db.CommerceMerchantConnectionRevisions.AsNoTracking().SingleAsync(x => x.Id == attempt.ConnectionRevisionId, ct);
        gateway = Gateway(attempt.Provider, attempt.Environment);
        if (!gateway.Capabilities.HasFlag(MerchantCapabilities.Refund)) throw new MerchantPaymentException("REFUND_UNSUPPORTED");
        refund = new CommercePaymentRefund { AttemptId = attempt.Id, IdempotencyKey = requestKey, AmountMinor = amountMinor, State = "Unknown", CreatedUtc = DateTime.UtcNow };
        db.CommercePaymentRefunds.Add(refund);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        });
        if (previous is not null) return previous;
        try
        {
            var account = await connections.LoadAccountAsync(revision, ct);
            var result = await gateway.RefundAsync(account, new(attempt.PaymentReference!, amountMinor, attempt.Currency, refund.Id.ToString("N")), ct);
            if (!string.IsNullOrWhiteSpace(result.Reference) && result.Reference.Length <= 200)
            {
                // Retain the processor reference even if its response is incomplete.
                refund.ProviderReference = result.Reference; refund.Version++;
                await db.SaveChangesAsync(ct);
            }
            ValidateRefund(attempt, refund, result);
            refund.State = result.State; refund.Version++;
            await db.SaveChangesAsync(ct);
            await ReconcileAsync(businessId, orderId, ct);
            return result;
        }
        catch { return new(refund.ProviderReference ?? refund.Id.ToString("N"), "Unknown"); }
    }

    private static void ValidateRefund(CommercePaymentAttempt attempt, CommercePaymentRefund refund, MerchantRefundResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Reference) || result.PaymentReference != attempt.PaymentReference || result.AmountMinor != refund.AmountMinor ||
            !string.Equals(result.Currency, attempt.Currency, StringComparison.OrdinalIgnoreCase) ||
            (refund.ProviderReference is not null && result.Reference != refund.ProviderReference))
            throw new MerchantPaymentException("REFUND_EVIDENCE_MISMATCH");
    }

    private IMerchantPaymentGateway Gateway(string provider, string environment)
    {
        var gateway = gateways.SingleOrDefault(x => x.Provider == provider);
        return gateway is not null && gateway.IsAvailable(environment) ? gateway : throw new MerchantPaymentException("PROVIDER_UNAVAILABLE");
    }
    private async Task ApplyAsync(CommercePaymentAttempt attempt, MerchantPaymentEvidence evidence, CancellationToken ct)
    {
        Validate(attempt, evidence);
        if (!string.IsNullOrEmpty(attempt.ProviderReference) && attempt.ProviderReference != evidence.Reference)
            throw new MerchantPaymentException("PAYMENT_REFERENCE_MISMATCH");
        attempt.ProviderReference = evidence.Reference;
        attempt.PaymentReference = evidence.PaymentReference ?? attempt.PaymentReference;
        if (attempt.State != "Captured" || evidence.State == "Captured") attempt.State = evidence.State;
        attempt.RefundedMinor = Math.Max(attempt.RefundedMinor, evidence.RefundedMinor);
        attempt.DisputeState = evidence.DisputeState ?? attempt.DisputeState;
        attempt.CheckoutUrl = evidence.CheckoutUrl ?? attempt.CheckoutUrl;
        attempt.UpdatedUtc = DateTime.UtcNow; attempt.Version++;
        await db.SaveChangesAsync(ct);
    }
    private static void Validate(CommercePaymentAttempt attempt, MerchantPaymentEvidence evidence)
    {
        if (evidence.AccountId != attempt.AccountId || evidence.Environment != attempt.Environment || evidence.AmountMinor != attempt.AmountMinor ||
            !string.Equals(evidence.Currency, attempt.Currency, StringComparison.OrdinalIgnoreCase) || evidence.RefundedMinor < 0 || evidence.RefundedMinor > attempt.AmountMinor ||
            (evidence.State == "Captured" && string.IsNullOrWhiteSpace(evidence.PaymentReference)))
            throw new MerchantPaymentException("PAYMENT_EVIDENCE_MISMATCH");
    }
    private static MerchantPaymentEvidence Evidence(CommercePaymentAttempt a) => new(a.AccountId, a.Environment, a.ProviderReference ?? "", a.PaymentReference,
        a.State, a.AmountMinor, a.Currency, a.RefundedMinor, a.CheckoutUrl, a.DisputeState);
}
