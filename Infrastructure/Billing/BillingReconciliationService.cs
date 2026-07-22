using System.Text.Json;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Billing.Square;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Billing;

internal sealed class BillingReconciliationService : IBillingReconciliationService
{
    private const int MaxRetryableAttempts = 3;
    private readonly MasterAppDbContext _db;
    private readonly IBillingGateway _gateway;
    private readonly IBillingEntitlementService _entitlements;

    private sealed record ProviderEventDecision(
        BillingProviderEventProcessingStatus Status,
        string? SafeErrorCode = null);

    public BillingReconciliationService(
        MasterAppDbContext db,
        IBillingGateway gateway,
        IBillingEntitlementService entitlements)
    {
        _db = db;
        _gateway = gateway;
        _entitlements = entitlements;
    }

    public async Task<ClientSubscription?> ReconcileSubscriptionAsync(Guid clientSubscriptionId, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        var subscription = await _db.ClientSubscriptions.FirstOrDefaultAsync(x => x.Id == clientSubscriptionId, cancellationToken);
        if (subscription is null || string.IsNullOrWhiteSpace(subscription.ProviderSubscriptionId))
            return subscription;

        var providerResult = await _gateway.GetSubscriptionAsync(subscription.ProviderSubscriptionId, correlationId, cancellationToken);
        if (!providerResult.Success)
        {
            subscription.Status = ClientSubscriptionStatus.ReconciliationRequired;
            subscription.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return subscription;
        }

        ApplyProviderSubscriptionResult(subscription, providerResult, DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);
        await RefreshEntitlementAsync(subscription.ClientProfileId, "RECONCILIATION", cancellationToken);
        return subscription;
    }

    public async Task<int> ReconcilePendingProviderEventsAsync(int maxItems, CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;
        var events = await _db.BillingProviderEvents
            .Where(x =>
                (x.ProcessingStatus == BillingProviderEventProcessingStatus.Deferred ||
                 x.ProcessingStatus == BillingProviderEventProcessingStatus.Failed) &&
                (!x.RetryUtc.HasValue || x.RetryUtc.Value <= nowUtc))
            .OrderBy(x => x.ReceivedUtc)
            .Take(maxItems)
            .ToListAsync(cancellationToken);

        foreach (var providerEvent in events)
        {
            await ReconcileProviderEventAsync(providerEvent, cancellationToken);
        }

        return events.Count;
    }

