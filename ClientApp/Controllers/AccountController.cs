using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ClientApp.Infrastructure;
using ClientApp.Models;
using ClientApp.Services;
using Shared.Diagnostics;

namespace ClientApp.Controllers;

[AllowAnonymous]
public class AccountController : Controller
{
    private readonly ClientIdentityAccessService _identityAccessService;
    private readonly ClientAppReturnUrlNormalizer _returnUrlNormalizer;

    public AccountController(
        ClientIdentityAccessService identityAccessService,
        ClientAppReturnUrlNormalizer returnUrlNormalizer)
    {
        _identityAccessService = identityAccessService;
        _returnUrlNormalizer = returnUrlNormalizer;
    }

    private async Task<IActionResult> StartChallengeAsync(string target)
    {
        OidcTransientCookieCleanup.Clear(HttpContext);
        Response.Cookies.Delete("impClientProfileId");
        Response.Cookies.Delete("selfClientProfileId");
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        var props = new AuthenticationProperties
        {
            RedirectUri = target
        };
        props.Items["prompt"] = "select_account";
        return Challenge(props, OpenIdConnectDefaults.AuthenticationScheme);
    }

    [HttpGet("/Account/AzureLogin")]
    public async Task<IActionResult> AzureLogin(string returnUrl = "/")
    {
        var target = _returnUrlNormalizer.Normalize(returnUrl);
        if (ClientIdentityAccessService.IsSupportReturnUrl(target))
        {
            return await StartChallengeAsync(target);
        }

        var challenge = await _identityAccessService.ValidateAzureChallengeAsync(
            HttpContext,
            target,
            HttpContext.RequestAborted);
        if (challenge.Success)
            return await StartChallengeAsync(challenge.ReturnUrl);

        _identityAccessService.ClearChallengeContinuationCookie(Response);

        return RedirectToAction(nameof(ActivationRequired), new
        {
            returnUrl = target,
            message = challenge.SanitizedMessage ?? "Use your activation link or the client sign-in form before continuing to Microsoft sign-in."
        });
    }

    [HttpGet]
    public async Task<IActionResult> Login(string returnUrl = "/", string? message = null)
    {
        var target = _returnUrlNormalizer.Normalize(returnUrl);

        if (User.Identity?.IsAuthenticated == true)
        {
            var session = await _identityAccessService.ValidateAuthenticatedClientSessionAsync(
                User,
                target,
                HttpContext.RequestAborted);
            if (session.Success)
                return LocalRedirect(session.ReturnUrl);

            _identityAccessService.ClearChallengeContinuationCookie(Response);
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            return View(new ClientLoginViewModel
            {
                ReturnUrl = target,
                Message = session.SanitizedMessage ?? "Sign in with the email connected to your active client subscription."
            });
        }

        if (ClientIdentityAccessService.IsSupportReturnUrl(target))
            return RedirectToAction(nameof(AzureLogin), new { returnUrl = target });

        return View(new ClientLoginViewModel
        {
            ReturnUrl = target,
            Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim()
        });
    }

    [HttpPost]
    [EnableRateLimiting("clientapp-login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LoginSubmit(ClientLoginViewModel model)
    {
        model.ReturnUrl = _returnUrlNormalizer.Normalize(model.ReturnUrl);
        _identityAccessService.ClearChallengeContinuationCookie(Response);

        if (ClientIdentityAccessService.IsSupportReturnUrl(model.ReturnUrl))
            return await AzureLogin(model.ReturnUrl);

        if (!ModelState.IsValid)
            return View(nameof(Login), model);

        var signInPreparation = await _identityAccessService.PrepareClientSignInAsync(model.Email, model.ReturnUrl, HttpContext.RequestAborted);
        if (!signInPreparation.Success || string.IsNullOrWhiteSpace(signInPreparation.ProtectedState) || !signInPreparation.ExpiresUtc.HasValue)
        {
            return View(nameof(ActivationRequired), new ActivationRequiredViewModel
            {
                ReturnUrl = model.ReturnUrl,
                Message = signInPreparation.SanitizedMessage ?? "This client account is not ready for sign-in yet."
            });
        }

        _identityAccessService.StoreChallengeContinuationCookie(Response, signInPreparation.ProtectedState, signInPreparation.ExpiresUtc.Value);
        return await StartChallengeAsync(signInPreparation.ReturnUrl);
    }

    // ✅ This is what your middleware redirects to on Forbid()
    [HttpGet]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult AccessDenied(string? returnUrl = null)
    {
        var diagnostics = AppFailureDiagnosticsBuilder.BuildForAccessDenied(
            HttpContext,
            "ClientApp",
            returnUrl,
            summary: "The current client session does not have permission to open this route.");

        var model = new AgentPortal.Models.ErrorViewModel
        {
            RequestId = diagnostics.RequestId,
            Diagnostics = diagnostics
        };

        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.Headers["X-Legend-Failure-Kind"] = diagnostics.FailureKind;
        return View("~/Views/Shared/Error.cshtml", model);
    }

    [HttpGet]
    public IActionResult ActivationRequired(string? returnUrl = null, string? message = null)
    {
        return View(new ActivationRequiredViewModel
        {
            ReturnUrl = _returnUrlNormalizer.Normalize(returnUrl),
            Message = string.IsNullOrWhiteSpace(message)
                ? "Use the activation link from your agent to finish access setup before signing in."
                : message.Trim()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("impClientProfileId");
        Response.Cookies.Delete("selfClientProfileId");
        _identityAccessService.ClearChallengeContinuationCookie(Response);

        return SignOut(
            new AuthenticationProperties
            {
                RedirectUri = Url.Action(nameof(LoggedOut), "Account")
            },
            CookieAuthenticationDefaults.AuthenticationScheme,
            OpenIdConnectDefaults.AuthenticationScheme);
    }

    [HttpGet]
    public IActionResult LoggedOut()
    {
        return View();
    }
}
