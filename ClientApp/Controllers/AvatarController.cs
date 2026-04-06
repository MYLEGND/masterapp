using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ClientApp.Controllers
{
    [Authorize]
    public class AvatarController : Controller
    {
        private readonly IWebHostEnvironment _env;

        public AvatarController(IWebHostEnvironment env)
        {
            _env = env;
        }

        private string GetAvatarRoot()
        {
            var configured = Environment.GetEnvironmentVariable("LEGEND_AVATAR_ROOT");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                Directory.CreateDirectory(configured);
                return configured;
            }

            var fallback = Path.Combine(_env.ContentRootPath, "App_Data", "avatars");
            Directory.CreateDirectory(fallback);
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

        [HttpGet("avatar/current")]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Current()
        {
            var userId = GetUserId();
            if (string.IsNullOrWhiteSpace(userId))
                return Unauthorized();

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

                        const string fallbackSvg = """
<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 120 120'>
    <defs>
        <linearGradient id='g' x1='0' y1='0' x2='1' y2='1'>
            <stop offset='0%' stop-color='#0f1d38'/>
            <stop offset='100%' stop-color='#1f355f'/>
        </linearGradient>
    </defs>
    <rect width='120' height='120' rx='60' fill='url(#g)'/>
    <circle cx='60' cy='47' r='24' fill='#f1f5f9'/>
    <path d='M18 104c8-19 24-30 42-30s34 11 42 30' fill='#f1f5f9'/>
</svg>
""";

                        return Content(fallbackSvg, "image/svg+xml");
        }
    }
}
