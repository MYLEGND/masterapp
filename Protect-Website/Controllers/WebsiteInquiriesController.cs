using System.ComponentModel.DataAnnotations;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Security;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ProtectWebsite.Controllers;

[ApiController]
[Route("api/website-inquiries")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WebsiteInquiriesController : ControllerBase
{
    private readonly MasterAppDbContext _db;
    private readonly WebsiteEditorTicketProtector _tickets;
    private readonly IConfiguration _configuration;
    private readonly WebsiteDomainService _domains;

    public WebsiteInquiriesController(MasterAppDbContext db, WebsiteEditorTicketProtector tickets, IConfiguration configuration, WebsiteDomainService domains)
    {
        _db = db;
        _tickets = tickets;
        _configuration = configuration;
        _domains = domains;
    }

    public sealed record PublicRequest(Guid SubmissionId, string Name, string Email, string Message, string SourcePath, bool Consent);

    [HttpPost("public")]
    [RequestSizeLimit(32768)]
    [EnableRateLimiting(PlatformRateLimiting.PublicFormPolicy)]
    public async Task<IActionResult> Submit([FromBody] PublicRequest request, CancellationToken cancellationToken)
    {
        // The browser's verified domain identifies the business. No caller-supplied
        // business/agent ID, page URL, or insurance lead fallback can choose a recipient.
        if (!Uri.TryCreate(Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || !origin.IsDefaultPort ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
            return BadRequest(new { error = "verified_website_origin_required" });
        var businessId = await _domains.ResolveAsync(origin.IdnHost, cancellationToken);
        if (businessId is null) return NotFound(new { error = "published_business_website_required" });
        var version = await WebsiteContentStore.PublishedBusinessAsync(_db, businessId.Value, cancellationToken);
        if (version is null) return NotFound(new { error = "published_business_website_required" });

        var name = request.Name?.Trim() ?? "";
        var email = request.Email?.Trim() ?? "";
        var message = request.Message?.Trim() ?? "";
        var path = request.SourcePath?.Trim() ?? "/";
        if (request.SubmissionId == Guid.Empty || !request.Consent || name.Length is < 1 or > 160 ||
            email.Length is < 3 or > 254 || !new EmailAddressAttribute().IsValid(email) ||
            name.Any(char.IsControl) || email.Any(char.IsControl) || message.Length is < 1 or > 12000 ||
            path.Length > 2048 || !path.StartsWith('/') || path.StartsWith("//") ||
            path.Contains('?') || path.Contains('#') || path.Contains('\\') || path.Any(char.IsControl))
            return BadRequest(new { error = "invalid_inquiry", message = "Enter your name, email and message, and agree to share them with this business." });

        var existing = await _db.Set<CommerceWebsiteInquiry>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.CommerceBusinessId == businessId && x.SubmissionId == request.SubmissionId, cancellationToken);
        if (existing is not null) return SubmissionResult(existing, name, email, message, path);
        var inquiry = new CommerceWebsiteInquiry
        {
            CommerceBusinessId = businessId.Value, PublishedVersionId = version.Id,
            SubmissionId = request.SubmissionId, Name = name, Email = email,
            Message = message, SourcePath = path
        };
        _db.Add(inquiry);
        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            // Concurrent retries are constrained by the database, not an in-process lock.
            _db.Entry(inquiry).State = EntityState.Detached;
            existing = await _db.Set<CommerceWebsiteInquiry>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.CommerceBusinessId == businessId && x.SubmissionId == request.SubmissionId, cancellationToken);
            if (existing is null) throw;
            return SubmissionResult(existing, name, email, message, path);
        }
        return Ok(new { accepted = true });
    }

    private IActionResult SubmissionResult(CommerceWebsiteInquiry row, string name, string email, string message, string path) =>
        row.Name == name && row.Email == email && row.Message == message && row.SourcePath == path
            ? Ok(new { accepted = true })
            : Conflict(new { error = "submission_id_already_used" });

    [HttpGet("manage")]
    public async Task<IActionResult> Manage([FromQuery] string ticket, CancellationToken cancellationToken)
    {
        var businessId = await AuthorizedBusinessAsync(ticket, cancellationToken);
        if (businessId is null) return Unauthorized();
        var inquiries = await _db.Set<CommerceWebsiteInquiry>().AsNoTracking()
            .Where(x => x.CommerceBusinessId == businessId)
            .OrderByDescending(x => x.CreatedUtc).Take(100)
            .Select(x => new { x.Id, x.Name, x.Email, x.Message, x.SourcePath, x.Status, x.CreatedUtc })
            .ToListAsync(cancellationToken);
        return Ok(new { inquiries });
    }

    public sealed record StatusRequest(string Ticket, Guid InquiryId, string Status);

    [HttpPost("manage/status")]
    public async Task<IActionResult> Status([FromBody] StatusRequest request, CancellationToken cancellationToken)
    {
        var businessId = await AuthorizedBusinessAsync(request.Ticket, cancellationToken);
        if (businessId is null) return Unauthorized();
        if (request.Status is not ("New" or "Contacted" or "Closed"))
            return BadRequest(new { error = "invalid_inquiry_status" });
        var inquiry = await _db.Set<CommerceWebsiteInquiry>()
            .SingleOrDefaultAsync(x => x.Id == request.InquiryId && x.CommerceBusinessId == businessId, cancellationToken);
        if (inquiry is null) return NotFound();
        inquiry.Status = request.Status;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { inquiry.Id, inquiry.Status });
    }

    private async Task<Guid?> AuthorizedBusinessAsync(string ticket, CancellationToken cancellationToken)
    {
        var resolved = await WebsiteTicketAuthorization.ResolveAsync(_db, _tickets, _configuration, ticket, cancellationToken);
        return resolved?.SiteKey == WebsiteEditorSiteKeys.Business ? resolved.CommerceBusinessId : null;
    }
}
