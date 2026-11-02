using System.Security.Claims;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Legend.Commerce;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

public sealed partial class CommerceManagementController
{
    public const string StoreSessionScheme = "LegendStore";
    private const string NonceCookie = "__Host-LEGEND.StoreNonce";

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (Request.Path.StartsWithSegments("/internal") && context.ActionDescriptor.Parameters.Any(p => p.Name == "ticket") &&
            (!context.ActionArguments.TryGetValue("ticket", out var suppliedTicket) || string.IsNullOrWhiteSpace(suppliedTicket as string)))
        {
            var session = await HttpContext.AuthenticateAsync(StoreSessionScheme);
            var scope = await stores.ResolveManagementScopeAsync(HttpContext, Request.Query["businessKey"].ToString(), HttpContext.RequestAborted);
            var actor = session.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!session.Succeeded || scope is null || string.IsNullOrWhiteSpace(actor) ||
                session.Principal!.FindFirstValue("store.business") != scope.CommerceBusinessId.ToString() ||
                session.Principal.FindFirstValue("store.site") != scope.WebsiteSiteKey ||
                session.Principal.FindFirstValue("store.owner") != scope.WebsiteOwnerKey)
            {
                context.Result = HttpMethods.IsGet(Request.Method)
                    ? Redirect(QueryHelpers.AddQueryString("/internal/login", new Dictionary<string,string?> {
                        ["businessKey"] = Request.Query["businessKey"].ToString(), ["returnPath"] = Request.Path + Request.QueryString }))
                    : Unauthorized();
                return;
            }
            Guid? profile = Guid.TryParse(session.Principal.FindFirstValue("store.profile"), out var id) ? id : null;
            // Compatibility adapter for existing action signatures; this token never leaves the session response.
            context.ActionArguments["ticket"] = tickets.Protect(new WebsiteEditorTicket(scope.WebsiteSiteKey,
                scope.WebsiteOwnerKey, session.Principal.FindFirstValue("store.agent"), false, DateTime.UtcNow.AddMinutes(2),
                scope.CommerceBusinessId, actor, ActorClientProfileId: profile));
            context.ModelState.Remove("ticket");
            HttpContext.Items["LegendStoreSession"] = true;
        }
        await next();
    }

    private async Task<string> ProviderContinuationAsync(string ticket, string capability, int minutes, CancellationToken ct)
    {
        if (!HttpContext.Items.ContainsKey("LegendStoreSession")) return ticket;
        var actor = tickets.TryUnprotect(ticket) ?? throw new InvalidOperationException("Store session is unavailable.");
        var scope = await stores.ResolveManagementScopeAsync(HttpContext, Request.Query["businessKey"].ToString(), ct)
            ?? throw new InvalidOperationException("Store scope is unavailable.");
        return tickets.ProtectStoreProviderContinuation(new(actor with { ExpiresUtc = DateTime.UtcNow.AddMinutes(minutes) }, scope.DestinationOrigin, capability));
    }

    private async Task<(CommerceStoreContext Store, WebsiteEditorTicket Actor, string ReturnUrl)?> ResolveProviderContinuationAsync(
        string value, string capability, string returnPath, CancellationToken ct)
    {
        var context = tickets.TryUnprotectStoreProviderContinuation(value);
        if (context is null || context.Capability != capability || context.Actor.CommerceBusinessId is not Guid businessId ||
            string.IsNullOrWhiteSpace(context.Actor.ActorUserId)) return null;
        var actor = context.Actor;
        var scope = await stores.ResolveManagementScopeAsync(new StoreManagementLoginRequest(businessId, actor.SiteKey,
            actor.OwnerUserId, context.DestinationOrigin, returnPath, new string('0', 64), DateTime.UtcNow.AddMinutes(1)), ct);
        if (scope is null) return null;
        var store = await stores.ResolveManagementAsync(scope, actor.ActorUserId, capability, actor.ActorClientProfileId, ct);
        if (store is null) return null;
        var target = context.DestinationOrigin + returnPath;
        if (scope.WebsiteSiteKey == WebsiteEditorSiteKeys.Protect) target = QueryHelpers.AddQueryString(target, "businessKey", scope.BusinessKey);
        return (store, actor, target);
    }

    [HttpGet("session/login")]
    public IActionResult StoreLoginPrompt(string? businessKey, string returnPath = "/internal")
    {
        if (!Request.Path.StartsWithSegments("/internal")) return NotFound();
        Response.Headers["Cache-Control"] = "no-store";
        return View("~/Views/Shared/StoreSession.cshtml", new StoreSessionForm("Continue to store", "/internal/login", businessKey, returnPath, null));
    }

    [HttpGet("session/complete")]
    public IActionResult CompleteStoreLoginPrompt(string state, string? businessKey)
    {
        if (!Request.Path.StartsWithSegments("/internal")) return NotFound();
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return View("~/Views/Shared/StoreSession.cshtml", new StoreSessionForm("Open store workspace", "/internal/login/complete", businessKey, null, state));
    }

    [HttpPost("session/login")]
    public async Task<IActionResult> StoreLogin(string? businessKey, string returnPath = "/", CancellationToken ct = default)
    {
        if (!Request.Path.StartsWithSegments("/internal")) return NotFound();
        var scope = await stores.ResolveManagementScopeAsync(HttpContext, businessKey, ct);
        if (scope is null || !await stores.HasProvisionedStoreAsync(scope, ct)) return NotFound();
        var portal = configuration["AgentPortalBaseUrl"] ?? "https://portal.mylegnd.com";
        if (!Uri.TryCreate(portal, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            !origin.IsDefaultPort || !string.IsNullOrEmpty(origin.UserInfo) || origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment)) return StatusCode(503);
        if (!string.IsNullOrEmpty(businessKey))
            returnPath = QueryHelpers.AddQueryString(returnPath.Split('?')[0], "businessKey", scope.BusinessKey);
        var nonce = WebsiteEditorHandoffToken.Create();
        var request = new StoreManagementLoginRequest(scope.CommerceBusinessId, scope.WebsiteSiteKey, scope.WebsiteOwnerKey,
            scope.DestinationOrigin, returnPath, WebsiteEditorHandoffToken.Hash(nonce), DateTime.UtcNow.AddMinutes(5));
        string protectedRequest;
        try { protectedRequest = tickets.ProtectStoreLoginRequest(request); }
        catch (ArgumentException) { return BadRequest(); }
        Response.Cookies.Append(NonceCookie, nonce, new CookieOptions { HttpOnly = true, Secure = true,
            SameSite = SameSiteMode.Lax, Path = "/", MaxAge = TimeSpan.FromMinutes(5) });
        return Redirect(QueryHelpers.AddQueryString(origin.GetLeftPart(UriPartial.Authority) + "/Account/StoreLogin", "request", protectedRequest));
    }

    [HttpPost("session/complete")]
    public async Task<IActionResult> CompleteStoreLogin(string state, string? businessKey, CancellationToken ct = default)
    {
        if (!Request.Path.StartsWithSegments("/internal")) return NotFound();
        var scope = await stores.ResolveManagementScopeAsync(HttpContext, businessKey, ct);
        if (scope is null || !Request.Cookies.TryGetValue(NonceCookie, out var nonce)) return Unauthorized();
        var result = await WebsiteEditorHandoffService.RedeemStoreAsync(HttpContext.RequestServices.GetRequiredService<MasterAppDbContext>(),
            stores, scope, state, nonce, path => CommerceManagementCapabilityPolicy.ForRequest(path.Split('?')[0], "GET"), ct);
        if (result is null) return Unauthorized();
        var actor = result.Value.Actor;
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actor.ActorUserId), new("store.business", scope.CommerceBusinessId.ToString()),
            new("store.site", scope.WebsiteSiteKey), new("store.owner", scope.WebsiteOwnerKey) };
        if (actor.ClientProfileId.HasValue) claims.Add(new("store.profile", actor.ClientProfileId.Value.ToString()));
        if (!string.IsNullOrWhiteSpace(actor.AgentSlug)) claims.Add(new("store.agent", actor.AgentSlug));
        await HttpContext.SignInAsync(StoreSessionScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, StoreSessionScheme)),
            new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1), AllowRefresh = false });
        Response.Cookies.Delete(NonceCookie, new CookieOptions { Secure = true, Path = "/" });
        return LocalRedirect(result.Value.ReturnPath);
    }

    [HttpGet("session/denied")]
    public IActionResult StoreDenied() => StatusCode(403, "You do not have access to this store.");

    [HttpPost("session/logout")]
    public async Task<IActionResult> StoreLogout()
    {
        if (!Request.Path.StartsWithSegments("/internal")) return NotFound();
        await HttpContext.SignOutAsync(StoreSessionScheme);
        return LocalRedirect("/");
    }
}

public sealed record StoreSessionForm(string Title, string Action, string? BusinessKey, string? ReturnPath, string? State);
