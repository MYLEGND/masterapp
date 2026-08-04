using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ClientApp.Services;
using Domain.Messaging;
using Infrastructure.Messaging;

namespace ClientApp.Controllers
{
    [Authorize]
    public class AvatarController : Controller
    {
        private const string FallbackAvatarSvg = """
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

        private readonly EffectiveClientContextService _clientContext;
        private readonly IProfileImageWriter _profileImages;
        private readonly IMessagingProfileImageResolver _profileImageResolver;

        public AvatarController(
            EffectiveClientContextService clientContext,
            IProfileImageWriter profileImages,
            IMessagingProfileImageResolver profileImageResolver)
        {
            _clientContext = clientContext;
            _profileImages = profileImages;
            _profileImageResolver = profileImageResolver;
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

            await using var stream = new MemoryStream();
            await photo.CopyToAsync(stream, HttpContext.RequestAborted);
            var bytes = stream.ToArray();

            var result = await _profileImages.UpdateAsync(
                new MessagingParticipantIdentity(
                    string.Empty,
                    MessagingParticipantTypes.Client,
                    clientProfileId.Value,
                    string.Empty,
                    null,
                    string.Empty),
                bytes,
                HttpContext.RequestAborted);
            if (!result.Succeeded)
                return BadRequest(new { message = result.ErrorMessage ?? "Only valid PNG, JPG, or WEBP images are allowed." });

            return Ok(new { message = "Profile picture updated." });
        }

        [HttpGet("avatar/current")]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> Current()
        {
            var clientProfileId = await GetClientProfileIdAsync();
            if (clientProfileId is null)
                return Unauthorized();

            return await ProfileImageResultAsync(
                MessagingParticipantTypes.Client,
                clientProfileId.Value);
        }

        [HttpGet("avatar/agent/current")]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> AgentCurrent()
        {
            var context = await _clientContext.ResolveAsync(User, Request.Cookies, allowRelink: false);
            if (context is not { IsAgentView: true, AgentProfileId: { } agentProfileId })
                return Unauthorized();

            return await ProfileImageResultAsync(
                MessagingParticipantTypes.Agent,
                agentProfileId);
        }

        private async Task<IActionResult> ProfileImageResultAsync(
            string participantType,
            Guid profileId)
        {
            var image = await _profileImageResolver.ResolveAsync(
                new MessagingParticipantIdentity(
                    string.Empty,
                    participantType,
                    profileId,
                    string.Empty,
                    null,
                    string.Empty),
                HttpContext.RequestAborted);
            if (image is not null)
                return File(image.Content, image.ContentType);

            return Content(FallbackAvatarSvg, "image/svg+xml");
        }
    }
}
