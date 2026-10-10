using Infrastructure.WebsiteRuntime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

/// <summary>
/// GET-only original Parfait editorial presentation through one source of Razor
/// truth. Requires prior verified-domain admission in BusinessWebsiteMiddleware.
/// </summary>
[Route("__parfait-preview")]
public sealed class ParfaitPublicPreviewController(
    CommerceStoreContextService stores, IWebHostEnvironment environment) : Controller
{
    [HttpGet("home")]
    public Task<IActionResult> Home(CancellationToken ct) => Page("Home", ct);
    [HttpGet("about")]
    public Task<IActionResult> About(CancellationToken ct) => Page("About", ct);
    [HttpGet("contact")]
    public Task<IActionResult> Contact(CancellationToken ct) => Page("Contact", ct);
    [HttpGet("training-packages")]
    public Task<IActionResult> TrainingPackages(CancellationToken ct) => Page("TrainingPackages", ct);
    [HttpGet("training")]
    public Task<IActionResult> Training(CancellationToken ct) => Page("Training", ct);
    [HttpGet("resources")]
    public Task<IActionResult> Resources(CancellationToken ct) => Page("Resources", ct);
    [HttpGet("support")]
    public Task<IActionResult> Support(CancellationToken ct) => Page("Support", ct);

    private async Task<IActionResult> Page(string viewName, CancellationToken ct)
    {
        if (!HttpContext.Items.ContainsKey(CommerceSharedHostPreviewGate.OriginalPagePathItem))
            return NotFound();

        var store = await stores.ResolvePublicAsync(HttpContext, null, ct);
        if (store is null || !store.IsParfait)
            return NotFound();

        ViewData["CommerceStoreContext"] = store;
        ViewData["ParfaitHostLayout"] = "~/Views/Shared/_CommerceParfaitLayout.cshtml";
        if (viewName == "Resources")
        {
            var folder = Path.Combine(environment.WebRootPath, "resources");
            ViewBag.Resources = Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                    .Select(f => new {
                        File = Path.GetFileName(f),
                        Name = Path.GetFileNameWithoutExtension(f).Replace("-", " "),
                        Extension = Path.GetExtension(f).ToLowerInvariant()
                    }).ToArray()
                : Array.Empty<object>();
        }
        if (viewName == "Training")
            ViewBag.Trainings = Array.Empty<object>();

        if (viewName == "TrainingPackages")
        {
            // A visual-only preview must never expose payment links.
            ViewBag.SquarePrelaunchLink = "#";
            ViewBag.SquareTrainingLink = "#";
            ViewBag.SquareFullLink = "#";
            ViewBag.SquareGymLink = "#";
            ViewBag.SquarePrelaunchLinkWealth = "#";
            ViewBag.SquareTrainingLinkWealth = "#";
            ViewBag.SquareFullLinkWealth = "#";
            ViewBag.SquareGymLinkWealth = "#";
        }
        return View("~/Views/ParfaitOriginal/" + viewName + ".cshtml");
    }
}