    private async Task ReconcileProviderEventAsync(BillingProviderEvent providerEvent, CancellationToken cancellationToken)
    {
        var startedUtc = DateTime.UtcNow;
        providerEvent.AttemptCount += 1;
        providerEvent.ProcessingStatus = BillingProviderEventProcessingStatus.Processing;
        providerEvent.SafeErrorCode = null;
        providerEvent.ProcessedUtc = null;
        providerEvent.RetryUtc = null;
        await _db.SaveChangesAsync(cancellationToken);

        ProviderEventDecision decision;
        try
        {
            var summary = SquareBillingWebhookEventParser.ParseStoredSummaryOrLegacyPayload(
                providerEvent.RetainedPayloadJson,
                providerEvent.EventType,
                providerEvent.ProviderObjectId);

            decision = await ProcessProviderEventAsync(providerEvent, summary, cancellationToken);
        }
        catch (JsonException)
        {
            decision = CreateRetryableFailureDecision(providerEvent, "PROVIDER_EVENT_SUMMARY_INVALID");
        }
        catch (InvalidOperationException)
        {
            decision = CreateRetryableFailureDecision(providerEvent, "PROVIDER_EVENT_SUMMARY_INVALID");
        }
        catch (Exception)
        {
            decision = CreateRetryableFailureDecision(providerEvent, "PROVIDER_EVENT_RECONCILIATION_FAILED");
        }

        ApplyProviderEventDecision(providerEvent, decision, startedUtc);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ProviderEventDecision> ProcessProviderEventAsync(
        BillingProviderEvent providerEvent,
        SquareBillingWebhookEventSummary summary,
        CancellationToken cancellationToken)
    {
        return summary.Family switch
        {
            SquareBillingWebhookEventFamily.Subscription => await ProcessSubscriptionEventAsync(providerEvent, summary, cancellationToken),
            SquareBillingWebhookEventFamily.Payment => await ProcessPaymentEventAsync(providerEvent, summary, cancellationToken),
            SquareBillingWebhookEventFamily.Invoice => await ProcessInvoiceEventAsync(providerEvent, summary, cancellationToken),
            SquareBillingWebhookEventFamily.Refund => await ProcessRefundEventAsync(providerEvent, summary, cancellationToken),
            SquareBillingWebhookEventFamily.Dispute => await ProcessDisputeEventAsync(providerEvent, summary, cancellationToken),
            _ => new ProviderEventDecision(BillingProviderEventProcessingStatus.IgnoredUnsupported, "WEBHOOK_EVENT_UNSUPPORTED")
        };
    }

    private async Task<ProviderEventDecision> ProcessSubscriptionEventAsync(
        BillingProviderEvent providerEvent,
        SquareBillingWebhookEventSummary summary,
        CancellationToken cancellationToken)
    {
        var subscription = await ResolveSubscriptionForSubscriptionEventAsync(providerEvent, summary, cancellationToken);
        if (subscription is null)
            return CreateExpectedResolutionDecision(providerEvent, "SUBSCRIPTION_NOT_FOUND");

        if (string.IsNullOrWhiteSpace(subscription.ProviderSubscriptionId))
            return CreateExpectedResolutionDecision(providerEvent, "SUBSCRIPTION_PROVIDER_ID_MISSING");

        var providerResult = await _gateway.GetSubscriptionAsync(subscription.ProviderSubscriptionId, providerEvent.ProviderEventId, cancellationToken);
        if (!providerResult.Success)
        {
            subscription.Status = ClientSubscriptionStatus.ReconciliationRequired;
            subscription.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return CreateRetryableFailureDecision(providerEvent, providerResult.SafeErrorCode ?? "SUBSCRIPTION_LOOKUP_FAILED");
        }

        ApplyProviderSubscriptionResult(subscription, providerResult, DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);
        await RefreshEntitlementAsync(subscription.ClientProfileId, "PROVIDER_EVENT_SUBSCRIPTION", cancellationToken);
        return new ProviderEventDecision(BillingProviderEventProcessingStatus.Processed);
    }

    private async Task<ProviderEventDecision> ProcessPaymentEventAsync(
        BillingProviderEvent providerEvent,
        SquareBillingWebhookEventSummary summary,
        CancellationToken cancellationToken)
    {
        var providerPaymentId = summary.PaymentId ?? summary.ObjectId;
        if (string.IsNullOrWhiteSpace(providerPaymentId))
            return CreateExpectedResolutionDecision(providerEvent, "PAYMENT_ID_MISSING");

        var providerResult = await _gateway.GetPaymentAsync(
            new BillingPaymentLookupRequest(providerPaymentId, providerEvent.ProviderEventId),
            cancellationToken);
        if (!providerResult.Success)
            return CreateRetryableFailureDecision(providerEvent, providerResult.SafeErrorCode ?? "PAYMENT_LOOKUP_FAILED");

        var payment = await FindSubscriptionPaymentAsync(
            providerEvent.Provider,
            providerEvent.ProviderEnvironment,
            providerPaymentId,
            providerResult.ProviderInvoiceId,
            null,
            cancellationToken);
        var subscription = payment?.ClientSubscription
            ?? await ResolveSubscriptionForPaymentAsync(providerEvent, summary, providerResult.ProviderInvoiceId, cancellationToken);

        if (payment is null && subscription is null)
            return CreateExpectedResolutionDecision(providerEvent, "PAYMENT_SUBSCRIPTION_NOT_FOUND");

        payment ??= CreateSubscriptionPayment(
            subscription,
            providerEvent,
            providerPaymentId,
            providerResult.ProviderInvoiceId,
            null,
            providerResult.AmountCents,
            providerResult.Currency,
            providerResult.ProviderOccurredUtc);

        if (subscription is not null && payment.ClientSubscriptionId is null)
        {
            payment.ClientSubscriptionId = subscription.Id;
            payment.ClientSubscription = subscription;
        }

        ApplyPaymentMutation(
            payment,
            subscription,
            providerPaymentId,
            providerResult.ProviderInvoiceId,
            null,
            providerResult.AmountCents,
            providerResult.Currency,
            providerResult.NormalizedStatus,
            providerResult.ProviderOccurredUtc,
            DateTime.UtcNow);

        if (subscription is not null)
        {
            ApplySubscriptionPaymentOutcome(subscription, payment.Status, DateTime.UtcNow);
        }

        await _db.SaveChangesAsync(cancellationToken);
        if (subscription is not null)
            await RefreshEntitlementAsync(subscription.ClientProfileId, "PROVIDER_EVENT_PAYMENT", cancellationToken);

        return new ProviderEventDecision(BillingProviderEventProcessingStatus.Processed);
    }

    private async Task<ProviderEventDecision> ProcessInvoiceEventAsync(
        BillingProviderEvent providerEvent,
        SquareBillingWebhookEventSummary summary,
        CancellationToken cancellationToken)
    {
        var providerInvoiceId = summary.InvoiceId ?? summary.ObjectId;
        if (string.IsNullOrWhiteSpace(providerInvoiceId))
            return CreateExpectedResolutionDecision(providerEvent, "INVOICE_ID_MISSING");

        BillingPaymentResult? providerPaymentResult = null;
        if (!string.IsNullOrWhiteSpace(summary.PaymentId))
        {
            providerPaymentResult = await _gateway.GetPaymentAsync(
                new BillingPaymentLookupRequest(summary.PaymentId, providerEvent.ProviderEventId),
                cancellationToken);

            if (!providerPaymentResult.Success && string.IsNullOrWhiteSpace(summary.NormalizedStatus))
                return CreateRetryableFailureDecision(providerEvent, providerPaymentResult.SafeErrorCode ?? "INVOICE_PAYMENT_LOOKUP_FAILED");
        }

        var payment = await FindSubscriptionPaymentAsync(
            providerEvent.Provider,
            providerEvent.ProviderEnvironment,
            summary.PaymentId,
            providerInvoiceId,
            null,
            cancellationToken);
        var subscription = payment?.ClientSubscription
            ?? await ResolveSubscriptionForInvoiceAsync(providerEvent, summary, providerInvoiceId, cancellationToken);

        if (subscription is null)
            return CreateExpectedResolutionDecision(providerEvent, "INVOICE_SUBSCRIPTION_NOT_FOUND");

        payment ??= CreateSubscriptionPayment(
            subscription,
            providerEvent,
            summary.PaymentId,
            providerInvoiceId,
            null,
            providerPaymentResult?.AmountCents,
            providerPaymentResult?.Currency ?? subscription.Currency,
            providerPaymentResult?.ProviderOccurredUtc ?? summary.ProviderOccurredUtc);

        if (payment.ClientSubscriptionId is null)
        {
            payment.ClientSubscriptionId = subscription.Id;
            payment.ClientSubscription = subscription;
        }

        var normalizedStatus = providerPaymentResult?.Success == true
            ? providerPaymentResult.NormalizedStatus
            : summary.NormalizedStatus;
        ApplyPaymentMutation(
            payment,
            subscription,
            summary.PaymentId,
            providerInvoiceId,
            null,
            providerPaymentResult?.AmountCents,
            providerPaymentResult?.Currency ?? subscription.Currency,
            normalizedStatus,
            providerPaymentResult?.ProviderOccurredUtc ?? summary.ProviderOccurredUtc,
            DateTime.UtcNow);
        ApplySubscriptionPaymentOutcome(subscription, payment.Status, DateTime.UtcNow);

        await _db.SaveChangesAsync(cancellationToken);
        await RefreshEntitlementAsync(subscription.ClientProfileId, "PROVIDER_EVENT_INVOICE", cancellationToken);
        return new ProviderEventDecision(BillingProviderEventProcessingStatus.Processed);
    }

    private async Task<ProviderEventDecision> ProcessRefundEventAsync(
        BillingProviderEvent providerEvent,
        SquareBillingWebhookEventSummary summary,
        CancellationToken cancellationToken)
    {
        var providerRefundId = summary.RefundId ?? summary.ObjectId;
        BillingPaymentResult? providerRefund = null;

        if (!string.IsNullOrWhiteSpace(providerRefundId))
        {
            providerRefund = await _gateway.GetRefundAsync(
                new BillingRefundLookupRequest(providerRefundId, providerEvent.ProviderEventId),
                cancellationToken);

            if (!providerRefund.Success && string.IsNullOrWhiteSpace(summary.PaymentId))
                return CreateRetryableFailureDecision(providerEvent, providerRefund.SafeErrorCode ?? "REFUND_LOOKUP_FAILED");
        }

        var providerPaymentId = providerRefund?.ExternalId ?? summary.PaymentId;
        var originalPayment = await FindSubscriptionPaymentAsync(
            providerEvent.Provider,
            providerEvent.ProviderEnvironment,
            providerPaymentId,
            providerRefund?.ProviderInvoiceId ?? summary.InvoiceId,
            null,
            cancellationToken);

        if (originalPayment is null)
            return CreateExpectedResolutionDecision(providerEvent, "REFUND_PAYMENT_NOT_FOUND");

        var originalStatus = originalPayment.Status.ToString();
        var refundAmountCents = providerRefund?.AmountCents ?? originalPayment.AmountCents;
        var isPartialRefund = originalPayment.AmountCents > 0 && refundAmountCents < originalPayment.AmountCents;
        var incomingOccurredUtc = providerRefund?.ProviderOccurredUtc ?? summary.ProviderOccurredUtc;

        if (!IsStaleProviderTimestamp(originalPayment.ProviderOccurredUtc, incomingOccurredUtc))
        {
            originalPayment.ProviderRefundId = providerRefund?.ProviderRefundId ?? providerRefundId ?? originalPayment.ProviderRefundId;
            originalPayment.ProviderInvoiceId = providerRefund?.ProviderInvoiceId ?? summary.InvoiceId ?? originalPayment.ProviderInvoiceId;
            originalPayment.ProviderOccurredUtc = MaxDate(originalPayment.ProviderOccurredUtc, incomingOccurredUtc);
            originalPayment.Status = isPartialRefund
                ? SubscriptionPaymentStatus.PartiallyRefunded
                : SubscriptionPaymentStatus.Refunded;
            originalPayment.UpdatedUtc = DateTime.UtcNow;
        }

        var subscription = originalPayment.ClientSubscription
            ?? (originalPayment.ClientSubscriptionId.HasValue
                ? await _db.ClientSubscriptions.FirstOrDefaultAsync(x => x.Id == originalPayment.ClientSubscriptionId.Value, cancellationToken)
                : null);

        if (subscription is not null && !isPartialRefund)
        {
            ApplyRefundOrDisputeOutcome(subscription, isDispute: false, DateTime.UtcNow);
        }

        AddAuditEntry(
            "SubscriptionPayment",
            originalPayment.Id,
            "refund_applied",
            originalStatus,
            originalPayment.Status.ToString(),
            BillingActorType.Provider,
            null,
            "billing_reconciliation",
            null,
            providerEvent.ProviderEventId,
            summary.ToSanitizedJson());

        await _db.SaveChangesAsync(cancellationToken);
        if (subscription is not null)
            await RefreshEntitlementAsync(subscription.ClientProfileId, "PROVIDER_EVENT_REFUND", cancellationToken);

        return new ProviderEventDecision(BillingProviderEventProcessingStatus.Processed);
    }

    private async Task<ProviderEventDecision> ProcessDisputeEventAsync(
        BillingProviderEvent providerEvent,
        SquareBillingWebhookEventSummary summary,
        CancellationToken cancellationToken)
    {
        var payment = await FindSubscriptionPaymentAsync(
            providerEvent.Provider,
            providerEvent.ProviderEnvironment,
            summary.PaymentId,
            summary.InvoiceId,
            null,
            cancellationToken);
        if (payment is null)
            return CreateExpectedResolutionDecision(providerEvent, "DISPUTE_PAYMENT_NOT_FOUND");

        var previousStatus = payment.Status.ToString();
        if (!IsStaleProviderTimestamp(payment.ProviderOccurredUtc, summary.ProviderOccurredUtc))
        {
            payment.Status = SubscriptionPaymentStatus.Disputed;
            payment.ProviderOccurredUtc = MaxDate(payment.ProviderOccurredUtc, summary.ProviderOccurredUtc);
            payment.UpdatedUtc = DateTime.UtcNow;
        }

        var subscription = payment.ClientSubscription
            ?? (payment.ClientSubscriptionId.HasValue
                ? await _db.ClientSubscriptions.FirstOrDefaultAsync(x => x.Id == payment.ClientSubscriptionId.Value, cancellationToken)
                : null);
        if (subscription is not null)
        {
            ApplyRefundOrDisputeOutcome(subscription, isDispute: true, DateTime.UtcNow);
        }

        AddAuditEntry(
            "SubscriptionPayment",
            payment.Id,
            "dispute_opened",
            previousStatus,
            payment.Status.ToString(),
            BillingActorType.Provider,
            null,
            "billing_reconciliation",
            null,
            providerEvent.ProviderEventId,
            summary.ToSanitizedJson());

        await _db.SaveChangesAsync(cancellationToken);
        if (subscription is not null)
            await RefreshEntitlementAsync(subscription.ClientProfileId, "PROVIDER_EVENT_DISPUTE", cancellationToken);

        return new ProviderEventDecision(BillingProviderEventProcessingStatus.Processed);
    }

    private async Task<ClientSubscription?> ResolveSubscriptionForSubscriptionEventAsync(
        BillingProviderEvent providerEvent,
        SquareBillingWebhookEventSummary summary,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(summary.SubscriptionId))
        {
            var bySubscriptionId = await FindSubscriptionByProviderSubscriptionIdAsync(
                providerEvent.Provider,
                providerEvent.ProviderEnvironment,
                summary.SubscriptionId,
                cancellationToken);
            if (bySubscriptionId is not null)
                return bySubscriptionId;
        }

        if (!string.IsNullOrWhiteSpace(summary.CustomerId))
        {
            return await FindSubscriptionByProviderCustomerIdAsync(
                providerEvent.Provider,
                providerEvent.ProviderEnvironment,
                summary.CustomerId,
                cancellationToken);
        }

        return null;
    }

