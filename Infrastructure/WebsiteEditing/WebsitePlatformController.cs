using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.WebsiteEditing.Controllers;

public class WebsitePlatformController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly MasterAppDbContext _db;
    private readonly WebsiteEditorTicketProtector _tickets;
    private readonly IConfiguration _configuration;

    public WebsitePlatformController(
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
        WebsiteSystemTemplateAuthority.Apply(siteKey, document);
        var publicFacts = business is null ? null : await WebsiteBusinessFacts.LoadAsync(_db, business.Id, cancellationToken);
        IReadOnlyDictionary<string, WebsiteCollectionProjection> publicCollections = business is null
            ? new Dictionary<string, WebsiteCollectionProjection>(StringComparer.Ordinal)
            : await new WebsiteCollectionProjectionService(_db).LoadAsync(document, business.Id, cancellationToken);
        var publicStoreScope = await PublishedStoreScopeAsync(ownerKey, siteKey, document, cancellationToken);
        return Ok(new
        {
            siteKey,
            business = business is null ? null : new { business.Id, business.DisplayName, business.LegalName, business.BusinessType },
            businessName = business?.DisplayName,
            facts = publicFacts,
            collections = publicCollections.Values,
            store = await StorePayloadAsync(siteKey, document, publicStoreScope, ticket: null, cancellationToken),
            document,
            legacyMigration = document.LegacyMigration
        });
    }

    [HttpGet("public/{siteKey}/favicon")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> PublicFavicon(
        string siteKey,
        [FromQuery] string? agentSlug = null,
        CancellationToken cancellationToken = default)
    {
        siteKey = NormalizeSiteKey(siteKey);
        if (siteKey is not (WebsiteEditorSiteKeys.Legend or WebsiteEditorSiteKeys.Protect))
            return NotFound();

        var ownerKey = siteKey == WebsiteEditorSiteKeys.Legend
            ? WebsiteEditorSiteKeys.GlobalOwnerKey
            : await ResolveProtectOwnerKeyAsync(agentSlug, cancellationToken);
        var document = string.IsNullOrWhiteSpace(ownerKey)
            ? null
            : await LoadAsync(ownerKey, siteKey, cancellationToken);

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "0";

        var favicon = document?.FaviconImageDataUrl;
        return Redirect(string.IsNullOrWhiteSpace(favicon)
            ? WebsiteFaviconParity.FallbackUrl(_configuration)
            : favicon);
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
        var document = scope.PublishedVersion is null ? new WebsiteContentDocument() : Read(scope.PublishedVersion.DocumentJson);
        var actions = await BuildCallToActionCatalogAsync(
            scope.SiteKey,
            scope.OwnerKey,
            agentSlug: null,
            scope.CommerceBusinessId,
            facts,
            cancellationToken, document);

        var owner = await Infrastructure.Analytics.CanonicalAdvertisingEventProjection.ResolveOwnerAsync(_db, _configuration, scope, cancellationToken);
        var browser = await HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingBrowserConfigurationService>()
            .GetAsync(owner, cancellationToken);
        var metaOptions = HttpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsSnapshot<Infrastructure.Analytics.MetaSignalIntelligenceOptions>>().Value;

        var apiBase = WebsiteContentApiBaseUrl();

        return Ok(new
        {
            siteKey = scope.SiteKey,
            publishedVersionId = scope.PublishedVersion?.Id,
            ctaCatalog = new { options = actions },
            analytics = new
            {
                endpoint = apiBase + "/api/tracking/ingest",
                attributionScope = browser.MarketingOwnerKey,
                metaTestMode = browser.MetaTestMode,
                allowedBrowserEvents = Shared.Analytics.AnalyticsEventCatalog.BrowserAllowedEventNames,
                behaviors = Shared.Analytics.AnalyticsEventCatalog.Behaviors,
                signalAliases = Shared.Analytics.MetaSignalAnalyticsAliasCatalog.BrowserProjectionMap,
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
                endpoint = apiBase + "/api/tracking/ingest",
                pixelId = browser.MetaPixelId,
                metaTestMode = browser.MetaTestMode,
                browserEventNames = Shared.Analytics.MetaSignalEventCatalog.BrowserPixelEventNames,
                browserSignalEventNames = Shared.Analytics.MetaSignalEventCatalog.Definitions
                    .Where(definition => !Shared.Analytics.MetaSignalEventCatalog.IsServerAuthorityEvent(definition.Name))
                    .Select(definition => definition.Name)
                    .ToArray(),
                weights = metaOptions.Weights
            },
            openai = new
            {
                enabled = !string.IsNullOrWhiteSpace(browser.OpenAiPixelId),
                pixelId = browser.OpenAiPixelId,
                accountApproved = browser.OpenAiAccountApproved,
                conversionsApiConfigured = browser.OpenAiConversionsApiConfigured
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
        var commerceService = HttpContext?.RequestServices?.GetService(typeof(WebsiteCommerceScopeService)) as WebsiteCommerceScopeService;
        var commerceScope = commerceService is null
            ? null
            : await commerceService.ResolveAsync(actor, state, createIfMissing: false, cancellationToken);
        var history = await _db.Set<WebsiteContentVersion>().AsNoTracking().Where(v => v.StateId == state.Id)
            .OrderByDescending(v => v.Revision).Select(v => new { versionId = v.Id, v.Revision, v.CreatedUtc }).ToListAsync(cancellationToken);
        var business = actor.CommerceBusinessId.HasValue ? await _db.CommerceBusinesses.AsNoTracking().SingleAsync(b => b.Id == actor.CommerceBusinessId, cancellationToken) : null;
        var facts = business is null ? null : await WebsiteBusinessFacts.LoadAsync(_db, business.Id, cancellationToken);
        var draft = Read(state.DraftJson);
        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, draft);
        IReadOnlyDictionary<string, WebsiteCollectionProjection> collectionData = business is null
            ? new Dictionary<string, WebsiteCollectionProjection>(StringComparer.Ordinal)
            : await new WebsiteCollectionProjectionService(_db).LoadCatalogAsync(business.Id, cancellationToken);
        var ctaOptions = await BuildCallToActionCatalogAsync(actor, facts, cancellationToken, draft);
        return Ok(new { business = business is null ? null : new { business.Id, business.DisplayName, business.LegalName, business.BusinessType }, siteKey = actor.SiteKey, agentSlug = actor.AgentSlug, commerceBusinessId = actor.CommerceBusinessId, document = draft, legacyMigration = draft.LegacyMigration,
            revision = state.Revision, publishedRevision = history.FirstOrDefault(v => v.versionId == state.PublishedVersionId)?.Revision,
            facts,
            dataCatalog = WebsiteCollectionSourcePolicy.Catalog,
            collections = collectionData.Values,
            ctaCatalog = new { options = ctaOptions },
            store = await StorePayloadAsync(actor.SiteKey, draft, commerceScope, ticket, cancellationToken),
            usage = new { mediaBytes = await _db.Set<WebsiteMediaAsset>().Where(a => a.OwnerKey == actor.OwnerUserId).SumAsync(a => (long?)a.SizeBytes, cancellationToken) ?? 0, mediaCount = await _db.Set<WebsiteMediaAsset>().CountAsync(a => a.OwnerKey == actor.OwnerUserId, cancellationToken), publishedVersions = history.Count },
            importReport = string.IsNullOrEmpty(state.ImportReportJson) ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(state.ImportReportJson),
            drafts = ReadDrafts(state).Select(d => new { d.Id, d.Name, d.UpdatedUtc }),
            history, signalCatalog = SignalCatalogPayload(), agentContract = WebsiteStudioAgentContract.Payload, capabilities = new {
                canPublish = await CanPublishAsync(actor, cancellationToken),
                canManageDomains = await CanPublishAsync(actor, cancellationToken),
                canImport = actor.SiteKey == WebsiteEditorSiteKeys.Business,
                canSchedule = await CanPublishAsync(actor, cancellationToken),
                canDelete = await CanPublishAsync(actor, cancellationToken),
                canPromote = await CanPublishAsync(actor, cancellationToken),
                compositionV3 = true,
                browserAgentWorkspace = true,
                externalAiApi = false,
                requiresCompositionMaterialization = draft.LegacyMigration is not null
            },
            schedule = new { publishUtc = state.ScheduledPublishUtc, error = state.ScheduleError },
            readiness = new { checks = new[] { new { passed = true, message = "Draft is isolated from published content. Publishing validates and compiles the complete website." } } } });
    }

    [HttpGet("manage/source")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> SiteSource(
        [FromQuery] string ticket,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();

        var state = await StateAsync(actor, cancellationToken);
        var document = Read(state.DraftJson);
        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, document);
        if (document.LegacyMigration is not null)
            return Ok(new
            {
                source = "legend_site_source",
                revision = state.Revision,
                requiresMaterialization = true,
                schema = WebsiteSiteSource.Schema
            });

        try
        {
            var sourceText = WebsiteSiteSource.Serialize(document);
            return Ok(new
            {
                source = "legend_site_source",
                revision = state.Revision,
                requiresMaterialization = false,
                schema = WebsiteSiteSource.Schema,
                text = sourceText,
                sourceMap = WebsiteSiteSource.BuildSourceMap(sourceText)
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { error = "website_site_source_unavailable", message = ex.Message });
        }
    }

    [HttpPost("manage/source/validate")]
    [RequestSizeLimit(2_500_000)]
    public async Task<IActionResult> ValidateSiteSource(
        [FromBody] WebsiteSiteSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();

        var state = await StateAsync(actor, cancellationToken);
        if (state.Revision != request.ExpectedRevision)
            return Conflict(new { error = "revision_conflict", revision = state.Revision });

        var baseline = Read(state.DraftJson);
        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, baseline);
        if (baseline.LegacyMigration is not null)
            return Conflict(new
            {
                error = "website_site_source_materialization_required",
                message = "Materialize the current website into the canonical v3 composition graph before editing Site Source."
            });

        CommerceBusiness? business = null;
        WebsiteBusinessFacts? facts = null;
        if (actor.SiteKey == WebsiteEditorSiteKeys.Business && actor.CommerceBusinessId.HasValue)
        {
            business = await _db.CommerceBusinesses.AsNoTracking()
                .SingleAsync(value => value.Id == actor.CommerceBusinessId.Value, cancellationToken);
            facts = await WebsiteBusinessFacts.LoadAsync(_db, actor.CommerceBusinessId.Value, cancellationToken);
        }

        try
        {
            var options = await BuildCallToActionCatalogAsync(actor, facts, cancellationToken, baseline);
            var parsed = WebsiteSiteSource.Parse(request.Source, baseline, options);
            WebsiteSiteSource.EnsureSelectedNodeOnly(baseline, parsed.Document, request.SelectedNodeId);
            await ValidateCompositionMediaOwnershipAsync(actor, parsed.Document, cancellationToken);
            var normalized = WebsiteSiteSource.Serialize(parsed.Document);
            return Ok(new
            {
                source = "legend_site_source_validation",
                baseRevision = state.Revision,
                persisted = false,
                published = false,
                text = normalized,
                proposedDocument = parsed.Document,
                sourceMap = parsed.SourceMap
            });
        }
        catch (WebsiteSiteSourceProtectionException ex)
        {
            return BadRequest(new
            {
                error = "website_site_source_protected",
                message = ex.Message,
                canonicalProtectionViolation = true,
                correction = WebsiteStudioAgentContract.ProtectedEditCorrection
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new
            {
                error = "website_site_source_invalid",
                message = ex.Message,
                canonicalProtectionViolation = false
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new
            {
                error = ex.Message,
                message = "LEGEND Site Source could not be validated. No draft changes were saved.",
                canonicalProtectionViolation = false
            });
        }
    }

    public sealed record StoreActionRequest(
        string Ticket,
        long ExpectedRevision,
        string? NavigationLabel = null,
        string? CartIcon = null,
        int? CartIconSizePx = null);

    [HttpPost("manage/store/enable")]
    public async Task<IActionResult> EnableStore(
        [FromBody] StoreActionRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();

        var state = await StateAsync(actor, cancellationToken);
        if (state.Revision != request.ExpectedRevision)
            return Conflict(new { error = "revision_conflict" });

        var scope = await HttpContext.RequestServices.GetRequiredService<WebsiteCommerceScopeService>()
            .ResolveAsync(actor, state, createIfMissing: true, cancellationToken)
            ?? throw new InvalidOperationException("The commerce scope could not be created.");

        var document = Read(state.DraftJson);
        if (document.LegacyMigration is not null)
            return Conflict(new { error = "website_materialization_required", message = "Open Website Studio to materialize this legacy draft before changing store settings." });
        document.Store.Enabled = true;
        if (!string.IsNullOrWhiteSpace(request.NavigationLabel))
            document.Store.NavigationLabel = request.NavigationLabel.Trim();
        if (!string.IsNullOrWhiteSpace(request.CartIcon))
            document.Store.CartIcon = request.CartIcon.Trim();
        if (request.CartIconSizePx.HasValue)
            document.Store.CartIconSizePx = request.CartIconSizePx.Value;

        document = WebsiteContentSanitizer.Sanitize(document);
        state.DraftJson = JsonSerializer.Serialize(document, JsonOptions);
        state.Revision++;
        state.UpdatedUtc = DateTime.UtcNow;
        state.ScheduledPublishUtc = null;
        state.ScheduledActorJson = null;
        state.ScheduledRevision = null;
        state.ScheduleError = null;

        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "revision_conflict" }); }

        return Ok(new
        {
            document,
            revision = state.Revision,
            store = await StorePayloadAsync(actor.SiteKey, document, scope, request.Ticket, cancellationToken)
        });
    }

    [HttpPost("manage/store/remove")]
    public async Task<IActionResult> RemoveStore(
        [FromBody] StoreActionRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();

        var state = await StateAsync(actor, cancellationToken);
        if (state.Revision != request.ExpectedRevision)
            return Conflict(new { error = "revision_conflict" });

        var document = Read(state.DraftJson);
        if (document.LegacyMigration is not null)
            return Conflict(new { error = "website_materialization_required", message = "Open Website Studio to materialize this legacy draft before changing store settings." });
        document.Store.Enabled = false;
        document = WebsiteContentSanitizer.Sanitize(document);
        state.DraftJson = JsonSerializer.Serialize(document, JsonOptions);
        state.Revision++;
        state.UpdatedUtc = DateTime.UtcNow;
        state.ScheduledPublishUtc = null;
        state.ScheduledActorJson = null;
        state.ScheduledRevision = null;
        state.ScheduleError = null;

        var scope = await HttpContext.RequestServices.GetRequiredService<WebsiteCommerceScopeService>()
            .ResolveAsync(actor, state, createIfMissing: false, cancellationToken);

        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "revision_conflict" }); }

        return Ok(new
        {
            document,
            revision = state.Revision,
            store = await StorePayloadAsync(actor.SiteKey, document, scope, request.Ticket, cancellationToken)
        });
    }

    public sealed record WebsiteSiteSourceRequest(
        string Ticket,
        long ExpectedRevision,
        string Source,
        string? SelectedNodeId = null);

    public sealed record WebsiteSignalUpdateRequest(
        string Ticket,
        long ExpectedRevision,
        string PagePath,
        string ElementId,
        List<WebsiteSignalBinding>? Signals,
        string? FieldKey = null);

    public sealed record WebsiteSignalTestRequest(
        string Ticket,
        long ExpectedRevision,
        string PagePath,
        string ElementId,
        string BindingId,
        string? FieldKey = null);

    public sealed record WebsiteStudioCommentCreateRequest(
        string Ticket,
        long ExpectedRevision,
        string PagePath,
        string? ElementId,
        string Body,
        Guid? ParentCommentId = null);

    public sealed record WebsiteStudioCommentStatusRequest(
        string Ticket,
        Guid CommentId,
        string Status);


    public sealed record SaveRequest(string Ticket, WebsiteContentDocument Document, long? ExpectedRevision = null, Guid? DraftId = null, string? DraftName = null);
    public sealed record ProfileRequest(string Ticket, BusinessWebsiteProfileInput Settings);
    private object SignalCatalogPayload() => new { events = WebsiteSignalBindingPolicy.Options, automaticBehaviors = Shared.Analytics.AnalyticsEventCatalog.Behaviors.Where(behavior => !string.IsNullOrWhiteSpace(behavior.AutomaticTrigger)), matchingFields = WebsiteSignalBindingPolicy.ApprovedMatchingFields, runtimeEnabled = _configuration.GetValue<bool>("WebsiteMarketing:Enabled") };

    [HttpGet("manage/signal-catalog")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> SignalCatalog([FromQuery] string ticket, CancellationToken cancellationToken)
    {
        if (await AuthorizeAsync(ticket, cancellationToken) is null) return Unauthorized();
        return Ok(SignalCatalogPayload());
    }

    [HttpGet("manage/event-map")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> EventMap([FromQuery] string ticket, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        return Ok(new { windowDays = 30, source = "published_configuration_and_receipts",
            entries = await new WebsiteEventMapQuery(_db, _configuration).ReadTicketAsync(actor, cancellationToken) });
    }

    [HttpPost("manage/signals")]
    public async Task<IActionResult> UpdateSignals(
        [FromBody] WebsiteSignalUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();

        var state = await StateAsync(actor, cancellationToken);
        if (state.Revision != request.ExpectedRevision)
            return Conflict(new { error = "revision_conflict", revision = state.Revision });

        var document = Read(state.DraftJson);
        if (document.LegacyMigration is not null)
            return Conflict(new
            {
                error = "website_materialization_required",
                message = "Materialize this website into canonical v3 before editing signal mappings."
            });

        if (!TryFindSignalTarget(
                document,
                request.PagePath,
                request.ElementId,
                request.FieldKey,
                out var target,
                out var fieldKey))
            return NotFound(new { error = "website_signal_target_not_found" });

        List<WebsiteSignalBinding> signals;
        try
        {
            signals = WebsiteSignalBindingPolicy.Validate(request.Signals);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = "website_signal_mapping_invalid", message = ex.Message });
        }

        if (fieldKey is null)
        {
            target.Signals = signals;
        }
        else
        {
            target.FieldSignals ??= new Dictionary<string, List<WebsiteSignalBinding>>(StringComparer.Ordinal);
            if (signals.Count == 0) target.FieldSignals.Remove(fieldKey);
            else target.FieldSignals[fieldKey] = signals;
        }

        document = WebsiteContentSanitizer.Sanitize(document);
        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, document);
        state.ScheduledPublishUtc = null;
        state.ScheduledActorJson = null;
        state.ScheduledRevision = null;
        state.ScheduleError = null;
        state.DraftJson = JsonSerializer.Serialize(document, JsonOptions);
        state.Revision++;
        state.UpdatedUtc = DateTime.UtcNow;

        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { error = "revision_conflict" });
        }

        return Ok(new
        {
            source = "website_signal_configuration",
            revision = state.Revision,
            document,
            elementId = request.ElementId,
            fieldKey,
            signals
        });
    }

    [HttpGet("manage/signals/health")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> SignalHealth(
        [FromQuery] string ticket,
        [FromQuery] string pagePath,
        [FromQuery] string elementId,
        [FromQuery] string bindingId,
        [FromQuery] string? fieldKey = null,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var state = await StateAsync(actor, cancellationToken);
        var document = Read(state.DraftJson);
        if (!TryFindSignalBinding(document, pagePath, elementId, fieldKey, bindingId, out var binding))
            return NotFound(new { error = "website_signal_binding_not_found" });
        if (!Shared.Analytics.AnalyticsEventCatalog.TryGetBehavior(binding.EventName, out var definition))
            return BadRequest(new { error = "website_signal_event_invalid" });

        var destination = await ResolveSignalDestinationAsync(actor, cancellationToken);
        var analyticsRows = new List<AnalyticsEvent>();
        var metaRows = new List<MetaSignalEvent>();
        if (state.PublishedVersionId.HasValue)
        {
            analyticsRows = await _db.AnalyticsEvents.AsNoTracking()
                .Where(row => row.WebsiteContentVersionId == state.PublishedVersionId &&
                              row.WebsiteBindingId == binding.Id)
                .OrderByDescending(row => row.Id)
                .Take(10)
                .ToListAsync(cancellationToken);
            metaRows = await _db.MetaSignalEvents.AsNoTracking()
                .Where(row => row.WebsiteContentVersionId == state.PublishedVersionId &&
                              row.WebsiteBindingId == binding.Id)
                .OrderByDescending(row => row.Id)
                .Take(10)
                .ToListAsync(cancellationToken);
        }

        return Ok(new
        {
            source = "website_signal_existing_authorities",
            state.Revision,
            publishedVersionId = state.PublishedVersionId,
            binding = SignalBindingPayload(binding, definition, destination),
            destination = SignalDestinationPayload(destination),
            analytics = analyticsRows.Select(row => new
            {
                row.EventType,
                row.ReceivedUtc,
                row.EventUtc,
                row.PageKey,
                row.Path,
                row.WebsiteContentVersionId,
                row.WebsiteBindingId
            }),
            meta = metaRows.Select(row => new
            {
                row.EventName,
                row.CreatedUtc,
                row.MetaBrowserSent,
                row.MetaServerSent,
                row.WebsiteContentVersionId,
                row.WebsiteBindingId,
                dispatch = SafeDispatchMetadata(row.MetadataJson)
            })
        });
    }

    [HttpPost("manage/signals/test")]
    public async Task<IActionResult> SignalDryRun(
        [FromBody] WebsiteSignalTestRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var state = await StateAsync(actor, cancellationToken);
        if (state.Revision != request.ExpectedRevision)
            return Conflict(new { error = "revision_conflict", revision = state.Revision });
        var document = Read(state.DraftJson);
        if (!TryFindSignalBinding(document, request.PagePath, request.ElementId, request.FieldKey, request.BindingId, out var binding))
            return NotFound(new { error = "website_signal_binding_not_found" });
        if (!Shared.Analytics.AnalyticsEventCatalog.TryGetBehavior(binding.EventName, out var definition))
            return BadRequest(new { error = "website_signal_event_invalid" });

        var destination = await ResolveSignalDestinationAsync(actor, cancellationToken);
        var serverAuthority = definition.RequiresServerAuthority;
        var browserTrigger = binding.Trigger is "viewed" or "click" or "form_started" or "submit_attempt"
            or "field_started" or "validation_failed" or "field_completed" or "scroll_threshold";
        var analyticsWouldAccept = (binding.DeliveryMode is "analytics" or "meta" or "destinations") && browserTrigger && !serverAuthority;
        var pixelWouldInvoke = (binding.DeliveryMode is "meta" or "destinations") && browserTrigger &&
            definition.BrowserAllowed && destination.HasBrowserPixel;
        var serverCapiRequiresVerifiedOutcome = (binding.DeliveryMode is "meta" or "destinations") && serverAuthority;

        return Ok(new
        {
            source = "website_signal_private_dry_run",
            dryRun = true,
            persisted = false,
            metaDispatched = false,
            state.Revision,
            binding = SignalBindingPayload(binding, definition, destination),
            destination = SignalDestinationPayload(destination),
            stages = new
            {
                mappingValidated = true,
                browserTriggerSupported = browserTrigger,
                browserAnalyticsWouldBeAccepted = analyticsWouldAccept,
                browserPixelWouldInvoke = pixelWouldInvoke,
                serverOutcomeRequired = serverAuthority,
                serverCapiWouldRequireVerifiedOutcome = serverCapiRequiresVerifiedOutcome,
                serverCapiDestinationReady = serverCapiRequiresVerifiedOutcome && destination.HasServerCapiCredentials
            }
        });
    }

    [HttpGet("manage/collaboration")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Collaboration(
        [FromQuery] string ticket,
        [FromQuery] string pagePath,
        [FromQuery] string? elementId = null,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var state = await StateAsync(actor, cancellationToken);
        var document = Read(state.DraftJson);
        var route = NormalizeCollaborationPagePath(pagePath);
        if (route is null || !document.Pages.ContainsKey(route))
            return BadRequest(new { error = "invalid_collaboration_page" });

        var role = await ResolveCollaborationRoleAsync(actor, cancellationToken);
        var comments = await _db.Set<WebsiteStudioComment>().AsNoTracking()
            .Where(comment =>
                comment.WebsiteContentStateId == state.Id &&
                comment.PagePath == route &&
                (elementId == null || comment.ElementId == elementId))
            .OrderBy(comment => comment.CreatedUtc)
            .Take(250)
            .ToListAsync(cancellationToken);
        var collaborators = await CollaborationRosterAsync(actor, cancellationToken);

        return Ok(new
        {
            source = "website_studio_collaboration",
            revision = state.Revision,
            publishedVersionId = state.PublishedVersionId,
            role,
            collaborators,
            comments = comments.Select(comment => CommentPayload(
                comment,
                role.CanResolveAll || string.Equals(comment.AuthorUserId, actor.ActorUserId, StringComparison.OrdinalIgnoreCase)))
        });
    }

    [HttpPost("manage/collaboration/comments")]
    public async Task<IActionResult> CreateCollaborationComment(
        [FromBody] WebsiteStudioCommentCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var state = await StateAsync(actor, cancellationToken);
        if (state.Revision != request.ExpectedRevision)
            return Conflict(new { error = "revision_conflict", revision = state.Revision });

        var document = Read(state.DraftJson);
        var route = NormalizeCollaborationPagePath(request.PagePath);
        if (route is null || !document.Pages.ContainsKey(route))
            return BadRequest(new { error = "invalid_collaboration_page" });
        var elementId = NormalizeCollaborationElementId(request.ElementId);
        if (request.ElementId is not null && elementId is null)
            return BadRequest(new { error = "invalid_collaboration_element" });
        var body = request.Body?.Trim();
        if (string.IsNullOrWhiteSpace(body) || body.Length > 4000)
            return BadRequest(new { error = "invalid_collaboration_comment", message = "Comments must contain 1–4,000 characters." });

        WebsiteStudioComment? parent = null;
        if (request.ParentCommentId.HasValue)
        {
            parent = await _db.Set<WebsiteStudioComment>().AsNoTracking()
                .SingleOrDefaultAsync(comment =>
                    comment.Id == request.ParentCommentId.Value &&
                    comment.WebsiteContentStateId == state.Id &&
                    comment.PagePath == route,
                    cancellationToken);
            if (parent is null)
                return BadRequest(new { error = "invalid_parent_comment" });
            if (parent.ParentCommentId.HasValue)
                return BadRequest(new { error = "nested_comment_depth_not_supported" });
        }

        var role = await ResolveCollaborationRoleAsync(actor, cancellationToken);
        var comment = new WebsiteStudioComment
        {
            WebsiteContentStateId = state.Id,
            WebsiteContentVersionId = state.PublishedVersionId,
            AnchorRevision = state.Revision,
            PagePath = route,
            ElementId = elementId ?? parent?.ElementId,
            ParentCommentId = parent?.Id,
            Body = body,
            Status = "open",
            AuthorUserId = actor.ActorUserId!.Trim(),
            AuthorEmail = string.IsNullOrWhiteSpace(actor.ActorEmail) ? null : actor.ActorEmail.Trim(),
            AuthorRole = role.RoleKey,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        _db.Set<WebsiteStudioComment>().Add(comment);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            source = "website_studio_collaboration",
            comment = CommentPayload(comment, canResolve: true)
        });
    }

    [HttpPost("manage/collaboration/comments/status")]
    public async Task<IActionResult> SetCollaborationCommentStatus(
        [FromBody] WebsiteStudioCommentStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var status = request.Status?.Trim().ToLowerInvariant();
        if (status is not ("open" or "resolved"))
            return BadRequest(new { error = "invalid_comment_status" });

        var state = await StateAsync(actor, cancellationToken);
        var comment = await _db.Set<WebsiteStudioComment>()
            .SingleOrDefaultAsync(value =>
                value.Id == request.CommentId &&
                value.WebsiteContentStateId == state.Id,
                cancellationToken);
        if (comment is null) return NotFound();

        var role = await ResolveCollaborationRoleAsync(actor, cancellationToken);
        var isAuthor = string.Equals(comment.AuthorUserId, actor.ActorUserId, StringComparison.OrdinalIgnoreCase);
        if (!role.CanResolveAll && !isAuthor) return Forbid();

        comment.Status = status;
        comment.UpdatedUtc = DateTime.UtcNow;
        comment.ResolvedByUserId = status == "resolved" ? actor.ActorUserId : null;
        comment.ResolvedUtc = status == "resolved" ? DateTime.UtcNow : null;
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            source = "website_studio_collaboration",
            comment = CommentPayload(comment, canResolve: true)
        });
    }

    [HttpGet("manage/quality")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> DraftQuality([FromQuery] string ticket, CancellationToken cancellationToken)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();

        var state = await StateAsync(actor, cancellationToken);
        var document = Read(state.DraftJson);
        var report = WebsiteDraftQualityInspector.Inspect(document);
        return Ok(new
        {
            source = "saved_draft_server",
            revision = state.Revision,
            report.CheckedUtc,
            report.ErrorCount,
            report.WarningCount,
            report.Checks
        });
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
    public sealed record DeleteWebsiteRequest(string Ticket, long ExpectedRevision);
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
        try
        {
            var baseline = Read(state.DraftJson);
            document = await NormalizeAuthorableDocumentAsync(
                actor,
                baseline,
                request.Document,
                cancellationToken);
        }
        catch (WebsiteSiteSourceProtectionException ex)
        {
            return BadRequest(new
            {
                error = "website_document_protected",
                message = ex.Message,
                canonicalProtectionViolation = true,
                correction = WebsiteStudioAgentContract.ProtectedEditCorrection
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new
            {
                error = "invalid_website_document",
                message = ex.Message,
                canonicalProtectionViolation = false
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new
            {
                error = ex.Message,
                message = "Materialize the website into the canonical v3 graph before saving."
            });
        }
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
            draft.Name = name;
            draft.DocumentJson = JsonSerializer.Serialize(document, JsonOptions);
            draft.UpdatedUtc = DateTime.UtcNow;
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

    private async Task<WebsiteContentDocument> NormalizeAuthorableDocumentAsync(
        WebsiteEditorTicket actor,
        WebsiteContentDocument baseline,
        WebsiteContentDocument proposed,
        CancellationToken cancellationToken)
    {
        var current = WebsiteContentSanitizer.Sanitize(baseline);
        var candidate = WebsiteContentSanitizer.Sanitize(proposed);

        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, current);
        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, candidate);

        WebsiteBusinessFacts? facts = actor.SiteKey == WebsiteEditorSiteKeys.Business &&
                                     actor.CommerceBusinessId.HasValue
            ? await WebsiteBusinessFacts.LoadAsync(
                _db,
                actor.CommerceBusinessId.Value,
                cancellationToken)
            : null;

        var actions = await BuildCallToActionCatalogAsync(
            actor,
            facts,
            cancellationToken,
            current);

        // One protection authority for Canvas, Selected Source, GPT, autosave,
        // and named drafts. Project the candidate through the same public
        // authoring representation used by Source, then parse it against the
        // current canonical baseline. The parser restores server-owned signals,
        // system/form/data authority and approved action destinations by stable
        // node ID while preserving authorable presentation and free structure.
        var source = WebsiteSiteSource.Serialize(candidate);
        var protectedDocument = WebsiteSiteSource.Parse(
            source,
            current,
            actions,
            validateCanonical: false).Document;

        await ValidateCompositionMediaOwnershipAsync(
            actor,
            protectedDocument,
            cancellationToken);

        return protectedDocument;
    }

    private static List<WebsiteNamedDraft> ReadDrafts(WebsiteContentState state)
    {
        if (string.IsNullOrWhiteSpace(state.NamedDraftsJson)) return [];

        try
        {
            using var parsed = JsonDocument.Parse(state.NamedDraftsJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Array) return [];
            var result = new List<WebsiteNamedDraft>();

            foreach (var item in parsed.RootElement.EnumerateArray().Take(20))
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                Guid id = Guid.NewGuid();
                string name = "Website draft";
                var updatedUtc = DateTime.UtcNow;
                string? documentJson = null;

                foreach (var property in item.EnumerateObject())
                {
                    if (property.Name.Equals("id", StringComparison.OrdinalIgnoreCase) &&
                        Guid.TryParse(property.Value.ToString(), out var parsedId))
                        id = parsedId;
                    else if (property.Name.Equals("name", StringComparison.OrdinalIgnoreCase))
                        name = property.Value.GetString() ?? name;
                    else if (property.Name.Equals("updatedUtc", StringComparison.OrdinalIgnoreCase) &&
                             property.Value.TryGetDateTime(out var parsedUtc))
                        updatedUtc = parsedUtc;
                    else if (property.Name.Equals("documentJson", StringComparison.OrdinalIgnoreCase) &&
                             property.Value.ValueKind == JsonValueKind.String)
                        documentJson = property.Value.GetString();
                    else if (property.Name.Equals("document", StringComparison.OrdinalIgnoreCase) &&
                             property.Value.ValueKind == JsonValueKind.Object)
                        documentJson = property.Value.GetRawText();
                }

                if (string.IsNullOrWhiteSpace(documentJson)) continue;
                result.Add(new WebsiteNamedDraft
                {
                    Id = id,
                    Name = name.Trim().Length is > 0 and <= 100 ? name.Trim() : "Website draft",
                    DocumentJson = documentJson,
                    UpdatedUtc = updatedUtc
                });
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

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
            state.DraftJson = draft.DocumentJson;
            state.ScheduledPublishUtc = null; state.ScheduledRevision = null;
            state.ScheduledActorJson = null; state.ScheduleError = null;
        }
        state.NamedDraftsJson = JsonSerializer.Serialize(drafts, JsonOptions);
        state.Revision++; state.UpdatedUtc = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "revision_conflict" }); }
        return Ok(new { revision = state.Revision, document = Read(state.DraftJson), drafts = drafts.Select(d => new { d.Id, d.Name, d.UpdatedUtc }) });
    }

    [HttpPost("manage/delete")]
    public async Task<IActionResult> DeleteWebsite([FromBody] DeleteWebsiteRequest request, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken)) return Forbid();

        var state = await StateAsync(actor, cancellationToken);
        if (state.Revision != request.ExpectedRevision) return Conflict(new { error = "revision_conflict" });

        state.PublishedVersionId = null;
        state.DraftJson = JsonSerializer.Serialize(new WebsiteContentDocument(), JsonOptions);
        state.NamedDraftsJson = "[]";
        state.ImportReportJson = null;
        state.ScheduledPublishUtc = null;
        state.ScheduledActorJson = null;
        state.ScheduledRevision = null;
        state.ScheduleError = null;
        state.Revision++;
        state.UpdatedUtc = DateTime.UtcNow;

        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "revision_conflict" }); }

        return Ok(new
        {
            revision = state.Revision,
            publishedRevision = (long?)null,
            document = Read(state.DraftJson),
            drafts = Array.Empty<object>()
        });
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
        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, document);
        CommerceBusiness? business = null;
        WebsiteBusinessFacts? facts = null;
        if (actor.SiteKey == WebsiteEditorSiteKeys.Business)
        {
            business = await _db.CommerceBusinesses.AsNoTracking().SingleAsync(b => b.Id == actor.CommerceBusinessId, cancellationToken);
            facts = await WebsiteBusinessFacts.LoadAsync(_db, business.Id, cancellationToken);
        }
        if (document.LegacyMigration is not null)
            return Conflict(new { error = "website_materialization_required", message = "This website must be materialized into the canonical v3 composition graph before publishing." });

        var ctaOptions = await BuildCallToActionCatalogAsync(actor, facts, cancellationToken, document);
        try
        {
            WebsiteSiteSource.ValidateCanonical(document, ctaOptions);
            await ValidateCompositionMediaOwnershipAsync(actor, document, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = "website_preflight_failed", message = ex.Message });
        }
        var ctaError = WebsiteCallToActionCatalog.PrepareForPublish(document, ctaOptions);
        if (ctaError is not null) return BadRequest(new { error = "button_destination_required", message = ctaError });
        state.DraftJson = JsonSerializer.Serialize(document, JsonOptions);
        var version = new WebsiteContentVersion { StateId = state.Id, Revision = state.Revision + 1,
            DocumentJson = state.DraftJson, ImportReportJson = state.ImportReportJson, ActorUserId = actor.ActorUserId! };
        if (business is not null)
        {
            var collections = await new WebsiteCollectionProjectionService(_db)
                .LoadAsync(document, business.Id, cancellationToken);
            var compiler = HttpContext.RequestServices.GetRequiredService<Infrastructure.WebsitePublishing.WebsitePageCompiler>();
            version.CompiledPagesJson = await compiler.CompileAsync(document, business, facts!, collections, cancellationToken);
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
        return Ok(new { domains = domains.Select(DomainPayload), cnameTarget = DomainService().CnameTarget });
    }

    [HttpPost("manage/domains")]
    public async Task<IActionResult> AddDomain([FromBody] DomainRequest request, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor?.SiteKey != WebsiteEditorSiteKeys.Business) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken)) return Forbid();
        try
        {
            var binding = await DomainService().RegisterAsync(actor.CommerceBusinessId!.Value, request.Hostname, cancellationToken);
            return Ok(new { binding = DomainPayload(binding), cnameTarget = DomainService().CnameTarget });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = "invalid_domain", message = ex.Message });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "domain_provider_unavailable", message = "LEGEND could not reach the domain verification provider. Your website settings were preserved. Try Verify status again." });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "domain_provider_timeout", message = "Domain verification timed out. Your website settings were preserved. Try Verify status again." });
        }
    }

    [HttpPost("manage/domains/refresh")]
    public async Task<IActionResult> RefreshDomain([FromBody] DomainActionRequest request, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor?.SiteKey != WebsiteEditorSiteKeys.Business) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken)) return Forbid();
        try
        {
            var binding = await DomainService().RefreshAsync(actor.CommerceBusinessId!.Value, request.BindingId, cancellationToken);
            return Ok(DomainPayload(binding));
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "domain_provider_unavailable", message = "LEGEND could not reach the domain verification provider. The saved DNS instructions are unchanged. Try Verify status again." });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "domain_provider_timeout", message = "Domain verification timed out. The saved DNS instructions are unchanged. Try Verify status again." });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new { error = "domain_verification_failed", message = "LEGEND received an invalid domain verification response. No website or DNS settings were changed." });
        }
    }

    private static object DomainPayload(WebsiteDomainBinding binding)
    {
        JsonElement? diagnostic = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(binding.VerificationJson))
            {
                using var json = JsonDocument.Parse(binding.VerificationJson);
                diagnostic = json.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
            // A stale legacy diagnostic must never block domain management.
        }

        return new
        {
            binding.Id,
            binding.Hostname,
            binding.Status,
            binding.CertificateStatus,
            binding.LastCheckedUtc,
            diagnostic
        };
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
        var asset = await media.StoreAsync(actor.OwnerUserId, Path.GetFileName(file.FileName), file.FileName, buffer.ToArray(), cancellationToken);
        return Ok(new { id = asset.Id, name = MediaDisplayName(asset), url = MediaBaseUrl() + "/api/website-content/media/" + asset.Id, contentType = asset.ContentType, sizeBytes = asset.SizeBytes, createdUtc = asset.CreatedUtc });
    }

    [HttpGet("manage/media")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> MediaLibrary(
        [FromQuery] string ticket,
        [FromQuery] string? q = null,
        [FromQuery] string? kind = null,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var query = _db.Set<WebsiteMediaAsset>().AsNoTracking().Where(asset => asset.OwnerKey == actor.OwnerUserId);
        var search = q?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(asset => asset.SourceUrl.Contains(search) || asset.ContentType.Contains(search));
        if (string.Equals(kind, "image", StringComparison.OrdinalIgnoreCase))
            query = query.Where(asset => asset.ContentType.StartsWith("image/"));
        else if (string.Equals(kind, "video", StringComparison.OrdinalIgnoreCase))
            query = query.Where(asset => asset.ContentType.StartsWith("video/"));
        else if (!string.IsNullOrWhiteSpace(kind) && !string.Equals(kind, "all", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "invalid_media_kind" });

        var assets = await query.OrderByDescending(asset => asset.CreatedUtc).ThenByDescending(asset => asset.Id).Take(200).ToListAsync(cancellationToken);
        return Ok(new
        {
            assets = assets.Select(asset => new
            {
                asset.Id,
                name = MediaDisplayName(asset),
                url = MediaBaseUrl() + "/api/website-content/media/" + asset.Id,
                asset.ContentType,
                asset.SizeBytes,
                asset.CreatedUtc
            })
        });
    }

    private static string MediaDisplayName(WebsiteMediaAsset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.SourceUrl)) return asset.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? "Website video" : "Website image";
        if (Uri.TryCreate(asset.SourceUrl, UriKind.Absolute, out var uri))
        {
            var name = Path.GetFileName(uri.LocalPath);
            return string.IsNullOrWhiteSpace(name) ? uri.Host : name;
        }
        return Path.GetFileName(asset.SourceUrl);
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
    private async Task ValidateCompositionMediaOwnershipAsync(
        WebsiteEditorTicket actor,
        WebsiteContentDocument document,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<Guid>();
        void Visit(IEnumerable<WebsiteCompositionNode> nodes)
        {
            foreach (var node in nodes ?? [])
            {
                if (node.MediaAssetId.HasValue && node.MediaAssetId.Value != Guid.Empty)
                    ids.Add(node.MediaAssetId.Value);

                if (!node.MediaAssetId.HasValue &&
                    !string.IsNullOrWhiteSpace(node.MediaUrl) &&
                    Uri.TryCreate(node.MediaUrl, UriKind.RelativeOrAbsolute, out var mediaUri))
                {
                    var path = mediaUri.IsAbsoluteUri ? mediaUri.AbsolutePath : node.MediaUrl.Split('?', '#')[0];
                    const string marker = "/api/website-content/media/";
                    var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (index >= 0 && Guid.TryParse(path[(index + marker.Length)..].Trim('/'), out var parsed))
                        ids.Add(parsed);
                }

                Visit(node.Children);
            }
        }

        Visit(document.Shell.Header);
        Visit(document.Shell.Footer);
        foreach (var page in document.Pages.Values) Visit(page.Composition);
        foreach (var component in document.ReusableComponents.Values) Visit(component.Composition);

        if (ids.Count == 0) return;

        var owned = await _db.Set<WebsiteMediaAsset>().AsNoTracking()
            .Where(asset => asset.OwnerKey == actor.OwnerUserId && ids.Contains(asset.Id))
            .Select(asset => asset.Id)
            .ToListAsync(cancellationToken);

        if (owned.Count != ids.Count)
            throw new ArgumentException("Website source references media that is unavailable to this website owner.");
    }

    private string WebsiteContentApiBaseUrl() => (_configuration["WebsiteContentApiBaseUrl"] ?? "https://masterapp-protect.azurewebsites.net").TrimEnd('/');
    private string MediaBaseUrl() => WebsiteContentApiBaseUrl();
    private WebsiteDomainService DomainService() => HttpContext.RequestServices.GetRequiredService<WebsiteDomainService>();

    private Task<IReadOnlyList<WebsiteCallToActionOption>> BuildCallToActionCatalogAsync(
        WebsiteEditorTicket actor,
        WebsiteBusinessFacts? facts,
        CancellationToken cancellationToken, WebsiteContentDocument? document = null) =>
        BuildCallToActionCatalogAsync(
            actor.SiteKey,
            actor.OwnerUserId,
            actor.AgentSlug,
            actor.CommerceBusinessId,
            facts,
            cancellationToken, document);

    private async Task<IReadOnlyList<WebsiteCallToActionOption>> BuildCallToActionCatalogAsync(
        string siteKey,
        string ownerUserId,
        string? agentSlug,
        Guid? commerceBusinessId,
        WebsiteBusinessFacts? facts,
        CancellationToken cancellationToken, WebsiteContentDocument? document = null)
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
                .GetService(typeof(Infrastructure.Bookings.IPublicBookingResolver))
                as Infrastructure.Bookings.IPublicBookingResolver;
            if (resolver is not null)
            {
                var booking = await resolver.ResolveAsync(
                    new Infrastructure.Bookings.PublicBookingResolveContext(
                        AgentUserId: ownerUserId,
                        AgentSlug: agentSlug),
                    cancellationToken);
                if (booking.Enabled)
                    bookingUrl = booking.FallbackUrl ?? booking.EmbedUrl;
            }
        }

        var storeScope = document is null ? null : await PublishedStoreScopeAsync(ownerUserId, siteKey, document, cancellationToken);
        var storeRoot = storeScope is null ? null : await ResolveCanonicalStoreRootAsync(siteKey, storeScope, cancellationToken);
        return WebsiteCallToActionCatalog.Build(siteKey, phone, email, bookingUrl, commerceStorePath: storeRoot);
    }

    private sealed record SignalDestinationStatus(
        string OwnerType,
        bool HasBrowserPixel,
        bool HasServerCapiCredentials,
        bool TestEventCodeConfigured);

    private sealed record CollaborationRole(
        string RoleKey,
        string Label,
        bool CanComment,
        bool CanResolveAll,
        bool CanPublish);

    private async Task<CollaborationRole> ResolveCollaborationRoleAsync(
        WebsiteEditorTicket actor,
        CancellationToken cancellationToken)
    {
        if (actor.SiteKey == WebsiteEditorSiteKeys.Legend)
            return new("founder", "Founder", true, true, true);
        if (actor.SiteKey == WebsiteEditorSiteKeys.Protect)
            return new("agent", "Agent", true, true, true);

        if (!actor.CommerceBusinessId.HasValue || !actor.ActorClientProfileId.HasValue)
            return new("member", "Member", false, false, false);

        var member = await _db.CommerceBusinessMembers.AsNoTracking()
            .SingleOrDefaultAsync(value =>
                value.CommerceBusinessId == actor.CommerceBusinessId.Value &&
                value.ClientProfileId == actor.ActorClientProfileId.Value &&
                value.Status.ToLower() == "active",
                cancellationToken);
        if (member is null || !member.CanManageStorefront)
            return new("member", "Member", false, false, false);

        var canPublish = await WebsiteBusinessAccess.CanPublishAsync(
            _db,
            actor.CommerceBusinessId.Value,
            actor.ActorClientProfileId.Value,
            cancellationToken);
        var roleKey = string.IsNullOrWhiteSpace(member.RoleKey)
            ? "member"
            : member.RoleKey.Trim().ToLowerInvariant();
        var label = roleKey switch
        {
            "owner" => "Owner",
            "account" => "Account manager",
            "platform_owner" => "Platform owner",
            _ => "Website editor"
        };
        return new(roleKey, label, true, canPublish, canPublish);
    }

    private async Task<object[]> CollaborationRosterAsync(
        WebsiteEditorTicket actor,
        CancellationToken cancellationToken)
    {
        if (actor.SiteKey != WebsiteEditorSiteKeys.Business || !actor.CommerceBusinessId.HasValue)
        {
            return
            [
                new
                {
                    roleKey = actor.SiteKey == WebsiteEditorSiteKeys.Legend ? "founder" : "agent",
                    displayName = actor.SiteKey == WebsiteEditorSiteKeys.Legend ? "Founder" : "Agent",
                    canManageStorefront = true,
                    canPublish = true
                }
            ];
        }

        var rows = await (
            from member in _db.CommerceBusinessMembers.AsNoTracking()
            join profile in _db.ClientProfiles.AsNoTracking()
                on member.ClientProfileId equals profile.Id
            where member.CommerceBusinessId == actor.CommerceBusinessId.Value &&
                  member.Status.ToLower() == "active" &&
                  member.CanManageStorefront
            orderby member.DisplayName, profile.FirstName, profile.LastName
            select new
            {
                member.ClientProfileId,
                member.RoleKey,
                member.DisplayName,
                member.CanManageStorefront,
                profile.FirstName,
                profile.LastName
            }).ToListAsync(cancellationToken);

        return rows.Select(row =>
        {
            var roleKey = string.IsNullOrWhiteSpace(row.RoleKey)
                ? "member"
                : row.RoleKey.Trim().ToLowerInvariant();
            var displayName = string.IsNullOrWhiteSpace(row.DisplayName)
                ? string.Join(" ", new[] { row.FirstName, row.LastName }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim()
                : row.DisplayName.Trim();
            return (object)new
            {
                row.ClientProfileId,
                roleKey,
                displayName = string.IsNullOrWhiteSpace(displayName) ? "Website collaborator" : displayName,
                canManageStorefront = row.CanManageStorefront,
                canPublish = roleKey is "owner" or "account"
            };
        }).ToArray();
    }

    private static object CommentPayload(WebsiteStudioComment comment, bool canResolve) => new
    {
        comment.Id,
        comment.WebsiteContentVersionId,
        comment.AnchorRevision,
        comment.PagePath,
        comment.ElementId,
        comment.ParentCommentId,
        comment.Body,
        comment.Status,
        comment.AuthorEmail,
        comment.AuthorRole,
        comment.CreatedUtc,
        comment.UpdatedUtc,
        comment.ResolvedUtc,
        canResolve
    };

    private static string? NormalizeCollaborationPagePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var route = value.Trim().ToLowerInvariant();
        if (!route.StartsWith('/') || route.StartsWith("//") || route.Contains('?') ||
            route.Contains('#') || route.Contains("..") || route.Contains('\\') ||
            route.Any(char.IsControl) || route.Length > 160)
            return null;
        return route.Length > 1 ? route.TrimEnd('/') : route;
    }

    private static string? NormalizeCollaborationElementId(string? value)
    {
        if (value is null) return null;
        var id = value.Trim();
        if (id.Length == 0 || id.Length > 200 || id.Any(char.IsControl))
            return null;
        return id;
    }

    private async Task<SignalDestinationStatus> ResolveSignalDestinationAsync(
        WebsiteEditorTicket actor,
        CancellationToken cancellationToken)
    {
        var owner = await Infrastructure.Analytics.CanonicalAdvertisingEventProjection.ResolveOwnerAsync(
            _db, _configuration, actor, cancellationToken);
        if (owner is null) return new("none", false, false, false);
        var destination = await HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IMetaPixelResolutionService>()
            .ResolveForOwnerAsync(owner, cancellationToken);
        return new(destination.PixelOwnerType, destination.HasBrowserPixel,
            destination.HasServerCapiCredentials, !string.IsNullOrWhiteSpace(destination.TestEventCode));
    }

    private static string? NormalizeSignalFieldKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var chars = value.Trim().Take(160)
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ':')
            .ToArray();
        return chars.Length == 0 ? null : new string(chars).ToLowerInvariant();
    }

    private static WebsiteCompositionNode? FindCompositionNode(
        IEnumerable<WebsiteCompositionNode>? nodes,
        string id)
    {
        foreach (var node in nodes ?? [])
        {
            if (node.Id == id) return node;
            var child = FindCompositionNode(node.Children, id);
            if (child is not null) return child;
        }
        return null;
    }

    private static bool TryFindSignalTarget(
        WebsiteContentDocument document,
        string? pagePath,
        string? elementId,
        string? fieldKey,
        out WebsiteCompositionNode target,
        out string? normalizedFieldKey)
    {
        target = null!;
        normalizedFieldKey = NormalizeSignalFieldKey(fieldKey);
        if (string.IsNullOrWhiteSpace(pagePath) || string.IsNullOrWhiteSpace(elementId) ||
            document.LegacyMigration is not null ||
            !document.Pages.TryGetValue(pagePath, out var page))
            return false;

        var found = FindCompositionNode(page.Composition, elementId);
        if (found is null) return false;

        if (normalizedFieldKey is not null &&
            found.Type != "form" &&
            !WebsiteSystemTemplateAuthority.IsRuntimeFormSystemKey(found.SystemKey))
            return false;

        target = found;
        return true;
    }

    private static bool TryFindSignalBinding(
        WebsiteContentDocument document,
        string? pagePath,
        string? elementId,
        string? fieldKey,
        string? bindingId,
        out WebsiteSignalBinding binding)
    {
        binding = null!;
        if (string.IsNullOrWhiteSpace(pagePath) || string.IsNullOrWhiteSpace(elementId) ||
            string.IsNullOrWhiteSpace(bindingId))
            return false;

        IEnumerable<WebsiteSignalBinding>? bindings = null;
        if (document.LegacyMigration is { } legacy)
        {
            if (!string.IsNullOrWhiteSpace(fieldKey) ||
                !legacy.Pages.TryGetValue(pagePath, out var legacyPage))
                return false;

            if (elementId.StartsWith("extra:", StringComparison.Ordinal))
            {
                var id = elementId.Split(':', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault();
                bindings = legacyPage.Extras.FirstOrDefault(extra => extra.Id == id)?.Signals;
            }
            else if (legacyPage.Elements.TryGetValue(elementId, out var legacyElement))
                bindings = legacyElement.Signals;
        }
        else
        {
            if (!TryFindSignalTarget(document, pagePath, elementId, fieldKey, out var target, out var normalizedFieldKey))
                return false;

            if (normalizedFieldKey is null)
                bindings = target.Signals;
            else if (target.FieldSignals.TryGetValue(normalizedFieldKey, out var fieldBindings))
                bindings = fieldBindings;
        }

        var matches = (bindings ?? []).Where(value => value.Id == bindingId).Take(2).ToArray();
        if (matches.Length != 1) return false;
        binding = matches[0];
        return true;
    }

    private static object SignalBindingPayload(
        WebsiteSignalBinding binding,
        Shared.Analytics.AnalyticsBehaviorContract definition,
        SignalDestinationStatus destination) => new
    {
        binding.Id,
        binding.Trigger,
        binding.EventName,
        binding.ActionKey,
        behaviorKey = definition.Key,
        locked = definition.RequiresServerAuthority,
        binding.DeliveryMode,
        binding.OncePerSession,
        binding.MatchingFields,
        browserSignal = !definition.RequiresServerAuthority,
        browserPixelEligible = definition.BrowserAllowed,
        serverForwardEligible = definition.RequiresServerAuthority,
        serverOutcomeRequired = definition.RequiresServerAuthority,
        matchingConsent = binding.MatchingFields.Count == 0 ? "not_requested" : "verified_server_outcome_required",
        destinationReady = (binding.DeliveryMode is not ("meta" or "destinations")) ||
            (definition.BrowserAllowed ? destination.HasBrowserPixel : destination.HasServerCapiCredentials)
    };

    private static object SignalDestinationPayload(
        SignalDestinationStatus destination) => new
    {
        ownerType = destination.OwnerType,
        browserPixelConfigured = destination.HasBrowserPixel,
        serverCapiConfigured = destination.HasServerCapiCredentials,
        testEventCodeConfigured = destination.TestEventCodeConfigured
    };

    private static object SafeDispatchMetadata(string? json)
    {
        string? StringValue(string name)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var parsed = JsonDocument.Parse(json);
                return parsed.RootElement.TryGetProperty(name, out var value) &&
                       value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            }
            catch (JsonException) { return null; }
        }

        int? IntValue(string name)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var parsed = JsonDocument.Parse(json);
                if (!parsed.RootElement.TryGetProperty(name, out var value)) return null;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
                return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)
                    ? number : null;
            }
            catch (JsonException) { return null; }
        }

        bool? BoolValue(string name)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var parsed = JsonDocument.Parse(json);
                if (!parsed.RootElement.TryGetProperty(name, out var value)) return null;
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
                return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var boolean)
                    ? boolean : null;
            }
            catch (JsonException) { return null; }
        }

        return new
        {
            attempted = BoolValue("metaServerAttempted"),
            sent = BoolValue("metaServerSent"),
            status = StringValue("metaServerStatus"),
            retryable = BoolValue("metaServerRetryable"),
            retryExhausted = BoolValue("metaServerRetryExhausted"),
            attemptCount = IntValue("metaServerAttemptCount"),
            httpStatusCode = IntValue("metaServerHttpStatusCode"),
            eventsReceived = IntValue("metaServerEventsReceived"),
            traceId = StringValue("metaServerTraceId"),
            nextAttemptUtc = StringValue("metaServerNextAttemptUtc"),
            dispatchedUtc = StringValue("metaServerDispatchedUtc")
        };
    }

    public sealed record PromotionRequest(string Ticket, Shared.Analytics.PromotionProposalRequest Promotion);
    public sealed record AdvertisingProposalActionRequest(string Ticket, Guid ProposalId, string Revision);

    [HttpGet("manage/promote/sources")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> PromotionSources(
        [FromQuery] string ticket,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var owner = await ResolveAdvertisingOwnerAsync(actor, cancellationToken);
        if (owner is null) return Forbid();

        try
        {
            var service = HttpContext.RequestServices.GetRequiredService<IPromotionOrchestrationService>();
            return Ok(new
            {
                source = "canonical_promotion_source_inventory",
                items = await service.SourcesAsync(owner, cancellationToken)
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "promotion_sources_unavailable", message = ex.Message });
        }
    }

    [HttpGet("manage/promote/conversions")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> PromotionConversions(
        [FromQuery] string ticket,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var owner = await ResolveAdvertisingOwnerAsync(actor, cancellationToken);
        if (owner is null) return Forbid();

        try
        {
            var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IOpenAiAdsExecutionService>();
            var settings = await service.ListConversionEventSettingsAsync(owner, cancellationToken);
            return Ok(new { source = "scoped_openai_conversion_settings", payload = settings.Payload });
        }
        catch (Exception ex) when (ex is InvalidOperationException or Infrastructure.Analytics.OpenAiAdsExecutionException)
        {
            return BadRequest(new { error = "promotion_conversions_unavailable", message = ex.Message });
        }
    }

    [HttpPost("manage/promote/draft")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> PromotionDraft(
        [FromBody] PromotionRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var owner = await ResolveAdvertisingOwnerAsync(actor, cancellationToken);
        if (owner is null) return Forbid();

        try
        {
            var service = HttpContext.RequestServices.GetRequiredService<IPromotionOrchestrationService>();
            var draft = await service.DraftAsync(owner, request.Promotion, cancellationToken);
            return Ok(new
            {
                source = "canonical_promote_this",
                persisted = false,
                approved = false,
                executable = false,
                draft
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { error = "promotion_draft_invalid", message = ex.Message });
        }
    }

    [HttpPost("manage/promote/propose")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> PromotionPropose(
        [FromBody] PromotionRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken)) return Forbid();
        var owner = await ResolveAdvertisingOwnerAsync(actor, cancellationToken);
        if (owner is null || string.IsNullOrWhiteSpace(actor.ActorUserId)) return Forbid();

        try
        {
            var service = HttpContext.RequestServices.GetRequiredService<IPromotionOrchestrationService>();
            var proposal = await service.ProposeAsync(
                owner,
                request.Promotion,
                actor.ActorUserId,
                cancellationToken);
            return Ok(new
            {
                source = "canonical_advertising_action_ledger",
                requiresApproval = true,
                executed = false,
                proposal
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { error = "promotion_proposal_invalid", message = ex.Message });
        }
    }

    [HttpGet("manage/promote/proposals/{proposalId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> PromotionProposal(
        Guid proposalId,
        [FromQuery] string ticket,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var owner = await ResolveAdvertisingOwnerAsync(actor, cancellationToken);
        if (owner is null) return Forbid();

        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingActionAuthorizationService>();
        var proposal = await service.GetAsync(owner, proposalId, cancellationToken);
        return proposal is null ? NotFound() : Ok(new { source = "canonical_advertising_action_ledger", proposal });
    }

    [HttpPost("manage/promote/approve")]
    public async Task<IActionResult> PromotionApprove(
        [FromBody] AdvertisingProposalActionRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken) || string.IsNullOrWhiteSpace(actor.ActorUserId)) return Forbid();
        var owner = await ResolveAdvertisingOwnerAsync(actor, cancellationToken);
        if (owner is null) return Forbid();

        try
        {
            var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingActionAuthorizationService>();
            var receipt = await service.ApproveAsync(
                owner,
                request.ProposalId,
                actor.ActorUserId,
                request.Revision,
                DateTime.UtcNow.AddMinutes(15),
                cancellationToken);
            return Ok(new { source = "canonical_advertising_action_ledger", approved = true, executed = false, receipt });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { error = "advertising_proposal_revision_conflict" });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { error = "advertising_approval_denied", message = ex.Message });
        }
    }

    [HttpPost("manage/promote/execute")]
    public async Task<IActionResult> PromotionExecute(
        [FromBody] AdvertisingProposalActionRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken)) return Forbid();
        var owner = await ResolveAdvertisingOwnerAsync(actor, cancellationToken);
        if (owner is null) return Forbid();

        try
        {
            var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingActionAuthorizationService>();
            var receipt = await service.ExecuteAsync(owner, request.ProposalId, request.Revision, cancellationToken);
            return Ok(new { source = "canonical_advertising_action_ledger", receipt });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { error = "advertising_proposal_revision_conflict" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "advertising_execution_denied", message = ex.Message });
        }
    }

    [HttpPost("manage/promote/reject")]
    public async Task<IActionResult> PromotionReject(
        [FromBody] AdvertisingProposalActionRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken) || string.IsNullOrWhiteSpace(actor.ActorUserId)) return Forbid();
        var owner = await ResolveAdvertisingOwnerAsync(actor, cancellationToken);
        if (owner is null) return Forbid();

        try
        {
            var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingActionAuthorizationService>();
            var proposal = await service.RejectAsync(owner, request.ProposalId, actor.ActorUserId, request.Revision, cancellationToken);
            return Ok(new { source = "canonical_advertising_action_ledger", proposal });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { error = "advertising_proposal_revision_conflict" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "advertising_rejection_denied", message = ex.Message });
        }
    }

    private Task<Shared.Analytics.MarketingOwnerScope?> ResolveAdvertisingOwnerAsync(
        WebsiteEditorTicket actor, CancellationToken cancellationToken) =>
        Infrastructure.Analytics.CanonicalAdvertisingEventProjection.ResolveOwnerAsync(_db, _configuration, actor, cancellationToken);

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

    private Task<WebsiteEditorTicket?> AuthorizeAsync(string token, CancellationToken cancellationToken) =>
        WebsiteTicketAuthorization.ResolveAsync(_db, _tickets, _configuration, token, cancellationToken);

    private async Task<WebsiteContentState> StateAsync(WebsiteEditorTicket actor, CancellationToken cancellationToken)
    {
        var state = await _db.Set<WebsiteContentState>().SingleOrDefaultAsync(s => s.OwnerKey == actor.OwnerUserId && s.SiteKey == actor.SiteKey, cancellationToken);
        if (state is not null) return state;
        // Legacy content remains the initial published snapshot, never a second write authority.
        var legacy = await _db.AgentFinanceToolStates.AsNoTracking().SingleOrDefaultAsync(s => s.AgentUserId == actor.OwnerUserId && s.ToolId == ToolId(actor.SiteKey), cancellationToken);
        state = new WebsiteContentState
        {
            OwnerKey = actor.OwnerUserId,
            SiteKey = actor.SiteKey,
            CommerceBusinessId = actor.SiteKey == WebsiteEditorSiteKeys.Business ? actor.CommerceBusinessId : null,
            DraftJson = legacy?.JsonState ?? "{}"
        };
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

    private static WebsiteContentDocument Read(string json) => WebsiteContentSanitizer.ReadPersisted(json, JsonOptions);

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

    private string CommercePublicBaseUrl() =>
        (_configuration["Commerce:PublicBaseUrl"] ?? "https://shopparfait.com").TrimEnd('/');

    private async Task<string?> ResolveCanonicalStoreRootAsync(
        string siteKey,
        WebsiteCommerceScope scope,
        CancellationToken cancellationToken)
    {
        if (siteKey == WebsiteEditorSiteKeys.Legend)
            return (_configuration["Commerce:LegendPublicBaseUrl"] ?? "https://mylegnd.com").TrimEnd('/') + "/store";

        if (siteKey == WebsiteEditorSiteKeys.Protect)
            return (_configuration["Commerce:ProtectPublicBaseUrl"] ?? "https://protect.mylegnd.com").TrimEnd('/') +
                   "/store/s/" + Uri.EscapeDataString(scope.BusinessKey);

        if (siteKey != WebsiteEditorSiteKeys.Business)
            return null;

        // Business websites use the same verified custom-domain authority as
        // the public commerce resolver. Never synthesize a mylegnd.com store
        // path for a business tenant.
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var hostname = await _db.Set<WebsiteDomainBinding>()
            .AsNoTracking()
            .Where(binding =>
                binding.CommerceBusinessId == scope.CommerceBusinessId &&
                binding.Status == "active" &&
                binding.CertificateStatus == "active" &&
                binding.LastCheckedUtc >= cutoff)
            .OrderBy(binding => binding.CreatedUtc)
            .Select(binding => binding.Hostname)
            .FirstOrDefaultAsync(cancellationToken);

        return string.IsNullOrWhiteSpace(hostname)
            ? null
            : "https://" + hostname.Trim().TrimEnd('.') + "/store";
    }

    private async Task<object> StorePayloadAsync(
        string siteKey,
        WebsiteContentDocument document,
        WebsiteCommerceScope? scope,
        string? ticket,
        CancellationToken cancellationToken)
    {
        var label = string.IsNullOrWhiteSpace(document.Store.NavigationLabel)
            ? "Store"
            : document.Store.NavigationLabel.Trim();

        if (scope is null)
            return new
            {
                enabled = document.Store.Enabled,
                label,
                cartIcon = document.Store.CartIcon,
                cartIconSizePx = document.Store.CartIconSizePx,
                commerceBusinessId = (Guid?)null,
                businessKey = (string?)null,
                storefrontUrl = (string?)null,
                cartUrl = (string?)null,
                previewUrl = (string?)null,
                managerUrl = (string?)null
            };

        var root = await ResolveCanonicalStoreRootAsync(siteKey, scope, cancellationToken);
        var managerBase = CommercePublicBaseUrl();
        var escapedTicket = string.IsNullOrWhiteSpace(ticket) ? null : Uri.EscapeDataString(ticket);
        return new
        {
            enabled = document.Store.Enabled,
            label,
            cartIcon = document.Store.CartIcon,
            cartIconSizePx = document.Store.CartIconSizePx,
            commerceBusinessId = (Guid?)scope.CommerceBusinessId,
            businessKey = scope.BusinessKey,
            storefrontUrl = root,
            cartUrl = root is null ? null : root + "/cart",
            previewUrl = escapedTicket is null
                ? null
                : managerBase + "/commerce/manage/preview?ticket=" + escapedTicket,
            managerUrl = escapedTicket is null
                ? null
                : managerBase + "/commerce/manage/products?ticket=" + escapedTicket
        };
    }

    private async Task<WebsiteCommerceScope?> PublishedStoreScopeAsync(
        string ownerKey,
        string siteKey,
        WebsiteContentDocument document,
        CancellationToken cancellationToken)
    {
        if (document.Store?.Enabled != true) return null;

        var state = await _db.Set<WebsiteContentState>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerKey == ownerKey && x.SiteKey == siteKey, cancellationToken);
        if (state?.CommerceBusinessId is not Guid commerceBusinessId || commerceBusinessId == Guid.Empty)
            return null;

        var business = await _db.CommerceBusinesses.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == commerceBusinessId &&
                 x.IsActive &&
                 x.Status.ToLower() == "active",
            cancellationToken);
        return business is null
            ? null
            : new WebsiteCommerceScope(business.Id, business.Key, business.DisplayName);
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
