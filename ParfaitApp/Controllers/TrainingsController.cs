using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Linq;

namespace ParfaitApp.Controllers
{
    public class TrainingController(ParfaitApp.Services.CommerceStoreContextService stores) : Controller
    {
        // GET: /Training/
        public async Task<IActionResult> Index(CancellationToken ct)
        {
            var store = await stores.ResolvePublicAsync(HttpContext, null, ct);
            if (store is not { IsParfait: true, TrainingPagePublished: true }) return NotFound();
            ViewData["Title"] = "Training";

            // Path to the wwwroot/training folder
            var trainingFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "trainings");

            // Make sure folder exists
            if (!Directory.Exists(trainingFolder))
            {
                Directory.CreateDirectory(trainingFolder);
            }

            // Get all files (pdf, mp4, etc.)
            var files = Directory.GetFiles(trainingFolder)
                                 .Select(f => new FileInfo(f))
                                 .OrderBy(f => f.Name)
                                 .Select(f => new
                                 {
                                     // Display name: clean filename (replace dashes with spaces)
                                     Name = Path.GetFileNameWithoutExtension(f.Name).Replace("-", " "),
                                     File = f.Name
                                 })
                                 .ToList();

            // Pass to view
            ViewBag.Trainings = files;

            return View();
        }
    }
}
