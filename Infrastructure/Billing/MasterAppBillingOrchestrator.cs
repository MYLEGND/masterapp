using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Billing;

internal sealed class MasterAppBillingOrchestrator : IBillingOrchestrator
{
    private readonly MasterAppDbContext _db;
    private readonly IBillingGateway _gateway;
    private readonly IBillingEntitlementService _entitlements;
    private readonly IClientSubscriptionActivationPolicyService _billingPolicy;

    public MasterAppBillingOrchestrator(
        MasterAppDbContext db,
        IBillingGateway gateway,
        IBillingEntitlementService entitlements,
        IClientSubscriptionActivationPolicyService billingPolicy)
    {
        _db = db;
        _gateway = gateway;
        _entitlements = entitlements;
        _billingPolicy = billingPolicy;
    }

    public async Task<ClientSubscriptionOffer> CreateClientSubscriptionOfferAsync(CreateClientSubscriptionOfferCommand command, CancellationToken cancellationToken = default)
    {
        await EnsureClientExistsAsync(command.ClientProfileId, cancellationToken);

        var nowUtc = DateTime.UtcNow;
        var authoritativeAmount = ClientSubscriptionOfferPricing.ResolveAuthoritativeMonthlyAmountCents(
            command.PriceType,
            command.CustomMonthlyAmountCents,
            command.AllowFounderZeroDollarCustomAmount
                ? ClientSubscriptionOfferPricing.FounderCustomMinimumCents
                : ClientSubscriptionOfferPricing.CustomMinimumCents);
        var resolvedBillingAnchorDay = ClientSubscriptionOfferPricing.ResolveBillingAnchorDay(
            command.BillingAnchorSelectionMode,
            command.SelectedBillingAnchorDay);

        var existingOffers = await _db.ClientSubscriptionOffers
            .Where(x => x.ClientProfileId == command.ClientProfileId &&
                        (x.Status == ClientSubscriptionOfferStatus.Draft || x.Status == ClientSubscriptionOfferStatus.Offered))
            .ToListAsync(cancellationToken);

        foreach (var existing in existingOffers)
        {
            existing.Status = ClientSubscriptionOfferStatus.Superseded;
            existing.UpdatedUtc = nowUtc;
        }

        var offer = new ClientSubscriptionOffer
        {
            ClientProfileId = command.ClientProfileId,
            OwnerAgentUserId = command.OwnerAgentUserId.Trim(),
            PriceType = command.PriceType,
            MonthlyAmountCents = authoritativeAmount,
            Currency = NormalizeCurrency(command.Currency),
            BillingAnchorSelectionMode = command.BillingAnchorSelectionMode,
            SelectedBillingAnchorDay = resolvedBillingAnchorDay,
            Status = command.EffectiveUtc.HasValue && command.EffectiveUtc.Value > nowUtc
                ? ClientSubscriptionOfferStatus.Draft
                : ClientSubscriptionOfferStatus.Offered,
            EffectiveUtc = command.EffectiveUtc,
            ExpiresUtc = command.ExpiresUtc,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        };

        _db.ClientSubscriptionOffers.Add(offer);
        AddAuditEntry("ClientSubscriptionOffer", offer.Id, "created", null, offer.Status.ToString(), BillingActorType.Agent, command.OwnerAgentUserId, "billing_orchestrator", null, null);

        await _db.SaveChangesAsync(cancellationToken);
        return offer;
    }

    public async Task<CreateSubscriptionActivationInvitationResult> CreateSubscriptionActivationInvitationAsync(CreateSubscriptionActivationInvitationCommand command, CancellationToken cancellationToken = default)
    {
        var client = await EnsureClientExistsAsync(command.ClientProfileId, cancellationToken);
        var offer = await _db.ClientSubscriptionOffers.FirstOrDefaultAsync(x => x.Id == command.ClientSubscriptionOfferId, cancellationToken)
            ?? throw new InvalidOperationException($"Client subscription offer {command.ClientSubscriptionOfferId} was not found.");

        if (offer.ClientProfileId != command.ClientProfileId)
            throw new InvalidOperationException("The selected offer does not belong to the requested client.");

        var nowUtc = DateTime.UtcNow;
        var normalizedEmail = NormalizeEmail(command.IntendedEmail);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
            normalizedEmail = NormalizeEmail(client.Email);

        if (string.IsNullOrWhiteSpace(normalizedEmail))
            throw new InvalidOperationException("A normalized invitation email is required.");

        var activeInvitations = await _db.SubscriptionActivationInvitations
            .Where(x => x.ClientProfileId == command.ClientProfileId &&
                        x.ClientSubscriptionOfferId == command.ClientSubscriptionOfferId &&
                        (x.Status == SubscriptionActivationInvitationStatus.Pending ||
                         x.Status == SubscriptionActivationInvitationStatus.Sent ||
                         x.Status == SubscriptionActivationInvitationStatus.Viewed ||
                         x.Status == SubscriptionActivationInvitationStatus.PaymentStarted))
            .ToListAsync(cancellationToken);

        foreach (var active in activeInvitations)
        {
            active.Status = SubscriptionActivationInvitationStatus.Superseded;
            active.SupersededUtc = nowUtc;
        }

        var plainTextToken = BillingIdempotency.CreateOpaqueToken();
        var invitation = new SubscriptionActivationInvitation
        {
            ClientProfileId = command.ClientProfileId,
            ClientSubscriptionOfferId = command.ClientSubscriptionOfferId,
            TokenHash = BillingIdempotency.Hash(plainTextToken),
            IntendedNormalizedEmail = normalizedEmail,
            Status = SubscriptionActivationInvitationStatus.Pending,
            ExpiresUtc = command.ExpiresUtc ?? nowUtc.AddDays(7),
            CreatedByAgentUserId = command.CreatedByAgentUserId.Trim(),
            CreatedUtc = nowUtc,
            SendCount = 0
        };

        _db.SubscriptionActivationInvitations.Add(invitation);
        AddAuditEntry("SubscriptionActivationInvitation", invitation.Id, "created", null, invitation.Status.ToString(), BillingActorType.Agent, command.CreatedByAgentUserId, "billing_orchestrator", null, null);

        await _db.SaveChangesAsync(cancellationToken);

        return new CreateSubscriptionActivationInvitationResult(
            true,
            null,
            "Subscription activation invitation created.",
            null,
            false,
            invitation,
            plainTextToken);
    }

    public async Task<SubscriptionActivationInvitation> MarkSubscriptionActivationInvitationSentAsync(MarkSubscriptionActivationInvitationSentCommand command, CancellationToken cancellationToken = default)
    {
        var invitation = await _db.SubscriptionActivationInvitations.FirstOrDefaultAsync(x => x.Id == command.InvitationId, cancellationToken)
            ?? throw new InvalidOperationException($"Subscription activation invitation {command.InvitationId} was not found.");

        var previousStatus = invitation.Status.ToString();
        var nowUtc = DateTime.UtcNow;

        if (invitation.Status is SubscriptionActivationInvitationStatus.Pending or SubscriptionActivationInvitationStatus.Sent)
        {
            invitation.Status = SubscriptionActivationInvitationStatus.Sent;
        }

        invitation.LastSentUtc = nowUtc;
        invitation.SendCount += 1;

        AddAuditEntry(
            "SubscriptionActivationInvitation",
            invitation.Id,
            "sent",
            previousStatus,
            invitation.Status.ToString(),
            BillingActorType.Agent,
            command.ActorId,
            "billing_orchestrator",
            null,
            command.CorrelationId);

        await _db.SaveChangesAsync(cancellationToken);
        return invitation;
    }

