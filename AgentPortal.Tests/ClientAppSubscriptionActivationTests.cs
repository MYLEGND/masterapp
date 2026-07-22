using System;
using System.IO;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClientApp.Models;
using ClientApp.Services;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Billing.Square;
using Infrastructure.Data;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using ClientAccountController = ClientApp.Controllers.AccountController;

namespace AgentPortal.Tests;

public class ClientAppSubscriptionActivationTests
{
    [Fact]
    public async Task ContinuationService_ConsumedContinuation_CannotBeReused()
    {
        using var db = BuildDb();
        var profile = await AddProfileAsync(db, "client@example.com");
        var service = BuildContinuationService(db);

        var (protectedState, _) = await service.CreateProtectedStateAsync(
            profile.Id,
            profile.NormalizedEmail ?? profile.Email,
            "/profile",
            ClientIdentityContinuationPurpose.SignIn);

        var initialValidation = await service.ValidateProtectedStateAsync(protectedState);
        Assert.True(initialValidation.Success);
        Assert.NotNull(initialValidation.Continuation);

        await service.ConsumeAsync(initialValidation.Continuation!);

        var reusedValidation = await service.ValidateProtectedStateAsync(protectedState);
        Assert.False(reusedValidation.Success);
        Assert.Equal("USED_CONTINUATION", reusedValidation.SafeErrorCode);
    }

    [Fact]
    public async Task AccountController_AzureLogin_WithValidContinuation_ReturnsChallenge()
    {
        using var db = BuildDb();
        var profile = await AddProfileAsync(db, "client@example.com");
        var continuationService = BuildContinuationService(db);
        var entitlementService = BuildEntitlementService(ClientEntitlementStatus.Active);
        var identityAccessService = new ClientIdentityAccessService(db, entitlementService.Object, continuationService, new ClientAppReturnUrlNormalizer());

        var (protectedState, expiresUtc) = await continuationService.CreateProtectedStateAsync(
            profile.Id,
            profile.NormalizedEmail ?? profile.Email,
            "/profile",
            ClientIdentityContinuationPurpose.SignIn);

        var httpContext = new DefaultHttpContext();
        AttachContinuationCookie(httpContext, continuationService, protectedState, expiresUtc);

        var controller = BuildAccountController(identityAccessService, httpContext);
        var result = await controller.AzureLogin("/profile");

        var challenge = Assert.IsType<ChallengeResult>(result);
        Assert.Contains("OpenIdConnect", challenge.AuthenticationSchemes);
    }

    [Fact]
    public async Task AccountController_AzureLogin_WithoutContinuation_RedirectsToActivationRequired()
    {
        using var db = BuildDb();
        var continuationService = BuildContinuationService(db);
        var entitlementService = BuildEntitlementService(ClientEntitlementStatus.Active);
        var identityAccessService = new ClientIdentityAccessService(db, entitlementService.Object, continuationService, new ClientAppReturnUrlNormalizer());
        var controller = BuildAccountController(identityAccessService, new DefaultHttpContext());

        var result = await controller.AzureLogin("/profile");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("ActivationRequired", redirect.ActionName);
        Assert.Equal("/profile", redirect.RouteValues?["returnUrl"]);
    }

    [Fact]
    public async Task AccountController_Login_NormalizesAccountLoopReturnUrl()
    {
        using var db = BuildDb();
        var continuationService = BuildContinuationService(db);
        var entitlementService = BuildEntitlementService(ClientEntitlementStatus.Active);
        var identityAccessService = new ClientIdentityAccessService(db, entitlementService.Object, continuationService, new ClientAppReturnUrlNormalizer());
        var controller = BuildAccountController(identityAccessService, new DefaultHttpContext());

        var result = await controller.Login("/Account/LoggedOut");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ClientLoginViewModel>(view.Model);
        Assert.Equal(ClientAppReturnUrlNormalizer.SafeLandingPath, model.ReturnUrl);
    }

