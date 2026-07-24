using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers.API;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Billing;
using Infrastructure.Billing.Square;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class BillingCentralizationTests
{
    [Theory]
    [InlineData(ClientSubscriptionOfferPriceType.Fixed50, null, ClientSubscriptionOfferPricing.Fixed50Cents)]
    [InlineData(ClientSubscriptionOfferPriceType.Fixed75, null, ClientSubscriptionOfferPricing.Fixed75Cents)]
    [InlineData(ClientSubscriptionOfferPriceType.Fixed100, null, ClientSubscriptionOfferPricing.Fixed100Cents)]
    [InlineData(ClientSubscriptionOfferPriceType.Fixed150, null, ClientSubscriptionOfferPricing.Fixed150Cents)]
    [InlineData(ClientSubscriptionOfferPriceType.Custom, 123_45, 123_45)]
    public void OfferPricing_UsesSingleAuthoritativeAmountMapping(
        ClientSubscriptionOfferPriceType priceType,
        int? customAmountCents,
        int expectedAmountCents)
    {
        var resolved = ClientSubscriptionOfferPricing.ResolveAuthoritativeMonthlyAmountCents(priceType, customAmountCents);

        Assert.Equal(expectedAmountCents, resolved);
    }

    [Fact]
    public void OfferPricing_InvalidCustomAmount_IsRejected()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ClientSubscriptionOfferPricing.ResolveAuthoritativeMonthlyAmountCents(
                ClientSubscriptionOfferPriceType.Custom,
                ClientSubscriptionOfferPricing.CustomMaximumCents + 1));

        Assert.Contains("Custom offers must provide a monthly amount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OfferPricing_ZeroCustomAmount_RequiresFounderAllowance()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ClientSubscriptionOfferPricing.ResolveAuthoritativeMonthlyAmountCents(
                ClientSubscriptionOfferPriceType.Custom,
                0));

        var founderAmount = ClientSubscriptionOfferPricing.ResolveAuthoritativeMonthlyAmountCents(
            ClientSubscriptionOfferPriceType.Custom,
            0,
            ClientSubscriptionOfferPricing.FounderCustomMinimumCents);

        Assert.Equal(0, founderAmount);
    }

    [Fact]
    public void OfferPricing_InvalidSpecificBillingAnchor_IsRejected()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ClientSubscriptionOfferPricing.ResolveBillingAnchorDay(
                BillingAnchorSelectionMode.SpecificDayOfMonth,
                32));

        Assert.Contains("Specific-day billing anchors", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateClientSubscriptionOffer_SupersedesExistingDraft_AndUsesAuthoritativeBillingValues()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var existingOffer = await AddOfferAsync(
            db,
            profile.Id,
            ownerAgentUserId: "agent-1",
            priceType: ClientSubscriptionOfferPriceType.Fixed50,
            monthlyAmountCents: ClientSubscriptionOfferPricing.Fixed50Cents,
            billingAnchorSelectionMode: BillingAnchorSelectionMode.FirstOfMonth,
            selectedBillingAnchorDay: null,
            status: ClientSubscriptionOfferStatus.Draft);

        var orchestrator = BuildOrchestrator(db);

        var created = await orchestrator.CreateClientSubscriptionOfferAsync(
            new CreateClientSubscriptionOfferCommand(
                profile.Id,
                "agent-1",
                ClientSubscriptionOfferPriceType.Fixed150,
                null,
                "usd",
                BillingAnchorSelectionMode.SpecificDayOfMonth,
                21,
                DateTime.UtcNow.AddMinutes(-1),
                null));

        var persistedExisting = await db.ClientSubscriptionOffers.FindAsync(existingOffer.Id);
        Assert.NotNull(persistedExisting);
        Assert.Equal(ClientSubscriptionOfferStatus.Superseded, persistedExisting!.Status);
        Assert.Equal(ClientSubscriptionOfferPricing.Fixed150Cents, created.MonthlyAmountCents);
        Assert.Equal("USD", created.Currency);
        Assert.Equal(BillingAnchorSelectionMode.SpecificDayOfMonth, created.BillingAnchorSelectionMode);
        Assert.Equal(21, created.SelectedBillingAnchorDay);
        Assert.Equal(ClientSubscriptionOfferStatus.Offered, created.Status);
    }

    [Fact]
    public async Task CreateSubscriptionActivationInvitation_SupersedesPriorInvitation_AndStoresOnlyTokenHash()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var priorInvitation = new SubscriptionActivationInvitation
        {
            ClientProfileId = profile.Id,
            ClientSubscriptionOfferId = offer.Id,
            TokenHash = BillingIdempotency.Hash("prior-token"),
            IntendedNormalizedEmail = profile.NormalizedEmail!,
            Status = SubscriptionActivationInvitationStatus.Sent,
            ExpiresUtc = DateTime.UtcNow.AddDays(2),
            CreatedByAgentUserId = "agent-1",
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            LastSentUtc = DateTime.UtcNow.AddDays(-1),
            SendCount = 1
        };
        db.SubscriptionActivationInvitations.Add(priorInvitation);
        await db.SaveChangesAsync();

        var orchestrator = BuildOrchestrator(db);

        var result = await orchestrator.CreateSubscriptionActivationInvitationAsync(
            new CreateSubscriptionActivationInvitationCommand(
                profile.Id,
                offer.Id,
                profile.Email,
                "agent-1",
                DateTime.UtcNow.AddDays(7)));

        Assert.True(result.Success);
        Assert.NotNull(result.Invitation);
        Assert.False(string.IsNullOrWhiteSpace(result.PlainTextToken));

        var latestInvitation = await db.SubscriptionActivationInvitations
            .OrderByDescending(x => x.CreatedUtc)
            .FirstAsync();
        var persistedPrior = await db.SubscriptionActivationInvitations.FindAsync(priorInvitation.Id);

        Assert.NotNull(persistedPrior);
        Assert.Equal(SubscriptionActivationInvitationStatus.Superseded, persistedPrior!.Status);
        Assert.True(persistedPrior.SupersededUtc.HasValue);
        Assert.Equal(BillingIdempotency.Hash(result.PlainTextToken!), latestInvitation.TokenHash);
        Assert.NotEqual(result.PlainTextToken, latestInvitation.TokenHash);
        Assert.Equal(SubscriptionActivationInvitationStatus.Pending, latestInvitation.Status);
        Assert.Equal(profile.NormalizedEmail, latestInvitation.IntendedNormalizedEmail);
    }

    [Fact]
    public async Task ActivateClientSubscription_ZeroDollarOffer_ActivatesWithoutAnySquareCalls()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(
            db,
            profile.Id,
            priceType: ClientSubscriptionOfferPriceType.Custom,
            monthlyAmountCents: 0,
            selectedBillingAnchorDay: 1);
        var invitation = new SubscriptionActivationInvitation
        {
            ClientProfileId = profile.Id,
            ClientSubscriptionOfferId = offer.Id,
            TokenHash = BillingIdempotency.Hash("zero-dollar-token"),
            IntendedNormalizedEmail = profile.NormalizedEmail!,
            Status = SubscriptionActivationInvitationStatus.Pending,
            ExpiresUtc = DateTime.UtcNow.AddDays(2),
            CreatedByAgentUserId = "founder-oid",
            CreatedUtc = DateTime.UtcNow
        };
        db.SubscriptionActivationInvitations.Add(invitation);
        await db.SaveChangesAsync();

        var gateway = BuildGateway();
        var orchestrator = new MasterAppBillingOrchestrator(db, gateway.Object, BuildEntitlementService(db), Mock.Of<IClientSubscriptionActivationPolicyService>());
        var firstChargeUtc = DateTime.UtcNow;
        var renewalUtc = firstChargeUtc.AddMonths(1);

        var result = await orchestrator.ActivateClientSubscriptionAsync(
            new ActivateClientSubscriptionCommand(
                profile.Id,
                offer.Id,
                "founder-oid",
                string.Empty,
                "USD",
                1,
                "America/Phoenix",
                firstChargeUtc,
                renewalUtc,
                DateOnly.FromDateTime(renewalUtc),
                true,
                true,
                true,
                profile.NormalizedEmail!,
                invitation.Id,
                null,
                string.Empty,
                "zero-dollar-correlation",
                "zero-dollar-idempotency",
                new BillingPostalAddress(null, null, null, null, null, "US")));

        var subscription = await db.ClientSubscriptions.SingleAsync();
        var entitlement = await db.ClientEntitlements.SingleAsync(x => x.ClientProfileId == profile.Id);
        var persistedInvitation = await db.SubscriptionActivationInvitations.SingleAsync(x => x.Id == invitation.Id);

        Assert.True(result.Success);
        Assert.Equal(ClientSubscriptionStatus.Active, subscription.Status);
        Assert.Equal(ClientSubscriptionPaymentStanding.Current, subscription.PaymentStanding);
        Assert.Null(subscription.ProviderCustomerId);
        Assert.Null(subscription.ProviderPaymentMethodId);
        Assert.Null(subscription.ProviderSubscriptionId);
        Assert.Equal(ClientSubscriptionOfferStatus.Accepted, offer.Status);
        Assert.Equal(SubscriptionActivationInvitationStatus.Redeemed, persistedInvitation.Status);
        Assert.Equal(ClientEntitlementStatus.Active, entitlement.Status);
        Assert.Empty(await db.SubscriptionPayments.ToListAsync());
        gateway.Verify(x => x.ResolveCustomerAsync(It.IsAny<BillingCustomerResolutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        gateway.Verify(x => x.AttachPaymentMethodAsync(It.IsAny<BillingPaymentMethodAttachmentRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        gateway.Verify(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ActivateClientSubscription_UsesVaultedSourceAndPlatformLifecycleWithoutAPlanVariation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(
            db,
            profile.Id,
            priceType: ClientSubscriptionOfferPriceType.Custom,
            monthlyAmountCents: 12_345,
            selectedBillingAnchorDay: 15);
        var gateway = BuildGateway();
        gateway.Setup(x => x.ResolveCustomerAsync(It.IsAny<BillingCustomerResolutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingCustomerResolutionResult(true, "cust_123", "COMPLETED", null, "Customer resolved.", "req_customer", false, "cust_123"));
        gateway.Setup(x => x.AttachPaymentMethodAsync(It.IsAny<BillingPaymentMethodAttachmentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingPaymentMethodAttachmentResult(true, "card_123", "COMPLETED", null, "Card vaulted.", "req_card", false, "cust_123", "card_123"));
        gateway.Setup(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingOneTimePaymentResult(true, "pay_initial_123", "COMPLETED", null, "Payment completed.", "req_payment", false));

        var orchestrator = new MasterAppBillingOrchestrator(
            db,
            gateway.Object,
            BuildEntitlementService(db),
            Mock.Of<IClientSubscriptionActivationPolicyService>());
        var firstChargeUtc = DateTime.UtcNow;
        var renewalUtc = firstChargeUtc.AddMonths(1);
        var command = new ActivateClientSubscriptionCommand(
            profile.Id,
            offer.Id,
            "agent-1",
            "raw-token-never-persisted",
            "USD",
            15,
            "UTC",
            firstChargeUtc,
            renewalUtc,
            DateOnly.FromDateTime(renewalUtc),
            true,
            true,
            true,
            profile.NormalizedEmail!,
            null,
            null,
            "Client One",
            "activation-correlation",
            "activation-idempotency",
            new BillingPostalAddress(null, null, null, null, null, "US"));

        var activated = await orchestrator.ActivateClientSubscriptionAsync(command);
        var duplicate = await orchestrator.ActivateClientSubscriptionAsync(command);
        var subscription = await db.ClientSubscriptions.SingleAsync();
        var payment = await db.SubscriptionPayments.SingleAsync();
        var entitlement = await db.ClientEntitlements.SingleAsync(x => x.ClientProfileId == profile.Id);

        Assert.True(activated.Success);
        Assert.False(duplicate.Success);
        Assert.Equal("ACTIVE_SUBSCRIPTION_EXISTS", duplicate.SafeErrorCode);
        Assert.True(subscription.IsPlatformManaged);
        Assert.NotNull(subscription.PlatformManagedSinceUtc);
        Assert.Null(subscription.ProviderSubscriptionId);
        Assert.Null(subscription.ProviderPlanVariationId);
        Assert.Equal(12_345, subscription.MonthlyAmountCents);
        Assert.Equal(ClientSubscriptionStatus.Active, subscription.Status);
        Assert.Equal(SubscriptionPaymentKind.InitialActivation, payment.Kind);
        Assert.Equal(12_345, payment.AmountCents);
        Assert.False(string.IsNullOrWhiteSpace(payment.IdempotencyKey));
        Assert.Equal(ClientEntitlementStatus.Active, entitlement.Status);
        gateway.Verify(
            x => x.CreateOneTimePaymentAsync(
                It.Is<BillingOneTimePaymentRequest>(request =>
                    request.SourceId == "card_123" &&
                    request.ExistingProviderCustomerId == "cust_123" &&
                    request.AmountCents == 12_345 &&
                    !string.IsNullOrWhiteSpace(request.IdempotencyKey)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlatformManagedRenewal_ChargesOnceAndAdvancesThePeriodExactlyOnce()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            status: ClientSubscriptionStatus.Active,
            paymentStanding: ClientSubscriptionPaymentStanding.Current,
            providerSubscriptionId: "historical-reference-only");
        var dueUtc = DateTime.UtcNow.AddMinutes(-1);
        subscription.BillingTimeZoneId = "UTC";
        subscription.BillingAnchorDay = null;
        subscription.CurrentPeriodStartUtc = dueUtc.AddMonths(-1);
        subscription.CurrentPeriodEndUtc = dueUtc;
        subscription.NextBillingDateUtc = dueUtc;
        subscription.NextChargeAttemptUtc = dueUtc;
        await db.SaveChangesAsync();

        var gateway = BuildGateway();
        gateway.Setup(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingOneTimePaymentResult(true, "pay_renewal_123", "COMPLETED", null, "Payment completed.", "req_renewal", false));
        var orchestrator = new MasterAppBillingOrchestrator(
            db,
            gateway.Object,
            BuildEntitlementService(db),
            new ClientSubscriptionActivationPolicyService(new ClientSubscriptionActivationPolicyOptions { BusinessTimeZoneId = "UTC" }));

        var firstRun = await orchestrator.ProcessDueClientSubscriptionRenewalsAsync(10, "worker-a");
        var secondRun = await orchestrator.ProcessDueClientSubscriptionRenewalsAsync(10, "worker-b");
        var payment = await db.SubscriptionPayments.SingleAsync();
        var renewed = await db.ClientSubscriptions.SingleAsync(x => x.Id == subscription.Id);

        Assert.Equal(1, firstRun.ChargesAttempted);
        Assert.Equal(1, firstRun.ChargesSucceeded);
        Assert.Equal(0, secondRun.ChargesAttempted);
        Assert.Equal(SubscriptionPaymentKind.Renewal, payment.Kind);
        Assert.Equal(SubscriptionPaymentStatus.Completed, payment.Status);
        Assert.Equal(dueUtc, payment.BillingPeriodStartUtc);
        Assert.False(string.IsNullOrWhiteSpace(payment.IdempotencyKey));
        Assert.Equal(ClientSubscriptionStatus.Active, renewed.Status);
        Assert.True(renewed.NextBillingDateUtc > dueUtc);
        Assert.Equal(renewed.NextBillingDateUtc, renewed.NextChargeAttemptUtc);
        gateway.Verify(
            x => x.CreateOneTimePaymentAsync(
                It.Is<BillingOneTimePaymentRequest>(request =>
                    request.SourceId == "card_123" &&
                    request.ExistingProviderCustomerId == "cust_123"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlatformManagedRenewal_RetryableFailureUsesTheCentralRetrySchedule()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            status: ClientSubscriptionStatus.Active,
            paymentStanding: ClientSubscriptionPaymentStanding.Current,
            providerSubscriptionId: "historical-reference-only");
        var dueUtc = DateTime.UtcNow.AddMinutes(-1);
        subscription.BillingTimeZoneId = "UTC";
        subscription.BillingAnchorDay = null;
        subscription.CurrentPeriodEndUtc = dueUtc;
        subscription.NextBillingDateUtc = dueUtc;
        subscription.NextChargeAttemptUtc = dueUtc;
        await db.SaveChangesAsync();

        var gateway = BuildGateway();
        gateway.SetupSequence(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingOneTimePaymentResult(false, null, "FAILED", "TEMPORARY_PROCESSOR_FAILURE", "Temporary failure.", "req_retry_1", true))
            .ReturnsAsync(new BillingOneTimePaymentResult(true, "pay_retry_2", "COMPLETED", null, "Payment completed.", "req_retry_2", false));
        var orchestrator = new MasterAppBillingOrchestrator(
            db,
            gateway.Object,
            BuildEntitlementService(db),
            new ClientSubscriptionActivationPolicyService(new ClientSubscriptionActivationPolicyOptions
            {
                BusinessTimeZoneId = "UTC",
                GracePeriodDays = 3,
                RenewalRetryDelayMinutes = [60]
            }));

        var failedRun = await orchestrator.ProcessDueClientSubscriptionRenewalsAsync(10, "worker-a");
        var failedAttempt = await db.SubscriptionPayments.SingleAsync();
        var deferredRun = await orchestrator.ProcessDueClientSubscriptionRenewalsAsync(10, "worker-b");
        subscription.NextChargeAttemptUtc = DateTime.UtcNow.AddMinutes(-1);
        failedAttempt.RetryNotBeforeUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        var recoveredRun = await orchestrator.ProcessDueClientSubscriptionRenewalsAsync(10, "worker-c");
        var attempts = await db.SubscriptionPayments.OrderBy(x => x.AttemptNumber).ToListAsync();
        var recovered = await db.ClientSubscriptions.SingleAsync(x => x.Id == subscription.Id);

        Assert.Equal(1, failedRun.ChargesFailed);
        Assert.Equal(ClientSubscriptionStatus.GracePeriod, subscription.Status);
        Assert.True(failedAttempt.RetryNotBeforeUtc.HasValue);
        Assert.Equal(0, deferredRun.ChargesAttempted);
        Assert.Equal(1, recoveredRun.ChargesSucceeded);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(new[] { 1, 2 }, attempts.Select(x => x.AttemptNumber).ToArray());
        Assert.NotEqual(attempts[0].IdempotencyKey, attempts[1].IdempotencyKey);
        Assert.Equal(ClientSubscriptionStatus.Active, recovered.Status);
        gateway.Verify(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task HistoricalProviderManagedSubscription_CannotBeCancelledOrChargedLocally()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            status: ClientSubscriptionStatus.Active,
            paymentStanding: ClientSubscriptionPaymentStanding.Current,
            providerSubscriptionId: "sub_legacy_123");
        subscription.IsPlatformManaged = false;
        subscription.NextBillingDateUtc = DateTime.UtcNow.AddMinutes(-1);
        subscription.NextChargeAttemptUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var gateway = BuildGateway();
        var orchestrator = new MasterAppBillingOrchestrator(
            db,
            gateway.Object,
            BuildEntitlementService(db),
            new ClientSubscriptionActivationPolicyService(new ClientSubscriptionActivationPolicyOptions { BusinessTimeZoneId = "UTC" }));

        var cancellation = await orchestrator.CancelClientSubscriptionAsync(
            new CancelClientSubscriptionCommand(subscription.Id, true, BillingActorType.Agent, "agent-1"));
        var renewal = await orchestrator.ProcessDueClientSubscriptionRenewalsAsync(10, "worker-a");

        Assert.False(cancellation.Success);
        Assert.Equal("LEGACY_PROVIDER_MANAGED_SUBSCRIPTION", cancellation.SafeErrorCode);
        Assert.Equal(0, renewal.ChargesAttempted);
        gateway.Verify(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProviderEventProcessor_IsIdempotentPerEnvironment_AndSeparatesSandboxFromProduction()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var processor = new BillingProviderEventProcessor(db);
        var receivedUtc = DateTime.UtcNow;
        var payload = BuildSubscriptionPayload("evt_123", "subscription.updated", "sub_123", "cust_123", "ACTIVE", "2026-07-22T01:00:00Z");

        var first = await processor.ProcessAsync(new BillingProviderEventEnvelope(
            BillingProvider.Square,
            BillingProviderEnvironment.Sandbox,
            "evt_123",
            "subscription.updated",
            payload,
            receivedUtc,
            "sub_123"));

        var duplicate = await processor.ProcessAsync(new BillingProviderEventEnvelope(
            BillingProvider.Square,
            BillingProviderEnvironment.Sandbox,
            "evt_123",
            "subscription.updated",
            payload,
            receivedUtc.AddSeconds(1),
            "sub_123"));

        var production = await processor.ProcessAsync(new BillingProviderEventEnvelope(
            BillingProvider.Square,
            BillingProviderEnvironment.Production,
            "evt_123",
            "subscription.updated",
            payload,
            receivedUtc.AddSeconds(2),
            "sub_123"));

        Assert.True(first.Success);
        Assert.True(duplicate.Success);
        Assert.True(production.Success);
        Assert.NotNull(first.EventRecord);
        Assert.NotNull(duplicate.EventRecord);
        Assert.NotNull(production.EventRecord);
        Assert.Equal(first.EventRecord!.Id, duplicate.EventRecord!.Id);
        Assert.NotEqual(first.EventRecord.Id, production.EventRecord!.Id);
        Assert.Equal(2, await db.BillingProviderEvents.CountAsync());
    }

    [Fact]
    public async Task UnsupportedEvent_IsExplicitlyIgnored()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var processor = new BillingProviderEventProcessor(db);

        var result = await processor.ProcessAsync(new BillingProviderEventEnvelope(
            BillingProvider.Square,
            BillingProviderEnvironment.Sandbox,
            "evt_catalog_123",
            "catalog.version.updated",
            BuildUnsupportedPayload("evt_catalog_123", "catalog.version.updated", "catalog_version", "catalog_123"),
            DateTime.UtcNow,
            "catalog_123"));

        Assert.True(result.Success);
        Assert.NotNull(result.EventRecord);
        Assert.Equal(BillingProviderEventProcessingStatus.IgnoredUnsupported, result.EventRecord!.ProcessingStatus);
        Assert.Equal("WEBHOOK_EVENT_UNSUPPORTED", result.EventRecord.SafeErrorCode);
        Assert.NotNull(result.EventRecord.ProcessedUtc);
    }

    [Fact]
    public async Task ProviderEventProcessor_PersistsSanitizedSummaryWithoutRawPii()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var payload = """
        {
          "event_id": "evt_pii_123",
          "type": "payment.updated",
          "data": {
            "type": "payment",
            "id": "pay_pii_123",
            "object": {
              "payment": {
                "id": "pay_pii_123",
                "customer_id": "cust_123",
                "invoice_id": "inv_123",
                "status": "COMPLETED",
                "source_id": "cnon:card-nonce-secret",
                "card_details": {
                  "card": {
                    "cardholder_name": "Alice Example",
                    "last_4": "1111"
                  }
                },
                "buyer_email_address": "alice@example.com",
                "reference_id": "4111111111111111",
                "updated_at": "2026-07-22T01:30:00Z"
              }
            }
          }
        }
        """;

        var processor = new BillingProviderEventProcessor(db);
        var result = await processor.ProcessAsync(new BillingProviderEventEnvelope(
            BillingProvider.Square,
            BillingProviderEnvironment.Sandbox,
            "evt_pii_123",
            "payment.updated",
            payload,
            DateTime.UtcNow,
            "pay_pii_123"));

        var stored = await db.BillingProviderEvents.SingleAsync();

        Assert.True(result.Success);
        Assert.NotNull(stored.RetainedPayloadJson);
        Assert.Contains("\"paymentId\":\"pay_pii_123\"", stored.RetainedPayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("alice@example.com", stored.RetainedPayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("4111111111111111", stored.RetainedPayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("cnon:card-nonce-secret", stored.RetainedPayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice Example", stored.RetainedPayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebhookSignatureValidation_RequiresExactRawPayload()
    {
        const string notificationUrl = "https://portal.example.com/api/billing/webhooks/square";
        const string payload = "{\"event_id\":\"evt_123\",\"type\":\"subscription.updated\"}";
        var options = new SquareBillingOptions
        {
            WebhookSignatureKey = "webhook-secret",
            WebhookNotificationUrl = notificationUrl
        };

        var signature = ComputeSquareSignature(options.WebhookSignatureKey!, notificationUrl, payload);
        var validator = new SquareBillingWebhookSignatureValidator(options);

        var valid = await validator.ValidateAsync(new BillingWebhookSignatureValidationRequest(
            BillingProvider.Square,
            notificationUrl,
            payload,
            signature));

        var invalid = await validator.ValidateAsync(new BillingWebhookSignatureValidationRequest(
            BillingProvider.Square,
            notificationUrl,
            $"{payload} ",
            signature));

        Assert.True(valid.Success);
        Assert.False(invalid.Success);
        Assert.Equal("WEBHOOK_SIGNATURE_INVALID", invalid.SafeErrorCode);
    }

    [Fact]
    public void BillingWebhookRoute_ComposesToCanonicalSquarePath()
    {
        var controllerRoute = Assert.Single(typeof(BillingWebhooksController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>());
        var action = typeof(BillingWebhooksController).GetMethod(nameof(BillingWebhooksController.Square));

        Assert.NotNull(action);

        var actionRoute = Assert.Single(action!
            .GetCustomAttributes(typeof(HttpPostAttribute), inherit: false)
            .Cast<HttpPostAttribute>());

        Assert.Equal("api/billing/webhooks", controllerRoute.Template);
        Assert.Equal("square", actionRoute.Template);
        Assert.Equal("api/billing/webhooks/square", $"{controllerRoute.Template}/{actionRoute.Template}");
    }

    [Fact]
    public async Task EntitlementRefresh_TransitionsAcrossGraceRestrictionSuspensionAndRecovery()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            status: ClientSubscriptionStatus.Active,
            paymentStanding: ClientSubscriptionPaymentStanding.Current,
            providerSubscriptionId: "sub_123");

        var service = BuildEntitlementService(db);

        var active = await service.RefreshAsync(profile.Id, BillingEntitlementKeys.ClientAppFullAccess);
        Assert.Equal(ClientEntitlementStatus.Active, active.Status);

        subscription.Status = ClientSubscriptionStatus.GracePeriod;
        subscription.GracePeriodEndsUtc = DateTime.UtcNow.AddDays(3);
        subscription.UpdatedUtc = DateTime.UtcNow.AddMinutes(1);
        await db.SaveChangesAsync();

        var grace = await service.RefreshAsync(profile.Id, BillingEntitlementKeys.ClientAppFullAccess);
        Assert.Equal(ClientEntitlementStatus.GracePeriod, grace.Status);
        Assert.Equal("SUBSCRIPTION_GRACE_PERIOD", grace.ReasonCode);

        subscription.Status = ClientSubscriptionStatus.PastDue;
        subscription.GracePeriodEndsUtc = DateTime.UtcNow.AddMinutes(-5);
        subscription.UpdatedUtc = DateTime.UtcNow.AddMinutes(2);
        await db.SaveChangesAsync();

        var restricted = await service.RefreshAsync(profile.Id, BillingEntitlementKeys.ClientAppFullAccess);
        Assert.Equal(ClientEntitlementStatus.Restricted, restricted.Status);
        Assert.Equal("SUBSCRIPTION_PAST_DUE", restricted.ReasonCode);

        subscription.Status = ClientSubscriptionStatus.Suspended;
        subscription.UpdatedUtc = DateTime.UtcNow.AddMinutes(3);
        await db.SaveChangesAsync();

        var suspended = await service.RefreshAsync(profile.Id, BillingEntitlementKeys.ClientAppFullAccess);
        Assert.Equal(ClientEntitlementStatus.Suspended, suspended.Status);
        Assert.Equal("SUBSCRIPTION_SUSPENDED", suspended.ReasonCode);

        subscription.Status = ClientSubscriptionStatus.Active;
        subscription.PaymentStanding = ClientSubscriptionPaymentStanding.Current;
        subscription.GracePeriodEndsUtc = null;
        subscription.UpdatedUtc = DateTime.UtcNow.AddMinutes(4);
        await db.SaveChangesAsync();

        var recovered = await service.RefreshAsync(profile.Id, BillingEntitlementKeys.ClientAppFullAccess);
        Assert.Equal(active.Id, recovered.Id);
        Assert.Equal(ClientEntitlementStatus.Active, recovered.Status);
        Assert.Null(recovered.ReasonCode);
    }

    [Fact]
    public async Task ReconcileSubscription_DoesNotReadProviderSubscriptionState()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            status: ClientSubscriptionStatus.Active,
            paymentStanding: ClientSubscriptionPaymentStanding.Current,
            providerSubscriptionId: "sub_123");

        var entitlements = new Mock<IBillingEntitlementService>(MockBehavior.Strict);
        var service = new BillingReconciliationService(db, BuildGateway().Object, entitlements.Object);

        var reconciled = await service.ReconcileSubscriptionAsync(subscription.Id);

        Assert.NotNull(reconciled);
        Assert.Equal(ClientSubscriptionStatus.Active, reconciled!.Status);
        entitlements.Verify(
            x => x.RefreshAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProviderSubscriptionEvent_IsRecordedAndIgnoredWithoutLifecycleMutation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            status: ClientSubscriptionStatus.ReconciliationRequired,
            paymentStanding: ClientSubscriptionPaymentStanding.Unknown,
            providerSubscriptionId: "sub_123");

        await RecordProviderEventAsync(
            db,
            BuildSubscriptionPayload(
                "evt_sub_123",
                "subscription.updated",
                "sub_123",
                "cust_123",
                "ACTIVE",
                "2026-07-22T01:00:00Z"));

        var service = new BillingReconciliationService(db, BuildGateway().Object, BuildEntitlementService(db));

        var processedCount = await service.ReconcilePendingProviderEventsAsync(10);

        var updatedSubscription = await db.ClientSubscriptions.SingleAsync(x => x.Id == subscription.Id);
        var entitlement = await db.ClientEntitlements.FirstOrDefaultAsync(x => x.ClientProfileId == profile.Id);
        var providerEvent = await db.BillingProviderEvents.SingleAsync();

        Assert.Equal(1, processedCount);
        Assert.Equal(ClientSubscriptionStatus.ReconciliationRequired, updatedSubscription.Status);
        Assert.Equal(ClientSubscriptionPaymentStanding.Unknown, updatedSubscription.PaymentStanding);
        Assert.Null(entitlement);
        Assert.Equal(BillingProviderEventProcessingStatus.IgnoredUnsupported, providerEvent.ProcessingStatus);
        Assert.Equal(1, providerEvent.AttemptCount);
        Assert.NotNull(providerEvent.ProcessedUtc);
        Assert.Null(providerEvent.RetryUtc);
    }

    [Fact]
    public async Task PaymentEvent_ResolvesByProviderPaymentId_WithoutOverridingPlatformLifecycle()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            status: ClientSubscriptionStatus.PastDue,
            paymentStanding: ClientSubscriptionPaymentStanding.PastDue,
            providerSubscriptionId: "sub_123");

        var payment = new SubscriptionPayment
        {
            ClientSubscriptionId = subscription.Id,
            Provider = BillingProvider.Square,
            ProviderEnvironment = BillingProviderEnvironment.Sandbox,
            ProviderPaymentId = "pay_123",
            ProviderInvoiceId = "inv_123",
            AmountCents = ClientSubscriptionOfferPricing.Fixed100Cents,
            Currency = "USD",
            Status = SubscriptionPaymentStatus.Pending,
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            UpdatedUtc = DateTime.UtcNow.AddDays(-1)
        };
        db.SubscriptionPayments.Add(payment);
        await db.SaveChangesAsync();

        await RecordProviderEventAsync(
            db,
            BuildPaymentPayload(
                "evt_pay_123",
                "payment.updated",
                "pay_123",
                "cust_123",
                "inv_123",
                "COMPLETED",
                "2026-07-22T01:05:00Z"));

        var gateway = BuildGateway();
        gateway
            .Setup(x => x.GetPaymentAsync(
                It.Is<BillingPaymentLookupRequest>(request => request.ProviderPaymentId == "pay_123"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingPaymentResult(
                true,
                "pay_123",
                "COMPLETED",
                null,
                "Payment completed.",
                "req_pay_123",
                false,
                ProviderInvoiceId: "inv_123",
                AmountCents: ClientSubscriptionOfferPricing.Fixed100Cents,
                Currency: "USD",
                ProviderOccurredUtc: DateTime.Parse("2026-07-22T01:05:00Z")));

        var service = new BillingReconciliationService(db, gateway.Object, BuildEntitlementService(db));

        await service.ReconcilePendingProviderEventsAsync(10);

        var updatedPayment = await db.SubscriptionPayments.SingleAsync(x => x.Id == payment.Id);
        var updatedSubscription = await db.ClientSubscriptions.SingleAsync(x => x.Id == subscription.Id);
        var entitlement = await db.ClientEntitlements.SingleAsync(x => x.ClientProfileId == profile.Id);

        Assert.Equal(SubscriptionPaymentStatus.Completed, updatedPayment.Status);
        Assert.Equal("inv_123", updatedPayment.ProviderInvoiceId);
        Assert.Equal(ClientSubscriptionStatus.PastDue, updatedSubscription.Status);
        Assert.Equal(ClientSubscriptionPaymentStanding.PastDue, updatedSubscription.PaymentStanding);
        Assert.Equal(ClientEntitlementStatus.Restricted, entitlement.Status);
    }

    [Fact]
    public async Task HistoricalInvoiceEvent_IsIgnoredWithoutCreatingAPlatformPaymentAttempt()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            status: ClientSubscriptionStatus.PastDue,
            paymentStanding: ClientSubscriptionPaymentStanding.PastDue,
            providerSubscriptionId: "sub_123");

        await RecordProviderEventAsync(
            db,
            BuildInvoicePayload(
                "evt_inv_123",
                "invoice.payment_made",
                "inv_123",
                "sub_123",
                "cust_123",
                "pay_123",
                "2026-07-22T01:10:00Z"));

        var gateway = BuildGateway();
        gateway
            .Setup(x => x.GetPaymentAsync(
                It.Is<BillingPaymentLookupRequest>(request => request.ProviderPaymentId == "pay_123"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingPaymentResult(
                true,
                "pay_123",
                "COMPLETED",
                null,
                "Invoice payment completed.",
                "req_inv_123",
                false,
                ProviderInvoiceId: "inv_123",
                AmountCents: ClientSubscriptionOfferPricing.Fixed100Cents,
                Currency: "USD",
                ProviderOccurredUtc: DateTime.Parse("2026-07-22T01:10:00Z")));

        var service = new BillingReconciliationService(db, gateway.Object, BuildEntitlementService(db));

        await service.ReconcilePendingProviderEventsAsync(10);

        var updatedSubscription = await db.ClientSubscriptions.SingleAsync(x => x.Id == subscription.Id);
        var providerEvent = await db.BillingProviderEvents.SingleAsync();

        Assert.Empty(await db.SubscriptionPayments.ToListAsync());
        Assert.Equal(ClientSubscriptionStatus.PastDue, updatedSubscription.Status);
        Assert.Equal(BillingProviderEventProcessingStatus.IgnoredUnsupported, providerEvent.ProcessingStatus);
        Assert.Equal("HISTORICAL_PROVIDER_INVOICE_EVENT", providerEvent.SafeErrorCode);
    }

    [Fact]
    public async Task RefundEvent_UpdatesOriginalPayment_AndAuditTrail()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            status: ClientSubscriptionStatus.Active,
            paymentStanding: ClientSubscriptionPaymentStanding.Current,
            providerSubscriptionId: "sub_123");

        var payment = new SubscriptionPayment
        {
            ClientSubscriptionId = subscription.Id,
            Provider = BillingProvider.Square,
            ProviderEnvironment = BillingProviderEnvironment.Sandbox,
            ProviderPaymentId = "pay_123",
            ProviderInvoiceId = "inv_123",
            AmountCents = ClientSubscriptionOfferPricing.Fixed100Cents,
            Currency = "USD",
            Status = SubscriptionPaymentStatus.Completed,
            ProviderOccurredUtc = DateTime.Parse("2026-07-22T01:00:00Z"),
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            UpdatedUtc = DateTime.UtcNow.AddDays(-1)
        };
        db.SubscriptionPayments.Add(payment);
        await db.SaveChangesAsync();

        await RecordProviderEventAsync(
            db,
            BuildRefundPayload(
                "evt_ref_123",
                "refund.updated",
                "ref_123",
                "pay_123",
                "inv_123",
                "2026-07-22T01:15:00Z"));

        var gateway = BuildGateway();
        gateway
            .Setup(x => x.GetRefundAsync(
                It.Is<BillingRefundLookupRequest>(request => request.ProviderRefundId == "ref_123"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingPaymentResult(
                true,
                "pay_123",
                "COMPLETED",
                null,
                "Refund completed.",
                "req_ref_123",
                false,
                ProviderInvoiceId: "inv_123",
                ProviderRefundId: "ref_123",
                AmountCents: 2_500,
                Currency: "USD",
                ProviderOccurredUtc: DateTime.Parse("2026-07-22T01:15:00Z")));

        var service = new BillingReconciliationService(db, gateway.Object, BuildEntitlementService(db));

        await service.ReconcilePendingProviderEventsAsync(10);

        var updatedPayment = await db.SubscriptionPayments.SingleAsync(x => x.Id == payment.Id);
        var refundAudit = await db.BillingAuditEntries.SingleAsync(x => x.Action == "refund_applied");

        Assert.Equal(SubscriptionPaymentStatus.PartiallyRefunded, updatedPayment.Status);
        Assert.Equal("ref_123", updatedPayment.ProviderRefundId);
        Assert.Contains("\"refundId\":\"ref_123\"", refundAudit.SanitizedMetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisputeEvent_UpdatesPaymentStanding_AndSuspendsEntitlement()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            status: ClientSubscriptionStatus.Active,
            paymentStanding: ClientSubscriptionPaymentStanding.Current,
            providerSubscriptionId: "sub_123");

        var payment = new SubscriptionPayment
        {
            ClientSubscriptionId = subscription.Id,
            Provider = BillingProvider.Square,
            ProviderEnvironment = BillingProviderEnvironment.Sandbox,
            ProviderPaymentId = "pay_123",
            AmountCents = ClientSubscriptionOfferPricing.Fixed100Cents,
            Currency = "USD",
            Status = SubscriptionPaymentStatus.Completed,
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            UpdatedUtc = DateTime.UtcNow.AddDays(-1)
        };
        db.SubscriptionPayments.Add(payment);
        await db.SaveChangesAsync();

        await RecordProviderEventAsync(
            db,
            BuildDisputePayload(
                "evt_disp_123",
                "dispute.created",
                "disp_123",
                "pay_123",
                "cust_123",
                "OPEN",
                "2026-07-22T01:20:00Z"));

        var service = new BillingReconciliationService(db, BuildGateway().Object, BuildEntitlementService(db));

        await service.ReconcilePendingProviderEventsAsync(10);

        var updatedPayment = await db.SubscriptionPayments.SingleAsync(x => x.Id == payment.Id);
        var updatedSubscription = await db.ClientSubscriptions.SingleAsync(x => x.Id == subscription.Id);
        var entitlement = await db.ClientEntitlements.SingleAsync(x => x.ClientProfileId == profile.Id);

        Assert.Equal(SubscriptionPaymentStatus.Disputed, updatedPayment.Status);
        Assert.Equal(ClientSubscriptionStatus.Suspended, updatedSubscription.Status);
        Assert.Equal(ClientEntitlementStatus.Suspended, entitlement.Status);
    }

    [Fact]
    public async Task UnresolvedExpectedEvent_StaysRetryable_ThenEscalatesToReconciliationRequired()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await RecordProviderEventAsync(
            db,
            BuildPaymentPayload(
                "evt_unresolved_123",
                "payment.updated",
                "pay_missing",
                "cust_missing",
                "inv_missing",
                "COMPLETED",
                "2026-07-22T01:25:00Z"));

        var gateway = BuildGateway();
        gateway
            .Setup(x => x.GetPaymentAsync(
                It.Is<BillingPaymentLookupRequest>(request => request.ProviderPaymentId == "pay_missing"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingPaymentResult(
                true,
                "pay_missing",
                "COMPLETED",
                null,
                "Payment completed.",
                "req_missing",
                false,
                ProviderInvoiceId: "inv_missing",
                AmountCents: ClientSubscriptionOfferPricing.Fixed100Cents,
                Currency: "USD",
                ProviderOccurredUtc: DateTime.Parse("2026-07-22T01:25:00Z")));

        var service = new BillingReconciliationService(db, gateway.Object, BuildEntitlementService(db));

        await service.ReconcilePendingProviderEventsAsync(10);

        var providerEvent = await db.BillingProviderEvents.SingleAsync();
        Assert.Equal(BillingProviderEventProcessingStatus.Deferred, providerEvent.ProcessingStatus);
        Assert.Equal("PAYMENT_SUBSCRIPTION_NOT_FOUND", providerEvent.SafeErrorCode);
        Assert.NotNull(providerEvent.RetryUtc);

        providerEvent.AttemptCount = 2;
        providerEvent.ProcessingStatus = BillingProviderEventProcessingStatus.Deferred;
        providerEvent.RetryUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        await service.ReconcilePendingProviderEventsAsync(10);

        providerEvent = await db.BillingProviderEvents.SingleAsync();
        Assert.Equal(BillingProviderEventProcessingStatus.ReconciliationRequired, providerEvent.ProcessingStatus);
        Assert.Equal("PAYMENT_SUBSCRIPTION_NOT_FOUND", providerEvent.SafeErrorCode);
        Assert.Null(providerEvent.RetryUtc);
    }

    [Fact]
    public async Task HistoricalInvoicePayment_DoesNotOverridePlatformEntitlement()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            status: ClientSubscriptionStatus.PastDue,
            paymentStanding: ClientSubscriptionPaymentStanding.PastDue,
            providerSubscriptionId: "sub_123");

        var entitlements = BuildEntitlementService(db);
        await entitlements.RefreshAsync(profile.Id, BillingEntitlementKeys.ClientAppFullAccess);

        await RecordProviderEventAsync(
            db,
            BuildInvoicePayload(
                "evt_recovery_123",
                "invoice.payment_made",
                "inv_recovery_123",
                "sub_123",
                "cust_123",
                "pay_recovery_123",
                "2026-07-22T01:35:00Z"));

        var gateway = BuildGateway();
        gateway
            .Setup(x => x.GetPaymentAsync(
                It.Is<BillingPaymentLookupRequest>(request => request.ProviderPaymentId == "pay_recovery_123"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingPaymentResult(
                true,
                "pay_recovery_123",
                "COMPLETED",
                null,
                "Payment completed.",
                "req_recovery_123",
                false,
                ProviderInvoiceId: "inv_recovery_123",
                AmountCents: ClientSubscriptionOfferPricing.Fixed100Cents,
                Currency: "USD",
                ProviderOccurredUtc: DateTime.Parse("2026-07-22T01:35:00Z")));

        var service = new BillingReconciliationService(db, gateway.Object, entitlements);

        await service.ReconcilePendingProviderEventsAsync(10);

        var updatedSubscription = await db.ClientSubscriptions.SingleAsync(x => x.Id == subscription.Id);
        var entitlement = await db.ClientEntitlements.SingleAsync(x => x.ClientProfileId == profile.Id);
        var providerEvent = await db.BillingProviderEvents.SingleAsync();

        Assert.Equal(ClientSubscriptionStatus.PastDue, updatedSubscription.Status);
        Assert.Equal(ClientEntitlementStatus.Restricted, entitlement.Status);
        Assert.Equal(BillingProviderEventProcessingStatus.IgnoredUnsupported, providerEvent.ProcessingStatus);
    }

    [Fact]
    public async Task HistoricalInvoiceFailure_IsIgnoredByThePlatformRenewalAuthority()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            status: ClientSubscriptionStatus.Active,
            paymentStanding: ClientSubscriptionPaymentStanding.Current,
            providerSubscriptionId: "sub_123");

        subscription.CurrentPeriodEndUtc = DateTime.UtcNow.AddDays(5);
        subscription.NextBillingDateUtc = DateTime.UtcNow.AddDays(5);
        await db.SaveChangesAsync();

        await RecordProviderEventAsync(
            db,
            BuildInvoiceFailurePayload(
                "evt_fail_123",
                "invoice.updated",
                "inv_fail_123",
                "sub_123",
                "cust_123",
                "UNPAID",
                "2026-07-22T01:40:00Z"));

        var service = new BillingReconciliationService(db, BuildGateway().Object, BuildEntitlementService(db));

        await service.ReconcilePendingProviderEventsAsync(10);

        var updatedSubscription = await db.ClientSubscriptions.SingleAsync(x => x.Id == subscription.Id);
        var entitlement = await db.ClientEntitlements.FirstOrDefaultAsync(x => x.ClientProfileId == profile.Id);
        var providerEvent = await db.BillingProviderEvents.SingleAsync();

        Assert.Equal(ClientSubscriptionStatus.Active, updatedSubscription.Status);
        Assert.Equal(ClientSubscriptionPaymentStanding.Current, updatedSubscription.PaymentStanding);
        Assert.Null(entitlement);
        Assert.Equal(BillingProviderEventProcessingStatus.IgnoredUnsupported, providerEvent.ProcessingStatus);
    }

    [Fact]
    public async Task FullRefund_FollowsCentralizedEntitlementPolicy()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            status: ClientSubscriptionStatus.Active,
            paymentStanding: ClientSubscriptionPaymentStanding.Current,
            providerSubscriptionId: "sub_123");

        var payment = new SubscriptionPayment
        {
            ClientSubscriptionId = subscription.Id,
            Provider = BillingProvider.Square,
            ProviderEnvironment = BillingProviderEnvironment.Sandbox,
            ProviderPaymentId = "pay_full_refund",
            ProviderInvoiceId = "inv_full_refund",
            AmountCents = ClientSubscriptionOfferPricing.Fixed100Cents,
            Currency = "USD",
            Status = SubscriptionPaymentStatus.Completed,
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            UpdatedUtc = DateTime.UtcNow.AddDays(-1)
        };
        db.SubscriptionPayments.Add(payment);
        await db.SaveChangesAsync();

        await RecordProviderEventAsync(
            db,
            BuildRefundPayload(
                "evt_full_refund",
                "refund.updated",
                "ref_full_123",
                "pay_full_refund",
                "inv_full_refund",
                "2026-07-22T01:45:00Z"));

        var gateway = BuildGateway();
        gateway
            .Setup(x => x.GetRefundAsync(
                It.Is<BillingRefundLookupRequest>(request => request.ProviderRefundId == "ref_full_123"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingPaymentResult(
                true,
                "pay_full_refund",
                "COMPLETED",
                null,
                "Refund completed.",
                "req_full_refund",
                false,
                ProviderInvoiceId: "inv_full_refund",
                ProviderRefundId: "ref_full_123",
                AmountCents: ClientSubscriptionOfferPricing.Fixed100Cents,
                Currency: "USD",
                ProviderOccurredUtc: DateTime.Parse("2026-07-22T01:45:00Z")));

        var service = new BillingReconciliationService(db, gateway.Object, BuildEntitlementService(db));

        await service.ReconcilePendingProviderEventsAsync(10);

        var updatedSubscription = await db.ClientSubscriptions.SingleAsync(x => x.Id == subscription.Id);
        var entitlement = await db.ClientEntitlements.SingleAsync(x => x.ClientProfileId == profile.Id);

        Assert.Equal(ClientSubscriptionStatus.Suspended, updatedSubscription.Status);
        Assert.Equal(ClientEntitlementStatus.Suspended, entitlement.Status);
    }

    [Fact]
    public async Task OutOfOrderDelivery_IsSafeAgainstLateRegression()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            status: ClientSubscriptionStatus.PastDue,
            paymentStanding: ClientSubscriptionPaymentStanding.PastDue,
            providerSubscriptionId: "sub_123");

        var payment = new SubscriptionPayment
        {
            ClientSubscriptionId = subscription.Id,
            Provider = BillingProvider.Square,
            ProviderEnvironment = BillingProviderEnvironment.Sandbox,
            ProviderPaymentId = "pay_out_of_order",
            AmountCents = ClientSubscriptionOfferPricing.Fixed100Cents,
            Currency = "USD",
            Status = SubscriptionPaymentStatus.Pending,
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            UpdatedUtc = DateTime.UtcNow.AddDays(-1)
        };
        db.SubscriptionPayments.Add(payment);
        await db.SaveChangesAsync();

        await RecordProviderEventAsync(
            db,
            BuildPaymentPayload(
                "evt_later_success",
                "payment.updated",
                "pay_out_of_order",
                "cust_123",
                "inv_out_of_order",
                "COMPLETED",
                "2026-07-22T02:00:00Z"));
        await RecordProviderEventAsync(
            db,
            BuildPaymentPayload(
                "evt_earlier_failed",
                "payment.updated",
                "pay_out_of_order",
                "cust_123",
                "inv_out_of_order",
                "FAILED",
                "2026-07-22T01:00:00Z"));

        var gateway = BuildGateway();
        gateway
            .Setup(x => x.GetPaymentAsync(
                It.Is<BillingPaymentLookupRequest>(request => request.ProviderPaymentId == "pay_out_of_order"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingPaymentResult(
                true,
                "pay_out_of_order",
                "COMPLETED",
                null,
                "Current provider state is completed.",
                "req_out_of_order",
                false,
                ProviderInvoiceId: "inv_out_of_order",
                AmountCents: ClientSubscriptionOfferPricing.Fixed100Cents,
                Currency: "USD",
                ProviderOccurredUtc: DateTime.Parse("2026-07-22T02:00:00Z")));

        var service = new BillingReconciliationService(db, gateway.Object, BuildEntitlementService(db));

        await service.ReconcilePendingProviderEventsAsync(10);

        var updatedPayment = await db.SubscriptionPayments.SingleAsync(x => x.Id == payment.Id);
        var updatedSubscription = await db.ClientSubscriptions.SingleAsync(x => x.Id == subscription.Id);
        var events = await db.BillingProviderEvents.OrderBy(x => x.ReceivedUtc).ToListAsync();

        Assert.Equal(SubscriptionPaymentStatus.Completed, updatedPayment.Status);
        Assert.Equal(ClientSubscriptionStatus.PastDue, updatedSubscription.Status);
        Assert.All(events, evt => Assert.Equal(BillingProviderEventProcessingStatus.Processed, evt.ProcessingStatus));
    }

    [Fact]
    public async Task CancelClientSubscription_AtPeriodEnd_KeepsEntitlementActiveUntilTermEnd()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            status: ClientSubscriptionStatus.Active,
            paymentStanding: ClientSubscriptionPaymentStanding.Current,
            providerSubscriptionId: "sub_123");

        var gateway = BuildGateway();

        var entitlements = BuildEntitlementService(db);
        await entitlements.RefreshAsync(profile.Id, BillingEntitlementKeys.ClientAppFullAccess);

        var orchestrator = new MasterAppBillingOrchestrator(db, gateway.Object, entitlements, Mock.Of<IClientSubscriptionActivationPolicyService>());

        var result = await orchestrator.CancelClientSubscriptionAsync(
            new CancelClientSubscriptionCommand(
                subscription.Id,
                true,
                BillingActorType.Agent,
                "agent-1"));

        var updatedSubscription = await db.ClientSubscriptions.SingleAsync(x => x.Id == subscription.Id);
        var entitlement = await db.ClientEntitlements.SingleAsync(x => x.ClientProfileId == profile.Id);

        Assert.True(result.Success);
        Assert.True(updatedSubscription.CancelAtPeriodEnd);
        Assert.Equal(ClientSubscriptionStatus.Active, updatedSubscription.Status);
        Assert.Equal(ClientEntitlementStatus.Active, entitlement.Status);
        Assert.Equal("SUBSCRIPTION_CANCELLED", entitlement.ReasonCode);
    }

    [Fact]
    public async Task CancelClientSubscription_ImmediatelyRevokesClientAppAccess()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            status: ClientSubscriptionStatus.Active,
            paymentStanding: ClientSubscriptionPaymentStanding.Current,
            providerSubscriptionId: "sub_immediate");

        var gateway = BuildGateway();

        var entitlements = BuildEntitlementService(db);
        await entitlements.RefreshAsync(profile.Id, BillingEntitlementKeys.ClientAppFullAccess);

        var orchestrator = new MasterAppBillingOrchestrator(db, gateway.Object, entitlements, Mock.Of<IClientSubscriptionActivationPolicyService>());
        var result = await orchestrator.CancelClientSubscriptionAsync(
            new CancelClientSubscriptionCommand(
                subscription.Id,
                false,
                BillingActorType.Client,
                "client-1"));

        var updatedSubscription = await db.ClientSubscriptions.SingleAsync(x => x.Id == subscription.Id);
        var entitlement = await db.ClientEntitlements.SingleAsync(x => x.ClientProfileId == profile.Id);

        Assert.True(result.Success);
        Assert.False(updatedSubscription.CancelAtPeriodEnd);
        Assert.Equal(ClientSubscriptionStatus.Canceled, updatedSubscription.Status);
        Assert.Equal(ClientSubscriptionPaymentStanding.Failed, updatedSubscription.PaymentStanding);
        Assert.NotNull(updatedSubscription.CancelledUtc);
        Assert.Equal(ClientEntitlementStatus.NotGranted, entitlement.Status);
        Assert.Equal("SUBSCRIPTION_CANCELLED", entitlement.ReasonCode);
    }

    private static MasterAppBillingOrchestrator BuildOrchestrator(MasterAppDbContext db)
    {
        var gateway = BuildGateway();
        var entitlements = new Mock<IBillingEntitlementService>(MockBehavior.Strict);
        return new MasterAppBillingOrchestrator(db, gateway.Object, entitlements.Object, Mock.Of<IClientSubscriptionActivationPolicyService>());
    }

    private static Mock<IBillingGateway> BuildGateway()
    {
        var gateway = new Mock<IBillingGateway>(MockBehavior.Strict);
        gateway.SetupGet(x => x.Provider).Returns(BillingProvider.Square);
        gateway.SetupGet(x => x.Environment).Returns(BillingProviderEnvironment.Sandbox);
        return gateway;
    }

    private static BillingEntitlementService BuildEntitlementService(MasterAppDbContext db)
    {
        return new BillingEntitlementService(db);
    }

    private static async Task<ClientProfile> AddClientProfileAsync(MasterAppDbContext db, string email = "client@example.com")
    {
        var profile = new ClientProfile
        {
            ClientUserId = Guid.NewGuid().ToString("N"),
            FirstName = "Client",
            LastName = "One",
            Email = email,
            NormalizedEmail = email.ToLowerInvariant(),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        db.ClientProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }

    private static async Task<ClientSubscriptionOffer> AddOfferAsync(
        MasterAppDbContext db,
        Guid clientProfileId,
        string ownerAgentUserId = "agent-1",
        ClientSubscriptionOfferPriceType priceType = ClientSubscriptionOfferPriceType.Fixed100,
        int monthlyAmountCents = ClientSubscriptionOfferPricing.Fixed100Cents,
        BillingAnchorSelectionMode billingAnchorSelectionMode = BillingAnchorSelectionMode.FirstOfMonth,
        int? selectedBillingAnchorDay = null,
        ClientSubscriptionOfferStatus status = ClientSubscriptionOfferStatus.Offered)
    {
        var offer = new ClientSubscriptionOffer
        {
            ClientProfileId = clientProfileId,
            OwnerAgentUserId = ownerAgentUserId,
            PriceType = priceType,
            MonthlyAmountCents = monthlyAmountCents,
            Currency = "USD",
            BillingAnchorSelectionMode = billingAnchorSelectionMode,
            SelectedBillingAnchorDay = selectedBillingAnchorDay,
            Status = status,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        db.ClientSubscriptionOffers.Add(offer);
        await db.SaveChangesAsync();
        return offer;
    }

    private static async Task<ClientSubscription> AddSubscriptionAsync(
        MasterAppDbContext db,
        Guid clientProfileId,
        Guid acceptedOfferId,
        ClientSubscriptionStatus status,
        ClientSubscriptionPaymentStanding paymentStanding,
        string providerSubscriptionId)
    {
        var subscription = new ClientSubscription
        {
            ClientProfileId = clientProfileId,
            AcceptedOfferId = acceptedOfferId,
            OwnerAgentUserId = "agent-1",
            Provider = BillingProvider.Square,
            ProviderEnvironment = BillingProviderEnvironment.Sandbox,
            ProviderCustomerId = "cust_123",
            ProviderPaymentMethodId = "card_123",
            ProviderSubscriptionId = providerSubscriptionId,
            ProviderPlanVariationId = "plan_123",
            MonthlyAmountCents = ClientSubscriptionOfferPricing.Fixed100Cents,
            Currency = "USD",
            BillingTimeZoneId = "America/Phoenix",
            BillingAnchorDay = 15,
            Status = status,
            PaymentStanding = paymentStanding,
            FirstChargeUtc = DateTime.UtcNow.AddDays(-30),
            FirstRecurringRenewalUtc = DateTime.UtcNow.AddDays(-1),
            CurrentPeriodStartUtc = DateTime.UtcNow.AddDays(-1),
            CurrentPeriodEndUtc = DateTime.UtcNow.AddDays(29),
            NextBillingDateUtc = DateTime.UtcNow.AddDays(29),
            NextChargeAttemptUtc = DateTime.UtcNow.AddDays(29),
            IsPlatformManaged = true,
            PlatformManagedSinceUtc = DateTime.UtcNow.AddDays(-30),
            ActivatedUtc = DateTime.UtcNow.AddDays(-30),
            CreatedUtc = DateTime.UtcNow.AddDays(-30),
            UpdatedUtc = DateTime.UtcNow
        };

        db.ClientSubscriptions.Add(subscription);
        await db.SaveChangesAsync();
        return subscription;
    }

    private static string ComputeSquareSignature(string secret, string notificationUrl, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{notificationUrl}{payload}"));
        return Convert.ToBase64String(hash);
    }

    private static async Task<BillingProviderEvent> RecordProviderEventAsync(MasterAppDbContext db, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var processor = new BillingProviderEventProcessor(db);
        var result = await processor.ProcessAsync(new BillingProviderEventEnvelope(
            BillingProvider.Square,
            BillingProviderEnvironment.Sandbox,
            root.GetProperty("event_id").GetString() ?? throw new InvalidOperationException("Missing event_id."),
            root.GetProperty("type").GetString() ?? throw new InvalidOperationException("Missing type."),
            payload,
            DateTime.UtcNow));

        Assert.True(result.Success);
        Assert.NotNull(result.EventRecord);
        return result.EventRecord!;
    }

    private static string BuildSubscriptionPayload(
        string eventId,
        string eventType,
        string subscriptionId,
        string customerId,
        string status,
        string updatedAtUtc)
    {
        return $$"""
        {
          "event_id": "{{eventId}}",
          "type": "{{eventType}}",
          "created_at": "{{updatedAtUtc}}",
          "data": {
            "type": "subscription",
            "id": "{{subscriptionId}}",
            "object": {
              "subscription": {
                "id": "{{subscriptionId}}",
                "customer_id": "{{customerId}}",
                "status": "{{status}}",
                "updated_at": "{{updatedAtUtc}}"
              }
            }
          }
        }
        """;
    }

    private static string BuildPaymentPayload(
        string eventId,
        string eventType,
        string paymentId,
        string customerId,
        string invoiceId,
        string status,
        string updatedAtUtc)
    {
        return $$"""
        {
          "event_id": "{{eventId}}",
          "type": "{{eventType}}",
          "created_at": "{{updatedAtUtc}}",
          "data": {
            "type": "payment",
            "id": "{{paymentId}}",
            "object": {
              "payment": {
                "id": "{{paymentId}}",
                "customer_id": "{{customerId}}",
                "invoice_id": "{{invoiceId}}",
                "status": "{{status}}",
                "updated_at": "{{updatedAtUtc}}"
              }
            }
          }
        }
        """;
    }

    private static string BuildInvoicePayload(
        string eventId,
        string eventType,
        string invoiceId,
        string subscriptionId,
        string customerId,
        string paymentId,
        string updatedAtUtc)
    {
        return $$"""
        {
          "event_id": "{{eventId}}",
          "type": "{{eventType}}",
          "created_at": "{{updatedAtUtc}}",
          "data": {
            "type": "invoice",
            "id": "{{invoiceId}}",
            "object": {
              "invoice": {
                "id": "{{invoiceId}}",
                "subscription_id": "{{subscriptionId}}",
                "customer_id": "{{customerId}}",
                "updated_at": "{{updatedAtUtc}}",
                "payment_requests": [
                  {
                    "payment_id": "{{paymentId}}"
                  }
                ]
              }
            }
          }
        }
        """;
    }

    private static string BuildInvoiceFailurePayload(
        string eventId,
        string eventType,
        string invoiceId,
        string subscriptionId,
        string customerId,
        string status,
        string updatedAtUtc)
    {
        return $$"""
        {
          "event_id": "{{eventId}}",
          "type": "{{eventType}}",
          "created_at": "{{updatedAtUtc}}",
          "data": {
            "type": "invoice",
            "id": "{{invoiceId}}",
            "object": {
              "invoice": {
                "id": "{{invoiceId}}",
                "subscription_id": "{{subscriptionId}}",
                "customer_id": "{{customerId}}",
                "status": "{{status}}",
                "updated_at": "{{updatedAtUtc}}"
              }
            }
          }
        }
        """;
    }

    private static string BuildRefundPayload(
        string eventId,
        string eventType,
        string refundId,
        string paymentId,
        string invoiceId,
        string updatedAtUtc)
    {
        return $$"""
        {
          "event_id": "{{eventId}}",
          "type": "{{eventType}}",
          "created_at": "{{updatedAtUtc}}",
          "data": {
            "type": "refund",
            "id": "{{refundId}}",
            "object": {
              "refund": {
                "id": "{{refundId}}",
                "payment_id": "{{paymentId}}",
                "invoice_id": "{{invoiceId}}",
                "status": "COMPLETED",
                "updated_at": "{{updatedAtUtc}}"
              }
            }
          }
        }
        """;
    }

    private static string BuildDisputePayload(
        string eventId,
        string eventType,
        string disputeId,
        string paymentId,
        string customerId,
        string state,
        string updatedAtUtc)
    {
        return $$"""
        {
          "event_id": "{{eventId}}",
          "type": "{{eventType}}",
          "created_at": "{{updatedAtUtc}}",
          "data": {
            "type": "dispute",
            "id": "{{disputeId}}",
            "object": {
              "dispute": {
                "id": "{{disputeId}}",
                "payment_id": "{{paymentId}}",
                "customer_id": "{{customerId}}",
                "state": "{{state}}",
                "updated_at": "{{updatedAtUtc}}"
              }
            }
          }
        }
        """;
    }

    private static string BuildUnsupportedPayload(string eventId, string eventType, string objectType, string objectId)
    {
        return $$"""
        {
          "event_id": "{{eventId}}",
          "type": "{{eventType}}",
          "data": {
            "type": "{{objectType}}",
            "id": "{{objectId}}"
          }
        }
        """;
    }
}
