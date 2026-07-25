using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Billing;

internal sealed class ClientPaymentMethodService : IClientPaymentMethodService
{
    private readonly MasterAppDbContext _db;
    private readonly IBillingGateway _gateway;
    private readonly IClientBillingNotificationService? _notifications;

    public ClientPaymentMethodService(
        MasterAppDbContext db,
        IBillingGateway gateway,
        IClientBillingNotificationService? notifications = null)
    {
        _db = db;
        _gateway = gateway;
        _notifications = notifications;
    }

    public async Task<IReadOnlyList<ClientPaymentMethod>> ListAsync(Guid clientProfileId, CancellationToken cancellationToken = default)
    {
        return await _db.ClientPaymentMethods
            .AsNoTracking()
            .Where(paymentMethod => paymentMethod.ClientProfileId == clientProfileId && paymentMethod.RetiredUtc == null)
            .OrderByDescending(paymentMethod => _db.ClientSubscriptions.Any(subscription =>
                subscription.ClientProfileId == clientProfileId &&
                subscription.DefaultPaymentMethodId == paymentMethod.Id))
            .ThenByDescending(paymentMethod => paymentMethod.CreatedUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<ClientPaymentMethodOperationResult> AddAsync(AddClientPaymentMethodCommand command, CancellationToken cancellationToken = default)
    {
        if (command.ActorType != BillingActorType.Client)
            return Failure("PAYMENT_METHOD_CLIENT_SELF_SERVICE_REQUIRED", "Only the client can manage saved payment methods.");

        if (string.IsNullOrWhiteSpace(command.SourceId))
            return Failure("PAYMENT_METHOD_SOURCE_REQUIRED", "Enter your payment details before saving the payment method.");

        var subscription = await GetManageableSubscriptionAsync(command.ClientProfileId, command.ClientSubscriptionId, cancellationToken);
        if (subscription is null)
            return Failure("SUBSCRIPTION_PAYMENT_METHOD_UNAVAILABLE", "Payment methods can be updated for an active membership or while payment needs attention.");

        if (string.IsNullOrWhiteSpace(subscription.ProviderCustomerId))
            return Failure("PROVIDER_CUSTOMER_MISSING", "Your payment profile is not ready yet. Please contact support if this continues.");

        var nowUtc = DateTime.UtcNow;
        BillingPaymentMethodAttachmentResult attachment;
        try
        {
            attachment = await _gateway.AttachPaymentMethodAsync(
                new BillingPaymentMethodAttachmentRequest(
                    subscription.ProviderCustomerId,
                    command.SourceId,
                    BillingIdempotency.CreateDeterministic("billing-payment-method", subscription.Id.ToString(), Guid.NewGuid().ToString("N")),
                    NormalizeCardholderName(command.CardholderName),
                    subscription.Id.ToString(),
                    null,
                    command.CorrelationId,
                    command.BillingAddress),
                cancellationToken);
        }
        catch (Exception)
        {
            return Failure("PAYMENT_METHOD_PROVIDER_UNAVAILABLE", "We could not save that payment method right now. Please try again.");
        }

        if (!attachment.Success || string.IsNullOrWhiteSpace(attachment.ProviderPaymentMethodId))
            return Failure(
                attachment.SafeErrorCode ?? "PAYMENT_METHOD_SAVE_FAILED",
                attachment.SanitizedSummary ?? "We could not save that payment method. Please review the details and try again.");

        var paymentMethod = ClientPaymentMethodFactory.Create(
            subscription,
            attachment,
            command.BillingAddress,
            command.DisplayName,
            nowUtc);
        _db.ClientPaymentMethods.Add(paymentMethod);

        var shouldSetDefault = command.MakeDefault || !subscription.DefaultPaymentMethodId.HasValue;
        if (shouldSetDefault)
            subscription.DefaultPaymentMethodId = paymentMethod.Id;

        subscription.UpdatedUtc = nowUtc;
        AddAuditEntry(
            paymentMethod.Id,
            shouldSetDefault ? "payment_method_added_as_default" : "payment_method_added",
            command.ActorType,
            command.ActorId,
            command.CorrelationId,
            shouldSetDefault ? "A payment method was added and made the default." : "A backup payment method was added.");
        QueuePaymentMethodNotification(subscription, $"payment-method-added:{paymentMethod.Id:N}");

        await _db.SaveChangesAsync(cancellationToken);
        return new ClientPaymentMethodOperationResult(
            true,
            null,
            shouldSetDefault ? "Payment method saved as your default." : "Backup payment method saved.",
            paymentMethod,
            shouldSetDefault);
    }

    public async Task<ClientPaymentMethodOperationResult> SetDefaultAsync(SetDefaultClientPaymentMethodCommand command, CancellationToken cancellationToken = default)
    {
        if (command.ActorType != BillingActorType.Client)
            return Failure("PAYMENT_METHOD_CLIENT_SELF_SERVICE_REQUIRED", "Only the client can manage saved payment methods.");

        var subscription = await GetManageableSubscriptionAsync(command.ClientProfileId, command.ClientSubscriptionId, cancellationToken);
        if (subscription is null)
            return Failure("SUBSCRIPTION_PAYMENT_METHOD_UNAVAILABLE", "Payment methods can be updated for an active membership or while payment needs attention.");

        var paymentMethod = await GetActivePaymentMethodAsync(command.ClientProfileId, command.PaymentMethodId, subscription, cancellationToken);
        if (paymentMethod is null)
            return Failure("PAYMENT_METHOD_NOT_FOUND", "That payment method is not available for this membership.");

        if (subscription.DefaultPaymentMethodId == paymentMethod.Id)
            return new ClientPaymentMethodOperationResult(true, null, "This payment method is already your default.", paymentMethod);

        subscription.DefaultPaymentMethodId = paymentMethod.Id;
        subscription.UpdatedUtc = DateTime.UtcNow;
        AddAuditEntry(paymentMethod.Id, "payment_method_default_changed", command.ActorType, command.ActorId, command.CorrelationId, "The default payment method was updated.");
        QueuePaymentMethodNotification(subscription, $"payment-method-default-changed:{paymentMethod.Id:N}");
        await _db.SaveChangesAsync(cancellationToken);
        return new ClientPaymentMethodOperationResult(true, null, "Default payment method updated.", paymentMethod, true);
    }

    public async Task<ClientPaymentMethodOperationResult> RenameAsync(RenameClientPaymentMethodCommand command, CancellationToken cancellationToken = default)
    {
        if (command.ActorType != BillingActorType.Client)
            return Failure("PAYMENT_METHOD_CLIENT_SELF_SERVICE_REQUIRED", "Only the client can manage saved payment methods.");

        var paymentMethod = await _db.ClientPaymentMethods.FirstOrDefaultAsync(
            item => item.Id == command.PaymentMethodId && item.ClientProfileId == command.ClientProfileId && item.RetiredUtc == null,
            cancellationToken);
        if (paymentMethod is null)
            return Failure("PAYMENT_METHOD_NOT_FOUND", "That payment method is not available.");

        paymentMethod.DisplayName = ClientPaymentMethodFactory.NormalizeDisplayName(command.DisplayName);
        paymentMethod.UpdatedUtc = DateTime.UtcNow;
        AddAuditEntry(paymentMethod.Id, "payment_method_renamed", command.ActorType, command.ActorId, command.CorrelationId, "The payment method label was updated.");
        QueuePaymentMethodNotificationForProfile(command.ClientProfileId, $"payment-method-renamed:{paymentMethod.Id:N}");
        await _db.SaveChangesAsync(cancellationToken);
        return new ClientPaymentMethodOperationResult(true, null, "Payment method label updated.", paymentMethod);
    }

    public async Task<ClientPaymentMethodOperationResult> RemoveAsync(RemoveClientPaymentMethodCommand command, CancellationToken cancellationToken = default)
    {
        if (command.ActorType != BillingActorType.Client)
            return Failure("PAYMENT_METHOD_CLIENT_SELF_SERVICE_REQUIRED", "Only the client can manage saved payment methods.");

        var subscription = await _db.ClientSubscriptions.FirstOrDefaultAsync(
            item => item.Id == command.ClientSubscriptionId && item.ClientProfileId == command.ClientProfileId,
            cancellationToken);
        if (subscription is null)
            return Failure("SUBSCRIPTION_NOT_FOUND", "The membership was not found.");

        var paymentMethod = await GetActivePaymentMethodAsync(command.ClientProfileId, command.PaymentMethodId, subscription, cancellationToken);
        if (paymentMethod is null)
            return Failure("PAYMENT_METHOD_NOT_FOUND", "That payment method is not available for this membership.");

        ClientPaymentMethod? replacement = null;
        if (subscription.DefaultPaymentMethodId == paymentMethod.Id)
        {
            replacement = await _db.ClientPaymentMethods
                .Where(item => item.ClientProfileId == command.ClientProfileId &&
                               item.Id != paymentMethod.Id &&
                               item.Provider == subscription.Provider &&
                               item.ProviderEnvironment == subscription.ProviderEnvironment &&
                               item.RetiredUtc == null)
                .OrderByDescending(item => item.CreatedUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if ((subscription.Status is ClientSubscriptionStatus.Active or ClientSubscriptionStatus.GracePeriod) && replacement is null)
            {
                return Failure(
                    "DEFAULT_PAYMENT_METHOD_REQUIRED",
                    "Add another payment method before removing the current default so your membership can continue.");
            }
        }

        BillingPaymentMethodDisableResult disabled;
        try
        {
            disabled = await _gateway.DisablePaymentMethodAsync(
                new BillingPaymentMethodDisableRequest(paymentMethod.ProviderPaymentMethodId, command.CorrelationId),
                cancellationToken);
        }
        catch (Exception)
        {
            return Failure("PAYMENT_METHOD_PROVIDER_UNAVAILABLE", "We could not remove that payment method right now. Please try again.");
        }

        if (!disabled.Success)
            return Failure(
                disabled.SafeErrorCode ?? "PAYMENT_METHOD_REMOVE_FAILED",
                disabled.SanitizedSummary ?? "We could not remove that payment method. Please try again.");

        var nowUtc = DateTime.UtcNow;
        paymentMethod.RetiredUtc = nowUtc;
        paymentMethod.UpdatedUtc = nowUtc;
        if (subscription.DefaultPaymentMethodId == paymentMethod.Id)
            subscription.DefaultPaymentMethodId = replacement?.Id;
        subscription.UpdatedUtc = nowUtc;

        AddAuditEntry(paymentMethod.Id, "payment_method_removed", command.ActorType, command.ActorId, command.CorrelationId, "The payment method was removed.");
        QueuePaymentMethodNotification(subscription, $"payment-method-removed:{paymentMethod.Id:N}");
        await _db.SaveChangesAsync(cancellationToken);
        return new ClientPaymentMethodOperationResult(true, null, "Payment method removed.", paymentMethod, replacement is not null);
    }

    private async Task<ClientSubscription?> GetManageableSubscriptionAsync(Guid clientProfileId, Guid subscriptionId, CancellationToken cancellationToken)
    {
        return await _db.ClientSubscriptions.FirstOrDefaultAsync(
            subscription => subscription.Id == subscriptionId &&
                            subscription.ClientProfileId == clientProfileId &&
                            subscription.IsPlatformManaged &&
                            (subscription.Status == ClientSubscriptionStatus.Active || subscription.Status == ClientSubscriptionStatus.GracePeriod),
            cancellationToken);
    }

    private Task<ClientPaymentMethod?> GetActivePaymentMethodAsync(
        Guid clientProfileId,
        Guid paymentMethodId,
        ClientSubscription subscription,
        CancellationToken cancellationToken)
    {
        return _db.ClientPaymentMethods.FirstOrDefaultAsync(
            item => item.Id == paymentMethodId &&
                    item.ClientProfileId == clientProfileId &&
                    item.Provider == subscription.Provider &&
                    item.ProviderEnvironment == subscription.ProviderEnvironment &&
                    item.RetiredUtc == null,
            cancellationToken);
    }

    private void AddAuditEntry(
        Guid paymentMethodId,
        string action,
        BillingActorType actorType,
        string? actorId,
        string? correlationId,
        string summary)
    {
        _db.BillingAuditEntries.Add(new BillingAuditEntry
        {
            EntityType = nameof(ClientPaymentMethod),
            EntityId = paymentMethodId.ToString(),
            Action = action,
            ActorType = actorType,
            ActorId = actorId,
            Source = "client_payment_method_service",
            CorrelationId = correlationId,
            OccurredUtc = DateTime.UtcNow,
            SanitizedMetadataJson = $$"""{"summary":"{{summary}}"}"""
        });
    }

    private void QueuePaymentMethodNotification(ClientSubscription subscription, string eventKey)
    {
        _notifications?.Queue(new ClientBillingNotificationRequest(
            subscription.ClientProfileId,
            subscription.Id,
            ClientBillingNotificationKind.PaymentMethodUpdated,
            eventKey));
    }

    private void QueuePaymentMethodNotificationForProfile(Guid clientProfileId, string eventKey)
    {
        var subscriptionId = _db.ClientSubscriptions
            .Where(subscription => subscription.ClientProfileId == clientProfileId)
            .OrderByDescending(subscription => subscription.UpdatedUtc)
            .Select(subscription => (Guid?)subscription.Id)
            .FirstOrDefault();
        if (!subscriptionId.HasValue)
            return;

        _notifications?.Queue(new ClientBillingNotificationRequest(
            clientProfileId,
            subscriptionId.Value,
            ClientBillingNotificationKind.PaymentMethodUpdated,
            eventKey));
    }

    private static ClientPaymentMethodOperationResult Failure(string safeErrorCode, string summary) =>
        new(false, safeErrorCode, summary);

    private static string? NormalizeCardholderName(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
