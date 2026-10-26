using Microsoft.AspNetCore.Mvc;
namespace Protect_Website.Controllers;
public class TrainingPackagesController : Controller
{
    [HttpGet]
    public IActionResult Index() => LocalRedirect("/store/training");
}
