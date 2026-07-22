using ClientApp.Infrastructure;
using ClientApp.Models;
using ClientApp.Services;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
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

    public SubscriptionController(
        MasterAppDbContext db,
        EffectiveClientContextService clientContextService,
        IBillingEntitlementService entitlementService,
        IBillingOrchestrator billingOrchestrator)
    {
        _db = db;
        _clientContextService = clientContextService;
        _entitlementService = entitlementService;
        _billingOrchestrator = billingOrchestrator;
    }

    [HttpGet("/subscription")]
    public async Task<IActionResult> Index(string returnUrl = "/profile")
    {
        var context = await _clientContextService.ResolveAsync(User, Request.Cookies, allowRelink: false);
        if (context is null)
        {
            return RedirectToAction("ActivationRequired", "Account", new
            {
                returnUrl = NormalizeReturnUrl(returnUrl),
                message = "Use the client activation flow or client sign-in form before opening subscription management."
            });
        }

        var latestSubscription = await _db.ClientSubscriptions
            .Where(x => x.ClientProfileId == context.ClientProfileId)
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync();

        var entitlement = await _entitlementService.EvaluateAsync(
            new BillingEntitlementEvaluationRequest(
                context.ClientProfileId,
                BillingEntitlementKeys.ClientAppFullAccess,
                DateTime.UtcNow),
            HttpContext.RequestAborted);

        var isLegacyGrandfatheredAccess =
            latestSubscription is null &&
            entitlement.Status is ClientEntitlementStatus.Active or ClientEntitlementStatus.GracePeriod &&
            string.Equals(entitlement.ReasonCode, "LEGACY_PROFILE_PRE_SUBSCRIPTION_CUTOFF", StringComparison.OrdinalIgnoreCase);

        ViewData["SubscriptionNotice"] = TempData["SubscriptionNotice"]?.ToString();
        return View(new ClientSubscriptionManagementViewModel
        {
            ClientName = $"{context.Profile.FirstName} {context.Profile.LastName}".Trim(),
            MonthlyAmountDisplay = isLegacyGrandfatheredAccess
                ? "Not required"
                : latestSubscription is null
                    ? "$0.00"
                    : (latestSubscription.MonthlyAmountCents / 100m).ToString("C"),
            SubscriptionStatus = isLegacyGrandfatheredAccess
                ? "Grandfathered access"
                : latestSubscription?.Status.ToString() ?? "Not Started",
            PaymentStanding = isLegacyGrandfatheredAccess
                ? "Not required"
                : latestSubscription?.PaymentStanding.ToString() ?? "Unknown",
            EntitlementStatus = isLegacyGrandfatheredAccess
                ? "Active (Legacy Access)"
                : entitlement.Status.ToString(),
            NextBillingDateDisplay = isLegacyGrandfatheredAccess
                ? "Not required"
                : FormatDate(latestSubscription?.NextBillingDateUtc),
            CurrentPeriodDisplay = isLegacyGrandfatheredAccess
                ? "Legacy client access remains available without a subscription."
                : BuildCurrentPeriodDisplay(latestSubscription),
            CancellationState = isLegacyGrandfatheredAccess
                ? "No subscription on file"
                : latestSubscription is null
                    ? "No active cancellation"
                : latestSubscription.CancelAtPeriodEnd
                    ? "Cancellation scheduled at period end"
                    : latestSubscription.Status == ClientSubscriptionStatus.Canceled
                        ? "Canceled"
                        : "Active",
            PaymentRepairInstructions = BuildRepairInstructions(latestSubscription, entitlement.Status, entitlement.ReasonCode),
            CanCancelAtPeriodEnd = latestSubscription is not null &&
                                   latestSubscription.Status is ClientSubscriptionStatus.Active or ClientSubscriptionStatus.GracePeriod &&
                                   !latestSubscription.CancelAtPeriodEnd,
            ReturnUrl = NormalizeReturnUrl(returnUrl)
        });
    }

    [HttpPost("/subscription/cancel-at-period-end")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelAtPeriodEnd(string returnUrl = "/profile")
    {
        var context = await _clientContextService.ResolveAsync(User, Request.Cookies, allowRelink: false);
        if (context is null)
        {
            return RedirectToAction("ActivationRequired", "Account", new
            {
                returnUrl = NormalizeReturnUrl(returnUrl),
                message = "Client sign-in is required before subscription changes can be made."
            });
        }

        var latestSubscription = await _db.ClientSubscriptions
            .Where(x => x.ClientProfileId == context.ClientProfileId)
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync();

        if (latestSubscription is null)
        {
            TempData["SubscriptionNotice"] = "No client subscription was found to cancel.";
            return RedirectToAction(nameof(Index), new { returnUrl = NormalizeReturnUrl(returnUrl) });
        }

        var cancellation = await _billingOrchestrator.CancelClientSubscriptionAsync(
            new CancelClientSubscriptionCommand(
                latestSubscription.Id,
                true,
                BillingActorType.Client,
                User.GetCanonicalUserId(),
                $"client-cancel-{latestSubscription.Id:N}"),
            HttpContext.RequestAborted);

        TempData["SubscriptionNotice"] = cancellation.Success
            ? "Cancellation at period end has been scheduled."
            : cancellation.SanitizedSummary ?? "The subscription could not be updated right now.";

        return RedirectToAction(nameof(Index), new { returnUrl = NormalizeReturnUrl(returnUrl) });
    }

    private static string NormalizeReturnUrl(string? returnUrl)
    {
        return !string.IsNullOrWhiteSpace(returnUrl) &&
               returnUrl.StartsWith("/", StringComparison.Ordinal) &&
               Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
            ? returnUrl
            : "/profile";
    }

    private static string FormatDate(DateTime? value)
    {
        return value.HasValue
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc).ToLocalTime().ToString("MMMM d, yyyy")
            : "Not scheduled";
    }

    private static string BuildCurrentPeriodDisplay(ClientSubscription? subscription)
    {
        if (subscription?.CurrentPeriodStartUtc is null || subscription.CurrentPeriodEndUtc is null)
            return "Not available";

        return $"{FormatDate(subscription.CurrentPeriodStartUtc)} - {FormatDate(subscription.CurrentPeriodEndUtc)}";
    }

    private static string BuildRepairInstructions(ClientSubscription? subscription, ClientEntitlementStatus entitlementStatus, string? entitlementReasonCode)
    {
        if (subscription is null &&
            entitlementStatus is ClientEntitlementStatus.Active or ClientEntitlementStatus.GracePeriod &&
            string.Equals(entitlementReasonCode, "LEGACY_PROFILE_PRE_SUBSCRIPTION_CUTOFF", StringComparison.OrdinalIgnoreCase))
        {
            return "This client profile existed before subscriptions became required, so access remains available without adding billing.";
        }

        if (subscription is null)
            return "Use your activation link or contact your agent to begin service.";

        if (entitlementStatus is ClientEntitlementStatus.Active or ClientEntitlementStatus.GracePeriod)
            return "Your subscription is in good standing. If you need changes, contact your agent or schedule cancellation at period end.";

        if (subscription.PaymentStanding is ClientSubscriptionPaymentStanding.PastDue or ClientSubscriptionPaymentStanding.Failed or ClientSubscriptionPaymentStanding.RequiresAction)
            return "Billing needs attention. Contact your agent so they can help repair the payment method and restore access.";

        return "If you need billing help, contact your agent before the current period ends.";
    }
}
