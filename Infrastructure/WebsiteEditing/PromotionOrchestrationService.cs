using System.Text.Json;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shared.Analytics;

namespace Infrastructure.WebsiteEditing;

public interface IPromotionOrchestrationService
{
    Task<IReadOnlyList<PromotionSourceOption>> SourcesAsync(
        MarketingOwnerScope owner,
        CancellationToken ct = default);

    Task<PromotionDraft> DraftAsync(
        MarketingOwnerScope owner,
        PromotionProposalRequest request,
        CancellationToken ct = default);

    Task<AdvertisingActionProposalSnapshot> ProposeAsync(
        MarketingOwnerScope owner,
        PromotionProposalRequest request,
        string proposedByUserId,
        CancellationToken ct = default);
}

public sealed class PromotionOrchestrationService(
    MasterAppDbContext db,
    IConfiguration configuration,
    IAdvertisingActionAuthorizationService authorizations,
    IOpenAiAdsAccountConnectionAuthority connections,
    IOpenAiAdsExecutionService ads,
    IBusinessPublicUrlResolver businessPublicUrls,
    IOpenAiProductFeedService productFeeds) : IPromotionOrchestrationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PromotionSourceOption>> SourcesAsync(
        MarketingOwnerScope owner,
        CancellationToken ct = default)
    {
        var published = await PublishedStateAsync(owner, ct);
        var document = ReadDocument(published.Version.DocumentJson);
        var options = document.Pages
            .Where(x => !x.Value.Navigation.IsDeleted)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new PromotionSourceOption(
                PromotionSourceKinds.WebsitePage,
                x.Key,
                Clean(x.Value.Title, 300) ?? (x.Key == "/" ? "Home" : x.Key.Trim('/')),
                PagePath: x.Key,
                Detail: Clean(x.Value.Description, 500), SiteKey: published.State.SiteKey))
            .ToList();

        if (owner == MarketingOwnerScope.Founder)
        {
            var protect = await PublishedStateAsync(owner, ct, WebsiteEditorSiteKeys.Protect);
            var protectDocument = ReadDocument(protect.Version.DocumentJson);
            options.AddRange(protectDocument.Pages.Where(x => !x.Value.Navigation.IsDeleted)
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new PromotionSourceOption(PromotionSourceKinds.WebsitePage,
                    "protect:" + x.Key, Clean(x.Value.Title, 300) ?? x.Key, x.Key,
                    Clean(x.Value.Description, 500), WebsiteEditorSiteKeys.Protect)));
        }
        if (owner.CommerceBusinessId is not Guid businessId)
            return options;

        var facts = await WebsiteBusinessFacts.LoadAsync(db, businessId, ct);
        foreach (var service in (facts.Services ?? string.Empty)
                     .Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(100))
        {
            options.Add(new PromotionSourceOption(
                PromotionSourceKinds.Service,
                service,
                service,
                PagePath: "/",
                Detail: "Business service"));
        }

        var products = await db.CommerceProducts.AsNoTracking()
            .Where(x => x.CommerceBusinessId == businessId && x.IsActive)
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.Name)
            .Take(500)
            .Select(x => new { x.Id, x.Name, x.Slug, x.PriceLabel })
            .ToListAsync(ct);
        options.AddRange(products.Select(x => new PromotionSourceOption(
            PromotionSourceKinds.Product,
            x.Id.ToString("D"),
            x.Name,
            PagePath: "/store/product/" + x.Slug,
            Detail: x.PriceLabel)));

        return options;
    }

    public async Task<PromotionDraft> DraftAsync(
        MarketingOwnerScope owner,
        PromotionProposalRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        if (request.DailyBudgetMicros < 1_000_000)
            throw new ArgumentException("Promotion budget must be at least one unit of the account currency per day.", nameof(request));

        var status = OpenAiAdsEntityStatuses.NormalizeCreate(request.Status);
        var biddingType = OpenAiAdsBiddingTypes.Normalize(request.BiddingType);
        var connection = await connections.GetAsync(owner, ct);
        if (!connection.Connected || !connection.HasManagementCredential)
            throw new InvalidOperationException("Connect the scoped ChatGPT Ads account before creating a promotion.");

        if (biddingType == OpenAiAdsBiddingTypes.Conversions)
        {
            if (string.IsNullOrWhiteSpace(request.ConversionEventSettingId))
                throw new ArgumentException("Select a standard conversion event setting for conversion optimization.", nameof(request));
            await ValidateConversionSettingAsync(owner, request.ConversionEventSettingId, ct);
        }
        else if (!string.IsNullOrWhiteSpace(request.ConversionEventSettingId))
        {
            throw new ArgumentException("A conversion event setting applies only to a conversions objective.", nameof(request));
        }

        var source = await ResolveSourceAsync(owner, request, ct);
        var hints = BuildContextHints(source, request.ContextHints);
        var alternatives = BuildAlternatives(source, hints);
        var selected = string.IsNullOrWhiteSpace(request.SelectedCreativeKey)
            ? alternatives[0]
            : alternatives.SingleOrDefault(x => string.Equals(x.Key, request.SelectedCreativeKey, StringComparison.Ordinal))
              ?? throw new ArgumentException("Selected creative alternative does not exist.", nameof(request));

        string? providerFeedId = null;
        if (source.SourceKind == PromotionSourceKinds.Product)
        {
            var feed = await productFeeds.GetAsync(owner, ct);
            var productRow = feed.Products.SingleOrDefault(x => x.CanonicalProductId.ToString("D") == source.SourceId);
            if (productRow is null || productRow.Status != OpenAiProductFeedStatuses.Published || string.IsNullOrWhiteSpace(productRow.ProviderFeedId))
                throw new InvalidOperationException("Publish this canonical product to the scoped ChatGPT Ads product feed before creating a product campaign.");
            providerFeedId = productRow.ProviderFeedId;
        }

        var plan = BuildPlan(source, selected, request, status, biddingType, hints, providerFeedId);
        return new(
            source,
            Objective: string.IsNullOrWhiteSpace(request.Goal) ? "Promote " + source.DisplayName : request.Goal.Trim(),
            biddingType,
            request.DailyBudgetMicros,
            request.ConversionEventSettingId,
            alternatives,
            selected.Key,
            plan);
    }

    public async Task<AdvertisingActionProposalSnapshot> ProposeAsync(
        MarketingOwnerScope owner,
        PromotionProposalRequest request,
        string proposedByUserId,
        CancellationToken ct = default)
    {
        var draft = await DraftAsync(owner, request, ct);
        var sourceJson = JsonSerializer.SerializeToElement(new
        {
            draft.Source,
            draft.Objective,
            draft.BiddingType,
            draft.DailyBudgetMicros,
            draft.ConversionEventSettingId,
            creativeAlternatives = draft.Alternatives,
            selectedCreativeKey = draft.SelectedAlternativeKey
        }, JsonOptions);

        return await authorizations.ProposeAsync(
            owner,
            proposalKind: "promote_this",
            draft.Plan,
            proposedByUserId,
            sourceJson,
            ct);
    }

    private async Task ValidateConversionSettingAsync(
        MarketingOwnerScope owner,
        string settingId,
        CancellationToken ct)
    {
        var settings = await ads.ListConversionEventSettingsAsync(owner, ct);
        if (!settings.Payload.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("ChatGPT Ads conversion settings are unavailable.");

        var row = data.EnumerateArray().FirstOrDefault(item =>
            item.TryGetProperty("id", out var id) &&
            id.ValueKind == JsonValueKind.String &&
            string.Equals(id.GetString(), settingId, StringComparison.Ordinal));
        if (row.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The selected conversion setting does not belong to this scoped ChatGPT Ads account.");

        var eventType = row.TryGetProperty("event_type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;
        var status = row.TryGetProperty("status", out var state) && state.ValueKind == JsonValueKind.String
            ? state.GetString()
            : null;
        if (string.Equals(eventType, "custom", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Custom ChatGPT Ads events are measurement-only and cannot be selected for conversion optimization.");
        if (string.Equals(status, "archived", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected ChatGPT Ads conversion setting is archived.");
    }

    private async Task<PromotionSourceSnapshot> ResolveSourceAsync(
        MarketingOwnerScope owner,
        PromotionProposalRequest request,
        CancellationToken ct)
    {
        var kind = (request.SourceKind ?? string.Empty).Trim().ToLowerInvariant();
        return kind switch
        {
            PromotionSourceKinds.Product => await ResolveProductAsync(owner, request, ct),
            PromotionSourceKinds.Service => await ResolveServiceAsync(owner, request, ct),
            PromotionSourceKinds.WebsitePage => await ResolveWebsitePageAsync(owner, request, ct),
            _ => throw new ArgumentException("Promotion source must be website_page, service, or product.", nameof(request))
        };
    }

    private async Task<PromotionSourceSnapshot> ResolveProductAsync(
        MarketingOwnerScope owner,
        PromotionProposalRequest request,
        CancellationToken ct)
    {
        if (owner.CommerceBusinessId is not Guid businessId)
            throw new InvalidOperationException("Products can only be promoted inside their owning business scope.");

        var id = (request.SourceId ?? string.Empty).Trim();
        if (id.Length == 0) throw new ArgumentException("Select a product to promote.", nameof(request));

        var hasProductId = Guid.TryParse(id, out var productId);
        var product = await db.CommerceProducts.AsNoTracking()
            .Include(x => x.Images)
            .SingleOrDefaultAsync(x =>
                x.CommerceBusinessId == businessId &&
                x.IsActive &&
                ((hasProductId && x.Id == productId) || x.Slug == id), ct)
            ?? throw new InvalidOperationException("The selected product is not active in this business.");

        var business = await ActiveBusinessAsync(businessId, ct);
        var root = await businessPublicUrls.ResolveAsync(businessId, ct);
        var state = await PublishedStateAsync(owner, ct);
        var image = product.Images.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.DisplayOrder)
            .Select(x => x.ImageUrl)
            .FirstOrDefault(IsHttpUrl);

        return new(
            PromotionSourceKinds.Product,
            product.Id.ToString("D"),
            product.Name,
            Clean(product.Description, 4000),
            $"{root}/store/product/{Uri.EscapeDataString(product.Slug)}",
            image,
            Clean(product.PriceLabel, 100),
            business.DisplayName,
            business.BusinessType,
            state.Version.Id,
            state.Version.Revision,
            DateTime.UtcNow);
    }

    private async Task<PromotionSourceSnapshot> ResolveServiceAsync(
        MarketingOwnerScope owner,
        PromotionProposalRequest request,
        CancellationToken ct)
    {
        if (owner.CommerceBusinessId is not Guid businessId)
            throw new InvalidOperationException("Service promotion uses the owning business service facts.");

        var requested = Clean(request.SourceId, 500)
            ?? throw new ArgumentException("Select a service to promote.", nameof(request));
        var facts = await WebsiteBusinessFacts.LoadAsync(db, businessId, ct);
        var services = (facts.Services ?? string.Empty)
            .Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var service = services.FirstOrDefault(x => string.Equals(x, requested, StringComparison.OrdinalIgnoreCase))
            ?? services.FirstOrDefault(x => x.Contains(requested, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected service is not present in the canonical business service facts.");

        var business = await ActiveBusinessAsync(businessId, ct);
        var root = await businessPublicUrls.ResolveAsync(businessId, ct);
        var published = await PublishedStateAsync(owner, ct);
        var document = ReadDocument(published.Version.DocumentJson);
        var pagePath = NormalizePagePath(request.PagePath);
        var page = document.Pages.TryGetValue(pagePath, out var found) ? found : document.Pages.GetValueOrDefault("/");
        var image = FindHttpImage(document, pagePath);

        return new(
            PromotionSourceKinds.Service,
            service,
            service,
            "Service offered by " + business.DisplayName,
            root + (pagePath == "/" ? string.Empty : pagePath),
            image,
            null,
            business.DisplayName,
            business.BusinessType,
            published.Version.Id,
            published.Version.Revision,
            DateTime.UtcNow);
    }

    private async Task<PromotionSourceSnapshot> ResolveWebsitePageAsync(
        MarketingOwnerScope owner,
        PromotionProposalRequest request,
        CancellationToken ct)
    {
        var protect = request.SourceId?.StartsWith("protect:", StringComparison.Ordinal) == true;
        if (protect && owner != MarketingOwnerScope.Founder)
            throw new InvalidOperationException("Only the Founder owner can select the Founder Protect site.");
        var published = await PublishedStateAsync(owner, ct, protect ? WebsiteEditorSiteKeys.Protect : null);
        var document = ReadDocument(published.Version.DocumentJson);
        var path = NormalizePagePath(protect ? request.SourceId!["protect:".Length..] : request.PagePath);
        if (!document.Pages.TryGetValue(path, out var page) || page.Navigation.IsDeleted)
            throw new InvalidOperationException("Only an existing published website page can be promoted.");

        var baseUrl = protect ? (configuration["Commerce:ProtectPublicBaseUrl"] ?? "https://protect.mylegnd.com").TrimEnd('/') : await PublicBaseAsync(owner, ct);
        var title = Clean(page.Title, 300) ?? (path == "/" ? "Home" : path.Trim('/'));
        var description = Clean(page.Description, 4000);
        string? businessName = null;
        string? businessType = null;
        if (owner.CommerceBusinessId is Guid businessId)
        {
            var business = await ActiveBusinessAsync(businessId, ct);
            businessName = business.DisplayName;
            businessType = business.BusinessType;
        }

        return new(
            PromotionSourceKinds.WebsitePage,
            protect ? "protect:" + path : path,
            title,
            description,
            baseUrl + (path == "/" ? string.Empty : path),
            FindHttpImage(document, path),
            null,
            businessName,
            businessType,
            published.Version.Id,
            published.Version.Revision,
            DateTime.UtcNow);
    }

    private AdvertisingMutationPlan BuildPlan(
        PromotionSourceSnapshot source,
        PromotionCreativeAlternative creative,
        PromotionProposalRequest request,
        string status,
        string biddingType,
        IReadOnlyList<string> hints,
        string? providerFeedId)
    {
        if (!IsHttpUrl(creative.ImageUrl))
            throw new InvalidOperationException("The selected promotion source has no approved HTTP(S) image. Resolve an image before creating an executable promotion proposal.");

        var seed = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(source.SourceKind + "\n" + source.SourceId + "\n" + source.LandingUrl + "\n" + creative.Key)))
            .ToLowerInvariant()[..24];

        var targeting = new OpenAiAdsTargeting(
            Countries: CleanList(request.Countries, 250, 10),
            Platforms: CleanList(request.Platforms, 3, 20));

        var campaign = new OpenAiAdsCampaignCreateRequest(
            Name: Trim($"{source.DisplayName} · ChatGPT Ads", 1000),
            Status: status,
            Budget: new(DailySpendLimitMicros: request.DailyBudgetMicros),
            BiddingType: biddingType,
            Targeting: targeting,
            ConversionEventSettingId: request.ConversionEventSettingId,
            Description: Clean(request.Goal, 4000),
            Mode: providerFeedId is null ? null : "product_feed",
            ProductFeedId: providerFeedId,
            IdempotencyKey: "legend-promote-campaign-" + seed);

        var strategy = biddingType switch
        {
            OpenAiAdsBiddingTypes.Conversions => OpenAiAdsBiddingStrategies.MaximizeConversions,
            OpenAiAdsBiddingTypes.Clicks => OpenAiAdsBiddingStrategies.MaximizeClicks,
            _ => OpenAiAdsBiddingStrategies.FixedBid
        };
        if (strategy == OpenAiAdsBiddingStrategies.FixedBid)
            throw new InvalidOperationException("Promote This requires an explicit manual bid before an impressions campaign can be proposed.");

        var group = new OpenAiAdsAdGroupCreateRequest(
            CampaignId: "pending-parent",
            Name: Trim(source.DisplayName + " audience", 1000),
            Status: status,
            Bidding: new(strategy),
            ContextHints: hints,
            Description: "LEGEND Promote This context",
            ProductFeedId: providerFeedId,
            ProductFilters: providerFeedId is null ? null :
            [
                new OpenAiAdsProductSetFilter("external_id", "equals", [source.SourceId])
            ],
            IdempotencyKey: "legend-promote-group-" + seed);

        var upload = JsonSerializer.SerializeToElement(new { imageUrl = creative.ImageUrl }, JsonOptions);
        var ad = new OpenAiAdsAdCreateRequest(
            AdGroupId: "pending-parent",
            Name: Trim(creative.Name, 1000),
            Creative: new(
                OpenAiAdsCreativeTypes.ChatCard,
                Trim(creative.Title, 50),
                Trim(creative.Body, 100),
                creative.TargetUrl,
                FileId: null,
                Price: creative.PriceLabel),
            Status: status,
            IdempotencyKey: "legend-promote-ad-" + seed);

        return new(
            Trim("Promote " + source.DisplayName, 300),
            [
                new("campaign", AdvertisingActionTypes.CampaignCreate, JsonSerializer.SerializeToElement(campaign, JsonOptions)),
                new("ad-group", AdvertisingActionTypes.AdGroupCreate, JsonSerializer.SerializeToElement(group, JsonOptions), ParentStepKey: "campaign"),
                new("creative", AdvertisingActionTypes.CreativeUploadUrl, upload),
                new("ad", AdvertisingActionTypes.AdCreate, JsonSerializer.SerializeToElement(ad, JsonOptions), ParentStepKey: "ad-group", CreativeStepKey: "creative")
            ]);
    }

    private static IReadOnlyList<PromotionCreativeAlternative> BuildAlternatives(
        PromotionSourceSnapshot source,
        IReadOnlyList<string> hints)
    {
        var name = Trim(source.DisplayName, 50);
        var description = Clean(source.Description, 100) ?? $"Discover {name}.";
        var business = Clean(source.BusinessName, 50);

        return new[]
        {
            new PromotionCreativeAlternative(
                "direct",
                Trim(name + " · Direct", 1000),
                Trim(name, 50),
                Trim(description, 100),
                source.LandingUrl,
                source.ImageUrl,
                source.PriceLabel,
                hints),
            new PromotionCreativeAlternative(
                "benefit",
                Trim(name + " · Benefit", 1000),
                Trim("Explore " + name, 50),
                Trim(business is null ? $"See what {name} can do for you." : $"See why customers choose {business}.", 100),
                source.LandingUrl,
                source.ImageUrl,
                source.PriceLabel,
                hints),
            new PromotionCreativeAlternative(
                "action",
                Trim(name + " · Action", 1000),
                Trim("Get started today", 50),
                Trim($"Learn more about {name} and take the next step.", 100),
                source.LandingUrl,
                source.ImageUrl,
                source.PriceLabel,
                hints)
        };
    }

    private static IReadOnlyList<string> BuildContextHints(
        PromotionSourceSnapshot source,
        IReadOnlyList<string>? requested)
    {
        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(source.DisplayName)) values.Add(source.DisplayName);
        if (!string.IsNullOrWhiteSpace(source.BusinessType)) values.Add(source.BusinessType!);
        if (!string.IsNullOrWhiteSpace(source.BusinessName)) values.Add(source.BusinessName!);
        if (requested is not null) values.AddRange(requested);

        return values
            .Select(x => Clean(x, 500))
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
    }

    private async Task<(WebsiteContentState State, WebsiteContentVersion Version)> PublishedStateAsync(
        MarketingOwnerScope owner,
        CancellationToken ct, string? selectedSite = null)
    {
        string ownerKey;
        string siteKey;

        if (owner.CommerceBusinessId is Guid businessId)
        {
            ownerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(businessId);
            siteKey = WebsiteEditorSiteKeys.Business;
        }
        else if (owner.AgentTrackingProfileId is Guid agentId)
        {
            var agent = await db.AgentTrackingProfiles.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == agentId, ct)
                ?? throw new InvalidOperationException("The scoped agent website owner no longer exists.");
            ownerKey = (agent.AgentUserId ?? string.Empty).Trim().ToLowerInvariant();
            siteKey = WebsiteEditorSiteKeys.Protect;
        }
        else
        {
            ownerKey = WebsiteEditorSiteKeys.GlobalOwnerKey;
            siteKey = WebsiteEditorSiteKeys.Legend;
        }

        if (selectedSite is not null)
        {
            if (owner != MarketingOwnerScope.Founder || selectedSite != WebsiteEditorSiteKeys.Protect)
                throw new InvalidOperationException("Invalid published-site selection for this marketing owner.");
            var founder = await new AgentTrackingResolver(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentTrackingResolver>.Instance)
                .ResolveByUpnAsync(configuration["Founder:Upn"] ?? string.Empty, ct);
            if (!founder.Found || founder.Profile is null) throw new InvalidOperationException("The permanent Founder website owner is unavailable.");
            ownerKey = (founder.Profile.AgentUserId ?? string.Empty).Trim().ToLowerInvariant();
            siteKey = WebsiteEditorSiteKeys.Protect;
        }
        var state = await db.Set<WebsiteContentState>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerKey == ownerKey && x.SiteKey == siteKey, ct)
            ?? throw new InvalidOperationException("The website has no canonical content state.");
        if (state.PublishedVersionId is not Guid versionId)
            throw new InvalidOperationException("Publish the website before promoting it.");

        var version = await db.Set<WebsiteContentVersion>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == versionId && x.StateId == state.Id, ct)
            ?? throw new InvalidOperationException("The published website version is unavailable.");
        return (state, version);
    }

    private async Task<string> PublicBaseAsync(
        MarketingOwnerScope owner,
        CancellationToken ct)
    {
        if (owner.CommerceBusinessId is Guid businessId)
            return await businessPublicUrls.ResolveAsync(businessId, ct);

        if (owner.AgentTrackingProfileId is Guid agentId)
        {
            var slug = await db.AgentTrackingProfiles.AsNoTracking()
                .Where(x => x.Id == agentId)
                .Select(x => x.Slug)
                .SingleOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(slug))
                throw new InvalidOperationException("The scoped agent does not have a canonical public slug.");
            return (configuration["Commerce:ProtectPublicBaseUrl"] ?? "https://protect.mylegnd.com").TrimEnd('/') +
                   "/a/" + Uri.EscapeDataString(slug.Trim());
        }

        return (configuration["Commerce:LegendPublicBaseUrl"] ?? "https://mylegnd.com").TrimEnd('/');
    }

    private async Task<CommerceBusiness> ActiveBusinessAsync(Guid businessId, CancellationToken ct) =>
        await db.CommerceBusinesses.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == businessId && x.IsActive && x.Status == "Active", ct)
        ?? throw new InvalidOperationException("The business is not active.");

    private static WebsiteContentDocument ReadDocument(string json) =>
        WebsiteContentSanitizer.ReadPersisted(json, JsonOptions);

    private static string NormalizePagePath(string? value)
    {
        var path = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim();
        if (!path.StartsWith('/')) path = "/" + path;
        if (path.Length > 1) path = path.TrimEnd('/');
        if (path.Contains("..", StringComparison.Ordinal) || path.Contains('?', StringComparison.Ordinal) || path.Contains('#', StringComparison.Ordinal))
            throw new ArgumentException("Choose a canonical website page path.", nameof(value));
        return path;
    }

    private static string? FindHttpImage(WebsiteContentDocument document, string pagePath)
    {
        if (document.LegacyMigration is { } legacy &&
            legacy.Pages.TryGetValue(pagePath, out var legacyPage))
        {
            var legacyValues = legacyPage.Elements.Values.Select(value => value.ImageDataUrl)
                .Concat(legacyPage.Extras.Select(value => value.ImageDataUrl));
            return legacyValues.FirstOrDefault(IsHttpUrl);
        }

        if (!document.Pages.TryGetValue(pagePath, out var page)) return null;

        string? Find(IEnumerable<WebsiteCompositionNode> nodes)
        {
            foreach (var node in nodes ?? [])
            {
                if (node.Type == "image")
                {
                    if (IsHttpUrl(node.MediaUrl)) return node.MediaUrl;
                    if (node.MediaAssetId.HasValue)
                        return "/api/website-content/media/" + node.MediaAssetId.Value.ToString("D");
                }
                var child = Find(node.Children);
                if (child is not null) return child;
            }
            return null;
        }

        return Find(page.Composition);
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static IReadOnlyList<string>? CleanList(IReadOnlyList<string>? values, int maxCount, int maxLength)
    {
        if (values is null) return null;
        if (values.Count > maxCount) throw new ArgumentException("Too many promotion targeting values.");
        return values.Select(x => Clean(x, maxLength) ?? throw new ArgumentException("Promotion targeting values cannot be blank."))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = new string(value.Trim().Where(ch => !char.IsControl(ch) || ch is '\n' or '\t').Take(max).ToArray());
        return text.Length == 0 ? null : text;
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
