using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Infrastructure.Data;
using Infrastructure.Identity;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using AgentPortal.Models;
using AgentPortal.Security;
using AgentPortal.Services;
using AgentPortal.Services.Tracking;
using Domain.Accounts;
using Domain.Entities;
using Domain.Messaging;
using Shared.Auth;

public class AccountController : Controller
{
    private readonly MasterAppDbContext _db;
    private readonly AgentProfileAccessResolver _profileAccessResolver;
    private readonly IAccountLifecycleService _accountLifecycle;
    private readonly IAgentTrackingService _tracking;
    private readonly WebsiteEditorTicketProtector _websiteEditorTickets;
    private readonly AgentMarketingProfileService _marketing;

    public AccountController(
        MasterAppDbContext db,
        AgentProfileAccessResolver profileAccessResolver,
        IAccountLifecycleService accountLifecycle,
        IAgentTrackingService tracking,
        WebsiteEditorTicketProtector websiteEditorTickets,
        AgentMarketingProfileService marketing)
    {
        _db = db;
        _profileAccessResolver = profileAccessResolver;
        _accountLifecycle = accountLifecycle;
        _tracking = tracking;
        _websiteEditorTickets = websiteEditorTickets;
        _marketing = marketing;
    }

    private async Task PopulateProtectWebsiteAsync(string userId)
    {
        var trackingProfile = await _tracking.GetByUserIdAsync(userId, HttpContext.RequestAborted);
        if (trackingProfile is null || !string.Equals(trackingProfile.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            ViewBag.ProtectWebsiteAvailable = false;
            ViewBag.ProtectWebsiteUrl = null;
            return;
        }

        var urls = await _tracking.GetPersonalUrlsAsync(trackingProfile, HttpContext.RequestAborted);
        ViewBag.ProtectWebsiteAvailable = true;
        ViewBag.ProtectWebsiteUrl = urls.PrimaryUrl;
        ViewBag.ProtectWebsiteSlug = trackingProfile.Slug;
    }

    private static string? NormalizeEmail(string? email)
    {
        var value = email?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }


    // GET: /Account/Login
    [HttpGet]
    public IActionResult Login(string returnUrl = "/")
    {
        // 🔹 Store returnUrl to redirect after login
        ViewData["ReturnUrl"] = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl;
        return View(); // This will render Login.cshtml
    }

    // POST: /Account/Login
    [HttpPost]
    [ValidateAntiForgeryToken]
    [AllowAnonymous]
    public IActionResult LoginSubmit(string returnUrl = "/")
    {
        // 🔹 Trigger Azure AD login
        OidcTransientCookieCleanup.Clear(HttpContext);
        return Challenge(
            new AuthenticationProperties
            {
                RedirectUri = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl,
                IsPersistent = true, // persist session
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
            },
            OpenIdConnectDefaults.AuthenticationScheme
        );
    }

    // POST: /Account/Logout
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize]
    public IActionResult Logout()
    {
        return SignOut(
            new AuthenticationProperties
            {
                RedirectUri = Url.Action("LoggedOut", "Account"),
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(-1)
            },
            OpenIdConnectDefaults.AuthenticationScheme,
            CookieAuthenticationDefaults.AuthenticationScheme
        );
    }

    // GET: /Account/LoggedOut
    [HttpGet]
    [AllowAnonymous]
    public IActionResult LoggedOut()
    {
        return View(); // renders LoggedOut.cshtml
    }

