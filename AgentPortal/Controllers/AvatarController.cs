using System.Security.Claims;
using AgentPortal.Services.Tracking;
using Infrastructure.Data;
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
    private static readonly string[] SupportedImageContentTypes = ["image/png", "image/jpeg", "image/jpg", "image/webp"];

    private readonly MasterAppDbContext _db;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AvatarController> _logger;
    private readonly AgentTrackingResolver _trackingResolver;

    public AvatarController(
        MasterAppDbContext db,
        IWebHostEnvironment environment,
        ILogger<AvatarController> logger,
        AgentTrackingResolver trackingResolver)
    {
        _db = db;
        _environment = environment;
        _logger = logger;
        _trackingResolver = trackingResolver;
    }

    [HttpGet]
    public IActionResult Edit() =>
        string.IsNullOrWhiteSpace(GetUserId())
            ? Forbid()
            : RedirectToAction("ManageProfile", "Account");

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

        if (photo.Length > 3 * 1024 * 1024)
        {
            TempData["AvatarError"] = "Please upload an image under 3 MB.";
            return RedirectToAction("ManageProfile", "Account");
        }

        if (!SupportedImageContentTypes.Contains(photo.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            TempData["AvatarError"] = "Only PNG, JPG, or WEBP images are allowed.";
            return RedirectToAction("ManageProfile", "Account");
        }

        try
        {
            await using var stream = new MemoryStream();
            await photo.CopyToAsync(stream, HttpContext.RequestAborted);
            profile.ProfileImageContent = stream.ToArray();
            profile.ProfileImageContentType = NormalizeImageContentType(photo.ContentType);
            profile.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(HttpContext.RequestAborted);

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
        var agentUserId = Normalize(GetUserId());
        if (string.IsNullOrWhiteSpace(agentUserId))
            return null;

        return await _db.AgentProfiles.SingleOrDefaultAsync(
            profile => profile.IsActive && profile.AgentUserId.ToLower() == agentUserId,
            cancellationToken);
    }

    private IActionResult DefaultAvatarResult()
    {
        var defaultAvatar = Path.Combine(_environment.WebRootPath, "images", "company-icons", "legend.png");
        return System.IO.File.Exists(defaultAvatar)
            ? PhysicalFile(defaultAvatar, "image/png")
            : NotFound();
    }

    private string? GetUserId() =>
        User.FindFirst("oid")?.Value ??
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
        User.Identity?.Name;

    private static bool HasImage(Domain.Entities.AgentProfile? profile) =>
        profile?.ProfileImageContent is { Length: > 0 } &&
        profile.ProfileImageContentType is "image/png" or "image/jpeg" or "image/webp";

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string NormalizeImageContentType(string value) =>
        string.Equals(value, "image/jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : value;
}
