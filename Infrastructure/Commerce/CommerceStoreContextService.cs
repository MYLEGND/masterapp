using System.Text.Json;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ParfaitApp.Services;

// Scope identity only; membership, capability and retained-store entitlement are checked separately.
public sealed record CommerceManagementScope(
    Guid CommerceBusinessId, string BusinessKey, Guid WebsiteStateId,
    string WebsiteSiteKey, string WebsiteOwnerKey, string DestinationOrigin);

public sealed record CommerceManagementActor(string ActorUserId, Guid? ClientProfileId, string? AgentSlug);

public sealed record CommerceStoreContext(
    Guid CommerceBusinessId,
    Guid? WebsiteContentVersionId,
    Guid? AgentTrackingProfileId,
    string WebsiteSiteKey,
    string BusinessKey,
    string StoreName,
    string NavigationLabel,
    string CartIcon,
    string Headline,
    string Subheadline,
    string StoreRootPath,
    string CartPath,
    string CheckoutPath,
    string SuccessPath,
    string CartStorageKey,
    bool IsParfait,
    string AccentColor,
    string? LogoUrl,
    string? GlobalCheckoutUrl,
    WebsiteDesignTheme Theme,
    string? WebsiteShellPrefix,
    string? WebsiteShellSuffix,
    string? StorePageShellPrefix = null,
    string? StorePageShellSuffix = null,
    string? StorePageBeforeCatalog = null,
    string? StorePageAfterCatalog = null,
    string? FaviconUrl = null);

