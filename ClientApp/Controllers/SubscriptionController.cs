using ClientApp.Infrastructure;
using ClientApp.Models;
using ClientApp.Services;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Billing.Square;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace ClientApp.Controllers;

[Authorize]
[BypassClientSubscriptionRequirement]
public sealed class SubscriptionController : Controller
{
    private readonly MasterAppDbContext _db;
    private readonly EffectiveClientContextService _clientContextService;
    private readonly IBillingEntitlementService _entitlementService;
    private readonly IBillingOrchestrator _billingOrchestrator;
    private readonly IClientPaymentMethodService _paymentMethodService;
    private readonly SquareBillingOptions _squareOptions;
    private readonly ClientAppReturnUrlNormalizer _returnUrlNormalizer;

    public SubscriptionController(
        MasterAppDbContext db,
        EffectiveClientContextService clientContextService,
        IBillingEntitlementService entitlementService,
        IBillingOrchestrator billingOrchestrator,
        IClientPaymentMethodService paymentMethodService,
        SquareBillingOptions squareOptions,
        ClientAppReturnUrlNormalizer returnUrlNormalizer)
    {
        _db = db;
        _clientContextService = clientContextService;
        _entitlementService = entitlementService;
        _billingOrchestrator = billingOrchestrator;
        _paymentMethodService = paymentMethodService;
        _squareOptions = squareOptions;
        _returnUrlNormalizer = returnUrlNormalizer;
    }

