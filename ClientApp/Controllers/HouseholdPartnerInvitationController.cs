using Infrastructure.Households;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClientApp.Controllers;

[AllowAnonymous]
[Route("household/partner")]
public sealed class HouseholdPartnerInvitationController : Controller
{
    private readonly IHouseholdMembershipService _households;

    public HouseholdPartnerInvitationController(IHouseholdMembershipService households)
    {
        _households = households;
    }

    [HttpGet("accept")]
    public IActionResult AcceptPage([FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return BadRequest("A household invitation token is required.");

        ViewData["InvitationToken"] = token;
        return View("Accept");
    }

    [HttpPost("accept")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Accept(
        [FromQuery] string token,
        [FromBody] AcceptPartnerInvitationRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest("Partner details are required.");

        try
        {
            var accepted = await _households.AcceptPartnerInvitationAsync(
                token,
                new AcceptPartnerInvitationCommand(
                    request.FirstName,
                    request.LastName,
                    request.Email),
                cancellationToken);

            return Ok(new
            {
                ok = true,
                profileId = accepted.Profile.Id,
                email = accepted.Profile.NormalizedEmail,
                signInUrl = "/Account/SignIn"
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    public sealed class AcceptPartnerInvitationRequest
    {
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Email { get; set; } = "";
    }
}
