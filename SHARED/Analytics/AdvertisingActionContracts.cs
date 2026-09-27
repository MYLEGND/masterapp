using System.Text.Json;

namespace Shared.Analytics;

public static class AdvertisingActionStates
{
    public const string Proposed = "Proposed";
    public const string Approved = "Approved";
    public const string Executing = "Executing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string OutcomeUnknown = "OutcomeUnknown";
    public const string Rejected = "Rejected";
    public const string Expired = "Expired";
}

public static class AdvertisingActionTypes
{
    public const string CampaignCreate = "campaign.create";
    public const string CampaignUpdate = "campaign.update";
    public const string CampaignStatus = "campaign.status";
    public const string AdGroupCreate = "ad_group.create";
    public const string AdGroupUpdate = "ad_group.update";
    public const string AdGroupStatus = "ad_group.status";
    public const string AdCreate = "ad.create";
    public const string AdUpdate = "ad.update";
    public const string AdStatus = "ad.status";
    public const string ConversionSettingCreate = "conversion_setting.create";
    public const string CreativeUploadUrl = "creative.upload_url";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        CampaignCreate, CampaignUpdate, CampaignStatus,
        AdGroupCreate, AdGroupUpdate, AdGroupStatus,
        AdCreate, AdUpdate, AdStatus,
        ConversionSettingCreate, CreativeUploadUrl
    };
}

public sealed record AdvertisingMutationPlan(
    string Title,
    IReadOnlyList<AdvertisingMutationPlanStep> Steps,
    string Version = "legend-ad-plan.v1");

public sealed record AdvertisingMutationPlanStep(
    string StepKey,
    string ActionType,
    JsonElement Payload,
    string? ParentStepKey = null,
    string? CreativeStepKey = null);

public sealed record AdvertisingActionProposalSnapshot(
    Guid Id,
    MarketingOwnerScope Owner,
    string Provider,
    string ProposalKind,
    string ActionDigest,
    AdvertisingMutationPlan Plan,
    string State,
    string ProposedByUserId,
    DateTime ProposedUtc,
    string? ApprovedByUserId,
    DateTime? ApprovedUtc,
    DateTime? ApprovalExpiresUtc,
    DateTime? ExecutionStartedUtc,
    DateTime? CompletedUtc,
    JsonElement? SourceSnapshot,
    JsonElement? ProviderReceipt,
    string? ErrorCode,
    string? ErrorMessage,
    string Revision);

public sealed record AdvertisingActionApprovalReceipt(
    Guid Id,
    string ActionDigest,
    string State,
    string Revision,
    DateTime ApprovalExpiresUtc);

public sealed record AdvertisingActionExecutionReceipt(
    Guid Id,
    string State,
    string Revision,
    JsonElement? ProviderReceipt,
    string? ErrorCode,
    string? ErrorMessage);

public static class PromotionSourceKinds
{
    public const string WebsitePage = "website_page";
    public const string Service = "service";
    public const string Product = "product";
}

public sealed record PromotionProposalRequest(
    string SourceKind,
    string? SourceId,
    string? PagePath,
    string? Goal,
    long DailyBudgetMicros,
    string BiddingType = OpenAiAdsBiddingTypes.Clicks,
    IReadOnlyList<string>? ContextHints = null,
    IReadOnlyList<string>? Countries = null,
    IReadOnlyList<string>? Platforms = null,
    string Status = OpenAiAdsEntityStatuses.Paused,
    string? ConversionEventSettingId = null,
    string? SelectedCreativeKey = null);

public sealed record PromotionSourceOption(
    string SourceKind,
    string SourceId,
    string Label,
    string? PagePath = null,
    string? Detail = null);

public sealed record PromotionSourceSnapshot(
    string SourceKind,
    string SourceId,
    string DisplayName,
    string? Description,
    string LandingUrl,
    string? ImageUrl,
    string? PriceLabel,
    string? BusinessName,
    string? BusinessType,
    Guid? WebsiteContentVersionId,
    long? WebsiteRevision,
    DateTime CapturedUtc);

public sealed record PromotionCreativeAlternative(
    string Key,
    string Name,
    string Title,
    string Body,
    string TargetUrl,
    string? ImageUrl,
    string? PriceLabel,
    IReadOnlyList<string> ContextHints);

public sealed record PromotionDraft(
    PromotionSourceSnapshot Source,
    string Objective,
    string BiddingType,
    long DailyBudgetMicros,
    string? ConversionEventSettingId,
    IReadOnlyList<PromotionCreativeAlternative> Alternatives,
    string SelectedAlternativeKey,
    AdvertisingMutationPlan Plan);
