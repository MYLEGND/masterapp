using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;

namespace ParfaitApp.Controllers;

[Route("internal")]
public sealed class InternalController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        return RedirectToAction("Index", "Dashboard");
    }

    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = null)
    {
        return Challenge(
            new AuthenticationProperties
            {
                RedirectUri = string.IsNullOrWhiteSpace(returnUrl) || !Url.IsLocalUrl(returnUrl)
                    ? "/internal/dashboard"
                    : returnUrl
            },
            OpenIdConnectDefaults.AuthenticationScheme);
    }

    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public IActionResult Logout()
    {
        return SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            CookieAuthenticationDefaults.AuthenticationScheme,
            OpenIdConnectDefaults.AuthenticationScheme);
    }

    [HttpGet("denied")]
    public IActionResult Denied()
    {
        return Content("Access denied. Use an authorized @mylegnd.com account.");
    }
}
