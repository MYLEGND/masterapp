using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[Route("internal")]
public sealed class InternalController : Controller
{
    private readonly IParfaitTeamAccessService _teamAccess;

    public InternalController(IParfaitTeamAccessService teamAccess)
    {
        _teamAccess = teamAccess;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated != true)
            return RedirectToAction(nameof(Login));

        var firstPage = await _teamAccess.GetFirstVisiblePageAsync(User, ct);
        if (firstPage is not null)
            return Redirect(firstPage.Route);

        return RedirectToAction(nameof(Denied));
    }

    [HttpGet("login")]
    public async Task<IActionResult> Login(string? returnUrl = null, CancellationToken ct = default)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);

            var firstPage = await _teamAccess.GetFirstVisiblePageAsync(User, ct);
            return Redirect(firstPage?.Route ?? "/internal/denied");
        }

        return Challenge(
            new AuthenticationProperties
            {
                RedirectUri = string.IsNullOrWhiteSpace(returnUrl) || !Url.IsLocalUrl(returnUrl)
                    ? "/internal"
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
        return View();
    }
}
