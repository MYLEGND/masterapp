using System.ComponentModel.DataAnnotations;
using System.Net;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Leads;
using Infrastructure.Security;
using Infrastructure.WebsiteEditing;
using ProtectWebsite.Services;
using ProtectWebsite.Services.Tracking;
using ProtectWebsite.Services.Communication;
using Shared.Analytics;
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
    private readonly PublicWebsiteRuntimeScopeResolver _publicScopes;
    private readonly IWebsiteLifeLeadCaptureService _capture;
    private readonly WebsiteIntakeRecipientResolver _recipients;
    private readonly IProtectEmailSender _emailSender;

    public WebsiteInquiriesController(
        MasterAppDbContext db,
        WebsiteEditorTicketProtector tickets,
        IConfiguration configuration,
        PublicWebsiteRuntimeScopeResolver publicScopes,
        IWebsiteLifeLeadCaptureService capture,
        WebsiteIntakeRecipientResolver recipients,
        IProtectEmailSender emailSender)
    {
        _db = db;
        _tickets = tickets;
        _configuration = configuration;
        _publicScopes = publicScopes;
        _capture = capture;
        _recipients = recipients;
        _emailSender = emailSender;
    }

    public sealed record PublicRequest(
        Guid SubmissionId,
        string FirstName,
        string LastName,
        string Phone,
        string Email,
        string Message,
        string SourcePath,
        bool Consent,
        string? SourceActionKey = null,
        string? SourceFormElementId = null,
        string? SessionId = null,
        string? VisitorId = null,
        string? UtmSource = null,
        string? UtmMedium = null,
        string? UtmCampaign = null,
        string? UtmId = null,
        string? UtmTerm = null,
        string? UtmContent = null,
        string? Fbclid = null,
        string? Fbp = null,
        string? Fbc = null,
        string? MetaCampaignId = null,
        string? MetaAdSetId = null,
        string? MetaAdId = null);

    [HttpPost("public")]
    [RequestSizeLimit(32768)]
    [EnableRateLimiting(PlatformRateLimiting.PublicFormPolicy)]
    public async Task<IActionResult> Submit([FromBody] PublicRequest request, CancellationToken cancellationToken)
    {
        if (!PublicWebsiteRuntimeScopeResolver.HasValidPublicOrigin(HttpContext))
            return BadRequest(new { error = "verified_website_origin_required" });

        var scope = await _publicScopes.ResolveInquiryAsync(HttpContext, cancellationToken);
        if (scope is null)
            return NotFound(new { error = "published_website_required" });

        var firstName = request.FirstName?.Trim() ?? "";
        var lastName = request.LastName?.Trim() ?? "";
        var phone = request.Phone?.Trim() ?? "";
        var email = request.Email?.Trim() ?? "";
        var message = request.Message?.Trim() ?? "";
        var path = request.SourcePath?.Trim() ?? "/";
        var phoneDigits = new string(phone.Where(char.IsDigit).ToArray());

        if (request.SubmissionId == Guid.Empty || !request.Consent ||
            firstName.Length is < 1 or > 120 || lastName.Length is < 1 or > 120 ||
            phone.Length is < 7 or > 64 || phoneDigits.Length is < 10 or > 15 ||
            email.Length is < 3 or > 254 || !new EmailAddressAttribute().IsValid(email) ||
            firstName.Any(char.IsControl) || lastName.Any(char.IsControl) || phone.Any(char.IsControl) ||
            email.Any(char.IsControl) || message.Length is < 1 or > 12000 ||
            path.Length > 2048 || !path.StartsWith('/') || path.StartsWith("//") ||
            path.Contains('?') || path.Contains('#') || path.Contains('\\') || path.Any(char.IsControl) ||
            !PublicWebsiteRuntimeScopeResolver.IsPublishedPath(scope, path))
            return BadRequest(new
            {
                error = "invalid_inquiry",
                message = "Enter your first name, last name, phone number, email and message, and agree to share them with this website."
            });

        var lead = BuildLead(scope, request, firstName, lastName, phone, email, message, path);
        var submissionBinding = ResolvePublishedSubmissionBinding(scope, path, request.SourceFormElementId);
        lead.WebsiteBindingId = submissionBinding?.Id
            ?? Optional(request.SourceFormElementId, 120)
            ?? lead.WebsiteBindingId;

        return scope.SiteKey switch
        {
            WebsiteEditorSiteKeys.Business when scope.CommerceBusinessId.HasValue && scope.PublishedVersion is not null =>
                await SubmitBusinessAsync(scope, request, lead, firstName, lastName, phone, email, message, path, submissionBinding, cancellationToken),
            WebsiteEditorSiteKeys.Legend =>
                await SubmitFounderAsync(scope, request, lead, firstName, lastName, phone, email, message, path, submissionBinding, cancellationToken),
            _ => NotFound(new { error = "published_website_required" })
        };
    }

    private WebsiteLead BuildLead(
        PublicWebsiteRuntimeScope scope,
        PublicRequest request,
        string firstName,
        string lastName,
        string phone,
        string email,
        string message,
        string path)
    {
        var lead = new WebsiteLead
        {
            LeadId = Guid.NewGuid(),
            CommerceBusinessId = scope.CommerceBusinessId,
            WebsiteContentVersionId = scope.PublishedVersion?.Id,
            FirstName = firstName,
            LastName = lastName,
            Phone = phone,
            Email = email,
            Notes = message,
            SourcePageKey = path.Length <= 120 ? path : scope.SiteKey + "-inquiry",
            SourceCtaKey = Optional(request.SourceActionKey, 120),
            WebsiteBindingId = Optional(request.SourceActionKey, 120),
            InterestType = scope.SiteKey == WebsiteEditorSiteKeys.Business ? "BusinessInquiry" : "LegendInquiry",
            TermsAccepted = request.Consent,
            // Sharing an inquiry is not separate marketing or call/text permission.
            MarketingEmailConsent = false,
            CallTextConsent = false,
            SessionId = Optional(request.SessionId, 128),
            VisitorId = Optional(request.VisitorId, 128),
            UtmSource = Optional(request.UtmSource, 160),
            UtmMedium = Optional(request.UtmMedium, 160),
            UtmCampaign = Optional(request.UtmCampaign, 160),
            UtmId = Optional(request.UtmId, 160),
            Fbclid = Optional(request.Fbclid, 120),
            Fbp = Optional(request.Fbp, 512),
            Fbc = Optional(request.Fbc, 512),
            MetaCampaignId = Optional(request.MetaCampaignId, 200),
            MetaAdSetId = Optional(request.MetaAdSetId, 200),
            MetaAdId = Optional(request.MetaAdId, 200),
            ClientIpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            ClientUserAgent = Request.Headers.UserAgent.ToString(),
            Host = scope.OriginHost,
            Environment = EnvironmentLabelResolver.Resolve(),
            CreatedUtc = DateTime.UtcNow
        };
        lead.MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            SiteKey = scope.SiteKey,
            SourceActionKey = lead.SourceCtaKey,
            UtmTerm = Optional(request.UtmTerm, 160),
            UtmContent = Optional(request.UtmContent, 160),
            PublishedWebsiteVersionId = scope.PublishedVersion?.Id
        });
        lead.LeadId = WebsiteLeadSubmission.ResolveId(lead, request.SubmissionId.ToString("D"));
        return lead;
    }

    private async Task<IActionResult> SubmitBusinessAsync(
        PublicWebsiteRuntimeScope scope,
        PublicRequest request,
        WebsiteLead lead,
        string firstName,
        string lastName,
        string phone,
        string email,
        string message,
        string path,
        WebsiteSignalBinding? submissionBinding,
        CancellationToken cancellationToken)
    {
        var businessId = scope.CommerceBusinessId!.Value;
        var version = scope.PublishedVersion!;
        var name = $"{firstName} {lastName}".Trim();

        var existing = await _db.Set<CommerceWebsiteInquiry>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.CommerceBusinessId == businessId && x.SubmissionId == request.SubmissionId, cancellationToken);
        if (existing is not null)
            return await SubmissionResult(existing, firstName, lastName, phone, email, message, path, cancellationToken);

        var inquiry = new CommerceWebsiteInquiry
        {
            CommerceBusinessId = businessId,
            PublishedVersionId = version.Id,
            SubmissionId = request.SubmissionId,
            WebsiteLeadId = lead.LeadId,
            Name = name,
            Email = email,
            Message = message,
            SourcePath = path
        };

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        _db.Add(inquiry);

        try
        {
            var created = await WebsiteLeadSubmission.TryCreateAsync(
                _db,
                lead,
                request.SubmissionId.ToString("D"),
                cancellationToken,
                async ct =>
                {
                    var captured = await _capture.UpsertAsync(new()
                    {
                        WebsiteLeadId = lead.LeadId,
                        SubmittedUtc = lead.CreatedUtc
                    }, ct);
                    if (!captured.Captured)
                        throw new InvalidOperationException("The business inquiry could not be linked to CRM.");
                });

            if (created)
                WriteLeadAnalytics(scope, lead, request, submissionBinding);

            // The inquiry row is durable independently of whether an idempotent
            // WebsiteLead already existed from the same scoped submission.
            await _db.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            _db.Entry(inquiry).State = EntityState.Detached;
            _db.Entry(lead).State = EntityState.Detached;
            existing = await _db.Set<CommerceWebsiteInquiry>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.CommerceBusinessId == businessId && x.SubmissionId == request.SubmissionId, cancellationToken);
            if (existing is null) throw;
            return await SubmissionResult(existing, firstName, lastName, phone, email, message, path, cancellationToken);
        }

        var notificationSent = await TryNotifyBusinessAsync(inquiry.Id, cancellationToken);
        return Ok(new { accepted = true, notificationSent });
    }

    private async Task<IActionResult> SubmitFounderAsync(
        PublicWebsiteRuntimeScope scope,
        PublicRequest request,
        WebsiteLead lead,
        string firstName,
        string lastName,
        string phone,
        string email,
        string message,
        string path,
        WebsiteSignalBinding? submissionBinding,
        CancellationToken cancellationToken)
    {
        var created = await WebsiteLeadSubmission.TryCreateAsync(
            _db,
            lead,
            request.SubmissionId.ToString("D"),
            cancellationToken,
            async ct =>
            {
                WriteLeadAnalytics(scope, lead, request, submissionBinding);
                await _db.SaveChangesAsync(ct);
            });

        if (!created)
        {
            var existing = await _db.WebsiteLeads.AsNoTracking()
                .SingleOrDefaultAsync(x => x.LeadId == lead.LeadId, cancellationToken);
            if (existing is null ||
                existing.FirstName != firstName ||
                (existing.LastName ?? "") != lastName ||
                (existing.Phone ?? "") != phone ||
                existing.Email != email ||
                (existing.Notes ?? "") != message ||
                existing.SourcePageKey != lead.SourcePageKey)
                return Conflict(new { error = "submission_id_already_used" });
            lead = existing;
        }

        var notificationSent = await TryNotifyFounderAsync(lead, cancellationToken);
        return Ok(new { accepted = true, notificationSent });
    }

    private void WriteLeadAnalytics(
        PublicWebsiteRuntimeScope scope,
        WebsiteLead lead,
        PublicRequest request,
        WebsiteSignalBinding? submissionBinding)
    {
        if (!AnalyticsEventCatalog.TryGet("website_lead_submitted", out var leadEvent))
            throw new InvalidOperationException("Canonical lead analytics event is unavailable.");

        var analytics = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext
        {
            SiteKey = scope.SiteKey,
            CommerceBusinessId = scope.CommerceBusinessId,
            WebsiteContentVersionId = scope.PublishedVersion?.Id,
            WebsiteBindingId = submissionBinding?.Id ?? Optional(request.SourceFormElementId, 120) ?? lead.SourceCtaKey,
            EventId = scope.SiteKey + "_lead_" + lead.LeadId.ToString("N"),
            EventName = leadEvent.Name,
            EventCategory = leadEvent.Category,
            EventUtc = lead.CreatedUtc,
            SessionId = lead.SessionId,
            VisitorId = lead.VisitorId,
            PageKey = lead.SourcePageKey,
            FormKey = "website_inquiry",
            UtmSource = lead.UtmSource,
            UtmMedium = lead.UtmMedium,
            UtmCampaign = lead.UtmCampaign,
            UtmId = lead.UtmId,
            UtmContent = Optional(request.UtmContent, 160),
            Fbclid = lead.Fbclid,
            Fbp = lead.Fbp,
            Fbc = lead.Fbc,
            MetaCampaignId = lead.MetaCampaignId,
            MetaAdSetId = lead.MetaAdSetId,
            MetaAdId = lead.MetaAdId,
            UserAgent = lead.ClientUserAgent,
            IpAddress = lead.ClientIpAddress,
            Host = lead.Host,
            Environment = lead.Environment,
            IsBrowserSignal = false,
            IsServerAuthority = true,
            MetaServerAuthorityEligible = submissionBinding is null || submissionBinding.DeliveryMode == "meta",
            Metadata = new
            {
                LeadId = lead.LeadId,
                WebsiteLeadId = lead.LeadId,
                SourceActionKey = lead.SourceCtaKey,
                WebsiteFormElementId = request.SourceFormElementId,
                WebsiteSignalBindingId = submissionBinding?.Id,
                WebsiteSignalDeliveryMode = submissionBinding?.DeliveryMode,
                Source = scope.SiteKey + "_website_inquiry_saved"
            }
        });
        analytics.ClientEventId = lead.LeadId;
        UnifiedAnalyticsWriter.Write(_db, analytics);
    }

    private static WebsiteSignalBinding? ResolvePublishedSubmissionBinding(
        PublicWebsiteRuntimeScope scope,
        string path,
        string? elementId)
    {
        if (scope.PublishedVersion is null || string.IsNullOrWhiteSpace(elementId))
            return null;

        try
        {
            var document = WebsiteContentSanitizer.Sanitize(
                System.Text.Json.JsonSerializer.Deserialize<WebsiteContentDocument>(
                    scope.PublishedVersion.DocumentJson,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? new());

            var route = path.TrimEnd('/');
            if (route.Length == 0) route = "/";
            if (!document.Pages.TryGetValue(route, out var page))
                return null;

            IEnumerable<WebsiteSignalBinding>? bindings = null;
            if (elementId.StartsWith("extra:", StringComparison.Ordinal))
            {
                var id = elementId.Split(':', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault();
                bindings = page.Extras.FirstOrDefault(extra => extra.Id == id)?.Signals;
            }
            else if (page.Elements.TryGetValue(elementId, out var element))
            {
                bindings = element.Signals;
            }

            return (bindings ?? []).SingleOrDefault(binding =>
                binding.Trigger == "submission_saved" &&
                binding.EventName == "Lead" &&
                binding.DeliveryMode != "off");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
        {
            return null;
        }
    }

    private async Task<bool> TryNotifyFounderAsync(WebsiteLead lead, CancellationToken cancellationToken)
    {
        if (lead.NotificationSentUtc.HasValue)
            return true;

        var trackedLead = await _db.WebsiteLeads.SingleAsync(x => x.LeadId == lead.LeadId, cancellationToken);
        if (!await WebsiteLeadSubmission.TryClaimNotificationAsync(_db, trackedLead, cancellationToken))
            return trackedLead.NotificationSentUtc.HasValue;

        var recipient = await _recipients.ResolveAsync(MarketingOwnerScope.Founder, cancellationToken);
        var sent = false;
        if (!string.IsNullOrWhiteSpace(recipient))
        {
            var name = $"{trackedLead.FirstName} {trackedLead.LastName}".Trim();
            var html =
                $"<p><strong>{WebUtility.HtmlEncode(name)}</strong></p>" +
                $"<p>{WebUtility.HtmlEncode(trackedLead.Email)} · {WebUtility.HtmlEncode(trackedLead.Phone)}</p>" +
                $"<p>{WebUtility.HtmlEncode(trackedLead.Notes ?? "").Replace("\n", "<br>")}</p>" +
                $"<p>Page: {WebUtility.HtmlEncode(trackedLead.SourcePageKey)}</p>";
            try
            {
                sent = await _emailSender.TrySendAsync(
                    recipient,
                    "New LEGEND® website inquiry",
                    html,
                    replyToEmail: trackedLead.Email,
                    cancellationToken: cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                sent = false;
            }
        }

        await WebsiteLeadSubmission.CompleteNotificationAsync(_db, trackedLead, sent, cancellationToken);
        return sent;
    }

    private static string? Optional(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = value.Trim();
        if (clean.Length > max || clean.Any(char.IsControl)) return null;
        return clean;
    }

    private async Task<IActionResult> SubmissionResult(
        CommerceWebsiteInquiry row,
        string firstName,
        string lastName,
        string phone,
        string email,
        string message,
        string path,
        CancellationToken cancellationToken)
    {
        var name = $"{firstName} {lastName}".Trim();
        var rowMatches = row.Name == name && row.Email == email && row.Message == message && row.SourcePath == path;
        if (!rowMatches) return Conflict(new { error = "submission_id_already_used" });

        if (row.WebsiteLeadId.HasValue)
        {
            var lead = await _db.WebsiteLeads.AsNoTracking()
                .SingleOrDefaultAsync(x => x.LeadId == row.WebsiteLeadId.Value, cancellationToken);
            if (lead is null ||
                lead.FirstName != firstName ||
                (lead.LastName ?? "") != lastName ||
                (lead.Phone ?? "") != phone ||
                lead.Email != email)
                return Conflict(new { error = "submission_id_already_used" });
        }

        var notificationSent = await TryNotifyBusinessAsync(row.Id, cancellationToken);
        return Ok(new { accepted = true, notificationSent });
    }

    private async Task<bool> TryNotifyBusinessAsync(Guid inquiryId, CancellationToken cancellationToken)
    {
        try
        {
            var service = new BusinessInquiryNotificationService(_db, _recipients, _emailSender);
            return await service.DeliverOneAsync(inquiryId, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The persisted CommerceWebsiteInquiry remains the durable retry queue.
            return false;
        }
    }

    [HttpGet("manage")]
    public async Task<IActionResult> Manage([FromQuery] string ticket, CancellationToken cancellationToken)
    {
        var businessId = await AuthorizedBusinessAsync(ticket, cancellationToken);
        if (businessId is null) return Unauthorized();
        var inquiries = await (
            from inquiry in _db.Set<CommerceWebsiteInquiry>().AsNoTracking()
            where inquiry.CommerceBusinessId == businessId
            join lead in _db.WebsiteLeads.AsNoTracking()
                on inquiry.WebsiteLeadId equals (Guid?)lead.LeadId into linkedLeads
            from lead in linkedLeads.DefaultIfEmpty()
            orderby inquiry.CreatedUtc descending
            select new
            {
                inquiry.Id,
                inquiry.Name,
                FirstName = lead == null ? null : lead.FirstName,
                LastName = lead == null ? null : lead.LastName,
                Phone = lead == null ? null : lead.Phone,
                Email = lead == null ? inquiry.Email : lead.Email,
                inquiry.Message,
                inquiry.SourcePath,
                inquiry.Status,
                inquiry.CreatedUtc
            })
            .Take(100)
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
