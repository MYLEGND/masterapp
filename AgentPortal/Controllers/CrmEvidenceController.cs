using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using AgentPortal.Services;
using Infrastructure.Crm;
using Infrastructure.Data;
using Infrastructure.Identity;
using Infrastructure.Security.UploadValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace AgentPortal.Controllers;

[Authorize]
[Route("CrmHistory/Evidence")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CrmEvidenceController(MasterAppDbContext db, EffectiveAgentContext agents, IConfiguration configuration, IWebHostEnvironment environment) : Controller
{
    private string Agent => (agents.EffectiveAgentOid ?? User.GetStableUserId() ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "").Trim();
    private Task<CrmHistoryRelationship?> Resolve(string kind, string id) => RestrictedAiIdentity.IsRestricted(User, configuration)
        ? Task.FromResult<CrmHistoryRelationship?>(null) : new CrmHistoryAccess(db).ResolveAsync(Agent, kind, id, HttpContext.RequestAborted);
    public sealed record Receipt(string Sha256, long Bytes, string ContentType, string OriginalName, string Source, string Completeness, Guid? PreviousVersion);

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(21 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 21 * 1024 * 1024)]
    public async Task<IActionResult> Upload(string kind, string id, Guid requestId, string source, string completeness, IFormFile? file, Guid? previousVersion = null)
    {
        if (!ModelState.IsValid || requestId == Guid.Empty || string.IsNullOrWhiteSpace(source) || source.Length > 500 ||
            string.IsNullOrWhiteSpace(completeness) || completeness.Length > 2000 || file is null) return BadRequest("Identify the original source, completeness and PDF evidence.");
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, HttpContext.RequestAborted) : null;
        var relationship = await Resolve(kind, id); if (relationship is null) return NotFound();
        var policy = UploadValidationPolicy.Pdf(20 * 1024 * 1024);
        if (!UploadValidator.ValidateMetadata(file.FileName, file.Length, policy).IsValid) return BadRequest("Provide a PDF no larger than 20 MB.");
        await using var input = file.OpenReadStream(); using var buffer = new MemoryStream();
        var block = new byte[65536]; int read;
        while ((read = await input.ReadAsync(block, HttpContext.RequestAborted)) != 0)
        {
            if (buffer.Length + read > policy.MaxSizeBytes) return BadRequest("The PDF exceeds 20 MB.");
            buffer.Write(block, 0, read);
        }
        var bytes = buffer.ToArray();
        if (!UploadValidator.ValidateContent(bytes, file.FileName, file.ContentType, policy).IsValid) return BadRequest("The document is not a supported PDF.");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var receipt = new Receipt(hash, bytes.LongLength, "application/pdf", Path.GetFileName(file.FileName), source.Trim(), completeness.Trim(), previousVersion);
        var content = JsonSerializer.Serialize(receipt); var sourceId = requestId.ToString("N");
        var entryId = CrmHistoryStore.EntryId(relationship.Key, "ProtectedDocument", sourceId);
        var prior = await db.Set<Domain.Entities.CrmHistoryEntry>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == entryId, HttpContext.RequestAborted);
        if (prior is not null) return prior.ContentJson == content ? Ok(new { saved = true, id = prior.Id }) : Conflict("This request already identifies different evidence; use a new version.");
        if (previousVersion.HasValue && !await db.Set<Domain.Entities.CrmHistoryEntry>().AnyAsync(x => x.Id == previousVersion && x.RelationshipKey == relationship.Key && x.Source == "ProtectedDocument", HttpContext.RequestAborted))
            return BadRequest("The original evidence version is unavailable.");
        try { await new CrmEvidenceStorage(configuration, environment).PutAsync(relationship.Key, hash, bytes, HttpContext.RequestAborted); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Azure.RequestFailedException)
        { return StatusCode(503, new { saved = false, reason = "Protected evidence storage is unavailable. No filed document was confirmed; retry the unchanged upload." }); }
        var entry = await new CrmHistoryStore(db).StageAsync(new() { RelationshipKey = relationship.Key, Source = "ProtectedDocument", SourceId = sourceId,
            Type = "Document", RecordedBy = User.GetStableUserId() ?? Agent, RecordedUtc = DateTime.UtcNow, TimePrecision = "unknown",
            RelatedEntryId = previousVersion, ContentJson = content, EvidenceStatus = "Uploaded original; source/completeness supplied by human" }, HttpContext.RequestAborted);
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        if (transaction is not null) await transaction.CommitAsync(HttpContext.RequestAborted);
        return Ok(new { saved = true, id = entry.Id });
    }

    [HttpGet("{entryId:guid}")]
    public async Task<IActionResult> Download(Guid entryId, string kind, string id)
    {
        var relationship = await Resolve(kind, id); if (relationship is null) return NotFound();
        var entry = await db.Set<Domain.Entities.CrmHistoryEntry>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == entryId && x.RelationshipKey == relationship.Key && x.Source == "ProtectedDocument", HttpContext.RequestAborted);
        if (entry is null) return NotFound();
        var receipt = JsonSerializer.Deserialize<Receipt>(entry.ContentJson)!;
        byte[] bytes;
        try { bytes = await new CrmEvidenceStorage(configuration, environment).ReadAsync(relationship.Key, receipt.Sha256, HttpContext.RequestAborted); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Azure.RequestFailedException)
        { return StatusCode(503, "This evidence version could not be verified. No partial or substitute document was returned."); }
        if (bytes.LongLength != receipt.Bytes) return StatusCode(503, "Evidence length verification failed.");
        await new CrmHistoryStore(db).StageAsync(new() { RelationshipKey = relationship.Key, Source = "EvidenceAccess", SourceId = Guid.NewGuid().ToString("N"),
            Type = "Access", RecordedBy = User.GetStableUserId() ?? Agent, OccurredUtc = DateTime.UtcNow, TimePrecision = "instant", RelatedEntryId = entryId,
            ContentJson = JsonSerializer.Serialize(new { evidenceId = entryId, action = "Authenticated download", receipt.Sha256 }) }, HttpContext.RequestAborted);
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(bytes, receipt.ContentType, receipt.OriginalName);
    }
}