    public async Task<SubscriptionActivationInvitation> MarkSubscriptionActivationInvitationSendFailureAsync(MarkSubscriptionActivationInvitationSendFailureCommand command, CancellationToken cancellationToken = default)
    {
        var invitation = await _db.SubscriptionActivationInvitations.FirstOrDefaultAsync(x => x.Id == command.InvitationId, cancellationToken)
            ?? throw new InvalidOperationException($"Subscription activation invitation {command.InvitationId} was not found.");

        AddAuditEntry(
            "SubscriptionActivationInvitation",
            invitation.Id,
            "send_failed",
            invitation.Status.ToString(),
            invitation.Status.ToString(),
            BillingActorType.Agent,
            command.ActorId,
            "billing_orchestrator",
            command.SafeErrorCode,
            command.CorrelationId,
            command.SanitizedSummary);

        await _db.SaveChangesAsync(cancellationToken);
        return invitation;
    }

    public async Task<SubscriptionActivationInvitation> RevokeSubscriptionActivationInvitationAsync(RevokeSubscriptionActivationInvitationCommand command, CancellationToken cancellationToken = default)
    {
        var invitation = await _db.SubscriptionActivationInvitations.FirstOrDefaultAsync(x => x.Id == command.InvitationId, cancellationToken)
            ?? throw new InvalidOperationException($"Subscription activation invitation {command.InvitationId} was not found.");

        if (invitation.Status == SubscriptionActivationInvitationStatus.Redeemed)
            throw new InvalidOperationException("A redeemed subscription activation invitation cannot be revoked.");

        if (invitation.Status == SubscriptionActivationInvitationStatus.Revoked)
            return invitation;

        var previousStatus = invitation.Status.ToString();
        invitation.Status = SubscriptionActivationInvitationStatus.Revoked;
        invitation.RevokedUtc = DateTime.UtcNow;

        AddAuditEntry(
            "SubscriptionActivationInvitation",
            invitation.Id,
            "revoked",
            previousStatus,
            invitation.Status.ToString(),
            BillingActorType.Agent,
            command.RevokedByAgentUserId,
            "billing_orchestrator",
            null,
            command.CorrelationId);

        await _db.SaveChangesAsync(cancellationToken);
        return invitation;
    }

    public async Task<ExecuteCommerceOneTimePaymentResult> ExecuteCommerceOneTimePaymentAsync(ExecuteCommerceOneTimePaymentCommand command, CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;
        var paymentRecord = new SubscriptionPayment
        {
            CommerceOrderId = command.CommerceOrderId,
            Provider = _gateway.Provider,
            ProviderEnvironment = _gateway.Environment,
            AmountCents = command.AmountCents,
            Currency = NormalizeCurrency(command.Currency),
            Kind = SubscriptionPaymentKind.CommerceOneTime,
            AttemptNumber = 1,
            IdempotencyKey = command.IdempotencyKey,
            Status = SubscriptionPaymentStatus.Pending,
            ScheduledChargeUtc = nowUtc,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        };

        _db.SubscriptionPayments.Add(paymentRecord);
        AddAuditEntry("SubscriptionPayment", paymentRecord.Id, "created", null, paymentRecord.Status.ToString(), BillingActorType.System, null, "billing_orchestrator", null, command.CorrelationId);
        await _db.SaveChangesAsync(cancellationToken);

        BillingOneTimePaymentResult providerResult;
        try
        {
            providerResult = await _gateway.CreateOneTimePaymentAsync(
                new BillingOneTimePaymentRequest(
                    command.SourceId,
                    command.AmountCents,
                    NormalizeCurrency(command.Currency),
                    command.Note,
                    command.IdempotencyKey,
                    command.CorrelationId,
                    command.CommerceOrderId?.ToString(),
                    command.ExistingProviderCustomerId),
                cancellationToken);
        }
        catch (Exception)
        {
            paymentRecord.Status = SubscriptionPaymentStatus.Failed;
            paymentRecord.SafeFailureCode = "UNHANDLED_BILLING_ERROR";
            paymentRecord.UpdatedUtc = DateTime.UtcNow;
            AddAuditEntry(
                "SubscriptionPayment",
                paymentRecord.Id,
                "failed",
                SubscriptionPaymentStatus.Pending.ToString(),
                paymentRecord.Status.ToString(),
                BillingActorType.System,
                null,
                "billing_orchestrator",
                paymentRecord.SafeFailureCode,
                command.CorrelationId,
                "One-time payment failed before the provider returned a safe result.");
            await _db.SaveChangesAsync(cancellationToken);

            var failedResult = new BillingOneTimePaymentResult(false, null, "FAILED", "UNHANDLED_BILLING_ERROR", "One-time payment failed before the provider returned a safe result.", null, false);
            return new ExecuteCommerceOneTimePaymentResult(false, failedResult.SafeErrorCode, failedResult.SanitizedSummary, null, false, paymentRecord, failedResult);
        }

        paymentRecord.ProviderPaymentId = providerResult.ExternalId;
        paymentRecord.ProviderRequestId = providerResult.ProviderRequestId;
        paymentRecord.ProviderOccurredUtc = providerResult.ProviderOccurredUtc;
        paymentRecord.Status = BillingStateMapper.MapPaymentStatus(providerResult.NormalizedStatus);
        paymentRecord.SafeFailureCode = providerResult.SafeErrorCode;
        paymentRecord.UpdatedUtc = DateTime.UtcNow;

        AddAuditEntry(
            "SubscriptionPayment",
            paymentRecord.Id,
            providerResult.Success ? "completed" : "failed",
            SubscriptionPaymentStatus.Pending.ToString(),
            paymentRecord.Status.ToString(),
            BillingActorType.System,
            null,
            "billing_orchestrator",
            providerResult.SafeErrorCode,
            command.CorrelationId,
            providerResult.SanitizedSummary);

        await _db.SaveChangesAsync(cancellationToken);

        return new ExecuteCommerceOneTimePaymentResult(
            providerResult.Success,
            providerResult.SafeErrorCode,
            providerResult.SanitizedSummary,
            providerResult.ProviderRequestId,
            providerResult.Retryable,
            paymentRecord,
            providerResult);
    }

