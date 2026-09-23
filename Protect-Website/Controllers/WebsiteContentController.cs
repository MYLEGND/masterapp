using System.Text.Json;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ProtectWebsite.Controllers;

[ApiController]
[Route("api/website-content")]
public sealed class WebsiteContentController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly MasterAppDbContext _db;
    private readonly WebsiteEditorTicketProtector _tickets;
    private readonly IConfiguration _configuration;

    public WebsiteContentController(
        MasterAppDbContext db,
        WebsiteEditorTicketProtector tickets,
        IConfiguration configuration)
    {
        _db = db;
        _tickets = tickets;
        _configuration = configuration;
    }

    public sealed record WebsiteEditorHandoffRequest(string State);

    [HttpPost("handoff")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ExchangeHandoff(
        [FromBody] WebsiteEditorHandoffRequest request,
        CancellationToken cancellationToken = default)
    {
        var tokenHash = WebsiteEditorHandoffToken.Hash(request?.State ?? string.Empty);
        if (string.IsNullOrWhiteSpace(tokenHash))
            return Unauthorized();

        var nowUtc = DateTime.UtcNow;
        var continuation = await _db.ClientIdentityContinuations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.TokenHash == tokenHash &&
                    candidate.Purpose == ClientIdentityContinuationPurpose.WebsiteEditor &&
                    candidate.ConsumedUtc == null &&
                    candidate.ExpiresUtc > nowUtc,
                cancellationToken);

        if (continuation is null ||
            !continuation.CommerceBusinessId.HasValue ||
            continuation.CommerceBusinessId == Guid.Empty ||
            string.IsNullOrWhiteSpace(continuation.ActorUserId))
            return Unauthorized();

        var actorEmail = string.IsNullOrWhiteSpace(continuation.ActorEmail)
            ? continuation.IntendedNormalizedEmail
            : continuation.ActorEmail;

        if (!await WebsiteBusinessAccess.CanManageAsActorAsync(
                _db,
                continuation.CommerceBusinessId.Value,
                continuation.ClientProfileId,
                continuation.ActorUserId,
                actorEmail,
                cancellationToken))
            return Unauthorized();

        var consumed = await _db.ClientIdentityContinuations
            .Where(candidate =>
                candidate.Id == continuation.Id &&
                candidate.Purpose == ClientIdentityContinuationPurpose.WebsiteEditor &&
                candidate.ConsumedUtc == null &&
                candidate.ExpiresUtc > nowUtc)
            .ExecuteUpdateAsync(
                updates => updates.SetProperty(candidate => candidate.ConsumedUtc, nowUtc),
                cancellationToken);

        if (consumed != 1)
            return Unauthorized();

        var ticket = _tickets.Protect(new WebsiteEditorTicket(
            WebsiteEditorSiteKeys.Business,
            WebsiteEditorSiteKeys.BusinessOwnerKey(continuation.CommerceBusinessId.Value),
            null,
            false,
            DateTime.UtcNow.AddHours(4),
            continuation.CommerceBusinessId,
            ActorUserId: continuation.ActorUserId,
            ActorEmail: actorEmail,
            ActorClientProfileId: continuation.ClientProfileId));

        return Ok(new
        {
            ticket,
            apiBase = WebsiteContentApiBaseUrl()
        });
    }

    [HttpGet("public/{siteKey}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Public(
        string siteKey,
        [FromQuery] string? agentSlug = null,
        [FromQuery] Guid? businessId = null,
        CancellationToken cancellationToken = default)
    {
        siteKey = NormalizeSiteKey(siteKey);
        if (siteKey.Length == 0) return NotFound();

        CommerceBusiness? business = null;
        string? ownerKey;
        if (siteKey == WebsiteEditorSiteKeys.Legend)
        {
            ownerKey = WebsiteEditorSiteKeys.GlobalOwnerKey;
        }
        else if (siteKey == WebsiteEditorSiteKeys.Protect)
        {
            ownerKey = await ResolveProtectOwnerKeyAsync(agentSlug, cancellationToken);
        }
        else if (siteKey == WebsiteEditorSiteKeys.Business)
        {
            business = await ResolveBusinessAsync(businessId, cancellationToken);
            ownerKey = business is null ? null : WebsiteEditorSiteKeys.BusinessOwnerKey(business.Id);
        }
        else
        {
            ownerKey = null;
        }

        if (string.IsNullOrWhiteSpace(ownerKey))
            return siteKey == WebsiteEditorSiteKeys.Business
                ? NotFound(new { error = "business_website_not_found" })
                : Ok(new { siteKey, document = new WebsiteContentDocument() });

        var document = await LoadAsync(ownerKey, siteKey, cancellationToken);
        if (document is null)
        {
            if (siteKey == WebsiteEditorSiteKeys.Business)
                return NotFound(new { error = "website_not_published" });
            document = new WebsiteContentDocument();
        }
        return Ok(new
        {
            siteKey,
            business = business is null ? null : new { business.Id, business.DisplayName, business.LegalName, business.BusinessType },
            businessName = business?.DisplayName,
            document
        });
    }

    [HttpGet("public/runtime")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> PublicRuntime(
        [FromQuery] string siteKey,
        CancellationToken cancellationToken = default)
    {
        var scopes = HttpContext.RequestServices.GetRequiredService<PublicWebsiteRuntimeScopeResolver>();
        var scope = await scopes.ResolveAsync(HttpContext, siteKey, cancellationToken);
        if (scope is null) return NotFound(new { error = "published_website_scope_not_found" });

        WebsiteBusinessFacts? facts = scope.CommerceBusinessId.HasValue
            ? await WebsiteBusinessFacts.LoadAsync(_db, scope.CommerceBusinessId.Value, cancellationToken)
            : null;
        var actions = await BuildCallToActionCatalogAsync(
            scope.SiteKey,
            scope.OwnerKey,
            agentSlug: null,
            scope.CommerceBusinessId,
            facts,
            cancellationToken);

        var metaResolver = HttpContext.RequestServices.GetRequiredService<ProtectWebsite.Services.Meta.IMetaPixelResolutionService>();
        var pixel = scope.CommerceBusinessId.HasValue
            ? await metaResolver.ResolveForBusinessAsync(scope.CommerceBusinessId.Value, cancellationToken)
            : await metaResolver.ResolveForLeadAsync(null, null, isFounderPath: true, cancellationToken);
        var metaOptions = HttpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsSnapshot<ProtectWebsite.Services.MetaSignal.MetaSignalIntelligenceOptions>>()
            .Value;
        var apiBase = WebsiteContentApiBaseUrl();

        return Ok(new
        {
            siteKey = scope.SiteKey,
            publishedVersionId = scope.PublishedVersion?.Id,
            ctaCatalog = new { options = actions },
            analytics = new
            {
                endpoint = apiBase + "/api/tracking/ingest",
                allowedBrowserEvents = Shared.Analytics.AnalyticsEventCatalog.BrowserAllowedEventNames,
                criticalBrowserEvents = Shared.Analytics.AnalyticsEventCatalog.CriticalBrowserEventNames,
                clientTrackingErrorEvent = Shared.Analytics.AnalyticsEventCatalog.ClientTrackingErrorEventName
            },
            meta = new
            {
                enabled = metaOptions.Enabled,
                sendBrowserEvents = metaOptions.SendBrowserEvents,
                sendServerEvents = metaOptions.SendServerEvents,
                persistEvents = metaOptions.PersistEvents,
                debugMode = metaOptions.DebugMode,
                highIntentThreshold = metaOptions.HighIntentThreshold,
                leadReadyThreshold = metaOptions.LeadReadyThreshold,
                endpoint = apiBase + "/analytics/meta-signal",
                pixelId = pixel.HasBrowserPixel ? pixel.PixelId : null,
                browserEventNames = Shared.Analytics.MetaSignalEventCatalog.BrowserPixelEventNames,
                weights = metaOptions.Weights
            }
        });
    }

    [HttpGet("manage")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Manage([FromQuery] string ticket, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var state = await StateAsync(actor, cancellationToken);
        var history = await _db.Set<WebsiteContentVersion>().AsNoTracking().Where(v => v.StateId == state.Id)
            .OrderByDescending(v => v.Revision).Select(v => new { versionId = v.Id, v.Revision, v.CreatedUtc }).ToListAsync(cancellationToken);
        var business = actor.CommerceBusinessId.HasValue ? await _db.CommerceBusinesses.AsNoTracking().SingleAsync(b => b.Id == actor.CommerceBusinessId, cancellationToken) : null;
        var facts = business is null ? null : await WebsiteBusinessFacts.LoadAsync(_db, business.Id, cancellationToken);
        var ctaOptions = await BuildCallToActionCatalogAsync(actor, facts, cancellationToken);
        return Ok(new { business = business is null ? null : new { business.Id, business.DisplayName, business.LegalName, business.BusinessType }, siteKey = actor.SiteKey, agentSlug = actor.AgentSlug, commerceBusinessId = actor.CommerceBusinessId, document = Read(state.DraftJson),
            revision = state.Revision, publishedRevision = history.FirstOrDefault(v => v.versionId == state.PublishedVersionId)?.Revision,
            facts,
            ctaCatalog = new { options = ctaOptions },
            usage = new { mediaBytes = await _db.Set<WebsiteMediaAsset>().Where(a => a.OwnerKey == actor.OwnerUserId).SumAsync(a => (long?)a.SizeBytes, cancellationToken) ?? 0, mediaCount = await _db.Set<WebsiteMediaAsset>().CountAsync(a => a.OwnerKey == actor.OwnerUserId, cancellationToken), publishedVersions = history.Count },
            importReport = string.IsNullOrEmpty(state.ImportReportJson) ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(state.ImportReportJson),
            drafts = ReadDrafts(state).Select(d => new { d.Id, d.Name, d.UpdatedUtc }),
            history, signalCatalog = SignalCatalogPayload(), capabilities = new { canPublish = await CanPublishAsync(actor, cancellationToken), canManageDomains = await CanPublishAsync(actor, cancellationToken), canImport = actor.SiteKey == WebsiteEditorSiteKeys.Business, canSchedule = await CanPublishAsync(actor, cancellationToken) },
            schedule = new { publishUtc = state.ScheduledPublishUtc, error = state.ScheduleError },
            readiness = new { checks = new[] { new { passed = true, message = "Draft is isolated from published content. Publishing validates and compiles the complete website." } } } });
    }

    public sealed record SaveRequest(string Ticket, WebsiteContentDocument Document, long? ExpectedRevision = null, Guid? DraftId = null, string? DraftName = null);
    public sealed record ProfileRequest(string Ticket, BusinessWebsiteProfileInput Settings);
    private object SignalCatalogPayload() => new { events = WebsiteSignalBindingPolicy.Options, matchingFields = WebsiteSignalBindingPolicy.ApprovedMatchingFields, runtimeEnabled = _configuration.GetValue<bool>("WebsiteMarketing:Enabled") };

    [HttpGet("manage/signal-catalog")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> SignalCatalog([FromQuery] string ticket, CancellationToken cancellationToken)
    {
        if (await AuthorizeAsync(ticket, cancellationToken) is null) return Unauthorized();
        return Ok(SignalCatalogPayload());
    }

    [HttpGet("manage/profile")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> BusinessProfile([FromQuery] string ticket, CancellationToken cancellationToken)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor?.SiteKey != WebsiteEditorSiteKeys.Business || !actor.CommerceBusinessId.HasValue) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken)) return Forbid();
        var service = new BusinessWebsiteProfileService(_db, HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingConnectionStore>());
        return Ok(await service.GetAsync(actor.CommerceBusinessId.Value, cancellationToken));
    }

    [HttpPost("manage/profile")]
    public async Task<IActionResult> SaveBusinessProfile([FromBody] ProfileRequest request, CancellationToken cancellationToken)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor?.SiteKey != WebsiteEditorSiteKeys.Business || !actor.CommerceBusinessId.HasValue) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken)) return Forbid();
        var service = new BusinessWebsiteProfileService(_db, HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingConnectionStore>());
        try { await service.SaveAsync(actor.CommerceBusinessId.Value, request.Settings, cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "profile_revision_conflict", message = "Settings changed in another session. Reload and try again." }); }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.DataAnnotations.ValidationException)
        { return BadRequest(new { error = "invalid_profile_settings", message = ex.Message }); }
        return Ok(await service.GetAsync(actor.CommerceBusinessId.Value, cancellationToken));
    }
    public sealed record PublishRequest(string Ticket, long ExpectedRevision);
    public sealed record RollbackRequest(string Ticket, long ExpectedRevision, Guid VersionId);

    [HttpPost("manage")]
    [RequestSizeLimit(4_500_000)]
    public async Task<IActionResult> Save([FromBody] SaveRequest request, CancellationToken cancellationToken = default)
    {
        if (request?.Document is null) return BadRequest();
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var state = await StateAsync(actor, cancellationToken);
        if (request.ExpectedRevision != state.Revision) return Conflict(new { error = "revision_conflict", revision = state.Revision });
        WebsiteContentDocument document;
        try { document = WebsiteContentSanitizer.Sanitize(request.Document); }
        catch (ArgumentException ex) { return BadRequest(new { error = "invalid_signal_binding", message = ex.Message }); }
        document.UpdatedUtc = DateTime.UtcNow;
        if (request.DraftId.HasValue || request.DraftName is not null)
        {
            var name = request.DraftName?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100) return BadRequest(new { message = "Enter a draft name of 1–100 characters." });
            var drafts = ReadDrafts(state);
            var draft = request.DraftId.HasValue ? drafts.SingleOrDefault(d => d.Id == request.DraftId) : null;
            if (request.DraftId.HasValue && draft is null) return NotFound();
            if (draft is null && drafts.Count >= 20) return BadRequest(new { message = "You can keep up to 20 drafts. Delete a draft before creating another." });
            if (drafts.Any(d => d.Id != draft?.Id && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
                return Conflict(new { message = "That name already exists. Select the existing draft to update it, or choose another name." });
            if (draft is null) { draft = new WebsiteNamedDraft(); drafts.Add(draft); }
            draft.Name = name; draft.Document = document; draft.UpdatedUtc = DateTime.UtcNow;
            state.NamedDraftsJson = JsonSerializer.Serialize(drafts, JsonOptions);
        }
        state.ScheduledPublishUtc = null;
        state.ScheduledActorJson = null;
        state.ScheduledRevision = null;
        state.ScheduleError = null;
        state.DraftJson = JsonSerializer.Serialize(document, JsonOptions);
        state.Revision++;
        state.UpdatedUtc = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "revision_conflict" }); }
        return Ok(new { document, revision = state.Revision, savedUtc = state.UpdatedUtc, drafts = ReadDrafts(state).Select(d => new { d.Id, d.Name, d.UpdatedUtc }) });
    }

    private static List<WebsiteNamedDraft> ReadDrafts(WebsiteContentState state) =>
        string.IsNullOrWhiteSpace(state.NamedDraftsJson) ? new() : JsonSerializer.Deserialize<List<WebsiteNamedDraft>>(state.NamedDraftsJson, JsonOptions) ?? new();

    public sealed record DraftRequest(string Ticket, long ExpectedRevision, Guid DraftId);

    [HttpPost("manage/drafts/load")]
    public Task<IActionResult> LoadDraft([FromBody] DraftRequest request, CancellationToken cancellationToken = default) => ChangeDraft(request, false, cancellationToken);

    [HttpPost("manage/drafts/delete")]
    public Task<IActionResult> DeleteDraft([FromBody] DraftRequest request, CancellationToken cancellationToken = default) => ChangeDraft(request, true, cancellationToken);

    private async Task<IActionResult> ChangeDraft(DraftRequest request, bool delete, CancellationToken cancellationToken)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var state = await StateAsync(actor, cancellationToken);
        if (state.Revision != request.ExpectedRevision) return Conflict(new { error = "revision_conflict" });
        var drafts = ReadDrafts(state);
        var draft = drafts.SingleOrDefault(d => d.Id == request.DraftId);
        if (draft is null) return NotFound();
        if (delete) drafts.Remove(draft);
        else
        {
            state.DraftJson = JsonSerializer.Serialize(draft.Document, JsonOptions);
            state.ScheduledPublishUtc = null; state.ScheduledRevision = null;
            state.ScheduledActorJson = null; state.ScheduleError = null;
        }
        state.NamedDraftsJson = JsonSerializer.Serialize(drafts, JsonOptions);
        state.Revision++; state.UpdatedUtc = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "revision_conflict" }); }
        return Ok(new { revision = state.Revision, document = Read(state.DraftJson), drafts = drafts.Select(d => new { d.Id, d.Name, d.UpdatedUtc }) });
    }

    [HttpPost("manage/publish")]
    public async Task<IActionResult> Publish([FromBody] PublishRequest request, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken)) return Forbid();
        var state = await StateAsync(actor, cancellationToken);
        if (request.ExpectedRevision != state.Revision) return Conflict(new { error = "revision_conflict" });
        var document = Read(state.DraftJson);
        CommerceBusiness? business = null;
        WebsiteBusinessFacts? facts = null;
        if (actor.SiteKey == WebsiteEditorSiteKeys.Business)
        {
            business = await _db.CommerceBusinesses.AsNoTracking().SingleAsync(b => b.Id == actor.CommerceBusinessId, cancellationToken);
            facts = await WebsiteBusinessFacts.LoadAsync(_db, business.Id, cancellationToken);
        }
        var ctaOptions = await BuildCallToActionCatalogAsync(actor, facts, cancellationToken);
        var ctaError = WebsiteCallToActionCatalog.PrepareForPublish(document, ctaOptions);
        if (ctaError is not null) return BadRequest(new { error = "button_destination_required", message = ctaError });
        state.DraftJson = JsonSerializer.Serialize(document, JsonOptions);
        var version = new WebsiteContentVersion { StateId = state.Id, Revision = state.Revision + 1,
            DocumentJson = state.DraftJson, ImportReportJson = state.ImportReportJson, ActorUserId = actor.ActorUserId! };
        if (business is not null)
        {
            var compiler = HttpContext.RequestServices.GetRequiredService<ProtectWebsite.Services.WebsitePageCompiler>();
            version.CompiledPagesJson = await compiler.CompileAsync(document, business, facts!, cancellationToken);
        }
        _db.Set<WebsiteContentVersion>().Add(version);
        state.PublishedVersionId = version.Id;
        state.ScheduledPublishUtc = null;
        state.ScheduledActorJson = null;
        state.ScheduledRevision = null;
        state.ScheduleError = null;
        state.Revision++;
        state.UpdatedUtc = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "revision_conflict" }); }
        return Ok(new { revision = state.Revision, publishedRevision = version.Revision, versionId = version.Id });
    }

    [HttpPost("manage/rollback")]
    public async Task<IActionResult> Rollback([FromBody] RollbackRequest request, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken)) return Forbid();
        var state = await StateAsync(actor, cancellationToken);
        if (state.Revision != request.ExpectedRevision) return Conflict(new { error = "revision_conflict" });
        var previous = await _db.Set<WebsiteContentVersion>().AsNoTracking().SingleOrDefaultAsync(v => v.Id == request.VersionId && v.StateId == state.Id, cancellationToken);
        if (previous is null) return NotFound();
        var restored = new WebsiteContentVersion { StateId = state.Id, Revision = state.Revision + 1,
            DocumentJson = previous.DocumentJson, CompiledPagesJson = previous.CompiledPagesJson, ImportReportJson = previous.ImportReportJson, ActorUserId = actor.ActorUserId! };
        _db.Set<WebsiteContentVersion>().Add(restored);
        state.ScheduledPublishUtc = null;
        state.ScheduledActorJson = null;
        state.ScheduledRevision = null;
        state.PublishedVersionId = restored.Id;
        state.DraftJson = previous.DocumentJson;
        state.ImportReportJson = previous.ImportReportJson;
        state.Revision++;
        state.UpdatedUtc = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "revision_conflict" }); }
        return Ok(new { revision = state.Revision, publishedRevision = restored.Revision, document = Read(state.DraftJson) });
    }

    [HttpGet("manage/export")]
    public async Task<IActionResult> Export([FromQuery] string ticket, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var state = await StateAsync(actor, cancellationToken);
        var package = await HttpContext.RequestServices.GetRequiredService<WebsiteImportService>().PreparePortableExportAsync(actor.OwnerUserId, Read(state.DraftJson), cancellationToken);
        return File(package, "application/zip", "website-export.zip");
    }

    public sealed record ImportRequest(string Ticket, long ExpectedRevision, string? SourceUrl, bool Authorized, WebsiteContentDocument? Document = null);
    public sealed record DomainRequest(string Ticket, string Hostname);
    public sealed record DomainActionRequest(string Ticket, Guid BindingId);

    [HttpPost("manage/import")]
    [RequestSizeLimit(4_500_000)]
    public async Task<IActionResult> Import([FromBody] ImportRequest request, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor?.SiteKey != WebsiteEditorSiteKeys.Business) return Unauthorized();
        if (!request.Authorized) return BadRequest(new { error = "import_authorization_required" });
        var state = await StateAsync(actor, cancellationToken);
        if (state.Revision != request.ExpectedRevision) return Conflict(new { error = "revision_conflict" });
        var importer = HttpContext.RequestServices.GetRequiredService<WebsiteImportService>();
        WebsiteImportResult result;
        if (request.Document is not null)
        {
            using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request.Document, JsonOptions));
            result = await importer.PrepareExportAsync(input, false, Read(state.DraftJson), true, actor.OwnerUserId, MediaBaseUrl(), cancellationToken);
        }
        else result = await importer.PrepareAsync(request.SourceUrl ?? "", Read(state.DraftJson), true, actor.OwnerUserId, MediaBaseUrl(), cancellationToken);
        state.ScheduledPublishUtc = null;
        state.ScheduledActorJson = null;
        state.ScheduledRevision = null;
        state.ImportReportJson = JsonSerializer.Serialize(result.Report, JsonOptions);
        state.DraftJson = JsonSerializer.Serialize(WebsiteContentSanitizer.Sanitize(result.Document), JsonOptions);
        state.Revision++;
        state.UpdatedUtc = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "revision_conflict" }); }
        return Ok(new { document = Read(state.DraftJson), revision = state.Revision, report = result.Report });
    }

    [HttpGet("manage/domains")]
    public async Task<IActionResult> Domains([FromQuery] string ticket, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor?.SiteKey != WebsiteEditorSiteKeys.Business) return Unauthorized();
        var domains = await _db.Set<WebsiteDomainBinding>().AsNoTracking().Where(d => d.CommerceBusinessId == actor.CommerceBusinessId).ToListAsync(cancellationToken);
        return Ok(new { domains, cnameTarget = DomainService().CnameTarget });
    }

    [HttpPost("manage/domains")]
    public async Task<IActionResult> AddDomain([FromBody] DomainRequest request, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor?.SiteKey != WebsiteEditorSiteKeys.Business) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken)) return Forbid();
        var binding = await DomainService().RegisterAsync(actor.CommerceBusinessId!.Value, request.Hostname, cancellationToken);
        return Ok(new { binding, cnameTarget = DomainService().CnameTarget });
    }

    [HttpPost("manage/domains/refresh")]
    public async Task<IActionResult> RefreshDomain([FromBody] DomainActionRequest request, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor?.SiteKey != WebsiteEditorSiteKeys.Business) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken)) return Forbid();
        return Ok(await DomainService().RefreshAsync(actor.CommerceBusinessId!.Value, request.BindingId, cancellationToken));
    }

    [HttpPost("manage/domains/remove")]
    public async Task<IActionResult> RemoveDomain([FromBody] DomainActionRequest request, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor?.SiteKey != WebsiteEditorSiteKeys.Business) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken)) return Forbid();
        await DomainService().RemoveAsync(actor.CommerceBusinessId!.Value, request.BindingId, cancellationToken);
        return Ok(new { success = true });
    }

    [HttpGet("public/resolve")]
    public async Task<IActionResult> ResolveDomain([FromQuery] string host, CancellationToken cancellationToken = default)
    {
        var domain = await DomainService().ResolveAsync(host, cancellationToken);
        return domain is null ? NotFound() : await Public(WebsiteEditorSiteKeys.Business, businessId: domain.Value, cancellationToken: cancellationToken);
    }

    [HttpGet("media/{id:guid}")]
    public async Task<IActionResult> Media(Guid id, [FromQuery] string? ticket = null, CancellationToken cancellationToken = default)
    {
        var asset = await _db.Set<WebsiteMediaAsset>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (asset is null) return NotFound();
        var actor = string.IsNullOrWhiteSpace(ticket) ? null : await AuthorizeAsync(ticket, cancellationToken);
        if (actor?.OwnerUserId != asset.OwnerKey)
        {
            var versions = await (from state in _db.Set<WebsiteContentState>().AsNoTracking()
                                  join version in _db.Set<WebsiteContentVersion>().AsNoTracking() on state.PublishedVersionId equals version.Id
                                  where state.OwnerKey == asset.OwnerKey select version.DocumentJson).ToListAsync(cancellationToken);
            if (!versions.Any(json => json.Contains("/api/website-content/media/" + id, StringComparison.OrdinalIgnoreCase))) return NotFound();
        }
        var media = await HttpContext.RequestServices.GetRequiredService<WebsiteMediaService>().OpenAsync(asset.OwnerKey, id, cancellationToken);
        return media is null ? NotFound() : File(media.Value.Content, media.Value.Asset.ContentType, enableRangeProcessing: true);
    }

    public sealed record BusinessDetailsRequest(string Ticket, long ExpectedRevision, WebsiteBusinessFacts Details);
    [HttpGet("manage/business-details")]
    public async Task<IActionResult> BusinessDetails([FromQuery] string ticket, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor?.SiteKey != WebsiteEditorSiteKeys.Business) return Unauthorized();
        var state = await StateAsync(actor, cancellationToken);
        return Ok(new { details = await WebsiteBusinessFacts.LoadAsync(_db, actor.CommerceBusinessId!.Value, cancellationToken), revision = state.Revision });
    }
    [HttpPost("manage/business-details")]
    public async Task<IActionResult> SaveBusinessDetails([FromBody] BusinessDetailsRequest request, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor?.SiteKey != WebsiteEditorSiteKeys.Business) return Unauthorized();
        var state = await StateAsync(actor, cancellationToken);
        if (state.Revision != request.ExpectedRevision) return Conflict(new { error = "revision_conflict" });
        var settings = await _db.CommerceBusinessStorefrontSettings.SingleOrDefaultAsync(s => s.CommerceBusinessId == actor.CommerceBusinessId, cancellationToken);
        if (settings is null) { settings = new CommerceBusinessStorefrontSettings { CommerceBusinessId = actor.CommerceBusinessId!.Value }; _db.Add(settings); }
        var details = WebsiteBusinessFacts.Sanitize(request.Details);
        settings.PublicFactsJson = JsonSerializer.Serialize(details, JsonOptions);
        settings.UpdatedUtc = DateTime.UtcNow;
        state.Revision++;
        state.ScheduledPublishUtc = null;
        state.ScheduledActorJson = null;
        state.ScheduledRevision = null;
        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "revision_conflict" }); }
        return Ok(new { details, revision = state.Revision });
    }
    [HttpPost("manage/media")]
    [RequestSizeLimit(26_000_000)]
    public async Task<IActionResult> UploadMedia([FromForm] string ticket, [FromForm] IFormFile file, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        if (file is null || file.Length <= 0 || file.Length > 25_000_000) return BadRequest();
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);
        var media = HttpContext.RequestServices.GetRequiredService<WebsiteMediaService>();
        var asset = await media.StoreAsync(actor.OwnerUserId, "", file.FileName, buffer.ToArray(), cancellationToken);
        return Ok(new { url = MediaBaseUrl() + "/api/website-content/media/" + asset.Id, sizeBytes = asset.SizeBytes });
    }
    [HttpPost("manage/import-file")]
    [RequestSizeLimit(52_000_000)]
    public async Task<IActionResult> ImportFile([FromForm] string ticket, [FromForm] long expectedRevision, [FromForm] bool authorized, [FromForm] IFormFile file, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor?.SiteKey != WebsiteEditorSiteKeys.Business) return Unauthorized();
        if (!authorized || file is null || file.Length > 50_000_000) return BadRequest();
        var state = await StateAsync(actor, cancellationToken);
        if (state.Revision != expectedRevision) return Conflict(new { error = "revision_conflict" });
        await using var input = file.OpenReadStream();
        var result = await HttpContext.RequestServices.GetRequiredService<WebsiteImportService>().PrepareExportAsync(input, Path.GetExtension(file.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase), Read(state.DraftJson), true, actor.OwnerUserId, MediaBaseUrl(), cancellationToken);
        state.ImportReportJson = JsonSerializer.Serialize(result.Report, JsonOptions);
        state.DraftJson = JsonSerializer.Serialize(WebsiteContentSanitizer.Sanitize(result.Document), JsonOptions);
        state.Revision++;
        state.ScheduledPublishUtc = null;
        state.ScheduledActorJson = null;
        state.ScheduledRevision = null;
        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "revision_conflict" }); }
        return Ok(new { document = Read(state.DraftJson), revision = state.Revision, report = result.Report });
    }
    private string WebsiteContentApiBaseUrl() => (_configuration["WebsiteContentApiBaseUrl"] ?? "https://masterapp-protect.azurewebsites.net").TrimEnd('/');
    private string MediaBaseUrl() => WebsiteContentApiBaseUrl();
    private WebsiteDomainService DomainService() => HttpContext.RequestServices.GetRequiredService<WebsiteDomainService>();

    private Task<IReadOnlyList<WebsiteCallToActionOption>> BuildCallToActionCatalogAsync(
        WebsiteEditorTicket actor,
        WebsiteBusinessFacts? facts,
        CancellationToken cancellationToken) =>
        BuildCallToActionCatalogAsync(
            actor.SiteKey,
            actor.OwnerUserId,
            actor.AgentSlug,
            actor.CommerceBusinessId,
            facts,
            cancellationToken);

    private async Task<IReadOnlyList<WebsiteCallToActionOption>> BuildCallToActionCatalogAsync(
        string siteKey,
        string ownerUserId,
        string? agentSlug,
        Guid? commerceBusinessId,
        WebsiteBusinessFacts? facts,
        CancellationToken cancellationToken)
    {
        string? phone = null;
        string? email = null;
        string? bookingUrl = null;

        if (siteKey == WebsiteEditorSiteKeys.Business && commerceBusinessId.HasValue)
        {
            phone = facts?.Phone;
            email = facts?.ContactEmail;
            var settings = await _db.CommerceBusinessStorefrontSettings.AsNoTracking()
                .SingleOrDefaultAsync(row => row.CommerceBusinessId == commerceBusinessId.Value, cancellationToken);
            if (settings?.BookingEnabled == true)
                bookingUrl = settings.BookingFallbackUrl ?? settings.BookingEmbedUrl;
        }
        else if (siteKey == WebsiteEditorSiteKeys.Protect)
        {
            var profile = await _db.AgentProfiles.AsNoTracking()
                .Where(row => row.AgentUserId == ownerUserId && row.IsActive)
                .OrderByDescending(row => row.UpdatedUtc)
                .FirstOrDefaultAsync(cancellationToken);
            phone = profile?.Phone;
            var resolver = ControllerContext.HttpContext?.RequestServices
                .GetService(typeof(ProtectWebsite.Services.Booking.IPublicBookingResolver))
                as ProtectWebsite.Services.Booking.IPublicBookingResolver;
            if (resolver is not null)
            {
                var booking = await resolver.ResolveAsync(
                    new ProtectWebsite.Services.Booking.PublicBookingResolveContext(
                        AgentUserId: ownerUserId,
                        AgentSlug: agentSlug),
                    cancellationToken);
                if (booking.Enabled)
                    bookingUrl = booking.FallbackUrl ?? booking.EmbedUrl;
            }
        }

        return WebsiteCallToActionCatalog.Build(siteKey, phone, email, bookingUrl);
    }

    private async Task<bool> CanPublishAsync(WebsiteEditorTicket actor, CancellationToken cancellationToken) =>
        actor.SiteKey != WebsiteEditorSiteKeys.Business ||
        actor.CommerceBusinessId.HasValue &&
        actor.ActorClientProfileId.HasValue &&
        await WebsiteBusinessAccess.CanPublishAsync(
            _db,
            actor.CommerceBusinessId.Value,
            actor.ActorClientProfileId.Value,
            cancellationToken);

    public sealed record ScheduleRequest(string Ticket, long ExpectedRevision, DateTime? PublishUtc);
    [HttpPost("manage/schedule")]
    public async Task<IActionResult> Schedule([FromBody] ScheduleRequest request, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken)) return Forbid();
        if (request.PublishUtc.HasValue && (request.PublishUtc.Value.Kind != DateTimeKind.Utc || request.PublishUtc <= DateTime.UtcNow)) return BadRequest(new { error = "future_utc_required" });
        var state = await StateAsync(actor, cancellationToken);
        if (state.Revision != request.ExpectedRevision) return Conflict(new { error = "revision_conflict" });
        state.Revision++;
        state.ScheduledRevision = state.Revision;
        state.ScheduledPublishUtc = request.PublishUtc;
        state.ScheduledActorJson = request.PublishUtc.HasValue ? JsonSerializer.Serialize(actor, JsonOptions) : null;
        state.ScheduleError = null;
        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "revision_conflict" }); }
        return Ok(new { revision = state.Revision, schedule = new { publishUtc = state.ScheduledPublishUtc } });
    }

    [HttpGet("/.well-known/legend-website")]
    public async Task<IActionResult> DomainProof(CancellationToken cancellationToken = default)
    {
        var host = Request.Host.Host.ToLowerInvariant();
        var binding = await _db.Set<WebsiteDomainBinding>().AsNoTracking().SingleOrDefaultAsync(d => d.Hostname == host && d.Status != "removing", cancellationToken);
        if (binding is null || !await _db.CommerceBusinesses.AnyAsync(b => b.Id == binding.CommerceBusinessId && b.IsActive && b.Status == "Active", cancellationToken)) return NotFound();
        return Ok(new { businessId = binding.CommerceBusinessId, bindingId = binding.Id });
    }

    private Task<WebsiteEditorTicket?> AuthorizeAsync(string token, CancellationToken cancellationToken) =>
        WebsiteTicketAuthorization.ResolveAsync(_db, _tickets, _configuration, token, cancellationToken);

    private async Task<WebsiteContentState> StateAsync(WebsiteEditorTicket actor, CancellationToken cancellationToken)
    {
        var state = await _db.Set<WebsiteContentState>().SingleOrDefaultAsync(s => s.OwnerKey == actor.OwnerUserId && s.SiteKey == actor.SiteKey, cancellationToken);
        if (state is not null) return state;
        // Legacy content remains the initial published snapshot, never a second write authority.
        var legacy = await _db.AgentFinanceToolStates.AsNoTracking().SingleOrDefaultAsync(s => s.AgentUserId == actor.OwnerUserId && s.ToolId == ToolId(actor.SiteKey), cancellationToken);
        state = new WebsiteContentState { OwnerKey = actor.OwnerUserId, SiteKey = actor.SiteKey, DraftJson = legacy?.JsonState ?? "{}" };
        _db.Set<WebsiteContentState>().Add(state);
        if (legacy is not null)
        {
            var version = new WebsiteContentVersion { StateId = state.Id, Revision = 0, DocumentJson = legacy.JsonState, ActorUserId = actor.ActorUserId! };
            _db.Set<WebsiteContentVersion>().Add(version);
            state.PublishedVersionId = version.Id;
        }
        await _db.SaveChangesAsync(cancellationToken);
        return state;
    }

    private static WebsiteContentDocument Read(string json) => WebsiteContentSanitizer.Sanitize(
        JsonSerializer.Deserialize<WebsiteContentDocument>(json, JsonOptions) ?? new());

    private async Task<WebsiteContentDocument?> LoadAsync(string ownerUserId, string siteKey, CancellationToken cancellationToken)
    {
        var state = await _db.Set<WebsiteContentState>().AsNoTracking().SingleOrDefaultAsync(s => s.OwnerKey == ownerUserId && s.SiteKey == siteKey, cancellationToken);
        if (state is not null)
        {
            if (!state.PublishedVersionId.HasValue) return null;
            var version = await _db.Set<WebsiteContentVersion>().AsNoTracking().SingleAsync(v => v.Id == state.PublishedVersionId && v.StateId == state.Id, cancellationToken);
            return Read(version.DocumentJson);
        }
        var legacy = await _db.AgentFinanceToolStates.AsNoTracking().SingleOrDefaultAsync(s => s.AgentUserId == ownerUserId && s.ToolId == ToolId(siteKey), cancellationToken);
        return legacy is null ? (siteKey == WebsiteEditorSiteKeys.Business ? null : new WebsiteContentDocument()) : Read(legacy.JsonState);
    }

    private async Task<string?> ResolveProtectOwnerKeyAsync(
        string? agentSlug,
        CancellationToken cancellationToken)
    {
        AgentTrackingProfile? profile = null;
        var slug = (agentSlug ?? string.Empty).Trim();

        if (slug.Length > 0)
        {
            var normalized = slug.ToLower();
            profile = await _db.AgentTrackingProfiles
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Slug.ToLower() == normalized, cancellationToken);
        }
        else
        {
            var founderUpn = (_configuration["Founder:Upn"] ?? string.Empty).Trim().ToLower();
            if (founderUpn.Length > 0)
            {
                profile = await _db.AgentTrackingProfiles
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.AgentUpn.ToLower() == founderUpn, cancellationToken);
            }
        }

        return profile is null ? null : NormalizeOwner(profile.AgentUserId);
    }

    private async Task<CommerceBusiness?> ResolveBusinessAsync(
        Guid? businessId,
        CancellationToken cancellationToken)
    {
        if (!businessId.HasValue || businessId == Guid.Empty)
            return null;

        return await _db.CommerceBusinesses
            .AsNoTracking()
            .SingleOrDefaultAsync(
                business => business.Id == businessId.Value &&
                            business.IsActive &&
                            business.Status == "Active",
                cancellationToken);
    }

    private static string NormalizeSiteKey(string? value)
    {
        var key = (value ?? string.Empty).Trim().ToLowerInvariant();
        return key is
            WebsiteEditorSiteKeys.Protect or
            WebsiteEditorSiteKeys.Legend or
            WebsiteEditorSiteKeys.Business
            ? key
            : string.Empty;
    }

    private static string NormalizeOwner(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string ToolId(string siteKey)
        => WebsiteEditorSiteKeys.ToolPrefix + siteKey;


}
