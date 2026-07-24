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

    public async Task<ClientSubscription?> ReconcileSubscriptionAsync(
        Guid clientSubscriptionId,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        // MASTERAPP owns the subscription schedule and lifecycle. Provider payment
        // lookups reconcile payment records only; Square Subscription state is never
        // read or applied to a local membership.
        return await _db.ClientSubscriptions.FirstOrDefaultAsync(x => x.Id == clientSubscriptionId, cancellationToken);
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
            await ReconcileProviderEventAsync(providerEvent, cancellationToken);

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
            SquareBillingWebhookEventFamily.Payment => await ProcessPaymentEventAsync(providerEvent, summary, cancellationToken),
            SquareBillingWebhookEventFamily.Refund => await ProcessRefundEventAsync(providerEvent, summary, cancellationToken),
            SquareBillingWebhookEventFamily.Dispute => await ProcessDisputeEventAsync(providerEvent, summary, cancellationToken),
            SquareBillingWebhookEventFamily.Subscription => new ProviderEventDecision(BillingProviderEventProcessingStatus.IgnoredUnsupported, "HISTORICAL_PROVIDER_SUBSCRIPTION_EVENT"),
            SquareBillingWebhookEventFamily.Invoice => new ProviderEventDecision(BillingProviderEventProcessingStatus.IgnoredUnsupported, "HISTORICAL_PROVIDER_INVOICE_EVENT"),
            _ => new ProviderEventDecision(BillingProviderEventProcessingStatus.IgnoredUnsupported, "WEBHOOK_EVENT_UNSUPPORTED")
        };
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
            ?? await ResolveSubscriptionByPaymentReferenceAsync(summary.ReferenceId, cancellationToken);

        if (payment is null && subscription is not null)
        {
            payment = await FindOpenSubscriptionPaymentAsync(subscription.Id, providerEvent.Provider, providerEvent.ProviderEnvironment, cancellationToken);
        }

        if (payment is null && subscription is null)
            return CreateExpectedResolutionDecision(providerEvent, "PAYMENT_SUBSCRIPTION_NOT_FOUND");

        payment ??= CreateSubscriptionPayment(
            subscription!,
            providerEvent,
            providerPaymentId,
            providerResult.ProviderInvoiceId,
            null,
            providerResult.AmountCents,
            providerResult.Currency,
            providerResult.ProviderOccurredUtc);

        if (payment.ClientSubscriptionId is null && subscription is not null)
        {
            payment.ClientSubscriptionId = subscription.Id;
            payment.ClientSubscription = subscription;
        }

        ApplyPaymentMutation(
            payment,
            providerPaymentId,
            providerResult.ProviderInvoiceId,
            null,
            providerResult.AmountCents,
            providerResult.Currency,
            providerResult.NormalizedStatus,
            providerResult.ProviderOccurredUtc,
            DateTime.UtcNow);

        await _db.SaveChangesAsync(cancellationToken);
        if (subscription is not null)
            await RefreshEntitlementAsync(subscription.ClientProfileId, "PROVIDER_EVENT_PAYMENT", cancellationToken);

        return new ProviderEventDecision(BillingProviderEventProcessingStatus.Processed);
    }

    private async Task<ProviderEventDecision> ProcessRefundEventAsync(
        BillingProviderEvent providerEvent,
        SquareBillingWebhookEventSummary summary,
        CancellationToken cancellationToken)
    {
        var providerRefundId = summary.RefundId ?? summary.ObjectId;
        if (string.IsNullOrWhiteSpace(providerRefundId))
            return CreateExpectedResolutionDecision(providerEvent, "REFUND_ID_MISSING");

        var providerRefund = await _gateway.GetRefundAsync(
            new BillingRefundLookupRequest(providerRefundId, providerEvent.ProviderEventId),
            cancellationToken);
        if (!providerRefund.Success)
            return CreateRetryableFailureDecision(providerEvent, providerRefund.SafeErrorCode ?? "REFUND_LOOKUP_FAILED");

        var payment = await FindSubscriptionPaymentAsync(
            providerEvent.Provider,
            providerEvent.ProviderEnvironment,
            providerRefund.ExternalId ?? summary.PaymentId,
            providerRefund.ProviderInvoiceId ?? summary.InvoiceId,
            providerRefund.ProviderRefundId ?? providerRefundId,
            cancellationToken);
        if (payment is null)
            return CreateExpectedResolutionDecision(providerEvent, "REFUND_PAYMENT_NOT_FOUND");

        var previousStatus = payment.Status.ToString();
        var refundAmountCents = providerRefund.AmountCents ?? payment.AmountCents;
        var isPartialRefund = payment.AmountCents > 0 && refundAmountCents < payment.AmountCents;
        if (!IsStaleProviderTimestamp(payment.ProviderOccurredUtc, providerRefund.ProviderOccurredUtc ?? summary.ProviderOccurredUtc))
        {
            payment.ProviderRefundId = providerRefund.ProviderRefundId ?? providerRefundId;
            payment.ProviderInvoiceId = providerRefund.ProviderInvoiceId ?? summary.InvoiceId ?? payment.ProviderInvoiceId;
            payment.ProviderOccurredUtc = MaxDate(payment.ProviderOccurredUtc, providerRefund.ProviderOccurredUtc ?? summary.ProviderOccurredUtc);
            payment.Status = isPartialRefund ? SubscriptionPaymentStatus.PartiallyRefunded : SubscriptionPaymentStatus.Refunded;
            payment.UpdatedUtc = DateTime.UtcNow;
        }

        var subscription = payment.ClientSubscription
            ?? (payment.ClientSubscriptionId.HasValue
                ? await _db.ClientSubscriptions.FirstOrDefaultAsync(x => x.Id == payment.ClientSubscriptionId.Value, cancellationToken)
                : null);
        if (subscription is not null && !isPartialRefund)
            ApplyRefundOrDisputeOutcome(subscription, isDispute: false, DateTime.UtcNow);

        AddAuditEntry("SubscriptionPayment", payment.Id, "refund_applied", previousStatus, payment.Status.ToString(), BillingActorType.Provider, null, "billing_reconciliation", null, providerEvent.ProviderEventId, summary.ToSanitizedJson());
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
            ApplyRefundOrDisputeOutcome(subscription, isDispute: true, DateTime.UtcNow);

        AddAuditEntry("SubscriptionPayment", payment.Id, "dispute_opened", previousStatus, payment.Status.ToString(), BillingActorType.Provider, null, "billing_reconciliation", null, providerEvent.ProviderEventId, summary.ToSanitizedJson());
        await _db.SaveChangesAsync(cancellationToken);
        if (subscription is not null)
            await RefreshEntitlementAsync(subscription.ClientProfileId, "PROVIDER_EVENT_DISPUTE", cancellationToken);

        return new ProviderEventDecision(BillingProviderEventProcessingStatus.Processed);
    }

    private async Task<ClientSubscription?> ResolveSubscriptionByPaymentReferenceAsync(string? referenceId, CancellationToken cancellationToken)
    {
        return Guid.TryParse(referenceId, out var subscriptionId)
            ? await _db.ClientSubscriptions.FirstOrDefaultAsync(x => x.Id == subscriptionId, cancellationToken)
            : null;
    }

    private async Task<SubscriptionPayment?> FindOpenSubscriptionPaymentAsync(
        Guid subscriptionId,
        BillingProvider provider,
        BillingProviderEnvironment environment,
        CancellationToken cancellationToken)
    {
        return await _db.SubscriptionPayments
            .Include(x => x.ClientSubscription)
            .Where(x => x.ClientSubscriptionId == subscriptionId &&
                        x.Provider == provider &&
                        x.ProviderEnvironment == environment &&
                        x.ProviderPaymentId == null &&
                        (x.Status == SubscriptionPaymentStatus.Pending || x.Status == SubscriptionPaymentStatus.Processing))
            .OrderByDescending(x => x.CreatedUtc)
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
            var byPaymentId = await query.FirstOrDefaultAsync(x => x.ProviderPaymentId == providerPaymentId, cancellationToken);
            if (byPaymentId is not null)
                return byPaymentId;
        }

        if (!string.IsNullOrWhiteSpace(providerRefundId))
        {
            var byRefundId = await query.FirstOrDefaultAsync(x => x.ProviderRefundId == providerRefundId, cancellationToken);
            if (byRefundId is not null)
                return byRefundId;
        }

        if (!string.IsNullOrWhiteSpace(providerInvoiceId))
        {
            return await query
                .Where(x => x.ProviderInvoiceId == providerInvoiceId)
                .OrderByDescending(x => x.UpdatedUtc)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return null;
    }

    private SubscriptionPayment CreateSubscriptionPayment(
        ClientSubscription subscription,
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
            ClientSubscriptionId = subscription.Id,
            ClientSubscription = subscription,
            Provider = providerEvent.Provider,
            ProviderEnvironment = providerEvent.ProviderEnvironment,
            ProviderPaymentId = providerPaymentId,
            ProviderInvoiceId = providerInvoiceId,
            ProviderRefundId = providerRefundId,
            AmountCents = amountCents ?? subscription.MonthlyAmountCents,
            Currency = NormalizeCurrency(currency ?? subscription.Currency),
            Kind = SubscriptionPaymentKind.Renewal,
            AttemptNumber = 1,
            Status = SubscriptionPaymentStatus.Pending,
            BillingPeriodStartUtc = subscription.CurrentPeriodStartUtc,
            BillingPeriodEndUtc = subscription.CurrentPeriodEndUtc,
            ProviderOccurredUtc = providerOccurredUtc,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        };
        _db.SubscriptionPayments.Add(payment);
        return payment;
    }

    private static void ApplyPaymentMutation(
        SubscriptionPayment payment,
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
        payment.ProviderOccurredUtc = MaxDate(payment.ProviderOccurredUtc, providerOccurredUtc);
        payment.UpdatedUtc = nowUtc;
    }

    private static void ApplyRefundOrDisputeOutcome(ClientSubscription subscription, bool isDispute, DateTime nowUtc)
    {
        if (BillingStateMapper.IsTerminal(subscription.Status))
            return;

        subscription.Status = ClientSubscriptionStatus.Suspended;
        subscription.PaymentStanding = ClientSubscriptionPaymentStanding.Failed;
        subscription.GracePeriodEndsUtc = null;
        subscription.NextChargeAttemptUtc = null;
        subscription.UpdatedUtc = nowUtc;
        if (!isDispute && !subscription.CancelledUtc.HasValue && subscription.CancelAtPeriodEnd)
            subscription.CancelledUtc = nowUtc;
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

    private static bool IsStaleProviderTimestamp(DateTime? existingOccurredUtc, DateTime? incomingOccurredUtc) =>
        existingOccurredUtc.HasValue && incomingOccurredUtc.HasValue && incomingOccurredUtc.Value < existingOccurredUtc.Value;

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

    private ProviderEventDecision CreateExpectedResolutionDecision(BillingProviderEvent providerEvent, string safeErrorCode) =>
        providerEvent.AttemptCount >= MaxRetryableAttempts
            ? new ProviderEventDecision(BillingProviderEventProcessingStatus.ReconciliationRequired, safeErrorCode)
            : new ProviderEventDecision(BillingProviderEventProcessingStatus.Deferred, safeErrorCode);

    private ProviderEventDecision CreateRetryableFailureDecision(BillingProviderEvent providerEvent, string safeErrorCode) =>
        providerEvent.AttemptCount >= MaxRetryableAttempts
            ? new ProviderEventDecision(BillingProviderEventProcessingStatus.ReconciliationRequired, safeErrorCode)
            : new ProviderEventDecision(BillingProviderEventProcessingStatus.Failed, safeErrorCode);

    private static int CalculateRetryDelayMinutes(int attemptCount) => Math.Min(30, Math.Max(5, attemptCount * 5));

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
}