    public async Task<ActivateClientSubscriptionResult> ActivateClientSubscriptionAsync(ActivateClientSubscriptionCommand command, CancellationToken cancellationToken = default)
    {
        var client = await EnsureClientExistsAsync(command.ClientProfileId, cancellationToken);
        var offer = await _db.ClientSubscriptionOffers.FirstOrDefaultAsync(x => x.Id == command.ClientSubscriptionOfferId, cancellationToken)
            ?? throw new InvalidOperationException($"Client subscription offer {command.ClientSubscriptionOfferId} was not found.");

        if (offer.ClientProfileId != client.Id)
            throw new InvalidOperationException("The accepted offer does not belong to the client.");

        var nowUtc = DateTime.UtcNow;
        var authoritativeCurrency = NormalizeCurrency(offer.Currency);
        var intendedNormalizedEmail = NormalizeEmail(command.IntendedNormalizedEmail);

        if (!command.RecurringAuthorizationAccepted || !command.CardOnFileConsentAccepted || !command.CancellationTermsAccepted)
        {
            var consentFailure = new ClientSubscriptionLifecycleResult(false, "CONSENT_REQUIRED", "MISSING_REQUIRED_CONSENT", "Recurring billing consent is required before activation can continue.", null, false);
            return new ActivateClientSubscriptionResult(false, consentFailure.SafeErrorCode, consentFailure.SanitizedSummary, null, false, null, null, consentFailure);
        }

        var subscription = await GetExistingReusableSubscriptionAsync(command.ClientProfileId, command.ClientSubscriptionOfferId, cancellationToken);
        if (subscription is null)
        {
            var blockingSubscription = await FindBlockingSubscriptionAsync(command.ClientProfileId, command.ClientSubscriptionOfferId, cancellationToken);
            if (blockingSubscription is not null)
            {
                var blockingResult = new ClientSubscriptionLifecycleResult(false, blockingSubscription.Status.ToString(), "ACTIVE_SUBSCRIPTION_EXISTS", "A non-terminal subscription already exists for this client.", null, false);
                return new ActivateClientSubscriptionResult(false, blockingResult.SafeErrorCode, blockingResult.SanitizedSummary, blockingResult.ProviderRequestId, false, blockingSubscription, null, blockingResult);
            }

            subscription = new ClientSubscription
            {
                ClientProfileId = command.ClientProfileId,
                AcceptedOfferId = offer.Id,
                OwnerAgentUserId = command.OwnerAgentUserId.Trim(),
                Provider = _gateway.Provider,
                ProviderEnvironment = _gateway.Environment,
                MonthlyAmountCents = offer.MonthlyAmountCents,
                Currency = authoritativeCurrency,
                BillingTimeZoneId = string.IsNullOrWhiteSpace(command.BillingTimeZoneId) ? "UTC" : command.BillingTimeZoneId.Trim(),
                BillingAnchorDay = command.BillingAnchorDay ?? offer.SelectedBillingAnchorDay,
                Status = ClientSubscriptionStatus.AwaitingPaymentMethod,
                PaymentStanding = ClientSubscriptionPaymentStanding.Unknown,
                FirstChargeUtc = command.FirstChargeUtc,
                FirstRecurringRenewalUtc = command.FirstRecurringRenewalUtc,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc
            };

            _db.ClientSubscriptions.Add(subscription);
            AddAuditEntry("ClientSubscription", subscription.Id, "created", null, subscription.Status.ToString(), BillingActorType.Agent, command.OwnerAgentUserId, "billing_orchestrator", null, command.CorrelationId);
            await _db.SaveChangesAsync(cancellationToken);
        }
        else if (subscription.Status == ClientSubscriptionStatus.Active)
        {
            var alreadyActive = new ClientSubscriptionLifecycleResult(
                false,
                subscription.Status.ToString(),
                "ACTIVE_SUBSCRIPTION_EXISTS",
                "An active subscription already exists for this client.",
                null,
                false);
            return new ActivateClientSubscriptionResult(false, alreadyActive.SafeErrorCode, alreadyActive.SanitizedSummary, null, false, subscription, null, alreadyActive);
        }

        var pendingActivationAttempt = await _db.SubscriptionPayments
            .AnyAsync(x =>
                x.ClientSubscriptionId == subscription.Id &&
                x.Kind == SubscriptionPaymentKind.InitialActivation &&
                (x.Status == SubscriptionPaymentStatus.Pending || x.Status == SubscriptionPaymentStatus.Processing),
                cancellationToken);
        if (pendingActivationAttempt)
        {
            var pendingResult = new ClientSubscriptionLifecycleResult(
                false,
                ClientSubscriptionStatus.ReconciliationRequired.ToString(),
                "PAYMENT_OUTCOME_RECONCILIATION_PENDING",
                "The previous payment attempt is still awaiting reconciliation. No additional charge was submitted.",
                null,
                false);
            return new ActivateClientSubscriptionResult(false, pendingResult.SafeErrorCode, pendingResult.SanitizedSummary, null, false, subscription, null, pendingResult);
        }

        SubscriptionActivationInvitation? invitation = null;
        if (command.InvitationId.HasValue)
        {
            invitation = await _db.SubscriptionActivationInvitations.FirstOrDefaultAsync(x => x.Id == command.InvitationId.Value, cancellationToken)
                ?? throw new InvalidOperationException($"Subscription activation invitation {command.InvitationId.Value} was not found.");

            if (invitation.ClientProfileId != command.ClientProfileId)
                throw new InvalidOperationException("The invitation does not belong to the requested client.");

            if (invitation.ClientSubscriptionOfferId != offer.Id)
                throw new InvalidOperationException("The invitation does not belong to the requested subscription offer.");

            if (invitation.ExpiresUtc <= nowUtc)
            {
                invitation.Status = SubscriptionActivationInvitationStatus.Expired;
                await _db.SaveChangesAsync(cancellationToken);
                var expiredResult = new ClientSubscriptionLifecycleResult(false, subscription.Status.ToString(), "INVITATION_EXPIRED", "The activation invitation has expired.", null, false);
                return new ActivateClientSubscriptionResult(false, expiredResult.SafeErrorCode, expiredResult.SanitizedSummary, null, false, subscription, null, expiredResult);
            }

            if (invitation.Status is SubscriptionActivationInvitationStatus.Revoked or SubscriptionActivationInvitationStatus.Superseded)
            {
                var unavailableResult = new ClientSubscriptionLifecycleResult(false, subscription.Status.ToString(), "INVITATION_UNAVAILABLE", "The activation invitation is no longer available.", null, false);
                return new ActivateClientSubscriptionResult(false, unavailableResult.SafeErrorCode, unavailableResult.SanitizedSummary, null, false, subscription, null, unavailableResult);
            }

            if (invitation.Status == SubscriptionActivationInvitationStatus.Redeemed)
            {
                var reusedResult = new ClientSubscriptionLifecycleResult(false, subscription.Status.ToString(), "INVITATION_ALREADY_USED", "The activation invitation has already been used.", null, false);
                return new ActivateClientSubscriptionResult(false, reusedResult.SafeErrorCode, reusedResult.SanitizedSummary, null, false, subscription, null, reusedResult);
            }

            if (!string.IsNullOrWhiteSpace(intendedNormalizedEmail) &&
                !string.Equals(invitation.IntendedNormalizedEmail, intendedNormalizedEmail, StringComparison.Ordinal))
            {
                var emailMismatchResult = new ClientSubscriptionLifecycleResult(false, subscription.Status.ToString(), "INVITATION_EMAIL_MISMATCH", "The activation invitation does not match the current client email.", null, false);
                return new ActivateClientSubscriptionResult(false, emailMismatchResult.SafeErrorCode, emailMismatchResult.SanitizedSummary, null, false, subscription, null, emailMismatchResult);
            }

            invitation.Status = SubscriptionActivationInvitationStatus.PaymentStarted;
            invitation.PaymentStartedUtc = nowUtc;
            AddAuditEntry(
                "SubscriptionActivationInvitation",
                invitation.Id,
                "consent_recorded",
                null,
                invitation.Status.ToString(),
                BillingActorType.Client,
                null,
                "billing_orchestrator",
                null,
                command.CorrelationId,
                null,
                BuildConsentMetadataJson(command, authoritativeCurrency, offer.MonthlyAmountCents));
            await _db.SaveChangesAsync(cancellationToken);
        }

        if (offer.MonthlyAmountCents == 0)
        {
            return await ActivateZeroDollarSubscriptionAsync(
                subscription,
                offer,
                invitation,
                command,
                authoritativeCurrency,
                nowUtc,
                cancellationToken);
        }

        var correlationId = command.CorrelationId ?? BillingIdempotency.CreateDeterministic("client-subscription-activation", command.ClientProfileId.ToString(), offer.Id.ToString(), subscription.Id.ToString());
        var customerResult = await _gateway.ResolveCustomerAsync(
            new BillingCustomerResolutionRequest(
                command.ExistingProviderCustomerId,
                new BillingCustomerProfileInput(
                    client.FirstName,
                    client.LastName,
                    client.Email,
                    client.Phone,
                    client.Id.ToString(),
                    $"Client subscription activation {subscription.Id}",
                    command.BillingAddress),
                BillingIdempotency.CreateDeterministic("billing-customer", client.Id.ToString(), client.Email),
                correlationId),
            cancellationToken);

        if (!customerResult.Success || string.IsNullOrWhiteSpace(customerResult.ProviderCustomerId))
        {
            subscription.Status = ClientSubscriptionStatus.ActivationFailed;
            subscription.PaymentStanding = ClientSubscriptionPaymentStanding.Failed;
            subscription.UpdatedUtc = DateTime.UtcNow;
            AddAuditEntry("ClientSubscription", subscription.Id, "activation_failed", ClientSubscriptionStatus.AwaitingPaymentMethod.ToString(), subscription.Status.ToString(), BillingActorType.System, null, "billing_orchestrator", customerResult.SafeErrorCode, correlationId, customerResult.SanitizedSummary);
            await _db.SaveChangesAsync(cancellationToken);
            return new ActivateClientSubscriptionResult(false, customerResult.SafeErrorCode, customerResult.SanitizedSummary, customerResult.ProviderRequestId, customerResult.Retryable, subscription, null, new ClientSubscriptionLifecycleResult(false, subscription.Status.ToString(), customerResult.SafeErrorCode, customerResult.SanitizedSummary, customerResult.ProviderRequestId, customerResult.Retryable));
        }

        subscription.ProviderCustomerId = customerResult.ProviderCustomerId;
        subscription.FirstChargeUtc = command.FirstChargeUtc;
        subscription.FirstRecurringRenewalUtc = command.FirstRecurringRenewalUtc;
        subscription.BillingTimeZoneId = string.IsNullOrWhiteSpace(command.BillingTimeZoneId) ? subscription.BillingTimeZoneId : command.BillingTimeZoneId.Trim();
        subscription.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        var attachmentResult = await _gateway.AttachPaymentMethodAsync(
            new BillingPaymentMethodAttachmentRequest(
                customerResult.ProviderCustomerId,
                command.SourceId,
                command.IdempotencyKey ?? BillingIdempotency.CreateDeterministic("billing-card", subscription.Id.ToString(), customerResult.ProviderCustomerId),
                command.CardholderName ?? $"{client.FirstName} {client.LastName}".Trim(),
                subscription.Id.ToString(),
                null,
                correlationId,
                command.BillingAddress),
            cancellationToken);

        if (!attachmentResult.Success || string.IsNullOrWhiteSpace(attachmentResult.ProviderPaymentMethodId))
        {
            subscription.Status = ClientSubscriptionStatus.AwaitingPaymentMethod;
            subscription.PaymentStanding = ClientSubscriptionPaymentStanding.RequiresAction;
            subscription.UpdatedUtc = DateTime.UtcNow;
            AddAuditEntry("ClientSubscription", subscription.Id, "payment_method_failed", null, subscription.Status.ToString(), BillingActorType.System, null, "billing_orchestrator", attachmentResult.SafeErrorCode, correlationId, attachmentResult.SanitizedSummary);
            await _db.SaveChangesAsync(cancellationToken);
            return new ActivateClientSubscriptionResult(false, attachmentResult.SafeErrorCode, attachmentResult.SanitizedSummary, attachmentResult.ProviderRequestId, attachmentResult.Retryable, subscription, null, new ClientSubscriptionLifecycleResult(false, subscription.Status.ToString(), attachmentResult.SafeErrorCode, attachmentResult.SanitizedSummary, attachmentResult.ProviderRequestId, attachmentResult.Retryable, customerResult.ProviderCustomerId));
        }

        subscription.ProviderPaymentMethodId = attachmentResult.ProviderPaymentMethodId;
        subscription.PaymentMethodBrand = attachmentResult.PaymentMethodBrand;
        subscription.PaymentMethodLast4 = attachmentResult.PaymentMethodLast4;
        subscription.PaymentMethodExpirationMonth = attachmentResult.PaymentMethodExpirationMonth;
        subscription.PaymentMethodExpirationYear = attachmentResult.PaymentMethodExpirationYear;
        subscription.PaymentMethodCardholderName = attachmentResult.PaymentMethodCardholderName;
        subscription.PaymentMethodUpdatedUtc = nowUtc;
        subscription.UpdatedUtc = nowUtc;
        await _db.SaveChangesAsync(cancellationToken);

        var initialPaymentRecord = new SubscriptionPayment
        {
            ClientSubscriptionId = subscription.Id,
            Provider = _gateway.Provider,
            ProviderEnvironment = _gateway.Environment,
            AmountCents = offer.MonthlyAmountCents,
            Currency = authoritativeCurrency,
            Kind = SubscriptionPaymentKind.InitialActivation,
            AttemptNumber = 1,
            IdempotencyKey = BillingIdempotency.CreateDeterministic(
                "billing-initial-payment",
                subscription.Id.ToString(),
                offer.MonthlyAmountCents.ToString(System.Globalization.CultureInfo.InvariantCulture),
                command.FirstChargeUtc.ToString("O")),
            Status = SubscriptionPaymentStatus.Pending,
            BillingPeriodStartUtc = command.FirstChargeUtc,
            BillingPeriodEndUtc = command.FirstRecurringRenewalUtc,
            ScheduledChargeUtc = command.FirstChargeUtc,
            ClaimedUtc = nowUtc,
            ClaimToken = correlationId,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        };

        _db.SubscriptionPayments.Add(initialPaymentRecord);
        AddAuditEntry("SubscriptionPayment", initialPaymentRecord.Id, "created", null, initialPaymentRecord.Status.ToString(), BillingActorType.Client, null, "billing_orchestrator", null, correlationId);
        await _db.SaveChangesAsync(cancellationToken);

        BillingOneTimePaymentResult initialPaymentResult;
        try
        {
            initialPaymentResult = await _gateway.CreateOneTimePaymentAsync(
                new BillingOneTimePaymentRequest(
                    attachmentResult.ProviderPaymentMethodId,
                    offer.MonthlyAmountCents,
                    authoritativeCurrency,
                    $"Client subscription initial payment {subscription.Id}",
                    initialPaymentRecord.IdempotencyKey,
                    correlationId,
                    subscription.Id.ToString(),
                    customerResult.ProviderCustomerId),
                cancellationToken);
        }
        catch (Exception)
        {
            subscription.Status = ClientSubscriptionStatus.ReconciliationRequired;
            subscription.PaymentStanding = ClientSubscriptionPaymentStanding.RequiresAction;
            subscription.LastChargeAttemptUtc = nowUtc;
            subscription.UpdatedUtc = DateTime.UtcNow;
            initialPaymentRecord.Status = SubscriptionPaymentStatus.Processing;
            initialPaymentRecord.SafeFailureCode = "INITIAL_PAYMENT_OUTCOME_UNKNOWN";
            initialPaymentRecord.UpdatedUtc = DateTime.UtcNow;
            AddAuditEntry("SubscriptionPayment", initialPaymentRecord.Id, "outcome_unknown", SubscriptionPaymentStatus.Pending.ToString(), initialPaymentRecord.Status.ToString(), BillingActorType.System, null, "billing_orchestrator", initialPaymentRecord.SafeFailureCode, correlationId, "The processor did not return a safe initial-payment result; the existing attempt requires reconciliation.");
            AddAuditEntry("ClientSubscription", subscription.Id, "reconciliation_required", null, subscription.Status.ToString(), BillingActorType.System, null, "billing_orchestrator", initialPaymentRecord.SafeFailureCode, correlationId);
            await _db.SaveChangesAsync(cancellationToken);

            var unknownResult = new ClientSubscriptionLifecycleResult(
                false,
                subscription.Status.ToString(),
                initialPaymentRecord.SafeFailureCode,
                "The initial payment is awaiting reconciliation. No additional charge was submitted.",
                null,
                false,
                customerResult.ProviderCustomerId,
                attachmentResult.ProviderPaymentMethodId);
            return new ActivateClientSubscriptionResult(false, unknownResult.SafeErrorCode, unknownResult.SanitizedSummary, null, false, subscription, null, unknownResult);
        }

        initialPaymentRecord.ProviderPaymentId = initialPaymentResult.ExternalId;
        initialPaymentRecord.ProviderRequestId = initialPaymentResult.ProviderRequestId;
        initialPaymentRecord.ProviderOccurredUtc = initialPaymentResult.ProviderOccurredUtc;
        initialPaymentRecord.Status = BillingStateMapper.MapPaymentStatus(initialPaymentResult.NormalizedStatus);
        initialPaymentRecord.SafeFailureCode = initialPaymentResult.SafeErrorCode;
        initialPaymentRecord.Retryable = false;
        initialPaymentRecord.UpdatedUtc = DateTime.UtcNow;

        AddAuditEntry(
            "SubscriptionPayment",
            initialPaymentRecord.Id,
            initialPaymentResult.Success ? "completed" : "failed",
            SubscriptionPaymentStatus.Pending.ToString(),
            initialPaymentRecord.Status.ToString(),
            BillingActorType.System,
            null,
            "billing_orchestrator",
            initialPaymentResult.SafeErrorCode,
            correlationId,
            initialPaymentResult.SanitizedSummary);

        if (!initialPaymentResult.Success || initialPaymentRecord.Status != SubscriptionPaymentStatus.Completed)
        {
            subscription.Status = ClientSubscriptionStatus.ActivationFailed;
            subscription.PaymentStanding = ClientSubscriptionPaymentStanding.Failed;
            subscription.ProviderCustomerId = customerResult.ProviderCustomerId;
            subscription.ProviderPaymentMethodId = attachmentResult.ProviderPaymentMethodId;
            subscription.LastChargeAttemptUtc = nowUtc;
            subscription.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            var failureCode = initialPaymentResult.SafeErrorCode ?? "INITIAL_PAYMENT_NOT_COMPLETED";
            var failedPaymentResult = new ClientSubscriptionLifecycleResult(false, subscription.Status.ToString(), failureCode, initialPaymentResult.SanitizedSummary ?? "The initial payment was not completed.", initialPaymentResult.ProviderRequestId, initialPaymentResult.Retryable, customerResult.ProviderCustomerId, attachmentResult.ProviderPaymentMethodId);
            return new ActivateClientSubscriptionResult(false, failedPaymentResult.SafeErrorCode, failedPaymentResult.SanitizedSummary, failedPaymentResult.ProviderRequestId, failedPaymentResult.Retryable, subscription, null, failedPaymentResult);
        }

        subscription.Status = ClientSubscriptionStatus.Active;
        subscription.PaymentStanding = ClientSubscriptionPaymentStanding.Current;
        subscription.ProviderCustomerId = customerResult.ProviderCustomerId;
        subscription.ProviderPaymentMethodId = attachmentResult.ProviderPaymentMethodId;
        subscription.IsPlatformManaged = true;
        subscription.PlatformManagedSinceUtc ??= nowUtc;
        subscription.FirstChargeUtc = command.FirstChargeUtc;
        subscription.FirstRecurringRenewalUtc = command.FirstRecurringRenewalUtc;
        subscription.BillingTimeZoneId = string.IsNullOrWhiteSpace(command.BillingTimeZoneId) ? subscription.BillingTimeZoneId : command.BillingTimeZoneId.Trim();
        subscription.CurrentPeriodStartUtc = command.FirstChargeUtc;
        subscription.CurrentPeriodEndUtc = command.FirstRecurringRenewalUtc;
        subscription.NextBillingDateUtc = command.FirstRecurringRenewalUtc;
        subscription.NextChargeAttemptUtc = command.FirstRecurringRenewalUtc;
        subscription.LastChargeAttemptUtc = nowUtc;
        subscription.LastSuccessfulChargeUtc = nowUtc;
        subscription.ActivatedUtc ??= nowUtc;
        subscription.UpdatedUtc = nowUtc;
        offer.Status = ClientSubscriptionOfferStatus.Accepted;
        offer.UpdatedUtc = nowUtc;

        if (invitation is not null)
        {
            invitation.Status = SubscriptionActivationInvitationStatus.Redeemed;
            invitation.RedeemedUtc = nowUtc;
        }

        AddAuditEntry("ClientSubscription", subscription.Id, "activated", ClientSubscriptionStatus.AwaitingPaymentMethod.ToString(), subscription.Status.ToString(), BillingActorType.System, null, "billing_orchestrator", null, correlationId, "Initial payment completed and recurring billing is now scheduled by MASTERAPP.");
        await _db.SaveChangesAsync(cancellationToken);

        var entitlement = await _entitlements.RefreshAsync(command.ClientProfileId, BillingEntitlementKeys.ClientAppFullAccess, "SUBSCRIPTION_ACTIVATED", cancellationToken);
        var activationResult = new ClientSubscriptionLifecycleResult(
            true,
            ClientSubscriptionStatus.Active.ToString(),
            null,
            "Client subscription activated.",
            initialPaymentResult.ProviderRequestId,
            false,
            customerResult.ProviderCustomerId,
            attachmentResult.ProviderPaymentMethodId,
            offer.MonthlyAmountCents,
            authoritativeCurrency,
            subscription.BillingAnchorDay,
            subscription.CurrentPeriodStartUtc,
            subscription.CurrentPeriodEndUtc,
            subscription.NextBillingDateUtc,
            false);
        return new ActivateClientSubscriptionResult(true, null, activationResult.SanitizedSummary, activationResult.ProviderRequestId, false, subscription, entitlement, activationResult);
    }

