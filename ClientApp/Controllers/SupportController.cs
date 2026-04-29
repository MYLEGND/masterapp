using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Infrastructure.Data;
using Shared.Auth;
using System.Linq;
using System.Security.Claims;

namespace ClientApp.Controllers;

[Authorize]
public class SupportController : Controller
{
    private const string ImpersonationCookieName = "impClientProfileId";
    private const string SelfClientCookieName = "selfClientProfileId";
    private const string ImpersonationLaunchCookieName = "impClientLaunch";
    private readonly MasterAppDbContext _db;

    public SupportController(MasterAppDbContext db)
    {
        _db = db;
    }

    private string GetUpn()
    {
        return (User.FindFirstValue("preferred_username")
             ?? User.FindFirstValue(ClaimTypes.Upn)
             ?? User.FindFirstValue(ClaimTypes.Email)
             ?? User.Identity?.Name
             ?? "").Trim().ToLowerInvariant();
    }

    private string[] GetAgentIdCandidates()
    {
        return User.GetUserIdCandidates()
            .Select(x => (x ?? "").Trim().ToLowerInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToArray();
    }

    // Agent clicks "View Profile" -> send them here:
    // /support/view-as-client/{clientProfileId}
    private static readonly HashSet<string> AllowedReturnUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        "/",
        "/profile",
        "/finance",
        "/protectionsnapshot",
        "/bookkeeping",
        "/bookkeeping/reports",
        "/resources",
        "/training"
    };

    private static string NormalizeReturnUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "/profile";

        raw = raw.Trim();
        if (!raw.StartsWith("/")) raw = "/" + raw;

        var hashIndex = raw.IndexOf('#');
        if (hashIndex >= 0)
            raw = raw[..hashIndex];

        var pathOnly = raw;
        var queryIndex = pathOnly.IndexOf('?');
        if (queryIndex >= 0)
            pathOnly = pathOnly[..queryIndex];

        return AllowedReturnUrls.Contains(pathOnly) ? raw : "/profile";
    }

        [HttpGet("/support/view-as-client/{clientProfileId:guid}")]
        public async Task<IActionResult> ViewAsClient(Guid clientProfileId, string? returnUrl = null)
        {
        var upn = GetUpn();
        var target = NormalizeReturnUrl(returnUrl);

        var profile = await _db.ClientProfiles
            .AsNoTracking()
            .Where(p => p.Id == clientProfileId)
            .Select(p => new
            {
                ClientUserId = (p.ClientUserId ?? "").Trim().ToLower(),
                Email = (p.Email ?? "").Trim().ToLower()
            })
            .FirstOrDefaultAsync();

        if (profile == null)
            return NotFound("Client profile not found.");

        var clientUserId = profile.ClientUserId;
        if (string.IsNullOrWhiteSpace(clientUserId))
            return NotFound("Client profile not found.");

        var agentIdCandidates = GetAgentIdCandidates();

        // Enforce ownership: only the agent who owns this client can impersonate
        var owns = await _db.AgentClients
            .AsNoTracking()
            .AnyAsync(a =>
                (a.ClientUserId ?? "").Trim().ToLower() == clientUserId &&
                (
                    (!string.IsNullOrWhiteSpace(upn) && (a.AgentUpn ?? "").Trim().ToLower() == upn) ||
                    agentIdCandidates.Contains((a.AgentUserId ?? "").Trim().ToLower())
                ));

        if (owns)
        {
            // Agent path
            Response.Cookies.Append(
                ImpersonationCookieName,
                clientProfileId.ToString(),
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    IsEssential = true,
                    Expires = DateTimeOffset.UtcNow.AddHours(2)
                });

            Response.Cookies.Append(
                ImpersonationLaunchCookieName,
                clientProfileId.ToString(),
                new CookieOptions
                {
                    HttpOnly = false,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    IsEssential = true,
                    Expires = DateTimeOffset.UtcNow.AddMinutes(10)
                });

            return Redirect(target);
        }

        // Fallback: allow the client themselves
        var userIdCandidates = User.GetUserIdCandidates()
            .Select(x => (x ?? "").Trim().ToLowerInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToArray();

        var matchesClient =
            (!string.IsNullOrWhiteSpace(profile.ClientUserId) &&
             userIdCandidates.Contains(profile.ClientUserId)) ||
            (!string.IsNullOrWhiteSpace(profile.Email) &&
             string.Equals(profile.Email, upn, StringComparison.OrdinalIgnoreCase));

        if (!matchesClient)
            return Forbid();

        Response.Cookies.Append(
            SelfClientCookieName,
            clientProfileId.ToString(),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Expires = DateTimeOffset.UtcNow.AddHours(12)
            });

        Response.Cookies.Delete(ImpersonationCookieName);
        Response.Cookies.Delete(ImpersonationLaunchCookieName);

        return Redirect(target);
    }

    [HttpGet("/support/stop-view-as-client")]
    public IActionResult StopViewAsClient(string? returnUrl = null, bool clearSelf = false)
    {
        Response.Cookies.Delete(ImpersonationCookieName);
        Response.Cookies.Delete(ImpersonationLaunchCookieName);

        if (clearSelf)
            Response.Cookies.Delete(SelfClientCookieName);

        return Redirect(NormalizeReturnUrl(returnUrl));
    }
}
