using ClientApp.Models;
using ClientApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClientApp.Controllers
{
    [Authorize]
    public class ResourcesController : Controller
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

        private static readonly IReadOnlyList<PolicyTypeFamilyItem> PolicyTypeFamilies = new List<PolicyTypeFamilyItem>
        {
            new()
            {
                Family = "Life & Income Protection",
                Types = new[]
                {
                    "Life Insurance",
                    "Mortgage Protection",
                    "Disability Insurance",
                    "Critical Illness",
                    "Long Term Care",
                    "Final Expense",
                    "Annuity"
                }
            },
            new()
            {
                Family = "Health & Medicare",
                Types = new[]
                {
                    "Medicare Advantage",
                    "Medicare Supplement",
                    "Medicare Part D",
                    "Hospital Indemnity",
                    "Dental / Vision / Hearing"
                }
            },
            new()
            {
                Family = "Personal Property & Casualty",
                Types = new[]
                {
                    "Auto Insurance",
                    "Homeowners Insurance",
                    "Renters Insurance",
                    "Condo Insurance",
                    "Flood Insurance",
                    "Umbrella Liability"
                }
            },
            new()
            {
                Family = "Business & Commercial",
                Types = new[]
                {
                    "Business Owner Policy (BOP)",
                    "General Liability",
                    "Professional Liability (E&O)",
                    "Workers Compensation",
                    "Commercial Auto",
                    "Commercial Property",
                    "Cyber Liability",
                    "Commercial Umbrella / Excess"
                }
            },
            new()
            {
                Family = "Specialty",
                Types = new[]
                {
                    "Identity Theft Protection",
                    "Pet Insurance",
                    "Travel Insurance",
                    "Other Policy"
                }
            }
        };

        private static readonly HashSet<string> ValidPolicyTypes = PolicyTypeFamilies
            .SelectMany(f => f.Types)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

            var records = LoadPolicyRecords(policyFolder);
            var policyFiles = Directory.GetFiles(policyFolder)
                .Where(path => !path.EndsWith("index.json", StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .ToList();

            var recordsByFile = records.ToDictionary(r => r.StoredFileName, StringComparer.OrdinalIgnoreCase);
            var updatedRecords = new List<PolicyDocumentRecord>();

            foreach (var file in policyFiles)
            {
                if (!recordsByFile.TryGetValue(file.Name, out var record))
                {
                    record = new PolicyDocumentRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        StoredFileName = file.Name,
                        DisplayName = Path.GetFileNameWithoutExtension(GetDisplayName(file.Name)).Replace("-", " ").Trim(),
                        PolicyType = "Other Policy",
                        PolicyFamily = GetPolicyFamily("Other Policy"),
                        PolicyYear = file.LastWriteTimeUtc.Year,
                        UploadedUtc = file.LastWriteTimeUtc,
                        SizeBytes = file.Length,
                        Extension = file.Extension.ToLowerInvariant()
                    };
                }

                record.SizeBytes = file.Length;
                record.Extension = file.Extension.ToLowerInvariant();
                if (record.PolicyYear <= 0)
                {
                    record.PolicyYear = file.LastWriteTimeUtc.Year;
                }

                updatedRecords.Add(record);
            }

            if (updatedRecords.Count != records.Count)
            {
                SavePolicyRecords(policyFolder, updatedRecords);
            }

            var policyDocuments = updatedRecords
                .OrderByDescending(record => record.PolicyYear)
                .ThenByDescending(record => record.UploadedUtc)
                .Select(record => new PolicyDocumentItem
                {
                    FileName = record.StoredFileName,
                    DisplayName = record.DisplayName,
                    PolicyType = record.PolicyType,
                    PolicyFamily = record.PolicyFamily,
                    PolicyYear = record.PolicyYear,
                    Extension = record.Extension,
                    SizeBytes = record.SizeBytes,
                    UploadedUtc = record.UploadedUtc,
                    PreviewUrl = Url.Action(nameof(PreviewPolicyDocument), new { fileName = record.StoredFileName }) ?? $"/Resources/PolicyDocuments/View/{Uri.EscapeDataString(record.StoredFileName)}",
                    DownloadUrl = Url.Action(nameof(DownloadPolicyDocument), new { fileName = record.StoredFileName }) ?? $"/Resources/PolicyDocuments/{Uri.EscapeDataString(record.StoredFileName)}"
                })
                .ToList();

            return View(new ResourcesIndexViewModel
            {
                Resources = files,
                PolicyDocuments = policyDocuments,
                PolicyTypeFamilies = PolicyTypeFamilies
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadPolicyDocuments(int policyYear, string? policyType, string? displayName, IFormFile? file)
        {
            var context = await _clientContext.ResolveAsync(User, Request.Cookies);
            if (context == null) return Forbid();

            if (policyYear < 2000 || policyYear > 2100)
            {
                TempData["ResourceUploadError"] = "Please choose a valid policy year.";
                return RedirectToAction(nameof(Index));
            }

            var safePolicyType = string.IsNullOrWhiteSpace(policyType) ? "Other Policy" : policyType.Trim();
            if (!ValidPolicyTypes.Contains(safePolicyType))
            {
                TempData["ResourceUploadError"] = "Please choose a valid policy type.";
                return RedirectToAction(nameof(Index));
            }

            if (file == null || file.Length == 0)
            {
                TempData["ResourceUploadError"] = "Choose one policy document to upload.";
                return RedirectToAction(nameof(Index));
            }

            var policyFolder = GetPolicyDocumentsFolder(context.ClientProfileId);
            Directory.CreateDirectory(policyFolder);

            var originalName = Path.GetFileName(file.FileName ?? string.Empty);
            var extension = Path.GetExtension(originalName).ToLowerInvariant();

            if (!AllowedPolicyExtensions.Contains(extension))
            {
                TempData["ResourceUploadError"] = "Only PDF, PNG, JPG, and JPEG files are allowed for policy uploads.";
                return RedirectToAction(nameof(Index));
            }

            if (file.Length > MaxPolicyFileBytes)
            {
                TempData["ResourceUploadError"] = "Each file must be 15 MB or smaller.";
                return RedirectToAction(nameof(Index));
            }

            var safeDisplayName = string.IsNullOrWhiteSpace(displayName)
                ? safePolicyType
                : displayName.Trim();

            safeDisplayName = Regex.Replace(safeDisplayName, @"\s+", " ").Trim();
            if (safeDisplayName.Length > 140)
            {
                safeDisplayName = safeDisplayName[..140];
            }

            var safeBaseName = Regex.Replace(safeDisplayName, @"[^A-Za-z0-9\- _]", "").Trim();
            if (string.IsNullOrWhiteSpace(safeBaseName))
            {
                safeBaseName = "policy-document";
            }

            var id = Guid.NewGuid().ToString("N");
            var stampedFileName = $"{SlugifyFileName(safeBaseName)}-{id}{extension}";
            var fullPath = Path.Combine(policyFolder, stampedFileName);

            await using (var stream = System.IO.File.Create(fullPath))
            {
                await file.CopyToAsync(stream);
            }

            var records = LoadPolicyRecords(policyFolder);
            records.Add(new PolicyDocumentRecord
            {
                Id = id,
                StoredFileName = stampedFileName,
                DisplayName = safeDisplayName,
                PolicyType = safePolicyType,
                PolicyFamily = GetPolicyFamily(safePolicyType),
                PolicyYear = policyYear,
                UploadedUtc = DateTime.UtcNow,
                SizeBytes = file.Length,
                Extension = extension
            });
            SavePolicyRecords(policyFolder, records);

            TempData["ResourceUploadSuccess"] = "Your policy document was uploaded successfully.";

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

            Response.Headers["Content-Disposition"] = "inline";

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
            var records = LoadPolicyRecords(GetPolicyDocumentsFolder(context.ClientProfileId));
            records.RemoveAll(r => string.Equals(r.StoredFileName, safeFileName, StringComparison.OrdinalIgnoreCase));
            SavePolicyRecords(GetPolicyDocumentsFolder(context.ClientProfileId), records);

            TempData["ResourceUploadSuccess"] = "The selected policy document was deleted.";
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

        private static string SlugifyFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "policy-document";
            }

            var slug = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
            if (slug.Length > 70)
            {
                slug = slug[..70].Trim('-');
            }

            return string.IsNullOrWhiteSpace(slug) ? "policy-document" : slug;
        }

        private static string GetPolicyFamily(string policyType)
        {
            var family = PolicyTypeFamilies.FirstOrDefault(f => f.Types.Contains(policyType, StringComparer.OrdinalIgnoreCase));
            return family?.Family ?? "Specialty";
        }

        private static List<PolicyDocumentRecord> LoadPolicyRecords(string policyFolder)
        {
            var indexPath = Path.Combine(policyFolder, "index.json");
            if (!System.IO.File.Exists(indexPath))
            {
                return new List<PolicyDocumentRecord>();
            }

            var json = System.IO.File.ReadAllText(indexPath);
            return JsonSerializer.Deserialize<List<PolicyDocumentRecord>>(json, JsonOptions) ?? new List<PolicyDocumentRecord>();
        }

        private static void SavePolicyRecords(string policyFolder, List<PolicyDocumentRecord> records)
        {
            var indexPath = Path.Combine(policyFolder, "index.json");
            var json = JsonSerializer.Serialize(records, JsonOptions);
            System.IO.File.WriteAllText(indexPath, json);
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

        private sealed class PolicyDocumentRecord
        {
            public string Id { get; set; } = string.Empty;
            public string StoredFileName { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string PolicyFamily { get; set; } = string.Empty;
            public string PolicyType { get; set; } = string.Empty;
            public int PolicyYear { get; set; }
            public DateTime UploadedUtc { get; set; }
            public long SizeBytes { get; set; }
            public string Extension { get; set; } = string.Empty;
        }
    }
}