    private async Task<ClientSubscription?> ResolveSubscriptionForPaymentAsync(
        BillingProviderEvent providerEvent,
        SquareBillingWebhookEventSummary summary,
        string? providerInvoiceId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(summary.SubscriptionId))
        {
            var bySubscriptionId = await FindSubscriptionByProviderSubscriptionIdAsync(
                providerEvent.Provider,
                providerEvent.ProviderEnvironment,
                summary.SubscriptionId,
                cancellationToken);
            if (bySubscriptionId is not null)
                return bySubscriptionId;
        }

        var byInvoicePayment = await FindSubscriptionPaymentAsync(
            providerEvent.Provider,
            providerEvent.ProviderEnvironment,
            summary.PaymentId,
            providerInvoiceId ?? summary.InvoiceId,
            null,
            cancellationToken);
        if (byInvoicePayment?.ClientSubscription is not null)
            return byInvoicePayment.ClientSubscription;

        if (!string.IsNullOrWhiteSpace(summary.CustomerId))
        {
            var byCustomer = await FindSubscriptionByProviderCustomerIdAsync(
                providerEvent.Provider,
                providerEvent.ProviderEnvironment,
                summary.CustomerId,
                cancellationToken);
            if (byCustomer is not null)
                return byCustomer;
        }

        return null;
    }

    private async Task<ClientSubscription?> ResolveSubscriptionForInvoiceAsync(
        BillingProviderEvent providerEvent,
        SquareBillingWebhookEventSummary summary,
        string providerInvoiceId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(summary.SubscriptionId))
        {
            var bySubscriptionId = await FindSubscriptionByProviderSubscriptionIdAsync(
                providerEvent.Provider,
                providerEvent.ProviderEnvironment,
                summary.SubscriptionId,
                cancellationToken);
            if (bySubscriptionId is not null)
                return bySubscriptionId;
        }

        var existingPayment = await FindSubscriptionPaymentAsync(
            providerEvent.Provider,
            providerEvent.ProviderEnvironment,
            summary.PaymentId,
            providerInvoiceId,
            null,
            cancellationToken);
        if (existingPayment?.ClientSubscription is not null)
            return existingPayment.ClientSubscription;

        if (!string.IsNullOrWhiteSpace(summary.CustomerId))
        {
            var byCustomer = await FindSubscriptionByProviderCustomerIdAsync(
                providerEvent.Provider,
                providerEvent.ProviderEnvironment,
                summary.CustomerId,
                cancellationToken);
            if (byCustomer is not null)
                return byCustomer;
        }

        return null;
    }

    private async Task<ClientSubscription?> FindSubscriptionByProviderSubscriptionIdAsync(
        BillingProvider provider,
        BillingProviderEnvironment environment,
        string providerSubscriptionId,
        CancellationToken cancellationToken)
    {
        return await _db.ClientSubscriptions
            .FirstOrDefaultAsync(
                x => x.Provider == provider &&
                     x.ProviderEnvironment == environment &&
                     x.ProviderSubscriptionId == providerSubscriptionId,
                cancellationToken);
    }

    private async Task<ClientSubscription?> FindSubscriptionByProviderCustomerIdAsync(
        BillingProvider provider,
        BillingProviderEnvironment environment,
        string providerCustomerId,
        CancellationToken cancellationToken)
    {
        return await _db.ClientSubscriptions
            .Where(x => x.Provider == provider &&
                        x.ProviderEnvironment == environment &&
                        x.ProviderCustomerId == providerCustomerId)
            .OrderByDescending(x => x.UpdatedUtc)
            .ThenByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<SubscriptionPayment?> FindSubscriptionPaymentAsync(
        BillingProvider provider,
        BillingProviderEnvironment environment,
        string? providerPaymentId,
        string? providerInvoiceId,
        string? providerRefundId,
        CancellationToken cancellationToken)
    {
        var query = _db.SubscriptionPayments
            .Include(x => x.ClientSubscription)
            .Where(x => x.Provider == provider && x.ProviderEnvironment == environment);

        if (!string.IsNullOrWhiteSpace(providerPaymentId))
        {
            var byPaymentId = await query
                .FirstOrDefaultAsync(x => x.ProviderPaymentId == providerPaymentId, cancellationToken);
            if (byPaymentId is not null)
                return byPaymentId;
        }

        if (!string.IsNullOrWhiteSpace(providerRefundId))
        {
            var byRefundId = await query
                .FirstOrDefaultAsync(x => x.ProviderRefundId == providerRefundId, cancellationToken);
            if (byRefundId is not null)
                return byRefundId;
        }

        if (!string.IsNullOrWhiteSpace(providerInvoiceId))
        {
            return await query
                .Where(x => x.ProviderInvoiceId == providerInvoiceId)
                .OrderByDescending(x => x.UpdatedUtc)
                .ThenByDescending(x => x.CreatedUtc)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return null;
    }

    private SubscriptionPayment CreateSubscriptionPayment(
        ClientSubscription? subscription,
        BillingProviderEvent providerEvent,
        string? providerPaymentId,
        string? providerInvoiceId,
        string? providerRefundId,
        int? amountCents,
        string? currency,
        DateTime? providerOccurredUtc)
    {
        var nowUtc = DateTime.UtcNow;
        var payment = new SubscriptionPayment
        {
            ClientSubscriptionId = subscription?.Id,
            ClientSubscription = subscription,
            Provider = providerEvent.Provider,
            ProviderEnvironment = providerEvent.ProviderEnvironment,
            ProviderPaymentId = providerPaymentId,
            ProviderInvoiceId = providerInvoiceId,
            ProviderRefundId = providerRefundId,
            AmountCents = amountCents ?? subscription?.MonthlyAmountCents ?? 0,
            Currency = NormalizeCurrency(currency ?? subscription?.Currency ?? "USD"),
            Status = SubscriptionPaymentStatus.Pending,
            BillingPeriodStartUtc = subscription?.CurrentPeriodStartUtc,
            BillingPeriodEndUtc = subscription?.CurrentPeriodEndUtc,
            ProviderOccurredUtc = providerOccurredUtc,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        };

        _db.SubscriptionPayments.Add(payment);
        return payment;
    }

    private static void ApplyPaymentMutation(
        SubscriptionPayment payment,
        ClientSubscription? subscription,
        string? providerPaymentId,
        string? providerInvoiceId,
        string? providerRefundId,
        int? amountCents,
        string? currency,
        string? normalizedStatus,
        DateTime? providerOccurredUtc,
        DateTime nowUtc)
    {
        if (IsStaleProviderTimestamp(payment.ProviderOccurredUtc, providerOccurredUtc))
            return;

        payment.ProviderPaymentId = providerPaymentId ?? payment.ProviderPaymentId;
        payment.ProviderInvoiceId = providerInvoiceId ?? payment.ProviderInvoiceId;
        payment.ProviderRefundId = providerRefundId ?? payment.ProviderRefundId;
        payment.AmountCents = amountCents ?? payment.AmountCents;
        payment.Currency = NormalizeCurrency(currency ?? payment.Currency);
        payment.Status = ResolvePaymentStatus(normalizedStatus);
        payment.SafeFailureCode = payment.Status is SubscriptionPaymentStatus.Failed or SubscriptionPaymentStatus.Canceled
            ? BillingStateMapper.Normalize(normalizedStatus)
            : null;
        payment.BillingPeriodStartUtc ??= subscription?.CurrentPeriodStartUtc;
        payment.BillingPeriodEndUtc ??= subscription?.CurrentPeriodEndUtc;
        payment.ProviderOccurredUtc = MaxDate(payment.ProviderOccurredUtc, providerOccurredUtc);
        payment.UpdatedUtc = nowUtc;
    }

    private static void ApplySubscriptionPaymentOutcome(
        ClientSubscription subscription,
        SubscriptionPaymentStatus paymentStatus,
        DateTime nowUtc)
    {
        if (BillingStateMapper.IsTerminal(subscription.Status))
            return;

        switch (paymentStatus)
        {
            case SubscriptionPaymentStatus.Completed:
            case SubscriptionPaymentStatus.Authorized:
                subscription.Status = ClientSubscriptionStatus.Active;
                subscription.PaymentStanding = ClientSubscriptionPaymentStanding.Current;
                subscription.GracePeriodEndsUtc = null;
                subscription.ActivatedUtc ??= nowUtc;
                break;

            case SubscriptionPaymentStatus.PartiallyRefunded:
                subscription.Status = ClientSubscriptionStatus.Active;
                subscription.PaymentStanding = ClientSubscriptionPaymentStanding.Current;
                break;

            case SubscriptionPaymentStatus.Refunded:
            case SubscriptionPaymentStatus.Disputed:
                ApplyRefundOrDisputeOutcome(subscription, paymentStatus == SubscriptionPaymentStatus.Disputed, nowUtc);
                return;

            case SubscriptionPaymentStatus.Failed:
            case SubscriptionPaymentStatus.Canceled:
                ApplyRecurringPaymentFailurePolicy(subscription, nowUtc);
                return;
        }

        subscription.UpdatedUtc = nowUtc;
    }

    private static void ApplyRecurringPaymentFailurePolicy(ClientSubscription subscription, DateTime nowUtc)
    {
        var graceBoundaryUtc = ResolveGraceBoundaryUtc(subscription, nowUtc);
        subscription.GracePeriodEndsUtc = graceBoundaryUtc;
        subscription.Status = graceBoundaryUtc.HasValue && graceBoundaryUtc.Value > nowUtc
            ? ClientSubscriptionStatus.GracePeriod
            : ClientSubscriptionStatus.PastDue;
        subscription.PaymentStanding = graceBoundaryUtc.HasValue && graceBoundaryUtc.Value > nowUtc
            ? ClientSubscriptionPaymentStanding.GracePeriod
            : ClientSubscriptionPaymentStanding.PastDue;
        subscription.UpdatedUtc = nowUtc;
    }

    private static void ApplyRefundOrDisputeOutcome(ClientSubscription subscription, bool isDispute, DateTime nowUtc)
    {
        if (BillingStateMapper.IsTerminal(subscription.Status))
            return;

        subscription.Status = ClientSubscriptionStatus.Suspended;
        subscription.PaymentStanding = ClientSubscriptionPaymentStanding.Failed;
        subscription.GracePeriodEndsUtc = null;
        subscription.UpdatedUtc = nowUtc;
        if (!isDispute && !subscription.CancelledUtc.HasValue && subscription.CancelAtPeriodEnd)
            subscription.CancelledUtc = nowUtc;
    }

    private static DateTime? ResolveGraceBoundaryUtc(ClientSubscription subscription, DateTime nowUtc)
    {
        if (subscription.CurrentPeriodEndUtc.HasValue && subscription.CurrentPeriodEndUtc.Value > nowUtc)
            return subscription.CurrentPeriodEndUtc.Value;

        if (subscription.NextBillingDateUtc.HasValue && subscription.NextBillingDateUtc.Value > nowUtc)
            return subscription.NextBillingDateUtc.Value;

        return subscription.GracePeriodEndsUtc.HasValue && subscription.GracePeriodEndsUtc.Value > nowUtc
            ? subscription.GracePeriodEndsUtc.Value
            : null;
    }

    private static SubscriptionPaymentStatus ResolvePaymentStatus(string? normalizedStatus)
    {
        var mapped = BillingStateMapper.MapPaymentStatus(normalizedStatus);
        if (mapped != SubscriptionPaymentStatus.Pending)
            return mapped;

        return BillingStateMapper.Normalize(normalizedStatus) switch
        {
            "UNPAID" or "PAST_DUE" or "REQUIRES_ACTION" => SubscriptionPaymentStatus.Failed,
            _ => SubscriptionPaymentStatus.Pending
        };
    }

    private static bool IsStaleProviderTimestamp(DateTime? existingOccurredUtc, DateTime? incomingOccurredUtc)
    {
        return existingOccurredUtc.HasValue &&
               incomingOccurredUtc.HasValue &&
               incomingOccurredUtc.Value < existingOccurredUtc.Value;
    }

    private static DateTime? MaxDate(DateTime? left, DateTime? right)
    {
        if (!left.HasValue)
            return right;
        if (!right.HasValue)
            return left;
        return left.Value >= right.Value ? left : right;
    }

    private static string NormalizeCurrency(string currency) =>
        string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();

    private void ApplyProviderEventDecision(BillingProviderEvent providerEvent, ProviderEventDecision decision, DateTime nowUtc)
    {
        providerEvent.ProcessingStatus = decision.Status;
        providerEvent.SafeErrorCode = decision.SafeErrorCode;

        switch (decision.Status)
        {
            case BillingProviderEventProcessingStatus.Processed:
            case BillingProviderEventProcessingStatus.IgnoredUnsupported:
                providerEvent.ProcessedUtc = nowUtc;
                providerEvent.RetryUtc = null;
                break;

            case BillingProviderEventProcessingStatus.Deferred:
            case BillingProviderEventProcessingStatus.Failed:
                providerEvent.ProcessedUtc = null;
                providerEvent.RetryUtc = nowUtc.AddMinutes(CalculateRetryDelayMinutes(providerEvent.AttemptCount));
                break;

            case BillingProviderEventProcessingStatus.ReconciliationRequired:
                providerEvent.ProcessedUtc = null;
                providerEvent.RetryUtc = null;
                break;
        }
    }

    private ProviderEventDecision CreateExpectedResolutionDecision(BillingProviderEvent providerEvent, string safeErrorCode)
    {
        return providerEvent.AttemptCount >= MaxRetryableAttempts
            ? new ProviderEventDecision(BillingProviderEventProcessingStatus.ReconciliationRequired, safeErrorCode)
            : new ProviderEventDecision(BillingProviderEventProcessingStatus.Deferred, safeErrorCode);
    }

    private ProviderEventDecision CreateRetryableFailureDecision(BillingProviderEvent providerEvent, string safeErrorCode)
    {
        return providerEvent.AttemptCount >= MaxRetryableAttempts
            ? new ProviderEventDecision(BillingProviderEventProcessingStatus.ReconciliationRequired, safeErrorCode)
            : new ProviderEventDecision(BillingProviderEventProcessingStatus.Failed, safeErrorCode);
    }

    private static int CalculateRetryDelayMinutes(int attemptCount)
    {
        return Math.Min(30, Math.Max(5, attemptCount * 5));
    }

    private async Task RefreshEntitlementAsync(Guid clientProfileId, string reasonCode, CancellationToken cancellationToken)
    {
        await _entitlements.RefreshAsync(
            clientProfileId,
            BillingEntitlementKeys.ClientAppFullAccess,
            reasonCode,
            cancellationToken);
    }

    private void AddAuditEntry(
        string entityType,
        Guid entityId,
        string action,
        string? previousStatus,
        string? newStatus,
        BillingActorType actorType,
        string? actorId,
        string source,
        string? reasonCode,
        string? correlationId,
        string? sanitizedMetadataJson)
    {
        _db.BillingAuditEntries.Add(new BillingAuditEntry
        {
            EntityType = entityType,
            EntityId = entityId.ToString(),
            Action = action,
            PreviousStatus = previousStatus,
            NewStatus = newStatus,
            ActorType = actorType,
            ActorId = actorId,
            Source = source,
            ReasonCode = reasonCode,
            CorrelationId = correlationId,
            OccurredUtc = DateTime.UtcNow,
            SanitizedMetadataJson = sanitizedMetadataJson
        });
    }

    private static void ApplyProviderSubscriptionResult(ClientSubscription subscription, BillingSubscriptionResult providerResult, DateTime nowUtc)
    {
        subscription.ProviderCustomerId = providerResult.ProviderCustomerId ?? subscription.ProviderCustomerId;
        subscription.ProviderPaymentMethodId = providerResult.ProviderPaymentMethodId ?? subscription.ProviderPaymentMethodId;
        subscription.ProviderPlanVariationId = providerResult.ProviderPlanVariationId ?? subscription.ProviderPlanVariationId;
        subscription.ProviderSubscriptionId = providerResult.ExternalId ?? subscription.ProviderSubscriptionId;
        subscription.MonthlyAmountCents = providerResult.AmountCents ?? subscription.MonthlyAmountCents;
        subscription.Currency = providerResult.Currency ?? subscription.Currency;
        subscription.BillingAnchorDay = providerResult.BillingAnchorDay ?? subscription.BillingAnchorDay;
        subscription.CurrentPeriodStartUtc = providerResult.CurrentPeriodStartUtc ?? subscription.CurrentPeriodStartUtc;
        subscription.CurrentPeriodEndUtc = providerResult.CurrentPeriodEndUtc ?? subscription.CurrentPeriodEndUtc;
        subscription.NextBillingDateUtc = providerResult.NextBillingDateUtc ?? subscription.NextBillingDateUtc;
        subscription.CancelAtPeriodEnd = providerResult.CancelAtPeriodEnd ?? subscription.CancelAtPeriodEnd;
        subscription.Status = BillingStateMapper.MapSubscriptionStatus(providerResult.NormalizedStatus);
        subscription.PaymentStanding = BillingStateMapper.MapPaymentStanding(providerResult.NormalizedStatus);
        if (subscription.Status == ClientSubscriptionStatus.Active)
        {
            subscription.GracePeriodEndsUtc = null;
            subscription.ActivatedUtc ??= nowUtc;
        }

        if (subscription.Status == ClientSubscriptionStatus.Canceled && !subscription.CancelledUtc.HasValue)
            subscription.CancelledUtc = nowUtc;

        subscription.UpdatedUtc = nowUtc;
    }
}