    [HttpGet("/subscription")]
    public async Task<IActionResult> Index(string? returnUrl = null)
    {
        var target = _returnUrlNormalizer.Normalize(returnUrl);
        var context = await _clientContextService.ResolveAsync(User, Request.Cookies, allowRelink: false);
        if (context is null)
        {
            return RedirectToAction("ActivationRequired", "Account", new
            {
                returnUrl = target,
                message = "Use the client activation flow or client sign-in form before opening subscription management."
            });
        }

        var entitlement = await _entitlementService.EvaluateAsync(
            new BillingEntitlementEvaluationRequest(
                context.ClientProfileId,
                BillingEntitlementKeys.ClientAppFullAccess,
                DateTime.UtcNow),
            HttpContext.RequestAborted);

        if (!context.IsAgentView && entitlement.Status is not (ClientEntitlementStatus.Active or ClientEntitlementStatus.GracePeriod))
        {
            return RedirectToAction("ActivationRequired", "Account", new
            {
                returnUrl = target,
                message = "Your client subscription is not active. Use the activation link from your agent to continue."
            });
        }

        var latestSubscription = await _db.ClientSubscriptions
            .Where(x => x.ClientProfileId == context.ClientProfileId)
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync();

        var paymentHistory = latestSubscription is null
            ? new List<SubscriptionPayment>()
            : await _db.SubscriptionPayments
                .AsNoTracking()
                .Where(payment => payment.ClientSubscriptionId == latestSubscription.Id)
                .OrderByDescending(payment => payment.ProviderOccurredUtc ?? payment.UpdatedUtc)
                .ThenByDescending(payment => payment.CreatedUtc)
                .Take(24)
                .ToListAsync(HttpContext.RequestAborted);

        var paymentMethods = !context.IsAgentView && latestSubscription is not null
            ? await _paymentMethodService.ListAsync(context.ClientProfileId, HttpContext.RequestAborted)
            : await _db.ClientPaymentMethods
                .AsNoTracking()
                .Where(method => method.ClientProfileId == context.ClientProfileId && method.RetiredUtc == null)
                .OrderByDescending(method => method.CreatedUtc)
                .ToListAsync(HttpContext.RequestAborted);
        var paymentMethod = latestSubscription?.DefaultPaymentMethodId is Guid paymentMethodId
            ? paymentMethods.FirstOrDefault(method => method.Id == paymentMethodId)
            : null;

        ViewData["SubscriptionNotice"] = TempData["SubscriptionNotice"]?.ToString();
        return View(new ClientSubscriptionManagementViewModel
        {
            ClientSubscriptionId = latestSubscription?.Id,
            ClientName = $"{context.Profile.FirstName} {context.Profile.LastName}".Trim(),
            CurrentPlanDisplay = BuildCurrentPlanDisplay(latestSubscription),
            BillingFrequencyDisplay = latestSubscription is null ? "Not scheduled" : "Monthly",
            MonthlyAmountDisplay = latestSubscription is null
                ? "$0.00"
                : (latestSubscription.MonthlyAmountCents / 100m).ToString("C"),
            SubscriptionStatus = latestSubscription is null
                ? "Not Started"
                : ClientSubscriptionDisplay.FormatMembershipState(latestSubscription.Status, latestSubscription.PaymentStanding),
            PaymentStanding = latestSubscription is null
                ? "Unknown"
                : ClientSubscriptionDisplay.FormatPaymentStanding(latestSubscription.PaymentStanding),
            EntitlementStatus = entitlement.Status.ToString(),
            MemberSinceDisplay = FormatDate(latestSubscription?.ActivatedUtc ?? latestSubscription?.CreatedUtc),
            NextBillingDateDisplay = FormatDate(latestSubscription?.NextBillingDateUtc),
            LastSuccessfulPaymentDisplay = FormatDateTime(latestSubscription?.LastSuccessfulChargeUtc),
            GracePeriodEndDisplay = FormatDate(latestSubscription?.GracePeriodEndsUtc),
            CurrentPeriodDisplay = BuildCurrentPeriodDisplay(latestSubscription),
            CancellationState = latestSubscription is null
                ? "No active cancellation"
                : latestSubscription.Status == ClientSubscriptionStatus.GracePeriod
                    ? "Payment update needed"
                : latestSubscription.CancelAtPeriodEnd
                    ? "Cancellation scheduled at period end"
                    : latestSubscription.Status == ClientSubscriptionStatus.Canceled
                        ? "Canceled"
                        : "Active",
            PaymentRepairInstructions = BuildRepairInstructions(latestSubscription, entitlement.Status, entitlement.ReasonCode),
            HasSubscription = latestSubscription is not null,
            CanCancelSubscription = !context.IsAgentView &&
                latestSubscription?.Status is ClientSubscriptionStatus.Active or ClientSubscriptionStatus.GracePeriod,
            CanManagePaymentMethods = !context.IsAgentView &&
                latestSubscription is { IsPlatformManaged: true } &&
                latestSubscription.Status is ClientSubscriptionStatus.Active or ClientSubscriptionStatus.GracePeriod,
            CanRetryPayment = !context.IsAgentView &&
                latestSubscription is { IsPlatformManaged: true, Status: ClientSubscriptionStatus.GracePeriod } &&
                latestSubscription.GracePeriodEndsUtc > DateTime.UtcNow,
            HasPaymentMethod = paymentMethod is not null,
            BrowserPaymentReady = _squareOptions.HasBrowserCredentials(),
            BrowserPaymentSetupMessage = BuildBrowserPaymentSetupMessage(),
            SquareApplicationId = _squareOptions.ApplicationId ?? string.Empty,
            SquareLocationId = _squareOptions.LocationId ?? string.Empty,
            SquareEnvironment = _squareOptions.Environment.ToString(),
            PaymentMethodDisplay = BuildPaymentMethodDisplay(paymentMethod),
            PaymentMethodExpirationDisplay = BuildPaymentMethodExpirationDisplay(paymentMethod),
            PaymentHistory = paymentHistory
                .Select(payment => new ClientSubscriptionPaymentHistoryItemViewModel
                {
                    DateDisplay = FormatDateTime(
                        payment.ProviderOccurredUtc ??
                        payment.ScheduledChargeUtc ??
                        payment.UpdatedUtc),
                    AmountDisplay = (payment.AmountCents / 100m).ToString("C"),
                    Status = payment.Status.ToString(),
                    Kind = payment.Kind.ToString(),
                    BillingPeriodDisplay = BuildPaymentPeriodDisplay(payment)
                })
                .ToList(),
            PaymentMethods = paymentMethods
                .Select(method => BuildPaymentMethodItem(method, latestSubscription?.DefaultPaymentMethodId == method.Id))
                .ToList(),
            ReturnUrl = target
        });
    }