    public async Task<CancelClientSubscriptionResult> CancelClientSubscriptionAsync(CancelClientSubscriptionCommand command, CancellationToken cancellationToken = default)
    {
        var subscription = await _db.ClientSubscriptions.FirstOrDefaultAsync(x => x.Id == command.ClientSubscriptionId, cancellationToken)
            ?? throw new InvalidOperationException($"Client subscription {command.ClientSubscriptionId} was not found.");

        if (!subscription.IsPlatformManaged && !string.IsNullOrWhiteSpace(subscription.ProviderSubscriptionId))
        {
            var historicalResult = new ClientSubscriptionLifecycleResult(
                false,
                subscription.Status.ToString(),
                "LEGACY_PROVIDER_MANAGED_SUBSCRIPTION",
                "This historical provider-managed subscription must be migrated or cancelled through the provider before local cancellation can be recorded.",
                null,
                false);
            return new CancelClientSubscriptionResult(
                false,
                historicalResult.SafeErrorCode,
                historicalResult.SanitizedSummary,
                null,
                false,
                subscription,
                null,
                historicalResult);
        }

        var cancelledUtc = DateTime.UtcNow;
        var correlationId = command.CorrelationId ?? BillingIdempotency.CreateDeterministic("cancel-client-subscription", subscription.Id.ToString(), command.CancelAtPeriodEnd.ToString());
        var previousStatus = subscription.Status.ToString();
        subscription.CancelAtPeriodEnd = command.CancelAtPeriodEnd;
        subscription.UpdatedUtc = cancelledUtc;

        if (!command.CancelAtPeriodEnd || !subscription.CurrentPeriodEndUtc.HasValue || subscription.CurrentPeriodEndUtc <= cancelledUtc)
        {
            subscription.Status = ClientSubscriptionStatus.Canceled;
            subscription.PaymentStanding = ClientSubscriptionPaymentStanding.Failed;
            subscription.CancelAtPeriodEnd = false;
            subscription.CancelledUtc = cancelledUtc;
            subscription.EndedUtc = cancelledUtc;
            subscription.GracePeriodEndsUtc = null;
            subscription.NextBillingDateUtc = null;
            subscription.NextChargeAttemptUtc = null;
        }
        else
        {
            subscription.NextChargeAttemptUtc = null;
        }

        AddAuditEntry("ClientSubscription", subscription.Id, command.CancelAtPeriodEnd ? "cancellation_scheduled" : "cancelled", previousStatus, subscription.Status.ToString(), command.ActorType, command.ActorId, "billing_orchestrator", null, correlationId, command.CancelAtPeriodEnd ? "Cancellation is scheduled locally at the current period boundary." : "Subscription cancelled locally and future charges were stopped.");
        await _db.SaveChangesAsync(cancellationToken);

        var entitlement = await _entitlements.RefreshAsync(subscription.ClientProfileId, BillingEntitlementKeys.ClientAppFullAccess, "SUBSCRIPTION_CANCELLED", cancellationToken);
        var cancellationResult = new ClientSubscriptionLifecycleResult(
            true,
            subscription.Status.ToString(),
            null,
            "Client subscription cancellation recorded.",
            null,
            false,
            subscription.ProviderCustomerId,
            subscription.ProviderPaymentMethodId,
            subscription.MonthlyAmountCents,
            subscription.Currency,
            subscription.BillingAnchorDay,
            subscription.CurrentPeriodStartUtc,
            subscription.CurrentPeriodEndUtc,
            subscription.NextBillingDateUtc,
            subscription.CancelAtPeriodEnd);
        return new CancelClientSubscriptionResult(true, null, cancellationResult.SanitizedSummary, null, false, subscription, entitlement, cancellationResult);
    }

