using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClientApp.Controllers;

[AllowAnonymous]
public class AccountController : Controller
{
    // Optional landing page (you can keep this view if you want)
    [HttpGet]
    public IActionResult Login(string returnUrl = "/")
    {
        ViewData["ReturnUrl"] = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult LoginSubmit(string returnUrl = "/")
    {
        var target = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl;

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