    [HttpPost("/subscription/payment-methods")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPaymentMethod(ClientPaymentMethodInput input, string? returnUrl = null)
    {
        var target = _returnUrlNormalizer.Normalize(returnUrl);
        var context = await ResolveSelfServiceContextAsync();
        if (context is null)
            return Forbid();

        var subscription = await GetCurrentSubscriptionAsync(context.ClientProfileId);
        if (subscription is null)
            return RedirectToSubscriptionWithNotice("No membership was found for payment-method management.", target);

        if (!HasCompletePaymentMethodInput(input))
            return RedirectToSubscriptionWithNotice("Complete the cardholder and billing-address fields before saving a payment method.", target);

        var result = await _paymentMethodService.AddAsync(
            new AddClientPaymentMethodCommand(
                context.ClientProfileId,
                subscription.Id,
                input.SourceId,
                input.CardholderName,
                BuildBillingAddress(input),
                input.MakeDefault,
                input.DisplayName,
                BillingActorType.Client,
                User.GetCanonicalUserId(),
                $"client-payment-method-add-{subscription.Id:N}"),
            HttpContext.RequestAborted);
        return RedirectToSubscriptionWithNotice(result.SanitizedSummary ?? "The payment method could not be updated right now.", target);
    }

    [HttpPost("/subscription/payment-methods/{paymentMethodId:guid}/default")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDefaultPaymentMethod(Guid paymentMethodId, string? returnUrl = null)
    {
        var target = _returnUrlNormalizer.Normalize(returnUrl);
        var context = await ResolveSelfServiceContextAsync();
        if (context is null)
            return Forbid();

        var subscription = await GetCurrentSubscriptionAsync(context.ClientProfileId);
        if (subscription is null)
            return RedirectToSubscriptionWithNotice("No membership was found for payment-method management.", target);

        var result = await _paymentMethodService.SetDefaultAsync(
            new SetDefaultClientPaymentMethodCommand(
                context.ClientProfileId,
                subscription.Id,
                paymentMethodId,
                BillingActorType.Client,
                User.GetCanonicalUserId(),
                $"client-payment-method-default-{paymentMethodId:N}"),
            HttpContext.RequestAborted);
        return RedirectToSubscriptionWithNotice(result.SanitizedSummary ?? "The default payment method could not be updated right now.", target);
    }

    [HttpPost("/subscription/payment-methods/{paymentMethodId:guid}/rename")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RenamePaymentMethod(Guid paymentMethodId, string? displayName, string? returnUrl = null)
    {
        var target = _returnUrlNormalizer.Normalize(returnUrl);
        var context = await ResolveSelfServiceContextAsync();
        if (context is null)
            return Forbid();

        var result = await _paymentMethodService.RenameAsync(
            new RenameClientPaymentMethodCommand(
                context.ClientProfileId,
                paymentMethodId,
                displayName,
                BillingActorType.Client,
                User.GetCanonicalUserId(),
                $"client-payment-method-rename-{paymentMethodId:N}"),
            HttpContext.RequestAborted);
        return RedirectToSubscriptionWithNotice(result.SanitizedSummary ?? "The payment method label could not be updated right now.", target);
    }

    [HttpPost("/subscription/payment-methods/{paymentMethodId:guid}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemovePaymentMethod(Guid paymentMethodId, string? returnUrl = null)
    {
        var target = _returnUrlNormalizer.Normalize(returnUrl);
        var context = await ResolveSelfServiceContextAsync();
        if (context is null)
            return Forbid();

        var subscription = await GetCurrentSubscriptionAsync(context.ClientProfileId);
        if (subscription is null)
            return RedirectToSubscriptionWithNotice("No membership was found for payment-method management.", target);

        var result = await _paymentMethodService.RemoveAsync(
            new RemoveClientPaymentMethodCommand(
                context.ClientProfileId,
                subscription.Id,
                paymentMethodId,
                BillingActorType.Client,
                User.GetCanonicalUserId(),
                $"client-payment-method-remove-{paymentMethodId:N}"),
            HttpContext.RequestAborted);
        return RedirectToSubscriptionWithNotice(result.SanitizedSummary ?? "The payment method could not be removed right now.", target);
    }

    [HttpPost("/subscription/cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(string? returnUrl = null)
    {
        var target = _returnUrlNormalizer.Normalize(returnUrl);
        var context = await _clientContextService.ResolveAsync(User, Request.Cookies, allowRelink: false);
        if (context is null)
        {
            return RedirectToAction("ActivationRequired", "Account", new
            {
                returnUrl = target,
                message = "Client sign-in is required before subscription changes can be made."
            });
        }

        if (context.IsAgentView)
            return Forbid();

        var entitlement = await _entitlementService.EvaluateAsync(
            new BillingEntitlementEvaluationRequest(
                context.ClientProfileId,
                BillingEntitlementKeys.ClientAppFullAccess,
                DateTime.UtcNow),
            HttpContext.RequestAborted);

        if (!context.IsAgentView && entitlement.Status is not (ClientEntitlementStatus.Active or ClientEntitlementStatus.GracePeriod))
        {
            return RedirectToAction("ActivationRequired", "Account", new
            {
                returnUrl = target,
                message = "Your client subscription is not active. Use the activation link from your agent to continue."
            });
        }

        var latestSubscription = await _db.ClientSubscriptions
            .Where(x => x.ClientProfileId == context.ClientProfileId)
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync();

        if (latestSubscription is null)
        {
            TempData["SubscriptionNotice"] = "No client subscription was found to cancel.";
            return RedirectToAction(nameof(Index), new { returnUrl = target });
        }

        var cancellation = await _billingOrchestrator.CancelClientSubscriptionAsync(
            new CancelClientSubscriptionCommand(
                latestSubscription.Id,
                false,
                BillingActorType.Client,
                User.GetCanonicalUserId(),
                $"client-cancel-{latestSubscription.Id:N}"),
            HttpContext.RequestAborted);

        if (!cancellation.Success)
        {
            TempData["SubscriptionNotice"] = cancellation.SanitizedSummary ?? "The subscription could not be updated right now.";
            return Redirect(target);
        }

        Response.Cookies.Delete("impClientProfileId");
        Response.Cookies.Delete("selfClientProfileId");
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        return RedirectToAction("Login", "Account", new
        {
            returnUrl = ClientAppReturnUrlNormalizer.SafeLandingPath,
            message = "Your subscription has been cancelled and portal access has ended."
        });
    }

    [HttpPost("/subscription/retry-payment")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryPayment(string? returnUrl = null)
    {
        var target = _returnUrlNormalizer.Normalize(returnUrl);
        var context = await ResolveSelfServiceContextAsync();
        if (context is null)
            return Forbid();

        var subscription = await GetCurrentSubscriptionAsync(context.ClientProfileId);
        if (subscription is null)
            return RedirectToSubscriptionWithNotice("No membership was found for a payment retry.", target);

        var result = await _billingOrchestrator.RetryClientSubscriptionRenewalAsync(
            new ManualClientSubscriptionRenewalRetryCommand(
                subscription.Id,
                BillingActorType.Client,
                User.GetCanonicalUserId(),
                $"client-manual-renewal-retry-{subscription.Id:N}"),
            HttpContext.RequestAborted);
        return RedirectToSubscriptionWithNotice(
            result.SanitizedSummary ?? "The membership payment retry could not be completed right now.",
            target);
    }

    private static string FormatDate(DateTime? value)
    {
        return value.HasValue
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc).ToLocalTime().ToString("MMMM d, yyyy")
            : "Not scheduled";
    }

    private static string FormatDateTime(DateTime value)
    {
        return DateTime.SpecifyKind(value, DateTimeKind.Utc)
            .ToLocalTime()
            .ToString("MMMM d, yyyy h:mm tt");
    }

    private static string FormatDateTime(DateTime? value) => value.HasValue ? FormatDateTime(value.Value) : "Not available";

    private static string BuildCurrentPeriodDisplay(ClientSubscription? subscription)
    {
        if (subscription?.CurrentPeriodStartUtc is null || subscription.CurrentPeriodEndUtc is null)
            return "Not available";

        return $"{FormatDate(subscription.CurrentPeriodStartUtc)} - {FormatDate(subscription.CurrentPeriodEndUtc)}";
    }

    private static string BuildCurrentPlanDisplay(ClientSubscription? subscription)
    {
        if (subscription is null)
            return "Not selected";

        return subscription.MonthlyAmountCents == 0
            ? "Complimentary Membership"
            : "Legend Client Portal Membership";
    }

    private static string BuildPaymentMethodDisplay(ClientPaymentMethod? paymentMethod)
    {
        if (paymentMethod is null)
            return "No saved payment method";

        var brand = string.IsNullOrWhiteSpace(paymentMethod.CardBrand)
            ? "Card"
            : paymentMethod.CardBrand.Trim();

        return string.IsNullOrWhiteSpace(paymentMethod.Last4)
            ? brand
            : $"{brand} ending in {paymentMethod.Last4.Trim()}";
    }

    private static string BuildPaymentMethodExpirationDisplay(ClientPaymentMethod? paymentMethod)
    {
        if (paymentMethod?.ExpirationMonth is null ||
            paymentMethod.ExpirationYear is null)
        {
            return "Not available";
        }

        return $"{paymentMethod.ExpirationMonth:00}/{paymentMethod.ExpirationYear:0000}";
    }

    private static ClientPaymentMethodItemViewModel BuildPaymentMethodItem(ClientPaymentMethod paymentMethod, bool isDefault)
    {
        var addressParts = new[]
            {
                paymentMethod.BillingAddressLine1,
                paymentMethod.BillingAddressLine2,
                paymentMethod.BillingCity,
                paymentMethod.BillingState,
                paymentMethod.BillingPostalCode,
                paymentMethod.BillingCountryCode
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim());
        return new ClientPaymentMethodItemViewModel
        {
            Id = paymentMethod.Id,
            IsDefault = isDefault,
            DisplayName = string.IsNullOrWhiteSpace(paymentMethod.DisplayName) ? "Payment method" : paymentMethod.DisplayName,
            CardDisplay = BuildPaymentMethodDisplay(paymentMethod),
            ExpirationDisplay = BuildPaymentMethodExpirationDisplay(paymentMethod),
            BillingAddressDisplay = string.Join(", ", addressParts) is { Length: > 0 } address
                ? address
                : "Billing address not available"
        };
    }

    private async Task<EffectiveClientContext?> ResolveSelfServiceContextAsync()
    {
        var context = await _clientContextService.ResolveAsync(User, Request.Cookies, allowRelink: false);
        return context is { IsAgentView: false } ? context : null;
    }

    private Task<ClientSubscription?> GetCurrentSubscriptionAsync(Guid clientProfileId)
    {
        return _db.ClientSubscriptions
            .Where(subscription => subscription.ClientProfileId == clientProfileId)
            .OrderByDescending(subscription => subscription.UpdatedUtc)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
    }

    private RedirectToActionResult RedirectToSubscriptionWithNotice(string notice, string returnUrl)
    {
        TempData["SubscriptionNotice"] = notice;
        return RedirectToAction(nameof(Index), new { returnUrl });
    }

    private string BuildBrowserPaymentSetupMessage()
    {
        if (_squareOptions.HasBrowserCredentials())
            return string.Empty;

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(_squareOptions.ApplicationId))
            missing.Add("Square application ID");
        if (string.IsNullOrWhiteSpace(_squareOptions.LocationId))
            missing.Add("Square location ID");
        return $"Secure card setup is unavailable because {string.Join(" and ", missing)} is not configured.";
    }

    private static BillingPostalAddress BuildBillingAddress(ClientPaymentMethodInput input)
    {
        return new BillingPostalAddress(
            input.BillingAddressLine1.Trim(),
            string.IsNullOrWhiteSpace(input.BillingAddressLine2) ? null : input.BillingAddressLine2.Trim(),
            input.BillingCity.Trim(),
            input.BillingState.Trim(),
            input.BillingPostalCode.Trim(),
            input.BillingCountryCode.Trim().ToUpperInvariant());
    }

    private static bool HasCompletePaymentMethodInput(ClientPaymentMethodInput input)
    {
        return !string.IsNullOrWhiteSpace(input.SourceId) &&
               !string.IsNullOrWhiteSpace(input.CardholderName) &&
               !string.IsNullOrWhiteSpace(input.BillingAddressLine1) &&
               !string.IsNullOrWhiteSpace(input.BillingCity) &&
               !string.IsNullOrWhiteSpace(input.BillingState) &&
               !string.IsNullOrWhiteSpace(input.BillingPostalCode) &&
               !string.IsNullOrWhiteSpace(input.BillingCountryCode);
    }

    private static string BuildPaymentPeriodDisplay(SubscriptionPayment payment)
    {
        if (payment.BillingPeriodStartUtc is null || payment.BillingPeriodEndUtc is null)
            return "Not available";

        return $"{FormatDate(payment.BillingPeriodStartUtc)} - {FormatDate(payment.BillingPeriodEndUtc)}";
    }

    private static string BuildRepairInstructions(ClientSubscription? subscription, ClientEntitlementStatus entitlementStatus, string? entitlementReasonCode)
    {
        if (subscription is null)
            return "Use your activation link or contact your agent to begin service.";

        if (subscription.Status == ClientSubscriptionStatus.GracePeriod ||
            subscription.PaymentStanding == ClientSubscriptionPaymentStanding.GracePeriod ||
            entitlementStatus == ClientEntitlementStatus.GracePeriod)
        {
            var graceEnd = FormatDate(subscription.GracePeriodEndsUtc);
            return $"Payment update needed. Your membership remains active while you update your payment method{(graceEnd == "Not scheduled" ? string.Empty : $" by {graceEnd}")}.";
        }

        if (entitlementStatus == ClientEntitlementStatus.Active)
            return "Your subscription is in good standing. You can cancel anytime from View / Edit Profile.";

        if (subscription.PaymentStanding is ClientSubscriptionPaymentStanding.PastDue or ClientSubscriptionPaymentStanding.Failed or ClientSubscriptionPaymentStanding.RequiresAction)
            return "Billing needs attention. Update your payment method, then try the payment again. Contact your agent if you need help.";

        return "If you need billing help, contact your agent before the current period ends.";
    }
}
