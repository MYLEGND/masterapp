using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers.API;
using AgentPortal.Services;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Billing;
using Infrastructure.Billing.Square;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class BillingCentralizationTests
{
    [Theory]
    [InlineData(ClientSubscriptionStatus.Canceled, ClientSubscriptionPaymentStanding.Failed, "Cancelled")]
    [InlineData(ClientSubscriptionStatus.GracePeriod, ClientSubscriptionPaymentStanding.GracePeriod, "Grace Period")]
    [InlineData(ClientSubscriptionStatus.Active, ClientSubscriptionPaymentStanding.Current, "Active")]
    [InlineData(ClientSubscriptionStatus.Active, ClientSubscriptionPaymentStanding.Failed, "Payment Failed")]
    public void ClientSubscriptionDisplay_PreservesTheAuthoritativeLifecycleState(
        ClientSubscriptionStatus status,
        ClientSubscriptionPaymentStanding paymentStanding,
        string expected)
    {
        Assert.Equal(expected, ClientSubscriptionDisplay.FormatMembershipState(status, paymentStanding));
    }

    [Fact]
    public void ClientPaymentMethodModel_MapsOnlySafePaymentMethodMetadata()
    {
        using var db = ControllerTestHelpers.BuildDb();

        var entity = db.Model.FindEntityType(typeof(ClientPaymentMethod));

        Assert.NotNull(entity);
        Assert.Equal(40, entity!.FindProperty(nameof(ClientPaymentMethod.CardBrand))?.GetMaxLength());
        Assert.Equal(4, entity.FindProperty(nameof(ClientPaymentMethod.Last4))?.GetMaxLength());
        Assert.Equal(200, entity.FindProperty(nameof(ClientPaymentMethod.CardholderName))?.GetMaxLength());
        Assert.NotNull(entity.FindProperty(nameof(ClientPaymentMethod.ExpirationMonth)));
        Assert.NotNull(entity.FindProperty(nameof(ClientPaymentMethod.ExpirationYear)));
        Assert.NotNull(entity.FindProperty(nameof(ClientPaymentMethod.UpdatedUtc)));

        var persistedPropertyNames = entity
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("CardNumber", persistedPropertyNames);
        Assert.DoesNotContain("FullCardNumber", persistedPropertyNames);
        Assert.DoesNotContain("Cvv", persistedPropertyNames);
        Assert.DoesNotContain("CVC", persistedPropertyNames);
        Assert.DoesNotContain("RawSourceToken", persistedPropertyNames);

        var subscriptionEntity = db.Model.FindEntityType(typeof(ClientSubscription));
        Assert.NotNull(subscriptionEntity);
        Assert.NotNull(subscriptionEntity!.FindProperty(nameof(ClientSubscription.DefaultPaymentMethodId)));
        Assert.Null(subscriptionEntity.FindProperty("ProviderPaymentMethodId"));
    }

    [Fact]
    public async Task ClientPaymentMethodService_AddsBackupAndChangesDefaultWithoutPersistingTheRawSource()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            ClientSubscriptionStatus.Active,
            ClientSubscriptionPaymentStanding.Current,
            "subscription_payment_methods");
        var originalDefaultId = subscription.DefaultPaymentMethodId;
        var gateway = BuildGateway();
        gateway.Setup(x => x.AttachPaymentMethodAsync(It.IsAny<BillingPaymentMethodAttachmentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingPaymentMethodAttachmentResult(
                true,
                "card_456",
                "ATTACHED",
                null,
                "Payment method attached.",
                "request_456",
                false,
                "cust_123",
                "card_456",
                "MASTERCARD",
                "5454",
                11,
                2030,
                "Client One"));

        var service = new ClientPaymentMethodService(db, gateway.Object);
        var added = await service.AddAsync(new AddClientPaymentMethodCommand(
            profile.Id,
            subscription.Id,
            "cnon:secure-browser-token-not-for-storage",
            "Client One",
            new BillingPostalAddress("1 Main Street", null, "Phoenix", "AZ", "85001", "US"),
            false,
            "Travel card",
            BillingActorType.Client,
            profile.ClientUserId));

        var addedMethod = Assert.IsType<ClientPaymentMethod>(added.PaymentMethod);
        var persisted = await db.ClientPaymentMethods.SingleAsync(item => item.Id == addedMethod.Id);
        var refreshedSubscription = await db.ClientSubscriptions.SingleAsync(item => item.Id == subscription.Id);

        Assert.True(added.Success);
        Assert.False(added.DefaultChanged);
        Assert.Equal(originalDefaultId, refreshedSubscription.DefaultPaymentMethodId);
        Assert.Equal("MASTERCARD", persisted.CardBrand);
        Assert.Equal("5454", persisted.Last4);
        Assert.Equal("Travel card", persisted.DisplayName);
        Assert.DoesNotContain(
            db.Entry(persisted).Properties.Select(property => property.CurrentValue?.ToString()),
            value => string.Equals(value, "cnon:secure-browser-token-not-for-storage", StringComparison.Ordinal));

        var defaultChanged = await service.SetDefaultAsync(new SetDefaultClientPaymentMethodCommand(
            profile.Id,
            subscription.Id,
            persisted.Id,
            BillingActorType.Client,
            profile.ClientUserId));

        refreshedSubscription = await db.ClientSubscriptions.SingleAsync(item => item.Id == subscription.Id);
        Assert.True(defaultChanged.Success);
        Assert.True(defaultChanged.DefaultChanged);
        Assert.Equal(persisted.Id, refreshedSubscription.DefaultPaymentMethodId);
        Assert.Contains(await db.BillingAuditEntries.ToListAsync(), entry => entry.Action == "payment_method_default_changed");
    }

    [Fact]
    public async Task ClientPaymentMethodService_RequiresReplacementBeforeRemovingDefaultAndRetiresProviderCardAfterward()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            ClientSubscriptionStatus.Active,
            ClientSubscriptionPaymentStanding.Current,
            "subscription_remove_payment_method");
        var defaultMethod = await db.ClientPaymentMethods.SingleAsync(item => item.Id == subscription.DefaultPaymentMethodId);
        var gateway = BuildGateway();
        var service = new ClientPaymentMethodService(db, gateway.Object);

        var blocked = await service.RemoveAsync(new RemoveClientPaymentMethodCommand(
            profile.Id,
            subscription.Id,
            defaultMethod.Id,
            BillingActorType.Client,
            profile.ClientUserId));

        Assert.False(blocked.Success);
        Assert.Equal("DEFAULT_PAYMENT_METHOD_REQUIRED", blocked.SafeErrorCode);
        gateway.Verify(x => x.DisablePaymentMethodAsync(It.IsAny<BillingPaymentMethodDisableRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        var backup = new ClientPaymentMethod
        {
            ClientProfileId = profile.Id,
            Provider = BillingProvider.Square,
            ProviderEnvironment = BillingProviderEnvironment.Sandbox,
            ProviderPaymentMethodId = "card_backup",
            CardBrand = "VISA",
            Last4 = "1111",
            CreatedUtc = DateTime.UtcNow.AddMinutes(1),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(1)
        };
        db.ClientPaymentMethods.Add(backup);
        await db.SaveChangesAsync();
        gateway.Setup(x => x.DisablePaymentMethodAsync(
                It.Is<BillingPaymentMethodDisableRequest>(request => request.ProviderPaymentMethodId == defaultMethod.ProviderPaymentMethodId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingPaymentMethodDisableResult(true, defaultMethod.ProviderPaymentMethodId, "DISABLED", null, "Payment method disabled.", "request_disabled", false));

        var removed = await service.RemoveAsync(new RemoveClientPaymentMethodCommand(
            profile.Id,
            subscription.Id,
            defaultMethod.Id,
            BillingActorType.Client,
            profile.ClientUserId));

        var refreshedDefault = await db.ClientSubscriptions.SingleAsync(item => item.Id == subscription.Id);
        var retired = await db.ClientPaymentMethods.SingleAsync(item => item.Id == defaultMethod.Id);
        var listed = await service.ListAsync(profile.Id);

        Assert.True(removed.Success);
        Assert.True(removed.DefaultChanged);
        Assert.Equal(backup.Id, refreshedDefault.DefaultPaymentMethodId);
        Assert.NotNull(retired.RetiredUtc);
        Assert.DoesNotContain(listed, item => item.Id == defaultMethod.Id);
        Assert.Contains(listed, item => item.Id == backup.Id);
        gateway.Verify(x => x.DisablePaymentMethodAsync(
            It.Is<BillingPaymentMethodDisableRequest>(request => request.ProviderPaymentMethodId == defaultMethod.ProviderPaymentMethodId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

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
    public void FreeTrialSchedule_DelaysTheFirstPremiumChargeByTheConfiguredDays()
    {
        var nowUtc = DateTime.SpecifyKind(new DateTime(2026, 8, 8, 15, 30, 0), DateTimeKind.Utc);
        var offer = new ClientSubscriptionOffer
        {
            MonthlyAmountCents = ClientSubscriptionOfferPricing.Fixed100Cents,
            Currency = "USD",
            BillingAnchorSelectionMode = BillingAnchorSelectionMode.FirstOfMonth,
            SelectedBillingAnchorDay = 1,
            FreeTrialDays = 21
        };
        var policy = new ClientSubscriptionActivationPolicyService(
            new ClientSubscriptionActivationPolicyOptions { BusinessTimeZoneId = "UTC" });

        var schedule = policy.ResolveActivationSchedule(offer, nowUtc);

        Assert.Equal(nowUtc.AddDays(21), schedule.FirstChargeUtc);
        Assert.Equal(21, schedule.FreeTrialDays);
        Assert.True(schedule.FirstRecurringRenewalUtc > schedule.FirstChargeUtc);
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
        Assert.Null(subscription.DefaultPaymentMethodId);
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
            .ReturnsAsync(new BillingPaymentMethodAttachmentResult(
                true,
                "card_123",
                "COMPLETED",
                null,
                "Card vaulted.",
                "req_card",
                false,
                "cust_123",
                "card_123",
                "VISA",
                "4242",
                12,
                2031,
                "Client One"));
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
        var paymentMethod = await db.ClientPaymentMethods.SingleAsync();
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
        Assert.Equal("cust_123", subscription.ProviderCustomerId);
        Assert.Equal(paymentMethod.Id, subscription.DefaultPaymentMethodId);
        Assert.Equal("card_123", paymentMethod.ProviderPaymentMethodId);
        Assert.Equal("VISA", paymentMethod.CardBrand);
        Assert.Equal("4242", paymentMethod.Last4);
        Assert.Equal(12, paymentMethod.ExpirationMonth);
        Assert.Equal(2031, paymentMethod.ExpirationYear);
        Assert.Equal("Client One", paymentMethod.CardholderName);
        Assert.True(paymentMethod.UpdatedUtc >= paymentMethod.CreatedUtc);
        Assert.Equal(SubscriptionPaymentKind.InitialActivation, payment.Kind);
        Assert.Equal(paymentMethod.Id, payment.ClientPaymentMethodId);
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
    public void OfferEligibility_ExactEffectiveBoundaryIncludesAndExpiryBoundaryExcludes()
    {
        var evaluatedUtc = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var offer = new ClientSubscriptionOffer
        {
            Status = ClientSubscriptionOfferStatus.Offered,
            EffectiveUtc = evaluatedUtc,
            ExpiresUtc = evaluatedUtc.AddTicks(1)
        };

        Assert.False(offer.IsEligibleForActivation(evaluatedUtc.AddTicks(-1)));
        Assert.True(offer.IsEligibleForActivation(evaluatedUtc));
        Assert.False(offer.IsEligibleForActivation(evaluatedUtc.AddTicks(1)));
        offer.EffectiveUtc = null;
        offer.ExpiresUtc = null;
        Assert.True(offer.IsEligibleForActivation(evaluatedUtc));
    }

    [Theory]
    [InlineData(ClientSubscriptionOfferStatus.Draft, null, null)]
    [InlineData(ClientSubscriptionOfferStatus.Accepted, null, null)]
    [InlineData(ClientSubscriptionOfferStatus.Expired, null, null)]
    [InlineData(ClientSubscriptionOfferStatus.Revoked, null, null)]
    [InlineData(ClientSubscriptionOfferStatus.Offered, 60, null)]
    [InlineData(ClientSubscriptionOfferStatus.Offered, null, 0)]
    [InlineData(ClientSubscriptionOfferStatus.Offered, null, -60)]
    public async Task OfferEligibility_UnavailableOfferCannotCreateSubscriptionOrCallProvider(
        ClientSubscriptionOfferStatus status, int? effectiveMinutes, int? expiresMinutes)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var nowUtc = DateTime.UtcNow;
        offer.Status = status;
        offer.EffectiveUtc = effectiveMinutes.HasValue ? nowUtc.AddMinutes(effectiveMinutes.Value) : null;
        offer.ExpiresUtc = expiresMinutes.HasValue ? nowUtc.AddMinutes(expiresMinutes.Value) : null;
        await db.SaveChangesAsync();
        var gateway = BuildInitialActivationGateway();
        gateway.Setup(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingOneTimePaymentResult(true, "pay_disallowed_offer", "COMPLETED", null,
                "Synthetic payment completed.", "req_disallowed_offer", false));
        var orchestrator = new MasterAppBillingOrchestrator(db, gateway.Object, BuildEntitlementService(db),
            Mock.Of<IClientSubscriptionActivationPolicyService>());

        var result = await orchestrator.ActivateClientSubscriptionAsync(BuildInitialActivationCommand(profile, offer));

        Assert.False(result.Success);
        Assert.Equal("OFFER_NOT_AVAILABLE", result.SafeErrorCode);
        Assert.Equal(status, offer.Status);
        Assert.Empty(db.ClientSubscriptions);
        Assert.Empty(db.SubscriptionPayments);
        Assert.Empty(db.ClientPaymentMethods);
        Assert.Empty(db.ClientEntitlements);
        gateway.Verify(x => x.ResolveCustomerAsync(It.IsAny<BillingCustomerResolutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        gateway.Verify(x => x.AttachPaymentMethodAsync(It.IsAny<BillingPaymentMethodAttachmentRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        gateway.Verify(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OfferEligibility_SupersededInvitationCannotChargeOldPriceAndCurrentOfferStillActivates()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var oldOffer = await AddOfferAsync(db, profile.Id);
        var gateway = BuildInitialActivationGateway();
        gateway.Setup(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingOneTimePaymentResult(true, "pay_current_offer", "COMPLETED", null,
                "Synthetic payment completed.", "req_current_offer", false));
        var orchestrator = new MasterAppBillingOrchestrator(db, gateway.Object, BuildEntitlementService(db),
            Mock.Of<IClientSubscriptionActivationPolicyService>());
        var invitationResult = await orchestrator.CreateSubscriptionActivationInvitationAsync(
            new CreateSubscriptionActivationInvitationCommand(profile.Id, oldOffer.Id, profile.Email,
                "agent-1", DateTime.UtcNow.AddDays(7)));
        var currentOffer = await orchestrator.CreateClientSubscriptionOfferAsync(
            new CreateClientSubscriptionOfferCommand(profile.Id, "agent-1", ClientSubscriptionOfferPriceType.Fixed150,
                null, "USD", BillingAnchorSelectionMode.FifteenthOfMonth, null, DateTime.UtcNow, null));
        var invitation = invitationResult.Invitation!;
        Assert.Equal(ClientSubscriptionOfferStatus.Superseded, oldOffer.Status);
        Assert.Equal(SubscriptionActivationInvitationStatus.Pending, invitation.Status);
        var staleCommand = BuildInitialActivationCommand(profile, oldOffer) with { InvitationId = invitation.Id };

        var stale = await orchestrator.ActivateClientSubscriptionAsync(staleCommand);

        Assert.False(stale.Success);
        Assert.Equal("OFFER_NOT_AVAILABLE", stale.SafeErrorCode);
        Assert.Empty(db.ClientSubscriptions);
        Assert.Empty(db.SubscriptionPayments);
        Assert.Empty(db.ClientEntitlements);
        Assert.Equal(SubscriptionActivationInvitationStatus.Pending, invitation.Status);
        gateway.Verify(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        var current = await orchestrator.ActivateClientSubscriptionAsync(BuildInitialActivationCommand(profile, currentOffer));
        Assert.True(current.Success);
        Assert.Equal(currentOffer.Id, Assert.Single(await db.ClientSubscriptions.ToListAsync()).AcceptedOfferId);
        Assert.Equal(ClientSubscriptionOfferPricing.Fixed150Cents, Assert.Single(await db.SubscriptionPayments.ToListAsync()).AmountCents);
        Assert.Equal(ClientSubscriptionOfferStatus.Superseded, oldOffer.Status);
        gateway.Verify(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("PENDING", true, false, SubscriptionPaymentStatus.Pending, false)]
    [InlineData("PROCESSING", true, false, SubscriptionPaymentStatus.Pending, false)]
    [InlineData("AUTHORIZED", true, false, SubscriptionPaymentStatus.Authorized, false)]
    [InlineData("APPROVED", true, false, SubscriptionPaymentStatus.Authorized, false)]
    [InlineData("HTTP_500", false, true, SubscriptionPaymentStatus.Pending, false)]
    [InlineData("HTTP_429", false, true, SubscriptionPaymentStatus.Pending, false)]
    [InlineData("UNKNOWN", false, false, SubscriptionPaymentStatus.Pending, false)]
    [InlineData(null, false, false, SubscriptionPaymentStatus.Pending, false)]
    [InlineData("FAILED", false, true, SubscriptionPaymentStatus.Processing, false)]
    [InlineData("COMPLETED", false, false, SubscriptionPaymentStatus.Processing, false)]
    [InlineData("COMPLETED", true, false, SubscriptionPaymentStatus.Processing, true)]
    public async Task InitialActivation_UncertainResultRetainsOneOwnedAttemptAcrossRetryAndCompletion(
        string? providerStatus, bool success, bool retryable, SubscriptionPaymentStatus expectedPaymentStatus, bool omitProviderPaymentId)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var gateway = BuildInitialActivationGateway();
        var ownedBeforeCharge = false;
        var providerPaymentId = !omitProviderPaymentId && (success || providerStatus == "COMPLETED") ? "pay_initial_uncertain" : null;
        gateway.Setup(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
            .Returns((BillingOneTimePaymentRequest request, CancellationToken token) =>
            {
                var persisted = db.ClientSubscriptions.AsNoTracking()
                    .Single(x => x.Id == Guid.Parse(request.OrderReference!));
                ownedBeforeCharge = persisted.IsPlatformManaged &&
                    persisted.Status == ClientSubscriptionStatus.ReconciliationRequired &&
                    persisted.PaymentStanding == ClientSubscriptionPaymentStanding.RequiresAction;
                return Task.FromResult(new BillingOneTimePaymentResult(success, providerPaymentId,
                    providerStatus, success ? null : "TEST_INITIAL_OUTCOME", "Initial result pending confirmation.",
                    "req_initial_uncertain", retryable));
            });
        var orchestrator = new MasterAppBillingOrchestrator(db, gateway.Object, BuildEntitlementService(db),
            Mock.Of<IClientSubscriptionActivationPolicyService>());
        var command = BuildInitialActivationCommand(profile, offer);

        var first = await orchestrator.ActivateClientSubscriptionAsync(command);
        var firstAttempt = await db.SubscriptionPayments.SingleAsync();
        var attemptId = firstAttempt.Id;
        var idempotencyKey = firstAttempt.IdempotencyKey;
        var duplicate = await orchestrator.ActivateClientSubscriptionAsync(command);

        Assert.False(first.Success);
        Assert.False(duplicate.Success);
        Assert.Equal("PAYMENT_OUTCOME_RECONCILIATION_PENDING", duplicate.SafeErrorCode);
        var subscription = Assert.Single(await db.ClientSubscriptions.ToListAsync());
        var attempt = Assert.Single(await db.SubscriptionPayments.ToListAsync());
        Assert.Equal(first.Subscription!.Id, subscription.Id);
        Assert.Equal(attemptId, attempt.Id);
        Assert.Equal(idempotencyKey, attempt.IdempotencyKey);
        Assert.Equal(providerPaymentId, attempt.ProviderPaymentId);
        Assert.Equal(ClientSubscriptionStatus.ReconciliationRequired, subscription.Status);
        Assert.Equal(ClientSubscriptionPaymentStanding.RequiresAction, subscription.PaymentStanding);
        Assert.True(ownedBeforeCharge);
        Assert.NotNull(subscription.PlatformManagedSinceUtc);
        Assert.Contains(subscription.Id, await db.ClientSubscriptions
            .Where(x => x.IsPlatformManaged && x.Status == ClientSubscriptionStatus.ReconciliationRequired)
            .Select(x => x.Id).ToListAsync());
        Assert.Empty(db.ClientEntitlements);
        var beforeCompletion = await BuildEntitlementService(db).EvaluateAsync(new BillingEntitlementEvaluationRequest(
            profile.Id, BillingEntitlementKeys.ClientAppFullAccess, DateTime.UtcNow));
        Assert.Equal(ClientEntitlementStatus.NotGranted, beforeCompletion.Status);
        await new BillingReconciliationService(db, gateway.Object, BuildEntitlementService(db))
            .ReconcileSubscriptionAsync(subscription.Id, "before-provider-proof");
        Assert.Equal(ClientSubscriptionStatus.ReconciliationRequired, subscription.Status);
        Assert.Equal(expectedPaymentStatus, attempt.Status);
        Assert.Empty(db.ClientEntitlements);
        gateway.Verify(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        await CompleteInitialActivationFromProviderEventAsync(db, gateway, subscription, "pay_initial_uncertain");
        Assert.Equal(attemptId, Assert.Single(await db.SubscriptionPayments.ToListAsync()).Id);
        Assert.Equal(idempotencyKey, attempt.IdempotencyKey);
        Assert.Equal("pay_initial_uncertain", attempt.ProviderPaymentId);
        Assert.Equal(SubscriptionPaymentStatus.Completed, attempt.Status);
        Assert.Equal(ClientSubscriptionStatus.Active, subscription.Status);
        Assert.Equal(ClientEntitlementStatus.Active, Assert.Single(await db.ClientEntitlements.ToListAsync()).Status);
        Assert.Single(await db.ClientSubscriptions.ToListAsync());
        Assert.Equal(1, await db.BillingAuditEntries.CountAsync(x => x.Action == "initial_payment_reconciled"));
        gateway.Verify(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InitialActivation_TimeoutRetainsWorkerEligibleAttemptAndCompletesWithoutAnotherCharge()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var gateway = BuildInitialActivationGateway();
        var ownedBeforeCharge = false;
        gateway.Setup(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
            .Callback((BillingOneTimePaymentRequest request, CancellationToken token) =>
            {
                var persisted = db.ClientSubscriptions.AsNoTracking()
                    .Single(x => x.Id == Guid.Parse(request.OrderReference!));
                ownedBeforeCharge = persisted.IsPlatformManaged &&
                    persisted.Status == ClientSubscriptionStatus.ReconciliationRequired &&
                    persisted.PaymentStanding == ClientSubscriptionPaymentStanding.RequiresAction;
            })
            .ThrowsAsync(new TimeoutException("Synthetic provider timeout."));
        var orchestrator = new MasterAppBillingOrchestrator(db, gateway.Object, BuildEntitlementService(db),
            Mock.Of<IClientSubscriptionActivationPolicyService>());
        var command = BuildInitialActivationCommand(profile, offer);

        var first = await orchestrator.ActivateClientSubscriptionAsync(command);
        var duplicate = await orchestrator.ActivateClientSubscriptionAsync(command);
        var subscription = Assert.Single(await db.ClientSubscriptions.ToListAsync());
        var attempt = Assert.Single(await db.SubscriptionPayments.ToListAsync());
        var attemptId = attempt.Id;
        Assert.False(first.Success);
        Assert.Equal("INITIAL_PAYMENT_OUTCOME_UNKNOWN", first.SafeErrorCode);
        Assert.Equal("PAYMENT_OUTCOME_RECONCILIATION_PENDING", duplicate.SafeErrorCode);
        Assert.Equal(SubscriptionPaymentStatus.Processing, attempt.Status);
        Assert.True(ownedBeforeCharge);
        Assert.True(subscription.IsPlatformManaged);
        Assert.Equal(ClientSubscriptionStatus.ReconciliationRequired, subscription.Status);
        Assert.Empty(db.ClientEntitlements);

        await CompleteInitialActivationFromProviderEventAsync(db, gateway, subscription, "pay_after_timeout");
        Assert.Equal(attemptId, Assert.Single(await db.SubscriptionPayments.ToListAsync()).Id);
        Assert.Equal(ClientSubscriptionStatus.Active, subscription.Status);
        Assert.Equal("pay_after_timeout", attempt.ProviderPaymentId);
        Assert.Equal(ClientEntitlementStatus.Active, Assert.Single(await db.ClientEntitlements.ToListAsync()).Status);
        gateway.Verify(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InitialActivation_CancelledRequestLeavesDurableWorkerStateAndBlocksAnotherCharge()
    {
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new MasterAppDbContext(options);
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var gateway = BuildInitialActivationGateway();
        using var cancellation = new CancellationTokenSource();
        gateway.Setup(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
            .Returns((BillingOneTimePaymentRequest request, CancellationToken token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<BillingOneTimePaymentResult>(token);
            });
        var command = BuildInitialActivationCommand(profile, offer);
        var orchestrator = new MasterAppBillingOrchestrator(db, gateway.Object, BuildEntitlementService(db),
            Mock.Of<IClientSubscriptionActivationPolicyService>());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.ActivateClientSubscriptionAsync(command, cancellation.Token));

        // A fresh context proves the state was saved before the provider call;
        // the cancelled response handler cannot persist its tracked changes.
        await using var restartedDb = new MasterAppDbContext(options);
        var subscription = Assert.Single(await restartedDb.ClientSubscriptions.ToListAsync());
        var attempt = Assert.Single(await restartedDb.SubscriptionPayments.ToListAsync());
        Assert.Equal(ClientSubscriptionStatus.ReconciliationRequired, subscription.Status);
        Assert.Equal(ClientSubscriptionPaymentStanding.RequiresAction, subscription.PaymentStanding);
        Assert.True(subscription.IsPlatformManaged);
        Assert.NotNull(subscription.PlatformManagedSinceUtc);
        Assert.Equal(SubscriptionPaymentStatus.Pending, attempt.Status);
        Assert.Empty(restartedDb.ClientEntitlements);
        var restart = new MasterAppBillingOrchestrator(restartedDb, gateway.Object, BuildEntitlementService(restartedDb),
            Mock.Of<IClientSubscriptionActivationPolicyService>());
        var retry = await restart.ActivateClientSubscriptionAsync(command);
        Assert.Equal("PAYMENT_OUTCOME_RECONCILIATION_PENDING", retry.SafeErrorCode);
        Assert.Single(await restartedDb.ClientSubscriptions.ToListAsync());
        Assert.Single(await restartedDb.SubscriptionPayments.ToListAsync());
        gateway.Verify(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("FAILED", SubscriptionPaymentStatus.Failed)]
    [InlineData("CANCELED", SubscriptionPaymentStatus.Canceled)]
    public async Task InitialActivation_DefinitiveFailureDeniesAccessAndPermitsSafeNewActivation(
        string providerStatus, SubscriptionPaymentStatus expectedPaymentStatus)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var gateway = BuildInitialActivationGateway();
        gateway.SetupSequence(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingOneTimePaymentResult(false, "pay_failed", providerStatus,
                "TEST_DEFINITIVE_FAILURE", "Payment definitively failed.", "req_failed", false))
            .ReturnsAsync(new BillingOneTimePaymentResult(true, "pay_safe_retry", "COMPLETED",
                null, "Payment completed.", "req_safe_retry", false));
        var orchestrator = new MasterAppBillingOrchestrator(db, gateway.Object, BuildEntitlementService(db),
            Mock.Of<IClientSubscriptionActivationPolicyService>());
        var command = BuildInitialActivationCommand(profile, offer);

        var failed = await orchestrator.ActivateClientSubscriptionAsync(command);
        Assert.False(failed.Success);
        Assert.Equal(ClientSubscriptionStatus.ActivationFailed, failed.Subscription!.Status);
        Assert.Equal(expectedPaymentStatus, Assert.Single(await db.SubscriptionPayments.ToListAsync()).Status);
        Assert.Empty(db.ClientEntitlements);
        var evaluation = await BuildEntitlementService(db).EvaluateAsync(new BillingEntitlementEvaluationRequest(
            profile.Id, BillingEntitlementKeys.ClientAppFullAccess, DateTime.UtcNow));
        Assert.Equal(ClientEntitlementStatus.NotGranted, evaluation.Status);

        var retry = await orchestrator.ActivateClientSubscriptionAsync(command);
        Assert.True(retry.Success);
        Assert.NotEqual(failed.Subscription.Id, retry.Subscription!.Id);
        Assert.Equal(ClientEntitlementStatus.Active, Assert.Single(await db.ClientEntitlements.ToListAsync()).Status);
        Assert.Equal(2, await db.SubscriptionPayments.CountAsync());
        gateway.Verify(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task InitialActivation_SquareCardDeclineRecordsDefinitiveFailureAndAllowsSafeRetry()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var paymentCalls = 0;
        using var handler = new BillingHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var (status, json) = request.RequestUri!.AbsolutePath switch
            {
                "/v2/customers/search" => (HttpStatusCode.OK, "{\"customers\":[{\"id\":\"cust_123\"}]}"),
                "/v2/cards" => (HttpStatusCode.OK, "{\"card\":{\"id\":\"card_123\",\"card_status\":\"ACTIVE\"}}"),
                "/v2/payments" when ++paymentCalls == 1 => (HttpStatusCode.BadRequest,
                    "{\"errors\":[{\"category\":\"PAYMENT_METHOD_ERROR\",\"code\":\"CARD_DECLINED\",\"detail\":\"Synthetic card decline.\"}]}"),
                "/v2/payments" => (HttpStatusCode.OK,
                    "{\"payment\":{\"id\":\"pay_after_decline\",\"status\":\"COMPLETED\"}}"),
                _ => throw new InvalidOperationException("Unexpected synthetic gateway request.")
            };
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("MasterAppBilling.Square"))
            .Returns(() => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://square.example/") });
        var gateway = new SquareBillingGateway(factory.Object,
            new SquareBillingOptions { AccessToken = "synthetic-test-token", LocationId = "synthetic-location" },
            NullLogger<SquareBillingGateway>.Instance);
        var orchestrator = new MasterAppBillingOrchestrator(db, gateway, BuildEntitlementService(db),
            Mock.Of<IClientSubscriptionActivationPolicyService>());
        var command = BuildInitialActivationCommand(profile, offer);

        var declined = await orchestrator.ActivateClientSubscriptionAsync(command);
        var rejectedAttempt = Assert.Single(await db.SubscriptionPayments.ToListAsync());
        Assert.False(declined.Success);
        Assert.False(declined.Retryable);
        Assert.Equal("CARD_DECLINED", declined.SafeErrorCode);
        Assert.Equal(ClientSubscriptionStatus.ActivationFailed, declined.Subscription!.Status);
        Assert.Equal(SubscriptionPaymentStatus.Failed, rejectedAttempt.Status);
        Assert.Null(rejectedAttempt.ProviderPaymentId);
        Assert.Empty(db.ClientEntitlements);

        var retry = await orchestrator.ActivateClientSubscriptionAsync(command);
        Assert.True(retry.Success);
        Assert.NotEqual(declined.Subscription.Id, retry.Subscription!.Id);
        Assert.Equal(2, paymentCalls);
        Assert.Equal(2, await db.SubscriptionPayments.CountAsync());
        Assert.Equal(SubscriptionPaymentStatus.Failed, rejectedAttempt.Status);
        Assert.Equal(ClientEntitlementStatus.Active, Assert.Single(await db.ClientEntitlements.ToListAsync()).Status);
    }

    [Fact]
    public async Task ActivateClientSubscription_FreeTrialVaultsTheCardWithoutSubmittingAnInitialCharge()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        offer.FreeTrialDays = 14;
        await db.SaveChangesAsync();

        var gateway = BuildGateway();
        gateway.Setup(x => x.ResolveCustomerAsync(It.IsAny<BillingCustomerResolutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingCustomerResolutionResult(true, "cust_trial", "COMPLETED", null, "Customer resolved.", "req_customer", false, "cust_trial"));
        gateway.Setup(x => x.AttachPaymentMethodAsync(It.IsAny<BillingPaymentMethodAttachmentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingPaymentMethodAttachmentResult(
                true,
                "card_trial",
                "COMPLETED",
                null,
                "Card vaulted.",
                "req_card",
                false,
                "cust_trial",
                "card_trial",
                "VISA",
                "4242",
                12,
                2031,
                "Client One"));

        var nowUtc = DateTime.UtcNow;
        var firstChargeUtc = nowUtc.AddDays(14);
        var firstRenewalUtc = firstChargeUtc.AddMonths(1);
        var orchestrator = new MasterAppBillingOrchestrator(
            db,
            gateway.Object,
            BuildEntitlementService(db),
            Mock.Of<IClientSubscriptionActivationPolicyService>());

        var result = await orchestrator.ActivateClientSubscriptionAsync(
            new ActivateClientSubscriptionCommand(
                profile.Id,
                offer.Id,
                "agent-1",
                "secure-browser-token",
                "USD",
                1,
                "UTC",
                firstChargeUtc,
                firstRenewalUtc,
                DateOnly.FromDateTime(firstRenewalUtc),
                true,
                true,
                true,
                profile.NormalizedEmail!,
                null,
                null,
                "Client One",
                "trial-correlation",
                "trial-idempotency",
                new BillingPostalAddress("1 Main Street", null, "Phoenix", "AZ", "85001", "US")));

        var subscription = await db.ClientSubscriptions.SingleAsync();
        Assert.True(result.Success);
        Assert.Equal(ClientSubscriptionStatus.Active, subscription.Status);
        Assert.Equal(firstChargeUtc, subscription.TrialEndsUtc);
        Assert.Equal(firstChargeUtc, subscription.NextBillingDateUtc);
        Assert.Equal(firstChargeUtc, subscription.NextChargeAttemptUtc);
        Assert.NotNull(subscription.DefaultPaymentMethodId);
        Assert.Empty(await db.SubscriptionPayments.ToListAsync());
        gateway.Verify(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()), Times.Never);
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
    public async Task PlatformManagedRenewal_SixMonthlyCyclesRecordOneCompletedPaymentAndAdvanceTheScheduleEachMonth()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            ClientSubscriptionStatus.Active,
            ClientSubscriptionPaymentStanding.Current,
            "platform-managed-six-month-simulation");
        var firstDueUtc = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddMonths(-5), DateTimeKind.Utc);
        subscription.BillingTimeZoneId = "UTC";
        subscription.BillingAnchorDay = null;
        subscription.CurrentPeriodStartUtc = firstDueUtc.AddMonths(-1);
        subscription.CurrentPeriodEndUtc = firstDueUtc;
        subscription.NextBillingDateUtc = firstDueUtc;
        subscription.NextChargeAttemptUtc = firstDueUtc;
        await db.SaveChangesAsync();

        var gateway = BuildGateway();
        var paymentResults = Enumerable.Range(1, 6)
            .Select(month => new BillingOneTimePaymentResult(
                true,
                $"payment_month_{month}",
                "COMPLETED",
                null,
                "Payment completed.",
                $"request_month_{month}",
                false))
            .ToList();
        gateway.SetupSequence(item => item.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(paymentResults[0])
            .ReturnsAsync(paymentResults[1])
            .ReturnsAsync(paymentResults[2])
            .ReturnsAsync(paymentResults[3])
            .ReturnsAsync(paymentResults[4])
            .ReturnsAsync(paymentResults[5]);
        var orchestrator = new MasterAppBillingOrchestrator(
            db,
            gateway.Object,
            BuildEntitlementService(db),
            new ClientSubscriptionActivationPolicyService(new ClientSubscriptionActivationPolicyOptions { BusinessTimeZoneId = "UTC" }));

        for (var month = 1; month <= 6; month++)
        {
            var result = await orchestrator.ProcessDueClientSubscriptionRenewalsAsync(10, $"worker-month-{month}");
            Assert.Equal(1, result.DueSubscriptions);
            Assert.Equal(1, result.ChargesAttempted);
            Assert.Equal(1, result.ChargesSucceeded);
            Assert.Equal(0, result.ChargesFailed);
        }

        var payments = await db.SubscriptionPayments
            .Where(payment => payment.ClientSubscriptionId == subscription.Id)
            .OrderBy(payment => payment.BillingPeriodStartUtc)
            .ToListAsync();
        var renewed = await db.ClientSubscriptions.SingleAsync(item => item.Id == subscription.Id);
        var entitlement = await db.ClientEntitlements.SingleAsync(item => item.ClientProfileId == profile.Id);

        Assert.Equal(6, payments.Count);
        Assert.All(payments, payment => Assert.Equal(SubscriptionPaymentStatus.Completed, payment.Status));
        Assert.Equal(paymentResults.Select(result => result.ExternalId), payments.Select(payment => payment.ProviderPaymentId));
        Assert.Equal(ClientSubscriptionStatus.Active, renewed.Status);
        Assert.Equal(ClientSubscriptionPaymentStanding.Current, renewed.PaymentStanding);
        Assert.True(renewed.NextBillingDateUtc > DateTime.UtcNow);
        Assert.Equal(ClientEntitlementStatus.Active, entitlement.Status);
        gateway.Verify(
            item => item.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(6));
    }

    [Fact]
    public async Task ConcurrentRenewalWorkers_CreateOneLogicalAttemptAndOneProviderCharge()
    {
        var connectionString = $"Data Source=file:billing-{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connectionString)
            .Options;

        Guid subscriptionId;
        await using (var seed = new MasterAppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            var profile = await AddClientProfileAsync(seed);
            var offer = await AddOfferAsync(seed, profile.Id);
            var subscription = await AddSubscriptionAsync(
                seed,
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
            await seed.SaveChangesAsync();
            subscriptionId = subscription.Id;
        }

        var gateway = BuildGateway();
        gateway.Setup(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(50);
                return new BillingOneTimePaymentResult(true, "pay_concurrent_123", "COMPLETED", null, "Payment completed.", "req_concurrent", false);
            });
        var policy = new ClientSubscriptionActivationPolicyService(new ClientSubscriptionActivationPolicyOptions { BusinessTimeZoneId = "UTC" });

        await using var workerOneDb = new MasterAppDbContext(options);
        await using var workerTwoDb = new MasterAppDbContext(options);
        var workerOne = new MasterAppBillingOrchestrator(workerOneDb, gateway.Object, BuildEntitlementService(workerOneDb), policy);
        var workerTwo = new MasterAppBillingOrchestrator(workerTwoDb, gateway.Object, BuildEntitlementService(workerTwoDb), policy);

        await Task.WhenAll(
            workerOne.ProcessDueClientSubscriptionRenewalsAsync(10, "worker-one"),
            workerTwo.ProcessDueClientSubscriptionRenewalsAsync(10, "worker-two"));

        await using var verificationDb = new MasterAppDbContext(options);
        var attempts = await verificationDb.SubscriptionPayments
            .Where(x => x.ClientSubscriptionId == subscriptionId)
            .ToListAsync();
        var renewed = await verificationDb.ClientSubscriptions.SingleAsync(x => x.Id == subscriptionId);
        var restartWorker = new MasterAppBillingOrchestrator(
            verificationDb,
            gateway.Object,
            BuildEntitlementService(verificationDb),
            policy);
        var restartRun = await restartWorker.ProcessDueClientSubscriptionRenewalsAsync(10, "worker-restart");

        Assert.Single(attempts);
        Assert.Equal(SubscriptionPaymentStatus.Completed, attempts[0].Status);
        Assert.Equal(ClientSubscriptionStatus.Active, renewed.Status);
        Assert.Equal(0, restartRun.ChargesAttempted);
        gateway.Verify(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()), Times.Once);
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
        Assert.Equal(1, failedRun.ChargesFailed);
        Assert.Equal(ClientSubscriptionStatus.GracePeriod, subscription.Status);
        Assert.True(failedAttempt.RetryNotBeforeUtc.HasValue);
        var deferredRun = await orchestrator.ProcessDueClientSubscriptionRenewalsAsync(10, "worker-b");
        subscription.NextChargeAttemptUtc = DateTime.UtcNow.AddMinutes(-1);
        failedAttempt.RetryNotBeforeUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        var recoveredRun = await orchestrator.ProcessDueClientSubscriptionRenewalsAsync(10, "worker-c");
        var attempts = await db.SubscriptionPayments.OrderBy(x => x.AttemptNumber).ToListAsync();
        var recovered = await db.ClientSubscriptions.SingleAsync(x => x.Id == subscription.Id);

        Assert.Equal(0, deferredRun.ChargesAttempted);
        Assert.Equal(1, recoveredRun.ChargesSucceeded);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(new[] { 1, 2 }, attempts.Select(x => x.AttemptNumber).ToArray());
        Assert.NotEqual(attempts[0].IdempotencyKey, attempts[1].IdempotencyKey);
        Assert.Equal(ClientSubscriptionStatus.Active, recovered.Status);
        gateway.Verify(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ClientRequestedRenewalRetry_UsesTheExistingRenewalAuthorityAndRestoresGracePeriodAccess()
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
            providerSubscriptionId: "platform-managed-manual-retry");
        var dueUtc = DateTime.UtcNow.AddMinutes(-1);
        subscription.BillingTimeZoneId = "UTC";
        subscription.BillingAnchorDay = null;
        subscription.CurrentPeriodEndUtc = dueUtc;
        subscription.NextBillingDateUtc = dueUtc;
        subscription.NextChargeAttemptUtc = dueUtc;
        await db.SaveChangesAsync();

        var gateway = BuildGateway();
        gateway.SetupSequence(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingOneTimePaymentResult(false, null, "FAILED", "CARD_DECLINED", "The saved card could not be charged.", "request_initial_failure", false))
            .ReturnsAsync(new BillingOneTimePaymentResult(true, "payment_recovered", "COMPLETED", null, "Payment completed.", "request_recovered", false));
        var notifications = new ClientBillingNotificationService(db);
        var orchestrator = new MasterAppBillingOrchestrator(
            db,
            gateway.Object,
            BuildEntitlementService(db),
            new ClientSubscriptionActivationPolicyService(new ClientSubscriptionActivationPolicyOptions
            {
                BusinessTimeZoneId = "UTC",
                GracePeriodDays = 30,
                GracePeriodReminderDaysBeforeEnd = 15,
                GracePeriodFinalReminderDaysBeforeEnd = 3
            }),
            notifications);

        var initialRun = await orchestrator.ProcessDueClientSubscriptionRenewalsAsync(10, "worker-initial");
        var replacement = new ClientPaymentMethod
        {
            ClientProfileId = profile.Id,
            Provider = BillingProvider.Square,
            ProviderEnvironment = BillingProviderEnvironment.Sandbox,
            ProviderPaymentMethodId = "card_replacement_456",
            CardBrand = "VISA",
            Last4 = "1111",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.ClientPaymentMethods.Add(replacement);
        subscription.DefaultPaymentMethodId = replacement.Id;
        await db.SaveChangesAsync();

        var retry = await orchestrator.RetryClientSubscriptionRenewalAsync(
            new ManualClientSubscriptionRenewalRetryCommand(
                subscription.Id,
                BillingActorType.Client,
                profile.ClientUserId,
                "client-retry-test"));
        var renewed = await db.ClientSubscriptions.SingleAsync(item => item.Id == subscription.Id);
        var attempts = await db.SubscriptionPayments
            .Where(payment => payment.ClientSubscriptionId == subscription.Id)
            .OrderBy(payment => payment.AttemptNumber)
            .ToListAsync();
        var entitlement = await db.ClientEntitlements.SingleAsync(item => item.ClientProfileId == profile.Id);
        var queuedKinds = await db.ClientBillingNotifications
            .Where(notification => notification.ClientSubscriptionId == subscription.Id)
            .Select(notification => notification.Kind)
            .ToListAsync();

        Assert.Equal(1, initialRun.ChargesFailed);
        Assert.True(retry.Success);
        Assert.Equal(1, retry.RunResult.ChargesAttempted);
        Assert.Equal(1, retry.RunResult.ChargesSucceeded);
        Assert.Equal(ClientSubscriptionStatus.Active, renewed.Status);
        Assert.Equal(ClientSubscriptionPaymentStanding.Current, renewed.PaymentStanding);
        Assert.Equal(ClientEntitlementStatus.Active, entitlement.Status);
        Assert.Equal(2, attempts.Count);
        Assert.Equal("card_123", gateway.Invocations
            .Where(invocation => invocation.Method.Name == nameof(IBillingGateway.CreateOneTimePaymentAsync))
            .Select(invocation => ((BillingOneTimePaymentRequest)invocation.Arguments[0]).SourceId)
            .First());
        Assert.Contains("card_replacement_456", gateway.Invocations
            .Where(invocation => invocation.Method.Name == nameof(IBillingGateway.CreateOneTimePaymentAsync))
            .Select(invocation => ((BillingOneTimePaymentRequest)invocation.Arguments[0]).SourceId));
        Assert.Contains(ClientBillingNotificationKind.PaymentFailed, queuedKinds);
        Assert.Contains(ClientBillingNotificationKind.GracePeriodStarted, queuedKinds);
        Assert.Contains(ClientBillingNotificationKind.MembershipReactivated, queuedKinds);
        Assert.Contains(ClientBillingNotificationKind.UpcomingRenewal, queuedKinds);
    }

    [Fact]
    public async Task ClientPaymentMethodService_RejectsAgentManagedPaymentMethodChanges()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            ClientSubscriptionStatus.Active,
            ClientSubscriptionPaymentStanding.Current,
            "subscription-agent-payment-method-denied");
        var defaultPaymentMethod = await db.ClientPaymentMethods.SingleAsync(item => item.Id == subscription.DefaultPaymentMethodId);
        var service = new ClientPaymentMethodService(db, BuildGateway().Object);

        var result = await service.RenameAsync(new RenameClientPaymentMethodCommand(
            profile.Id,
            defaultPaymentMethod.Id,
            "Agent-selected label",
            BillingActorType.Agent,
            "agent-1"));

        Assert.False(result.Success);
        Assert.Equal("PAYMENT_METHOD_CLIENT_SELF_SERVICE_REQUIRED", result.SafeErrorCode);
        Assert.Null((await db.ClientPaymentMethods.SingleAsync(item => item.Id == defaultPaymentMethod.Id)).DisplayName);
    }

    [Fact]
    public async Task PlatformManagedRenewal_UsesBackupPaymentMethodOnlyAfterTerminalPrimaryFailure()
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
            providerSubscriptionId: "platform-managed-backup-payment");
        var primaryMethodId = subscription.DefaultPaymentMethodId;
        var backupMethod = new ClientPaymentMethod
        {
            ClientProfileId = profile.Id,
            Provider = BillingProvider.Square,
            ProviderEnvironment = BillingProviderEnvironment.Sandbox,
            ProviderPaymentMethodId = "card_backup_456",
            CardBrand = "VISA",
            Last4 = "1111",
            CreatedUtc = DateTime.UtcNow.AddMinutes(1),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(1)
        };
        db.ClientPaymentMethods.Add(backupMethod);
        var dueUtc = DateTime.UtcNow.AddMinutes(-1);
        subscription.BillingTimeZoneId = "UTC";
        subscription.BillingAnchorDay = null;
        subscription.CurrentPeriodEndUtc = dueUtc;
        subscription.NextBillingDateUtc = dueUtc;
        subscription.NextChargeAttemptUtc = dueUtc;
        await db.SaveChangesAsync();

        var gateway = BuildGateway();
        gateway.SetupSequence(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingOneTimePaymentResult(false, null, "FAILED", "CARD_DECLINED", "Primary card declined.", "request_primary_failed", false))
            .ReturnsAsync(new BillingOneTimePaymentResult(true, "payment_backup_completed", "COMPLETED", null, "Backup payment completed.", "request_backup_completed", false));
        var orchestrator = new MasterAppBillingOrchestrator(
            db,
            gateway.Object,
            BuildEntitlementService(db),
            new ClientSubscriptionActivationPolicyService(new ClientSubscriptionActivationPolicyOptions { BusinessTimeZoneId = "UTC" }));

        var run = await orchestrator.ProcessDueClientSubscriptionRenewalsAsync(10, "worker-backup");
        var attempts = await db.SubscriptionPayments
            .Where(payment => payment.ClientSubscriptionId == subscription.Id)
            .OrderBy(payment => payment.AttemptNumber)
            .ToListAsync();
        var renewed = await db.ClientSubscriptions.SingleAsync(item => item.Id == subscription.Id);

        Assert.Equal(2, run.ChargesAttempted);
        Assert.Equal(1, run.ChargesSucceeded);
        Assert.Equal(1, run.ChargesFailed);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(SubscriptionPaymentStatus.Failed, attempts[0].Status);
        Assert.Equal(primaryMethodId, attempts[0].ClientPaymentMethodId);
        Assert.Equal(SubscriptionPaymentStatus.Completed, attempts[1].Status);
        Assert.Equal(backupMethod.Id, attempts[1].ClientPaymentMethodId);
        Assert.Equal(ClientSubscriptionStatus.Active, renewed.Status);
        Assert.Equal(primaryMethodId, renewed.DefaultPaymentMethodId);
        Assert.Contains(await db.BillingAuditEntries.ToListAsync(), entry => entry.Action == "renewal_backup_payment_completed");
        Assert.Contains(await db.BillingAuditEntries.ToListAsync(), entry => entry.Action == "renewal_recovered_with_backup_payment");
        gateway.Verify(
            x => x.CreateOneTimePaymentAsync(
                It.Is<BillingOneTimePaymentRequest>(request => request.SourceId == "card_123"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        gateway.Verify(
            x => x.CreateOneTimePaymentAsync(
                It.Is<BillingOneTimePaymentRequest>(request => request.SourceId == "card_backup_456"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlatformManagedRenewal_DoesNotTryBackupAfterAnUncertainPrimaryFailure()
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
            providerSubscriptionId: "platform-managed-uncertain-primary");
        db.ClientPaymentMethods.Add(new ClientPaymentMethod
        {
            ClientProfileId = profile.Id,
            Provider = BillingProvider.Square,
            ProviderEnvironment = BillingProviderEnvironment.Sandbox,
            ProviderPaymentMethodId = "card_backup_456",
            CardBrand = "VISA",
            Last4 = "1111"
        });
        var dueUtc = DateTime.UtcNow.AddMinutes(-1);
        subscription.BillingTimeZoneId = "UTC";
        subscription.BillingAnchorDay = null;
        subscription.CurrentPeriodEndUtc = dueUtc;
        subscription.NextBillingDateUtc = dueUtc;
        subscription.NextChargeAttemptUtc = dueUtc;
        await db.SaveChangesAsync();

        var gateway = BuildGateway();
        gateway.Setup(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingOneTimePaymentResult(false, null, "FAILED", "PROCESSOR_TIMEOUT", "The provider response is uncertain.", "request_uncertain", true));
        var orchestrator = new MasterAppBillingOrchestrator(
            db,
            gateway.Object,
            BuildEntitlementService(db),
            new ClientSubscriptionActivationPolicyService(new ClientSubscriptionActivationPolicyOptions
            {
                BusinessTimeZoneId = "UTC",
                RenewalRetryDelayMinutes = [60]
            }));

        var run = await orchestrator.ProcessDueClientSubscriptionRenewalsAsync(10, "worker-uncertain");
        var attempt = await db.SubscriptionPayments.SingleAsync(payment => payment.ClientSubscriptionId == subscription.Id);

        Assert.Equal(1, run.ChargesAttempted);
        Assert.Equal(1, run.ChargesFailed);
        Assert.Equal(SubscriptionPaymentStatus.Failed, attempt.Status);
        Assert.True(attempt.Retryable);
        gateway.Verify(x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PlatformManagedRenewal_GracePeriodExpiryCancelsMembershipAndRevokesEntitlement()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            status: ClientSubscriptionStatus.GracePeriod,
            paymentStanding: ClientSubscriptionPaymentStanding.GracePeriod,
            providerSubscriptionId: "historical-reference-only");
        var graceEndedUtc = DateTime.UtcNow.AddMinutes(-1);
        subscription.BillingTimeZoneId = "UTC";
        subscription.CurrentPeriodEndUtc = DateTime.UtcNow.AddDays(-30);
        subscription.NextBillingDateUtc = DateTime.UtcNow.AddDays(-30);
        subscription.NextChargeAttemptUtc = null;
        subscription.GracePeriodEndsUtc = graceEndedUtc;
        await db.SaveChangesAsync();

        var gateway = BuildGateway();
        var orchestrator = new MasterAppBillingOrchestrator(
            db,
            gateway.Object,
            BuildEntitlementService(db),
            new ClientSubscriptionActivationPolicyService(new ClientSubscriptionActivationPolicyOptions
            {
                BusinessTimeZoneId = "UTC",
                GracePeriodDays = 30
            }));

        var result = await orchestrator.ProcessDueClientSubscriptionRenewalsAsync(10, "worker-a");
        var cancelled = await db.ClientSubscriptions.SingleAsync(x => x.Id == subscription.Id);
        var entitlement = await db.ClientEntitlements.SingleAsync(x => x.ClientProfileId == profile.Id);

        Assert.Equal(1, result.EndedAtPeriodBoundary);
        Assert.Equal(ClientSubscriptionStatus.Canceled, cancelled.Status);
        Assert.Equal(ClientSubscriptionPaymentStanding.Failed, cancelled.PaymentStanding);
        Assert.Equal(graceEndedUtc, cancelled.CancelledUtc);
        Assert.Equal(graceEndedUtc, cancelled.EndedUtc);
        Assert.Null(cancelled.GracePeriodEndsUtc);
        Assert.Null(cancelled.NextBillingDateUtc);
        Assert.Null(cancelled.NextChargeAttemptUtc);
        Assert.Equal(ClientEntitlementStatus.NotGranted, entitlement.Status);
        Assert.Equal("GRACE_PERIOD_EXPIRED_CANCELLED", entitlement.ReasonCode);
        gateway.Verify(
            x => x.CreateOneTimePaymentAsync(It.IsAny<BillingOneTimePaymentRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
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
    public async Task Reconciliation_CompletesAnUncertainInitialPaymentFromItsDurableAttempt()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            status: ClientSubscriptionStatus.ReconciliationRequired,
            paymentStanding: ClientSubscriptionPaymentStanding.RequiresAction,
            providerSubscriptionId: "historical-reference-only");
        subscription.IsPlatformManaged = false;
        subscription.ActivatedUtc = null;
        var attempt = new SubscriptionPayment
        {
            ClientSubscriptionId = subscription.Id,
            Provider = BillingProvider.Square,
            ProviderEnvironment = BillingProviderEnvironment.Sandbox,
            ProviderPaymentId = "pay_uncertain_123",
            AmountCents = subscription.MonthlyAmountCents,
            Currency = subscription.Currency,
            Kind = SubscriptionPaymentKind.InitialActivation,
            AttemptNumber = 1,
            IdempotencyKey = BillingIdempotency.CreateDeterministic("test-initial-reconciliation", subscription.Id.ToString()),
            Status = SubscriptionPaymentStatus.Processing,
            BillingPeriodStartUtc = subscription.FirstChargeUtc,
            BillingPeriodEndUtc = subscription.FirstRecurringRenewalUtc,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.SubscriptionPayments.Add(attempt);
        await db.SaveChangesAsync();

        await RecordProviderEventAsync(
            db,
            BuildPaymentPayload(
                "evt_uncertain_123",
                "payment.updated",
                "pay_uncertain_123",
                "cust_123",
                "inv_uncertain_123",
                "COMPLETED",
                "2026-07-24T00:00:00Z"));
        var gateway = BuildGateway();
        gateway.Setup(x => x.GetPaymentAsync(
                It.Is<BillingPaymentLookupRequest>(request => request.ProviderPaymentId == "pay_uncertain_123"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingPaymentResult(
                true,
                "pay_uncertain_123",
                "COMPLETED",
                null,
                "Payment completed.",
                "req_uncertain",
                false,
                ProviderInvoiceId: "inv_uncertain_123",
                AmountCents: subscription.MonthlyAmountCents,
                Currency: subscription.Currency,
                ProviderOccurredUtc: DateTime.Parse("2026-07-24T00:00:00Z")));
        var reconciliation = new BillingReconciliationService(db, gateway.Object, BuildEntitlementService(db));

        await reconciliation.ReconcilePendingProviderEventsAsync(10);
        await reconciliation.ReconcileSubscriptionAsync(subscription.Id, "test-reconciliation");

        var reconciled = await db.ClientSubscriptions.SingleAsync(x => x.Id == subscription.Id);
        var reconciledAttempt = await db.SubscriptionPayments.SingleAsync(x => x.Id == attempt.Id);
        var entitlement = await db.ClientEntitlements.SingleAsync(x => x.ClientProfileId == profile.Id);

        Assert.Equal(SubscriptionPaymentStatus.Completed, reconciledAttempt.Status);
        Assert.True(reconciled.IsPlatformManaged);
        Assert.Equal(ClientSubscriptionStatus.Active, reconciled.Status);
        Assert.Equal(ClientEntitlementStatus.Active, entitlement.Status);
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

    [Theory]
    [InlineData(ClientSubscriptionStatus.Active, null, ClientEntitlementStatus.Active, null)]
    [InlineData(ClientSubscriptionStatus.GracePeriod, 60, ClientEntitlementStatus.GracePeriod, "SUBSCRIPTION_GRACE_PERIOD")]
    [InlineData(ClientSubscriptionStatus.PastDue, 60, ClientEntitlementStatus.GracePeriod, "SUBSCRIPTION_GRACE_PERIOD")]
    [InlineData(ClientSubscriptionStatus.GracePeriod, null, ClientEntitlementStatus.NotGranted, "SUBSCRIPTION_GRACEPERIOD")]
    [InlineData(ClientSubscriptionStatus.GracePeriod, -60, ClientEntitlementStatus.NotGranted, "SUBSCRIPTION_GRACEPERIOD")]
    [InlineData(ClientSubscriptionStatus.GracePeriod, 0, ClientEntitlementStatus.NotGranted, "SUBSCRIPTION_GRACEPERIOD")]
    [InlineData(ClientSubscriptionStatus.PastDue, 0, ClientEntitlementStatus.Restricted, "SUBSCRIPTION_PAST_DUE")]
    [InlineData(ClientSubscriptionStatus.Canceled, 60, ClientEntitlementStatus.NotGranted, "SUBSCRIPTION_CANCELED")]
    [InlineData(ClientSubscriptionStatus.Suspended, 60, ClientEntitlementStatus.Suspended, "SUBSCRIPTION_SUSPENDED")]
    [InlineData(ClientSubscriptionStatus.Paused, 60, ClientEntitlementStatus.Suspended, "SUBSCRIPTION_SUSPENDED")]
    [InlineData(ClientSubscriptionStatus.ActivationFailed, 60, ClientEntitlementStatus.NotGranted, "SUBSCRIPTION_ACTIVATIONFAILED")]
    [InlineData(ClientSubscriptionStatus.ReconciliationRequired, 60, ClientEntitlementStatus.NotGranted, "SUBSCRIPTION_RECONCILIATIONREQUIRED")]
    public async Task EntitlementEvaluation_RequiresEligibleStateAndUnexpiredGraceWithoutWorkerRun(
        ClientSubscriptionStatus status,
        int? graceMinutes,
        ClientEntitlementStatus expectedStatus,
        string? expectedReason)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(db, profile.Id, offer.Id, status,
            ClientSubscriptionPaymentStanding.Unknown, "subscription_entitlement_boundary");
        var evaluatedUtc = DateTime.UtcNow;
        subscription.GracePeriodEndsUtc = graceMinutes.HasValue ? evaluatedUtc.AddMinutes(graceMinutes.Value) : null;
        await db.SaveChangesAsync();

        var result = await BuildEntitlementService(db).EvaluateAsync(new BillingEntitlementEvaluationRequest(
            profile.Id, BillingEntitlementKeys.ClientAppFullAccess, evaluatedUtc));
        var access = await ControllerTestHelpers.BuildHouseholdMembershipService(db).ResolveActiveAccessAsync(profile.Id);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Equal(subscription.Id.ToString(), result.SourceId);
        Assert.Equal(expectedStatus is ClientEntitlementStatus.Active or ClientEntitlementStatus.GracePeriod,
            access.HasActiveMembership);
        Assert.Equal(status, subscription.Status);
        Assert.Empty(db.ClientEntitlements);
    }

    [Fact]
    public async Task EntitlementEvaluation_ScheduledCancellationDeniesAtExactBoundaryWithoutWorkerRun()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(db, profile.Id, offer.Id, ClientSubscriptionStatus.Active,
            ClientSubscriptionPaymentStanding.Current, "subscription_scheduled_boundary");
        var boundaryUtc = DateTime.UtcNow.AddDays(1);
        subscription.CancelAtPeriodEnd = true;
        subscription.CurrentPeriodEndUtc = boundaryUtc;
        subscription.GracePeriodEndsUtc = boundaryUtc.AddDays(1);
        await db.SaveChangesAsync();
        var service = BuildEntitlementService(db);

        var before = await service.EvaluateAsync(new BillingEntitlementEvaluationRequest(
            profile.Id, BillingEntitlementKeys.ClientAppFullAccess, boundaryUtc.AddTicks(-1)));
        var atBoundary = await service.EvaluateAsync(new BillingEntitlementEvaluationRequest(
            profile.Id, BillingEntitlementKeys.ClientAppFullAccess, boundaryUtc));

        Assert.Equal(ClientEntitlementStatus.Active, before.Status);
        Assert.Equal(ClientEntitlementStatus.NotGranted, atBoundary.Status);
        Assert.Equal("SUBSCRIPTION_ENDED_AT_PERIOD_END", atBoundary.ReasonCode);
        Assert.Equal(ClientSubscriptionStatus.Active, subscription.Status);
    }

    [Fact]
    public async Task EntitlementEvaluation_UnrelatedClientCannotUseAnotherOwnersSubscription()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var owner = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, owner.Id);
        await AddSubscriptionAsync(db, owner.Id, offer.Id, ClientSubscriptionStatus.Active,
            ClientSubscriptionPaymentStanding.Current, "subscription_unrelated_owner");
        var service = BuildEntitlementService(db);
        var unrelated = await service.EvaluateAsync(new BillingEntitlementEvaluationRequest(
            Guid.NewGuid(), BillingEntitlementKeys.ClientAppFullAccess, DateTime.UtcNow));
        var emptyProfile = new ClientProfile { ClientUserId = "unsubscribed", Email = "unsubscribed@example.test" };
        db.ClientProfiles.Add(emptyProfile);
        await db.SaveChangesAsync();
        var noSubscription = await service.EvaluateAsync(new BillingEntitlementEvaluationRequest(
            emptyProfile.Id, BillingEntitlementKeys.ClientAppFullAccess, DateTime.UtcNow));

        Assert.Equal(ClientEntitlementStatus.NotGranted, unrelated.Status);
        Assert.Equal("NO_SUBSCRIPTION", unrelated.ReasonCode);
        Assert.Equal(ClientEntitlementStatus.NotGranted, noSubscription.Status);
        Assert.Equal("NO_SUBSCRIPTION", noSubscription.ReasonCode);
        Assert.Empty(db.HouseholdAccounts);
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

    [Theory]
    [InlineData(2500, "USD")]
    [InlineData(2499, "USD")]
    [InlineData(null, "USD")]
    [InlineData(2500, "CAD")]
    [InlineData(2500, null)]
    public async Task CommercePaymentReconciliationPreservesCommandedFinancialEvidence(int? amount, string? currency)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(db, profile.Id, offer.Id, ClientSubscriptionStatus.Active, ClientSubscriptionPaymentStanding.Current, "sub_commerce_guard");
        var payment = new SubscriptionPayment { Kind = SubscriptionPaymentKind.CommerceOneTime,
            Provider = BillingProvider.Square, ProviderEnvironment = BillingProviderEnvironment.Sandbox,
            ProviderPaymentId = "pay_commerce", AmountCents = 2500, Currency = "USD",
            Status = SubscriptionPaymentStatus.Processing, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
        db.SubscriptionPayments.Add(payment); await db.SaveChangesAsync();
        await RecordProviderEventAsync(db, BuildPaymentPayload("evt_commerce", "payment.updated", "pay_commerce",
            "cust", "", "COMPLETED", "2026-07-22T01:05:00Z", subscription.Id.ToString()));
        var gateway = BuildGateway();
        gateway.Setup(x => x.GetPaymentAsync(It.IsAny<BillingPaymentLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingPaymentResult(true, "pay_commerce", "COMPLETED", null, null, "req", false,
                AmountCents: amount, Currency: currency));
        var entitlements = new Mock<IBillingEntitlementService>(MockBehavior.Strict);
        await new BillingReconciliationService(db, gateway.Object, entitlements.Object).ReconcilePendingProviderEventsAsync(10);
        entitlements.VerifyNoOtherCalls();
        var saved = await db.SubscriptionPayments.SingleAsync();
        Assert.Null(saved.ClientSubscriptionId);
        Assert.Equal(2500, saved.AmountCents);
        Assert.Equal("USD", saved.Currency);
        Assert.Equal(amount == 2500 && currency == "USD" ? SubscriptionPaymentStatus.Completed : SubscriptionPaymentStatus.Processing, saved.Status);
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

    [Fact]
    public async Task UpdateClientSubscription_RequiresFounderAuthorityAndPreservesTheCurrentSchedule()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddClientProfileAsync(db);
        var offer = await AddOfferAsync(db, profile.Id);
        var subscription = await AddSubscriptionAsync(
            db,
            profile.Id,
            offer.Id,
            ClientSubscriptionStatus.Active,
            ClientSubscriptionPaymentStanding.Current,
            "subscription_update");
        var scheduledCharge = subscription.NextBillingDateUtc;
        var orchestrator = new MasterAppBillingOrchestrator(
            db,
            BuildGateway().Object,
            Mock.Of<IBillingEntitlementService>(),
            Mock.Of<IClientSubscriptionActivationPolicyService>(),
            new ClientBillingNotificationService(db));

        var denied = await orchestrator.UpdateClientSubscriptionAsync(
            new UpdateClientSubscriptionCommand(
                subscription.Id,
                ClientSubscriptionOfferPriceType.Fixed150,
                null,
                BillingAnchorSelectionMode.SpecificDayOfMonth,
                20,
                "agent-1",
                FounderAuthorized: false));
        var updated = await orchestrator.UpdateClientSubscriptionAsync(
            new UpdateClientSubscriptionCommand(
                subscription.Id,
                ClientSubscriptionOfferPriceType.Custom,
                17_500,
                BillingAnchorSelectionMode.SpecificDayOfMonth,
                20,
                "founder-oid",
                FounderAuthorized: true));

        var persisted = await db.ClientSubscriptions.SingleAsync(x => x.Id == subscription.Id);
        Assert.False(denied.Success);
        Assert.Equal("FOUNDER_SUBSCRIPTION_CONTROL_REQUIRED", denied.SafeErrorCode);
        Assert.True(updated.Success);
        Assert.Equal(17_500, persisted.MonthlyAmountCents);
        Assert.Equal(20, persisted.BillingAnchorDay);
        Assert.Equal(scheduledCharge, persisted.NextBillingDateUtc);
        Assert.Contains(await db.BillingAuditEntries.ToListAsync(), entry =>
            entry.EntityType == "ClientSubscription" &&
            entry.EntityId == subscription.Id.ToString() &&
            entry.Action == "terms_updated");

        var notification = await db.ClientBillingNotifications.SingleAsync();
        Assert.Equal(ClientBillingNotificationKind.MembershipTermsUpdated, notification.Kind);
        Assert.Contains("$100.00", notification.PlainTextBody);
        Assert.Contains("$175.00", notification.PlainTextBody);
        Assert.Contains("next scheduled charge", notification.PlainTextBody);
        Assert.Contains(scheduledCharge!.Value.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture),
            notification.PlainTextBody);

        var unchanged = await orchestrator.UpdateClientSubscriptionAsync(
            new UpdateClientSubscriptionCommand(subscription.Id, ClientSubscriptionOfferPriceType.Custom, 17_500,
                BillingAnchorSelectionMode.SpecificDayOfMonth, 20, "founder-oid", FounderAuthorized: true));
        Assert.True(unchanged.Success);
        Assert.Equal(1, await db.ClientBillingNotifications.CountAsync());
        Assert.Equal(1, await db.BillingAuditEntries.CountAsync(entry => entry.Action == "terms_updated"));
        
        var lowered = await orchestrator.UpdateClientSubscriptionAsync(
            new UpdateClientSubscriptionCommand(subscription.Id, ClientSubscriptionOfferPriceType.Custom, 5_000,
                BillingAnchorSelectionMode.FirstOfMonth, null, "founder-oid", FounderAuthorized: true));
        Assert.True(lowered.Success);
        Assert.Equal(5_000, (await db.ClientSubscriptions.SingleAsync(item => item.Id == subscription.Id)).MonthlyAmountCents);
        Assert.Equal(scheduledCharge, (await db.ClientSubscriptions.SingleAsync(item => item.Id == subscription.Id)).NextBillingDateUtc);
        var notices = await db.ClientBillingNotifications.OrderBy(item => item.CreatedUtc).ToListAsync();
        Assert.Equal(2, notices.Count);
        Assert.Contains(notices, item => item.PlainTextBody.Contains("$175.00") && item.PlainTextBody.Contains("$50.00"));
        var email = new Mock<IEmailSender>(MockBehavior.Strict);
        email.Setup(sender => sender.TrySendAsync(
                profile.Email!, "Your Legend membership terms changed", null,
                It.Is<string>(body => body.Contains("next scheduled charge", StringComparison.Ordinal)),
                null, null, null))
            .ReturnsAsync(true);
        var delivery = new ClientBillingNotificationDeliveryService(
            db, email.Object, NullLogger<ClientBillingNotificationDeliveryService>.Instance);
        var delivered = await delivery.DeliverDueAsync(10);
        Assert.Equal(2, delivered.Sent);
        Assert.All(await db.ClientBillingNotifications.ToListAsync(), item => Assert.NotNull(item.SentUtc));
    }

    private sealed class BillingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _sendAsync;
        public BillingHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync) => _sendAsync = sendAsync;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _sendAsync(request);
    }

    private static Mock<IBillingGateway> BuildInitialActivationGateway()
    {
        var gateway = BuildGateway();
        gateway.Setup(x => x.ResolveCustomerAsync(It.IsAny<BillingCustomerResolutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingCustomerResolutionResult(true, "cust_123", "COMPLETED", null,
                "Customer resolved.", "req_customer", false, "cust_123"));
        gateway.Setup(x => x.AttachPaymentMethodAsync(It.IsAny<BillingPaymentMethodAttachmentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingPaymentMethodAttachmentResult(true, "card_123", "COMPLETED", null,
                "Card vaulted.", "req_card", false, "cust_123", "card_123"));
        return gateway;
    }

    private static ActivateClientSubscriptionCommand BuildInitialActivationCommand(ClientProfile profile, ClientSubscriptionOffer offer)
    {
        var firstChargeUtc = DateTime.UtcNow;
        var renewalUtc = firstChargeUtc.AddMonths(1);
        return new ActivateClientSubscriptionCommand(profile.Id, offer.Id, "agent-1", "synthetic-vault-token",
            offer.Currency, 15, "UTC", firstChargeUtc, renewalUtc, DateOnly.FromDateTime(renewalUtc),
            true, true, true, profile.NormalizedEmail!);
    }

    private static async Task CompleteInitialActivationFromProviderEventAsync(
        MasterAppDbContext db, Mock<IBillingGateway> gateway, ClientSubscription subscription, string paymentId)
    {
        var occurredUtc = DateTime.UtcNow;
        await RecordProviderEventAsync(db, BuildPaymentPayload("evt_initial_completion", "payment.updated",
            paymentId, "cust_123", "inv_initial_completion", "COMPLETED", occurredUtc.ToString("O"), subscription.Id.ToString()));
        gateway.Setup(x => x.GetPaymentAsync(
                It.Is<BillingPaymentLookupRequest>(request => request.ProviderPaymentId == paymentId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingPaymentResult(true, paymentId, "COMPLETED", null,
                "Payment completed.", "req_initial_completion", false,
                ProviderInvoiceId: "inv_initial_completion", AmountCents: subscription.MonthlyAmountCents,
                Currency: subscription.Currency, ProviderOccurredUtc: occurredUtc));
        var reconciliation = new BillingReconciliationService(db, gateway.Object, BuildEntitlementService(db));
        await reconciliation.ReconcilePendingProviderEventsAsync(10);
        await reconciliation.ReconcileSubscriptionAsync(subscription.Id, "initial-completion");
        await reconciliation.ReconcilePendingProviderEventsAsync(10);
        await reconciliation.ReconcileSubscriptionAsync(subscription.Id, "initial-completion-replay");
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
        return new BillingEntitlementService(db, ControllerTestHelpers.BuildHouseholdMembershipService(db));
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

        var paymentMethod = new ClientPaymentMethod
        {
            ClientProfileId = clientProfileId,
            Provider = BillingProvider.Square,
            ProviderEnvironment = BillingProviderEnvironment.Sandbox,
            ProviderPaymentMethodId = "card_123",
            CardBrand = "VISA",
            Last4 = "4242"
        };
        subscription.DefaultPaymentMethodId = paymentMethod.Id;
        db.ClientPaymentMethods.Add(paymentMethod);
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
        string updatedAtUtc,
        string? referenceId = null)
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
                "reference_id": "{{referenceId ?? string.Empty}}",
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
