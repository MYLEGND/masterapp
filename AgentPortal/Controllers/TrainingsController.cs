using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using System.IO;
using System.Linq;

namespace AgentPortal.Controllers
{
    public class TrainingController : Controller
    {
        private readonly IWebHostEnvironment _env;

        public TrainingController(IWebHostEnvironment env)
        {
            _env = env;
        }

        // GET: /Training
        [HttpGet]
        public IActionResult Index()
        {
            ViewData["Title"] = "Training";

            // Guaranteed correct path to wwwroot
            var trainingFolder = Path.Combine(_env.WebRootPath, "trainings");

            // Ensure folder exists
            if (!Directory.Exists(trainingFolder))
                Directory.CreateDirectory(trainingFolder);

            // Only list safe file types (add more if needed)
            var allowedExtensions = new[] { ".pdf", ".mp4", ".mov", ".png", ".jpg", ".jpeg" };

            var files = Directory.EnumerateFiles(trainingFolder)
                .Where(f => allowedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.Name)
                .Select(f => new
                {
                    Name = Path.GetFileNameWithoutExtension(f.Name).Replace("-", " "),
                    File = f.Name
                })
                .ToList();

            ViewBag.Trainings = files;

            return View();
        }
    }
}