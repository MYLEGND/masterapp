using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ClientApp.Services;
using Infrastructure.Bookings;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace ClientApp.Controllers;

/// <summary>
/// Authenticated business-scoped transport into the existing AgentPortal scheduler.
/// This controller owns no booking configuration, Graph logic, or appointment state.
/// </summary>
[Authorize]
[Route("business/{businessId:guid}/crm/api/Booking")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BusinessBookingController(
    MasterAppDbContext db,
    EffectiveClientContextService contextService,
    BusinessBookingTicketProtector tickets,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration) : Controller
{
    private const string TicketHeader = "X-Legend-Business-Booking-Ticket";

    [HttpGet("status")]
    public Task<IActionResult> Status(Guid businessId, CancellationToken ct) =>
        ForwardAsync(businessId, HttpMethod.Get, "status", null, ct);

    [HttpGet("day-availability")]
    public Task<IActionResult> DayAvailability(Guid businessId, string date, string? excludeEventId, CancellationToken ct)
    {
        var query = $"day-availability?date={Uri.EscapeDataString(date ?? string.Empty)}";
        if (!string.IsNullOrWhiteSpace(excludeEventId))
            query += "&excludeEventId=" + Uri.EscapeDataString(excludeEventId.Trim());
        return ForwardAsync(businessId, HttpMethod.Get, query, null, ct);
    }

    [HttpPost("create-event")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Create(Guid businessId, [FromBody] JsonElement payload, CancellationToken ct) =>
        ForwardAsync(businessId, HttpMethod.Post, "create-event", payload.GetRawText(), ct);

    [HttpPost("update-appointment")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Update(Guid businessId, [FromBody] JsonElement payload, CancellationToken ct) =>
        ForwardAsync(businessId, HttpMethod.Post, "update-appointment", payload.GetRawText(), ct);

    [HttpPost("cancel-appointment")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Cancel(Guid businessId, [FromBody] JsonElement payload, CancellationToken ct) =>
        ForwardAsync(businessId, HttpMethod.Post, "cancel-appointment", payload.GetRawText(), ct);

    private async Task<IActionResult> ForwardAsync(
        Guid businessId,
        HttpMethod method,
        string relative,
        string? json,
        CancellationToken ct)
    {
        var context = await contextService.ResolveAsync(User, Request.Cookies, allowRelink: false);
        if (context is null) return Forbid();

        var actor = User.GetCanonicalUserId();
        var actorEmail = context.IsAgentView ? context.AgentEmail : context.Profile.Email;
        var business = await BusinessWorkspaceAccess.ResolveAsync(
            db, businessId, context.Profile.Id, actor, actorEmail, "crm", ct);
        if (business is null) return Forbid();

        AgentProfile? agent = null;
        if (context.IsAgentView)
        {
            var actorKey = (actor ?? string.Empty).Trim().ToLower();
            var actorEmailKey = (context.AgentEmail ?? string.Empty).Trim().ToLower();
            agent = await db.AgentProfiles.AsNoTracking()
                .Where(x =>
                    (!string.IsNullOrWhiteSpace(actorKey) &&
                     (x.AgentUserId ?? string.Empty).Trim().ToLower() == actorKey) ||
                    (!string.IsNullOrWhiteSpace(actorEmailKey) &&
                     ((x.AgentUpn ?? string.Empty).Trim().ToLower() == actorEmailKey ||
                      (x.NormalizedEmail ?? string.Empty).Trim().ToLower() == actorEmailKey)))
                .OrderByDescending(x => x.UpdatedUtc)
                .FirstOrDefaultAsync(ct);
        }

        if (agent is null && context.AgentProfileId is { } attachedProfileId)
            agent = await BusinessBookingAccess.ResolveAttachedAgentAsync(db, businessId, attachedProfileId, ct);

        if (agent is null ||
            await BusinessBookingAccess.ResolveAttachedAgentAsync(db, businessId, agent.Id, ct) is null)
            return Conflict("This business does not have an active attached agent booking profile.");

        var actorDisplay = (actorEmail ?? actor ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(actorDisplay))
            return Forbid();

        var timeZoneId =
            User.FindFirstValue("zoneinfo") ??
            User.FindFirstValue("timezone") ??
            User.FindFirstValue("timeZone") ??
            User.FindFirstValue("tz") ??
            "America/Phoenix";

        var ticket = tickets.Protect(new BusinessBookingTicket(
            businessId,
            agent.Id,
            agent.AgentUserId,
            actorDisplay,
            timeZoneId,
            DateTime.UtcNow.AddMinutes(3)));

        var baseUrl = (configuration["AgentPortal:BaseUrl"] ??
                       configuration["AgentPortalBaseUrl"] ??
                       "https://portal.mylegnd.com").TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBase) ||
            parsedBase.Scheme != Uri.UriSchemeHttps)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "AgentPortal booking authority is not configured.");

        using var request = new HttpRequestMessage(method,
            new Uri(parsedBase, "/api/internal/business-booking/" + relative));
        request.Headers.TryAddWithoutValidation(TicketHeader, ticket);
        request.Headers.Accept.ParseAdd("application/json");
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var client = httpClientFactory.CreateClient("AgentPortalBusinessBooking");
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "The canonical booking authority is temporarily unavailable.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            return new ContentResult
            {
                StatusCode = (int)response.StatusCode,
                ContentType = contentType,
                Content = body
            };
        }
    }
}
