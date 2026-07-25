using Domain.Entities;

namespace Domain.Billing;

public interface IBillingGateway
{
    BillingProvider Provider { get; }
    BillingProviderEnvironment Environment { get; }

    Task<BillingOneTimePaymentResult> CreateOneTimePaymentAsync(BillingOneTimePaymentRequest request, CancellationToken cancellationToken = default);
    Task<BillingCustomerResolutionResult> ResolveCustomerAsync(BillingCustomerResolutionRequest request, CancellationToken cancellationToken = default);
    Task<BillingPaymentMethodAttachmentResult> AttachPaymentMethodAsync(BillingPaymentMethodAttachmentRequest request, CancellationToken cancellationToken = default);
    Task<BillingPaymentMethodDisableResult> DisablePaymentMethodAsync(BillingPaymentMethodDisableRequest request, CancellationToken cancellationToken = default);
    Task<BillingPaymentResult> GetPaymentAsync(BillingPaymentLookupRequest request, CancellationToken cancellationToken = default);
    Task<BillingPaymentResult> GetRefundAsync(BillingRefundLookupRequest request, CancellationToken cancellationToken = default);
}

public interface IBillingOrchestrator
{
    Task<ClientSubscriptionOffer> CreateClientSubscriptionOfferAsync(CreateClientSubscriptionOfferCommand command, CancellationToken cancellationToken = default);
    Task<CreateSubscriptionActivationInvitationResult> CreateSubscriptionActivationInvitationAsync(CreateSubscriptionActivationInvitationCommand command, CancellationToken cancellationToken = default);
    Task<SubscriptionActivationInvitation> MarkSubscriptionActivationInvitationSentAsync(MarkSubscriptionActivationInvitationSentCommand command, CancellationToken cancellationToken = default);
    Task<SubscriptionActivationInvitation> MarkSubscriptionActivationInvitationSendFailureAsync(MarkSubscriptionActivationInvitationSendFailureCommand command, CancellationToken cancellationToken = default);
    Task<SubscriptionActivationInvitation> RevokeSubscriptionActivationInvitationAsync(RevokeSubscriptionActivationInvitationCommand command, CancellationToken cancellationToken = default);
    Task<ExecuteCommerceOneTimePaymentResult> ExecuteCommerceOneTimePaymentAsync(ExecuteCommerceOneTimePaymentCommand command, CancellationToken cancellationToken = default);
    Task<ActivateClientSubscriptionResult> ActivateClientSubscriptionAsync(ActivateClientSubscriptionCommand command, CancellationToken cancellationToken = default);
    Task<CancelClientSubscriptionResult> CancelClientSubscriptionAsync(CancelClientSubscriptionCommand command, CancellationToken cancellationToken = default);
    Task<ManualClientSubscriptionRenewalRetryResult> RetryClientSubscriptionRenewalAsync(ManualClientSubscriptionRenewalRetryCommand command, CancellationToken cancellationToken = default);
    Task<PlatformRecurringBillingRunResult> ProcessDueClientSubscriptionRenewalsAsync(int maxItems, string workerId, CancellationToken cancellationToken = default);
}

public interface IClientSubscriptionActivationPolicyService
{
    ClientSubscriptionActivationSchedule ResolveActivationSchedule(ClientSubscriptionOffer offer, DateTime nowUtc);
    ClientSubscriptionRenewalSchedule ResolveRenewalSchedule(ClientSubscription subscription);
    TimeSpan? ResolveRenewalRetryDelay(int failedAttemptNumber);
    DateTime ResolveGracePeriodEndUtc(DateTime failureUtc);
    int ResolveUpcomingRenewalReminderDays();
    int ResolveGracePeriodReminderDaysBeforeEnd();
    int ResolveGracePeriodFinalReminderDaysBeforeEnd();
}

public interface IBillingEntitlementService
{
    Task<BillingEntitlementEvaluationResult> EvaluateAsync(BillingEntitlementEvaluationRequest request, CancellationToken cancellationToken = default);
    Task<ClientEntitlement> RefreshAsync(Guid clientProfileId, string entitlementKey, string? reasonCode = null, CancellationToken cancellationToken = default);
}

public interface IClientPaymentMethodService
{
    Task<IReadOnlyList<ClientPaymentMethod>> ListAsync(Guid clientProfileId, CancellationToken cancellationToken = default);
    Task<ClientPaymentMethodOperationResult> AddAsync(AddClientPaymentMethodCommand command, CancellationToken cancellationToken = default);
    Task<ClientPaymentMethodOperationResult> SetDefaultAsync(SetDefaultClientPaymentMethodCommand command, CancellationToken cancellationToken = default);
    Task<ClientPaymentMethodOperationResult> RenameAsync(RenameClientPaymentMethodCommand command, CancellationToken cancellationToken = default);
    Task<ClientPaymentMethodOperationResult> RemoveAsync(RemoveClientPaymentMethodCommand command, CancellationToken cancellationToken = default);
}

public interface IClientBillingNotificationService
{
    void Queue(ClientBillingNotificationRequest request);
}

public interface IBillingProviderEventProcessor
{
    Task<BillingProviderEventProcessResult> ProcessAsync(BillingProviderEventEnvelope envelope, CancellationToken cancellationToken = default);
}

public interface IBillingReconciliationService
{
    Task<ClientSubscription?> ReconcileSubscriptionAsync(Guid clientSubscriptionId, string? correlationId = null, CancellationToken cancellationToken = default);
    Task<int> ReconcilePendingProviderEventsAsync(int maxItems, CancellationToken cancellationToken = default);
}

public interface IBillingWebhookSignatureValidator
{
    Task<BillingWebhookSignatureValidationResult> ValidateAsync(BillingWebhookSignatureValidationRequest request, CancellationToken cancellationToken = default);
}

public interface IBillingWebhookIngressService
{
    Task<BillingWebhookIngressResult> IngestAsync(BillingWebhookIngressCommand command, CancellationToken cancellationToken = default);
}