    public async Task<PlatformRecurringBillingRunResult> ProcessDueClientSubscriptionRenewalsAsync(
        int maxItems,
        string workerId,
        CancellationToken cancellationToken = default)
    {
        if (maxItems <= 0)
            return new PlatformRecurringBillingRunResult(0, 0, 0, 0, 0);

        var normalizedWorkerId = string.IsNullOrWhiteSpace(workerId) ? "billing-worker" : workerId.Trim();
        var nowUtc = DateTime.UtcNow;
        var dueSubscriptionIds = await _db.ClientSubscriptions
            .AsNoTracking()
            .Where(x =>
                x.IsPlatformManaged &&
                (x.Status == ClientSubscriptionStatus.Active || x.Status == ClientSubscriptionStatus.GracePeriod) &&
                ((x.CancelAtPeriodEnd && x.CurrentPeriodEndUtc.HasValue && x.CurrentPeriodEndUtc <= nowUtc) ||
                 (!x.CancelAtPeriodEnd &&
                  (x.NextChargeAttemptUtc ?? x.NextBillingDateUtc).HasValue &&
                  (x.NextChargeAttemptUtc ?? x.NextBillingDateUtc) <= nowUtc) ||
                 (x.Status == ClientSubscriptionStatus.GracePeriod && x.GracePeriodEndsUtc.HasValue && x.GracePeriodEndsUtc <= nowUtc)))
            .OrderBy(x => x.NextBillingDateUtc)
            .ThenBy(x => x.UpdatedUtc)
            .Select(x => x.Id)
            .Take(maxItems)
            .ToListAsync(cancellationToken);

        var chargesAttempted = 0;
        var chargesSucceeded = 0;
        var chargesFailed = 0;
        var endedAtPeriodBoundary = 0;

        foreach (var subscriptionId in dueSubscriptionIds)
        {
            var outcome = await ProcessDueClientSubscriptionRenewalAsync(subscriptionId, normalizedWorkerId, nowUtc, cancellationToken);
            chargesAttempted += outcome.ChargesAttempted;
            chargesSucceeded += outcome.ChargesSucceeded;
            chargesFailed += outcome.ChargesFailed;
            endedAtPeriodBoundary += outcome.EndedAtPeriodBoundary;
        }

        return new PlatformRecurringBillingRunResult(
            dueSubscriptionIds.Count,
            chargesAttempted,
            chargesSucceeded,
            chargesFailed,
            endedAtPeriodBoundary);
    }