    // ============================================
    // Manage Profile (Agent-facing)
    // ============================================
    [HttpGet]
    [Authorize]
    public async Task<IActionResult> ManageProfile()
    {
        var userId = User.GetCanonicalUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        var upn = AgentProfileAccessResolver.GetDirectoryEmail(User) ?? "";
        var normalizedUpn = NormalizeEmail(upn);

        var profile = await _profileAccessResolver.ResolveCurrentAsync(
            User,
            requireActive: false,
            HttpContext.RequestAborted);
        if (profile == null)
        {
            profile = new Domain.Entities.AgentProfile
            {
                AgentUserId = userId,
                AgentUpn = upn ?? "",
                NormalizedEmail = normalizedUpn,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            _db.AgentProfiles.Add(profile);
            _db.SaveChanges();
        }
        else
        {
            // GET should be read-only.
            // Do not mutate AgentUpn or NormalizedEmail during page load.
            // Identity reconciliation is handled by AgentProfileAccessResolver.
        }

        var firstName =
            User.FindFirst(ClaimTypes.GivenName)?.Value
            ?? User.FindFirst("given_name")?.Value;
        var lastName =
            User.FindFirst(ClaimTypes.Surname)?.Value
            ?? User.FindFirst("family_name")?.Value;
        var displayName = string.Join(" ", new[] { firstName, lastName }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName =
                User.FindFirst("name")?.Value
                ?? User.FindFirst(ClaimTypes.Name)?.Value
                ?? User.Identity?.Name
                ?? "Agent";
        }

        var vm = new ManageAgentProfileViewModel
        {
            FullName = profile.FullName ?? displayName,
            Email = profile.AgentUpn ?? upn ?? "",
            Title = profile.Title,
            Phone = profile.Phone,
            ShortBio = profile.ShortBio,
            Npn = profile.Npn,
            PreferModalOnMobile = false
        };

        ViewBag.AccountLifecycle = await _accountLifecycle.GetAsync(
            new AccountLifecycleSubject(userId, MessagingParticipantTypes.Agent, profile.Id),
            HttpContext.RequestAborted);
        await PopulateProtectWebsiteAsync(userId);

        return View(vm);
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ManageProfile(ManageAgentProfileViewModel vm)
    {
        var userId = User.GetCanonicalUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        var directoryUpn = AgentProfileAccessResolver.GetDirectoryEmail(User) ?? vm.Email ?? "";
        var normalizedUpn = NormalizeEmail(directoryUpn);

        var existingProfile = await _profileAccessResolver.ResolveCurrentAsync(
            User,
            requireActive: false,
            HttpContext.RequestAborted);

        if (!ModelState.IsValid)
        {
            await PopulateProtectWebsiteAsync(userId);
            return View(vm);
        }

        var profile = existingProfile;
        if (profile == null)
        {
            profile = new Domain.Entities.AgentProfile
            {
                AgentUserId = userId,
                AgentUpn = directoryUpn,
                NormalizedEmail = normalizedUpn,
                CreatedUtc = DateTime.UtcNow
            };
            _db.AgentProfiles.Add(profile);
        }

        profile.FullName = vm.FullName?.Trim();
        profile.Title = string.IsNullOrWhiteSpace(vm.Title) ? null : vm.Title.Trim();
        profile.Npn = vm.Npn?.Trim();
        profile.Phone = vm.Phone?.Trim();
        profile.ShortBio = string.IsNullOrWhiteSpace(vm.ShortBio) ? null : vm.ShortBio.Trim();
        // Marketing destination and booking configuration are intentionally not
        // mutated by the profile form. Website Analytics > Marketing Setup owns
        // that configuration surface while these existing fields remain the
        // canonical runtime storage consumed by booking/public quote flows.
        profile.PreferModalOnMobile = false;
        // Email (UPN) remains authoritative from directory; do not allow editing here.
        // Only write it when Azure AD gives us a clean value.
        if (!string.IsNullOrWhiteSpace(directoryUpn) && normalizedUpn != null)
        {
            profile.AgentUpn = directoryUpn;
            profile.NormalizedEmail = normalizedUpn;
        }

        profile.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        TempData["ProfileSaved"] = "Agent profile updated.";
        return RedirectToAction(nameof(ManageProfile));
    }

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> EditProtectWebsite()
    {
        var userId = User.GetCanonicalUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        var trackingProfile = await _tracking.GetByUserIdAsync(userId, HttpContext.RequestAborted);
        if (trackingProfile is null ||
            !string.Equals(trackingProfile.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            TempData["ProfileWebsiteError"] = "Your Protect website scope is not available yet.";
            return RedirectToAction(nameof(ManageProfile));
        }

        var urls = await _tracking.GetPersonalUrlsAsync(trackingProfile, HttpContext.RequestAborted);
        var ticket = _websiteEditorTickets.Protect(new WebsiteEditorTicket(
            WebsiteEditorSiteKeys.Protect,
            trackingProfile.AgentUserId.Trim().ToLowerInvariant(),
            trackingProfile.Slug,
            FounderGuard.IsFounder(User),
            DateTime.UtcNow.AddMinutes(45),
            ActorUserId: userId,
            ActorEmail: User.FindFirstValue(ClaimTypes.Email)));

        var separator = urls.PrimaryUrl.Contains('?') ? "&" : "?";
        return Redirect($"{urls.PrimaryUrl}{separator}legendEdit={Uri.EscapeDataString(ticket)}");
    }

    [HttpGet]
    [Authorize]
    public IActionResult EditLegendWebsite()
    {
        if (!FounderGuard.IsFounder(User)) return Forbid();
        var ticket = _websiteEditorTickets.Protect(new WebsiteEditorTicket(
            WebsiteEditorSiteKeys.Legend, WebsiteEditorSiteKeys.GlobalOwnerKey, null,
            true, DateTime.UtcNow.AddMinutes(45), ActorUserId: User.GetCanonicalUserId(),
            ActorEmail: User.FindFirstValue(ClaimTypes.Email)));
        var configuration = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var baseUrl = (configuration["LegendWebsiteBaseUrl"] ?? "https://www.mylegnd.com").TrimEnd('/');
        return Redirect($"{baseUrl}/?legendEdit={Uri.EscapeDataString(ticket)}");
    }

    [HttpGet]
    [Authorize]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> WebsiteSession(string site = "protect")
    {
        var userId = User.GetCanonicalUserId();
        if (string.IsNullOrWhiteSpace(userId)) return Challenge();
        WebsiteEditorTicket scope;
        if (site == WebsiteEditorSiteKeys.Legend)
        {
            if (!FounderGuard.IsFounder(User)) return Forbid();
            scope = new WebsiteEditorTicket(site, WebsiteEditorSiteKeys.GlobalOwnerKey, null,
                true, DateTime.UtcNow.AddMinutes(45), ActorUserId: userId,
                ActorEmail: User.FindFirstValue(ClaimTypes.Email));
        }
        else if (site == WebsiteEditorSiteKeys.Protect)
        {
            var profile = await _tracking.GetByUserIdAsync(userId, HttpContext.RequestAborted);
            if (profile is null || !string.Equals(profile.Status, "active", StringComparison.OrdinalIgnoreCase)) return Forbid();
            scope = new WebsiteEditorTicket(site, profile.AgentUserId.Trim().ToLowerInvariant(), profile.Slug,
                FounderGuard.IsFounder(User), DateTime.UtcNow.AddMinutes(45), ActorUserId: userId,
                ActorEmail: User.FindFirstValue(ClaimTypes.Email));
        }
        else return BadRequest();
        var configuration = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        return Json(new { ticket = _websiteEditorTickets.Protect(scope),
            apiBase = (configuration["LandingRoutes:BaseUrl"] ?? "https://protect.mylegnd.com").TrimEnd('/') });
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AccountAccess(string operation, string? confirmation)
    {
        var userId = User.GetCanonicalUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        var profile = await _profileAccessResolver.ResolveCurrentAsync(
            User,
            requireActive: false,
            HttpContext.RequestAborted);
        if (profile is null)
            return Forbid();

        var subject = new AccountLifecycleSubject(
            userId,
            MessagingParticipantTypes.Agent,
            profile.Id);
        AccountLifecycleOperationResult result;
        if (string.Equals(operation, "pause", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(confirmation?.Trim(), "PAUSE", StringComparison.Ordinal))
            {
                TempData["AccountLifecycleError"] = "Type PAUSE to confirm that you want to pause your account.";
                return RedirectToAction(nameof(ManageProfile));
            }

            result = await _accountLifecycle.PauseAsync(subject, HttpContext.TraceIdentifier, HttpContext.RequestAborted);
        }
        else if (string.Equals(operation, "resume", StringComparison.OrdinalIgnoreCase))
        {
            result = await _accountLifecycle.ResumeAsync(subject, HttpContext.TraceIdentifier, HttpContext.RequestAborted);
        }
        else
        {
            return BadRequest();
        }

        TempData[result.Succeeded ? "AccountLifecycleNotice" : "AccountLifecycleError"] = result.Message;
        return RedirectToAction(nameof(ManageProfile));
    }

}
