using Domain.Entities;

namespace Domain.Billing;

public sealed record BillingMoney(int AmountCents, string Currency);

public sealed record BillingPostalAddress(
    string? AddressLine1,
    string? AddressLine2,
    string? Locality,
    string? AdministrativeDistrictLevel1,
    string? PostalCode,
    string? Country);

public record BillingProviderResult(
    bool Success,
    string? ExternalId,
    string? NormalizedStatus,
    string? SafeErrorCode,
    string? SanitizedSummary,
    string? ProviderRequestId,
    bool Retryable);

public sealed record BillingOneTimePaymentRequest(
    string SourceId,
    int AmountCents,
    string Currency,
    string Note,
    string IdempotencyKey,
    string? CorrelationId = null,
    string? OrderReference = null,
    string? ExistingProviderCustomerId = null);

public sealed record BillingOneTimePaymentResult(
    bool Success,
    string? ExternalId,
    string? NormalizedStatus,
    string? SafeErrorCode,
    string? SanitizedSummary,
    string? ProviderRequestId,
    bool Retryable,
    int? AmountCents = null,
    string? Currency = null,
    DateTime? ProviderOccurredUtc = null)
    : BillingProviderResult(Success, ExternalId, NormalizedStatus, SafeErrorCode, SanitizedSummary, ProviderRequestId, Retryable);

public sealed record BillingCustomerProfileInput(
    string? GivenName,
    string? FamilyName,
    string? Email,
    string? Phone,
    string? ReferenceId,
    string? Note,
    BillingPostalAddress? Address = null);

public sealed record BillingCustomerResolutionRequest(
    string? ExistingProviderCustomerId,
    BillingCustomerProfileInput Customer,
    string? IdempotencyKey = null,
    string? CorrelationId = null);

public sealed record BillingCustomerResolutionResult(
    bool Success,
    string? ExternalId,
    string? NormalizedStatus,
    string? SafeErrorCode,
    string? SanitizedSummary,
    string? ProviderRequestId,
    bool Retryable,
    string? ProviderCustomerId = null)
    : BillingProviderResult(Success, ExternalId, NormalizedStatus, SafeErrorCode, SanitizedSummary, ProviderRequestId, Retryable);

public sealed record BillingPaymentMethodAttachmentRequest(
    string ProviderCustomerId,
    string SourceId,
    string IdempotencyKey,
    string? CardholderName = null,
    string? ReferenceId = null,
    string? VerificationToken = null,
    string? CorrelationId = null,
    BillingPostalAddress? BillingAddress = null);

public sealed record BillingPaymentMethodAttachmentResult(
    bool Success,
    string? ExternalId,
    string? NormalizedStatus,
    string? SafeErrorCode,
    string? SanitizedSummary,
    string? ProviderRequestId,
    bool Retryable,
    string? ProviderCustomerId = null,
    string? ProviderPaymentMethodId = null)
    : BillingProviderResult(Success, ExternalId, NormalizedStatus, SafeErrorCode, SanitizedSummary, ProviderRequestId, Retryable);

public sealed record BillingSubscriptionCreateRequest(
    string ProviderCustomerId,
    string? ProviderPaymentMethodId,
    string ProviderPlanVariationId,
    int AmountCents,
    string Currency,
    int? BillingAnchorDay,
    DateOnly? StartDateLocal,
    string IdempotencyKey,
    string? CorrelationId = null);

public sealed record BillingSubscriptionResult(
    bool Success,
    string? ExternalId,
    string? NormalizedStatus,
    string? SafeErrorCode,
    string? SanitizedSummary,
    string? ProviderRequestId,
    bool Retryable,
    string? ProviderCustomerId = null,
    string? ProviderPaymentMethodId = null,
    string? ProviderPlanVariationId = null,
    int? AmountCents = null,
    string? Currency = null,
    int? BillingAnchorDay = null,
    DateTime? CurrentPeriodStartUtc = null,
    DateTime? CurrentPeriodEndUtc = null,
    DateTime? NextBillingDateUtc = null,
    bool? CancelAtPeriodEnd = null)
    : BillingProviderResult(Success, ExternalId, NormalizedStatus, SafeErrorCode, SanitizedSummary, ProviderRequestId, Retryable);

public sealed record BillingSubscriptionCancellationRequest(
    string ProviderSubscriptionId,
    string IdempotencyKey,
    bool CancelAtPeriodEnd,
    string? CorrelationId = null);

public sealed record BillingPaymentLookupRequest(
    string ProviderPaymentId,
    string? CorrelationId = null);

public sealed record BillingRefundLookupRequest(
    string ProviderRefundId,
    string? CorrelationId = null);

public sealed record BillingPaymentResult(
    bool Success,
    string? ExternalId,
    string? NormalizedStatus,
    string? SafeErrorCode,
    string? SanitizedSummary,
    string? ProviderRequestId,
    bool Retryable,
    string? ProviderInvoiceId = null,
    string? ProviderRefundId = null,
    int? AmountCents = null,
    string? Currency = null,
    DateTime? ProviderOccurredUtc = null)
    : BillingProviderResult(Success, ExternalId, NormalizedStatus, SafeErrorCode, SanitizedSummary, ProviderRequestId, Retryable);

public sealed record BillingProviderEventEnvelope(
    BillingProvider Provider,
    BillingProviderEnvironment ProviderEnvironment,
    string ProviderEventId,
    string EventType,
    string PayloadJson,
    DateTime ReceivedUtc,
    string? ProviderObjectId = null,
    string? Signature = null,
    string? CorrelationId = null);

public sealed record BillingProviderEventProcessResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedSummary,
    bool Retryable,
    BillingProviderEvent? EventRecord = null);