    [Fact]
    public async Task AccountController_Login_AuthenticatedClientWithoutEntitlement_DoesNotRedirectIntoPortal()
    {
        using var db = BuildDb();
        await AddProfileAsync(db, "client@example.com", "client-oid");

        var continuationService = BuildContinuationService(db);
        var entitlementService = BuildEntitlementService(ClientEntitlementStatus.NotGranted);
        var identityAccessService = new ClientIdentityAccessService(db, entitlementService.Object, continuationService, new ClientAppReturnUrlNormalizer());
        var httpContext = new DefaultHttpContext
        {
            User = BuildPrincipal("client-oid", "client@example.com")
        };
        var controller = BuildAccountController(identityAccessService, httpContext);

        var result = await controller.Login("/Home/Index");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ClientLoginViewModel>(view.Model);
        Assert.Equal("/Home/Index", model.ReturnUrl);
        Assert.Contains("subscription", model.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrepareClientSignIn_InactiveEntitlement_BlocksChallenge()
    {
        using var db = BuildDb();
        await AddProfileAsync(db, "client@example.com");

        var continuationService = BuildContinuationService(db);
        var entitlementService = BuildEntitlementService(ClientEntitlementStatus.NotGranted);
        var service = new ClientIdentityAccessService(db, entitlementService.Object, continuationService, new ClientAppReturnUrlNormalizer());

        var result = await service.PrepareClientSignInAsync("client@example.com", "/profile");

        Assert.False(result.Success);
        Assert.Equal("CLIENT_NOT_ACTIVE", result.SafeErrorCode);
        Assert.Null(result.ProtectedState);
    }

    [Fact]
    public async Task PrepareClientSignIn_ActiveEntitlement_ReturnsProtectedState()
    {
        using var db = BuildDb();
        await AddProfileAsync(db, "client@example.com");

        var continuationService = BuildContinuationService(db);
        var entitlementService = BuildEntitlementService(ClientEntitlementStatus.Active);
        var service = new ClientIdentityAccessService(db, entitlementService.Object, continuationService, new ClientAppReturnUrlNormalizer());

        var result = await service.PrepareClientSignInAsync("client@example.com", "/profile");

        Assert.True(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.ProtectedState));
        Assert.True(result.ExpiresUtc.HasValue);
        Assert.Equal(1, await db.ClientIdentityContinuations.CountAsync());
    }

    [Fact]
    public async Task CompleteClientSignIn_WithoutContinuation_ClientPrincipalIsBlocked()
    {
        using var db = BuildDb();
        var continuationService = BuildContinuationService(db);
        var entitlementService = BuildEntitlementService(ClientEntitlementStatus.Active);
        var service = new ClientIdentityAccessService(db, entitlementService.Object, continuationService, new ClientAppReturnUrlNormalizer());

        var result = await service.CompleteClientSignInAsync(
            new DefaultHttpContext(),
            BuildPrincipal("client-oid", "client@example.com"));

        Assert.False(result.Success);
        Assert.Equal("MISSING_CONTINUATION", result.SafeErrorCode);
    }

    [Fact]
    public async Task CompleteClientSignIn_WithoutContinuation_AgentPrincipalCanBypass()
    {
        using var db = BuildDb();
        var continuationService = BuildContinuationService(db);
        var entitlementService = BuildEntitlementService(ClientEntitlementStatus.Active);
        var service = new ClientIdentityAccessService(db, entitlementService.Object, continuationService, new ClientAppReturnUrlNormalizer());

        var result = await service.CompleteClientSignInAsync(
            new DefaultHttpContext(),
            BuildPrincipal("agent-oid", "agent@mylegnd.com"),
            "/support");

        Assert.True(result.Success);
        Assert.Equal("/support", result.ReturnUrl);
    }

    [Fact]
    public async Task CompleteClientSignIn_WithoutContinuation_AgentCannotBypassIntoClientRoute()
    {
        using var db = BuildDb();
        var continuationService = BuildContinuationService(db);
        var entitlementService = BuildEntitlementService(ClientEntitlementStatus.Active);
        var service = new ClientIdentityAccessService(db, entitlementService.Object, continuationService, new ClientAppReturnUrlNormalizer());

        var result = await service.CompleteClientSignInAsync(
            new DefaultHttpContext(),
            BuildPrincipal("agent-oid", "agent@mylegnd.com"),
            "/Home/Index");

        Assert.False(result.Success);
        Assert.Equal("MISSING_CONTINUATION", result.SafeErrorCode);
    }

    [Fact]
    public async Task CompleteClientSignIn_ConflictingIdentity_IsRejected()
    {
        using var db = BuildDb();
        var targetProfile = await AddProfileAsync(db, "client@example.com");
        await AddProfileAsync(db, "other@example.com", "other-oid");

        var continuationService = BuildContinuationService(db);
        var entitlementService = BuildEntitlementService(ClientEntitlementStatus.Active);
        var service = new ClientIdentityAccessService(db, entitlementService.Object, continuationService, new ClientAppReturnUrlNormalizer());

        var (protectedState, expiresUtc) = await continuationService.CreateProtectedStateAsync(
            targetProfile.Id,
            targetProfile.NormalizedEmail ?? targetProfile.Email,
            "/profile",
            ClientIdentityContinuationPurpose.SignIn);

        var httpContext = new DefaultHttpContext();
        AttachContinuationCookie(httpContext, continuationService, protectedState, expiresUtc);

        var result = await service.CompleteClientSignInAsync(
            httpContext,
            BuildPrincipal("other-oid", "client@example.com"));

        Assert.False(result.Success);
        Assert.Equal("IDENTITY_CONFLICT", result.SafeErrorCode);

        var persistedProfile = await db.ClientProfiles.SingleAsync(x => x.Id == targetProfile.Id);
        Assert.True(string.IsNullOrWhiteSpace(persistedProfile.ExternalIdentityObjectId));
    }

    [Fact]
    public async Task CompleteClientSignIn_BindsIdentityAndConsumesContinuation()
    {
        using var db = BuildDb();
        var profile = await AddProfileAsync(db, "client@example.com");

        var continuationService = BuildContinuationService(db);
        var entitlementService = BuildEntitlementService(ClientEntitlementStatus.Active);
        var service = new ClientIdentityAccessService(db, entitlementService.Object, continuationService, new ClientAppReturnUrlNormalizer());

        var (protectedState, expiresUtc) = await continuationService.CreateProtectedStateAsync(
            profile.Id,
            profile.NormalizedEmail ?? profile.Email,
            "/profile",
            ClientIdentityContinuationPurpose.SignIn);

        var httpContext = new DefaultHttpContext();
        AttachContinuationCookie(httpContext, continuationService, protectedState, expiresUtc);

        var result = await service.CompleteClientSignInAsync(
            httpContext,
            BuildPrincipal("client-oid", "client@example.com"));

        Assert.True(result.Success);
        Assert.Equal("/profile", result.ReturnUrl);

        var persistedProfile = await db.ClientProfiles.SingleAsync(x => x.Id == profile.Id);
        Assert.Equal("client-oid", persistedProfile.ExternalIdentityObjectId);

        var continuation = await db.ClientIdentityContinuations.SingleAsync();
        Assert.True(continuation.ConsumedUtc.HasValue);
    }

    [Fact]
    public async Task CompleteClientSignIn_EmailChangedAfterPreparation_BlocksTheStaleContinuation()
    {
        using var db = BuildDb();
        var profile = await AddProfileAsync(db, "client@example.com");
        var continuationService = BuildContinuationService(db);
        var entitlementService = BuildEntitlementService(ClientEntitlementStatus.Active);
        var service = new ClientIdentityAccessService(db, entitlementService.Object, continuationService, new ClientAppReturnUrlNormalizer());

        var (protectedState, expiresUtc) = await continuationService.CreateProtectedStateAsync(
            profile.Id,
            "client@example.com",
            "/profile",
            ClientIdentityContinuationPurpose.SignIn);

        profile.Email = "new-email@example.com";
        profile.NormalizedEmail = "new-email@example.com";
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext();
        AttachContinuationCookie(httpContext, continuationService, protectedState, expiresUtc);

        var result = await service.CompleteClientSignInAsync(
            httpContext,
            BuildPrincipal("client-oid", "client@example.com"));

        Assert.False(result.Success);
        Assert.Equal("EMAIL_CHANGED", result.SafeErrorCode);
    }

    [Fact]
    public async Task SubscriptionIdentitySync_EmailChange_SupersedesOldInvitationAndContinuation()
    {
        using var db = BuildDb();
        var profile = await AddProfileAsync(db, "client@example.com");
        var offer = await AddOfferAsync(db, profile.Id);
        var invitation = await AddInvitationAsync(
            db,
            profile,
            offer,
            "old-email-activation",
            SubscriptionActivationInvitationStatus.Sent,
            DateTime.UtcNow.AddDays(1));
        var continuationService = BuildContinuationService(db);
        await continuationService.CreateProtectedStateAsync(
            profile.Id,
            "client@example.com",
            "/profile",
            ClientIdentityContinuationPurpose.SignIn);

        var sync = new ClientSubscriptionIdentitySyncService(db);
        var result = await sync.SynchronizeAfterEmailChangeAsync(
            profile.Id,
            "client@example.com",
            "new-email@example.com");
        await db.SaveChangesAsync();

        Assert.True(result.EmailChanged);
        Assert.True(result.RequiresReplacementInvitation);
        Assert.Equal(1, result.InvalidatedContinuationCount);
        Assert.Equal(SubscriptionActivationInvitationStatus.Superseded, invitation.Status);
        Assert.NotNull(invitation.SupersededUtc);
        Assert.NotNull((await db.ClientIdentityContinuations.SingleAsync()).ConsumedUtc);
    }

    [Fact]
    public async Task ActivationContext_ExpiredInvitation_MarksInvitationExpired()
    {
        using var db = BuildDb();
        var profile = await AddProfileAsync(db, "client@example.com");
        var offer = await AddOfferAsync(db, profile.Id);
        const string token = "expired-token";
        var invitation = await AddInvitationAsync(
            db,
            profile,
            offer,
            token,
            SubscriptionActivationInvitationStatus.Pending,
            DateTime.UtcNow.AddMinutes(-5));

        var service = BuildActivationService(
            db,
            new Mock<IBillingOrchestrator>(),
            BuildActivationPolicyService());

        var result = await service.GetContextAsync(token);

        Assert.Equal(SubscriptionActivationAvailability.Expired, result.Availability);
        var persisted = await db.SubscriptionActivationInvitations.SingleAsync(x => x.Id == invitation.Id);
        Assert.Equal(SubscriptionActivationInvitationStatus.Expired, persisted.Status);
    }

    [Fact]
    public async Task ActivationContext_RevokedInvitation_ReturnsUnavailable()
    {
        using var db = BuildDb();
        var profile = await AddProfileAsync(db, "client@example.com");
        var offer = await AddOfferAsync(db, profile.Id);
        const string token = "revoked-token";
        await AddInvitationAsync(
            db,
            profile,
            offer,
            token,
            SubscriptionActivationInvitationStatus.Revoked,
            DateTime.UtcNow.AddDays(2));

        var service = BuildActivationService(
            db,
            new Mock<IBillingOrchestrator>(),
            BuildActivationPolicyService());

        var result = await service.GetContextAsync(token);

        Assert.Equal(SubscriptionActivationAvailability.Unavailable, result.Availability);
    }

    [Fact]
    public async Task ActivateAsync_FailedActivation_DoesNotCreateContinuation()
    {
        using var db = BuildDb();
        var profile = await AddProfileAsync(db, "client@example.com");
        var offer = await AddOfferAsync(db, profile.Id);
        const string token = "activation-failure-token";
        await AddInvitationAsync(
            db,
            profile,
            offer,
            token,
            SubscriptionActivationInvitationStatus.Pending,
            DateTime.UtcNow.AddDays(2));

        var orchestrator = new Mock<IBillingOrchestrator>();
        orchestrator
            .Setup(x => x.ActivateClientSubscriptionAsync(It.IsAny<ActivateClientSubscriptionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivateClientSubscriptionResult(
                false,
                "PAYMENT_FAILED",
                "The first payment was declined.",
                null,
                false,
                null,
                null,
                new BillingSubscriptionResult(false, null, "FAILED", "PAYMENT_FAILED", "The first payment was declined.", null, false)));

        var service = BuildActivationService(db, orchestrator, BuildActivationPolicyService());

        var result = await service.ActivateAsync(token, BuildPaymentInput());

        Assert.False(result.Success);
        Assert.Equal("PAYMENT_FAILED", result.SafeErrorCode);
        Assert.Equal(0, await db.ClientIdentityContinuations.CountAsync());
    }

    [Fact]
    public async Task ActivateAsync_SuccessCreatesContinuationAndUsesAuthoritativeInputs()
    {
        using var db = BuildDb();
        var profile = await AddProfileAsync(db, "client@example.com");
        var offer = await AddOfferAsync(db, profile.Id, BillingAnchorSelectionMode.SpecificDayOfMonth, 15);
        const string token = "activation-success-token";
        var invitation = await AddInvitationAsync(
            db,
            profile,
            offer,
            token,
            SubscriptionActivationInvitationStatus.Pending,
            DateTime.UtcNow.AddDays(2));

        var expectedSchedule = BuildSchedule(15);
        var policyService = BuildActivationPolicyService(expectedSchedule, "plan-variation-1");
        var orchestrator = new Mock<IBillingOrchestrator>();
        orchestrator
            .Setup(x => x.ActivateClientSubscriptionAsync(
                It.Is<ActivateClientSubscriptionCommand>(command =>
                    command.ClientProfileId == profile.Id &&
                    command.ClientSubscriptionOfferId == offer.Id &&
                    command.ProviderPlanVariationId == "plan-variation-1" &&
                    command.BillingAnchorDay == expectedSchedule.BillingAnchorDay &&
                    command.BillingTimeZoneId == expectedSchedule.BillingTimeZoneId &&
                    command.FirstChargeUtc == expectedSchedule.FirstChargeUtc &&
                    command.FirstRecurringRenewalUtc == expectedSchedule.FirstRecurringRenewalUtc &&
                    command.IntendedNormalizedEmail == invitation.IntendedNormalizedEmail),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivateClientSubscriptionResult(
                true,
                null,
                "Activated.",
                null,
                false,
                new ClientSubscription
                {
                    Id = Guid.NewGuid(),
                    ClientProfileId = profile.Id,
                    AcceptedOfferId = offer.Id,
                    OwnerAgentUserId = offer.OwnerAgentUserId,
                    MonthlyAmountCents = offer.MonthlyAmountCents,
                    Currency = offer.Currency,
                    Status = ClientSubscriptionStatus.Active,
                    PaymentStanding = ClientSubscriptionPaymentStanding.Current
                },
                new ClientEntitlement
                {
                    ClientProfileId = profile.Id,
                    EntitlementKey = BillingEntitlementKeys.ClientAppFullAccess,
                    Status = ClientEntitlementStatus.Active,
                    SourceId = "sub-1"
                },
                new BillingSubscriptionResult(true, "sub-1", "ACTIVE", null, "Activated.", null, false)));

        var service = BuildActivationService(db, orchestrator, policyService);
        var result = await service.ActivateAsync(token, BuildPaymentInput());

        Assert.True(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.ProtectedContinuationState));
        Assert.True(result.ContinuationExpiresUtc.HasValue);
        Assert.Equal(1, await db.ClientIdentityContinuations.CountAsync());
        orchestrator.VerifyAll();
    }

    private static MasterAppDbContext BuildDb() => ControllerTestHelpers.BuildDb();

    private static ClientIdentityContinuationService BuildContinuationService(MasterAppDbContext db)
    {
        var keyPath = Path.Combine(Path.GetTempPath(), "clientapp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keyPath);
        return new ClientIdentityContinuationService(
            db,
            DataProtectionProvider.Create(new DirectoryInfo(keyPath)),
            new ClientAppReturnUrlNormalizer());
    }

    private static ClientAccountController BuildAccountController(ClientIdentityAccessService identityAccessService, HttpContext httpContext)
    {
        var authenticationService = new Mock<IAuthenticationService>();
        authenticationService
            .Setup(x => x.SignOutAsync(httpContext, It.IsAny<string>(), It.IsAny<AuthenticationProperties>()))
            .Returns(Task.CompletedTask);
        httpContext.RequestServices = new ServiceCollection()
            .AddSingleton(authenticationService.Object)
            .BuildServiceProvider();

        var controller = new ClientAccountController(identityAccessService, new ClientAppReturnUrlNormalizer())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
        controller.TempData = Mock.Of<ITempDataDictionary>();

        var urlHelper = new Mock<IUrlHelper>();
        urlHelper.Setup(x => x.IsLocalUrl(It.IsAny<string>()))
            .Returns<string>(value =>
                !string.IsNullOrWhiteSpace(value) &&
                value.StartsWith("/", StringComparison.Ordinal) &&
                !value.StartsWith("//", StringComparison.Ordinal));
        controller.Url = urlHelper.Object;
        return controller;
    }

    private static Mock<IBillingEntitlementService> BuildEntitlementService(ClientEntitlementStatus status)
    {
        var entitlementService = new Mock<IBillingEntitlementService>();
        entitlementService
            .Setup(x => x.EvaluateAsync(It.IsAny<BillingEntitlementEvaluationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingEntitlementEvaluationResult(
                status,
                DateTime.UtcNow,
                DateTime.UtcNow.AddDays(30),
                null,
                null,
                ClientEntitlementSourceType.Subscription,
                "sub-1",
                status.ToString()));
        return entitlementService;
    }

    private static Mock<IClientSubscriptionActivationPolicyService> BuildActivationPolicyService(
        ClientSubscriptionActivationSchedule? schedule = null,
        string providerPlanVariationId = "plan-variation-1")
    {
        var resolvedSchedule = schedule ?? BuildSchedule(1);
        var policyService = new Mock<IClientSubscriptionActivationPolicyService>();
        policyService
            .Setup(x => x.ResolveActivationSchedule(It.IsAny<ClientSubscriptionOffer>(), It.IsAny<DateTime>()))
            .Returns(resolvedSchedule);
        policyService
            .Setup(x => x.ResolveProviderPlanVariationId(It.IsAny<ClientSubscriptionOffer>()))
            .Returns(providerPlanVariationId);
        return policyService;
    }

    private static SubscriptionActivationService BuildActivationService(
        MasterAppDbContext db,
        Mock<IBillingOrchestrator> orchestrator,
        Mock<IClientSubscriptionActivationPolicyService> policyService)
    {
        return new SubscriptionActivationService(
            db,
            orchestrator.Object,
            policyService.Object,
            new SquareBillingOptions
            {
                ApplicationId = "sq0idp-test",
                LocationId = "location-1",
                Environment = BillingProviderEnvironment.Sandbox
            },
            BuildContinuationService(db),
            new ClientAppReturnUrlNormalizer());
    }

    private static ClaimsPrincipal BuildPrincipal(string oid, string email)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("oid", oid),
            new Claim("preferred_username", email)
        ], "TestAuth"));
    }

