using Microsoft.AspNetCore.Mvc;

namespace ClientApp.Controllers;

public class DashboardController : Controller
{
    public IActionResult Index()
    {
        return View();
    }
}
