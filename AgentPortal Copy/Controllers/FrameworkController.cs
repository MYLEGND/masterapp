using Microsoft.AspNetCore.Mvc;
using AgentPortal.Filters;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Legend.Core.Branding;

namespace AgentPortal.Controllers
{
    public class Illustration
    {
        public string File { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
    }

    [AssistantBlock]
public class FrameworkController : Controller
    {
        public IActionResult Index()
        {
            var illustrationsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images", "illustrations");

            if (!Directory.Exists(illustrationsFolder))
                Directory.CreateDirectory(illustrationsFolder);

            // -----------------------------
            // Build the illustration list
            // -----------------------------
            var files = Directory.GetFiles(illustrationsFolder)
                .Where(f => f.EndsWith(".png") || f.EndsWith(".jpg") || f.EndsWith(".jpeg"))
                .Select(f => new Illustration
                {
                    File = Path.GetFileName(f),
                    Label = Path.GetFileNameWithoutExtension(f).Replace("-", " ").Trim()
                })
                .ToList();

            // -----------------------------
            // Force Legend™ Framework authoritative label and first position
            // -----------------------------
            var legendItem = files.FirstOrDefault(f =>
                f.File.Equals("Legend-Framework.png", System.StringComparison.OrdinalIgnoreCase)
            );

            if (legendItem != null)
            {
                legendItem.Label = Brand.FrameworkName; // authoritative
                files.Remove(legendItem);
                files.Insert(0, legendItem); // force first
            }

            // -----------------------------
            // Force page title to authoritative brand
            // -----------------------------
            ViewData["Title"] = Brand.FrameworkName;

            // -----------------------------
            // Pass illustrations to dropdown and search
            // -----------------------------
            // JS will read from dropdown, so no need to manually build search list
            ViewBag.Illustrations = files;

            return View();
        }
    }
}
