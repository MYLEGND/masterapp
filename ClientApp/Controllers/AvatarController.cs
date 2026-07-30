using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ClientApp.Services;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ClientApp.Controllers
{
    [Authorize]
    public class AvatarController : Controller
    {
        private readonly MasterAppDbContext _db;
        private readonly EffectiveClientContextService _clientContext;

        public AvatarController(MasterAppDbContext db, EffectiveClientContextService clientContext)
        {
            _db = db;
            _clientContext = clientContext;
        }

        private async Task<Guid?> GetClientProfileIdAsync()
        {
            var context = await _clientContext.ResolveAsync(User, Request.Cookies);
            return context?.ClientProfileId;
        }

        [HttpPost("/avatar/upload")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Upload(IFormFile photo)
        {
            var clientProfileId = await GetClientProfileIdAsync();
            if (clientProfileId is null)
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

            var profile = await _db.ClientProfiles
                .SingleOrDefaultAsync(x => x.Id == clientProfileId.Value, HttpContext.RequestAborted);
            if (profile is null)
                return Forbid();

            await using var stream = new MemoryStream();
            await photo.CopyToAsync(stream, HttpContext.RequestAborted);
            var bytes = stream.ToArray();

            // Verify the actual bytes are a real image of an allowed type — the
            // client-declared Content-Type alone is not trustworthy.
            var validation = global::Infrastructure.Security.UploadValidation.UploadValidator.ValidateImageContent(
                bytes,
                global::Infrastructure.Security.UploadValidation.UploadValidationPolicy.Images(3 * 1024 * 1024));
            if (!validation.IsValid)
                return BadRequest(new { message = "Only valid PNG, JPG, or WEBP images are allowed." });

            profile.ProfileImageContent = bytes;
            profile.ProfileImageContentType = photo.ContentType == "image/jpg"
                ? "image/jpeg"
                : photo.ContentType;
            profile.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(HttpContext.RequestAborted);

            return Ok(new { message = "Profile picture updated." });
        }

        [HttpGet("avatar/current")]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> Current()
        {
            var clientProfileId = await GetClientProfileIdAsync();
            if (clientProfileId is null)
                return Unauthorized();

            var profile = await _db.ClientProfiles
                .AsNoTracking()
                .Where(x => x.Id == clientProfileId.Value)
                .Select(x => new { x.ProfileImageContent, x.ProfileImageContentType })
                .SingleOrDefaultAsync(HttpContext.RequestAborted);
            if (profile?.ProfileImageContent is { Length: > 0 } &&
                profile.ProfileImageContentType is "image/png" or "image/jpeg" or "image/webp")
                return File(profile.ProfileImageContent, profile.ProfileImageContentType);

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
