using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Infrastructure.Data;
using Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using AgentPortal.Models;
using AgentPortal.Security;
using AgentPortal.Services;
using Domain.Accounts;
using Domain.Entities;
using Domain.Messaging;
using Shared.Auth;

public class AccountController : Controller
{
    private readonly MasterAppDbContext _db;
    private readonly AgentProfileAccessResolver _profileAccessResolver;
    private readonly IAccountLifecycleService _accountLifecycle;

    public AccountController(
        MasterAppDbContext db,
        AgentProfileAccessResolver profileAccessResolver,
        IAccountLifecycleService accountLifecycle)
    {
        _db = db;
        _profileAccessResolver = profileAccessResolver;
        _accountLifecycle = accountLifecycle;
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
            MetaPixelId = profile.MetaPixelId,
            BookingEnabled = profile.BookingEnabled ?? false,
            MicrosoftBookingsEmbedUrl = profile.MicrosoftBookingsEmbedUrl,
            FallbackBookingUrl = profile.FallbackBookingUrl,
            BookingPageIdOrMailbox = profile.BookingPageIdOrMailbox,
            CalendarEmail = profile.CalendarEmail,
            PreferModalOnMobile = false,
            HasSecureMetaCapiAccessToken = !string.IsNullOrWhiteSpace(profile.MetaCapiAccessToken)
        };

        ViewBag.AccountLifecycle = await _accountLifecycle.GetAsync(
            new AccountLifecycleSubject(userId, MessagingParticipantTypes.Agent, profile.Id),
            HttpContext.RequestAborted);

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

        vm.MetaPixelId = string.IsNullOrWhiteSpace(vm.MetaPixelId) ? null : vm.MetaPixelId.Trim();
        vm.MicrosoftBookingsEmbedUrl = string.IsNullOrWhiteSpace(vm.MicrosoftBookingsEmbedUrl) ? null : vm.MicrosoftBookingsEmbedUrl.Trim();
        vm.FallbackBookingUrl = string.IsNullOrWhiteSpace(vm.FallbackBookingUrl) ? null : vm.FallbackBookingUrl.Trim();
        vm.BookingPageIdOrMailbox = string.IsNullOrWhiteSpace(vm.BookingPageIdOrMailbox) ? null : vm.BookingPageIdOrMailbox.Trim();
        vm.CalendarEmail = string.IsNullOrWhiteSpace(vm.CalendarEmail) ? null : vm.CalendarEmail.Trim();
        var existingProfile = await _profileAccessResolver.ResolveCurrentAsync(
            User,
            requireActive: false,
            HttpContext.RequestAborted);
        vm.HasSecureMetaCapiAccessToken = !string.IsNullOrWhiteSpace(existingProfile?.MetaCapiAccessToken);

        if (!ModelState.IsValid)
            return View(vm);

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
        profile.MetaPixelId = string.IsNullOrWhiteSpace(vm.MetaPixelId) ? null : vm.MetaPixelId.Trim();
        var hasBookingFieldValues =
            !string.IsNullOrWhiteSpace(vm.MicrosoftBookingsEmbedUrl) ||
            !string.IsNullOrWhiteSpace(vm.FallbackBookingUrl) ||
            !string.IsNullOrWhiteSpace(vm.BookingPageIdOrMailbox) ||
            !string.IsNullOrWhiteSpace(vm.CalendarEmail);
        profile.BookingEnabled = vm.BookingEnabled ? true : hasBookingFieldValues ? false : null;
        profile.MicrosoftBookingsEmbedUrl = vm.MicrosoftBookingsEmbedUrl;
        profile.FallbackBookingUrl = vm.FallbackBookingUrl;
        profile.BookingPageIdOrMailbox = vm.BookingPageIdOrMailbox;
        profile.CalendarEmail = vm.CalendarEmail;
        profile.PreferModalOnMobile = false;
        // Email (UPN) remains authoritative from directory; do not allow editing here.
        // Only write it when Azure AD gives us a clean value.
        if (!string.IsNullOrWhiteSpace(directoryUpn) && normalizedUpn != null)
        {
            profile.AgentUpn = directoryUpn;
            profile.NormalizedEmail = normalizedUpn;
        }

        profile.UpdatedUtc = DateTime.UtcNow;

        _db.SaveChanges();
        TempData["ProfileSaved"] = "Agent profile updated.";
        return RedirectToAction(nameof(ManageProfile));
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
