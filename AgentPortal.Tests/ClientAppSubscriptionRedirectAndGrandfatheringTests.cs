using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using ClientApp.Infrastructure;
using ClientApp.Models;
using ClientApp.Services;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Billing;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Moq;
using Xunit;
using ClientAccountController = ClientApp.Controllers.AccountController;
using ClientSubscriptionController = ClientApp.Controllers.SubscriptionController;

namespace AgentPortal.Tests;

public sealed class ClientAppSubscriptionRedirectAndGrandfatheringTests
{
    private static readonly DateTime CutoverUtc = new(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task PreCutoverProfile_IsGrantedClientAppAccessWithoutSubscription()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db, CutoverUtc.AddDays(-1));

        var result = await EvaluateAsync(db, profile.Id);

        Assert.Equal(ClientEntitlementStatus.Active, result.Status);
        Assert.Equal("LEGACY_PROFILE_PRE_SUBSCRIPTION_CUTOFF", result.ReasonCode);
        Assert.Empty(db.ClientSubscriptions);
    }

    [Fact]
    public async Task ProfileCreatedImmediatelyBeforeCutover_IsGrandfathered()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db, CutoverUtc.AddTicks(-1));

        var result = await EvaluateAsync(db, profile.Id);

        Assert.Equal(ClientEntitlementStatus.Active, result.Status);
        Assert.Equal("LEGACY_PROFILE_PRE_SUBSCRIPTION_CUTOFF", result.ReasonCode);
    }

    [Fact]
    public async Task ProfileCreatedAtCutover_IsNotGrandfathered()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db, CutoverUtc);

        var result = await EvaluateAsync(db, profile.Id);

        Assert.Equal(ClientEntitlementStatus.NotGranted, result.Status);
        Assert.Equal("NO_SUBSCRIPTION", result.ReasonCode);
    }

    [Fact]
    public async Task ProfileCreatedAfterCutover_StillRequiresSubscriptionActivation()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db, CutoverUtc.AddTicks(1));

        var result = await EvaluateAsync(db, profile.Id);

        Assert.Equal(ClientEntitlementStatus.NotGranted, result.Status);
        Assert.Equal("NO_SUBSCRIPTION", result.ReasonCode);
    }

    [Fact]
    public async Task SubscriptionDoesNotSendItsOwnPathIntoActivationRequired()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var controller = BuildSubscriptionController(db, new DefaultHttpContext());

        var result = await controller.Index("/Subscription?returnUrl=%2FAccount%2FActivationRequired%3FreturnUrl%3D%252FSubscription");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("ActivationRequired", redirect.ActionName);
        Assert.Equal(ClientAppReturnUrlNormalizer.SafeLandingPath, redirect.RouteValues?["returnUrl"]);
    }

    [Fact]
    public void ActivationRequiredCannotReturnToItself()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var normalizer = new ClientAppReturnUrlNormalizer();
        var controller = BuildAccountController(db, new DefaultHttpContext(), normalizer);

        var result = controller.ActivationRequired("/Account/ActivationRequired?returnUrl=%2FAccount%2FActivationRequired");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ActivationRequiredViewModel>(view.Model);
        Assert.Equal(ClientAppReturnUrlNormalizer.SafeLandingPath, model.ReturnUrl);
    }

    [Fact]
    public void RepeatedEncodedReturnUrlsAreRejected()
    {
        var normalizer = new ClientAppReturnUrlNormalizer();

        var result = normalizer.Normalize("/profile?returnUrl=%252Fprofile%253FreturnUrl%253D%25252FSubscription");

        Assert.Equal(ClientAppReturnUrlNormalizer.SafeLandingPath, result);
    }

    [Fact]
    public void ExcessivelyLongReturnUrlIsRejected()
    {
        var normalizer = new ClientAppReturnUrlNormalizer();

        var result = normalizer.Normalize("/profile?x=" + new string('a', ClientAppReturnUrlNormalizer.MaximumReturnUrlLength));

        Assert.Equal(ClientAppReturnUrlNormalizer.SafeLandingPath, result);
    }

    [Fact]
    public void ValidLocalApplicationReturnUrlIsPreserved()
    {
        var normalizer = new ClientAppReturnUrlNormalizer();

        Assert.Equal("/profile?tab=coverage", normalizer.Normalize("/profile?tab=coverage"));
    }

    [Fact]
    public void ExternalReturnUrlIsRejected()
    {
        var normalizer = new ClientAppReturnUrlNormalizer();

        Assert.Equal(ClientAppReturnUrlNormalizer.SafeLandingPath, normalizer.Normalize("https://attacker.example/next"));
        Assert.Equal(ClientAppReturnUrlNormalizer.SafeLandingPath, normalizer.Normalize("/%2Fattacker.example/next"));
    }

    [Fact]
    public async Task EntitledSubscribedUserCanOpenSubscriptionManagement()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db, CutoverUtc.AddDays(1), "subscribed-client");
        db.ClientSubscriptions.Add(new ClientSubscription
        {
            ClientProfileId = profile.Id,
            OwnerAgentUserId = "agent-1",
            Status = ClientSubscriptionStatus.Active,
            PaymentStanding = ClientSubscriptionPaymentStanding.Current,
            MonthlyAmountCents = 10000,
            CurrentPeriodStartUtc = DateTime.UtcNow,
            CurrentPeriodEndUtc = DateTime.UtcNow.AddDays(30),
            NextBillingDateUtc = DateTime.UtcNow.AddDays(30)
        });
        await db.SaveChangesAsync();

        var httpContext = CreateAuthenticatedContext("subscribed-client");
        var controller = BuildSubscriptionController(db, httpContext, ClientEntitlementStatus.Active);

        var result = await controller.Index("/profile");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ClientSubscriptionManagementViewModel>(view.Model);
        Assert.Equal("Active", model.EntitlementStatus);
        Assert.Equal("/profile", model.ReturnUrl);
    }

    [Fact]
    public async Task NewlyCreatedUnentitledUserGetsOneActivationRedirectWithNoSubscriptionContinuation()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db, CutoverUtc, "new-client");
        var controller = BuildSubscriptionController(db, CreateAuthenticatedContext("new-client"), ClientEntitlementStatus.NotGranted);

        var result = await controller.Index("/Subscription");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("ActivationRequired", redirect.ActionName);
        Assert.Equal(ClientAppReturnUrlNormalizer.SafeLandingPath, redirect.RouteValues?["returnUrl"]);
        Assert.DoesNotContain("Subscription", redirect.RouteValues?["returnUrl"]?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnauthenticatedSubscriptionRequestUsesOneNonRecursiveSignInEntry()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var normalizer = new ClientAppReturnUrlNormalizer();
        var requestContext = new DefaultHttpContext();
        requestContext.Request.Path = "/subscription";
        var entryPoint = BuildSignInEntryPoint(db, normalizer);

        var location = await entryPoint.ResolveAsync(requestContext);

        Assert.Equal("/Account/Login?returnUrl=%2FHome%2FIndex", location);
        Assert.DoesNotContain("Subscription", location, StringComparison.OrdinalIgnoreCase);
        Assert.True(location.Length <= ClientAppReturnUrlNormalizer.MaximumReturnUrlLength);
    }

    [Fact]
    public async Task RedirectFlowIsBoundedAndDoesNotAmplifyReturnUrl()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var normalizer = new ClientAppReturnUrlNormalizer();
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(x => x.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), null!, ClientAppAuthorizationPolicies.ClientSubscriptionActive))
            .ReturnsAsync(AuthorizationResult.Failed());

        var requestContext = CreateAuthenticatedContext("unbound-client");
        requestContext.Request.Path = "/profile";
        requestContext.Request.QueryString = new QueryString("?returnUrl=%2FSubscription%3FreturnUrl%3D%252FAccount%252FActivationRequired");
        var filterContext = new AuthorizationFilterContext(
            new ActionContext(requestContext, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>());

        var filter = new ClientSubscriptionAuthorizeFilter(authorization.Object, normalizer);
        await filter.OnAuthorizationAsync(filterContext);

        var subscriptionRedirect = Assert.IsType<RedirectToActionResult>(filterContext.Result);
        var firstTarget = Assert.IsType<string>(subscriptionRedirect.RouteValues?["returnUrl"]);
        Assert.Equal(ClientAppReturnUrlNormalizer.SafeLandingPath, firstTarget);

        var subscriptionController = BuildSubscriptionController(db, requestContext, returnUrlNormalizer: normalizer);
        var activationResult = await subscriptionController.Index(firstTarget);
        var activationRedirect = Assert.IsType<RedirectToActionResult>(activationResult);
        var secondTarget = Assert.IsType<string>(activationRedirect.RouteValues?["returnUrl"]);
        Assert.Equal(ClientAppReturnUrlNormalizer.SafeLandingPath, secondTarget);

        var accountController = BuildAccountController(db, requestContext, normalizer);
        var terminalResult = accountController.ActivationRequired(secondTarget);

        Assert.IsType<ViewResult>(terminalResult);
        var redirectCount = 2;
        Assert.InRange(redirectCount, 0, 2);
        Assert.True(firstTarget.Length <= ClientAppReturnUrlNormalizer.MaximumReturnUrlLength);
        Assert.True(secondTarget.Length <= ClientAppReturnUrlNormalizer.MaximumReturnUrlLength);
    }

    private static async Task<BillingEntitlementEvaluationResult> EvaluateAsync(MasterAppDbContext db, Guid profileId)
    {
        var service = new BillingEntitlementService(db, new ClientSubscriptionActivationPolicyOptions
        {
            SubscriptionRequiredForProfilesCreatedOnOrAfterUtc = CutoverUtc
        });

        return await service.EvaluateAsync(new BillingEntitlementEvaluationRequest(
            profileId,
            BillingEntitlementKeys.ClientAppFullAccess,
            CutoverUtc.AddDays(10)));
    }

    private static ClientSubscriptionController BuildSubscriptionController(
        MasterAppDbContext db,
        HttpContext httpContext,
        ClientEntitlementStatus status = ClientEntitlementStatus.NotGranted,
        ClientAppReturnUrlNormalizer? returnUrlNormalizer = null)
    {
        var entitlements = new Mock<IBillingEntitlementService>();
        entitlements
            .Setup(x => x.EvaluateAsync(It.IsAny<BillingEntitlementEvaluationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingEntitlementEvaluationResult(
                status,
                DateTime.UtcNow,
                DateTime.UtcNow.AddDays(30),
                null,
                status == ClientEntitlementStatus.Active ? null : "NO_SUBSCRIPTION",
                ClientEntitlementSourceType.Subscription,
                "test",
                status.ToString()));

        var controller = new ClientSubscriptionController(
            db,
            new EffectiveClientContextService(db),
            entitlements.Object,
            Mock.Of<IBillingOrchestrator>(),
            returnUrlNormalizer ?? new ClientAppReturnUrlNormalizer())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = Mock.Of<ITempDataDictionary>()
        };
        return controller;
    }

    private static ClientAccountController BuildAccountController(
        MasterAppDbContext db,
        HttpContext httpContext,
        ClientAppReturnUrlNormalizer normalizer)
    {
        var dataProtection = Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "clientapp-redirect-tests", Guid.NewGuid().ToString("N"))));
        var continuation = new ClientIdentityContinuationService(db, dataProtection, normalizer);
        var identityAccess = new ClientIdentityAccessService(
            db,
            Mock.Of<IBillingEntitlementService>(),
            continuation,
            normalizer);

        var controller = new ClientAccountController(identityAccess, normalizer)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = Mock.Of<ITempDataDictionary>()
        };
        return controller;
    }

    private static ClientAppSignInEntryPoint BuildSignInEntryPoint(
        MasterAppDbContext db,
        ClientAppReturnUrlNormalizer normalizer)
    {
        var dataProtection = Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "clientapp-redirect-tests", Guid.NewGuid().ToString("N"))));
        var continuation = new ClientIdentityContinuationService(db, dataProtection, normalizer);
        var identityAccess = new ClientIdentityAccessService(
            db,
            Mock.Of<IBillingEntitlementService>(),
            continuation,
            normalizer);
        return new ClientAppSignInEntryPoint(identityAccess, normalizer);
    }

    private static DefaultHttpContext CreateAuthenticatedContext(string oid)
    {
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", oid)], "TestAuth"))
        };
    }

    private static async Task<ClientProfile> AddProfileAsync(MasterAppDbContext db, DateTime createdUtc, string? externalIdentityObjectId = null)
    {
        var profile = new ClientProfile
        {
            ClientUserId = externalIdentityObjectId ?? Guid.NewGuid().ToString("N"),
            ExternalIdentityObjectId = externalIdentityObjectId,
            FirstName = "Test",
            LastName = "Client",
            Email = $"{Guid.NewGuid():N}@example.com",
            CreatedUtc = createdUtc,
            UpdatedUtc = createdUtc
        };
        db.ClientProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }
}
