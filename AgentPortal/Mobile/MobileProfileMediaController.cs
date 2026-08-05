using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Mobile;

/// <summary>
/// Protected mobile transport for the canonical member profile image.
///
/// Storage, profile typing, validation, reads, and writes remain exclusively
/// owned by IMessagingProfileImageResolver / IProfileImageWriter.
/// </summary>
[ApiController]
[Route("api/v1/mobile/profile-images")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileProfileMediaController : ControllerBase
{
    private readonly IMessagingProfileImageResolver _profiles;

    public MobileProfileMediaController(
        IMessagingProfileImageResolver profiles)
    {
        _profiles = profiles;
    }

    [HttpGet("{participantType}/{profileId:guid}")]
    [ResponseCache(
        Duration = 86400,
        Location = ResponseCacheLocation.Client)]
    public async Task<IActionResult> Get(
        string participantType,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        if (profileId == Guid.Empty ||
            participantType is not (
                MessagingParticipantTypes.Agent or
                MessagingParticipantTypes.Client))
        {
            return NotFound();
        }

        var image = await _profiles.ResolveAsync(
            new MessagingParticipantIdentity(
                string.Empty,
                participantType,
                profileId,
                string.Empty,
                null,
                string.Empty),
            cancellationToken);

        return image is null
            ? NotFound()
            : File(image.Content, image.ContentType);
    }
}
