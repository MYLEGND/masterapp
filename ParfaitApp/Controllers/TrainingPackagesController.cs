using Microsoft.AspNetCore.Mvc;
namespace Protect_Website.Controllers;
public class TrainingPackagesController(ParfaitApp.Services.CommerceStoreContextService stores) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var store = await stores.ResolvePublicAsync(HttpContext, null, ct);
        return store is { IsParfait: true, TrainingPagePublished: true }
            ? LocalRedirect("/store/training") : NotFound();
    }
}
