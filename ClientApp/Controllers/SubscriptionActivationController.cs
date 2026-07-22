using ClientApp.Models;
using ClientApp.Services;
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

    public SubscriptionActivationController(
        SubscriptionActivationService activationService,
        ClientIdentityContinuationService continuationService,
        ClientAppReturnUrlNormalizer returnUrlNormalizer)
    {
        _activationService = activationService;
        _continuationService = continuationService;
        _returnUrlNormalizer = returnUrlNormalizer;
    }

    [HttpGet("/activate/{token}")]
    public async Task<IActionResult> Index(string token, string returnUrl = "/profile")
    {
        var context = await _activationService.GetContextAsync(token, null, HttpContext.RequestAborted);
        return RenderContext(token, returnUrl, context);
    }

    [HttpPost("/activate/{token}/prepare")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Prepare(string token, SubscriptionActivationPrepareRequest request)
    {
        var context = await _activationService.GetContextAsync(token, request.BillingAnchorDay, HttpContext.RequestAborted);
        return Json(_activationService.BuildPrepareResponse(context));
    }

    [HttpPost("/activate/{token}/payment-method")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PaymentMethod(string token, SubscriptionActivationPaymentInput input)
    {
        if (!ModelState.IsValid)
        {
            var invalidContext = await _activationService.GetContextAsync(token, input.BillingAnchorDay, HttpContext.RequestAborted);
            return View("Index", _activationService.BuildPageViewModel(invalidContext, token, input.ReturnUrl, "Complete the required consent and payment fields before continuing."));
        }

        var activation = await _activationService.ActivateAsync(token, input, HttpContext.RequestAborted);
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

        _continuationService.StoreCookie(
            Response,
            activation.ProtectedContinuationState,
            activation.ContinuationExpiresUtc ?? DateTime.UtcNow.AddMinutes(20));

        return RedirectToAction("AzureLogin", "Account", new { returnUrl = _returnUrlNormalizer.Normalize(input.ReturnUrl) });
    }

    [HttpGet("/activate/{token}/status")]
    public async Task<IActionResult> Status(string token)
    {
        var context = await _activationService.GetContextAsync(token, null, HttpContext.RequestAborted);
        return Json(new
        {
            ok = context.Availability == SubscriptionActivationAvailability.Ready,
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
                Message = validation.SanitizedMessage ?? "This activation continuation is no longer available. Use the client sign-in page instead.",
                ReturnUrl = _returnUrlNormalizer.Normalize(returnUrl)
            });
        }

        _continuationService.StoreCookie(Response, protectedContinuationState, validation.Continuation.ExpiresUtc);
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
                    Message = messageOverride ?? context.Message ?? "This activation link expired. Contact your agent for a fresh invitation.",
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