    private async Task<RecurringBillingOutcome> ProcessDueClientSubscriptionRenewalAsync(
        Guid subscriptionId,
        string workerId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var subscription = await _db.ClientSubscriptions.FirstOrDefaultAsync(x => x.Id == subscriptionId, cancellationToken);
        if (subscription is null ||
            !subscription.IsPlatformManaged ||
            subscription.Status is not (ClientSubscriptionStatus.Active or ClientSubscriptionStatus.GracePeriod))
            return RecurringBillingOutcome.None;

        if (subscription.CancelAtPeriodEnd && subscription.CurrentPeriodEndUtc.HasValue && subscription.CurrentPeriodEndUtc <= nowUtc)
        {
            var previousStatus = subscription.Status.ToString();
            subscription.Status = ClientSubscriptionStatus.Canceled;
            subscription.PaymentStanding = ClientSubscriptionPaymentStanding.Failed;
            subscription.CancelAtPeriodEnd = false;
            subscription.CancelledUtc = subscription.CurrentPeriodEndUtc;
            subscription.EndedUtc = subscription.CurrentPeriodEndUtc;
            subscription.GracePeriodEndsUtc = null;
            subscription.NextBillingDateUtc = null;
            subscription.NextChargeAttemptUtc = null;
            subscription.UpdatedUtc = nowUtc;
            AddAuditEntry(
                "ClientSubscription",
                subscription.Id,
                "cancelled_at_period_end",
                previousStatus,
                subscription.Status.ToString(),
                BillingActorType.System,
                null,
                "billing_renewal_worker",
                null,
                workerId,
                "Subscription ended locally at its scheduled period boundary.");
            await _db.SaveChangesAsync(cancellationToken);
            await _entitlements.RefreshAsync(subscription.ClientProfileId, BillingEntitlementKeys.ClientAppFullAccess, "SUBSCRIPTION_ENDED_AT_PERIOD_END", cancellationToken);
            return new RecurringBillingOutcome(0, 0, 0, 1);
        }

        if (subscription.Status == ClientSubscriptionStatus.GracePeriod &&
            subscription.GracePeriodEndsUtc.HasValue &&
            subscription.GracePeriodEndsUtc <= nowUtc)
        {
            var previousStatus = subscription.Status.ToString();
            subscription.Status = ClientSubscriptionStatus.PastDue;
            subscription.PaymentStanding = ClientSubscriptionPaymentStanding.PastDue;
            subscription.NextChargeAttemptUtc = null;
            subscription.UpdatedUtc = nowUtc;
            AddAuditEntry(
                "ClientSubscription",
                subscription.Id,
                "grace_period_expired",
                previousStatus,
                subscription.Status.ToString(),
                BillingActorType.System,
                null,
                "billing_renewal_worker",
                "GRACE_PERIOD_EXPIRED",
                workerId,
                "Recurring payment retries did not restore the subscription before grace expired.");
            await _db.SaveChangesAsync(cancellationToken);
            await _entitlements.RefreshAsync(subscription.ClientProfileId, BillingEntitlementKeys.ClientAppFullAccess, "GRACE_PERIOD_EXPIRED", cancellationToken);
            return RecurringBillingOutcome.None;
        }

        if (!subscription.NextBillingDateUtc.HasValue || subscription.NextBillingDateUtc > nowUtc)
            return RecurringBillingOutcome.None;

        ClientSubscriptionRenewalSchedule renewalSchedule;
        try
        {
            renewalSchedule = _billingPolicy.ResolveRenewalSchedule(subscription);
        }
        catch (InvalidOperationException)
        {
            subscription.Status = ClientSubscriptionStatus.ReconciliationRequired;
            subscription.PaymentStanding = ClientSubscriptionPaymentStanding.RequiresAction;
            subscription.UpdatedUtc = nowUtc;
            AddAuditEntry("ClientSubscription", subscription.Id, "renewal_schedule_invalid", null, subscription.Status.ToString(), BillingActorType.System, null, "billing_renewal_worker", "RENEWAL_SCHEDULE_INVALID", workerId);
            await _db.SaveChangesAsync(cancellationToken);
            await _entitlements.RefreshAsync(subscription.ClientProfileId, BillingEntitlementKeys.ClientAppFullAccess, "RENEWAL_SCHEDULE_INVALID", cancellationToken);
            return RecurringBillingOutcome.None;
        }

        if (subscription.MonthlyAmountCents == 0)
        {
            subscription.CurrentPeriodStartUtc = renewalSchedule.PeriodStartUtc;
            subscription.CurrentPeriodEndUtc = renewalSchedule.PeriodEndUtc;
            subscription.NextBillingDateUtc = renewalSchedule.NextBillingDateUtc;
            subscription.NextChargeAttemptUtc = renewalSchedule.NextBillingDateUtc;
            subscription.UpdatedUtc = nowUtc;
            AddAuditEntry("ClientSubscription", subscription.Id, "complimentary_period_advanced", null, subscription.Status.ToString(), BillingActorType.System, null, "billing_renewal_worker", null, workerId);
            await _db.SaveChangesAsync(cancellationToken);
            return RecurringBillingOutcome.None;
        }

        var attempt = await GetOrCreateDueRenewalAttemptAsync(subscription, renewalSchedule, nowUtc, cancellationToken);
        if (attempt is null || !await TryClaimPaymentAttemptAsync(attempt, workerId, nowUtc, cancellationToken))
            return RecurringBillingOutcome.None;

        BillingOneTimePaymentResult providerResult;
        if (string.IsNullOrWhiteSpace(subscription.ProviderCustomerId) || string.IsNullOrWhiteSpace(subscription.ProviderPaymentMethodId))
        {
            providerResult = new BillingOneTimePaymentResult(
                false,
                null,
                "FAILED",
                "PAYMENT_METHOD_MISSING",
                "A stored payment method is required before the renewal can be charged.",
                null,
                false);
        }
        else
        {
            try
            {
                providerResult = await _gateway.CreateOneTimePaymentAsync(
                    new BillingOneTimePaymentRequest(
                        subscription.ProviderPaymentMethodId,
                        subscription.MonthlyAmountCents,
                        subscription.Currency,
                        $"Client subscription renewal {subscription.Id}",
                        attempt.IdempotencyKey ?? throw new InvalidOperationException("A renewal payment attempt requires an idempotency key."),
                        workerId,
                        subscription.Id.ToString(),
                        subscription.ProviderCustomerId),
                    cancellationToken);
            }
            catch (Exception)
            {
                providerResult = new BillingOneTimePaymentResult(
                    false,
                    null,
                    "FAILED",
                    "UNHANDLED_BILLING_ERROR",
                    "The renewal payment could not be completed before the provider returned a safe result.",
                    null,
                    true);
            }
        }

        await _db.Entry(attempt).ReloadAsync(cancellationToken);
        var previousPaymentStatus = attempt.Status.ToString();
        attempt.ProviderPaymentId = providerResult.ExternalId;
        attempt.ProviderRequestId = providerResult.ProviderRequestId;
        attempt.ProviderOccurredUtc = providerResult.ProviderOccurredUtc;
        attempt.SafeFailureCode = providerResult.SafeErrorCode;
        attempt.UpdatedUtc = nowUtc;
        subscription.LastChargeAttemptUtc = nowUtc;

        var paymentCompleted = providerResult.Success && BillingStateMapper.MapPaymentStatus(providerResult.NormalizedStatus) == SubscriptionPaymentStatus.Completed;
        if (paymentCompleted)
        {
            attempt.Status = SubscriptionPaymentStatus.Completed;
            attempt.Retryable = false;
            attempt.RetryNotBeforeUtc = null;
            subscription.Status = ClientSubscriptionStatus.Active;
            subscription.PaymentStanding = ClientSubscriptionPaymentStanding.Current;
            subscription.GracePeriodEndsUtc = null;
            subscription.CurrentPeriodStartUtc = renewalSchedule.PeriodStartUtc;
            subscription.CurrentPeriodEndUtc = renewalSchedule.PeriodEndUtc;
            subscription.NextBillingDateUtc = renewalSchedule.NextBillingDateUtc;
            subscription.NextChargeAttemptUtc = renewalSchedule.NextBillingDateUtc;
            subscription.LastSuccessfulChargeUtc = nowUtc;
            subscription.UpdatedUtc = nowUtc;
            AddAuditEntry("SubscriptionPayment", attempt.Id, "completed", previousPaymentStatus, attempt.Status.ToString(), BillingActorType.System, null, "billing_renewal_worker", null, workerId, providerResult.SanitizedSummary);
            AddAuditEntry("ClientSubscription", subscription.Id, "renewed", null, subscription.Status.ToString(), BillingActorType.System, null, "billing_renewal_worker", null, workerId);
            await _db.SaveChangesAsync(cancellationToken);
            await _entitlements.RefreshAsync(subscription.ClientProfileId, BillingEntitlementKeys.ClientAppFullAccess, "RENEWAL_PAYMENT_COMPLETED", cancellationToken);
            return new RecurringBillingOutcome(1, 1, 0, 0);
        }

        var retryDelay = providerResult.Retryable
            ? _billingPolicy.ResolveRenewalRetryDelay(attempt.AttemptNumber)
            : null;
        attempt.Status = SubscriptionPaymentStatus.Failed;
        attempt.Retryable = retryDelay.HasValue;
        attempt.RetryNotBeforeUtc = retryDelay.HasValue ? nowUtc.Add(retryDelay.Value) : null;
        subscription.GracePeriodEndsUtc ??= _billingPolicy.ResolveGracePeriodEndUtc(nowUtc);
        subscription.Status = subscription.GracePeriodEndsUtc <= nowUtc
            ? ClientSubscriptionStatus.PastDue
            : ClientSubscriptionStatus.GracePeriod;
        subscription.PaymentStanding = subscription.Status == ClientSubscriptionStatus.PastDue
            ? ClientSubscriptionPaymentStanding.PastDue
            : ClientSubscriptionPaymentStanding.GracePeriod;
        subscription.NextChargeAttemptUtc = attempt.RetryNotBeforeUtc;
        subscription.UpdatedUtc = nowUtc;
        AddAuditEntry("SubscriptionPayment", attempt.Id, "failed", previousPaymentStatus, attempt.Status.ToString(), BillingActorType.System, null, "billing_renewal_worker", attempt.SafeFailureCode ?? "RENEWAL_PAYMENT_FAILED", workerId, providerResult.SanitizedSummary);
        AddAuditEntry("ClientSubscription", subscription.Id, "renewal_payment_failed", null, subscription.Status.ToString(), BillingActorType.System, null, "billing_renewal_worker", attempt.SafeFailureCode ?? "RENEWAL_PAYMENT_FAILED", workerId);
        await _db.SaveChangesAsync(cancellationToken);
        await _entitlements.RefreshAsync(subscription.ClientProfileId, BillingEntitlementKeys.ClientAppFullAccess, "RENEWAL_PAYMENT_FAILED", cancellationToken);
        return new RecurringBillingOutcome(1, 0, 1, 0);
    }