public sealed record BillingEntitlementEvaluationRequest(
    Guid ClientProfileId,
    string EntitlementKey,
    DateTime EvaluatedUtc,
    string? CorrelationId = null);

public sealed record BillingEntitlementEvaluationResult(
    ClientEntitlementStatus Status,
    DateTime? EffectiveUtc,
    DateTime? ExpirationUtc,
    DateTime? GraceOrSuspensionUtc,
    string? ReasonCode,
    ClientEntitlementSourceType SourceType,
    string SourceId,
    string Summary);

public sealed record ClientSubscriptionActivationSchedule(
    int MonthlyAmountCents,
    string Currency,
    int? BillingAnchorDay,
    string BillingTimeZoneId,
    int MinimumAnchorIntervalDays,
    int SameDayCutoffHourLocal,
    DateTime FirstChargeUtc,
    DateTime FirstRecurringRenewalUtc,
    DateOnly FirstRecurringRenewalLocalDate);

public sealed record CreateClientSubscriptionOfferCommand(
    Guid ClientProfileId,
    string OwnerAgentUserId,
    ClientSubscriptionOfferPriceType PriceType,
    int? CustomMonthlyAmountCents,
    string Currency,
    BillingAnchorSelectionMode BillingAnchorSelectionMode,
    int? SelectedBillingAnchorDay,
    DateTime? EffectiveUtc,
    DateTime? ExpiresUtc,
    bool AllowFounderZeroDollarCustomAmount = false);

public sealed record CreateSubscriptionActivationInvitationCommand(
    Guid ClientProfileId,
    Guid ClientSubscriptionOfferId,
    string IntendedEmail,
    string CreatedByAgentUserId,
    DateTime? ExpiresUtc);

public sealed record MarkSubscriptionActivationInvitationSentCommand(
    Guid InvitationId,
    string ActorId,
    string? CorrelationId = null);

public sealed record MarkSubscriptionActivationInvitationSendFailureCommand(
    Guid InvitationId,
    string ActorId,
    string SafeErrorCode,
    string SanitizedSummary,
    string? CorrelationId = null);

public sealed record RevokeSubscriptionActivationInvitationCommand(
    Guid InvitationId,
    string RevokedByAgentUserId,
    string? CorrelationId = null);

public sealed record ActivateClientSubscriptionCommand(
    Guid ClientProfileId,
    Guid ClientSubscriptionOfferId,
    string OwnerAgentUserId,
    string SourceId,
    string Currency,
    string? ProviderPlanVariationId,
    int? BillingAnchorDay,
    string BillingTimeZoneId,
    DateTime FirstChargeUtc,
    DateTime FirstRecurringRenewalUtc,
    DateOnly FirstRecurringRenewalLocalDate,
    bool RecurringAuthorizationAccepted,
    bool CardOnFileConsentAccepted,
    bool CancellationTermsAccepted,
    string IntendedNormalizedEmail,
    Guid? InvitationId = null,
    string? ExistingProviderCustomerId = null,
    string? CardholderName = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null,
    BillingPostalAddress? BillingAddress = null);

public sealed record ExecuteCommerceOneTimePaymentCommand(
    string SourceId,
    int AmountCents,
    string Currency,
    string Note,
    string IdempotencyKey,
    Guid? CommerceOrderId = null,
    string? CorrelationId = null,
    string? ExistingProviderCustomerId = null);

public sealed record CancelClientSubscriptionCommand(
    Guid ClientSubscriptionId,
    bool CancelAtPeriodEnd,
    BillingActorType ActorType,
    string? ActorId,
    string? CorrelationId = null);

public sealed record BillingWebhookSignatureValidationRequest(
    BillingProvider Provider,
    string NotificationUrl,
    string PayloadJson,
    string? Signature);

public sealed record BillingWebhookSignatureValidationResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedSummary);

public sealed record BillingWebhookIngressCommand(
    BillingProvider Provider,
    string NotificationUrl,
    string PayloadJson,
    string? Signature,
    string? CorrelationId = null);

public sealed record BillingWebhookIngressResult(
    bool Success,
    int StatusCode,
    string? SafeErrorCode,
    string? SanitizedSummary,
    BillingProviderEvent? EventRecord = null);

public record BillingWorkflowResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedSummary,
    string? ProviderRequestId,
    bool Retryable);

public sealed record CreateSubscriptionActivationInvitationResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedSummary,
    string? ProviderRequestId,
    bool Retryable,
    SubscriptionActivationInvitation? Invitation,
    string? PlainTextToken)
    : BillingWorkflowResult(Success, SafeErrorCode, SanitizedSummary, ProviderRequestId, Retryable);

public sealed record ExecuteCommerceOneTimePaymentResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedSummary,
    string? ProviderRequestId,
    bool Retryable,
    SubscriptionPayment? PaymentRecord,
    BillingOneTimePaymentResult ProviderResult)
    : BillingWorkflowResult(Success, SafeErrorCode, SanitizedSummary, ProviderRequestId, Retryable);

public sealed record ActivateClientSubscriptionResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedSummary,
    string? ProviderRequestId,
    bool Retryable,
    ClientSubscription? Subscription,
    ClientEntitlement? Entitlement,
    BillingSubscriptionResult ProviderResult)
    : BillingWorkflowResult(Success, SafeErrorCode, SanitizedSummary, ProviderRequestId, Retryable);

public sealed record CancelClientSubscriptionResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedSummary,
    string? ProviderRequestId,
    bool Retryable,
    ClientSubscription? Subscription,
    ClientEntitlement? Entitlement,
    BillingSubscriptionResult ProviderResult)
    : BillingWorkflowResult(Success, SafeErrorCode, SanitizedSummary, ProviderRequestId, Retryable);
