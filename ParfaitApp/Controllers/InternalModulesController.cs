using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ParfaitApp.Controllers;

[Authorize]
[Route("internal")]
public sealed class InternalModulesController : Controller
{
    [HttpGet("commerce")]
    public IActionResult Commerce() => View();

    [HttpGet("customers")]
    public IActionResult Customers() => View();

    [HttpGet("marketing")]
    public IActionResult Marketing() => View();

    [HttpGet("content")]
    public IActionResult Content() => View();

    [HttpGet("analytics")]
    public IActionResult Analytics() => View();
}