    private static void AttachContinuationCookie(
        HttpContext httpContext,
        ClientIdentityContinuationService continuationService,
        string protectedState,
        DateTime expiresUtc)
    {
        continuationService.StoreCookie(httpContext.Response, protectedState, expiresUtc);
        var cookieHeader = httpContext.Response.Headers.SetCookie.ToString();
        httpContext.Request.Headers.Cookie = cookieHeader.Split(';', 2, StringSplitOptions.TrimEntries)[0];
    }

    private static async Task<ClientProfile> AddProfileAsync(
        MasterAppDbContext db,
        string email,
        string? externalIdentityObjectId = null)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var profile = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = $"client-{Guid.NewGuid():N}",
            ExternalIdentityObjectId = externalIdentityObjectId,
            FirstName = "Test",
            LastName = "Client",
            Email = email,
            NormalizedEmail = normalizedEmail,
            Phone = "5550001234",
            MaritalStatus = "Single",
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
        BillingAnchorSelectionMode selectionMode = BillingAnchorSelectionMode.FirstOfMonth,
        int? selectedBillingAnchorDay = 1)
    {
        var offer = new ClientSubscriptionOffer
        {
            Id = Guid.NewGuid(),
            ClientProfileId = clientProfileId,
            OwnerAgentUserId = "agent-1",
            PriceType = ClientSubscriptionOfferPriceType.Fixed100,
            MonthlyAmountCents = 10000,
            Currency = "USD",
            BillingAnchorSelectionMode = selectionMode,
            SelectedBillingAnchorDay = selectedBillingAnchorDay,
            Status = ClientSubscriptionOfferStatus.Offered,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        db.ClientSubscriptionOffers.Add(offer);
        await db.SaveChangesAsync();
        return offer;
    }

    private static async Task<SubscriptionActivationInvitation> AddInvitationAsync(
        MasterAppDbContext db,
        ClientProfile profile,
        ClientSubscriptionOffer offer,
        string plainToken,
        SubscriptionActivationInvitationStatus status,
        DateTime expiresUtc)
    {
        var invitation = new SubscriptionActivationInvitation
        {
            Id = Guid.NewGuid(),
            ClientProfileId = profile.Id,
            ClientProfile = profile,
            ClientSubscriptionOfferId = offer.Id,
            ClientSubscriptionOffer = offer,
            TokenHash = Hash(plainToken),
            IntendedNormalizedEmail = profile.NormalizedEmail ?? profile.Email.ToLowerInvariant(),
            Status = status,
            ExpiresUtc = expiresUtc,
            CreatedByAgentUserId = "agent-1",
            CreatedUtc = DateTime.UtcNow
        };

        db.SubscriptionActivationInvitations.Add(invitation);
        await db.SaveChangesAsync();
        return invitation;
    }

    private static SubscriptionActivationPaymentInput BuildPaymentInput()
    {
        return new SubscriptionActivationPaymentInput
        {
            SourceId = "cnon:card-nonce-ok",
            CardholderName = "Test Client",
            ReturnUrl = "/profile",
            BillingAuthorizationAccepted = true
        };
    }

    private static ClientSubscriptionActivationSchedule BuildSchedule(int? billingAnchorDay)
    {
        return new ClientSubscriptionActivationSchedule(
            10000,
            "USD",
            billingAnchorDay,
            "America/Phoenix",
            14,
            17,
            new DateTime(2026, 7, 22, 15, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 15, 15, 0, 0, DateTimeKind.Utc),
            new DateOnly(2026, 8, 15));
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
