using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;
using Domain.Entities;
using ClientApp.Services;

namespace ClientApp.Controllers;

public class ProfileController : Controller
{
    private readonly MasterAppDbContext _db;
    private readonly EffectiveClientContextService _clientContext;

    public ProfileController(
        MasterAppDbContext db,
        EffectiveClientContextService clientContext)
    {
        _db = db;
        _clientContext = clientContext;
    }

    private static string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant();

    private async Task<HouseholdMember?> LoadSignificantOtherAsync(string clientId)
    {
        var clientIdNorm = Norm(clientId);

        // ✅ spouse stored as HouseholdMember (SignificantOther / Spouse)
        // NOTE: No Norm() inside the EF query. Normalize only the input.
        var so = await _db.HouseholdMembers
            .AsNoTracking()
            .Where(x => x.ClientUserId == clientIdNorm)
            .Where(x => x.RelationshipType == "SignificantOther" || x.RelationshipType == "Spouse")
            .OrderByDescending(x => x.UpdatedUtc)
            .ThenByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync();

        // Backward-compat: if you have legacy rows with different casing, fall back once.
        if (so == null)
        {
            so = await _db.HouseholdMembers
                .AsNoTracking()
                .Where(x => x.ClientUserId == clientIdNorm)
                .Where(x =>
                    (x.RelationshipType ?? "").ToLower() == "significantother" ||
                    (x.RelationshipType ?? "").ToLower() == "spouse")
                .OrderByDescending(x => x.UpdatedUtc)
                .ThenByDescending(x => x.CreatedUtc)
                .FirstOrDefaultAsync();
        }

        return so;
    }

    // CLIENT: /profile
    [HttpGet("/profile")]
    public async Task<IActionResult> MyProfile()
    {
        var context = await _clientContext.ResolveAsync(User, Request.Cookies);
        if (context == null)
            return NotFound("No client profile found for this user.");

        ViewBag.SignificantOther = await LoadSignificantOtherAsync(context.ClientUserId);
        ViewBag.ViewMode = context.IsAgentView ? "agent" : "client";
        ViewBag.ViewingClientName = $"{context.Profile.FirstName} {context.Profile.LastName}".Trim();

        return View("Index", context.Profile);
    }

    // AGENT: /profile/{clientUserId}
    [HttpGet("/profile/{clientUserId}")]
    public async Task<IActionResult> ClientProfile(string clientUserId)
    {
        var canonicalAgentId = Norm(User.GetStableUserId());
        var clientId = Norm(clientUserId);

        if (string.IsNullOrWhiteSpace(canonicalAgentId) || string.IsNullOrWhiteSpace(clientId))
            return Forbid();

        if (string.Equals(canonicalAgentId, clientId, StringComparison.OrdinalIgnoreCase))
            return RedirectToAction(nameof(MyProfile));

        // Ensure client exists (✅ no Norm in query)
        var clientExists = await _db.ClientProfiles
            .AsNoTracking()
            .AnyAsync(x => x.ClientUserId == clientId);

        if (!clientExists)
            return NotFound("Client profile not found.");

        // 1) Canonical link path (✅ no Norm in query)
        var link = await _db.AgentClients
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AgentUserId == canonicalAgentId && x.ClientUserId == clientId);

        // 2) Legacy candidates path
        if (link == null)
        {
            var candidates = User.GetUserIdCandidates()
                .Select(Norm)
                .Distinct()
                .ToArray();

            // ✅ no Norm in query; candidates already normalized
            link = await _db.AgentClients
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.ClientUserId == clientId &&
                    candidates.Contains(x.AgentUserId));
        }

        if (link == null)
            return Forbid();

        var profile = await _db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ClientUserId == clientId);

        if (profile == null)
            return NotFound("Client profile not found.");

        ViewBag.SignificantOther = await LoadSignificantOtherAsync(clientId);

        ViewBag.ViewMode = "agent";
        ViewBag.ViewingClientName = $"{profile.FirstName} {profile.LastName}";
        return View("Index", profile);
    }
}
