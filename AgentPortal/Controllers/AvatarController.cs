using System.Security.Claims;
using AgentPortal.Services.Tracking;
using AgentPortal.Services;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Controllers;

[Authorize]
[EnableRateLimiting("anon-public")]
public sealed class AvatarController : Controller
{
    private readonly MasterAppDbContext _db;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AvatarController> _logger;
    private readonly AgentTrackingResolver _trackingResolver;
    private readonly AgentProfileAccessResolver _profileAccessResolver;
    private readonly IProfileImageWriter _profileImages;

    public AvatarController(
        MasterAppDbContext db,
        IWebHostEnvironment environment,
        ILogger<AvatarController> logger,
        AgentTrackingResolver trackingResolver,
        AgentProfileAccessResolver profileAccessResolver,
        IProfileImageWriter profileImages)
    {
        _db = db;
        _environment = environment;
        _logger = logger;
        _trackingResolver = trackingResolver;
        _profileAccessResolver = profileAccessResolver;
        _profileImages = profileImages;
    }

    [HttpGet]
    public async Task<IActionResult> Edit()
    {
        var profile = await GetCurrentProfileAsync(HttpContext.RequestAborted);
        return profile is null
            ? Forbid()
            : RedirectToAction("ManageProfile", "Account");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(IFormFile photo)
    {
        var profile = await GetCurrentProfileAsync(HttpContext.RequestAborted);
        if (profile is null)
            return Forbid();

        if (photo is null || photo.Length == 0)
        {
            TempData["AvatarError"] = "Please choose an image file.";
            return RedirectToAction("ManageProfile", "Account");
        }

        try
        {
            await using var stream = new MemoryStream();
            await photo.CopyToAsync(stream, HttpContext.RequestAborted);
            var bytes = stream.ToArray();

            var result = await _profileImages.UpdateAsync(
                new MessagingParticipantIdentity(
                    string.Empty,
                    MessagingParticipantTypes.Agent,
                    profile.Id,
                    string.Empty,
                    null,
                    string.Empty),
                bytes,
                HttpContext.RequestAborted);
            if (!result.Succeeded)
            {
                TempData["AvatarError"] = result.ErrorMessage ?? "Only valid PNG, JPG, or WEBP images are allowed.";
                return RedirectToAction("ManageProfile", "Account");
            }

            TempData["AvatarSuccess"] = "Profile picture updated.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent profile image upload failed. AgentProfileId={AgentProfileId}", profile.Id);
            TempData["AvatarError"] = "We couldn’t save your profile picture right now. Please try again.";
        }

        return RedirectToAction("ManageProfile", "Account");
    }

    [HttpGet("avatar/current")]
    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> Current()
    {
        var profile = await GetCurrentProfileAsync(HttpContext.RequestAborted);
        return HasImage(profile)
            ? File(profile!.ProfileImageContent!, profile.ProfileImageContentType!)
            : DefaultAvatarResult();
    }

    [HttpGet("avatar/agent/{slug}")]
    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> Agent(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return DefaultAvatarResult();

        var resolved = await _trackingResolver.ResolveAsync(slug.Trim(), null, HttpContext.RequestAborted);
        if (!resolved.Found || string.IsNullOrWhiteSpace(resolved.Profile.AgentUserId))
            return DefaultAvatarResult();

        var agentUserId = Normalize(resolved.Profile.AgentUserId);
        var profile = await _db.AgentProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.IsActive && x.AgentUserId.ToLower() == agentUserId, HttpContext.RequestAborted);
        return HasImage(profile)
            ? File(profile!.ProfileImageContent!, profile.ProfileImageContentType!)
            : DefaultAvatarResult();
    }

    private async Task<Domain.Entities.AgentProfile?> GetCurrentProfileAsync(CancellationToken cancellationToken)
    {
        return await _profileAccessResolver.ResolveCurrentAsync(
            User,
            requireActive: true,
            cancellationToken);
    }

    private IActionResult DefaultAvatarResult()
    {
        var defaultAvatar = Path.Combine(_environment.WebRootPath, "images", "company-icons", "legend.png");
        return System.IO.File.Exists(defaultAvatar)
            ? PhysicalFile(defaultAvatar, "image/png")
            : NotFound();
    }

    private static bool HasImage(Domain.Entities.AgentProfile? profile) =>
        profile?.ProfileImageContent is { Length: > 0 } &&
        profile.ProfileImageContentType is "image/png" or "image/jpeg" or "image/webp";

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

}
