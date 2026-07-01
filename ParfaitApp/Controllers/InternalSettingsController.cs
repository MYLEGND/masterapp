using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Security;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[Authorize]
[Route("internal/settings")]
public sealed class InternalSettingsController : Controller
{
    private readonly IParfaitBusinessProfileService _profileService;
    private readonly IParfaitTeamAccessService _teamAccess;

    public InternalSettingsController(
        IParfaitBusinessProfileService profileService,
        IParfaitTeamAccessService teamAccess)
    {
        _profileService = profileService;
        _teamAccess = teamAccess;
    }

    [HttpGet("business-profile")]
    [ParfaitInternalPage(
        "Business Profile",
        "Settings",
        "Store identity, checkout ownership, and storefront business settings.",
        2,
        1)]
    public async Task<IActionResult> BusinessProfile(CancellationToken ct)
    {
        return View(await _profileService.GetProfileAsync(ct));
    }

    [HttpPost("business-profile")]
    [ParfaitInternalPageAccess("/internal/settings/business-profile")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BusinessProfile(ParfaitBusinessProfileViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            var current = await _profileService.GetProfileAsync(ct);
            model.DomainStatus = current.DomainStatus;
            model.AnalyticsStatus = current.AnalyticsStatus;
            model.TrustStatus = current.TrustStatus;
            return View(model);
        }

        await _profileService.SaveProfileAsync(model, ct);

        TempData["ProfileStatus"] = "Parfait business profile saved.";
        return RedirectToAction(nameof(BusinessProfile));
    }

    [HttpGet("team")]
    [ParfaitInternalPage(
        "Team",
        "Settings",
        "Founder-managed team access, page visibility, and internal route permissions.",
        2,
        2)]
    public async Task<IActionResult> Team(CancellationToken ct)
    {
        return View(await _teamAccess.GetTeamManagementViewModelAsync(ct));
    }

    [HttpPost("team/add")]
    [ParfaitInternalPageAccess("/internal/settings/team")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddTeamMember([Bind(Prefix = "NewMember")] ParfaitTeamCreateMemberInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            var vm = await _teamAccess.GetTeamManagementViewModelAsync(ct);
            vm.NewMember = input;
            return View("Team", vm);
        }

        try
        {
            var inviteScheme = Request.Host.Host.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                ? Request.Scheme
                : Uri.UriSchemeHttps;
            var inviteUrl = Url.Action(
                                nameof(InternalController.Login),
                                "Internal",
                                new { returnUrl = "/internal" },
                                inviteScheme)
                            ?? $"{inviteScheme}://{Request.Host}/internal/login";
            var invitedBy = User.FindFirst("name")?.Value
                            ?? User.Identity?.Name
                            ?? User.FindFirst("preferred_username")?.Value
                            ?? "Parfait Admin";

            await _teamAccess.AddMemberAsync(input, inviteUrl, invitedBy, ct);
            TempData["TeamStatus"] = $"{input.Email.Trim()} added to Parfait internal and invited by email.";
            return RedirectToAction(nameof(Team));
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("NewMember.Email", ex.Message);
            var vm = await _teamAccess.GetTeamManagementViewModelAsync(ct);
            vm.NewMember = input;
            return View("Team", vm);
        }
    }

    [HttpPost("team/{id:guid}")]
    [ParfaitInternalPageAccess("/internal/settings/team")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateTeamMember(Guid id, ParfaitTeamUpdateMemberInput input, CancellationToken ct)
    {
        try
        {
            await _teamAccess.UpdateMemberAsync(id, input, ct);
            TempData["TeamStatus"] = "Team member permissions updated.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["TeamError"] = ex.Message;
        }

        return RedirectToAction(nameof(Team));
    }

    [HttpPost("team/{id:guid}/remove")]
    [ParfaitInternalPageAccess("/internal/settings/team")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveTeamMember(Guid id, CancellationToken ct)
    {
        try
        {
            await _teamAccess.RemoveMemberAsync(id, ct);
            TempData["TeamStatus"] = "Team member removed.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["TeamError"] = ex.Message;
        }

        return RedirectToAction(nameof(Team));
    }
}
