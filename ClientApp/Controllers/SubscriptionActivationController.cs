using ClientApp.Models;
using ClientApp.Services;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ClientApp.Controllers;

[AllowAnonymous]
[EnableRateLimiting("clientapp-public")]
public sealed class SubscriptionActivationController : Controller
{
    private readonly SubscriptionActivationService _activationService;
    private readonly ClientIdentityContinuationService _continuationService;
    private readonly ClientAppReturnUrlNormalizer _returnUrlNormalizer;
    private readonly IClientEntraLifecycleService _entraLifecycle;

    public SubscriptionActivationController(
        SubscriptionActivationService activationService,
        ClientIdentityContinuationService continuationService,
        ClientAppReturnUrlNormalizer returnUrlNormalizer,
        IClientEntraLifecycleService entraLifecycle)
    {
        _activationService = activationService;
        _continuationService = continuationService;
        _returnUrlNormalizer = returnUrlNormalizer;
        _entraLifecycle = entraLifecycle;
    }

    [HttpGet("/activate/{token}")]
    public async Task<IActionResult> Index(string token, string returnUrl = "/profile")
    {
        var context = await _activationService.GetContextAsync(token, HttpContext.RequestAborted);
        return RenderContext(token, returnUrl, context);
    }

    [HttpPost("/activate/{token}/payment-method")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PaymentMethod(string token, SubscriptionActivationPaymentInput input)
    {
        if (!ModelState.IsValid)
        {
            var invalidContext = await _activationService.GetContextAsync(token, HttpContext.RequestAborted);
            return View("Index", _activationService.BuildPageViewModel(invalidContext, token, input.ReturnUrl, "Complete the required activation fields before continuing."));
        }

        var activation = await _activationService.ActivateAsync(token, input, HttpContext.RequestAborted);
        if (!activation.Success &&
            activation.Context.Subscription?.Status == Domain.Billing.ClientSubscriptionStatus.Active)
        {
            return View("Unavailable", new SubscriptionActivationNoticeViewModel
            {
                Title = "Membership Active — Sign-In Setup Pending",
                Message = activation.SanitizedMessage
                    ?? "Your membership is active. Secure sign-in setup is still completing. Do not submit another payment.",
                ReturnUrl = _returnUrlNormalizer.Normalize(input.ReturnUrl)
            });
        }

        if (!activation.Success || string.IsNullOrWhiteSpace(activation.ProtectedContinuationState))
        {
            if (activation.Context.Availability == SubscriptionActivationAvailability.Ready)
            {
                return View("Index", _activationService.BuildPageViewModel(
                    activation.Context,
                    token,
                    input.ReturnUrl,
                    activation.SanitizedMessage ?? "The subscription could not be activated yet."));
            }

            return RenderContext(token, input.ReturnUrl, activation.Context, activation.SanitizedMessage);
        }

        if (activation.Context.Offer?.MonthlyAmountCents == 0)
        {
            return View("Confirmed", new SubscriptionActivationConfirmationViewModel
            {
                Token = token,
                ClientName = $"{activation.Context.Client?.FirstName} {activation.Context.Client?.LastName}".Trim(),
                ClientEmail = activation.Context.Invitation?.IntendedNormalizedEmail ?? activation.Context.Client?.Email ?? string.Empty,
                MonthlyAmountDisplay = "$0.00",
                ReturnUrl = _returnUrlNormalizer.Normalize(input.ReturnUrl),
                ProtectedContinuationState = activation.ProtectedContinuationState
            });
        }

        _continuationService.StoreCookie(
            Response,
            activation.ProtectedContinuationState,
            activation.ContinuationExpiresUtc ?? DateTime.UtcNow.AddMinutes(20));

        if (!string.IsNullOrWhiteSpace(activation.IdentityRedemptionUrl))
            return Redirect(activation.IdentityRedemptionUrl);

        return RedirectToAction("AzureLogin", "Account", new { returnUrl = _returnUrlNormalizer.Normalize(input.ReturnUrl) });
    }

    [HttpGet("/activate/{token}/status")]
    public async Task<IActionResult> Status(string token)
    {
        var context = await _activationService.GetContextAsync(token, HttpContext.RequestAborted);
        var isActivated = context.Availability == SubscriptionActivationAvailability.AlreadyActivated &&
            context.Subscription?.Status == Domain.Billing.ClientSubscriptionStatus.Active;
        return Json(new
        {
            ok = context.Availability == SubscriptionActivationAvailability.Ready || isActivated,
            activated = isActivated,
            state = context.Availability.ToString(),
            message = context.Message,
            subscriptionStatus = context.Subscription?.Status.ToString(),
            entitlementReady = context.Subscription is not null
        });
    }

    [HttpPost("/activate/{token}/continue")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Continue(string token, string protectedContinuationState, string returnUrl = "/profile")
    {
        var validation = await _continuationService.ValidateProtectedStateAsync(protectedContinuationState, HttpContext.RequestAborted);
        if (!validation.Success || validation.Continuation is null)
        {
            return View("Unavailable", new SubscriptionActivationNoticeViewModel
            {
                Title = "Continuation Unavailable",
                Message = validation.SanitizedMessage ?? "This activation continuation is no longer available. Use the member sign-in page instead.",
                ReturnUrl = _returnUrlNormalizer.Normalize(returnUrl)
            });
        }

        ClientEntraIdentitySynchronizationResult identity;
        try
        {
            identity = await _entraLifecycle.SynchronizeClientIdentityAsync(
                validation.Continuation.ClientProfileId,
                HttpContext.RequestAborted);
        }
        catch
        {
            return View("Unavailable", new SubscriptionActivationNoticeViewModel
            {
                Title = "Membership Active — Sign-In Setup Pending",
                Message = "Your membership is active. Microsoft sign-in setup is still completing. Do not submit another payment. Try member sign-in again shortly.",
                ReturnUrl = _returnUrlNormalizer.Normalize(returnUrl)
            });
        }

        _continuationService.StoreCookie(Response, protectedContinuationState, validation.Continuation.ExpiresUtc);

        if (identity.RequiresRedemption && !string.IsNullOrWhiteSpace(identity.RedemptionUrl))
            return Redirect(identity.RedemptionUrl);

        return RedirectToAction("AzureLogin", "Account", new { returnUrl = _returnUrlNormalizer.Normalize(returnUrl) });
    }

    private IActionResult RenderContext(string token, string returnUrl, SubscriptionActivationContextResult context, string? messageOverride = null)
    {
        return context.Availability switch
        {
            SubscriptionActivationAvailability.Ready =>
                View("Index", _activationService.BuildPageViewModel(context, token, returnUrl, messageOverride)),
            SubscriptionActivationAvailability.Expired =>
                View("Expired", new SubscriptionActivationNoticeViewModel
                {
                    Title = "Activation Link Expired",
                    Message = messageOverride ?? context.Message ?? "This activation link expired. Contact your LEGEND guide for a fresh invitation.",
                    ReturnUrl = _returnUrlNormalizer.Normalize(returnUrl)
                }),
            _ =>
                View("Unavailable", new SubscriptionActivationNoticeViewModel
                {
                    Title = context.Availability == SubscriptionActivationAvailability.AlreadyActivated
                        ? "Already Activated"
                        : "Activation Unavailable",
                    Message = messageOverride ?? context.Message ?? "This activation flow is not available.",
                    ReturnUrl = _returnUrlNormalizer.Normalize(returnUrl)
                })
        };
    }

}
