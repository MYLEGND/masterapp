using AgentPortal.Models;
using AgentPortal.Filters;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgentPortal.Controllers
{
    [AssistantBlock]
public class ResourcesController : Controller
    {
        // GET: /Resources/
        public IActionResult Index()
        {
            ViewData["Title"] = "Resources";

            var resourcesFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "resources");
            var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
                ".txt", ".csv", ".png", ".jpg", ".jpeg", ".gif"
            };

            if (!Directory.Exists(resourcesFolder))
            {
                Directory.CreateDirectory(resourcesFolder);
            }

            var files = Directory.GetFiles(resourcesFolder)
                                 .Where(f => allowedExtensions.Contains(Path.GetExtension(f)))
                                 .Select(f => new FileInfo(f))
                                 .OrderBy(f => f.Name)
                                 .Select(f => new ResourceFileViewModel
                                 {
                                     Name = Path.GetFileNameWithoutExtension(f.Name).Replace("-", " "),
                                     File = f.Name,
                                     Extension = f.Extension.ToLowerInvariant()
                                 })
                                 .ToList();

            return View(files);
        }
    }
}
