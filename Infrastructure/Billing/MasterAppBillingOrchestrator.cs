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

    public MasterAppBillingOrchestrator(
        MasterAppDbContext db,
        IBillingGateway gateway,
        IBillingEntitlementService entitlements)
    {
        _db = db;
        _gateway = gateway;
        _entitlements = entitlements;
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
            Status = SubscriptionPaymentStatus.Pending,
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
            var consentFailure = new BillingSubscriptionResult(false, null, "CONSENT_REQUIRED", "MISSING_REQUIRED_CONSENT", "Recurring billing consent is required before activation can continue.", null, false);
            return new ActivateClientSubscriptionResult(false, consentFailure.SafeErrorCode, consentFailure.SanitizedSummary, null, false, null, null, consentFailure);
        }

        var subscription = await GetExistingReusableSubscriptionAsync(command.ClientProfileId, command.ClientSubscriptionOfferId, cancellationToken);
        if (subscription is null)
        {
            var blockingSubscription = await FindBlockingSubscriptionAsync(command.ClientProfileId, command.ClientSubscriptionOfferId, cancellationToken);
            if (blockingSubscription is not null)
            {
                var blockingResult = new BillingSubscriptionResult(false, blockingSubscription.ProviderSubscriptionId, blockingSubscription.Status.ToString(), "ACTIVE_SUBSCRIPTION_EXISTS", "A non-terminal subscription already exists for this client.", null, false);
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
                var expiredResult = new BillingSubscriptionResult(false, subscription.ProviderSubscriptionId, subscription.Status.ToString(), "INVITATION_EXPIRED", "The activation invitation has expired.", null, false);
                return new ActivateClientSubscriptionResult(false, expiredResult.SafeErrorCode, expiredResult.SanitizedSummary, null, false, subscription, null, expiredResult);
            }

            if (invitation.Status is SubscriptionActivationInvitationStatus.Revoked or SubscriptionActivationInvitationStatus.Superseded)
            {
                var unavailableResult = new BillingSubscriptionResult(false, subscription.ProviderSubscriptionId, subscription.Status.ToString(), "INVITATION_UNAVAILABLE", "The activation invitation is no longer available.", null, false);
                return new ActivateClientSubscriptionResult(false, unavailableResult.SafeErrorCode, unavailableResult.SanitizedSummary, null, false, subscription, null, unavailableResult);
            }

            if (invitation.Status == SubscriptionActivationInvitationStatus.Redeemed)
            {
                var reusedResult = new BillingSubscriptionResult(false, subscription.ProviderSubscriptionId, subscription.Status.ToString(), "INVITATION_ALREADY_USED", "The activation invitation has already been used.", null, false);
                return new ActivateClientSubscriptionResult(false, reusedResult.SafeErrorCode, reusedResult.SanitizedSummary, null, false, subscription, null, reusedResult);
            }

            if (!string.IsNullOrWhiteSpace(intendedNormalizedEmail) &&
                !string.Equals(invitation.IntendedNormalizedEmail, intendedNormalizedEmail, StringComparison.Ordinal))
            {
                var emailMismatchResult = new BillingSubscriptionResult(false, subscription.ProviderSubscriptionId, subscription.Status.ToString(), "INVITATION_EMAIL_MISMATCH", "The activation invitation does not match the current client email.", null, false);
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
            return new ActivateClientSubscriptionResult(false, customerResult.SafeErrorCode, customerResult.SanitizedSummary, customerResult.ProviderRequestId, customerResult.Retryable, subscription, null, new BillingSubscriptionResult(false, subscription.ProviderSubscriptionId, subscription.Status.ToString(), customerResult.SafeErrorCode, customerResult.SanitizedSummary, customerResult.ProviderRequestId, customerResult.Retryable));
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
            return new ActivateClientSubscriptionResult(false, attachmentResult.SafeErrorCode, attachmentResult.SanitizedSummary, attachmentResult.ProviderRequestId, attachmentResult.Retryable, subscription, null, new BillingSubscriptionResult(false, subscription.ProviderSubscriptionId, subscription.Status.ToString(), attachmentResult.SafeErrorCode, attachmentResult.SanitizedSummary, attachmentResult.ProviderRequestId, attachmentResult.Retryable, customerResult.ProviderCustomerId));
        }

        var initialPaymentRecord = new SubscriptionPayment
        {
            ClientSubscriptionId = subscription.Id,
            Provider = _gateway.Provider,
            ProviderEnvironment = _gateway.Environment,
            AmountCents = offer.MonthlyAmountCents,
            Currency = authoritativeCurrency,
            Status = SubscriptionPaymentStatus.Pending,
            BillingPeriodStartUtc = command.FirstChargeUtc,
            BillingPeriodEndUtc = command.FirstRecurringRenewalUtc,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        };

        _db.SubscriptionPayments.Add(initialPaymentRecord);
        AddAuditEntry("SubscriptionPayment", initialPaymentRecord.Id, "created", null, initialPaymentRecord.Status.ToString(), BillingActorType.Client, null, "billing_orchestrator", null, correlationId);
        await _db.SaveChangesAsync(cancellationToken);

        var initialPaymentResult = await _gateway.CreateOneTimePaymentAsync(
            new BillingOneTimePaymentRequest(
                command.SourceId,
                offer.MonthlyAmountCents,
                authoritativeCurrency,
                $"Client subscription initial payment {subscription.Id}",
                BillingIdempotency.CreateDeterministic("billing-initial-payment", subscription.Id.ToString(), offer.MonthlyAmountCents.ToString(), command.FirstChargeUtc.ToString("O")),
                correlationId,
                subscription.Id.ToString(),
                customerResult.ProviderCustomerId),
            cancellationToken);

        initialPaymentRecord.ProviderPaymentId = initialPaymentResult.ExternalId;
        initialPaymentRecord.ProviderOccurredUtc = initialPaymentResult.ProviderOccurredUtc;
        initialPaymentRecord.Status = BillingStateMapper.MapPaymentStatus(initialPaymentResult.NormalizedStatus);
        initialPaymentRecord.SafeFailureCode = initialPaymentResult.SafeErrorCode;
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

        if (!initialPaymentResult.Success)
        {
            subscription.Status = ClientSubscriptionStatus.ActivationFailed;
            subscription.PaymentStanding = ClientSubscriptionPaymentStanding.Failed;
            subscription.ProviderPaymentMethodId = attachmentResult.ProviderPaymentMethodId;
            subscription.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            var failedPaymentResult = new BillingSubscriptionResult(false, subscription.ProviderSubscriptionId, subscription.Status.ToString(), initialPaymentResult.SafeErrorCode, initialPaymentResult.SanitizedSummary, initialPaymentResult.ProviderRequestId, initialPaymentResult.Retryable, customerResult.ProviderCustomerId, attachmentResult.ProviderPaymentMethodId);
            return new ActivateClientSubscriptionResult(false, failedPaymentResult.SafeErrorCode, failedPaymentResult.SanitizedSummary, failedPaymentResult.ProviderRequestId, failedPaymentResult.Retryable, subscription, null, failedPaymentResult);
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(command.ProviderPlanVariationId))
        {
            subscription.Status = ClientSubscriptionStatus.ActivationFailed;
            subscription.PaymentStanding = ClientSubscriptionPaymentStanding.Failed;
            subscription.ProviderPaymentMethodId = attachmentResult.ProviderPaymentMethodId;
            subscription.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            var missingPlanResult = new BillingSubscriptionResult(false, subscription.ProviderSubscriptionId, subscription.Status.ToString(), "MISSING_PLAN_VARIATION", "A provider plan variation ID is required for subscription creation.", null, false, customerResult.ProviderCustomerId, attachmentResult.ProviderPaymentMethodId);
            return new ActivateClientSubscriptionResult(false, missingPlanResult.SafeErrorCode, missingPlanResult.SanitizedSummary, null, false, subscription, null, missingPlanResult);
        }

        subscription.Status = ClientSubscriptionStatus.PendingProviderActivation;
        subscription.PaymentStanding = ClientSubscriptionPaymentStanding.Current;
        subscription.ProviderPaymentMethodId = attachmentResult.ProviderPaymentMethodId;
        subscription.ProviderPlanVariationId = command.ProviderPlanVariationId;
        subscription.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        var providerResult = await _gateway.CreateSubscriptionAsync(
            new BillingSubscriptionCreateRequest(
                customerResult.ProviderCustomerId,
                attachmentResult.ProviderPaymentMethodId,
                command.ProviderPlanVariationId,
                offer.MonthlyAmountCents,
                authoritativeCurrency,
                command.BillingAnchorDay ?? offer.SelectedBillingAnchorDay,
                command.FirstRecurringRenewalLocalDate,
                command.IdempotencyKey ?? BillingIdempotency.CreateDeterministic("billing-subscription", subscription.Id.ToString(), command.ProviderPlanVariationId),
                correlationId),
            cancellationToken);

        if (!providerResult.Success)
        {
            subscription.Status = providerResult.Retryable
                ? ClientSubscriptionStatus.ReconciliationRequired
                : ClientSubscriptionStatus.ActivationFailed;
            subscription.PaymentStanding = BillingStateMapper.MapPaymentStanding(providerResult.NormalizedStatus);
            subscription.UpdatedUtc = DateTime.UtcNow;
            AddAuditEntry("ClientSubscription", subscription.Id, "provider_activation_failed", null, subscription.Status.ToString(), BillingActorType.System, null, "billing_orchestrator", providerResult.SafeErrorCode, correlationId, providerResult.SanitizedSummary);
            await _db.SaveChangesAsync(cancellationToken);
            return new ActivateClientSubscriptionResult(false, providerResult.SafeErrorCode, providerResult.SanitizedSummary, providerResult.ProviderRequestId, providerResult.Retryable, subscription, null, providerResult);
        }

        ApplyProviderSubscriptionResult(subscription, providerResult, nowUtc);
        subscription.FirstChargeUtc = command.FirstChargeUtc;
        subscription.FirstRecurringRenewalUtc = command.FirstRecurringRenewalUtc;
        subscription.BillingTimeZoneId = string.IsNullOrWhiteSpace(command.BillingTimeZoneId) ? subscription.BillingTimeZoneId : command.BillingTimeZoneId.Trim();
        subscription.CurrentPeriodStartUtc ??= command.FirstChargeUtc;
        subscription.CurrentPeriodEndUtc ??= command.FirstRecurringRenewalUtc;
        subscription.NextBillingDateUtc ??= command.FirstRecurringRenewalUtc;
        if (subscription.Status == ClientSubscriptionStatus.PendingProviderActivation)
        {
            subscription.Status = ClientSubscriptionStatus.Active;
            subscription.PaymentStanding = ClientSubscriptionPaymentStanding.Current;
        }
        offer.Status = ClientSubscriptionOfferStatus.Accepted;
        offer.UpdatedUtc = nowUtc;

        if (invitation is not null)
        {
            invitation.Status = SubscriptionActivationInvitationStatus.Redeemed;
            invitation.RedeemedUtc = nowUtc;
        }

        AddAuditEntry("ClientSubscription", subscription.Id, "activated", ClientSubscriptionStatus.PendingProviderActivation.ToString(), subscription.Status.ToString(), BillingActorType.System, null, "billing_orchestrator", null, correlationId);
        await _db.SaveChangesAsync(cancellationToken);

        var entitlement = await _entitlements.RefreshAsync(command.ClientProfileId, BillingEntitlementKeys.ClientAppFullAccess, "SUBSCRIPTION_ACTIVATED", cancellationToken);
        return new ActivateClientSubscriptionResult(true, null, "Client subscription activated.", providerResult.ProviderRequestId, false, subscription, entitlement, providerResult);
    }

    public async Task<CancelClientSubscriptionResult> CancelClientSubscriptionAsync(CancelClientSubscriptionCommand command, CancellationToken cancellationToken = default)
    {
        var subscription = await _db.ClientSubscriptions.FirstOrDefaultAsync(x => x.Id == command.ClientSubscriptionId, cancellationToken)
            ?? throw new InvalidOperationException($"Client subscription {command.ClientSubscriptionId} was not found.");

        if (string.IsNullOrWhiteSpace(subscription.ProviderSubscriptionId))
        {
            if (subscription.MonthlyAmountCents == 0)
            {
                var localCancelledUtc = DateTime.UtcNow;
                subscription.CancelAtPeriodEnd = command.CancelAtPeriodEnd;
                subscription.UpdatedUtc = localCancelledUtc;

                if (!command.CancelAtPeriodEnd)
                {
                    subscription.Status = ClientSubscriptionStatus.Canceled;
                    subscription.PaymentStanding = ClientSubscriptionPaymentStanding.Failed;
                    subscription.CancelAtPeriodEnd = false;
                    subscription.CancelledUtc = localCancelledUtc;
                    subscription.GracePeriodEndsUtc = null;
                    subscription.NextBillingDateUtc = null;
                }

                AddAuditEntry(
                    "ClientSubscription",
                    subscription.Id,
                    "cancelled",
                    null,
                    subscription.Status.ToString(),
                    command.ActorType,
                    command.ActorId,
                    "billing_orchestrator",
                    null,
                    command.CorrelationId,
                    "Complimentary client subscription cancellation recorded locally.");
                await _db.SaveChangesAsync(cancellationToken);

                var localEntitlement = await _entitlements.RefreshAsync(
                    subscription.ClientProfileId,
                    BillingEntitlementKeys.ClientAppFullAccess,
                    "SUBSCRIPTION_CANCELLED",
                    cancellationToken);
                var localResult = new BillingSubscriptionResult(
                    true,
                    null,
                    subscription.Status.ToString(),
                    null,
                    "Client subscription cancellation recorded.",
                    null,
                    false);
                return new CancelClientSubscriptionResult(true, null, localResult.SanitizedSummary, null, false, subscription, localEntitlement, localResult);
            }

            var localFailure = new BillingSubscriptionResult(false, null, subscription.Status.ToString(), "MISSING_PROVIDER_SUBSCRIPTION_ID", "The subscription cannot be cancelled because it does not have a provider subscription ID.", null, false);
            return new CancelClientSubscriptionResult(false, localFailure.SafeErrorCode, localFailure.SanitizedSummary, null, false, subscription, null, localFailure);
        }

        var correlationId = command.CorrelationId ?? BillingIdempotency.CreateDeterministic("cancel-client-subscription", subscription.Id.ToString(), subscription.ProviderSubscriptionId);
        var providerResult = await _gateway.CancelSubscriptionAsync(
            new BillingSubscriptionCancellationRequest(
                subscription.ProviderSubscriptionId,
                BillingIdempotency.CreateDeterministic("provider-cancel", subscription.ProviderSubscriptionId, command.CancelAtPeriodEnd.ToString()),
                command.CancelAtPeriodEnd,
                correlationId),
            cancellationToken);

        if (!providerResult.Success)
        {
            return new CancelClientSubscriptionResult(false, providerResult.SafeErrorCode, providerResult.SanitizedSummary, providerResult.ProviderRequestId, providerResult.Retryable, subscription, null, providerResult);
        }

        var cancelledUtc = DateTime.UtcNow;
        subscription.CancelAtPeriodEnd = command.CancelAtPeriodEnd;
        subscription.ProviderSubscriptionId = providerResult.ExternalId ?? subscription.ProviderSubscriptionId;
        subscription.CurrentPeriodEndUtc = providerResult.CurrentPeriodEndUtc ?? subscription.CurrentPeriodEndUtc;
        subscription.NextBillingDateUtc = providerResult.NextBillingDateUtc ?? subscription.NextBillingDateUtc;
        subscription.UpdatedUtc = cancelledUtc;

        var mappedStatus = BillingStateMapper.MapSubscriptionStatus(providerResult.NormalizedStatus);
        if (!command.CancelAtPeriodEnd)
        {
            // A client-initiated cancellation ends portal access now. Do not allow a
            // delayed or stale provider status to leave the entitlement active.
            subscription.Status = ClientSubscriptionStatus.Canceled;
            subscription.PaymentStanding = ClientSubscriptionPaymentStanding.Failed;
            subscription.CancelAtPeriodEnd = false;
            subscription.CancelledUtc = cancelledUtc;
            subscription.GracePeriodEndsUtc = null;
            subscription.NextBillingDateUtc = null;
        }
        else if (mappedStatus != ClientSubscriptionStatus.Canceled)
        {
            subscription.Status = ClientSubscriptionStatus.Active;
        }
        else
        {
            subscription.Status = mappedStatus;
            if (subscription.Status == ClientSubscriptionStatus.Canceled)
                subscription.CancelledUtc = cancelledUtc;
        }

        AddAuditEntry("ClientSubscription", subscription.Id, "cancelled", null, subscription.Status.ToString(), command.ActorType, command.ActorId, "billing_orchestrator", null, correlationId);
        await _db.SaveChangesAsync(cancellationToken);

        var entitlement = await _entitlements.RefreshAsync(subscription.ClientProfileId, BillingEntitlementKeys.ClientAppFullAccess, "SUBSCRIPTION_CANCELLED", cancellationToken);
        return new CancelClientSubscriptionResult(true, null, "Client subscription cancellation recorded.", providerResult.ProviderRequestId, false, subscription, entitlement, providerResult);
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
        subscription.ProviderCustomerId = null;
        subscription.ProviderPaymentMethodId = null;
        subscription.ProviderSubscriptionId = null;
        subscription.ProviderPlanVariationId = null;
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
        var result = new BillingSubscriptionResult(
            true,
            null,
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
                        !BillingStateMapper.IsTerminal(x.Status))
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<ClientSubscription?> FindBlockingSubscriptionAsync(Guid clientProfileId, Guid offerId, CancellationToken cancellationToken)
    {
        return await _db.ClientSubscriptions
            .Where(x => x.ClientProfileId == clientProfileId &&
                        x.AcceptedOfferId != offerId &&
                        !BillingStateMapper.IsTerminal(x.Status))
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

    private static void ApplyProviderSubscriptionResult(ClientSubscription subscription, BillingSubscriptionResult providerResult, DateTime nowUtc)
    {
        subscription.ProviderCustomerId = providerResult.ProviderCustomerId ?? subscription.ProviderCustomerId;
        subscription.ProviderPaymentMethodId = providerResult.ProviderPaymentMethodId ?? subscription.ProviderPaymentMethodId;
        subscription.ProviderSubscriptionId = providerResult.ExternalId ?? subscription.ProviderSubscriptionId;
        subscription.ProviderPlanVariationId = providerResult.ProviderPlanVariationId ?? subscription.ProviderPlanVariationId;
        subscription.MonthlyAmountCents = providerResult.AmountCents ?? subscription.MonthlyAmountCents;
        subscription.Currency = providerResult.Currency ?? subscription.Currency;
        subscription.BillingAnchorDay = providerResult.BillingAnchorDay ?? subscription.BillingAnchorDay;
        subscription.Status = BillingStateMapper.MapSubscriptionStatus(providerResult.NormalizedStatus);
        subscription.PaymentStanding = BillingStateMapper.MapPaymentStanding(providerResult.NormalizedStatus);
        subscription.CurrentPeriodStartUtc = providerResult.CurrentPeriodStartUtc ?? subscription.CurrentPeriodStartUtc;
        subscription.CurrentPeriodEndUtc = providerResult.CurrentPeriodEndUtc ?? subscription.CurrentPeriodEndUtc;
        subscription.NextBillingDateUtc = providerResult.NextBillingDateUtc ?? subscription.NextBillingDateUtc;
        subscription.CancelAtPeriodEnd = providerResult.CancelAtPeriodEnd ?? subscription.CancelAtPeriodEnd;
        subscription.ActivatedUtc ??= nowUtc;
        subscription.UpdatedUtc = nowUtc;
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