    private async Task<SubscriptionPayment?> GetOrCreateDueRenewalAttemptAsync(
        ClientSubscription subscription,
        ClientSubscriptionRenewalSchedule renewalSchedule,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var attempts = await _db.SubscriptionPayments
            .Where(x => x.ClientSubscriptionId == subscription.Id &&
                        x.BillingPeriodStartUtc == renewalSchedule.PeriodStartUtc)
            .OrderByDescending(x => x.AttemptNumber)
            .ToListAsync(cancellationToken);
        var latestAttempt = attempts.FirstOrDefault();

        if (latestAttempt is not null)
        {
            if (latestAttempt.Status == SubscriptionPaymentStatus.Completed)
                return null;

            if (latestAttempt.Status == SubscriptionPaymentStatus.Processing)
                return latestAttempt.ClaimedUtc <= nowUtc.AddMinutes(-15) ? latestAttempt : null;

            if (latestAttempt.Status == SubscriptionPaymentStatus.Pending)
                return latestAttempt;

            if (latestAttempt.Status != SubscriptionPaymentStatus.Failed ||
                !latestAttempt.Retryable ||
                !latestAttempt.RetryNotBeforeUtc.HasValue ||
                latestAttempt.RetryNotBeforeUtc > nowUtc)
            {
                return null;
            }
        }

        var attemptNumber = (latestAttempt?.AttemptNumber ?? 0) + 1;
        var attempt = new SubscriptionPayment
        {
            ClientSubscriptionId = subscription.Id,
            Provider = subscription.Provider,
            ProviderEnvironment = subscription.ProviderEnvironment,
            AmountCents = subscription.MonthlyAmountCents,
            Currency = subscription.Currency,
            Kind = SubscriptionPaymentKind.Renewal,
            AttemptNumber = attemptNumber,
            IdempotencyKey = BillingIdempotency.CreateDeterministic(
                "billing-recurring-payment",
                subscription.Id.ToString(),
                renewalSchedule.PeriodStartUtc.ToString("O"),
                attemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Status = SubscriptionPaymentStatus.Pending,
            BillingPeriodStartUtc = renewalSchedule.PeriodStartUtc,
            BillingPeriodEndUtc = renewalSchedule.PeriodEndUtc,
            ScheduledChargeUtc = renewalSchedule.PeriodStartUtc,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        };
        _db.SubscriptionPayments.Add(attempt);
        AddAuditEntry("SubscriptionPayment", attempt.Id, "created", null, attempt.Status.ToString(), BillingActorType.System, null, "billing_renewal_worker", null, null);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return attempt;
        }
        catch (DbUpdateException)
        {
            _db.Entry(attempt).State = EntityState.Detached;
            return null;
        }
    }

