using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClientApp.Controllers;

[AllowAnonymous]
public class AccountController : Controller
{
    private string NormalizeReturnUrl(string? returnUrl)
    {
        var target = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl;
        return Url.IsLocalUrl(target) ? target : "/";
    }

    [HttpGet]
    public async Task<IActionResult> AzureLogin(string returnUrl = "/")
    {
        var target = NormalizeReturnUrl(returnUrl);

        // Start from a clean local session before sending the browser to Microsoft login.
        Response.Cookies.Delete("impClientProfileId");
        Response.Cookies.Delete("selfClientProfileId");
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        var props = new AuthenticationProperties
        {
            RedirectUri = target
        };

        // Force the Microsoft account picker instead of silently reusing a stale app session.
        props.Items["prompt"] = "select_account";

        return Challenge(props, OpenIdConnectDefaults.AuthenticationScheme);
    }

    // Optional landing page (you can keep this view if you want)
    [HttpGet]
    public IActionResult Login(string returnUrl = "/")
    {
        var target = NormalizeReturnUrl(returnUrl);

        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(target);
        }

        return Challenge(
            new AuthenticationProperties { RedirectUri = target },
            OpenIdConnectDefaults.AuthenticationScheme
        );
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult LoginSubmit(string returnUrl = "/")
    {
        var target = NormalizeReturnUrl(returnUrl);

        return Challenge(
            new AuthenticationProperties { RedirectUri = target },
            OpenIdConnectDefaults.AuthenticationScheme
        );
    }

    // ✅ This is what your middleware redirects to on Forbid()
    [HttpGet]
    public IActionResult AccessDenied(string? returnUrl = null)
    {
        ViewBag.ReturnUrl = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl;
        return View();
    }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Logout()
        {
            // Clear any impersonation / pinned client context
            Response.Cookies.Delete("impClientProfileId");
            Response.Cookies.Delete("selfClientProfileId");

            return SignOut(
                new AuthenticationProperties
                {
                    RedirectUri = Url.Action(nameof(LoggedOut), "Account")
                },
            CookieAuthenticationDefaults.AuthenticationScheme,
            OpenIdConnectDefaults.AuthenticationScheme
        );
    }

    [HttpGet]
    public IActionResult LoggedOut()
    {
        return View();
    }
}
