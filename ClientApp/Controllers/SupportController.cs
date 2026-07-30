using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Infrastructure.Data;
using Shared.Auth;
using System.Linq;
using System.Security.Claims;
using ClientApp.Services;
using ClientApp.Infrastructure;

namespace ClientApp.Controllers;

[Authorize]
public class SupportController : Controller
{
    private const string ImpersonationCookieName = "impClientProfileId";
    private const string SelfClientCookieName = "selfClientProfileId";
    private const string ImpersonationLaunchCookieName = "impClientLaunch";
    private readonly MasterAppDbContext _db;
    private readonly ClientAppReturnUrlNormalizer _returnUrlNormalizer;

    public SupportController(MasterAppDbContext db, ClientAppReturnUrlNormalizer returnUrlNormalizer)
    {
        _db = db;
        _returnUrlNormalizer = returnUrlNormalizer;
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

    private string NormalizeSupportReturnUrl(string? raw)
    {
        var target = _returnUrlNormalizer.Normalize(raw);
        var hashIndex = target.IndexOf('#');
        if (hashIndex >= 0)
            target = target[..hashIndex];

        var pathOnly = target;
        var queryIndex = pathOnly.IndexOf('?');
        if (queryIndex >= 0)
            pathOnly = pathOnly[..queryIndex];

        return AllowedReturnUrls.Contains(pathOnly)
            ? target
            : ClientAppReturnUrlNormalizer.SafeLandingPath;
    }

    // This entry point performs its own owner check before it issues the
    // short-lived impersonation cookie. It must stay reachable for an agent
    // even when the selected client has not activated a subscription yet.
    [BypassClientSubscriptionRequirement]
    [HttpGet("/support/view-as-client/{clientProfileId:guid}")]
    public async Task<IActionResult> ViewAsClient(Guid clientProfileId, string? returnUrl = null)
    {
        var upn = GetUpn();
        var target = NormalizeSupportReturnUrl(returnUrl);

        var profile = await _db.ClientProfiles
            .AsNoTracking()
            .Where(p => p.Id == clientProfileId)
            .Select(p => new
            {
                ClientUserId = (p.ClientUserId ?? "").Trim().ToLower(),
                ExternalIdentityObjectId = (p.ExternalIdentityObjectId ?? "").Trim().ToLower(),
                Email = (p.Email ?? "").Trim().ToLower()
            })
            .FirstOrDefaultAsync();

        if (profile == null)
            return NotFound("Client profile not found.");

        var clientUserId = profile.ClientUserId;
        if (string.IsNullOrWhiteSpace(clientUserId))
            return NotFound("Client profile not found.");

        var agentIdCandidates = GetAgentIdCandidates();

        // Enforce ownership via the single shared ownership authority (D2/F21)
        // rather than reproducing the AgentClients predicate. Object ID is
        // authoritative; legacy candidate ids and UPN remain explicit
        // compatibility inputs.
        var owns = await _db.AgentOwnsClientAsync(
            User.GetCanonicalUserId(),
            clientUserId,
            upn,
            agentIdCandidates);

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
            (!string.IsNullOrWhiteSpace(profile.ExternalIdentityObjectId) &&
             userIdCandidates.Contains(profile.ExternalIdentityObjectId)) ||
            (!string.IsNullOrWhiteSpace(profile.ClientUserId) &&
             userIdCandidates.Contains(profile.ClientUserId));

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

    // Allow an agent to leave a managed view even if the client's
    // subscription changes while the view is open.
    [BypassClientSubscriptionRequirement]
    [HttpGet("/support/stop-view-as-client")]
    public IActionResult StopViewAsClient(string? returnUrl = null, bool clearSelf = false)
    {
        Response.Cookies.Delete(ImpersonationCookieName);
        Response.Cookies.Delete(ImpersonationLaunchCookieName);

        if (clearSelf)
            Response.Cookies.Delete(SelfClientCookieName);

        return Redirect(NormalizeSupportReturnUrl(returnUrl));
    }
}
