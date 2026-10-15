using System.Text.Json;
using Infrastructure.WebsiteEditing;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public sealed record AdvertisingCommandCenterSnapshot(
    MarketingOwnerScope Owner,
    OpenAiAdsConnectionSnapshot Connection,
    JsonElement Campaigns,
    JsonElement ConversionSettings,
    IReadOnlyList<PromotionSourceOption> Sources,
    IReadOnlyList<AdvertisingActionProposalSnapshot> Proposals,
    string? SourceInventoryError = null);

public sealed record AdvertisingCampaignStatusProposalRequest(
    string CampaignId,
    string CampaignName,
    string Status);

public sealed record AdvertisingCampaignBudgetProposalRequest(
    string CampaignId,
    string CampaignName,
    OpenAiAdsBudget Budget);

public interface IAdvertisingCommandCenterService
{
    Task<AdvertisingCommandCenterSnapshot> GetAsync(MarketingOwnerScope owner, CancellationToken ct = default);
    Task<PromotionDraft> DraftPromotionAsync(MarketingOwnerScope owner, PromotionProposalRequest request, CancellationToken ct = default);
    Task<AdvertisingActionProposalSnapshot> ProposePromotionAsync(MarketingOwnerScope owner, PromotionProposalRequest request, string actorUserId, CancellationToken ct = default);
    Task<AdvertisingActionProposalSnapshot> ProposeCampaignStatusAsync(MarketingOwnerScope owner, AdvertisingCampaignStatusProposalRequest request, string actorUserId, CancellationToken ct = default);
    Task<AdvertisingActionProposalSnapshot> ProposeCampaignBudgetAsync(MarketingOwnerScope owner, AdvertisingCampaignBudgetProposalRequest request, string actorUserId, CancellationToken ct = default);
    Task<AdvertisingActionApprovalReceipt> ApproveAsync(MarketingOwnerScope owner, Guid proposalId, string actorUserId, string revision, CancellationToken ct = default);
    Task<AdvertisingActionExecutionReceipt> ExecuteAsync(MarketingOwnerScope owner, Guid proposalId, string revision, CancellationToken ct = default);
    Task<AdvertisingActionProposalSnapshot> RejectAsync(MarketingOwnerScope owner, Guid proposalId, string actorUserId, string revision, CancellationToken ct = default);
    Task<OpenAiAdsInsightsResult> CampaignInsightsAsync(MarketingOwnerScope owner, string campaignId, OpenAiAdsInsightsQuery query, CancellationToken ct = default);
}

