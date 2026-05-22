using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using AgentPortal.Models;
using Domain.Entities;

public class AccountController : Controller
{
    private readonly MasterAppDbContext _db;

    public AccountController(MasterAppDbContext db)
    {
        _db = db;
    }

    private static string? GetCurrentUserId(ClaimsPrincipal user)
    {
        var value = user.FindFirst("oid")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.Identity?.Name;
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? GetCurrentUserEmail(ClaimsPrincipal user)
    {
        var candidates = new[]
        {
            user.FindFirst("preferred_username")?.Value,
            user.FindFirst(ClaimTypes.Email)?.Value,
            user.FindFirst("email")?.Value,
            user.FindFirst("upn")?.Value,
            user.FindFirst(ClaimTypes.Upn)?.Value,
            user.Identity?.Name
        };

        foreach (var candidate in candidates)
        {
            var value = candidate?.Trim();
            if (!string.IsNullOrWhiteSpace(value) && value.Contains("@"))
                return value;
        }

        return null;
    }

    private static string? NormalizeEmail(string? email)
    {
        var value = email?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private AgentProfile? FindAgentProfile(string userId, string? normalizedEmail)
    {
        // Resolve by email first because NormalizedEmail is unique.
        // Do NOT mutate AgentUserId inside a lookup method. If another profile already owns
        // this userId, changing it here can cause a duplicate-key crash on SaveChanges().
        if (!string.IsNullOrWhiteSpace(normalizedEmail))
        {
            var byEmail = _db.AgentProfiles.FirstOrDefault(x =>
                x.NormalizedEmail == normalizedEmail ||
                (x.NormalizedEmail == null && x.AgentUpn != null && x.AgentUpn.ToLower() == normalizedEmail));

            if (byEmail != null)
                return byEmail;
        }

        return _db.AgentProfiles.FirstOrDefault(x => x.AgentUserId == userId);
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
    public IActionResult ManageProfile()
    {
        var userId = GetCurrentUserId(User);
        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        var upn = GetCurrentUserEmail(User) ?? "";
        var normalizedUpn = NormalizeEmail(upn);

        var profile = FindAgentProfile(userId, normalizedUpn);
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
            // Identity reconciliation is handled by FindAgentProfile() and POST save.
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
            PreferModalOnMobile = profile.PreferModalOnMobile ?? true,
            HasSecureMetaCapiAccessToken = !string.IsNullOrWhiteSpace(profile.MetaCapiAccessToken)
        };

        return View(vm);
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public IActionResult ManageProfile(ManageAgentProfileViewModel vm)
    {
        var userId = GetCurrentUserId(User);
        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        var directoryUpn = GetCurrentUserEmail(User) ?? vm.Email ?? "";
        var normalizedUpn = NormalizeEmail(directoryUpn);

        vm.MetaPixelId = string.IsNullOrWhiteSpace(vm.MetaPixelId) ? null : vm.MetaPixelId.Trim();
        vm.MicrosoftBookingsEmbedUrl = string.IsNullOrWhiteSpace(vm.MicrosoftBookingsEmbedUrl) ? null : vm.MicrosoftBookingsEmbedUrl.Trim();
        vm.FallbackBookingUrl = string.IsNullOrWhiteSpace(vm.FallbackBookingUrl) ? null : vm.FallbackBookingUrl.Trim();
        vm.BookingPageIdOrMailbox = string.IsNullOrWhiteSpace(vm.BookingPageIdOrMailbox) ? null : vm.BookingPageIdOrMailbox.Trim();
        vm.CalendarEmail = string.IsNullOrWhiteSpace(vm.CalendarEmail) ? null : vm.CalendarEmail.Trim();
        var existingProfile = FindAgentProfile(userId, normalizedUpn);
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
        profile.PreferModalOnMobile = vm.BookingEnabled || hasBookingFieldValues ? vm.PreferModalOnMobile : null;
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
}
