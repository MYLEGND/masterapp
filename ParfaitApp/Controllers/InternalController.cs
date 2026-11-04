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

        return MicrosoftAccountChallenge(returnUrl);
    }

    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SwitchAccount(string? returnUrl = null)
    {
        // Clear only this application's session. Microsoft owns account/email entry;
        // the existing token validation still decides Founder and Team access.
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return MicrosoftAccountChallenge(returnUrl);
    }

    private ChallengeResult MicrosoftAccountChallenge(string? returnUrl)
    {
        var properties = new AuthenticationProperties
        {
            RedirectUri = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
                ? returnUrl : "/internal"
        };
        properties.Parameters["prompt"] = "select_account";
        return Challenge(properties, OpenIdConnectDefaults.AuthenticationScheme);
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
