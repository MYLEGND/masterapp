using ClientApp.Models;
using ClientApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClientApp.Controllers
{
    [Authorize]
    public class ResourcesController : Controller
    {
        private static readonly HashSet<string> AllowedPolicyExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf",
            ".png",
            ".jpg",
            ".jpeg"
        };

        private const long MaxPolicyFileBytes = 15 * 1024 * 1024;
        private readonly EffectiveClientContextService _clientContext;

        public ResourcesController(EffectiveClientContextService clientContext)
        {
            _clientContext = clientContext;
        }

        // GET: /Resources/
        public async Task<IActionResult> Index()
        {
            var context = await _clientContext.ResolveAsync(User, Request.Cookies);
            if (context == null) return Forbid();

            ViewData["Title"] = "Resources";

            var resourcesFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "resources");
            if (!Directory.Exists(resourcesFolder))
            {
                Directory.CreateDirectory(resourcesFolder);
            }

            var files = Directory.GetFiles(resourcesFolder)
                                 .Select(f => new FileInfo(f))
                                 .OrderBy(f => f.Name)
                                 .Select(f => new ResourceLibraryItem
                                 {
                                     Name = Path.GetFileNameWithoutExtension(f.Name).Replace("-", " "),
                                     File = f.Name,
                                     Extension = f.Extension.ToLower()
                                 })
                                 .ToList();

            var policyFolder = GetPolicyDocumentsFolder(context.ClientProfileId);
            Directory.CreateDirectory(policyFolder);

            var policyFiles = Directory.GetFiles(policyFolder)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => new PolicyDocumentItem
                {
                    FileName = file.Name,
                    DisplayName = GetDisplayName(file.Name),
                    Extension = file.Extension.ToLowerInvariant(),
                    SizeBytes = file.Length,
                    UploadedUtc = file.LastWriteTimeUtc,
                    PreviewUrl = Url.Action(nameof(PreviewPolicyDocument), new { fileName = file.Name }) ?? $"/Resources/PolicyDocuments/View/{Uri.EscapeDataString(file.Name)}",
                    DownloadUrl = Url.Action(nameof(DownloadPolicyDocument), new { fileName = file.Name }) ?? $"/Resources/PolicyDocuments/{Uri.EscapeDataString(file.Name)}"
                })
                .ToList();

            return View(new ResourcesIndexViewModel
            {
                Resources = files,
                PolicyDocuments = policyFiles
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadPolicyDocuments(List<IFormFile>? files)
        {
            var context = await _clientContext.ResolveAsync(User, Request.Cookies);
            if (context == null) return Forbid();

            if (files == null || files.Count == 0 || files.All(f => f == null || f.Length == 0))
            {
                TempData["ResourceUploadError"] = "Choose at least one PDF or image file to upload.";
                return RedirectToAction(nameof(Index));
            }

            var policyFolder = GetPolicyDocumentsFolder(context.ClientProfileId);
            Directory.CreateDirectory(policyFolder);

            var uploadedCount = 0;
            foreach (var file in files.Where(f => f != null && f.Length > 0))
            {
                var originalName = Path.GetFileName(file.FileName ?? string.Empty);
                var extension = Path.GetExtension(originalName).ToLowerInvariant();

                if (!AllowedPolicyExtensions.Contains(extension))
                {
                    TempData["ResourceUploadError"] = "Only PDF, PNG, JPG, and JPEG files are allowed for E-policy uploads.";
                    return RedirectToAction(nameof(Index));
                }

                if (file.Length > MaxPolicyFileBytes)
                {
                    TempData["ResourceUploadError"] = "Each file must be 15 MB or smaller.";
                    return RedirectToAction(nameof(Index));
                }

                var safeBaseName = Regex.Replace(Path.GetFileNameWithoutExtension(originalName), @"[^A-Za-z0-9\- _]", "").Trim();
                if (string.IsNullOrWhiteSpace(safeBaseName))
                {
                    safeBaseName = "policy-document";
                }

                var stampedFileName = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}__{safeBaseName}{extension}";
                var fullPath = Path.Combine(policyFolder, stampedFileName);

                await using var stream = System.IO.File.Create(fullPath);
                await file.CopyToAsync(stream);
                uploadedCount++;
            }

            TempData["ResourceUploadSuccess"] = uploadedCount == 1
                ? "Your E-policy document was uploaded successfully."
                : $"{uploadedCount} E-policy documents were uploaded successfully.";

            return RedirectToAction(nameof(Index));
        }

        [HttpGet("/Resources/PolicyDocuments/View/{fileName}")]
        public async Task<IActionResult> PreviewPolicyDocument(string fileName)
        {
            var context = await _clientContext.ResolveAsync(User, Request.Cookies);
            if (context == null) return Forbid();

            var safeFileName = Path.GetFileName(fileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                return NotFound();
            }

            var fullPath = Path.Combine(GetPolicyDocumentsFolder(context.ClientProfileId), safeFileName);
            if (!System.IO.File.Exists(fullPath))
            {
                return NotFound();
            }

            var extension = Path.GetExtension(safeFileName).ToLowerInvariant();
            var contentType = extension switch
            {
                ".pdf" => "application/pdf",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                _ => "application/octet-stream"
            };

            return PhysicalFile(fullPath, contentType, enableRangeProcessing: true);
        }

        [HttpGet("/Resources/PolicyDocuments/{fileName}")]
        public async Task<IActionResult> DownloadPolicyDocument(string fileName)
        {
            var context = await _clientContext.ResolveAsync(User, Request.Cookies);
            if (context == null) return Forbid();

            var safeFileName = Path.GetFileName(fileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                return NotFound();
            }

            var fullPath = Path.Combine(GetPolicyDocumentsFolder(context.ClientProfileId), safeFileName);
            if (!System.IO.File.Exists(fullPath))
            {
                return NotFound();
            }

            var extension = Path.GetExtension(safeFileName).ToLowerInvariant();
            var contentType = extension switch
            {
                ".pdf" => "application/pdf",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                _ => "application/octet-stream"
            };

            return PhysicalFile(fullPath, contentType, GetDisplayName(safeFileName), enableRangeProcessing: true);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeletePolicyDocument(string fileName)
        {
            var context = await _clientContext.ResolveAsync(User, Request.Cookies);
            if (context == null) return Forbid();

            var safeFileName = Path.GetFileName(fileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                TempData["ResourceUploadError"] = "That document could not be deleted.";
                return RedirectToAction(nameof(Index));
            }

            var fullPath = Path.Combine(GetPolicyDocumentsFolder(context.ClientProfileId), safeFileName);
            if (!System.IO.File.Exists(fullPath))
            {
                TempData["ResourceUploadError"] = "That document could not be found.";
                return RedirectToAction(nameof(Index));
            }

            System.IO.File.Delete(fullPath);
            TempData["ResourceUploadSuccess"] = "The selected E-policy document was deleted.";
            return RedirectToAction(nameof(Index));
        }

        private static string GetDisplayName(string storedFileName)
        {
            var fileName = Path.GetFileName(storedFileName);
            var separatorIndex = fileName.IndexOf("__", StringComparison.Ordinal);
            return separatorIndex >= 0 && separatorIndex + 2 < fileName.Length
                ? fileName[(separatorIndex + 2)..]
                : fileName;
        }

        private static string GetPolicyDocumentsFolder(Guid clientProfileId)
        {
            return Path.Combine(
                Directory.GetCurrentDirectory(),
                "App_Data",
                "client-uploads",
                "policy-documents",
                clientProfileId.ToString("N"));
        }
    }
}
