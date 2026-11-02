using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Diagnostics;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Security.UploadValidation;
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
        CancellationToken cancellationToken = default,
        [FromQuery] string? pagePath = null)
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

        var publication = await LoadPublicationAsync(ownerKey, siteKey, cancellationToken);
        var document = publication.Document;
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
        var publicActions = await BuildCallToActionCatalogAsync(
            siteKey,
            ownerKey,
            agentSlug,
            business?.Id,
            publicFacts,
            cancellationToken,
            document);
        var publicStore = await StorePayloadAsync(siteKey, document, publicStoreScope, ticket: null, cancellationToken);
        // Protect keeps its native Razor forms, but needs only this route's body
        // for public composition. Resolve all derived capabilities first, and
        // retain the complete navigation catalog and shared document authority.
        // This is a response-local projection, never an authored document write.
        var publicRoute = NormalizeCollaborationPagePath(pagePath);
        var agentPrefix = "/a/" + (agentSlug ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(agentSlug) && publicRoute is not null)
        {
            if (publicRoute == agentPrefix) publicRoute = "/";
            else if (publicRoute.StartsWith(agentPrefix + "/", StringComparison.Ordinal))
                publicRoute = publicRoute[agentPrefix.Length..];
        }
        if (siteKey == WebsiteEditorSiteKeys.Protect && document.Version == 3 &&
            document.LegacyMigration is null && publicRoute is not null &&
            document.Pages.Keys.Any(route => NormalizeCollaborationPagePath(route) == publicRoute))
        {
            foreach (var (route, page) in document.Pages)
                if (NormalizeCollaborationPagePath(route) != publicRoute && page.DynamicBinding is null)
                    page.Composition = [];
        }
        return Ok(new
        {
            siteKey,
            publication = publication.VersionId.HasValue ? new { versionId = publication.VersionId, revision = publication.Revision } : null,
            business = business is null ? null : new { business.Id, business.DisplayName, business.LegalName, business.BusinessType },
            businessName = business?.DisplayName,
            facts = publicFacts,
            collections = publicCollections.Values,
            store = publicStore,
            ctaCatalog = new { options = publicActions },
            document,
            legacyMigration = document.LegacyMigration
        });
    }

    // The company site's HTML comes from the same public publication envelope as
    // the CMS, rendered by the existing compiler before a visitor receives it.
    [HttpGet("public/legend/page")]
    [HttpHead("public/legend/page")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> LegendPage([FromQuery] string path = "/", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 160 ||
            !System.Text.RegularExpressions.Regex.IsMatch(path, @"^/(?:[a-z0-9_-]+/)*[a-z0-9_-]*$"))
            return NotFound();
        var route = path.TrimEnd('/');
        if (route.Length == 0) route = "/";
        // Resolve exactly one immutable published snapshot. The visitor path must
        // never rebuild the entire public API envelope or launch a Node compiler.
        var publication = WebsiteContentStore.ReadImmutablePublication(() => (
            from state in _db.Set<WebsiteContentState>().AsNoTracking()
            join version in _db.Set<WebsiteContentVersion>().AsNoTracking()
                on state.PublishedVersionId equals version.Id
            where state.SiteKey == WebsiteEditorSiteKeys.Legend &&
                  state.OwnerKey == WebsiteEditorSiteKeys.GlobalOwnerKey &&
                  version.StateId == state.Id &&
                  version.SnapshotRetiredUtc == null
            select new { version.Id, version.StateId, version.CompiledPagesJson }
        ).SingleOrDefault(), cancellationToken);
        if (publication is null) return NotFound();

        try
        {
            var compiledPages = publication.CompiledPagesJson;
            if (string.IsNullOrWhiteSpace(compiledPages))
            {
                string? missingCompiledPages = compiledPages;
                // One-time materialization for historical immutable publications.
                // Derived compiled HTML can fill a missing slot without changing
                // the authored DocumentJson, publication ID, or published pointer.
                var result = await Public(WebsiteEditorSiteKeys.Legend, cancellationToken: cancellationToken);
                if (result is not OkObjectResult published) return StatusCode(503);
                var payload = JsonSerializer.Serialize(published.Value, JsonOptions);
                using var envelope = JsonDocument.Parse(payload);
                if (!envelope.RootElement.TryGetProperty("publication", out var identity) ||
                    identity.ValueKind != JsonValueKind.Object ||
                    !identity.TryGetProperty("versionId", out var value) ||
                    value.GetGuid() != publication.Id)
                    return StatusCode(503);

                var compiler = HttpContext.RequestServices
                    .GetRequiredService<Infrastructure.WebsitePublishing.WebsitePageCompiler>();
                compiledPages = await compiler.CompileLegendAsync(payload, cancellationToken);
                // Match the observed missing slot: empty/whitespace historical
                // values need the same one-time materialization as null.
                await _db.Set<WebsiteContentVersion>()
                    .Where(version => version.Id == publication.Id &&
                                      version.StateId == publication.StateId &&
                                      version.CompiledPagesJson == missingCompiledPages &&
                                      version.SnapshotRetiredUtc == null)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(version => version.CompiledPagesJson, compiledPages),
                        cancellationToken);
            }

            using var compiled = JsonDocument.Parse(compiledPages);
            if (!compiled.RootElement.GetProperty("pages").TryGetProperty(route, out var rendered)) return NotFound();
            var html = (rendered.GetProperty("html").GetString() ?? "")
                .Replace("__LEGEND_CANONICAL_URL__", "https://www.mylegnd.com" + route, StringComparison.Ordinal);
            html = await HttpContext.RequestServices
                .GetRequiredService<Infrastructure.WebsitePublishing.WebsitePageCompiler>()
                .BindRuntimeAssetsAsync(html, cancellationToken);
            Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
            Response.Headers.XContentTypeOptions = "nosniff";
            Response.Headers["X-Legend-Publication-Id"] = publication.Id.ToString("D");
            return Content(HttpMethods.IsHead(Request.Method) ? "" : html, "text/html; charset=utf-8");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or JsonException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            // Never turn a failed publication read/render into a successful old homepage.
            Response.Headers.CacheControl = "no-store";
            Response.Headers.RetryAfter = "30";
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Published website temporarily unavailable.");
        }
    }

    public sealed record ProtectPublishedPage(Guid VersionId, string Title, string Description,
        string? Header, string? Footer, string Main, string Styles, string HtmlStyle, string BodyStyle,
        string Payload, bool NativeSlot);

    // Called by Protect's request-owned Razor layout, never an alternate public route.
    // Native controls/tokens/ModelState stay in Razor; only immutable authored fragments
    // and their exact publication envelope are persisted by the shared compiler.
    [NonAction]
    public async Task<ProtectPublishedPage?> ResolveProtectPageAsync(string path, CancellationToken cancellationToken)
    {
        if (HttpContext.Items["TrackingProfile"] is not AgentTrackingProfile profile ||
            string.IsNullOrWhiteSpace(profile.AgentUserId)) return null;
        var ownerKey = NormalizeOwner(profile.AgentUserId);
        var route = path.TrimEnd('/').ToLowerInvariant();
        if (route.Length == 0 || route == "/home" || route == "/home/index") route = "/";
        if (route == "/privacy") route = "/home/privacy";
        if (route == "/terms") route = "/home/terms";
        if (route.EndsWith("/index", StringComparison.Ordinal)) route = route[..^6];
        if (route == "/trainingpackages") return null;
        var publication = WebsiteContentStore.ReadImmutablePublication(() => (
            from state in _db.Set<WebsiteContentState>().AsNoTracking()
            join version in _db.Set<WebsiteContentVersion>().AsNoTracking() on state.PublishedVersionId equals version.Id
            where state.SiteKey == WebsiteEditorSiteKeys.Protect && state.OwnerKey == ownerKey &&
                  version.StateId == state.Id && version.SnapshotRetiredUtc == null
            select new { version.Id, version.StateId, version.CompiledPagesJson }
        ).SingleOrDefault(), cancellationToken);
        if (publication is null) return null;
        var compiledPages = publication.CompiledPagesJson;
        if (string.IsNullOrWhiteSpace(compiledPages))
        {
            string? missingCompiledPages = compiledPages;
            // Same bounded historical materialization as LEGEND. Verify the
            // envelope is still the exact version selected above before writing
            // derived data. Never store request-generated native markup.
            var slug = HttpContext.Items["IsFounderPath"] as bool? == true ? null : profile.Slug;
            var result = await Public(WebsiteEditorSiteKeys.Protect, slug, cancellationToken: cancellationToken);
            if (result is not OkObjectResult published) return null;
            var payload = JsonSerializer.Serialize(published.Value, JsonOptions);
            using var envelope = JsonDocument.Parse(payload);
            if (!envelope.RootElement.TryGetProperty("publication", out var identity) ||
                identity.ValueKind != JsonValueKind.Object || identity.GetProperty("versionId").GetGuid() != publication.Id)
                return null;
            var compiler = HttpContext.RequestServices.GetRequiredService<Infrastructure.WebsitePublishing.WebsitePageCompiler>();
            compiledPages = await compiler.CompileLegendAsync(payload, cancellationToken);
            // Fill the observed missing slot (null or blank), without replacing a
            // concurrently materialized publication or a retired snapshot.
            await _db.Set<WebsiteContentVersion>()
                .Where(version => version.Id == publication.Id && version.StateId == publication.StateId &&
                                  version.CompiledPagesJson == missingCompiledPages && version.SnapshotRetiredUtc == null)
                .ExecuteUpdateAsync(updates => updates.SetProperty(version => version.CompiledPagesJson, compiledPages), cancellationToken);
        }
        using var compiled = JsonDocument.Parse(compiledPages);
        if (!compiled.RootElement.GetProperty("pages").TryGetProperty(route, out var page)) return null;
        string Value(string name) => page.GetProperty(name).GetString() ?? "";
        string? Optional(string name) => page.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Value(name);
        using var embedded = JsonDocument.Parse(Value("payload"));
        if (embedded.RootElement.GetProperty("publication").GetProperty("versionId").GetGuid() != publication.Id)
            throw new InvalidOperationException("protect_publication_identity_mismatch");
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["X-Legend-Publication-Id"] = publication.Id.ToString("D");
        return new(publication.Id, Value("title"), Value("description"), Optional("header"), Optional("footer"),
            Value("main"), Value("styles"), Value("htmlStyle"), Value("bodyStyle"), Value("payload"), page.GetProperty("nativeSlot").GetBoolean());
    }

    [HttpGet("public/{siteKey}/favicon")]
    [HttpHead("public/{siteKey}/favicon")]
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

        var icon = WebsiteFaviconParity.ResolvePublished(document?.FaviconImageDataUrl);
        if (icon?.Bytes is { } bytes)
            return File(bytes, icon.ContentType!);
        if (icon?.RedirectUrl is { } url)
            return Redirect(url);
        return Redirect(WebsiteFaviconParity.FallbackUrl(_configuration));
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
    public async Task<IActionResult> Manage(
        [FromQuery] string ticket,
        CancellationToken cancellationToken = default,
        [FromQuery] bool audit = false)
    {
        using var timing = new Shared.Diagnostics.RuntimeDiagnosticStageSequence();
        try
        {
            var actor = await AuthorizeAsync(ticket, cancellationToken);
            if (actor is null) return Unauthorized();
            timing.Next(Shared.Diagnostics.RuntimeDiagnosticStage.StateRead);
            var state = await StateAsync(actor, cancellationToken);
            timing.Next(Shared.Diagnostics.RuntimeDiagnosticStage.BusinessFacts);
            var business = actor.CommerceBusinessId.HasValue
                ? await _db.CommerceBusinesses.AsNoTracking()
                    .SingleAsync(b => b.Id == actor.CommerceBusinessId, cancellationToken)
                : null;
            var facts = business is null
                ? null
                : await WebsiteBusinessFacts.LoadAsync(_db, business.Id, cancellationToken);
            timing.Next(Shared.Diagnostics.RuntimeDiagnosticStage.DraftValidation);
            WebsiteContentDocument draft;
            try { draft = Read(state.DraftJson); }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("website_duplicate_node_identity:", StringComparison.Ordinal))
            {
                return Conflict(new { error = "website_draft_duplicate_identity",
                    message = "The saved website needs identity recovery before editing. Your content and publication were preserved." });
            }
            if (WebsiteSystemTemplateAuthority.HasReviewableRuntimeAnchorRecovery(actor.SiteKey, draft))
                return Conflict(new { error = "website_draft_runtime_anchor_recovery",
                    message = "Review the saved protected form anchors before editing. Your saved content and publication were preserved." });
            WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, draft);
            timing.Next(Shared.Diagnostics.RuntimeDiagnosticStage.Collections);
            IReadOnlyDictionary<string, WebsiteCollectionProjection> collectionData =
                business is null || (draft.Collections?.Count ?? 0) == 0
                    ? new Dictionary<string, WebsiteCollectionProjection>(StringComparer.Ordinal)
                    : await new WebsiteCollectionProjectionService(_db)
                        .LoadAsync(draft, business.Id, cancellationToken);

            timing.Next(Shared.Diagnostics.RuntimeDiagnosticStage.CommerceStore);
            var commerceService = HttpContext?.RequestServices?.GetService(typeof(WebsiteCommerceScopeService))
                as WebsiteCommerceScopeService;
            var commerceScope = commerceService is null || (audit && draft.Store?.Enabled != true)
                ? null
                : await commerceService.ResolveAsync(actor, state, createIfMissing: false, cancellationToken);
            var storePayload = await StorePayloadAsync(
                actor.SiteKey,
                draft,
                commerceScope,
                ticket,
                cancellationToken);

            if (audit)
            {
                return Ok(new
                {
                    source = "website_studio_audit_render",
                    business = business is null
                        ? null
                        : new { business.Id, business.DisplayName, business.LegalName, business.BusinessType },
                    siteKey = actor.SiteKey,
                    agentSlug = actor.AgentSlug,
                    commerceBusinessId = actor.CommerceBusinessId,
                    document = draft,
                    legacyMigration = draft.LegacyMigration,
                    revision = state.Revision,
                    facts,
                    collections = collectionData.Values,
                    ctaCatalog = new { options = Array.Empty<object>() },
                    store = storePayload,
                    drafts = Array.Empty<object>()
                });
            }

            timing.Next(Shared.Diagnostics.RuntimeDiagnosticStage.PublicationHistory);
            var publicationUsage = await _db.Set<WebsiteContentVersion>().AsNoTracking()
                .Where(v => v.StateId == state.Id)
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    IdentityCount = group.Count(),
                    RetainedPayloadCount = group.Count(v => v.SnapshotRetiredUtc == null)
                })
                .SingleOrDefaultAsync(cancellationToken);
            var history = await _db.Set<WebsiteContentVersion>().AsNoTracking()
                .Where(v => v.StateId == state.Id && v.SnapshotRetiredUtc == null)
                .OrderByDescending(v => v.Revision).ThenByDescending(v => v.Id)
                .Take(Infrastructure.WebsitePublishing.WebsiteSnapshotRetention.Limit)
                .Select(v => new { versionId = v.Id, v.Revision, v.CreatedUtc })
                .ToListAsync(cancellationToken);
            timing.Next(Shared.Diagnostics.RuntimeDiagnosticStage.CallToAction);
            var ctaOptions = await BuildCallToActionCatalogAsync(actor, facts, cancellationToken, draft);
            timing.Next(Shared.Diagnostics.RuntimeDiagnosticStage.PublishPermission);
            var canPublish = await CanPublishAsync(actor, cancellationToken);
            timing.Next(Shared.Diagnostics.RuntimeDiagnosticStage.Media);
            var mediaUsage = await _db.Set<WebsiteMediaAsset>().AsNoTracking()
                .Where(a => a.OwnerKey == actor.OwnerUserId)
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    MediaBytes = group.Sum(value => value.SizeBytes),
                    MediaCount = group.Count()
                })
                .SingleOrDefaultAsync(cancellationToken);

            timing.Next(Shared.Diagnostics.RuntimeDiagnosticStage.NamedDrafts);
            var drafts = WebsiteNamedDraftReader.ReadMetadata(state);
            timing.Next(Shared.Diagnostics.RuntimeDiagnosticStage.ResponsePreparation);
            return Ok(new
            {
                business = business is null
                    ? null
                    : new { business.Id, business.DisplayName, business.LegalName, business.BusinessType },
                siteKey = actor.SiteKey,
                agentSlug = actor.AgentSlug,
                commerceBusinessId = actor.CommerceBusinessId,
                document = draft,
                legacyMigration = draft.LegacyMigration,
                revision = state.Revision,
                publishedRevision = history.FirstOrDefault(v => v.versionId == state.PublishedVersionId)?.Revision,
                publicationScopeId = state.Id,
                facts,
                receptionistSetupUrl = await ReceptionistSetupUrlAsync(actor, cancellationToken),
                editorBaseUrl = actor.SiteKey == WebsiteEditorSiteKeys.Business
                    ? (_configuration["LegendWebsiteBaseUrl"] ?? "https://www.mylegnd.com").TrimEnd('/')
                    : null,
                dataCatalog = WebsiteCollectionSourcePolicy.Catalog,
                collections = collectionData.Values,
                ctaCatalog = new { options = ctaOptions },
                store = storePayload,
                usage = new
                {
                    mediaBytes = mediaUsage?.MediaBytes ?? 0,
                    mediaCount = mediaUsage?.MediaCount ?? 0,
                    publishedVersions = publicationUsage?.RetainedPayloadCount ?? 0,
                    publicationIdentityCount = publicationUsage?.IdentityCount ?? 0,
                    retiredPublishedVersions = (publicationUsage?.IdentityCount ?? 0) - (publicationUsage?.RetainedPayloadCount ?? 0),
                    publishedVersionOverflow = Math.Max(0, (publicationUsage?.RetainedPayloadCount ?? 0) -
                        Infrastructure.WebsitePublishing.WebsiteSnapshotRetention.Limit),
                    publishedVersionLimit = Infrastructure.WebsitePublishing.WebsiteSnapshotRetention.Limit
                },
                importReport = string.IsNullOrEmpty(state.ImportReportJson)
                    ? (JsonElement?)null
                    : JsonSerializer.Deserialize<JsonElement>(state.ImportReportJson),
                drafts,
                history,
                signalCatalog = (object?)null,
                agentContract = WebsiteStudioAgentContract.CompactPayload,
                capabilities = new
                {
                    canPublish,
                    canManageDomains = canPublish,
                    canImport = actor.SiteKey == WebsiteEditorSiteKeys.Business,
                    canSchedule = canPublish,
                    canDelete = canPublish,
                    canPromote = canPublish,
                    compositionV3 = true,
                    browserAgentWorkspace = true,
                    externalAiApi = false,
                    requiresCompositionMaterialization = draft.LegacyMigration is not null
                },
                schedule = new { publishUtc = state.ScheduledPublishUtc, error = state.ScheduleError },
                readiness = new
                {
                    checks = new[]
                    {
                        new
                        {
                            passed = true,
                            message = "Draft is isolated from published content. Publishing validates and compiles the complete website."
                        }
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw;
        }
    }

    [HttpGet("manage/data-catalog")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> DataCatalog(
        [FromQuery] string ticket,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor?.SiteKey != WebsiteEditorSiteKeys.Business || !actor.CommerceBusinessId.HasValue)
            return Unauthorized();

        var data = await new WebsiteCollectionProjectionService(_db)
            .LoadCatalogAsync(actor.CommerceBusinessId.Value, cancellationToken);
        return Ok(new
        {
            source = "website_business_data_catalog",
            dataCatalog = WebsiteCollectionSourcePolicy.Catalog,
            collections = data.Values
        });
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
        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, document);
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
        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, document);
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
    public sealed record WebsiteMutationRequest(
        string Ticket,
        long ExpectedRevision,
        List<WebsiteMutationOperation>? Operations,
        Guid? DraftId = null,
        string? DraftName = null);
    public sealed record WebsiteDesignPlanRequest(
        string Ticket,
        long ExpectedRevision,
        WebsiteDesignPlan Plan);
    public sealed record ProfileRequest(string Ticket, BusinessWebsiteProfileInput Settings);
    private object SignalCatalogPayload() => new { events = WebsiteSignalBindingPolicy.Options, automaticBehaviors = Shared.Analytics.AnalyticsEventCatalog.Behaviors.Where(behavior => !string.IsNullOrWhiteSpace(behavior.AutomaticTrigger)), matchingFields = WebsiteSignalBindingPolicy.ApprovedMatchingFields, runtimeEnabled = _configuration.GetValue<bool>("WebsiteMarketing:Enabled") };

    [HttpGet("manage/signal-catalog")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> SignalCatalog([FromQuery] string ticket, CancellationToken cancellationToken)
    {
        if (await AuthorizeAsync(ticket, cancellationToken) is null) return Unauthorized();
        return Ok(SignalCatalogPayload());
    }

    [HttpGet("manage/agent/contract")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> CreativeAgentContract(
        [FromQuery] string ticket,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var state = await StateAsync(actor, cancellationToken);
        var document = Read(state.DraftJson);
        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, document);
        WebsiteBusinessFacts? facts = actor.SiteKey == WebsiteEditorSiteKeys.Business && actor.CommerceBusinessId.HasValue
            ? await WebsiteBusinessFacts.LoadAsync(_db, actor.CommerceBusinessId.Value, cancellationToken)
            : null;
        var actions = await BuildCallToActionCatalogAsync(actor, facts, cancellationToken, document);
        return Ok(WebsiteStudioAgentContract.ForScope(actions, SignalCatalogPayload()));
    }


    [HttpGet("manage/agent/summary")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> CreativeWorkspaceSummary(
        [FromQuery] string ticket,
        CancellationToken cancellationToken = default)
    {
        var performance = Stopwatch.StartNew();
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var state = await StateAsync(actor, cancellationToken);
        var document = Read(state.DraftJson);
        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, document);
        if (document.LegacyMigration is not null)
            return Conflict(new { error = "website_materialization_required" });

        CommerceBusiness? business = actor.CommerceBusinessId.HasValue
            ? await _db.CommerceBusinesses.AsNoTracking().SingleOrDefaultAsync(value => value.Id == actor.CommerceBusinessId.Value, cancellationToken)
            : null;
        var facts = business is null ? null : await WebsiteBusinessFacts.LoadAsync(_db, business.Id, cancellationToken);
        var actions = await BuildCallToActionCatalogAsync(actor, facts, cancellationToken, document);
        var capabilities = WebsiteCreativeCapabilityResolver.Resolve(actor.SiteKey, document, actions);
        var quality = WebsiteDraftQualityInspector.Inspect(document);
        var designQuality = WebsiteDesignQualityInspector.Inspect(document, capabilities);
        var identity = new
        {
            siteKey = actor.SiteKey,
            businessId = (Guid?)business?.Id,
            displayName = business?.DisplayName,
            legalName = business?.LegalName,
            businessType = business?.BusinessType,
            agentSlug = actor.AgentSlug
        };
        var mediaRows = await _db.Set<WebsiteMediaAsset>().AsNoTracking()
            .Where(value => value.OwnerKey == actor.OwnerUserId)
            .OrderByDescending(value => value.CreatedUtc)
            .Take(12)
            .ToListAsync(cancellationToken);
        var media = mediaRows.Select(value => (object)new
        {
            value.Id,
            name = MediaDisplayName(value),
            value.ContentType,
            value.SizeBytes,
            value.CreatedUtc
        }).ToArray();

        performance.Stop();
        HttpContext?.RequestServices.GetService<ILogger<WebsitePlatformController>>()?.LogInformation(
            "WebsiteStudio summary completed SiteKey={SiteKey} Pages={Pages} MediaRows={MediaRows} DurationMs={DurationMs}",
            actor.SiteKey,
            document.Pages.Count,
            media.Length,
            performance.ElapsedMilliseconds);
        return Ok(WebsiteCreativeProjection.SiteSummary(
            actor.SiteKey,
            state.Revision,
            document,
            identity,
            facts is null ? null : new
            {
                facts.ContactEmail,
                facts.Phone,
                hours = PreviewAgentFact(facts.Hours, 800),
                locations = PreviewAgentFact(facts.Locations, 1200),
                services = PreviewAgentFact(facts.Services, 2500),
                fullBusinessDataAvailable = true
            },
            capabilities,
            quality,
            designQuality,
            media));
    }

    private static string PreviewAgentFact(string? value, int maximum)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maximum ? text : text[..maximum] + "…";
    }

    [HttpGet("manage/agent/page-outline")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> CreativePageOutline(
        [FromQuery] string ticket,
        [FromQuery] string page,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var state = await StateAsync(actor, cancellationToken);
        var document = Read(state.DraftJson);
        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, document);
        if (!document.Pages.TryGetValue(page, out var value)) return NotFound(new { error = "website_page_not_found" });

        return Ok(new
        {
            schema = "legend-page-outline/v1",
            revision = state.Revision,
            path = page,
            fingerprint = WebsiteCreativeFingerprint.Page(value),
            page = WebsiteCreativeProjection.PageOutline(value)
        });
    }

    [HttpGet("manage/agent/node")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> CreativeNode(
        [FromQuery] string ticket,
        [FromQuery] string id,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var state = await StateAsync(actor, cancellationToken);
        var document = Read(state.DraftJson);
        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, document);
        if (!WebsiteDocumentIndex.TryFind(document, id, out var node, out var location))
            return NotFound(new { error = "website_node_not_found" });

        return Ok(new
        {
            schema = "legend-node-source/v1",
            revision = state.Revision,
            fingerprint = WebsiteCreativeFingerprint.Node(node),
            location,
            node = WebsiteCreativeProjection.Node(node)
        });
    }

    [HttpGet("manage/agent/recipes")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> CreativeRecipes(
        [FromQuery] string ticket,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        return Ok(new
        {
            schema = "legend-website-recipes/v1",
            siteKey = actor.SiteKey,
            recipes = WebsiteRecipeCatalog.Definitions,
            pageRecipes = WebsitePageRecipeCatalog.Definitions,
            designPlan = WebsiteDesignPlanContract.Payload,
            artDirections = new[] { "roadster-precision", "editorial-luxe", "modern-minimal", "warm-craft", "clinical-precision", "high-energy-performance" }
        });
    }


    [HttpGet("manage/agent/design-quality")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> CreativeDesignQuality(
        [FromQuery] string ticket,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var state = await StateAsync(actor, cancellationToken);
        var document = Read(state.DraftJson);
        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, document);
        WebsiteBusinessFacts? facts = actor.SiteKey == WebsiteEditorSiteKeys.Business && actor.CommerceBusinessId.HasValue
            ? await WebsiteBusinessFacts.LoadAsync(_db, actor.CommerceBusinessId.Value, cancellationToken)
            : null;
        var actions = await BuildCallToActionCatalogAsync(actor, facts, cancellationToken, document);
        var capabilities = WebsiteCreativeCapabilityResolver.Resolve(actor.SiteKey, document, actions);
        var structural = WebsiteDraftQualityInspector.Inspect(document);
        var design = WebsiteDesignQualityInspector.Inspect(document, capabilities);
        var media = await BuildMediaPerformanceQualityAsync(actor, document, cancellationToken);
        return Ok(new
        {
            schema = "legend-design-quality/v1",
            revision = state.Revision,
            structural,
            design,
            media
        });
    }

    [HttpGet("manage/agent/design-quality/repairs")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> CreativeDesignQualityRepairs(
        [FromQuery] string ticket,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var state = await StateAsync(actor, cancellationToken);
        var document = Read(state.DraftJson);
        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, document);
        if (document.LegacyMigration is not null)
            return Conflict(new { error = "website_materialization_required" });

        var plan = WebsiteDesignQualityRepairPlanner.Plan(document);
        return Ok(new
        {
            schema = plan.Schema,
            revision = state.Revision,
            repairs = plan.Repairs,
            operations = plan.Operations
        });
    }

    [HttpPost("manage/mutations")]
    [RequestSizeLimit(2_500_000)]
    public async Task<IActionResult> ApplyMutations(
        [FromBody] WebsiteMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        using var diagnosticStage = Shared.Diagnostics.RuntimeDiagnosticScope.Current?.Stage(Shared.Diagnostics.RuntimeDiagnosticStage.StudioEdit);
        return await ApplyMutationBatchAsync(request, cancellationToken);
    }

    [HttpPost("manage/design-plan")]
    [RequestSizeLimit(2_500_000)]
    public async Task<IActionResult> ApplyDesignPlan(
        [FromBody] WebsiteDesignPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request?.Plan is null) return BadRequest(new { error = "website_design_plan_required" });
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var state = await StateAsync(actor, cancellationToken);
        if (state.Revision != request.ExpectedRevision)
            return Conflict(new { error = "revision_conflict", revision = state.Revision });

        var baseline = Read(state.DraftJson);
        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, baseline);
        WebsiteBusinessFacts? facts = actor.SiteKey == WebsiteEditorSiteKeys.Business && actor.CommerceBusinessId.HasValue
            ? await WebsiteBusinessFacts.LoadAsync(_db, actor.CommerceBusinessId.Value, cancellationToken)
            : null;
        var actions = await BuildCallToActionCatalogAsync(actor, facts, cancellationToken, baseline);
        var capabilities = WebsiteCreativeCapabilityResolver.Resolve(actor.SiteKey, baseline, actions);
        IReadOnlyList<WebsiteMutationOperation> operations;
        var planPerformance = Stopwatch.StartNew();
        try
        {
            operations = WebsiteDesignPlanResolver.Resolve(baseline, actor.SiteKey, capabilities, request.Plan);
        }
        catch (WebsiteSiteSourceProtectionException ex)
        {
            return BadRequest(new { error = "website_design_plan_protected", message = ex.Message, canonicalProtectionViolation = true });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = "website_design_plan_invalid", message = ex.Message });
        }

        planPerformance.Stop();
        HttpContext?.RequestServices.GetService<ILogger<WebsitePlatformController>>()?.LogInformation(
            "WebsiteStudio design plan resolved SiteKey={SiteKey} Pages={Pages} Operations={Operations} DurationMs={DurationMs}",
            actor.SiteKey,
            request.Plan.Pages?.Count ?? 0,
            operations.Count,
            planPerformance.ElapsedMilliseconds);
        return await ApplyMutationBatchAsync(
            new WebsiteMutationRequest(request.Ticket, request.ExpectedRevision, operations.ToList()),
            cancellationToken,
            actor,
            state,
            baseline,
            actions,
            capabilities);
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

    [HttpGet("manage/agent/conversion-readiness")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> CreativeConversionReadiness(
        [FromQuery] string ticket,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();

        var state = await StateAsync(actor, cancellationToken);
        var document = Read(state.DraftJson);
        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, document);
        if (document.LegacyMigration is not null)
            return Conflict(new { error = "website_materialization_required" });

        WebsiteBusinessFacts? facts = actor.SiteKey == WebsiteEditorSiteKeys.Business && actor.CommerceBusinessId.HasValue
            ? await WebsiteBusinessFacts.LoadAsync(_db, actor.CommerceBusinessId.Value, cancellationToken)
            : null;
        var actions = await BuildCallToActionCatalogAsync(actor, facts, cancellationToken, document);
        var capabilities = WebsiteCreativeCapabilityResolver.Resolve(actor.SiteKey, document, actions);
        var design = WebsiteDesignQualityInspector.Inspect(document, capabilities);

        var publishedRevision = state.PublishedVersionId.HasValue
            ? await _db.Set<WebsiteContentVersion>().AsNoTracking()
                .Where(value => value.Id == state.PublishedVersionId && value.StateId == state.Id)
                .Select(value => (long?)value.Revision)
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        var currentMetaDestination = await ResolveSignalDestinationAsync(actor, cancellationToken);
        var advertisingOwner = await Infrastructure.Analytics.CanonicalAdvertisingEventProjection.ResolveOwnerAsync(
            _db, _configuration, actor, cancellationToken);
        var browserDestination = await HttpContext.RequestServices
            .GetRequiredService<Infrastructure.Analytics.MarketingBrowserConfigurationService>()
            .GetAsync(advertisingOwner, cancellationToken);

        Infrastructure.Analytics.MarketingMeasurementEvidenceSnapshot? measurementEvidence = null;
        string? measurementEvidenceError = null;
        if (advertisingOwner is not null)
        {
            try
            {
                measurementEvidence = await HttpContext.RequestServices
                    .GetRequiredService<Infrastructure.Analytics.MarketingMeasurementEvidenceService>()
                    .GetAsync(advertisingOwner, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                // Readiness must remain useful when one optional provider read is unavailable.
                measurementEvidenceError = "measurement_evidence_unavailable";
            }
        }

        var eventMap = await new WebsiteEventMapQuery(_db, _configuration)
            .ReadTicketAsync(actor, cancellationToken);
        var relevant = eventMap
            .Where(entry =>
                string.Equals(entry.Authority, "verified_server", StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(entry.ActionKey) ||
                !string.IsNullOrWhiteSpace(entry.Binding))
            .ToArray();

        static bool ProviderAccepted(string status) => status == "provider_accepted";
        static bool HttpAccepted(string status) => status == "http_accepted";
        static bool InFlight(string status) =>
            status is "pending" or "retryable" or "projected";
        static bool Problem(string status) =>
            status is "blocked" or "failed" or "permanent_failure" or "requires_reconciliation"
                or "partial_delivery" or "receipt_unverified";
        static bool RowProblem(WebsiteEventMapQuery.Entry entry) =>
            Problem(entry.AnalyticsStatus) || Problem(entry.MetaStatus) || Problem(entry.OpenAiStatus);

        var returned = relevant
            .OrderByDescending(RowProblem)
            .ThenByDescending(entry => entry.PublishedRevision)
            .ThenBy(entry => entry.Page, StringComparer.Ordinal)
            .ThenBy(entry => entry.Element, StringComparer.Ordinal)
            .Take(80)
            .ToArray();

        var metaProviderAccepted = relevant.Count(entry => ProviderAccepted(entry.MetaStatus));
        var metaHttpAccepted = relevant.Count(entry => HttpAccepted(entry.MetaStatus));
        var openAiProviderAccepted = relevant.Count(entry => ProviderAccepted(entry.OpenAiStatus));
        var openAiHttpAccepted = relevant.Count(entry => HttpAccepted(entry.OpenAiStatus));

        return Ok(new
        {
            schema = "legend-conversion-readiness/v2",
            revision = state.Revision,
            publishedRevision,
            publishedVersionId = state.PublishedVersionId,
            currentDraftIsPublished = publishedRevision.HasValue && publishedRevision.Value == state.Revision,
            draft = new
            {
                conversionPaths = design.ConversionPaths,
                checks = design.Checks.Where(check =>
                    check.Code.StartsWith("conversion_", StringComparison.Ordinal)).ToArray()
            },
            destinations = new
            {
                meta = SignalDestinationPayload(currentMetaDestination),
                openai = new
                {
                    browserPixelConfigured = !string.IsNullOrWhiteSpace(browserDestination.OpenAiPixelId),
                    accountApproved = browserDestination.OpenAiAccountApproved,
                    serverConversionsConfigured = browserDestination.OpenAiConversionsApiConfigured
                }
            },
            measurementEvidence,
            measurementEvidenceScope = "advertising_owner_30d_all_published_sources",
            measurementEvidenceError,
            published = new
            {
                windowDays = 30,
                evidenceRows = relevant.Length,
                returnedRows = returned.Length,
                resultLimit = 80,
                analyticsObserved = relevant.Count(entry => entry.AnalyticsStatus == "observed"),
                metaProviderAccepted,
                metaHttpAccepted,
                openAiProviderAccepted,
                openAiHttpAccepted,
                acceptanceSemantics = new
                {
                    meta = "provider_events_received",
                    openai = "http_2xx_transport_only"
                },
                acceptance = new
                {
                    meta = new
                    {
                        providerAccepted = metaProviderAccepted,
                        httpAccepted = metaHttpAccepted
                    },
                    openai = new
                    {
                        providerAccepted = openAiProviderAccepted,
                        httpAccepted = openAiHttpAccepted
                    }
                },
                inFlightRows = relevant.Count(entry =>
                    InFlight(entry.MetaStatus) || InFlight(entry.OpenAiStatus)),
                problemRows = relevant.Count(RowProblem),
                entries = returned.Select(entry => new
                {
                    entry.Site,
                    entry.Page,
                    entry.Element,
                    entry.VisibleLabel,
                    entry.ActionKey,
                    entry.BehaviorKey,
                    entry.CanonicalEvent,
                    entry.Authority,
                    entry.Mode,
                    entry.Locked,
                    entry.MetaMapping,
                    entry.OpenAiMapping,
                    entry.AnalyticsStatus,
                    entry.MetaStatus,
                    entry.OpenAiStatus,
                    entry.Trigger,
                    entry.PublishedRevision
                }).ToArray()
            }
        });
    }

    [HttpGet("manage/agent/conversion-trace")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> CreativeConversionTrace(
        [FromQuery] string ticket,
        [FromQuery] int take = 20,
        [FromQuery] long? analyticsEventId = null,
        [FromQuery] string? eventName = null,
        [FromQuery] string? page = null,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();

        var state = await StateAsync(actor, cancellationToken);
        var owner = await Infrastructure.Analytics.CanonicalAdvertisingEventProjection.ResolveOwnerAsync(
            _db, _configuration, actor, cancellationToken);
        if (owner is null)
            return Ok(new
            {
                schema = "legend-conversion-trace/v1",
                source = "canonical_analytics_event_lineage",
                revision = state.Revision,
                publishedVersionId = state.PublishedVersionId,
                windowDays = 30,
                matchedEvents = 0,
                returnedEvents = 0,
                events = Array.Empty<object>()
            });

        take = Math.Clamp(take, 1, 50);
        var fromUtc = DateTime.UtcNow.AddDays(-30);
        var ownerEvents = await new Infrastructure.Analytics.AnalyticsQueryService(_db, _configuration)
            .LoadOwnerEventsAsync(fromUtc, owner, cancellationToken);
        var publishedEvents = ownerEvents
            .Where(row => state.PublishedVersionId.HasValue &&
                          row.WebsiteContentVersionId == state.PublishedVersionId)
            .ToArray();

        if (analyticsEventId.HasValue)
            publishedEvents = publishedEvents.Where(row => row.Id == analyticsEventId.Value).ToArray();

        if (!string.IsNullOrWhiteSpace(eventName))
        {
            var normalizedEvent = eventName.Trim();
            publishedEvents = publishedEvents.Where(row =>
                    string.Equals(row.EventType, normalizedEvent, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        Infrastructure.Analytics.CanonicalAdvertisingEventProjection.ResolveEventName(row),
                        normalizedEvent,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        if (!string.IsNullOrWhiteSpace(page))
        {
            var normalizedPage = page.Trim();
            publishedEvents = publishedEvents.Where(row =>
                    string.Equals(row.Path, normalizedPage, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(row.PageKey, normalizedPage, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        var matchedEvents = publishedEvents.Length;
        var selected = publishedEvents
            .OrderByDescending(row => row.EventUtc)
            .ThenByDescending(row => row.Id)
            .Take(take)
            .ToArray();
        var selectedIds = selected.Select(row => row.Id).ToArray();

        var metaRows = selectedIds.Length == 0
            ? new List<MetaSignalEvent>()
            : await _db.MetaSignalEvents.AsNoTracking()
                .Where(row =>
                    row.WebsiteContentVersionId == state.PublishedVersionId &&
                    row.CreatedUtc >= fromUtc &&
                    row.AgentTrackingProfileId == owner.AgentTrackingProfileId &&
                    row.CommerceBusinessId == owner.CommerceBusinessId)
                .OrderByDescending(row => row.Id)
                .ToListAsync(cancellationToken);
        metaRows = metaRows.Where(row =>
                Infrastructure.Analytics.CanonicalAdvertisingEventProjection.ReadInt64(
                    row.MetadataJson, "sourceAnalyticsEventId") is { } sourceId &&
                selectedIds.Contains(sourceId))
            .ToList();

        var openAiRows = selectedIds.Length == 0
            ? new List<MarketingDestinationDelivery>()
            : await _db.MarketingDestinationDeliveries.AsNoTracking()
                .Where(row =>
                    row.OwnerKey == owner.Key &&
                    row.AgentTrackingProfileId == owner.AgentTrackingProfileId &&
                    row.CommerceBusinessId == owner.CommerceBusinessId &&
                    row.Channel == "server" && row.CanonicalSource == nameof(AnalyticsEvent) &&
                    row.Provider == Shared.Analytics.MarketingDestinationKeys.OpenAi &&
                    row.AnalyticsEventId.HasValue &&
                    selectedIds.Contains(row.AnalyticsEventId.Value) &&
                    row.CreatedUtc >= fromUtc)
                .OrderByDescending(row => row.UpdatedUtc)
                .ToListAsync(cancellationToken);

        var identities = new Dictionary<long, Infrastructure.Analytics.CanonicalMarketingIdentity>();
        foreach (var row in selected)
            identities[row.Id] = await Infrastructure.Analytics.CanonicalMarketingIdentityResolver.ResolveAsync(
                _db, row, ct: cancellationToken);

        static string? LineageKey(Infrastructure.Analytics.CanonicalMarketingIdentity identity)
        {
            var raw = identity.WebsiteLeadId?.ToString("N") ??
                      identity.WorkstationLeadId ??
                      identity.ClientUserId;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }

        object Trace(AnalyticsEvent row)
        {
            var identity = identities[row.Id];
            var linkedLead = identity.WebsiteLead;
            var meta = metaRows.Where(value =>
                    Infrastructure.Analytics.CanonicalAdvertisingEventProjection.ReadInt64(
                        value.MetadataJson, "sourceAnalyticsEventId") == row.Id &&
                    value.AgentTrackingProfileId == row.AgentTrackingProfileId &&
                    value.CommerceBusinessId == row.CommerceBusinessId)
                .ToArray();
            var openAi = openAiRows.Where(value => value.AnalyticsEventId == row.Id).ToArray();

            bool MetaAccepted(MetaSignalEvent value) =>
                Infrastructure.Analytics.MarketingDeliveryEvidencePolicy.MetaProviderAccepted(value);
            bool MetaAttempted(MetaSignalEvent value) =>
                Infrastructure.Analytics.MarketingDeliveryEvidencePolicy.MetaAttempted(value);
            bool MetaRetryable(MetaSignalEvent value) =>
                Infrastructure.Analytics.MarketingDeliveryEvidencePolicy.MetaRetryable(value);

            var latestMetaStatus = meta
                .Select(value => Infrastructure.Analytics.CanonicalAdvertisingEventProjection.ReadString(
                    value.MetadataJson, "metaServerStatus"))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            return new
            {
                analytics = new
                {
                    id = row.Id,
                    row.EventId,
                    row.ClientEventId,
                    row.EventType,
                    canonicalEvent = Infrastructure.Analytics.CanonicalAdvertisingEventProjection.ResolveEventName(row),
                    authority = Infrastructure.Analytics.CanonicalAdvertisingEventProjection.CanProjectServer(row)
                        ? "verified_server"
                        : "browser_observation",
                    row.EventUtc,
                    row.ReceivedUtc,
                    row.Path,
                    row.PageKey,
                    row.ElementKey,
                    row.ElementId,
                    row.FormKey,
                    row.WebsiteBindingId,
                    actionKey = Infrastructure.Analytics.CanonicalAdvertisingEventProjection.ReadString(
                        row.MetadataJson, "actionKey"),
                    row.SubmitOutcome
                },
                attribution = new
                {
                    channel = Infrastructure.Analytics.CanonicalMarketingOutcomeProjection.ChannelFor(row),
                    row.UtmSource,
                    row.UtmMedium,
                    row.UtmCampaign,
                    row.UtmId,
                    row.MetaCampaignId,
                    row.MetaAdSetId,
                    row.MetaAdId,
                    fbclidPresent = !string.IsNullOrWhiteSpace(row.Fbclid),
                    opprefPresent = !string.IsNullOrWhiteSpace(row.Oppref),
                    obrefPresent = !string.IsNullOrWhiteSpace(identity.Obref)
                },
                crm = new
                {
                    lineageKey = LineageKey(identity),
                    identityAuthority = "CanonicalMarketingIdentityResolver",
                    websiteLeadLinked = linkedLead is not null,
                    websiteLeadStatus = linkedLead?.Status,
                    websiteLeadCreatedUtc = linkedLead?.CreatedUtc,
                    workstationLeadLinked = !string.IsNullOrWhiteSpace(identity.WorkstationLeadId),
                    clientLinked = !string.IsNullOrWhiteSpace(identity.ClientUserId),
                    productionOutcomeLinked = !string.IsNullOrWhiteSpace(
                        Infrastructure.Analytics.CanonicalAdvertisingEventProjection.ReadString(
                            row.MetadataJson, "productionRecordId"))
                },
                meta = new
                {
                    projected = meta.Length,
                    browserSent = meta.Count(value => value.MetaBrowserSent),
                    serverSent = meta.Count(value => value.MetaServerSent),
                    attempted = meta.Count(MetaAttempted),
                    providerAccepted = meta.Count(MetaAccepted),
                    retryable = meta.Count(MetaRetryable),
                    latestStatus = latestMetaStatus,
                    acceptanceSemantics = "provider_events_received"
                },
                openai = new
                {
                    receipts = openAi.Select(value => new
                    {
                        deliveryId = value.Id,
                        value.CanonicalSource,
                        value.CanonicalEventName,
                        value.ProviderEventName,
                        value.Status,
                        value.AttemptCount,
                        value.LastHttpStatusCode,
                        httpAccepted = Infrastructure.Analytics.MarketingDeliveryEvidencePolicy
                            .HttpTransportAccepted(value),
                        value.SentUtc,
                        value.UpdatedUtc,
                        destination = new
                        {
                            value.AdvertiserAccountId,
                            value.ConversionDataSourceId,
                            value.PixelId
                        }
                    }).ToArray(),
                    acceptanceSemantics = "http_2xx_transport_only"
                }
            };
        }

        return Ok(new
        {
            schema = "legend-conversion-trace/v1",
            source = "canonical_analytics_event_lineage",
            revision = state.Revision,
            publishedVersionId = state.PublishedVersionId,
            currentDraftIsPublished = state.PublishedVersionId.HasValue &&
                await _db.Set<WebsiteContentVersion>().AsNoTracking()
                    .AnyAsync(value =>
                        value.Id == state.PublishedVersionId &&
                        value.StateId == state.Id &&
                        value.Revision == state.Revision,
                        cancellationToken),
            windowDays = 30,
            matchedEvents,
            returnedEvents = selected.Length,
            resultLimit = take,
            privacy = new
            {
                piiIncluded = false,
                rawCustomerIdentifiersIncluded = false,
                rawClickReferencesIncluded = false,
                providerCredentialsIncluded = false
            },
            events = selected.Select(Trace).ToArray()
        });
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
            if (target.Type == "experience" && target.Experience is not null && fieldKey is not null)
            {
                var control = target.Experience.Controls.Single(control =>
                    string.Equals(control.Key, fieldKey, StringComparison.OrdinalIgnoreCase));
                WebsiteSignalBindingPolicy.ValidateExperienceControl(control, signals);
            }
            else if (fieldKey is not null &&
                     (target.Type == "form" ||
                      WebsiteSystemTemplateAuthority.IsRuntimeFormSystemKey(target.SystemKey)))
            {
                WebsiteSignalBindingPolicy.ValidateProtectedFormField(target, fieldKey, signals);
            }
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

        if (!TryFindSignalTarget(
                document,
                request.PagePath,
                request.ElementId,
                fieldKey,
                out var savedTarget,
                out _))
            throw new InvalidOperationException("website_signal_target_lost_after_save");

        return Ok(new
        {
            source = "website_signal_configuration",
            revision = state.Revision,
            pagePath = request.PagePath,
            elementId = request.ElementId,
            fieldKey,
            signals,
            nodeSignals = savedTarget.Signals,
            fieldSignals = savedTarget.FieldSignals
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
    public sealed record PublishRequest(string Ticket, long ExpectedRevision, Guid? RequestId = null);
    public sealed record DeleteWebsiteRequest(string Ticket, long ExpectedRevision);
    public sealed record RollbackRequest(string Ticket, long ExpectedRevision, Guid VersionId);

    [HttpPost("manage")]
    [RequestSizeLimit(4_500_000)]
    public async Task<IActionResult> Save([FromBody] SaveRequest request, CancellationToken cancellationToken = default)
    {
        using var diagnosticStage = Shared.Diagnostics.RuntimeDiagnosticScope.Current?.Stage(Shared.Diagnostics.RuntimeDiagnosticStage.StudioSave);
        if (request?.Document is null) return BadRequest();
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var state = await StateAsync(actor, cancellationToken);
        if (request.ExpectedRevision != state.Revision)
            return Conflict(new { error = "revision_conflict", revision = state.Revision });

        var baseline = Read(state.DraftJson);
        if (baseline.LegacyMigration is null)
            return BadRequest(new
            {
                error = "website_full_document_write_retired",
                message = "Canonical v3 websites are written only through typed Website Studio mutations."
            });
        if (request.DraftId.HasValue || request.DraftName is not null)
            return BadRequest(new
            {
                error = "website_materialization_draft_not_supported",
                message = "Complete canonical materialization before saving named drafts."
            });

        WebsiteContentDocument document;
        try
        {
            document = WebsiteContentSanitizer.Sanitize(request.Document);
            if (document.LegacyMigration is not null ||
                document.Version != WebsiteStudioContract.CurrentDocumentVersion)
                return BadRequest(new
                {
                    error = "website_materialization_invalid",
                    message = "Legacy materialization must produce one canonical v3 website document."
                });

            WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, document);
            WebsiteContentSanitizer.EnsureUniqueCanonicalNodeIds(document);
            WebsiteSystemTemplateAuthority.RestoreRuntimeForms(actor.SiteKey, document, document);
            await ValidateCompositionMediaOwnershipAsync(actor, document, cancellationToken);
        }
        catch (WebsiteSiteSourceProtectionException ex)
        {
            return BadRequest(new
            {
                error = "website_materialization_protected",
                message = ex.Message,
                canonicalProtectionViolation = true,
                correction = WebsiteStudioAgentContract.ProtectedEditCorrection
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("website_duplicate_node_identity:", StringComparison.Ordinal))
        {
            return BadRequest(new
            {
                error = "website_materialization_duplicate_identity",
                message = "The prepared website contains duplicate content identities. The existing draft was preserved."
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new
            {
                error = "website_materialization_invalid",
                message = ex.Message,
                canonicalProtectionViolation = false
            });
        }

        document.UpdatedUtc = DateTime.UtcNow;
        state.ScheduledPublishUtc = null;
        state.ScheduledActorJson = null;
        state.ScheduledRevision = null;
        state.ScheduleError = null;
        state.DraftJson = JsonSerializer.Serialize(document, JsonOptions);
        state.Revision++;
        state.UpdatedUtc = DateTime.UtcNow;

        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "revision_conflict" }); }

        return Ok(new
        {
            source = "canonical_v3_materialization",
            document,
            revision = state.Revision,
            savedUtc = state.UpdatedUtc,
            drafts = WebsiteNamedDraftReader.ReadMetadata(state)
        });
    }

    private async Task<IActionResult> ApplyMutationBatchAsync(
        WebsiteMutationRequest request,
        CancellationToken cancellationToken,
        WebsiteEditorTicket? authorizedActor = null,
        WebsiteContentState? authorizedState = null,
        WebsiteContentDocument? preparedBaseline = null,
        IReadOnlyList<WebsiteCallToActionOption>? preparedActions = null,
        WebsiteCapabilityManifest? preparedCapabilities = null)
    {
        var performance = Stopwatch.StartNew();
        if (request is null || request.Operations is null)
            return BadRequest(new { error = "website_mutations_required" });
        if (request.Operations.Count == 0 && !request.DraftId.HasValue && request.DraftName is null)
            return BadRequest(new { error = "website_mutations_required" });
        if (request.Operations.Count > 400)
            return BadRequest(new { error = "website_mutation_limit" });

        var actor = authorizedActor ?? await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        var state = authorizedState ?? await StateAsync(actor, cancellationToken);

        var canScopedRebase = request.Operations.Count > 0 && request.Operations.All(operation =>
            (operation.Type is "replaceNode" or "removeNode" or "moveNode" or "updatePage" or "setApprovedCapability") &&
            !string.IsNullOrWhiteSpace(operation.ExpectedFingerprint));
        if (state.Revision != request.ExpectedRevision && !canScopedRebase)
            return Conflict(new { error = "revision_conflict", revision = state.Revision });

        var baseline = preparedBaseline ?? Read(state.DraftJson);
        if (preparedBaseline is null)
            WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, baseline);
        if (baseline.LegacyMigration is not null)
            return Conflict(new { error = "website_materialization_required" });

        IReadOnlyList<WebsiteCallToActionOption> actions;
        WebsiteCapabilityManifest capabilities;
        if (preparedActions is not null && preparedCapabilities is not null)
        {
            actions = preparedActions;
            capabilities = preparedCapabilities;
        }
        else
        {
            WebsiteBusinessFacts? facts = actor.SiteKey == WebsiteEditorSiteKeys.Business && actor.CommerceBusinessId.HasValue
                ? await WebsiteBusinessFacts.LoadAsync(_db, actor.CommerceBusinessId.Value, cancellationToken)
                : null;
            actions = await BuildCallToActionCatalogAsync(actor, facts, cancellationToken, baseline);
            capabilities = WebsiteCreativeCapabilityResolver.Resolve(actor.SiteKey, baseline, actions);
        }

        WebsiteMutationApplyResult result;
        try
        {
            result = WebsiteDocumentMutationService.Apply(
                baseline,
                actor.SiteKey,
                actions,
                capabilities,
                request.Operations);
            await ValidateNewCompositionMediaOwnershipAsync(actor, baseline, result.Document, cancellationToken);
        }
        catch (WebsiteMutationConflictException ex)
        {
            return Conflict(new
            {
                error = "scope_revision_conflict",
                revision = state.Revision,
                kind = ex.Kind,
                targetId = ex.TargetId,
                fingerprint = ex.ActualFingerprint
            });
        }
        catch (WebsiteSiteSourceProtectionException ex)
        {
            return BadRequest(new
            {
                error = "website_mutation_protected",
                message = ex.Message,
                canonicalProtectionViolation = true,
                correction = WebsiteStudioAgentContract.ProtectedEditCorrection
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = "website_mutation_invalid", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        var document = result.Document;
        document.UpdatedUtc = DateTime.UtcNow;
        if (request.DraftId.HasValue || request.DraftName is not null)
        {
            var named = request.DraftName?.Trim();
            if (string.IsNullOrWhiteSpace(named) || named.Length > 100)
                return BadRequest(new { message = "Enter a draft name of 1–100 characters." });
            var drafts = ReadDrafts(state);
            if (!NamedDraftEntriesAreComplete(state, drafts))
                return Conflict(new { error = "website_draft_list_requires_review",
                    message = "The saved draft list contains entries that need review. All existing drafts were preserved." });
            var draft = request.DraftId.HasValue ? drafts.SingleOrDefault(value => value.Id == request.DraftId) : null;
            if (request.DraftId.HasValue && draft is null) return NotFound();
            if (draft is null && drafts.Count >= 20)
                return BadRequest(new { message = "You can keep up to 20 drafts. Delete a draft before creating another." });
            if (drafts.Any(value => value.Id != draft?.Id && string.Equals(value.Name, named, StringComparison.OrdinalIgnoreCase)))
                return Conflict(new { message = "That name already exists. Select the existing draft to update it, or choose another name." });
            if (draft is null) { draft = new WebsiteNamedDraft(); drafts.Add(draft); }
            draft.Name = named;
            draft.DocumentJson = JsonSerializer.Serialize(document, JsonOptions);
            draft.UpdatedUtc = DateTime.UtcNow;
            state.NamedDraftsJson = JsonSerializer.Serialize(drafts, JsonOptions);
        }

        state.DraftJson = JsonSerializer.Serialize(document, JsonOptions);
        state.ScheduledPublishUtc = null;
        state.ScheduledActorJson = null;
        state.ScheduledRevision = null;
        state.ScheduleError = null;
        state.Revision++;
        state.UpdatedUtc = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "revision_conflict" }); }

        var changedPagePaths = result.ChangedScopes
            .Where(value => value.StartsWith("/", StringComparison.Ordinal))
            .Select(value =>
            {
                var marker = value.IndexOf('#');
                return marker < 0 ? value : value[..marker];
            })
            .Distinct(StringComparer.Ordinal)
            .Where(document.Pages.ContainsKey)
            .ToArray();
        var allChangedComponents = result.ChangedScopes
            .Where(value => value.StartsWith("@component/", StringComparison.Ordinal))
            .Select(value =>
            {
                var remainder = value["@component/".Length..];
                var marker = remainder.IndexOf('#');
                return marker < 0 ? remainder : remainder[..marker];
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var changedComponents = allChangedComponents.Where(document.ReusableComponents.ContainsKey).ToArray();
        var removedPages = result.ChangedScopes
            .Where(value => value.StartsWith("/", StringComparison.Ordinal))
            .Select(value =>
            {
                var marker = value.IndexOf('#');
                return marker < 0 ? value : value[..marker];
            })
            .Distinct(StringComparer.Ordinal)
            .Where(value => !document.Pages.ContainsKey(value))
            .ToArray();
        var removedComponents = allChangedComponents.Where(value => !document.ReusableComponents.ContainsKey(value)).ToArray();

        performance.Stop();
        HttpContext?.RequestServices.GetService<ILogger<WebsitePlatformController>>()?.LogInformation(
            "WebsiteStudio mutation completed SiteKey={SiteKey} Operations={Operations} ChangedScopes={ChangedScopes} RequestBytes={RequestBytes} DurationMs={DurationMs}",
            actor.SiteKey,
            request.Operations.Count,
            result.ChangedScopes.Count,
            Request.ContentLength ?? 0,
            performance.ElapsedMilliseconds);

        return Ok(new
        {
            source = "canonical_v3_mutation",
            revision = state.Revision,
            performance = new
            {
                durationMs = performance.ElapsedMilliseconds,
                operationCount = request.Operations.Count,
                changedScopeCount = result.ChangedScopes.Count
            },
            changedScopes = result.ChangedScopes,
            fingerprints = result.Fingerprints,
            changes = new
            {
                pages = changedPagePaths.ToDictionary(value => value, value => document.Pages[value], StringComparer.Ordinal),
                theme = result.ChangedScopes.Contains("@theme", StringComparer.Ordinal) ? document.Theme : null,
                faviconChanged = result.ChangedScopes.Contains("@favicon", StringComparer.Ordinal),
                faviconImageDataUrl = result.ChangedScopes.Contains("@favicon", StringComparer.Ordinal) ? document.FaviconImageDataUrl : null,
                breakpoints = result.ChangedScopes.Contains("@breakpoints", StringComparer.Ordinal) ? document.Breakpoints : null,
                shellHeader = result.ChangedScopes.Any(value => value.StartsWith("@shell/header", StringComparison.Ordinal)) ? document.Shell.Header : null,
                shellFooter = result.ChangedScopes.Any(value => value.StartsWith("@shell/footer", StringComparison.Ordinal)) ? document.Shell.Footer : null,
                reusableComponents = changedComponents.ToDictionary(value => value, value => document.ReusableComponents[value], StringComparer.Ordinal),
                removedPages,
                removedComponents,
                store = result.ChangedScopes.Contains("@store-presentation", StringComparer.Ordinal) ? document.Store : null
            },
            savedUtc = state.UpdatedUtc,
            drafts = WebsiteNamedDraftReader.ReadMetadata(state)
        });
    }

    private static List<WebsiteNamedDraft> ReadDrafts(WebsiteContentState state) => WebsiteNamedDraftReader.Read(state);

    private static bool NamedDraftEntriesAreComplete(WebsiteContentState state, IReadOnlyCollection<WebsiteNamedDraft> drafts)
    {
        if (string.IsNullOrWhiteSpace(state.NamedDraftsJson)) return drafts.Count == 0;
        try
        {
            using var parsed = JsonDocument.Parse(state.NamedDraftsJson);
            return parsed.RootElement.ValueKind == JsonValueKind.Array &&
                parsed.RootElement.GetArrayLength() == drafts.Count;
        }
        catch (JsonException) { return false; }
    }

    public sealed record PreserveNamedDraftRequest(
        string Ticket, Guid PublicationScopeId, long ExpectedRevision,
        Guid DraftId, string DraftName, string DocumentJson);

    [HttpGet("manage/drafts/preservation")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> NamedDraftPreservation(
        [FromQuery] string ticket, [FromQuery] Guid publicationScopeId,
        [FromQuery] Guid draftId, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        if (publicationScopeId == Guid.Empty || draftId == Guid.Empty) return BadRequest();
        // Read only the existing owner row. Never initialize a website during readback.
        // Keep the existing synchronous large-LOB read workaround.
        cancellationToken.ThrowIfCancellationRequested();
        var stored = _db.Set<WebsiteContentState>().AsNoTracking()
            .Where(state => state.Id == publicationScopeId &&
                state.OwnerKey == actor.OwnerUserId && state.SiteKey == actor.SiteKey &&
                (actor.SiteKey != WebsiteEditorSiteKeys.Business ||
                 state.CommerceBusinessId == actor.CommerceBusinessId))
            .Select(state => new { state.Id, state.Revision, state.NamedDraftsJson, state.UpdatedUtc })
            .SingleOrDefault();
        cancellationToken.ThrowIfCancellationRequested();
        if (stored is null) return NotFound();
        var draft = ReadDrafts(new WebsiteContentState
        {
            Id = stored.Id, NamedDraftsJson = stored.NamedDraftsJson, UpdatedUtc = stored.UpdatedUtc
        }).SingleOrDefault(value => value.Id == draftId);
        return Ok(new
        {
            publicationScopeId = stored.Id, revision = stored.Revision, found = draft is not null,
            draft = draft is null ? null : new { draft.Id, draft.Name, draft.DocumentJson, draft.UpdatedUtc }
        });
    }

    [HttpPost("manage/drafts/preserve")]
    [RequestSizeLimit(4_500_000)]
    public async Task<IActionResult> PreserveNamedDraft(
        [FromBody] PreserveNamedDraftRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || request.PublicationScopeId == Guid.Empty || request.DraftId == Guid.Empty ||
            request.ExpectedRevision < 0 || request.ExpectedRevision == long.MaxValue ||
            string.IsNullOrWhiteSpace(request.DocumentJson))
            return BadRequest(new { error = "website_draft_preservation_invalid" });
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        cancellationToken.ThrowIfCancellationRequested();
        var state = _db.Set<WebsiteContentState>()
            .SingleOrDefault(value => value.Id == request.PublicationScopeId &&
                value.OwnerKey == actor.OwnerUserId && value.SiteKey == actor.SiteKey &&
                (actor.SiteKey != WebsiteEditorSiteKeys.Business ||
                 value.CommerceBusinessId == actor.CommerceBusinessId));
        cancellationToken.ThrowIfCancellationRequested();
        if (state is null) return NotFound();
        var name = request.DraftName?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            return BadRequest(new { message = "Enter a draft name of 1–100 characters." });
        var drafts = ReadDrafts(state);
        var existing = drafts.SingleOrDefault(value => value.Id == request.DraftId);
        if (existing is not null)
        {
            // A lost response may be retried only as this exact immutable new copy.
            if (!string.Equals(existing.Name, name, StringComparison.Ordinal) ||
                !string.Equals(existing.DocumentJson, request.DocumentJson, StringComparison.Ordinal))
                return Conflict(new { error = "website_draft_identity_conflict",
                    message = "This draft identity already contains different saved work. Read it back before creating another copy." });
            return Ok(new { publicationScopeId = state.Id, revision = state.Revision,
                draftId = existing.Id, alreadySaved = true });
        }
        if (state.Revision != request.ExpectedRevision)
            return Conflict(new { error = "revision_conflict", revision = state.Revision });
        if (drafts.Any(value => string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase)))
            return Conflict(new { message = "That name already exists. Choose a new draft name to preserve this copy." });

        System.Text.Json.Nodes.JsonArray storedDrafts;
        try
        {
            storedDrafts = string.IsNullOrWhiteSpace(state.NamedDraftsJson)
                ? new System.Text.Json.Nodes.JsonArray()
                : System.Text.Json.Nodes.JsonNode.Parse(state.NamedDraftsJson) as System.Text.Json.Nodes.JsonArray
                    ?? throw new ArgumentException("The saved draft list could not be read safely.");
            if (storedDrafts.Count >= 20)
                return BadRequest(new { message = "You can keep up to 20 drafts. Existing drafts were preserved; free a draft slot before creating another." });
            using var parsed = JsonDocument.Parse(request.DocumentJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object ||
                !parsed.RootElement.TryGetProperty("version", out var version) ||
                !version.TryGetInt32(out var number) || number != WebsiteStudioContract.CurrentDocumentVersion ||
                !parsed.RootElement.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("The local draft must use the canonical v3 document schema.");
            // Reject unstable or legacy data instead of silently changing the preserved copy.
            var document = Read(request.DocumentJson);
            await ValidateCompositionMediaOwnershipAsync(actor, document, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { error = "website_draft_preservation_invalid", message = ex.Message });
        }

        var draft = new WebsiteNamedDraft
        {
            Id = request.DraftId, Name = name, DocumentJson = request.DocumentJson, UpdatedUtc = DateTime.UtcNow
        };
        // Append to the existing JSON array; retain every previous entry and its fields.
        // This is a private named copy. Only the existing governed LoadDraft can make it active.
        storedDrafts.Add(JsonSerializer.SerializeToNode(draft, JsonOptions));
        state.NamedDraftsJson = storedDrafts.ToJsonString(JsonOptions);
        var previousRevision = state.Revision;
        state.Revision++;
        if (state.ScheduledRevision == previousRevision)
            state.ScheduledRevision = state.Revision; // The scheduled active document did not change.
        state.UpdatedUtc = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "revision_conflict" }); }
        return Ok(new { publicationScopeId = state.Id, revision = state.Revision,
            draftId = draft.Id, alreadySaved = false });
    }

    public sealed record RecoverDuplicateDraftRequest(
        string Ticket, long ExpectedRevision, Guid RecoveryId,
        string SourceFingerprint, string RepairedFingerprint, bool AcceptPresentationConversions = false,
        bool AcceptRuntimeAnchorConversions = false);

    [HttpGet("manage/draft-recovery")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> PlanDraftRecovery([FromQuery] string ticket, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken)) return Forbid();
        var state = await _db.Set<WebsiteContentState>().AsNoTracking()
            .Where(value => value.OwnerKey == actor.OwnerUserId && value.SiteKey == actor.SiteKey &&
                (actor.SiteKey != WebsiteEditorSiteKeys.Business || value.CommerceBusinessId == actor.CommerceBusinessId))
            .Select(value => new { value.DraftJson, value.Revision })
            .SingleOrDefaultAsync(cancellationToken);
        if (state is null) return NotFound();
        var sourceFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(state.DraftJson))).ToLowerInvariant();
        try
        {
            var plan = WebsiteContentSanitizer.PlanDuplicateIdentityRecovery(state.DraftJson, JsonOptions, actor.SiteKey);
            return Ok(new { revision = state.Revision, plan.SourceFingerprint, plan.RepairedFingerprint,
                changes = plan.Changes, presentationChanges = plan.PresentationChanges, runtimeAnchorChanges = plan.RuntimeAnchorChanges,
                preservesPublishedVersion = true, preservesOriginalDraft = true });
        }
        catch (WebsiteContentSanitizer.RecoveryReviewException ex)
        {
            return Conflict(new { error = "website_draft_recovery_requires_review",
                message = "This saved website needs an independent recovery review. All content was preserved.",
                revision = state.Revision, sourceFingerprint, reasonCode = ex.ReasonCode,
                collisionCount = ex.CollisionCount, collisions = ex.Collisions,
                differenceCount = ex.DifferenceCount, differences = ex.Differences, truncated = ex.Truncated });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
        {
            return Conflict(new { error = "website_draft_recovery_requires_review",
                message = "This saved website needs an independent recovery review. All content was preserved.",
                revision = state.Revision, sourceFingerprint,
                reasonCode = ex is JsonException ? "malformed_document" : "unsupported_document" });
        }
    }

    [HttpPost("manage/draft-recovery")]
    public async Task<IActionResult> RecoverDuplicateDraft(
        [FromBody] RecoverDuplicateDraftRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || request.RecoveryId == Guid.Empty || request.ExpectedRevision < 0 ||
            request.ExpectedRevision == long.MaxValue)
            return BadRequest();
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken)) return Forbid();
        var state = await StateAsync(actor, cancellationToken);
        var drafts = ReadDrafts(state);
        static string Fingerprint(string value) => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        var existing = drafts.SingleOrDefault(draft => draft.Id == request.RecoveryId);
        if (existing is not null)
        {
            if (state.Revision == request.ExpectedRevision + 1 &&
                Fingerprint(existing.DocumentJson) == request.SourceFingerprint &&
                Fingerprint(state.DraftJson) == request.RepairedFingerprint)
                return Ok(new { revision = state.Revision, recoveryId = existing.Id, alreadyRecovered = true });
            return Conflict(new { error = "website_recovery_identity_conflict" });
        }
        if (state.Revision != request.ExpectedRevision ||
            Fingerprint(state.DraftJson) != request.SourceFingerprint)
            return Conflict(new { error = "revision_conflict", revision = state.Revision });
        if (!NamedDraftEntriesAreComplete(state, drafts) || drafts.Count >= 20)
            return Conflict(new { error = "website_recovery_snapshot_unavailable",
                message = "Recovery requires one available saved-draft slot. All existing drafts were preserved." });

        WebsiteContentSanitizer.DuplicateIdentityRecovery plan;
        System.Text.Json.Nodes.JsonArray storedDrafts;
        try
        {
            plan = WebsiteContentSanitizer.PlanDuplicateIdentityRecovery(state.DraftJson, JsonOptions, actor.SiteKey);
            if (plan.RepairedFingerprint != request.RepairedFingerprint)
                return Conflict(new { error = "website_recovery_plan_changed" });
            if (plan.RuntimeAnchorChanges.Count > 0 && !request.AcceptRuntimeAnchorConversions)
                return Conflict(new { error = "website_recovery_runtime_review_required",
                    message = "Review the protected form anchor conversions before applying this plan." });
            if (plan.PresentationChanges.Count > 0 && !request.AcceptPresentationConversions)
                return Conflict(new { error = "website_recovery_presentation_review_required",
                    message = "Reopen the recovery review to inspect the inactive presentation conversions before applying this plan." });
            storedDrafts = string.IsNullOrWhiteSpace(state.NamedDraftsJson)
                ? new System.Text.Json.Nodes.JsonArray()
                : System.Text.Json.Nodes.JsonNode.Parse(state.NamedDraftsJson) as System.Text.Json.Nodes.JsonArray
                    ?? throw new ArgumentException("Invalid saved draft list.");
            if (storedDrafts.Count >= 20)
                return Conflict(new { error = "website_recovery_snapshot_unavailable" });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
        {
            return Conflict(new { error = "website_draft_recovery_requires_review" });
        }

        // Preserve exact original bytes in the existing snapshot authority in
        // the same concurrency-guarded write. No publication is changed.
        var backup = new WebsiteNamedDraft
        {
            Id = request.RecoveryId,
            Name = "Before identity recovery " + request.RecoveryId.ToString("N"),
            DocumentJson = state.DraftJson,
            UpdatedUtc = DateTime.UtcNow
        };
        storedDrafts.Add(JsonSerializer.SerializeToNode(backup, JsonOptions));
        state.NamedDraftsJson = storedDrafts.ToJsonString(JsonOptions);
        state.DraftJson = plan.DocumentJson;
        state.Revision++;
        state.UpdatedUtc = DateTime.UtcNow;
        state.ScheduledPublishUtc = null;
        state.ScheduledRevision = null;
        state.ScheduledActorJson = null;
        state.ScheduleError = null;
        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "revision_conflict" }); }
        return Ok(new { revision = state.Revision, recoveryId = backup.Id, alreadyRecovered = false });
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
        if (!NamedDraftEntriesAreComplete(state, drafts))
            return Conflict(new { error = "website_draft_list_requires_review",
                message = "The saved draft list contains entries that need review. All existing drafts were preserved." });
        var draft = drafts.SingleOrDefault(d => d.Id == request.DraftId);
        if (draft is null) return NotFound();
        if (delete) drafts.Remove(draft);
        else
        {
            WebsiteContentDocument restored;
            try
            {
                restored = Read(draft.DocumentJson);
                if (restored.LegacyMigration is null)
                {
                    var baseline = Read(state.DraftJson);
                    var facts = actor.SiteKey == WebsiteEditorSiteKeys.Business && actor.CommerceBusinessId.HasValue
                        ? await WebsiteBusinessFacts.LoadAsync(_db, actor.CommerceBusinessId.Value, cancellationToken)
                        : null;
                    var actions = await BuildCallToActionCatalogAsync(actor, facts, cancellationToken, baseline);
                    var source = WebsiteSiteSource.Serialize(restored);
                    restored = WebsiteSiteSource.Parse(source, baseline, actions).Document;
                    WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, restored);
                    WebsiteSiteSource.ValidateCanonical(restored, actions);
                    await ValidateCompositionMediaOwnershipAsync(actor, restored, cancellationToken);
                    state.DraftJson = JsonSerializer.Serialize(restored, JsonOptions);
                }
                else
                {
                    // Historical pre-v3 named drafts remain eligible only for the
                    // existing explicit one-way materialization boundary.
                    state.DraftJson = draft.DocumentJson;
                }
            }
            catch (WebsiteSiteSourceProtectionException ex)
            {
                return BadRequest(new
                {
                    error = "website_draft_restore_protected",
                    message = ex.Message,
                    canonicalProtectionViolation = true
                });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
            {
                return BadRequest(new { error = "website_draft_restore_invalid", message = ex.Message });
            }

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

    [HttpGet("manage/publication-status")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> PublicationStatus([FromQuery] string ticket, [FromQuery] Guid requestId,
        CancellationToken cancellationToken = default, [FromQuery] long? expectedRevision = null)
    {
        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        if (requestId == Guid.Empty || expectedRevision is < 0)
            return BadRequest(new { error = "invalid_publication_request" });

        // Commerce is an additional tenant boundary for business websites. Founder
        // and Protect tickets own the website even after it acquires a store.
        var state = await _db.Set<WebsiteContentState>().AsNoTracking()
            .Where(s => s.OwnerKey == actor.OwnerUserId && s.SiteKey == actor.SiteKey &&
                (actor.SiteKey != WebsiteEditorSiteKeys.Business ||
                    s.CommerceBusinessId == actor.CommerceBusinessId))
            .Select(s => new { s.Id, s.Revision, s.PublishedVersionId })
            .SingleOrDefaultAsync(cancellationToken);

        // Preserve this order: after observing a newer state revision, an older
        // publisher cannot commit under the revision concurrency token. The
        // subsequent immutable identity lookup determines whether it already did.
        var version = state is null ? null : await _db.Set<WebsiteContentVersion>().AsNoTracking()
            .Where(v => v.StateId == state.Id && v.Id == requestId)
            .Select(v => new { versionId = v.Id, publishedRevision = v.Revision }).SingleOrDefaultAsync(cancellationToken);
        var rejection = state is not null && version is null &&
                expectedRevision.HasValue && state.Revision > expectedRevision.Value
            ? new { disposition = "rejected_before_commit", requestId,
                expectedRevision = expectedRevision.Value, currentRevision = state.Revision }
            : null;
        return Ok(new { requestId, expectedRevision, publicationScopeId = state?.Id,
            found = version is not null, publication = version, revision = state?.Revision,
            isCurrent = version is not null && state?.PublishedVersionId == version.versionId,
            publicationRejection = rejection });
    }

    [HttpPost("manage/publish")]
    public async Task<IActionResult> Publish([FromBody] PublishRequest request, CancellationToken cancellationToken = default)
    {
        using var diagnosticStage = Shared.Diagnostics.RuntimeDiagnosticScope.Current?.Stage(Shared.Diagnostics.RuntimeDiagnosticStage.PublicationValidate);
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken)) return Forbid();
        var state = await StateAsync(actor, cancellationToken);
        if (request.RequestId == Guid.Empty) return BadRequest(new { error = "invalid_request_id" });
        if (request.RequestId.HasValue)
        {
            var prior = await _db.Set<WebsiteContentVersion>().AsNoTracking()
                .SingleOrDefaultAsync(v => v.Id == request.RequestId && v.StateId == state.Id, cancellationToken);
            if (prior is not null)
                return Ok(new { revision = state.Revision, publishedRevision = prior.Revision, versionId = prior.Id,
                    alreadyPublished = true, isCurrent = state.PublishedVersionId == prior.Id });
        }
        if (request.ExpectedRevision != state.Revision)
            return Conflict(new { error = "revision_conflict",
                publicationRejection = PublicationRejectionReceipt(request.RequestId, request.ExpectedRevision) });
        return await PublishDocumentAsync(actor, state, Read(state.DraftJson), state.ImportReportJson, cancellationToken, request.RequestId);
    }

    private static object? PublicationRejectionReceipt(Guid? requestId, long expectedRevision) =>
        requestId is { } id && id != Guid.Empty
            ? new { disposition = "rejected_before_commit", requestId = id, expectedRevision }
            : null;

    public sealed record LocalProtectCapture(string Schema, string OwnerUserId, WebsiteContentDocument Document);

    [NonAction]
    public async Task<IActionResult> BootstrapLocalProtectAsync(
        string databasePath, LocalProtectCapture capture, CancellationToken cancellationToken = default)
    {
        var environment = HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        if (environment.EnvironmentName != "Development" || !_db.Database.IsSqlite() ||
            !Path.IsPathFullyQualified(databasePath) ||
            Path.GetFullPath(_db.Database.GetDbConnection().DataSource) != Path.GetFullPath(databasePath))
            throw new InvalidOperationException("local_website_database_scope_required");
        var upn = (_configuration["Founder:Upn"] ?? "").Trim().ToLowerInvariant();
        if (upn.Length == 0) throw new InvalidOperationException("local_website_owner_required");
        var owner = await _db.AgentTrackingProfiles.AsNoTracking().SingleOrDefaultAsync(
            p => p.AgentUpn.ToLower() == upn && p.Status.ToLower() == "active", cancellationToken);
        if (owner is null || string.IsNullOrWhiteSpace(owner.AgentUserId))
            throw new InvalidOperationException("local_website_active_owner_required");
        var ownerKey = NormalizeOwner(owner.AgentUserId);
        if (capture.Schema != "legend-local-protect/v1" || capture.OwnerUserId != ownerKey)
            throw new InvalidOperationException("local_website_capture_owner_mismatch");
        // Captures enter through the same normalization boundary as materialization.
        // Persisted drafts are never normalized during reads or overwritten here.
        WebsiteContentDocument document;
        try
        {
            WebsiteContentSanitizer.EnsureUniqueCanonicalNodeIds(capture.Document);
            document = WebsiteContentSanitizer.Sanitize(capture.Document);
            WebsiteSystemTemplateAuthority.Apply(WebsiteEditorSiteKeys.Protect, document);
            _ = WebsiteContentSanitizer.ReadPersisted(JsonSerializer.Serialize(document, JsonOptions), JsonOptions);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { error = "local_website_capture_invalid" });
        }
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        if (await _db.Set<WebsiteContentState>().AnyAsync(
                p => p.OwnerKey == ownerKey && p.SiteKey == WebsiteEditorSiteKeys.Protect, cancellationToken))
            throw new InvalidOperationException("local_website_already_exists");
        var state = new WebsiteContentState { OwnerKey = ownerKey, SiteKey = WebsiteEditorSiteKeys.Protect, DraftJson = "{}" };
        _db.Add(state);
        // This scope is local operator input, never a protected editor credential.
        // HTTP callers retain their normal ticket and publication authorization.
        var scope = new WebsiteEditorTicket(WebsiteEditorSiteKeys.Protect, ownerKey, owner.Slug,
            false, DateTime.UtcNow.AddMinutes(1), ActorUserId: owner.AgentUserId);
        var result = await PublishDocumentAsync(scope, state, document, null, cancellationToken);
        if (result is OkObjectResult) await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<IActionResult> PublishDocumentAsync(
        WebsiteEditorTicket actor, WebsiteContentState state, WebsiteContentDocument document,
        string? importReportJson, CancellationToken cancellationToken, Guid? requestId = null)
    {
        using var diagnosticStage = new Shared.Diagnostics.RuntimeDiagnosticStageSequence(Shared.Diagnostics.RuntimeDiagnosticStage.PublicationValidate);
        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, document);
        CommerceBusiness? business = null;
        WebsiteBusinessFacts? facts = null;
        if (actor.SiteKey == WebsiteEditorSiteKeys.Business)
        {
            business = await _db.CommerceBusinesses.AsNoTracking().SingleAsync(b => b.Id == actor.CommerceBusinessId, cancellationToken);
            facts = await WebsiteBusinessFacts.LoadAsync(_db, business.Id, cancellationToken);
        }
        if (document.LegacyMigration is not null)
            return Conflict(new { error = "website_materialization_required",
                message = "This website must be materialized into the canonical v3 composition graph before publishing.",
                publicationRejection = PublicationRejectionReceipt(requestId, state.Revision) });

        var ctaOptions = await BuildCallToActionCatalogAsync(actor, facts, cancellationToken, document);
        try
        {
            WebsiteSiteSource.ValidateCanonical(document, ctaOptions);
            await ValidateCompositionMediaOwnershipAsync(actor, document, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = "website_preflight_failed", message = ex.Message,
                publicationRejection = PublicationRejectionReceipt(requestId, state.Revision) });
        }
        var ctaError = WebsiteCallToActionCatalog.PrepareForPublish(document, ctaOptions);
        if (ctaError is not null)
            return BadRequest(new { error = "button_destination_required", message = ctaError,
                publicationRejection = PublicationRejectionReceipt(requestId, state.Revision) });
        state.DraftJson = JsonSerializer.Serialize(document, JsonOptions);
        var version = new WebsiteContentVersion { Id = requestId ?? Guid.NewGuid(), StateId = state.Id, Revision = state.Revision + 1,
            DocumentJson = state.DraftJson, ImportReportJson = importReportJson, ActorUserId = actor.ActorUserId! };
        if (business is not null)
        {
            var collections = await new WebsiteCollectionProjectionService(_db)
                .LoadAsync(document, business.Id, cancellationToken);
            var compiler = HttpContext.RequestServices.GetRequiredService<Infrastructure.WebsitePublishing.WebsitePageCompiler>();
            version.CompiledPagesJson = await compiler.CompileAsync(document, business, facts!, collections, cancellationToken);
        }
        else if (actor.SiteKey is WebsiteEditorSiteKeys.Legend or WebsiteEditorSiteKeys.Protect)
        {
            // Compile before the published pointer is committed, as scoped
            // business sites already do. Readers never see an incomplete version.
            var compiler = ControllerContext.HttpContext?.RequestServices?
                .GetService<Infrastructure.WebsitePublishing.WebsitePageCompiler>();
            if (compiler is null && _db.Database.IsRelational())
                throw new InvalidOperationException("website_compiler_not_configured");
            if (compiler is not null)
            {
                var scope = await PublishedStoreScopeAsync(actor.OwnerUserId, actor.SiteKey, document, cancellationToken);
                var publicPayload = JsonSerializer.Serialize(new
                {
                    siteKey = actor.SiteKey,
                    publication = new { versionId = version.Id, revision = version.Revision },
                    business = (object?)null,
                    businessName = (string?)null,
                    facts = (object?)null,
                    collections = Array.Empty<object>(),
                    store = await StorePayloadAsync(actor.SiteKey, document, scope, ticket: null, cancellationToken),
                    ctaCatalog = new { options = ctaOptions },
                    document,
                    legacyMigration = (object?)null
                }, JsonOptions);
                version.CompiledPagesJson = await compiler.CompileLegendAsync(publicPayload, cancellationToken);
            }
        }
        diagnosticStage.Next(Shared.Diagnostics.RuntimeDiagnosticStage.PublicationCommit);
        _db.Set<WebsiteContentVersion>().Add(version);
        state.ImportReportJson = importReportJson;
        state.PublishedVersionId = version.Id;
        state.ScheduledPublishUtc = null;
        state.ScheduledActorJson = null;
        state.ScheduledRevision = null;
        state.ScheduleError = null;
        state.Revision++;
        state.UpdatedUtc = DateTime.UtcNow;
        await Infrastructure.WebsitePublishing.WebsiteSnapshotRetention.StageAsync(_db, state, cancellationToken);
        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "revision_conflict" }); }
        return Ok(new { revision = state.Revision, publishedRevision = version.Revision, versionId = version.Id, document });
    }

    [HttpPost("manage/rollback")]
    public async Task<IActionResult> Rollback([FromBody] RollbackRequest request, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await CanPublishAsync(actor, cancellationToken)) return Forbid();
        var state = await StateAsync(actor, cancellationToken);
        if (state.Revision != request.ExpectedRevision) return Conflict(new { error = "revision_conflict" });
        var previous = await _db.Set<WebsiteContentVersion>().AsNoTracking().SingleOrDefaultAsync(v => v.Id == request.VersionId && v.StateId == state.Id && v.SnapshotRetiredUtc == null, cancellationToken);
        if (previous is null) return NotFound();
        return await PublishDocumentAsync(actor, state, Read(previous.DocumentJson), previous.ImportReportJson, cancellationToken);
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
        var baseline = Read(state.DraftJson);
        var importer = HttpContext.RequestServices.GetRequiredService<WebsiteImportService>();
        WebsiteImportResult result;
        if (request.Document is not null)
        {
            using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request.Document, JsonOptions));
            result = await importer.PrepareExportAsync(input, false, baseline, true, actor.OwnerUserId, MediaBaseUrl(), cancellationToken);
        }
        else result = await importer.PrepareAsync(request.SourceUrl ?? "", baseline, true, actor.OwnerUserId, MediaBaseUrl(), cancellationToken);
        state.ScheduledPublishUtc = null;
        state.ScheduledActorJson = null;
        state.ScheduledRevision = null;
        state.ImportReportJson = JsonSerializer.Serialize(result.Report, JsonOptions);
        WebsiteContentDocument importedDocument;
        try
        {
            importedDocument = await ReconcileImportedDocumentAsync(actor, baseline, result.Document, cancellationToken);
        }
        catch (WebsiteSiteSourceProtectionException ex)
        {
            return BadRequest(new { error = "website_import_protected", message = ex.Message, canonicalProtectionViolation = true });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = "website_import_invalid", message = ex.Message });
        }
        state.DraftJson = JsonSerializer.Serialize(importedDocument, JsonOptions);
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
        var ownerPreview = actor?.OwnerUserId == asset.OwnerKey;
        if (!ownerPreview)
        {
            var versions = await (from state in _db.Set<WebsiteContentState>().AsNoTracking()
                                  join version in _db.Set<WebsiteContentVersion>().AsNoTracking() on state.PublishedVersionId equals version.Id
                                  where state.OwnerKey == asset.OwnerKey
                                  select version.DocumentJson).ToListAsync(cancellationToken);
            if (!versions.Any(json => WebsiteMediaReferenceCatalog.References(Read(json), id)))
                return NotFound();
        }

        var etag = "\"" + asset.Sha256 + "\"";
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = ownerPreview
            ? "private, max-age=3600"
            : "public, max-age=86400";
        if (Request.Headers.IfNoneMatch.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
            return StatusCode(StatusCodes.Status304NotModified);

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
    public sealed record WebsiteMediaImportRequest(string Ticket, string SourceUrl);

    [HttpPost("manage/media/import")]
    public async Task<IActionResult> ImportMedia(
        [FromBody] WebsiteMediaImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(request.Ticket, cancellationToken);
        if (actor is null) return Unauthorized();

        WebsiteMediaAsset asset;
        try
        {
            asset = await HttpContext.RequestServices.GetRequiredService<WebsiteImportService>()
                .ImportImageAsync(request.SourceUrl, actor.OwnerUserId, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = "website_media_import_invalid", message = ex.Message });
        }
        catch (HttpRequestException)
        {
            return BadRequest(new { error = "website_media_import_unavailable", message = "The public image could not be retrieved safely." });
        }

        return Ok(new
        {
            id = asset.Id,
            name = MediaDisplayName(asset),
            url = MediaBaseUrl() + "/api/website-content/media/" + asset.Id,
            contentType = asset.ContentType,
            sizeBytes = asset.SizeBytes,
            createdUtc = asset.CreatedUtc
        });
    }

    [HttpPost("manage/media")]
    [RequestSizeLimit(26_000_000)]
    public async Task<IActionResult> UploadMedia(CancellationToken cancellationToken = default)
    {
        var transport = await MultipartUploadTransport.ReadAsync(Request, cancellationToken);
        if (!transport.IsValid || transport.Form is null)
            return BadRequest(new
            {
                error = "website_media_transport_invalid",
                message = transport.ErrorMessage
            });

        var form = transport.Form;
        var ticket = form["ticket"].FirstOrDefault();
        var file = form.Files.GetFile("file");
        var actor = await AuthorizeAsync(ticket ?? string.Empty, cancellationToken);
        if (actor is null) return Unauthorized();
        if (file is null || file.Length <= 0 || file.Length > 25_000_000)
            return BadRequest(new { error = "website_media_file_invalid" });

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);
        var media = HttpContext.RequestServices.GetRequiredService<WebsiteMediaService>();
        WebsiteMediaAsset asset;
        try
        {
            asset = await media.StoreAsync(
                actor.OwnerUserId,
                Path.GetFileName(file.FileName),
                file.FileName,
                buffer.ToArray(),
                cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = "website_media_file_invalid", message = ex.Message });
        }

        return Ok(new
        {
            id = asset.Id,
            name = MediaDisplayName(asset),
            url = MediaBaseUrl() + "/api/website-content/media/" + asset.Id,
            contentType = asset.ContentType,
            sizeBytes = asset.SizeBytes,
            createdUtc = asset.CreatedUtc
        });
    }

    [HttpGet("manage/media")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> MediaLibrary(
        [FromQuery] string ticket,
        [FromQuery] string? q = null,
        [FromQuery] string? kind = null,
        [FromQuery] long? cursor = null,
        [FromQuery] int take = 100,
        [FromQuery] bool designMetadata = false,
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

        if (cursor.HasValue)
        {
            if (cursor.Value < DateTime.MinValue.Ticks || cursor.Value > DateTime.MaxValue.Ticks)
                return BadRequest(new { error = "invalid_media_cursor" });
            var before = new DateTime(cursor.Value, DateTimeKind.Utc);
            query = query.Where(asset => asset.CreatedUtc < before);
        }
        take = Math.Clamp(take, 1, designMetadata ? 24 : 100);
        var assets = await query.OrderByDescending(asset => asset.CreatedUtc).ThenByDescending(asset => asset.Id)
            .Take(take).ToListAsync(cancellationToken);

        var visualMetadata = new Dictionary<Guid, WebsiteMediaVisualMetadata>();
        if (designMetadata)
        {
            var mediaService = HttpContext.RequestServices.GetRequiredService<WebsiteMediaService>();
            var images = assets.Where(asset =>
                asset.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)).ToArray();
            var gate = new SemaphoreSlim(4, 4);
            try
            {
                var inspections = images.Select(async asset =>
                {
                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        return (asset.Id, Metadata: await mediaService.InspectVisualMetadataAsync(asset, cancellationToken));
                    }
                    finally
                    {
                        gate.Release();
                    }
                });
                foreach (var (id, metadata) in await Task.WhenAll(inspections))
                    visualMetadata[id] = metadata;
            }
            finally
            {
                gate.Dispose();
            }
        }

        var state = await StateAsync(actor, cancellationToken);
        var document = Read(state.DraftJson);
        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, document);
        var usage = BuildMediaUsageIndex(document);

        return Ok(new
        {
            assets = assets.Select(asset => new
            {
                asset.Id,
                name = MediaDisplayName(asset),
                url = MediaBaseUrl() + "/api/website-content/media/" + asset.Id,
                kind = asset.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? "video" : "image",
                asset.ContentType,
                asset.SizeBytes,
                asset.CreatedUtc,
                widthPx = visualMetadata.TryGetValue(asset.Id, out var visual) ? visual.WidthPx : null,
                heightPx = visualMetadata.TryGetValue(asset.Id, out visual) ? visual.HeightPx : null,
                aspectRatio = visualMetadata.TryGetValue(asset.Id, out visual) ? visual.AspectRatio : null,
                orientation = visualMetadata.TryGetValue(asset.Id, out visual) ? visual.Orientation : null,
                usageLocations = usage.TryGetValue(asset.Id, out var locations) ? locations.Select(value => value.Scope).Distinct(StringComparer.Ordinal).ToArray() : Array.Empty<string>(),
                roles = usage.TryGetValue(asset.Id, out var roles) ? roles.Select(value => value.Role).Where(value => value is not null).Distinct(StringComparer.Ordinal).ToArray() : Array.Empty<string?>(),
                altDescriptions = usage.TryGetValue(asset.Id, out var alts) ? alts.Select(value => value.Alt).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray() : Array.Empty<string?>(),
                objectPositions = usage.TryGetValue(asset.Id, out var positions) ? positions.Select(value => value.ObjectPosition).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray() : Array.Empty<string?>()
            }),
            nextCursor = assets.Count == take ? assets[^1].CreatedUtc.Ticks : (long?)null
        });
    }

    private sealed record WebsiteMediaDesignUsage(
        string Scope,
        string? Role,
        string? Alt,
        string? ObjectPosition);

    private static Dictionary<Guid, List<WebsiteMediaDesignUsage>> BuildMediaUsageIndex(
        WebsiteContentDocument document)
    {
        var result = new Dictionary<Guid, List<WebsiteMediaDesignUsage>>();

        static string? RoleFor(WebsiteCompositionNode node)
        {
            var identity = ((node.Id ?? string.Empty) + " " + (node.ClassName ?? string.Empty)).ToLowerInvariant();
            if (identity.Contains("hero", StringComparison.Ordinal)) return "hero";
            if (identity.Contains("logo", StringComparison.Ordinal) || identity.Contains("brand", StringComparison.Ordinal)) return "brand";
            if (identity.Contains("gallery", StringComparison.Ordinal)) return "gallery";
            if (identity.Contains("feature", StringComparison.Ordinal)) return "feature";
            if (identity.Contains("card", StringComparison.Ordinal)) return "card";
            return node.Type;
        }

        void Visit(IEnumerable<WebsiteCompositionNode>? nodes, string scope)
        {
            foreach (var node in nodes ?? [])
            {
                if (node.MediaAssetId.HasValue)
                {
                    if (!result.TryGetValue(node.MediaAssetId.Value, out var usages))
                    {
                        usages = [];
                        result[node.MediaAssetId.Value] = usages;
                    }

                    usages.Add(new(
                        scope + "#" + node.Id,
                        RoleFor(node),
                        node.Alt,
                        node.Style?.ObjectPosition));
                }

                Visit(node.Children, scope);
            }
        }

        Visit(document.Shell?.Header, "@shell/header");
        Visit(document.Shell?.Footer, "@shell/footer");
        foreach (var (pagePath, page) in document.Pages ?? new Dictionary<string, WebsitePageDocument>(StringComparer.Ordinal))
            Visit(page?.Composition, pagePath);
        foreach (var (componentId, component) in document.ReusableComponents ?? new Dictionary<string, WebsiteReusableComponentDefinition>(StringComparer.Ordinal))
            Visit(component?.Composition, "@component/" + componentId);

        return result;
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
    public async Task<IActionResult> ImportFile(CancellationToken cancellationToken = default)
    {
        var transport = await MultipartUploadTransport.ReadAsync(Request, cancellationToken);
        if (!transport.IsValid || transport.Form is null)
            return BadRequest(new { error = "website_import_transport_invalid", message = transport.ErrorMessage });

        var form = transport.Form;
        var ticket = form["ticket"].FirstOrDefault() ?? string.Empty;
        _ = long.TryParse(form["expectedRevision"].FirstOrDefault(), out var expectedRevision);
        _ = bool.TryParse(form["authorized"].FirstOrDefault(), out var authorized);
        var file = form.Files.GetFile("file");

        var actor = await AuthorizeAsync(ticket, cancellationToken);
        if (actor?.SiteKey != WebsiteEditorSiteKeys.Business) return Unauthorized();
        if (!authorized || file is null || file.Length <= 0 || file.Length > 50_000_000) return BadRequest();
        var state = await StateAsync(actor, cancellationToken);
        if (state.Revision != expectedRevision) return Conflict(new { error = "revision_conflict" });
        var baseline = Read(state.DraftJson);
        await using var input = file.OpenReadStream();
        var result = await HttpContext.RequestServices.GetRequiredService<WebsiteImportService>().PrepareExportAsync(input, Path.GetExtension(file.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase), baseline, true, actor.OwnerUserId, MediaBaseUrl(), cancellationToken);
        state.ImportReportJson = JsonSerializer.Serialize(result.Report, JsonOptions);
        WebsiteContentDocument importedDocument;
        try
        {
            importedDocument = await ReconcileImportedDocumentAsync(actor, baseline, result.Document, cancellationToken);
        }
        catch (WebsiteSiteSourceProtectionException ex)
        {
            return BadRequest(new { error = "website_import_protected", message = ex.Message, canonicalProtectionViolation = true });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = "website_import_invalid", message = ex.Message });
        }
        state.DraftJson = JsonSerializer.Serialize(importedDocument, JsonOptions);
        state.Revision++;
        state.ScheduledPublishUtc = null;
        state.ScheduledActorJson = null;
        state.ScheduledRevision = null;
        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "revision_conflict" }); }
        return Ok(new { document = Read(state.DraftJson), revision = state.Revision, report = result.Report });
    }
    private async Task<WebsiteContentDocument> ReconcileImportedDocumentAsync(
        WebsiteEditorTicket actor,
        WebsiteContentDocument baseline,
        WebsiteContentDocument imported,
        CancellationToken cancellationToken)
    {
        var facts = actor.SiteKey == WebsiteEditorSiteKeys.Business && actor.CommerceBusinessId.HasValue
            ? await WebsiteBusinessFacts.LoadAsync(_db, actor.CommerceBusinessId.Value, cancellationToken)
            : null;
        var actions = await BuildCallToActionCatalogAsync(actor, facts, cancellationToken, baseline);

        // Import is an ingestion boundary, never a second behavior authority.
        // Projecting through Site Source strips incoming provider/system wiring,
        // restores protected baseline behavior by stable ID, validates scoped
        // actions/runtime classes, and leaves presentation as the import payload.
        var protectedProjection = WebsiteSiteSource.Serialize(
            WebsiteContentSanitizer.Sanitize(imported));
        var reconciled = WebsiteSiteSource.Parse(
            protectedProjection,
            baseline,
            actions).Document;

        WebsiteSystemTemplateAuthority.Apply(actor.SiteKey, reconciled);
        WebsiteSiteSource.ValidateCanonical(reconciled, actions);
        await ValidateCompositionMediaOwnershipAsync(actor, reconciled, cancellationToken);
        return reconciled;
    }

    private async Task<object> BuildMediaPerformanceQualityAsync(
        WebsiteEditorTicket actor,
        WebsiteContentDocument document,
        CancellationToken cancellationToken)
    {
        var ids = WebsiteMediaReferenceCatalog.Collect(document);
        if (ids.Count == 0)
            return new { referencedAssetCount = 0, referencedBytes = 0L, checks = Array.Empty<object>() };

        var assets = await _db.Set<WebsiteMediaAsset>().AsNoTracking()
            .Where(asset => asset.OwnerKey == actor.OwnerUserId && ids.Contains(asset.Id))
            .ToListAsync(cancellationToken);
        var loopingVideos = WebsiteSiteSource.Flatten(document)
            .Where(entry =>
                entry.Node.Type == "video" &&
                entry.Node.VideoLoop == true &&
                entry.Node.MediaAssetId.HasValue)
            .Select(entry => entry.Node.MediaAssetId!.Value)
            .ToHashSet();

        var checks = new List<object>();
        foreach (var asset in assets)
        {
            var name = MediaDisplayName(asset);
            if (asset.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                asset.SizeBytes > 1_500_000)
                checks.Add(new
                {
                    code = "performance_heavy_image",
                    severity = "warning",
                    assetId = asset.Id,
                    message = $"Image '{name}' is {Math.Round(asset.SizeBytes / 1_000_000m, 1)} MB. Replace it with a lighter owned image for faster page load."
                });

            if (asset.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) &&
                loopingVideos.Contains(asset.Id) &&
                asset.SizeBytes > 8_000_000)
                checks.Add(new
                {
                    code = "performance_heavy_loop_video",
                    severity = "warning",
                    assetId = asset.Id,
                    message = $"Looping video '{name}' is {Math.Round(asset.SizeBytes / 1_000_000m, 1)} MB and preloads for playback. Use a lighter clip to protect first interaction speed."
                });
        }

        return new
        {
            referencedAssetCount = assets.Count,
            referencedBytes = assets.Sum(asset => (long)asset.SizeBytes),
            checks
        };
    }

    private async Task ValidateNewCompositionMediaOwnershipAsync(
        WebsiteEditorTicket actor,
        WebsiteContentDocument baseline,
        WebsiteContentDocument document,
        CancellationToken cancellationToken)
    {
        var ids = WebsiteMediaReferenceCatalog.Collect(document);
        ids.ExceptWith(WebsiteMediaReferenceCatalog.Collect(baseline));
        if (ids.Count == 0) return;

        var owned = await _db.Set<WebsiteMediaAsset>().AsNoTracking()
            .Where(asset => asset.OwnerKey == actor.OwnerUserId && ids.Contains(asset.Id))
            .Select(asset => asset.Id)
            .ToListAsync(cancellationToken);

        if (owned.Count != ids.Count)
            throw new ArgumentException("Website source references media that is unavailable to this website owner.");
    }

    private async Task ValidateCompositionMediaOwnershipAsync(
        WebsiteEditorTicket actor,
        WebsiteContentDocument document,
        CancellationToken cancellationToken)
    {
        var ids = WebsiteMediaReferenceCatalog.Collect(document);
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

    // CTA construction needs a phone, never the profile's large image payload.
    private static IQueryable<string?> ProjectProtectPhones(IQueryable<AgentProfile> profiles) =>
        profiles.Select(profile => profile.Phone);

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
            phone = await ProjectProtectPhones(_db.AgentProfiles.AsNoTracking()
                .Where(row => row.AgentUserId == ownerUserId && row.IsActive)
                .OrderByDescending(row => row.UpdatedUtc))
                .FirstOrDefaultAsync(cancellationToken);
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

    internal static bool TryFindSignalTarget(
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
            found.Type is not ("form" or "experience") &&
            !WebsiteSystemTemplateAuthority.IsRuntimeFormSystemKey(found.SystemKey))
            return false;

        if (normalizedFieldKey is not null &&
            found.Type == "experience")
        {
            var signalFieldKey = normalizedFieldKey;
            if (found.Experience is null ||
                found.Experience.Controls.Count(control =>
                    string.Equals(control.Key, signalFieldKey, StringComparison.OrdinalIgnoreCase)) != 1)
                return false;
        }
        else if (normalizedFieldKey is not null &&
                 (found.Type == "form" ||
                  WebsiteSystemTemplateAuthority.IsRuntimeFormSystemKey(found.SystemKey)) &&
                 !WebsiteSignalBindingPolicy.IsKnownProtectedFormField(found, normalizedFieldKey))
        {
            return false;
        }

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

    private async Task<string?> ReceptionistSetupUrlAsync(WebsiteEditorTicket actor, CancellationToken ct)
    {
        // Navigation only. The destination independently rechecks settings membership
        // and antiforgery; a website ticket never grants receptionist operation rights.
        if (!Uri.TryCreate(_configuration["AgentPortalBaseUrl"], UriKind.Absolute, out var portal) ||
            portal.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(portal.UserInfo)) return null;
        var owner = await ResolveAdvertisingOwnerAsync(actor, ct);
        if (owner is null) return null;
        string? path = null;
        if (owner.CommerceBusinessId is Guid businessId)
            path = $"/business/{businessId:D}/receptionist";
        else
        {
            var profileId = owner.AgentTrackingProfileId;
            if (owner == Shared.Analytics.MarketingOwnerScope.Founder)
            {
                var profiles = new Infrastructure.Analytics.AgentTrackingResolver(_db,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Analytics.AgentTrackingResolver>.Instance);
                var founder = await Infrastructure.Analytics.ProtectWebsiteOwnerResolver.ResolveLeadAsync(
                    profiles, _configuration["Founder:Upn"], null, null, true, ct);
                profileId = founder?.Profile.Id;
            }
            if (profileId is Guid id && id != Guid.Empty)
                path = $"/WebsiteAnalytics/receptionist?agentProfileId={id:D}";
        }
        return path is null ? null : new Uri(portal, path).AbsoluteUri;
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
        // SqlClient async materialization can replay large text packets; named draft
        // snapshots make this one editor-only row multi-megabyte. Keep this read
        // synchronous (EF's documented large-LOB workaround), not the writes or
        // other queries. The existing command timeout remains unchanged;
        // cancellation is observed before and after the blocking read.
        cancellationToken.ThrowIfCancellationRequested();
        var state = _db.Set<WebsiteContentState>()
            .SingleOrDefault(s => s.OwnerKey == actor.OwnerUserId && s.SiteKey == actor.SiteKey);
        cancellationToken.ThrowIfCancellationRequested();
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
        => (await LoadPublicationAsync(ownerUserId, siteKey, cancellationToken)).Document;

    private async Task<(WebsiteContentDocument? Document, Guid? VersionId, long? Revision)> LoadPublicationAsync(
        string ownerUserId, string siteKey, CancellationToken cancellationToken)
    {
        // Public requests need only the publication pointer, never the mutable
        // draft or named-draft snapshots (which can be many megabytes).
        var state = await _db.Set<WebsiteContentState>().AsNoTracking()
            .Where(s => s.OwnerKey == ownerUserId && s.SiteKey == siteKey)
            .Select(s => new { s.Id, s.PublishedVersionId })
            .SingleOrDefaultAsync(cancellationToken);
        if (state is not null)
        {
            if (!state.PublishedVersionId.HasValue) return (null, null, null);
            var version = WebsiteContentStore.ReadImmutablePublication(() => _db.Set<WebsiteContentVersion>().AsNoTracking()
                .Where(v => v.Id == state.PublishedVersionId && v.StateId == state.Id)
                .Select(v => new { v.Id, v.Revision, v.DocumentJson, v.SnapshotRetiredUtc })
                .Single(), cancellationToken);
            if (version.SnapshotRetiredUtc.HasValue) throw new InvalidOperationException("Active publication snapshot is unavailable.");
            return (Read(version.DocumentJson), version.Id, version.Revision);
        }
        var legacy = await _db.AgentFinanceToolStates.AsNoTracking().SingleOrDefaultAsync(s => s.AgentUserId == ownerUserId && s.ToolId == ToolId(siteKey), cancellationToken);
        return (legacy is null ? (siteKey == WebsiteEditorSiteKeys.Business ? null : new WebsiteContentDocument()) : Read(legacy.JsonState), null, null);
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
                managerUrl = (string?)null,
                paymentsUrl = (string?)null
            };

        var root = await ResolveCanonicalStoreRootAsync(siteKey, scope, cancellationToken);
        // Private management is served by the shared website API even before
        // the customer domain has completed its public storefront cutover.
        var managerBase = WebsiteContentApiBaseUrl();
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
                : managerBase + "/commerce/manage/products?ticket=" + escapedTicket,
            paymentsUrl = escapedTicket is null
                ? null
                : managerBase + "/commerce/manage/payments?ticket=" + escapedTicket
        };
    }

    private async Task<WebsiteCommerceScope?> PublishedStoreScopeAsync(
        string ownerKey,
        string siteKey,
        WebsiteContentDocument document,
        CancellationToken cancellationToken)
    {
        if (document.Store?.Enabled != true) return null;

        var businessId = await _db.Set<WebsiteContentState>().AsNoTracking()
            .Where(x => x.OwnerKey == ownerKey && x.SiteKey == siteKey)
            .Select(x => x.CommerceBusinessId)
            .SingleOrDefaultAsync(cancellationToken);
        if (businessId is not Guid commerceBusinessId || commerceBusinessId == Guid.Empty)
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