    private async Task<bool> TryClaimPaymentAttemptAsync(
        SubscriptionPayment attempt,
        string workerId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var claimToken = $"{workerId}:{Guid.NewGuid():N}";
        if (string.Equals(_db.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal))
        {
            if (attempt.Status == SubscriptionPaymentStatus.Processing && attempt.ClaimedUtc > nowUtc.AddMinutes(-15))
                return false;
            if (attempt.Status is not (SubscriptionPaymentStatus.Pending or SubscriptionPaymentStatus.Processing))
                return false;

            attempt.Status = SubscriptionPaymentStatus.Processing;
            attempt.ClaimedUtc = nowUtc;
            attempt.ClaimToken = claimToken;
            attempt.UpdatedUtc = nowUtc;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var staleClaimUtc = nowUtc.AddMinutes(-15);
        var claimed = await _db.SubscriptionPayments
            .Where(x => x.Id == attempt.Id &&
                        (x.Status == SubscriptionPaymentStatus.Pending ||
                         (x.Status == SubscriptionPaymentStatus.Processing && x.ClaimedUtc.HasValue && x.ClaimedUtc <= staleClaimUtc)))
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(x => x.Status, SubscriptionPaymentStatus.Processing)
                .SetProperty(x => x.ClaimedUtc, nowUtc)
                .SetProperty(x => x.ClaimToken, claimToken)
                .SetProperty(x => x.UpdatedUtc, nowUtc), cancellationToken);

        return claimed == 1;
    }

    private sealed record RecurringBillingOutcome(int ChargesAttempted, int ChargesSucceeded, int ChargesFailed, int EndedAtPeriodBoundary)
    {
        public static readonly RecurringBillingOutcome None = new(0, 0, 0, 0);
    }

    private async Task<ActivateClientSubscriptionResult> ActivateZeroDollarSubscriptionAsync(
        ClientSubscription subscription,
        ClientSubscriptionOffer offer,
        SubscriptionActivationInvitation? invitation,
        ActivateClientSubscriptionCommand command,
        string currency,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var previousStatus = subscription.Status;
        var correlationId = command.CorrelationId ?? BillingIdempotency.CreateDeterministic(
            "client-zero-dollar-subscription-activation",
            command.ClientProfileId.ToString(),
            offer.Id.ToString(),
            subscription.Id.ToString());

        subscription.MonthlyAmountCents = 0;
        subscription.Currency = currency;
        subscription.BillingTimeZoneId = string.IsNullOrWhiteSpace(command.BillingTimeZoneId)
            ? subscription.BillingTimeZoneId
            : command.BillingTimeZoneId.Trim();
        subscription.BillingAnchorDay = command.BillingAnchorDay ?? offer.SelectedBillingAnchorDay;
        subscription.Status = ClientSubscriptionStatus.Active;
        subscription.PaymentStanding = ClientSubscriptionPaymentStanding.Current;
        subscription.FirstChargeUtc = command.FirstChargeUtc;
        subscription.FirstRecurringRenewalUtc = command.FirstRecurringRenewalUtc;
        subscription.CurrentPeriodStartUtc = command.FirstChargeUtc;
        subscription.CurrentPeriodEndUtc = command.FirstRecurringRenewalUtc;
        subscription.NextBillingDateUtc = command.FirstRecurringRenewalUtc;
        subscription.NextChargeAttemptUtc = command.FirstRecurringRenewalUtc;
        subscription.ProviderCustomerId = null;
        subscription.ProviderPaymentMethodId = null;
        subscription.IsPlatformManaged = true;
        subscription.PlatformManagedSinceUtc ??= nowUtc;
        subscription.ActivatedUtc ??= nowUtc;
        subscription.UpdatedUtc = nowUtc;

        offer.Status = ClientSubscriptionOfferStatus.Accepted;
        offer.UpdatedUtc = nowUtc;

        if (invitation is not null)
        {
            invitation.Status = SubscriptionActivationInvitationStatus.Redeemed;
            invitation.RedeemedUtc = nowUtc;
        }

        AddAuditEntry(
            "ClientSubscription",
            subscription.Id,
            "zero_dollar_activated",
            previousStatus.ToString(),
            subscription.Status.ToString(),
            BillingActorType.System,
            null,
            "billing_orchestrator",
            null,
            correlationId,
            "Complimentary client subscription activated without a payment method.",
            BuildConsentMetadataJson(command, currency, 0));
        await _db.SaveChangesAsync(cancellationToken);

        var entitlement = await _entitlements.RefreshAsync(
            command.ClientProfileId,
            BillingEntitlementKeys.ClientAppFullAccess,
            "ZERO_DOLLAR_SUBSCRIPTION_ACTIVATED",
            cancellationToken);
        var result = new ClientSubscriptionLifecycleResult(
            true,
            ClientSubscriptionStatus.Active.ToString(),
            null,
            "Complimentary client subscription activated.",
            null,
            false);
        return new ActivateClientSubscriptionResult(true, null, result.SanitizedSummary, null, false, subscription, entitlement, result);
    }

    private async Task<ClientProfile> EnsureClientExistsAsync(Guid clientProfileId, CancellationToken cancellationToken)
    {
        return await _db.ClientProfiles.FirstOrDefaultAsync(x => x.Id == clientProfileId, cancellationToken)
            ?? throw new InvalidOperationException($"Client profile {clientProfileId} was not found.");
    }

    private async Task<ClientSubscription?> GetExistingReusableSubscriptionAsync(Guid clientProfileId, Guid offerId, CancellationToken cancellationToken)
    {
        return await _db.ClientSubscriptions
            .Where(x => x.ClientProfileId == clientProfileId &&
                        x.AcceptedOfferId == offerId &&
                        x.Status != ClientSubscriptionStatus.Canceled &&
                        x.Status != ClientSubscriptionStatus.ActivationFailed)
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<ClientSubscription?> FindBlockingSubscriptionAsync(Guid clientProfileId, Guid offerId, CancellationToken cancellationToken)
    {
        return await _db.ClientSubscriptions
            .Where(x => x.ClientProfileId == clientProfileId &&
                        x.AcceptedOfferId != offerId &&
                        x.Status != ClientSubscriptionStatus.Canceled &&
                        x.Status != ClientSubscriptionStatus.ActivationFailed)
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync(cancellationToken);
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
        string? summary = null,
        string? sanitizedMetadataJson = null)
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
            SanitizedMetadataJson = !string.IsNullOrWhiteSpace(sanitizedMetadataJson)
                ? sanitizedMetadataJson
                : (string.IsNullOrWhiteSpace(summary) ? null : $$"""{"summary":"{{EscapeJson(summary)}}"}""")
        });
    }

    private static string NormalizeCurrency(string currency) => string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();

    private static string NormalizeEmail(string? email) => string.IsNullOrWhiteSpace(email) ? string.Empty : email.Trim().ToLowerInvariant();

    private static string EscapeJson(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static string BuildConsentMetadataJson(ActivateClientSubscriptionCommand command, string currency, int amountCents)
    {
        return $$"""
        {"billingAnchorDay":{{command.BillingAnchorDay?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}},"billingTimeZoneId":"{{EscapeJson(command.BillingTimeZoneId)}}","firstChargeUtc":"{{command.FirstChargeUtc:O}}","firstRecurringRenewalUtc":"{{command.FirstRecurringRenewalUtc:O}}","monthlyAmountCents":{{amountCents}},"currency":"{{EscapeJson(currency)}}","intendedNormalizedEmail":"{{EscapeJson(command.IntendedNormalizedEmail)}}","recurringAuthorizationAccepted":{{command.RecurringAuthorizationAccepted.ToString().ToLowerInvariant()}},"cardOnFileConsentAccepted":{{command.CardOnFileConsentAccepted.ToString().ToLowerInvariant()}},"cancellationTermsAccepted":{{command.CancellationTermsAccepted.ToString().ToLowerInvariant()}}}
        """;
    }
}
