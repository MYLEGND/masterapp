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
        var userId = User.FindFirst("oid")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        var upn = User.FindFirst(ClaimTypes.Email)?.Value
            ?? User.Identity?.Name
            ?? "";

        var profile = _db.AgentProfiles.FirstOrDefault(x => x.AgentUserId == userId);
        if (profile == null)
        {
            profile = new Domain.Entities.AgentProfile
            {
                AgentUserId = userId,
                AgentUpn = upn ?? "",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            _db.AgentProfiles.Add(profile);
            _db.SaveChanges();
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
            Npn = profile.Npn
        };

        return View(vm);
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public IActionResult ManageProfile(ManageAgentProfileViewModel vm)
    {
        var userId = User.FindFirst("oid")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        if (!ModelState.IsValid)
            return View(vm);

        var profile = _db.AgentProfiles.FirstOrDefault(x => x.AgentUserId == userId);
        if (profile == null)
        {
            profile = new Domain.Entities.AgentProfile
            {
                AgentUserId = userId,
                AgentUpn = vm.Email ?? "",
                CreatedUtc = DateTime.UtcNow
            };
            _db.AgentProfiles.Add(profile);
        }

        profile.FullName = vm.FullName?.Trim();
        profile.Title = string.IsNullOrWhiteSpace(vm.Title) ? null : vm.Title.Trim();
        profile.Npn = vm.Npn?.Trim();
        profile.Phone = vm.Phone?.Trim();
        profile.ShortBio = string.IsNullOrWhiteSpace(vm.ShortBio) ? null : vm.ShortBio.Trim();
        // Email (UPN) remains authoritative from directory; do not allow editing here.
        profile.UpdatedUtc = DateTime.UtcNow;

        _db.SaveChanges();
        TempData["ProfileSaved"] = "Agent profile updated.";
        return RedirectToAction(nameof(ManageProfile));
    }
}
