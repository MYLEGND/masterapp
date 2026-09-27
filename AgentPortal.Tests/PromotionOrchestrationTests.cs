using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.WebsiteEditing;
using Microsoft.Extensions.Configuration;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class PromotionOrchestrationTests
{
    [Fact]
    public async Task BusinessProductPromotionUsesPublishedWebsiteCustomDomainAndCanonicalCommerceProduct()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness
        {
            Id = Guid.NewGuid(),
            Key = "roofer",
            DisplayName = "Roofing Co",
            LegalName = "Roofing Co LLC",
            BusinessType = "Roofing",
            OwnerEmail = "owner@example.test",
            IsActive = true,
            Status = "Active"
        };
        db.CommerceBusinesses.Add(business);
        db.CommerceBusinessStorefrontSettings.Add(new CommerceBusinessStorefrontSettings
        {
            CommerceBusinessId = business.Id,
            PublicFactsJson = JsonSerializer.Serialize(new WebsiteBusinessFacts
            {
                Services = "Roof repair\nRoof replacement"
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        });
        db.Set<WebsiteDomainBinding>().Add(new WebsiteDomainBinding
        {
            CommerceBusinessId = business.Id,
            Hostname = "roofing.example",
            Status = "active",
            CertificateStatus = "active",
            LastCheckedUtc = DateTime.UtcNow
        });

        var document = new WebsiteContentDocument();
        document.Pages["/"] = new WebsitePageDocument
        {
            Title = "Roofing Co",
            Description = "Local roofing service"
        };
        var state = new WebsiteContentState
        {
            OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(business.Id),
            SiteKey = WebsiteEditorSiteKeys.Business,
            CommerceBusinessId = business.Id,
            Revision = 7,
            DraftJson = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        var version = new WebsiteContentVersion
        {
            StateId = state.Id,
            Revision = 6,
            DocumentJson = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ActorUserId = "owner"
        };
        state.PublishedVersionId = version.Id;
        db.Set<WebsiteContentState>().Add(state);
        db.Set<WebsiteContentVersion>().Add(version);

        var product = new CommerceProduct
        {
            CommerceBusinessId = business.Id,
            ExternalProductKey = "roof-package",
            Name = "Roof Tune-Up",
            Slug = "roof-tune-up",
            Description = "Inspection and targeted roof repair.",
            PriceLabel = "From $299",
            PriceCents = 29900,
            IsActive = true
        };
        product.Images.Add(new CommerceProductImage
        {
            ExternalImageKey = "hero",
            ImageUrl = "https://cdn.example.test/roof.jpg",
            FileName = "roof.jpg",
            AltText = "Roof",
            IsPrimary = true
        });
        db.CommerceProducts.Add(product);
        await db.SaveChangesAsync();

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var approvals = new FakeApprovalService();
        var connection = new FakeConnectionAuthority();
        var ads = new FakeAdsExecutionService();
        var service = new PromotionOrchestrationService(db, config, approvals, connection, ads);
        var actor = new WebsiteEditorTicket(
            WebsiteEditorSiteKeys.Business,
            WebsiteEditorSiteKeys.BusinessOwnerKey(business.Id),
            null,
            false,
            DateTime.UtcNow.AddHours(1),
            business.Id,
            ActorUserId: "owner",
            ActorClientProfileId: Guid.NewGuid());
        var owner = MarketingOwnerScope.Business(business.Id);

        var sources = await service.SourcesAsync(actor, owner);
        Assert.Contains(sources, x => x.SourceKind == PromotionSourceKinds.Product && x.SourceId == product.Id.ToString("D"));
        Assert.Contains(sources, x => x.SourceKind == PromotionSourceKinds.Service && x.Label == "Roof repair");
        Assert.Contains(sources, x => x.SourceKind == PromotionSourceKinds.WebsitePage && x.PagePath == "/");

        var draft = await service.DraftAsync(actor, owner, new PromotionProposalRequest(
            PromotionSourceKinds.Product,
            product.Id.ToString("D"),
            null,
            "Get more roof tune-up customers",
            25_000_000,
            Countries: ["US"],
            Status: "paused"));

        Assert.Equal("https://roofing.example/store/product/roof-tune-up", draft.Source.LandingUrl);
        Assert.Equal(version.Id, draft.Source.WebsiteContentVersionId);
        Assert.Equal("https://cdn.example.test/roof.jpg", draft.Source.ImageUrl);
        Assert.Equal(3, draft.Alternatives.Count);
        Assert.Equal(4, draft.Plan.Steps.Count);
        Assert.Equal(
            new[] {
                AdvertisingActionTypes.CampaignCreate,
                AdvertisingActionTypes.AdGroupCreate,
                AdvertisingActionTypes.CreativeUploadUrl,
                AdvertisingActionTypes.AdCreate
            },
            draft.Plan.Steps.Select(x => x.ActionType).ToArray());
        Assert.Equal("campaign", draft.Plan.Steps[1].ParentStepKey);
        Assert.Equal("ad-group", draft.Plan.Steps[3].ParentStepKey);
        Assert.Equal("creative", draft.Plan.Steps[3].CreativeStepKey);
    }

    [Fact]
    public void SharedWebsiteManagerOwnsReviewApproveExecutePromoteFlow()
    {
        var root = FindRoot();
        var ui = File.ReadAllText(Path.Combine(root, "Legend-Design", "legend-website-management.js"));
        var controller = File.ReadAllText(Path.Combine(root, "Infrastructure", "WebsiteEditing", "WebsitePlatformController.cs"));

        Assert.Contains("tile('Promote This'", ui, StringComparison.Ordinal);
        Assert.Contains("/promote/sources", ui, StringComparison.Ordinal);
        Assert.Contains("/promote/conversions", ui, StringComparison.Ordinal);
        Assert.Contains("/promote/draft", ui, StringComparison.Ordinal);
        Assert.Contains("/promote/propose", ui, StringComparison.Ordinal);
        Assert.Contains("/promote/approve", ui, StringComparison.Ordinal);
        Assert.Contains("/promote/execute", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("api.ads.openai.com", ui, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("[HttpPost(\"manage/promote/propose\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"manage/promote/approve\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"manage/promote/execute\")]", controller, StringComparison.Ordinal);
        Assert.Contains("CanPublishAsync(actor", controller, StringComparison.Ordinal);
        Assert.Contains("ResolveAdvertisingOwnerAsync", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSpecificHostsDoNotOwnParallelPromotionOrAdvertisingApprovalServices()
    {
        var root = FindRoot();
        foreach (var path in new[]
        {
            Path.Combine(root, "AgentPortal", "Services", "PromotionOrchestrationService.cs"),
            Path.Combine(root, "ClientApp", "Services", "PromotionOrchestrationService.cs"),
            Path.Combine(root, "ParfaitApp", "Services", "PromotionOrchestrationService.cs"),
            Path.Combine(root, "AgentPortal", "Services", "AdvertisingActionAuthorizationService.cs"),
            Path.Combine(root, "ClientApp", "Services", "AdvertisingActionAuthorizationService.cs"),
            Path.Combine(root, "ParfaitApp", "Services", "AdvertisingActionAuthorizationService.cs")
        })
            Assert.False(File.Exists(path), $"Parallel scoped implementation is forbidden: {path}");
    }

    private static string FindRoot()
    {
        var github = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(github) && File.Exists(Path.Combine(github, "MASTERAPP.sln")))
            return github;
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MASTERAPP.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException();
    }

    private sealed class FakeConnectionAuthority : IOpenAiAdsAccountConnectionAuthority
    {
        public Task<OpenAiAdsConnectionSnapshot> GetAsync(MarketingOwnerScope owner, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OpenAiAdsConnectionSnapshot(
                owner, true, true, Guid.NewGuid(), "acct", "Scoped Ads", "admin", "approved", "api_key",
                null, null, ["ad_account.read"], "pixel", "source", true, true, DateTime.UtcNow, null, DateTime.UtcNow));

        public Task<OpenAiAdsConnectionSecrets> GetSecretsAsync(MarketingOwnerScope owner, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OpenAiAdsConnectionSecrets("secret", "capi"));

        public Task<OpenAiAdsConnectionSnapshot> BindVerifiedAsync(MarketingOwnerScope owner, VerifiedOpenAiAdsAccount verifiedAccount, OpenAiAdsConnectionSecrets secrets, Guid? expectedRevision = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OpenAiAdsConnectionSnapshot> DisconnectAsync(MarketingOwnerScope owner, Guid expectedRevision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAdsExecutionService : IOpenAiAdsExecutionService
    {
        public Task<OpenAiAdsProviderPage> ListConversionEventSettingsAsync(MarketingOwnerScope owner, CancellationToken ct = default) =>
            Task.FromResult(new OpenAiAdsProviderPage(JsonSerializer.SerializeToElement(new
            {
                data = new[] { new { id = "ces_1", event_type = "lead_created", status = "active", name = "Lead" } }
            })));

        public Task<OpenAiAdsProviderPage> ListCampaignsAsync(MarketingOwnerScope owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> GetCampaignAsync(MarketingOwnerScope owner, string campaignId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> CreateCampaignAsync(MarketingOwnerScope owner, OpenAiAdsCampaignCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> UpdateCampaignAsync(MarketingOwnerScope owner, OpenAiAdsCampaignUpdateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> SetCampaignStatusAsync(MarketingOwnerScope owner, string campaignId, string status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderPage> ListAdGroupsAsync(MarketingOwnerScope owner, string? campaignId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> GetAdGroupAsync(MarketingOwnerScope owner, string adGroupId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> CreateAdGroupAsync(MarketingOwnerScope owner, OpenAiAdsAdGroupCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> UpdateAdGroupAsync(MarketingOwnerScope owner, OpenAiAdsAdGroupUpdateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> SetAdGroupStatusAsync(MarketingOwnerScope owner, string adGroupId, string status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderPage> ListAdsAsync(MarketingOwnerScope owner, string? adGroupId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> GetAdAsync(MarketingOwnerScope owner, string adId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> CreateAdAsync(MarketingOwnerScope owner, OpenAiAdsAdCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> UpdateAdAsync(MarketingOwnerScope owner, OpenAiAdsAdUpdateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> SetAdStatusAsync(MarketingOwnerScope owner, string adId, string status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> PreviewAdAsync(MarketingOwnerScope owner, string adId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsImageUploadResult> UploadImageUrlAsync(MarketingOwnerScope owner, string imageUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsImageUploadResult> UploadImageAsync(MarketingOwnerScope owner, Stream content, string fileName, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsGeoSearchResult> SearchGeoAsync(MarketingOwnerScope owner, string query, int limit = 20, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> CreateConversionEventSettingAsync(MarketingOwnerScope owner, OpenAiAdsConversionEventSettingCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsInsightsResult> GetConversionInsightsAsync(MarketingOwnerScope owner, OpenAiAdsConversionInsightsQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsInsightsResult> GetAccountInsightsAsync(MarketingOwnerScope owner, string aggregationLevel, OpenAiAdsInsightsQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsInsightsResult> GetCampaignInsightsAsync(MarketingOwnerScope owner, string campaignId, string aggregationLevel, OpenAiAdsInsightsQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsInsightsResult> GetAdGroupInsightsAsync(MarketingOwnerScope owner, string adGroupId, string aggregationLevel, OpenAiAdsInsightsQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsInsightsResult> GetAdInsightsAsync(MarketingOwnerScope owner, string adId, OpenAiAdsInsightsQuery query, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeApprovalService : IAdvertisingActionAuthorizationService
    {
        public Task<AdvertisingActionProposalSnapshot> ProposeAsync(MarketingOwnerScope owner, string proposalKind, AdvertisingMutationPlan plan, string proposedByUserId, JsonElement? sourceSnapshot = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AdvertisingActionApprovalReceipt> ApproveAsync(MarketingOwnerScope owner, Guid proposalId, string approvedByUserId, string expectedRevision, DateTime approvalExpiresUtc, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AdvertisingActionExecutionReceipt> ExecuteAsync(MarketingOwnerScope owner, Guid proposalId, string expectedRevision, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AdvertisingActionProposalSnapshot> RejectAsync(MarketingOwnerScope owner, Guid proposalId, string rejectedByUserId, string expectedRevision, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AdvertisingActionProposalSnapshot?> GetAsync(MarketingOwnerScope owner, Guid proposalId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
