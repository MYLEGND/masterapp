using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AgentPortal.Services.Tracking;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentPortal.Controllers
{
    [Authorize]
    [EnableRateLimiting("anon-public")]
    public class AvatarController : Controller
    {
        private readonly MasterAppDbContext _db;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<AvatarController> _logger;
        private readonly AgentTrackingResolver _trackingResolver;
        private static readonly object AvatarRootLogSync = new();
        private static string? _loggedConfiguredRoot;
        private static string? _loggedHomeFallbackRoot;
        private static string? _loggedFallbackRoot;

        public AvatarController(
            MasterAppDbContext db,
            IWebHostEnvironment env,
            ILogger<AvatarController> logger,
            AgentTrackingResolver trackingResolver)
        {
            _db = db;
            _env = env;
            _logger = logger;
            _trackingResolver = trackingResolver;
        }

        private void LogAvatarRootOnce(string root, bool fromEnvVar)
        {
            lock (AvatarRootLogSync)
            {
                if (fromEnvVar)
                {
                    if (string.Equals(_loggedConfiguredRoot, root, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    _loggedConfiguredRoot = root;
                    _logger.LogInformation(
                        "Avatar storage root resolved from LEGEND_AVATAR_ROOT: {AvatarRoot}",
                        root);
                    return;
                }

                if (string.Equals(_loggedFallbackRoot, root, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _loggedFallbackRoot = root;
                if (_env.IsDevelopment())
                {
                    _logger.LogWarning(
                        "LEGEND_AVATAR_ROOT is not set. Falling back to {AvatarRoot}. This path may be deployment-scoped.",
                        root);
                }
                else
                {
                    _logger.LogError(
                        "LEGEND_AVATAR_ROOT is not set in {EnvironmentName}. Avatars are using fallback path {AvatarRoot}, which can reset after publish.",
                        _env.EnvironmentName,
                        root);
                }
            }
        }

        private string GetAvatarRoot()
        {
            var configured = Environment.GetEnvironmentVariable("LEGEND_AVATAR_ROOT");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var expanded = Environment.ExpandEnvironmentVariables(configured.Trim());
                try
                {
                    var root = Path.GetFullPath(expanded);
                    Directory.CreateDirectory(root);
                    LogAvatarRootOnce(root, fromEnvVar: true);
                    return root;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Configured LEGEND_AVATAR_ROOT path failed: {ConfiguredAvatarRoot}. Falling back to a safe writable root.",
                        expanded);
                }
            }

            // Azure App Service exposes HOME as a persistent writable root.
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                try
                {
                    var appServiceRoot = Path.GetFullPath(Path.Combine(home.Trim(), "avatars"));
                    Directory.CreateDirectory(appServiceRoot);

                    lock (AvatarRootLogSync)
                    {
                        if (!string.Equals(_loggedHomeFallbackRoot, appServiceRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            _loggedHomeFallbackRoot = appServiceRoot;
                            _logger.LogInformation(
                                "Avatar storage root resolved from HOME fallback: {AvatarRoot}",
                                appServiceRoot);
                        }
                    }

                    return appServiceRoot;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "HOME fallback avatar path failed: {HomePath}. Falling back to application content root.",
                        home);
                }
            }

            var fallback = Path.Combine(_env.ContentRootPath, "App_Data", "avatars");
            Directory.CreateDirectory(fallback);
            LogAvatarRootOnce(fallback, fromEnvVar: false);
            return fallback;
        }

        private string? GetUserId()
        {
            var user = User;
            return user.FindFirst("oid")?.Value
                ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.Identity?.Name;
        }

        private string? GetUserUpn()
        {
            var user = User;
            return user.FindFirst("preferred_username")?.Value
                ?? user.FindFirst("upn")?.Value
                ?? user.FindFirst(ClaimTypes.Email)?.Value
                ?? user.Identity?.Name;
        }

        private bool TryResolveAvatarFile(string userId, out string? path, out string mime)
        {
            path = null;
            mime = "image/jpeg";

            if (string.IsNullOrWhiteSpace(userId))
            {
                return false;
            }

            var root = GetAvatarRoot();
            var candidates = new[] { ".png", ".jpg", ".jpeg", ".webp" };
            foreach (var ext in candidates)
            {
                var candidate = Path.Combine(root, $"{userId}{ext}");
                if (!System.IO.File.Exists(candidate))
                {
                    continue;
                }

                path = candidate;
                mime = ext.ToLowerInvariant() switch
                {
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    _ => "image/jpeg"
                };
                return true;
            }

            return false;
        }

        private IActionResult DefaultAvatarResult()
        {
            var defaultAvatar = Path.Combine(_env.WebRootPath, "images", "company-icons", "legend.png");
            if (System.IO.File.Exists(defaultAvatar))
            {
                return PhysicalFile(defaultAvatar, "image/png");
            }

            return NotFound();
        }

        private async Task<(string? Path, string Mime, string? UserId)> ResolveAvatarFileAsync(string? primaryUserId, string? agentUpn, CancellationToken ct)
        {
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string? userId)
            {
                var trimmed = userId?.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || !seen.Add(trimmed))
                {
                    return;
                }

                candidates.Add(trimmed);
            }

            AddCandidate(primaryUserId);

            var resolvedUpn = agentUpn?.Trim();
            if (string.IsNullOrWhiteSpace(resolvedUpn) && !string.IsNullOrWhiteSpace(primaryUserId))
            {
                var userKey = primaryUserId.Trim().ToLowerInvariant();
                resolvedUpn = await _db.AgentProfiles.AsNoTracking()
                    .Where(x => x.AgentUserId != null && x.AgentUserId.ToLower() == userKey)
                    .OrderByDescending(x => x.UpdatedUtc)
                    .Select(x => x.AgentUpn)
                    .FirstOrDefaultAsync(ct);
            }

            if (!string.IsNullOrWhiteSpace(resolvedUpn))
            {
                var sameProfileUpnIds = await _db.AgentProfiles.AsNoTracking()
                    .Where(x => x.AgentUpn == resolvedUpn)
                    .OrderByDescending(x => !string.IsNullOrWhiteSpace(x.FullName))
                    .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.Title))
                    .ThenByDescending(x => x.UpdatedUtc)
                    .Select(x => x.AgentUserId)
                    .ToListAsync(ct);

                foreach (var candidate in sameProfileUpnIds)
                {
                    AddCandidate(candidate);
                }

                var sameTrackingUpnIds = await _db.AgentTrackingProfiles.AsNoTracking()
                    .Where(x => x.AgentUpn == resolvedUpn)
                    .OrderByDescending(x => x.UpdatedUtc)
                    .Select(x => x.AgentUserId)
                    .ToListAsync(ct);

                foreach (var candidate in sameTrackingUpnIds)
                {
                    AddCandidate(candidate);
                }
            }

            foreach (var candidate in candidates)
            {
                if (TryResolveAvatarFile(candidate, out var path, out var mime) && path != null)
                {
                    return (path, mime, candidate);
                }
            }

            return (null, "image/jpeg", null);
        }

        [HttpGet]
        public IActionResult Edit()
        {
            var userId = GetUserId();
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Forbid();
            }

            ViewBag.AvatarUrl = Url.Action(nameof(Current));
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Upload(IFormFile photo)
        {
            var userId = GetUserId();
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Forbid();
            }

            if (photo == null || photo.Length == 0)
            {
                TempData["AvatarError"] = "Please choose an image file.";
                return RedirectToAction(nameof(Edit));
            }

            if (photo.Length > 3 * 1024 * 1024)
            {
                TempData["AvatarError"] = "Please upload an image under 3 MB.";
                return RedirectToAction(nameof(Edit));
            }

            var allowed = new[] { "image/png", "image/jpeg", "image/jpg", "image/webp" };
            if (!allowed.Contains(photo.ContentType))
            {
                TempData["AvatarError"] = "Only PNG, JPG, or WEBP images are allowed.";
                return RedirectToAction(nameof(Edit));
            }

            var ext = Path.GetExtension(photo.FileName);
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = photo.ContentType switch
                {
                    "image/png" => ".png",
                    "image/webp" => ".webp",
                    _ => ".jpg"
                };
            }

            try
            {
                var root = GetAvatarRoot();
                var filePath = Path.Combine(root, $"{userId}{ext}");

                foreach (var existing in Directory.EnumerateFiles(root, $"{userId}.*"))
                {
                    System.IO.File.Delete(existing);
                }

                await using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await photo.CopyToAsync(stream);
                }

                TempData["AvatarSuccess"] = "Profile picture updated.";
                return RedirectToAction(nameof(Edit));
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Avatar upload failed for user {UserId}.",
                    userId);
                TempData["AvatarError"] = "We couldn’t save your profile picture right now. Please try again.";
                return RedirectToAction(nameof(Edit));
            }
        }

        [HttpGet("avatar/current")]
        [AllowAnonymous]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> Current()
        {
            var userId = GetUserId();
            var upn = GetUserUpn();
            var resolved = await ResolveAvatarFileAsync(userId, upn, HttpContext.RequestAborted);
            if (resolved.Path != null)
            {
                return PhysicalFile(resolved.Path, resolved.Mime);
            }

            return DefaultAvatarResult();
        }

        [HttpGet("avatar/agent/{slug}")]
        [AllowAnonymous]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> Agent(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug))
            {
                return DefaultAvatarResult();
            }

            var resolved = await _trackingResolver.ResolveAsync(slug.Trim(), null, HttpContext.RequestAborted);
            if (!resolved.Found || string.IsNullOrWhiteSpace(resolved.Profile.AgentUserId))
            {
                return DefaultAvatarResult();
            }

            var avatar = await ResolveAvatarFileAsync(resolved.Profile.AgentUserId, resolved.Profile.AgentUpn, HttpContext.RequestAborted);
            if (avatar.Path != null)
            {
                return PhysicalFile(avatar.Path, avatar.Mime);
            }

            return DefaultAvatarResult();
        }
    }
}
