using System.Security.Claims;
using AgentPortal.Services.Tracking;
using AgentPortal.Services;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AgentPortal.Controllers;

[Authorize]
[EnableRateLimiting("anon-public")]
public sealed class AvatarController : Controller
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AvatarController> _logger;
    private readonly AgentTrackingResolver _trackingResolver;
    private readonly AgentProfileAccessResolver _profileAccessResolver;
    private readonly IProfileImageWriter _profileImages;
    private readonly IMessagingProfileImageResolver _profileImageResolver;

    public AvatarController(
        IWebHostEnvironment environment,
        ILogger<AvatarController> logger,
        AgentTrackingResolver trackingResolver,
        AgentProfileAccessResolver profileAccessResolver,
        IProfileImageWriter profileImages,
        IMessagingProfileImageResolver profileImageResolver)
    {
        _environment = environment;
        _logger = logger;
        _trackingResolver = trackingResolver;
        _profileAccessResolver = profileAccessResolver;
        _profileImages = profileImages;
        _profileImageResolver = profileImageResolver;
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
        return profile is null
            ? DefaultAvatarResult()
            : await ProfileImageResultAsync(
                MessagingParticipantTypes.Agent,
                profile.Id);
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

        var identities = await _profileImageResolver.ResolveIdentitiesAsync(
            [new MessagingParticipantReference(
                resolved.Profile.AgentUserId,
                MessagingParticipantTypes.Agent)],
            HttpContext.RequestAborted);
        return identities.TryGetValue(
            (resolved.Profile.AgentUserId.Trim().ToLowerInvariant(), MessagingParticipantTypes.Agent),
            out var identity)
            ? await ProfileImageResultAsync(identity)
            : DefaultAvatarResult();
    }

    [HttpGet("/api/v1/mobile/profile-images/{participantType}/{profileId:guid}")]
    [Authorize(Policy = AgentPortal.Mobile.MobileApiAuthorization.PolicyName)]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)]
    public async Task<IActionResult> MobileProfileImage(
        string participantType,
        Guid profileId)
    {
        if (profileId == Guid.Empty ||
            participantType is not (
                MessagingParticipantTypes.Agent or
                MessagingParticipantTypes.Client))
        {
            return NotFound();
        }

        return await ProfileImageResultAsync(
            participantType,
            profileId);
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

    private Task<IActionResult> ProfileImageResultAsync(
        string participantType,
        Guid profileId) =>
        ProfileImageResultAsync(
            new MessagingParticipantIdentity(
                string.Empty,
                participantType,
                profileId,
                string.Empty,
                null,
                string.Empty));

    private async Task<IActionResult> ProfileImageResultAsync(
        MessagingParticipantIdentity identity)
    {
        var image = await _profileImageResolver.ResolveAsync(identity, HttpContext.RequestAborted);
        return image is null
            ? DefaultAvatarResult()
            : File(image.Content, image.ContentType);
    }

}
