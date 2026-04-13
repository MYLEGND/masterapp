using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace AgentPortal.Controllers
{
[Authorize]
[EnableRateLimiting("anon-public")]
    public class AvatarController : Controller
    {
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<AvatarController> _logger;
        private static readonly object AvatarRootLogSync = new();
        private static string? _loggedConfiguredRoot;
        private static string? _loggedHomeFallbackRoot;
        private static string? _loggedFallbackRoot;

        public AvatarController(IWebHostEnvironment env, ILogger<AvatarController> logger)
        {
            _env = env;
            _logger = logger;
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
        public IActionResult Current()
        {
            var userId = GetUserId();
            if (!string.IsNullOrWhiteSpace(userId))
            {
                var root = GetAvatarRoot();
                var candidates = new[] { ".png", ".jpg", ".jpeg", ".webp" };
                foreach (var ext in candidates)
                {
                    var path = Path.Combine(root, $"{userId}{ext}");
                    if (System.IO.File.Exists(path))
                    {
                        var mime = ext.ToLowerInvariant() switch
                        {
                            ".png" => "image/png",
                            ".webp" => "image/webp",
                            _ => "image/jpeg"
                        };
                        return PhysicalFile(path, mime);
                    }
                }
            }

            // Fallback to default avatar to avoid 404 noise (even if unauthenticated)
            var defaultAvatar = Path.Combine(_env.WebRootPath, "images", "company-icons", "legend.png");
            if (System.IO.File.Exists(defaultAvatar))
            {
                return PhysicalFile(defaultAvatar, "image/png");
            }

            return NotFound();
        }
    }
}
