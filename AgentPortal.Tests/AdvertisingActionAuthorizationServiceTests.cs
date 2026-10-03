using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Analytics;
using Microsoft.AspNetCore.DataProtection;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class AdvertisingActionAuthorizationServiceTests
{
    [Fact]
    public async Task ProposalCannotExecuteUntilExactOwnerApproves()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var owner = MarketingOwnerScope.Business(Guid.NewGuid());
        var provider = new FakeExecutionService();
        var service = new AdvertisingActionAuthorizationService(db, provider);
        var plan = Plan();

        var proposal = await service.ProposeAsync(owner, "promote_this", plan, "user-1");

        Assert.Equal(AdvertisingActionStates.Proposed, proposal.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteAsync(owner, proposal.Id, proposal.Revision));
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task ApprovalThenExecutionConsumesPlanOnceAndPersistsProviderReceipts()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var owner = MarketingOwnerScope.Business(Guid.NewGuid());
        var provider = new FakeExecutionService();
        var service = new AdvertisingActionAuthorizationService(db, provider);
        var proposal = await service.ProposeAsync(owner, "promote_this", Plan(), "user-1");

        var approval = await service.ApproveAsync(
            owner, proposal.Id, "business-owner", proposal.Revision, DateTime.UtcNow.AddMinutes(10));
        Assert.Equal(AdvertisingActionStates.Approved, approval.State);

        var receipt = await service.ExecuteAsync(owner, proposal.Id, approval.Revision);

        Assert.Equal(AdvertisingActionStates.Completed, receipt.State);
        Assert.Equal(new[] { "campaign", "ad-group", "creative", "ad" }, provider.Calls);
        Assert.True(receipt.ProviderReceipt.HasValue);

        var stored = await service.GetAsync(owner, proposal.Id);
        Assert.Equal(AdvertisingActionStates.Completed, stored!.State);
        Assert.NotNull(stored.CompletedUtc);

        var replay = await service.ExecuteAsync(owner, proposal.Id, stored.Revision);
        Assert.Equal(AdvertisingActionStates.Completed, replay.State);
        Assert.Equal(4, provider.Calls.Count);
    }

    [Fact]
    public async Task CrossScopeCannotReadApproveRejectOrExecuteAnotherOwnersPlan()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var owner = MarketingOwnerScope.Business(Guid.NewGuid());
        var foreign = MarketingOwnerScope.Business(Guid.NewGuid());
        var service = new AdvertisingActionAuthorizationService(db, new FakeExecutionService());
        var proposal = await service.ProposeAsync(owner, "promote_this", Plan(), "owner-user");

        Assert.Null(await service.GetAsync(foreign, proposal.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApproveAsync(foreign, proposal.Id, "foreign", proposal.Revision, DateTime.UtcNow.AddMinutes(5)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RejectAsync(foreign, proposal.Id, "foreign", proposal.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteAsync(foreign, proposal.Id, proposal.Revision));
    }

    [Fact]
    public async Task StaleRevisionCannotApproveOrExecuteChangedLedgerRow()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var owner = MarketingOwnerScope.Agent(Guid.NewGuid());
        var service = new AdvertisingActionAuthorizationService(db, new FakeExecutionService());
        var proposal = await service.ProposeAsync(owner, "manual", Plan(), "agent");
        var approved = await service.ApproveAsync(
            owner, proposal.Id, "agent", proposal.Revision, DateTime.UtcNow.AddMinutes(5));

        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException>(() =>
            service.ExecuteAsync(owner, proposal.Id, proposal.Revision));

        var receipt = await service.ExecuteAsync(owner, proposal.Id, approved.Revision);
        Assert.Equal(AdvertisingActionStates.Completed, receipt.State);
    }

    [Fact]
    public async Task RetryableProviderFailureBecomesOutcomeUnknownAndCannotBlindReplay()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var owner = MarketingOwnerScope.Founder;
        var provider = new FakeExecutionService { FailOnAdGroup = true };
        var service = new AdvertisingActionAuthorizationService(db, provider);
        var proposal = await service.ProposeAsync(owner, "manual", Plan(), "founder");
        var approved = await service.ApproveAsync(
            owner, proposal.Id, "founder", proposal.Revision, DateTime.UtcNow.AddMinutes(5));

        var receipt = await service.ExecuteAsync(owner, proposal.Id, approved.Revision);

        Assert.Equal(AdvertisingActionStates.OutcomeUnknown, receipt.State);
        var stored = await service.GetAsync(owner, proposal.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteAsync(owner, proposal.Id, stored!.Revision));
    }

    [Fact]
    public async Task PlanDigestIsDeterministicAndChangedBudgetCreatesDifferentProposal()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var owner = MarketingOwnerScope.Founder;
        var service = new AdvertisingActionAuthorizationService(db, new FakeExecutionService());

        var first = await service.ProposeAsync(owner, "manual", Plan(10_000_000), "founder");
        var duplicate = await service.ProposeAsync(owner, "manual", Plan(10_000_000), "founder");
        var changed = await service.ProposeAsync(owner, "manual", Plan(20_000_000), "founder");

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(first.ActionDigest, duplicate.ActionDigest);
        Assert.NotEqual(first.ActionDigest, changed.ActionDigest);
        Assert.NotEqual(first.Id, changed.Id);
    }

    private static AdvertisingMutationPlan Plan(long dailyBudget = 10_000_000)
    {
        var campaign = new OpenAiAdsCampaignCreateRequest(
            "Roofing Growth", "paused", new(DailySpendLimitMicros: dailyBudget), "clicks",
            IdempotencyKey: "campaign-key");
        var group = new OpenAiAdsAdGroupCreateRequest(
            "pending-parent", "Roofing Audience", "paused",
            new(OpenAiAdsBiddingStrategies.MaximizeClicks),
            ContextHints: ["roof repair"],
            IdempotencyKey: "group-key");
        var upload = JsonSerializer.SerializeToElement(new { imageUrl = "https://example.com/roof.jpg" });
        var ad = new OpenAiAdsAdCreateRequest(
            "pending-parent", "Roofing Ad",
            new(OpenAiAdsCreativeTypes.ChatCard, "Roof repair", "Get a quote", "https://example.com/roof", null),
            "paused", "ad-key");

        return new("Roofing promotion",
        [
            new("campaign", AdvertisingActionTypes.CampaignCreate, JsonSerializer.SerializeToElement(campaign)),
            new("ad-group", AdvertisingActionTypes.AdGroupCreate, JsonSerializer.SerializeToElement(group), "campaign"),
            new("creative", AdvertisingActionTypes.CreativeUploadUrl, upload),
            new("ad", AdvertisingActionTypes.AdCreate, JsonSerializer.SerializeToElement(ad), "ad-group", "creative")
        ]);
    }

    private sealed class FakeExecutionService : IOpenAiAdsExecutionService
    {
        public List<string> Calls { get; } = [];
        public bool FailOnAdGroup { get; set; }

        public Task<OpenAiAdsProviderEntity> CreateCampaignAsync(MarketingOwnerScope owner, OpenAiAdsCampaignCreateRequest request, CancellationToken ct = default)
        {
            Calls.Add("campaign");
            return Task.FromResult(Entity(new { id = "cmpn_1" }));
        }

        public Task<OpenAiAdsProviderEntity> CreateAdGroupAsync(MarketingOwnerScope owner, OpenAiAdsAdGroupCreateRequest request, CancellationToken ct = default)
        {
            Calls.Add("ad-group");
            Assert.Equal("cmpn_1", request.CampaignId);
            if (FailOnAdGroup) throw new OpenAiAdsExecutionException(503, true, "temporary");
            return Task.FromResult(Entity(new { id = "ag_1" }));
        }

        public Task<OpenAiAdsImageUploadResult> UploadImageUrlAsync(MarketingOwnerScope owner, string imageUrl, CancellationToken ct = default)
        {
            Calls.Add("creative");
            using var json = JsonDocument.Parse("""{"file_id":"file_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""");
            return Task.FromResult(new OpenAiAdsImageUploadResult(
                "file_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", json.RootElement.Clone()));
        }

        public Task<OpenAiAdsProviderEntity> CreateAdAsync(MarketingOwnerScope owner, OpenAiAdsAdCreateRequest request, CancellationToken ct = default)
        {
            Calls.Add("ad");
            Assert.Equal("ag_1", request.AdGroupId);
            Assert.Equal("file_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", request.Creative.FileId);
            return Task.FromResult(Entity(new { id = "ad_1" }));
        }

        private static OpenAiAdsProviderEntity Entity(object value) =>
            new(JsonSerializer.SerializeToElement(value));

        public Task<OpenAiAdsProviderPage> ListCampaignsAsync(MarketingOwnerScope owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> GetCampaignAsync(MarketingOwnerScope owner, string campaignId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> UpdateCampaignAsync(MarketingOwnerScope owner, OpenAiAdsCampaignUpdateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> SetCampaignStatusAsync(MarketingOwnerScope owner, string campaignId, string status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderPage> ListAdGroupsAsync(MarketingOwnerScope owner, string? campaignId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> GetAdGroupAsync(MarketingOwnerScope owner, string adGroupId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> UpdateAdGroupAsync(MarketingOwnerScope owner, OpenAiAdsAdGroupUpdateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> SetAdGroupStatusAsync(MarketingOwnerScope owner, string adGroupId, string status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderPage> ListAdsAsync(MarketingOwnerScope owner, string? adGroupId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> GetAdAsync(MarketingOwnerScope owner, string adId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> UpdateAdAsync(MarketingOwnerScope owner, OpenAiAdsAdUpdateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> SetAdStatusAsync(MarketingOwnerScope owner, string adId, string status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> PreviewAdAsync(MarketingOwnerScope owner, string adId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsImageUploadResult> UploadImageAsync(MarketingOwnerScope owner, System.IO.Stream content, string fileName, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsGeoSearchResult> SearchGeoAsync(MarketingOwnerScope owner, string query, int limit = 20, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderPage> ListConversionEventSettingsAsync(MarketingOwnerScope owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> CreateConversionEventSettingAsync(MarketingOwnerScope owner, OpenAiAdsConversionEventSettingCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsInsightsResult> GetConversionInsightsAsync(MarketingOwnerScope owner, OpenAiAdsConversionInsightsQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsInsightsResult> GetAccountInsightsAsync(MarketingOwnerScope owner, string aggregationLevel, OpenAiAdsInsightsQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsInsightsResult> GetCampaignInsightsAsync(MarketingOwnerScope owner, string campaignId, string aggregationLevel, OpenAiAdsInsightsQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsInsightsResult> GetAdGroupInsightsAsync(MarketingOwnerScope owner, string adGroupId, string aggregationLevel, OpenAiAdsInsightsQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsInsightsResult> GetAdInsightsAsync(MarketingOwnerScope owner, string adId, OpenAiAdsInsightsQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderPage> ListProductFeedsAsync(MarketingOwnerScope owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> CreateProductFeedAsync(MarketingOwnerScope owner, OpenAiAdsProductFeedCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderPage> ListProductFeedItemsAsync(MarketingOwnerScope owner, string productFeedId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenAiAdsProviderEntity> UpsertProductFeedItemAsync(MarketingOwnerScope owner, OpenAiAdsProductFeedItemUpsertRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
