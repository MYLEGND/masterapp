using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ClientApp.Controllers;

public class DebugController : Controller
{
    [HttpGet("/debug/whoami")]
    public IActionResult WhoAmI()
    {
        string? oid = User.FindFirstValue("oid");
        string? sub = User.FindFirstValue("sub");
        string? name = User.Identity?.Name;
        string? upn = User.FindFirstValue("preferred_username") ?? User.FindFirstValue("upn");

        return Content(
            $"Name: {name}\n" +
            $"preferred_username/upn: {upn}\n" +
            $"oid: {oid}\n" +
            $"sub: {sub}\n"
        );
    }
}
