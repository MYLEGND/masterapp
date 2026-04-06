using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text;
using AgentPortal.Models.AgentDocuments;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers;

[Authorize]
public sealed class AgentDocumentsController : Controller
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string[] DocumentCategories =
    {
        "CE — Life & Health (AML)",
        "CE — Life & Health (Long Term Care)",
        "CE — Property & Casualty",
        "W-9 Tax Form",
        "Bank Letterhead / Voided Check",
        "E&O Policy",
        "Personal Insurance Policy",
        "Licensing / Appointment Document",
        "Contracting Packet",
        "Carrier Requirement",
        "Compliance Document",
        "Other"
    };

    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _configuration;
    private readonly BlobContainerClient? _blobContainer;

    public AgentDocumentsController(IWebHostEnvironment env, IConfiguration configuration)
    {
        _env = env;
        _configuration = configuration;
        _blobContainer = BuildBlobContainerClient(configuration);
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Forbid();
        }

        var records = await LoadRecordsAsync(userId);
        var yearGroups = records
            .OrderByDescending(x => x.DocumentYear)
            .ThenByDescending(x => x.UploadedUtc)
            .GroupBy(x => x.DocumentYear)
            .Select(g => new AgentDocumentYearGroupViewModel
            {
                Year = g.Key,
                Documents = g.Select(r => new AgentDocumentItemViewModel
                {
                    Id = r.Id,
                    Title = r.Title,
                    Category = r.Category,
                    OriginalFileName = r.OriginalFileName,
                    FileSizeBytes = r.FileSizeBytes,
                    UploadedUtc = r.UploadedUtc
                }).ToList()
            })
            .ToList();

        var vm = new AgentDocumentsViewModel
        {
            DefaultYear = DateTime.UtcNow.Year,
            Categories = DocumentCategories,
            YearGroups = yearGroups
        };

        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestFormLimits(MultipartBodyLengthLimit = 20 * 1024 * 1024)]
    public async Task<IActionResult> Upload(int documentYear, string? category, string? title, IFormFile? file)
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Forbid();
        }

        if (documentYear < 2000 || documentYear > 2100)
        {
            TempData["AgentDocsError"] = "Please choose a valid document year.";
            return RedirectToAction(nameof(Index));
        }

        if (file == null || file.Length == 0)
        {
            TempData["AgentDocsError"] = "Please choose a PDF to upload.";
            return RedirectToAction(nameof(Index));
        }

        if (file.Length > 20 * 1024 * 1024)
        {
            TempData["AgentDocsError"] = "Please keep uploads under 20 MB per file.";
            return RedirectToAction(nameof(Index));
        }

        var ext = Path.GetExtension(file.FileName);
        var isPdf = string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(file.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase);
        if (!isPdf)
        {
            TempData["AgentDocsError"] = "Only PDF uploads are allowed.";
            return RedirectToAction(nameof(Index));
        }

        var safeCategory = string.IsNullOrWhiteSpace(category) ? "Other" : category.Trim();
        var safeTitle = string.IsNullOrWhiteSpace(title)
            ? safeCategory
            : title.Trim();

        if (safeTitle.Length > 140)
        {
            safeTitle = safeTitle[..140];
        }

        var id = Guid.NewGuid().ToString("N");
        var storedFileName = $"{SlugifyFileName(safeTitle)}-{id}.pdf";

        var record = new AgentDocumentRecord
        {
            Id = id,
            DocumentYear = documentYear,
            Category = safeCategory,
            Title = safeTitle,
            OriginalFileName = Path.GetFileName(file.FileName),
            StoredFileName = storedFileName,
            FileSizeBytes = file.Length,
            UploadedUtc = DateTime.UtcNow
        };

        await using (var fs = file.OpenReadStream())
        {
            await SaveFileAsync(userId, storedFileName, fs);
        }

        var records = (await LoadRecordsAsync(userId)).ToList();
        records.Add(record);

        await SaveRecordsAsync(userId, records);

        TempData["AgentDocsSuccess"] = "Document uploaded.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Download(string id)
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Forbid();
        }

        var rec = (await LoadRecordsAsync(userId)).FirstOrDefault(x => x.Id == id);
        if (rec == null)
        {
            return NotFound();
        }

        if (UseBlobStorage())
        {
            var blobClient = _blobContainer!.GetBlobClient(GetFileBlobName(userId, rec.StoredFileName));
            if (!await blobClient.ExistsAsync())
            {
                return NotFound();
            }

            var streamResult = await blobClient.DownloadStreamingAsync();
            Response.Headers["Content-Disposition"] = "inline";
            return File(streamResult.Value.Content, "application/pdf", enableRangeProcessing: true);
        }

        var path = Path.Combine(GetUserRoot(userId), "files", rec.StoredFileName);
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        Response.Headers["Content-Disposition"] = "inline";
        return PhysicalFile(path, "application/pdf", enableRangeProcessing: true);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string id)
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Forbid();
        }

        var records = (await LoadRecordsAsync(userId)).ToList();
        var rec = records.FirstOrDefault(x => x.Id == id);
        if (rec == null)
        {
            TempData["AgentDocsError"] = "Document not found.";
            return RedirectToAction(nameof(Index));
        }

        records.Remove(rec);
        await SaveRecordsAsync(userId, records);

        if (UseBlobStorage())
        {
            var blobClient = _blobContainer!.GetBlobClient(GetFileBlobName(userId, rec.StoredFileName));
            await blobClient.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots);
        }
        else
        {
            var path = Path.Combine(GetUserRoot(userId), "files", rec.StoredFileName);
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }

        TempData["AgentDocsSuccess"] = "Document removed.";
        return RedirectToAction(nameof(Index));
    }

    private string? GetUserId()
    {
        return User.FindFirst("oid")?.Value
               ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
               ?? User.Identity?.Name;
    }

    private string GetBaseRoot()
    {
        var configured = Environment.GetEnvironmentVariable("LEGEND_AGENT_DOCUMENTS_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Directory.CreateDirectory(configured);
            return configured;
        }

        var fallback = Path.Combine(_env.ContentRootPath, "App_Data", "agent-documents");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private string GetUserRoot(string userId)
    {
        var safeUser = string.Concat(userId.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
        var root = Path.Combine(GetBaseRoot(), safeUser);
        Directory.CreateDirectory(root);
        return root;
    }

    private string GetIndexPath(string userId)
    {
        return Path.Combine(GetUserRoot(userId), "index.json");
    }

    private async Task<IReadOnlyList<AgentDocumentRecord>> LoadRecordsAsync(string userId)
    {
        if (UseBlobStorage())
        {
            var blobClient = _blobContainer!.GetBlobClient(GetIndexBlobName(userId));
            if (!await blobClient.ExistsAsync())
            {
                return Array.Empty<AgentDocumentRecord>();
            }

            var download = await blobClient.DownloadContentAsync();
            var blobJson = download.Value.Content.ToString();
            var blobRecords = JsonSerializer.Deserialize<List<AgentDocumentRecord>>(blobJson, JsonOptions);
            return blobRecords ?? new List<AgentDocumentRecord>();
        }

        var indexPath = GetIndexPath(userId);
        if (!System.IO.File.Exists(indexPath))
        {
            return Array.Empty<AgentDocumentRecord>();
        }

        var json = System.IO.File.ReadAllText(indexPath);
        var records = JsonSerializer.Deserialize<List<AgentDocumentRecord>>(json, JsonOptions);
        return records ?? new List<AgentDocumentRecord>();
    }

    private async Task SaveRecordsAsync(string userId, IReadOnlyList<AgentDocumentRecord> records)
    {
        if (UseBlobStorage())
        {
            await EnsureContainerExistsAsync();
            var blobClient = _blobContainer!.GetBlobClient(GetIndexBlobName(userId));
            var blobJson = JsonSerializer.Serialize(records, JsonOptions);
            await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(blobJson));
            await blobClient.UploadAsync(ms, overwrite: true);
            return;
        }

        var indexPath = GetIndexPath(userId);
        var json = JsonSerializer.Serialize(records, JsonOptions);
        System.IO.File.WriteAllText(indexPath, json);
    }

    private async Task SaveFileAsync(string userId, string storedFileName, Stream stream)
    {
        if (UseBlobStorage())
        {
            await EnsureContainerExistsAsync();
            var blobClient = _blobContainer!.GetBlobClient(GetFileBlobName(userId, storedFileName));
            await blobClient.UploadAsync(stream, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/pdf" }
            });
            return;
        }

        var root = GetUserRoot(userId);
        var filesDir = Path.Combine(root, "files");
        Directory.CreateDirectory(filesDir);
        var targetPath = Path.Combine(filesDir, storedFileName);
        await using var fs = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(fs);
    }

    private bool UseBlobStorage() => _blobContainer != null;

    private static BlobContainerClient? BuildBlobContainerClient(IConfiguration configuration)
    {
        var connString = configuration["AgentDocuments:StorageConnectionString"];
        var containerName = configuration["AgentDocuments:ContainerName"];
        if (!string.IsNullOrWhiteSpace(connString) && !string.IsNullOrWhiteSpace(containerName))
        {
            return new BlobContainerClient(connString, containerName);
        }

        var containerUrl = configuration["AgentDocuments:BlobContainerUrl"];
        if (!string.IsNullOrWhiteSpace(containerUrl))
        {
            return new BlobContainerClient(new Uri(containerUrl), new DefaultAzureCredential());
        }

        return null;
    }

    private async Task EnsureContainerExistsAsync()
    {
        if (_blobContainer == null) return;
        await _blobContainer.CreateIfNotExistsAsync(PublicAccessType.None);
    }

    private static string GetFileBlobName(string userId, string storedFileName)
    {
        var safeUser = GetSafeUser(userId);
        return $"users/{safeUser}/files/{storedFileName}";
    }

    private static string GetIndexBlobName(string userId)
    {
        var safeUser = GetSafeUser(userId);
        return $"users/{safeUser}/index.json";
    }

    private static string GetSafeUser(string userId)
    {
        return string.Concat(userId.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
    }

    private static string SlugifyFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "document";
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (ch == ' ' || ch == '-' || ch == '_')
            {
                sb.Append('-');
            }
        }

        var normalized = sb.ToString().Trim('-');
        if (normalized.Length > 80)
        {
            normalized = normalized[..80].Trim('-');
        }

        return string.IsNullOrWhiteSpace(normalized) ? "document" : normalized;
    }

    private sealed class AgentDocumentRecord
    {
        public string Id { get; set; } = string.Empty;
        public int DocumentYear { get; set; }
        public string Category { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public string StoredFileName { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public DateTime UploadedUtc { get; set; }
    }
}
