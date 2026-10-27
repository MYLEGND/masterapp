using System.Security.Cryptography;
using System.Text.Json;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Analytics;
using Shared.Auth;

namespace Infrastructure.Reception;

/// <summary>Website media only. Owner, configuration, credentials and business effects stay server-owned.</summary>
[ApiController, AllowAnonymous, IgnoreAntiforgeryToken]
[EnableCors(CorsPolicy), EnableRateLimiting("ingest")]
[Route("api/receptionist/browser")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ReceptionistBrowserController(IServiceProvider services) : ControllerBase
{
    internal const string CorsPolicy = "PublicWebsiteReceptionist";
    public sealed record GrantRequest(string SourcePath);
    public sealed record StartRequest(string Grant, string Offer);
    public sealed record ControlRequest(string Handle);
    private sealed record Ticket(int Version, string Origin, string SourcePath, string OwnerKey,
        Guid ConnectionId, Guid ConfigurationId, Guid CreationId, DateTime ExpiresUtc, Guid? CallId = null,
        string? ActorId = null, Guid? WindowId = null, Guid? WindowRevision = null);
    private IDataProtector Protector => services.GetRequiredService<IDataProtectionProvider>().CreateProtector("receptionist-browser-v1");
    private TimeProvider Clock => services.GetRequiredService<TimeProvider>();
    private ReceptionistAdmissionService Admission => services.GetRequiredService<ReceptionistAdmissionService>();

    [HttpPost("grant"), RequestSizeLimit(4096)]
    public async Task<IActionResult> Grant([FromBody] GrantRequest request, CancellationToken ct)
    {
        var runtime = services.GetService<ReceptionistRuntimeAuthority>();
        if (runtime?.Enabled != true) return Unavailable();
        var path = request.SourcePath;
        if (!ValidPath(path) || !PublicWebsiteRuntimeScopeResolver.HasValidPublicOrigin(HttpContext)) return BadRequest();
        var scope = await services.GetRequiredService<PublicWebsiteRuntimeScopeResolver>().ResolveInquiryAsync(HttpContext, path, ct);
        if (scope is null || !PublicWebsiteRuntimeScopeResolver.IsPublishedPath(scope, path)) return NotFound();
        var owner = ScopeOwner(scope);
        if (owner is null) return NotFound();
        var binding = await Admission.GetBrowserBindingAsync(owner, ct);
        // Anonymous traffic can never select a synthetic acceptance classification.
        if (binding is null || binding.Binding.ControlledTestOnly || !ReceptionistConfigurationCodec.IsOpen(binding.Configuration, Clock.GetUtcNow().UtcDateTime)) return Unavailable();
        try { _ = ReceptionistRuntimeAuthority.Ready(binding.Provider, binding.Configuration, binding.Binding, false); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or JsonException) { return Unavailable(); }
        var ticket = new Ticket(1, Request.Headers.Origin.ToString(), path, owner.Key, binding.MarketingConnectionId,
            binding.ConfigurationId, Guid.NewGuid(), Clock.GetUtcNow().UtcDateTime.AddSeconds(90));
        return Ok(new { grant = Protect(ticket), expiresInSeconds = 90 });
    }

    [HttpPost("start"), RequestSizeLimit(73728)]
    public async Task<IActionResult> Start([FromBody] StartRequest request, CancellationToken ct)
    {
        var runtime = services.GetService<ReceptionistRuntimeAuthority>();
        if (runtime?.Enabled != true) return Unavailable();
        var ticket = Unprotect(request.Grant);
        if (ticket is null || ticket.CallId is not null || ticket.ActorId is not null) return BadRequest();
        try { OpenAiReceptionistProtocol.ValidateSdp(request.Offer); }
        catch (ArgumentException) { return BadRequest(); }
        var scope = await services.GetRequiredService<PublicWebsiteRuntimeScopeResolver>().ResolveInquiryAsync(HttpContext, ticket.SourcePath, ct);
        var owner = scope is null ? null : ScopeOwner(scope);
        if (scope is null || owner?.Key != ticket.OwnerKey || !PublicWebsiteRuntimeScopeResolver.IsPublishedPath(scope, ticket.SourcePath)) return NotFound();
        var current = await Admission.GetBrowserBindingAsync(owner!, ct);
        if (current is null || current.MarketingConnectionId != ticket.ConnectionId || current.ConfigurationId != ticket.ConfigurationId ||
            current.Binding.ControlledTestOnly) return Unavailable();
        return await StartCoreAsync(ticket, current, request, runtime, ct);
    }

    // Called only from existing owner-authorized, antiforgery-protected management actions.
    [NonAction]
    public async Task<IActionResult> GrantForOwnerAsync(MarketingOwnerScope owner,
        Func<MarketingOwnerScope, CancellationToken, Task<bool>> authorize, CancellationToken ct)
    {
        if (!await OwnerAuthorizedAsync(owner, authorize, ct)) return Forbid();
        if (services.GetService<ReceptionistRuntimeAuthority>()?.Enabled != true) return Unavailable();
        var binding = await Admission.GetBrowserBindingAsync(owner, ct);
        if (binding is null || !binding.Binding.ControlledTestOnly) return Unavailable();
        var window = await Admission.GetAcceptanceWindowAsync(binding, ct);
        if (window is null || !ReceptionistConfigurationCodec.IsOpen(binding.Configuration, Clock.GetUtcNow().UtcDateTime)) return Unavailable();
        var expires = new[] { Clock.GetUtcNow().UtcDateTime.AddSeconds(90), window.ExpiresUtc }.Min();
        var ticket = new Ticket(1, Request.Headers.Origin.ToString(), "/", owner.Key, binding.MarketingConnectionId,
            binding.ConfigurationId, Guid.NewGuid(), expires, ActorId: User.GetCanonicalUserId(), WindowId: window.Id, WindowRevision: window.Revision);
        return Ok(new { grant = Protect(ticket), expiresInSeconds = Math.Max(1, (int)(expires - Clock.GetUtcNow().UtcDateTime).TotalSeconds) });
    }

    [NonAction]
    public async Task<IActionResult> StartForOwnerAsync(MarketingOwnerScope owner, StartRequest request,
        Func<MarketingOwnerScope, CancellationToken, Task<bool>> authorize, CancellationToken ct)
    {
        if (!await OwnerAuthorizedAsync(owner, authorize, ct)) return Forbid();
        var ticket = Unprotect(request.Grant);
        if (ticket is null || ticket.ActorId is null || ticket.OwnerKey != owner.Key || ticket.CallId is not null) return BadRequest();
        var runtime = services.GetService<ReceptionistRuntimeAuthority>();
        if (runtime?.Enabled != true) return Unavailable();
        try { OpenAiReceptionistProtocol.ValidateSdp(request.Offer); } catch (ArgumentException) { return BadRequest(); }
        var current = await Admission.GetBrowserBindingAsync(owner, ct);
        if (current is null || !current.Binding.ControlledTestOnly || current.MarketingConnectionId != ticket.ConnectionId ||
            current.ConfigurationId != ticket.ConfigurationId) return Unavailable();
        var window = await Admission.GetAcceptanceWindowAsync(current, ct);
        if (window is null || window.Id != ticket.WindowId || window.Revision != ticket.WindowRevision) return Unavailable();
        return await StartCoreAsync(ticket, current, request, runtime, ct);
    }

    private async Task<bool> OwnerAuthorizedAsync(MarketingOwnerScope owner,
        Func<MarketingOwnerScope, CancellationToken, Task<bool>> authorize, CancellationToken ct) =>
        User.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(User.GetCanonicalUserId()) &&
        Request.Headers.Origin.ToString() == Request.Scheme + "://" + Request.Host && await authorize(owner, ct);

    private async Task<IActionResult> StartCoreAsync(Ticket ticket, ReceptionistIngressBinding current,
        StartRequest request, ReceptionistRuntimeAuthority runtime, CancellationToken ct)
    {
        var controlled = ticket.ActorId is not null;
        OpenAiReceptionistSessionSettings settings;
        string credential;
        try
        {
            settings = ReceptionistRuntimeAuthority.Ready(current.Provider, current.Configuration, current.Binding, controlled);
            credential = runtime.Credential(current.Owner, current.Binding, false);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or JsonException or CryptographicException) { return Unavailable(); }
        var claim = await Admission.ReserveBrowserAsync(current, ticket.CreationId,
            ReceptionistConfigurationCodec.Hash(request.Offer), controlled, ct, ticket.WindowId, ticket.WindowRevision);
        if (!claim.MayCreate || claim.CallId is not { } callId) return Unavailable();
        // No request retries. The reservation commits before this one provider call.
        OpenAiReceptionistBrowserOutcome outcome;
        try
        {
            outcome = await services.GetRequiredService<IOpenAiReceptionistClient>().CreateBrowserAsync(
                current.Binding.ProviderProjectKey, credential, settings, request.Offer, ct);
        }
        catch (Exception) { outcome = new(false, null, null, null, "browser_create:outcome_unknown"); }
        var handoffAttempted = false;
        try
        {
            using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var usable = await Admission.BindBrowserOutcomeAsync(callId, ticket.CreationId, outcome, !ct.IsCancellationRequested, persistence.Token);
            // Recheck after persistence; the browser may have left during the commit.
            if (!usable || ct.IsCancellationRequested || outcome.Answer is null)
            {
                await CleanupOriginalAsync(ticket, claim, outcome);
                return Unavailable();
            }
            handoffAttempted = true;
            using var handoff = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (!await Admission.ReleaseBrowserCleanupAsync(callId, ticket.CreationId, claim.CleanupActionId!.Value, handoff.Token))
            {
                await Admission.RequestRuntimeStopAsync(callId, "browser_ended", handoff.Token);
                return Unavailable();
            }
            if (ct.IsCancellationRequested)
            {
                await Admission.RequestRuntimeStopAsync(callId, "browser_ended", handoff.Token);
                return Unavailable();
            }
            var handle = Protect(ticket with { CallId = callId, ExpiresUtc = Clock.GetUtcNow().UtcDateTime.AddSeconds(current.Configuration.MaximumCallSeconds + 300) });
            // No provider ID, private instructions, keys, tool results or session object is exposed.
            return Ok(new { answer = outcome.Answer, handle, maximumSeconds = current.Configuration.MaximumCallSeconds });
        }
        catch (Exception)
        {
            if (!handoffAttempted) await CleanupOriginalAsync(ticket, claim, outcome);
            else
            {
                // Handoff may have committed: the creator must not compete with the watchdog.
                try
                {
                    using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await Admission.RequestRuntimeStopAsync(callId, "browser_ended", recovery.Token);
                }
                catch (Exception) { LogRecovery(callId, outcome); }
            }
            return Unavailable();
        }
    }

    private async Task CleanupOriginalAsync(Ticket ticket,
        ReceptionistBrowserClaim claim, OpenAiReceptionistBrowserOutcome outcome)
    {
        if (outcome.SessionId is null) return; // Unknown original outcome retains its entire reservation.
        try
        {
            // No provider stop was attempted by this request. Persist the original identity and
            // transfer the provably unused cleanup through the existing durable handoff. Only the
            // worker holding an observation lease may attach and send WebRTC session.close.
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await Admission.BindBrowserOutcomeAsync(claim.CallId!.Value, ticket.CreationId, outcome, false, recovery.Token);
            if (!await Admission.ReleaseBrowserCleanupAsync(claim.CallId.Value, ticket.CreationId,
                claim.CleanupActionId!.Value, recovery.Token))
            {
                LogRecovery(claim.CallId.Value, outcome);
                return; // Expired/uncertain ownership is not permission to retry a provider effect.
            }
            await Admission.RequestRuntimeStopAsync(claim.CallId.Value, "browser_ended", recovery.Token);
        }
        catch (Exception) { LogRecovery(claim.CallId!.Value, outcome); }
    }

    private void LogRecovery(Guid callId, OpenAiReceptionistBrowserOutcome outcome) =>
        services.GetRequiredService<ILogger<ReceptionistBrowserController>>().LogError(
            "Receptionist browser recovery unresolved. Call {CallId}; original provider session {SessionId}; receipt {Receipt}. Reservation retained.",
            callId, outcome.SessionId, outcome.Receipt);

    [HttpPost("status"), RequestSizeLimit(8192)]
    public async Task<IActionResult> Status([FromBody] ControlRequest request, CancellationToken ct)
    {
        var ticket = Unprotect(request.Handle);
        if (ticket?.CallId is not { } id) return BadRequest();
        var state = await Admission.ReadBrowserStatusAsync(id, ticket.CreationId, ticket.ConnectionId, ticket.OwnerKey, ct);
        return state is null ? NotFound() : Ok(new { state });
    }

    [HttpPost("end"), RequestSizeLimit(8192)]
    public async Task<IActionResult> End([FromBody] ControlRequest request, CancellationToken ct)
    {
        var ticket = Unprotect(request.Handle);
        if (ticket?.CallId is not { } id || await Admission.ReadBrowserStatusAsync(id, ticket.CreationId,
            ticket.ConnectionId, ticket.OwnerKey, ct) is null) return NotFound();
        await Admission.RequestRuntimeStopAsync(id, "browser_ended", ct);
        return Ok(new { state = "ending" }); // Closure and final charges require independent provider evidence.
    }

    private IActionResult Unavailable() => StatusCode(503, new { state = "unavailable", message = "Voice is unavailable. Please use the contact form." });
    private string Protect(Ticket ticket) => Protector.Protect(JsonSerializer.Serialize(ticket));
    private Ticket? Unprotect(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192) return null;
        try
        {
            var ticket = JsonSerializer.Deserialize<Ticket>(Protector.Unprotect(value));
            return ticket is { Version: 1 } && ticket.CreationId != Guid.Empty && ticket.ConnectionId != Guid.Empty &&
                ticket.ConfigurationId != Guid.Empty && ticket.ExpiresUtc.Kind == DateTimeKind.Utc && ticket.ExpiresUtc > Clock.GetUtcNow().UtcDateTime &&
                ticket.Origin == Request.Headers.Origin.ToString() && ValidPath(ticket.SourcePath) &&
                (ticket.ActorId is null ? ticket.WindowId is null && ticket.WindowRevision is null :
                    ticket.WindowId is not null && ticket.WindowRevision is not null && User.Identity?.IsAuthenticated == true &&
                    User.GetCanonicalUserId() == ticket.ActorId) ? ticket : null;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or ArgumentException) { return null; }
    }
    private static bool ValidPath(string? path) => path is { Length: > 0 and <= 2048 } && path.StartsWith('/') &&
        !path.StartsWith("//") && !path.Any(c => char.IsControl(c) || c is '?' or '#' or '\\');
    private static MarketingOwnerScope? ScopeOwner(PublicWebsiteRuntimeScope scope) =>
        scope.CommerceBusinessId is { } business && business != Guid.Empty ? MarketingOwnerScope.Business(business) :
        scope.SiteKey == WebsiteEditorSiteKeys.Legend || scope.IsFounder ? MarketingOwnerScope.Founder :
        scope.SiteKey == WebsiteEditorSiteKeys.Protect && scope.AgentTrackingProfileId is { } agent && agent != Guid.Empty ? MarketingOwnerScope.Agent(agent) : null;
}