/// <summary>
/// One public storefront scope resolver for Parfait and every website-linked commerce tenant.
/// Normal public storefronts resolve from the authenticated public hostname and therefore live
/// at /store on that hostname. The scoped /store/s/{businessKey} route is retained only for
/// Protect's shared agent host and legacy compatibility.
/// </summary>
public sealed class CommerceStoreContextService(
    MasterAppDbContext db,
    CommerceBusinessScopeResolver businesses,
    ParfaitBusinessScopeService parfaitScope,
    WebsiteDomainService domains,
    IConfiguration configuration)
{
    private const string ProtectHost = "protect.mylegnd.com";
    private const string ParfaitAzureHost = "masterapp-parfait.azurewebsites.net";

    public static async Task<string?> PublishedManagementEntryAsync(MasterAppDbContext db, IConfiguration? configuration,
        string site, string owner, CancellationToken ct = default)
    {
        var states = await db.Set<WebsiteContentState>().AsNoTracking().Where(x => x.SiteKey == site && x.OwnerKey == owner)
            .Select(x => new { x.Id, x.CommerceBusinessId, x.ProvisionedStoreBusinessId, x.PublishedVersionId }).Take(2).ToListAsync(ct);
        if (states.Count != 1) return null;
        var state = states[0];
        if (state.CommerceBusinessId is not Guid businessId || state.ProvisionedStoreBusinessId != businessId || state.PublishedVersionId is not Guid versionId) return null;
        var document = WebsiteContentStore.ReadImmutablePublication(() => db.Set<WebsiteContentVersion>().AsNoTracking()
            .Where(x => x.Id == versionId && x.StateId == state.Id && x.SnapshotRetiredUtc == null)
            .Select(x => x.DocumentJson).SingleOrDefault(), ct);
        if (document is null || !Deserialize(document).Store.Enabled) return null;
        var business = await db.CommerceBusinesses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == businessId && x.IsActive && x.Status == "Active", ct);
        if (business is null) return null;
        var root = await WebsiteStoreRootAsync(db, configuration, site, businessId, business.Key, ct);
        return ManagementEntry(root, site == WebsiteEditorSiteKeys.Protect ? business.Key : null);
    }

    public static string? ManagementEntry(string? root, string? businessKey = null)
    {
        if (!Uri.TryCreate(root, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        var target = uri.GetLeftPart(UriPartial.Authority) + "/internal/login";
        return string.IsNullOrEmpty(businessKey) ? target : Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(target, "businessKey", businessKey);
    }

    public static async Task<string?> WebsiteStoreRootAsync(MasterAppDbContext db, IConfiguration? configuration,
        string site, Guid businessId, string businessKey, CancellationToken ct = default)
    {
        if (site == WebsiteEditorSiteKeys.Legend) return (configuration?["Commerce:LegendPublicBaseUrl"] ?? "https://mylegnd.com").TrimEnd('/') + "/store";
        if (site == WebsiteEditorSiteKeys.Protect) return (configuration?["Commerce:ProtectPublicBaseUrl"] ?? "https://protect.mylegnd.com").TrimEnd('/') + "/store/s/" + Uri.EscapeDataString(businessKey);
        if (site != WebsiteEditorSiteKeys.Business) return null;
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var hostname = await db.Set<WebsiteDomainBinding>().AsNoTracking().Where(x => x.CommerceBusinessId == businessId &&
            x.Status == "active" && x.CertificateStatus == "active" && x.LastCheckedUtc >= cutoff)
            .OrderBy(x => x.CreatedUtc).Select(x => x.Hostname).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(hostname) ? null : "https://" + hostname.Trim().TrimEnd('.') + "/store";
    }

    public async Task<CommerceManagementScope?> ResolveManagementScopeAsync(
        HttpContext context, string? businessKey, CancellationToken ct = default)
    {
        // Scheme is established by the host's trusted forwarding middleware, never a
        // caller-supplied forwarding header interpreted here.
        if (!context.Request.IsHttps || context.Request.Host.Port is not (null or 443)) return null;
        var host = WebsiteRequestHostResolver.Resolve(context, configuration, allowLegendCommerceHost: true);
        var key = NormalizeKey(businessKey);
        CommerceBusiness? business;
        IQueryable<WebsiteContentState> states = db.Set<WebsiteContentState>().AsNoTracking();
        if (string.Equals(host, ProtectHost, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(key)) return null;
            business = await businesses.ResolveActiveByKeyAsync(key, ct);
            if (business is null) return null;
            states = states.Where(x => x.CommerceBusinessId == business.Id &&
                x.SiteKey == WebsiteEditorSiteKeys.Protect);
        }
        else if (PublicWebsiteRuntimeScopeResolver.IsLegendHost(host))
        {
            if (!string.IsNullOrEmpty(key)) return null;
            states = states.Where(x => x.SiteKey == WebsiteEditorSiteKeys.Legend &&
                x.OwnerKey == WebsiteEditorSiteKeys.GlobalOwnerKey);
            var bindings = await states.Select(x => x.CommerceBusinessId).Take(2).ToListAsync(ct);
            if (bindings.Count != 1 || bindings[0] is not Guid id || id == Guid.Empty) return null;
            business = await businesses.ResolveActiveByIdAsync(id, ct);
        }
        else
        {
            // No default tenant, Parfait provisioning or caller-supplied business selection.
            if (!string.IsNullOrEmpty(key)) return null;
            var id = await domains.ResolveAsync(host, ct);
            if (!id.HasValue) return null;
            business = await businesses.ResolveActiveByIdAsync(id.Value, ct);
            if (business is null) return null;
            var owner = WebsiteEditorSiteKeys.BusinessOwnerKey(business.Id);
            states = states.Where(x => x.CommerceBusinessId == business.Id &&
                x.SiteKey == WebsiteEditorSiteKeys.Business && x.OwnerKey == owner);
        }
        if (business is null) return null;
        // Deliberately avoid draft/published document loading. A retained binding survives
        // Remove Store; it does not itself grant access or prove a store was ever published.
        var matches = await states.Select(x => new { x.Id, x.CommerceBusinessId, x.SiteKey, x.OwnerKey })
            .Take(2).ToListAsync(ct);
        if (matches.Count != 1 || matches[0].CommerceBusinessId != business.Id ||
            string.IsNullOrWhiteSpace(matches[0].OwnerKey)) return null;
        var state = matches[0];
        return new(business.Id, business.Key, state.Id, state.SiteKey, state.OwnerKey, "https://" + host);
    }

    public async Task<CommerceManagementScope?> ResolveManagementScopeAsync(
        StoreManagementLoginRequest request, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(request.DestinationOrigin, UriKind.Absolute, out var origin) ||
            origin.Scheme != "https" || !origin.IsDefaultPort || origin.AbsolutePath != "/" ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            request.DestinationOrigin != origin.GetLeftPart(UriPartial.Authority)) return null;
        var key = await db.CommerceBusinesses.AsNoTracking().Where(b => b.Id == request.CommerceBusinessId)
            .Select(b => b.Key).SingleOrDefaultAsync(ct);
        if (key is null) return null;
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(origin.IdnHost);
        var scope = await ResolveManagementScopeAsync(context, origin.IdnHost == ProtectHost ? key : null, ct);
        return scope is not null && scope.CommerceBusinessId == request.CommerceBusinessId &&
            scope.WebsiteSiteKey == request.SiteKey && scope.WebsiteOwnerKey == request.OwnerKey &&
            scope.DestinationOrigin == request.DestinationOrigin ? scope : null;
    }

    public async Task<CommerceManagementActor?> ResolveManagementActorAsync(
        CommerceManagementScope scope, string actorUserId, string capability,
        Guid? selectedProfileId = null, CancellationToken ct = default)
    {
        var current = await ResolveManagementScopeAsync(new StoreManagementLoginRequest(
            scope.CommerceBusinessId, scope.WebsiteSiteKey, scope.WebsiteOwnerKey, scope.DestinationOrigin,
            "/internal", new string('0', 64), DateTime.UtcNow.AddMinutes(1)), ct);
        if (current != scope) return null;
        if (!await HasProvisionedStoreAsync(scope, ct) ||
            !await db.CommerceBusinesses.AnyAsync(b => b.Id == scope.CommerceBusinessId && b.IsActive && b.Status.ToLower() == "active", ct))
            return null;
        var actor = Shared.Auth.IdentityKey.Normalize(actorUserId);
        if (string.IsNullOrWhiteSpace(actor)) return null;
        var member = await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, scope.CommerceBusinessId,
            actor, capability, selectedProfileId, ct);
        if (member is not null) return new(actor, member.ClientProfileId, null);
        if (selectedProfileId.HasValue) return null;
        // These are the existing website-owner scopes, never platform-wide business grants.
        var founder = configuration["Founder:Oid"] ?? configuration["FOUNDER_OID"] ?? Environment.GetEnvironmentVariable("FOUNDER_OID");
        string? slug = null;
        if (scope.WebsiteSiteKey == WebsiteEditorSiteKeys.Legend)
        {
            if (scope.WebsiteOwnerKey != WebsiteEditorSiteKeys.GlobalOwnerKey ||
                !Shared.Auth.FounderAuthority.IsConfiguredFounderIdentity(actor, founder)) return null;
        }
        else if (scope.WebsiteSiteKey == WebsiteEditorSiteKeys.Protect)
        {
            if (!string.Equals(scope.WebsiteOwnerKey, actor, StringComparison.OrdinalIgnoreCase)) return null;
            var tracking = await db.AgentTrackingProfiles.AsNoTracking().Where(p =>
                p.AgentUserId.ToLower() == actor && p.Status.ToLower() == "active").Take(2).ToListAsync(ct);
            if (tracking.Count != 1) return null;
            slug = tracking[0].Slug;
        }
        else return null;
        if (!BusinessWorkspaceAccess.IsKnownCapability(capability)) return null;
        var agents = await db.AgentProfiles.AsNoTracking().Where(p => p.AgentUserId.ToLower() == actor && p.IsActive)
            .Take(2).ToListAsync(ct);
        if (agents.Count != 1 || !(await Infrastructure.Identity.AccountLifecycleService.ReadAsync(db,
            new Domain.Accounts.AccountLifecycleSubject(actor, Domain.Messaging.MessagingParticipantTypes.Agent, agents[0].Id), ct)).AllowsFullAccess)
            return null;
        return new(actor, null, slug);
    }

    public async Task<CommerceManagementActor?> ResolveWebsiteOwnerActorAsync(
        Guid businessId, string actorUserId, string capability, CancellationToken ct = default)
    {
        var states = await db.Set<WebsiteContentState>().AsNoTracking()
            .Where(x => x.CommerceBusinessId == businessId && x.ProvisionedStoreBusinessId == businessId)
            .Select(x => new { x.SiteKey, x.OwnerKey }).Take(2).ToListAsync(ct);
        if (states.Count != 1) return null;
        var state = states[0];
        var business = await businesses.ResolveActiveByIdAsync(businessId, ct);
        if (business is null) return null;
        var root = await WebsiteStoreRootAsync(db, configuration, state.SiteKey, businessId, business.Key, ct);
        if (!Uri.TryCreate(root, UriKind.Absolute, out var uri)) return null;
        var scope = await ResolveManagementScopeAsync(new StoreManagementLoginRequest(
            businessId, state.SiteKey, state.OwnerKey, uri.GetLeftPart(UriPartial.Authority),
            "/internal", new string('0', 64), DateTime.UtcNow.AddMinutes(1)), ct);
        return scope is null ? null : await ResolveManagementActorAsync(scope, actorUserId, capability, null, ct);
    }

    public Task<bool> HasProvisionedStoreAsync(CommerceManagementScope scope, CancellationToken ct = default) =>
        db.Set<WebsiteContentState>().AsNoTracking().AnyAsync(state =>
            state.Id == scope.WebsiteStateId && state.SiteKey == scope.WebsiteSiteKey &&
            state.OwnerKey == scope.WebsiteOwnerKey && state.CommerceBusinessId == scope.CommerceBusinessId &&
            state.ProvisionedStoreBusinessId == scope.CommerceBusinessId, ct);

    public async Task<CommerceStoreContext?> ResolvePublicAsync(string? businessKey, CancellationToken ct = default)
    {
        var normalized = NormalizeKey(businessKey);
        CommerceBusiness? business;
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized == ParfaitBusinessScopeService.ParfaitBusinessKey)
        {
            business = await parfaitScope.GetParfaitAsync(ct);
            return await BuildAsync(business, publishedOnly: false, ct, useScopedPath: false);
        }

        business = await businesses.ResolveActiveByKeyAsync(normalized, ct);
        if (business is null) return null;
        return await BuildAsync(business, publishedOnly: true, ct, useScopedPath: true);
    }

    public async Task<CommerceStoreContext?> ResolvePublicAsync(
        HttpContext context,
        string? businessKey,
        CancellationToken ct = default)
    {
        var normalized = NormalizeKey(businessKey);
        var host = WebsiteRequestHostResolver.Resolve(context, configuration, allowLegendCommerceHost: true);
        if (!string.IsNullOrWhiteSpace(normalized))
            return string.Equals(host, ProtectHost, StringComparison.OrdinalIgnoreCase)
                ? await ResolvePublicAsync(normalized, ct) : null;

        if (IsParfaitHost(host))
        {
            // A website-host preview is GET-only and must never implicitly
            // seed a new Parfait identity into the production SQL database.
            var parfait = (string.Equals(configuration["Commerce:SharedHostPreview:Enabled"],
                "true", StringComparison.OrdinalIgnoreCase) ||
                Infrastructure.WebsiteRuntime.CommerceSharedHostCutoverGate.IsConfigured(configuration))
                ? await parfaitScope.FindExistingParfaitAsync(ct)
                : await parfaitScope.GetParfaitAsync(ct);
            return parfait is null
                ? null
                : await BuildAsync(parfait, publishedOnly: false, ct, useScopedPath: false);
        }

        if (PublicWebsiteRuntimeScopeResolver.IsLegendHost(host))
        {
            var state = await db.Set<WebsiteContentState>().AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.OwnerKey == WebsiteEditorSiteKeys.GlobalOwnerKey &&
                         x.SiteKey == WebsiteEditorSiteKeys.Legend,
                    ct);
            if (state?.CommerceBusinessId is not Guid legendBusinessId || legendBusinessId == Guid.Empty)
                return null;

            var business = await businesses.ResolveActiveByIdAsync(legendBusinessId, ct);
            if (business is null) return null;

            return await BuildAsync(
                business,
                publishedOnly: true,
                ct,
                explicitSiteKey: WebsiteEditorSiteKeys.Legend,
                explicitState: state,
                useScopedPath: false);
        }

        // Protect is the one shared-host exception. It must retain an explicit scoped
        // business key so one agent cannot select another agent's store by hostname alone.
        if (string.Equals(host, ProtectHost, StringComparison.OrdinalIgnoreCase))
            return null;

        var businessId = await domains.ResolveAsync(host, ct);
        if (!businessId.HasValue || businessId == Guid.Empty)
            return null;

        var scopedBusiness = await businesses.ResolveActiveByIdAsync(businessId.Value, ct);
        if (scopedBusiness is null) return null;

        return await BuildAsync(scopedBusiness, publishedOnly: true, ct, useScopedPath: false);
    }

    public async Task<CommerceStoreContext?> ResolveManagementAsync(CommerceManagementScope scope,
        string actorUserId, string capability, Guid? profileId, CancellationToken ct = default)
    {
        if (await ResolveManagementActorAsync(scope, actorUserId, capability, profileId, ct) is null) return null;
        var business = await businesses.ResolveActiveByIdAsync(scope.CommerceBusinessId, ct);
        var state = await db.Set<WebsiteContentState>().SingleOrDefaultAsync(x => x.Id == scope.WebsiteStateId &&
            x.CommerceBusinessId == scope.CommerceBusinessId && x.ProvisionedStoreBusinessId == scope.CommerceBusinessId, ct);
        if (business is null || state is null) return null;
        return await BuildAsync(business, publishedOnly: false, ct, explicitDocument: Deserialize(state.DraftJson),
            explicitSiteKey: scope.WebsiteSiteKey, explicitState: state,
            useScopedPath: new Uri(scope.DestinationOrigin).Host == ProtectHost);
    }

    public async Task<CommerceStoreContext?> ResolveForWebsiteTicketAsync(
        string token,
        WebsiteEditorTicketProtector tickets,
        IConfiguration configuration,
        CancellationToken ct = default)
    {
        var actor = await WebsiteTicketAuthorization.ResolveAsync(db, tickets, configuration, token, ct);
        if (actor is null) return null;

        var state = await db.Set<WebsiteContentState>()
            .SingleOrDefaultAsync(x => x.OwnerKey == actor.OwnerUserId && x.SiteKey == actor.SiteKey, ct);
        if (state?.CommerceBusinessId is not Guid businessId || businessId == Guid.Empty) return null;

        var business = await businesses.ResolveActiveByIdAsync(businessId, ct);
        if (business is null) return null;

        var draft = Deserialize(state.DraftJson);
        if (draft.Store?.Enabled != true) return null;

        return await BuildAsync(
            business,
            publishedOnly: false,
            ct,
            explicitDocument: draft,
            explicitSiteKey: actor.SiteKey,
            explicitState: state,
            useScopedPath: true);
    }

    public async Task<CommerceStoreContext?> ResolveAnalyticsAsync(HttpContext context, string? path, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(context.Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin) ||
            origin.Scheme != "https" || !origin.IsDefaultPort || !string.IsNullOrEmpty(origin.UserInfo) || origin.AbsolutePath != "/" ||
            string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal)) return null;
        if (!IsParfaitHost(origin.IdnHost) && (!path.StartsWith("/store", StringComparison.Ordinal) ||
            (path.Length > 6 && path[6] != '/'))) return null;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var key = segments.Length > 1 && segments[1] == "s" ? segments.ElementAtOrDefault(2) : null;
        if (segments.Length > 1 && segments[1] == "s" && string.IsNullOrWhiteSpace(key)) return null;
        if (!string.IsNullOrWhiteSpace(key) && !string.Equals(origin.IdnHost, ProtectHost, StringComparison.OrdinalIgnoreCase)) return null;
        var scopeContext = new DefaultHttpContext();
        scopeContext.Request.Scheme = origin.Scheme;
        scopeContext.Request.Host = new HostString(origin.IdnHost);
        return await ResolvePublicAsync(scopeContext, key, ct);
    }

    public bool IsCentralCommerceHost(HttpContext context)
    {
        var host = WebsiteRequestHostResolver.Resolve(context, configuration, allowLegendCommerceHost: true);
        return IsParfaitHost(host);
    }

    public string ResolveEffectivePublicHost(HttpContext context) =>
        WebsiteRequestHostResolver.Resolve(context, configuration, allowLegendCommerceHost: true);

    public async Task<string?> ResolveCanonicalPublicRootAsync(
        Guid commerceBusinessId,
        CancellationToken ct = default)
    {
        if (commerceBusinessId == Guid.Empty) return null;
        var business = await businesses.ResolveActiveByIdAsync(commerceBusinessId, ct);
        if (business is null) return null;
        var store = await BuildAsync(business, publishedOnly: !IsParfaitKey(business.Key), ct);
        return store is null ? null : await ResolveCanonicalPublicRootAsync(store, ct);
    }

    public async Task<string?> ResolveCanonicalPublicRootAsync(
        CommerceStoreContext store,
        CancellationToken ct = default)
    {
        if (store.IsParfait)
            return PublicBase("Commerce:PublicBaseUrl", "https://shopparfait.com") + "/store";

        if (string.Equals(store.WebsiteSiteKey, WebsiteEditorSiteKeys.Legend, StringComparison.OrdinalIgnoreCase))
            return PublicBase("Commerce:LegendPublicBaseUrl", "https://mylegnd.com") + "/store";

        if (string.Equals(store.WebsiteSiteKey, WebsiteEditorSiteKeys.Protect, StringComparison.OrdinalIgnoreCase))
            return PublicBase("Commerce:ProtectPublicBaseUrl", "https://protect.mylegnd.com") +
                   "/store/s/" + Uri.EscapeDataString(store.BusinessKey);

        if (!string.Equals(store.WebsiteSiteKey, WebsiteEditorSiteKeys.Business, StringComparison.OrdinalIgnoreCase))
            return null;

        var cutoff = DateTime.UtcNow.AddHours(-24);
        var hostname = await db.Set<WebsiteDomainBinding>().AsNoTracking()
            .Where(x =>
                x.CommerceBusinessId == store.CommerceBusinessId &&
                x.Status == "active" &&
                x.CertificateStatus == "active" &&
                x.LastCheckedUtc >= cutoff)
            .OrderBy(x => x.CreatedUtc)
            .Select(x => x.Hostname)
            .FirstOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(hostname) ? null : "https://" + hostname + "/store";
    }

    private async Task<CommerceStoreContext?> BuildAsync(
        CommerceBusiness business,
        bool publishedOnly,
        CancellationToken ct,
        WebsiteContentDocument? explicitDocument = null,
        string? explicitSiteKey = null,
        WebsiteContentState? explicitState = null,
        bool useScopedPath = false)
    {
        var settings = await db.CommerceBusinessStorefrontSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CommerceBusinessId == business.Id, ct);

        var linkedState = explicitState ?? await db.Set<WebsiteContentState>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.CommerceBusinessId == business.Id, ct);

        WebsiteContentDocument? websiteDocument = explicitDocument;
        Guid? publishedVersionId = null;
        string? websiteShellPrefix = null;
        string? websiteShellSuffix = null;
        string? storeShellPrefix = null, storeShellSuffix = null, storeBefore = null, storeAfter = null;
        var websiteSiteKey = !string.IsNullOrWhiteSpace(explicitSiteKey)
            ? explicitSiteKey.Trim().ToLowerInvariant()
            : linkedState?.SiteKey?.Trim().ToLowerInvariant()
              ?? (IsParfaitKey(business.Key) ? "ParfaitApp" : WebsiteEditorSiteKeys.Business);

        if (websiteDocument is null && linkedState is not null)
        {
            if (publishedOnly)
            {
                if (!linkedState.PublishedVersionId.HasValue) return null;
                var version = WebsiteContentStore.ReadImmutablePublication(() => db.Set<WebsiteContentVersion>().AsNoTracking()
                    .Where(x => x.Id == linkedState.PublishedVersionId.Value && x.StateId == linkedState.Id && x.SnapshotRetiredUtc == null)
                    .Select(x => new { x.Id, x.DocumentJson, x.CompiledPagesJson }).SingleOrDefault(), ct);
                if (version is null) return null;
                publishedVersionId = version.Id;
                websiteDocument = Deserialize(version.DocumentJson);
                if (websiteDocument.Store?.Enabled != true) return null;
                TryExtractPublishedWebsiteShell(version.CompiledPagesJson, out websiteShellPrefix, out websiteShellSuffix);
                if (websiteDocument.Pages.ContainsKey("/store"))
                {
                    WebsiteSystemTemplateAuthority.ValidateStorePage(websiteDocument);
                    TryExtractPublishedWebsiteShell(version.CompiledPagesJson, out storeShellPrefix, out storeShellSuffix,
                        "/store", out storeBefore, out storeAfter);
                    if (storeBefore is null || storeAfter is null) return null;
                }
            }
            else
            {
                websiteDocument = Deserialize(linkedState.DraftJson);
            }
        }

        var isParfait = IsParfaitKey(business.Key);
        if (publishedOnly && !isParfait && (websiteDocument?.Store?.Enabled != true ||
            websiteDocument.Pages.TryGetValue("/store", out var storePage) &&
            (storePage.Navigation.IsDeleted || !storePage.Navigation.IsPublished))) return null;

        Guid? agentTrackingProfileId = null;
        if (string.Equals(websiteSiteKey, WebsiteEditorSiteKeys.Protect, StringComparison.OrdinalIgnoreCase) &&
            linkedState is not null &&
            !string.IsNullOrWhiteSpace(linkedState.OwnerKey))
        {
            var owner = linkedState.OwnerKey.Trim().ToLowerInvariant();
            agentTrackingProfileId = await db.AgentTrackingProfiles.AsNoTracking()
                .Where(profile =>
                    profile.AgentUserId.ToLower() == owner &&
                    profile.Status.ToLower() == "active")
                .Select(profile => (Guid?)profile.Id)
                .SingleOrDefaultAsync(ct);
            if (!agentTrackingProfileId.HasValue)
                return null;
        }

        var label = websiteDocument?.Store?.NavigationLabel?.Trim();
        if (string.IsNullOrWhiteSpace(label)) label = isParfait ? "Shop" : "Store";
        var cartIcon = websiteDocument?.Store?.CartIcon?.Trim().ToLowerInvariant();
        if (cartIcon is not ("cart" or "bag" or "basket")) cartIcon = "cart";

        var root = !isParfait && useScopedPath
            ? "/store/s/" + Uri.EscapeDataString(business.Key)
            : "/store";
        var theme = websiteDocument?.Theme ?? new WebsiteDesignTheme();

        return new CommerceStoreContext(
            business.Id,
            publishedVersionId,
            agentTrackingProfileId,
            websiteSiteKey,
            business.Key,
            business.DisplayName,
            label,
            cartIcon,
            string.IsNullOrWhiteSpace(settings?.BrandHeadline) ? business.DisplayName : settings!.BrandHeadline,
            string.IsNullOrWhiteSpace(settings?.BrandSubheadline)
                ? business.DisplayName + " storefront."
                : settings!.BrandSubheadline,
            root,
            root + "/cart",
            root + "/checkout",
            root + "/success",
            isParfait ? "parfaitCart" : "legendCommerceCart:" + business.Key.ToLowerInvariant(),
            isParfait,
            settings?.AccentColor?.Trim() ?? "",
            string.IsNullOrWhiteSpace(settings?.LogoUrl) ? null : settings!.LogoUrl.Trim(),
            string.IsNullOrWhiteSpace(settings?.GlobalStoreCheckoutUrl) ? null : settings!.GlobalStoreCheckoutUrl.Trim(),
            theme,
            isParfait ? null : websiteShellPrefix,
            isParfait ? null : websiteShellSuffix,
            storeShellPrefix, storeShellSuffix, storeBefore, storeAfter,
            websiteSiteKey == WebsiteEditorSiteKeys.Legend
                ? WebsiteFaviconParity.PublicUrl(configuration, websiteSiteKey)
                : websiteSiteKey == WebsiteEditorSiteKeys.Business ? "/favicon.jpg"
                : WebsiteFaviconParity.ResolvePublished(websiteDocument?.FaviconImageDataUrl)?.RedirectUrl
                  ?? websiteDocument?.FaviconImageDataUrl ?? WebsiteFaviconParity.FallbackUrl(configuration));
    }


    private static void TryExtractPublishedWebsiteShell(
        string? compiledPagesJson,
        out string? prefix,
        out string? suffix) => TryExtractPublishedWebsiteShell(compiledPagesJson, out prefix, out suffix, "/", out _, out _);

    private static void TryExtractPublishedWebsiteShell(string? compiledPagesJson, out string? prefix,
        out string? suffix, string route, out string? before, out string? after)
    {
        before = null; after = null;
        prefix = null;
        suffix = null;
        if (string.IsNullOrWhiteSpace(compiledPagesJson)) return;

        try
        {
            using var compiled = JsonDocument.Parse(compiledPagesJson);
            if (!compiled.RootElement.TryGetProperty("pages", out var pages) ||
                !pages.TryGetProperty(route, out var home) ||
                !home.TryGetProperty("html", out var htmlValue))
                return;

            var html = htmlValue.GetString();
            if (string.IsNullOrWhiteSpace(html)) return;

            var mainStart = html.IndexOf("<main", StringComparison.OrdinalIgnoreCase);
            if (mainStart < 0) return;
            var mainOpenEnd = html.IndexOf('>', mainStart);
            if (mainOpenEnd < 0) return;
            var mainClose = html.IndexOf("</main>", mainOpenEnd + 1, StringComparison.OrdinalIgnoreCase);
            if (mainClose < 0) return;

            if (route == "/store")
            {
                const string begin = "<!--legend:store:catalog:start-->";
                const string end = "<!--legend:store:catalog:end-->";
                var beginIndex = html.IndexOf(begin, mainOpenEnd, StringComparison.Ordinal);
                var endIndex = html.IndexOf(end, mainOpenEnd, StringComparison.Ordinal);
                if (beginIndex < 0 || endIndex != beginIndex + begin.Length || endIndex >= mainClose ||
                    html.IndexOf(begin, beginIndex + begin.Length, StringComparison.Ordinal) >= 0) return;
                before = html[(mainOpenEnd + 1)..beginIndex];
                after = html[(endIndex + end.Length)..mainClose];
            }
            prefix = html[..(mainOpenEnd + 1)];
            suffix = html[(mainClose + "</main>".Length)..];

            var headClose = prefix.LastIndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            if (headClose >= 0)
            {
                const string storeStyle = "<link rel=\"stylesheet\" href=\"/store-assets/css/storefront.css\" />";
                prefix = prefix.Insert(headClose, storeStyle);
            }
        }
        catch (JsonException)
        {
            prefix = null;
            suffix = null;
        }
    }

    private string PublicBase(string key, string fallback)
    {
        var configured = configuration[key]?.Trim();
        if (!string.IsNullOrWhiteSpace(configured) &&
            Uri.TryCreate(configured, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrWhiteSpace(uri.UserInfo))
            return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return fallback;
    }

    private bool IsParfaitHost(string host)
    {
        if (string.Equals(host, ParfaitAzureHost, StringComparison.OrdinalIgnoreCase))
            return true;

        var configured = PublicBase("Commerce:PublicBaseUrl", "https://shopparfait.com");
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri) &&
               (string.Equals(host, uri.IdnHost, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "www." + uri.IdnHost, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeKey(string? value) => (value ?? "").Trim().ToLowerInvariant();

    private static bool IsParfaitKey(string? key) =>
        string.Equals(key, ParfaitBusinessScopeService.ParfaitBusinessKey, StringComparison.OrdinalIgnoreCase);

    private static WebsiteContentDocument Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new WebsiteContentDocument();
        try
        {
            return WebsiteContentSanitizer.ReadPersisted(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return new WebsiteContentDocument();
        }
    }
}
