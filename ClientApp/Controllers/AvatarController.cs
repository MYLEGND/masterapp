using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ClientApp.Services;

namespace ClientApp.Controllers
{
    [Authorize]
    public class AvatarController : Controller
    {
        private readonly IWebHostEnvironment _env;
        private readonly EffectiveClientContextService _clientContext;

        public AvatarController(IWebHostEnvironment env, EffectiveClientContextService clientContext)
        {
            _env = env;
            _clientContext = clientContext;
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
                    return root;
                }
                catch { }
            }

            // Azure App Service exposes HOME as a persistent writable root that survives redeployment.
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                try
                {
                    var appServiceRoot = Path.GetFullPath(Path.Combine(home.Trim(), "avatars"));
                    Directory.CreateDirectory(appServiceRoot);
                    return appServiceRoot;
                }
                catch { }
            }

            var fallback = Path.Combine(_env.ContentRootPath, "App_Data", "avatars");
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        private async Task<string?> GetClientAvatarKeyAsync()
        {
            var context = await _clientContext.ResolveAsync(User, Request.Cookies);
            // Avatar files are keyed to the stable client profile identity, never
            // an Identity user, servicing agent, invitation sender, or email.
            return context is null ? null : context.ClientProfileId.ToString("D");
        }

        [HttpPost("/avatar/upload")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Upload(IFormFile photo)
        {
            var avatarKey = await GetClientAvatarKeyAsync();
            if (string.IsNullOrWhiteSpace(avatarKey))
            {
                return Forbid();
            }

            if (photo == null || photo.Length == 0)
            {
                return BadRequest(new { message = "Please choose an image file." });
            }

            if (photo.Length > 3 * 1024 * 1024)
            {
                return BadRequest(new { message = "Please upload an image under 3 MB." });
            }

            var allowed = new[] { "image/png", "image/jpeg", "image/jpg", "image/webp" };
            if (!allowed.Contains(photo.ContentType))
            {
                return BadRequest(new { message = "Only PNG, JPG, or WEBP images are allowed." });
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
            var filePath = Path.Combine(root, $"{avatarKey}{ext}");

            foreach (var existing in Directory.EnumerateFiles(root, $"{avatarKey}.*"))
            {
                System.IO.File.Delete(existing);
            }

            await using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await photo.CopyToAsync(stream);
            }

            return Ok(new { message = "Profile picture updated." });
        }

        [HttpGet("avatar/current")]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> Current()
        {
            var avatarKey = await GetClientAvatarKeyAsync();
            if (string.IsNullOrWhiteSpace(avatarKey))
                return Unauthorized();

            var root = GetAvatarRoot();
            var candidates = new[] { ".png", ".jpg", ".jpeg", ".webp" };
            foreach (var ext in candidates)
            {
                var path = Path.Combine(root, $"{avatarKey}{ext}");
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