public sealed class AdvertisingCommandCenterService(
    IOpenAiAdsAccountConnectionAuthority connections,
    IOpenAiAdsExecutionService ads,
    IPromotionOrchestrationService promotion,
    IAdvertisingActionAuthorizationService authorizations) : IAdvertisingCommandCenterService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AdvertisingCommandCenterSnapshot> GetAsync(
        MarketingOwnerScope owner,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var connection = await connections.GetAsync(owner, ct);

        JsonElement campaigns = EmptyList();
        JsonElement conversions = EmptyList();
        IReadOnlyList<PromotionSourceOption> sources = [];
        string? sourceInventoryError = null;

        if (connection.Connected && connection.HasManagementCredential)
        {
            campaigns = (await ads.ListCampaignsAsync(owner, ct)).Payload;
            conversions = (await ads.ListConversionEventSettingsAsync(owner, ct)).Payload;
        }

        try
        {
            sources = await promotion.SourcesAsync(owner, ct);
        }
        catch (InvalidOperationException ex)
        {
            sourceInventoryError = ex.Message;
        }

        var proposals = await authorizations.ListAsync(owner, 100, ct);
        return new(owner, connection, campaigns, conversions, sources, proposals, sourceInventoryError);
    }

    public Task<PromotionDraft> DraftPromotionAsync(
        MarketingOwnerScope owner,
        PromotionProposalRequest request,
        CancellationToken ct = default) =>
        promotion.DraftAsync(owner, request, ct);

    public Task<AdvertisingActionProposalSnapshot> ProposePromotionAsync(
        MarketingOwnerScope owner,
        PromotionProposalRequest request,
        string actorUserId,
        CancellationToken ct = default) =>
        promotion.ProposeAsync(owner, request, actorUserId, ct);

    public Task<AdvertisingActionProposalSnapshot> ProposeCampaignStatusAsync(
        MarketingOwnerScope owner,
        AdvertisingCampaignStatusProposalRequest request,
        string actorUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var campaignId = CleanId(request.CampaignId);
        var status = OpenAiAdsEntityStatuses.NormalizeUpdate(request.Status);
        var plan = new AdvertisingMutationPlan(
            $"Set {CleanText(request.CampaignName, 300)} to {status}",
            [
                new(
                    "campaign-status",
                    AdvertisingActionTypes.CampaignStatus,
                    JsonSerializer.SerializeToElement(new { entityId = campaignId, status }, JsonOptions))
            ]);

        return authorizations.ProposeAsync(
            owner,
            "campaign_status",
            plan,
            CleanText(actorUserId, 450),
            JsonSerializer.SerializeToElement(new { campaignId, campaignName = request.CampaignName, status }, JsonOptions),
            ct);
    }

    public Task<AdvertisingActionProposalSnapshot> ProposeCampaignBudgetAsync(
        MarketingOwnerScope owner,
        AdvertisingCampaignBudgetProposalRequest request,
        string actorUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Budget.Validate();
        var campaignId = CleanId(request.CampaignId);
        var update = new OpenAiAdsCampaignUpdateRequest(campaignId, Budget: request.Budget);
        var plan = new AdvertisingMutationPlan(
            $"Update budget · {CleanText(request.CampaignName, 300)}",
            [
                new(
                    "campaign-budget",
                    AdvertisingActionTypes.CampaignUpdate,
                    JsonSerializer.SerializeToElement(update, JsonOptions))
            ]);

        return authorizations.ProposeAsync(
            owner,
            "campaign_budget",
            plan,
            CleanText(actorUserId, 450),
            JsonSerializer.SerializeToElement(new { campaignId, campaignName = request.CampaignName, request.Budget }, JsonOptions),
            ct);
    }

    public Task<AdvertisingActionApprovalReceipt> ApproveAsync(
        MarketingOwnerScope owner,
        Guid proposalId,
        string actorUserId,
        string revision,
        CancellationToken ct = default) =>
        authorizations.ApproveAsync(
            owner,
            proposalId,
            CleanText(actorUserId, 450),
            revision,
            DateTime.UtcNow.AddMinutes(15),
            ct);

    public Task<AdvertisingActionExecutionReceipt> ExecuteAsync(
        MarketingOwnerScope owner,
        Guid proposalId,
        string revision,
        CancellationToken ct = default) =>
        authorizations.ExecuteAsync(owner, proposalId, revision, ct);

    public Task<AdvertisingActionProposalSnapshot> RejectAsync(
        MarketingOwnerScope owner,
        Guid proposalId,
        string actorUserId,
        string revision,
        CancellationToken ct = default) =>
        authorizations.RejectAsync(owner, proposalId, CleanText(actorUserId, 450), revision, ct);

    public Task<OpenAiAdsInsightsResult> CampaignInsightsAsync(
        MarketingOwnerScope owner,
        string campaignId,
        OpenAiAdsInsightsQuery query,
        CancellationToken ct = default) =>
        ads.GetCampaignInsightsAsync(owner, CleanId(campaignId), "campaign", query, ct);

    private static JsonElement EmptyList() =>
        JsonSerializer.SerializeToElement(new { @object = "list", data = Array.Empty<object>(), has_more = false }, JsonOptions);

    private static string CleanId(string? value)
    {
        var text = CleanText(value, 500);
        if (text.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')))
            throw new ArgumentException("Advertising provider identifier is invalid.");
        return text;
    }

    private static string CleanText(string? value, int max)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > max || text.Any(char.IsControl))
            throw new ArgumentException("A valid advertising value is required.");
        return text;
    }
}
