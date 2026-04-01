using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Linq;

namespace ParfaitApp.Controllers
{
    public class ResourcesController : Controller
    {
        // GET: /Resources/
        public IActionResult Index()
        {
            ViewData["Title"] = "Resources";

            var resourcesFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "resources");

            if (!Directory.Exists(resourcesFolder))
            {
                Directory.CreateDirectory(resourcesFolder);
            }

            var files = Directory.GetFiles(resourcesFolder)
                                 .Select(f => new FileInfo(f))
                                 .OrderBy(f => f.Name)
                                 .Select(f => new
                                 {
                                     Name = Path.GetFileNameWithoutExtension(f.Name).Replace("-", " "),
                                     File = f.Name,
                                     Extension = f.Extension.ToLower()
                                 })
                                 .ToList();

            ViewBag.Resources = files;

            return View();
        }
    }
}
