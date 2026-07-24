namespace AgentPortal.Models;

public sealed class FounderSubscribersQuery
{
    public string? Search { get; set; }
    public string? Status { get; set; }
    public string? AgentId { get; set; }
    public int? MonthlyAmountCents { get; set; }
    public DateTime? CreatedFromUtc { get; set; }
    public DateTime? CreatedToUtc { get; set; }
    public DateTime? RenewalFromUtc { get; set; }
    public DateTime? RenewalToUtc { get; set; }
    public bool ShowCancelled { get; set; }
    public string? Sort { get; set; }
    public int GroupPage { get; set; } = 1;
}

public sealed class FounderSubscribersDashboardVm
{
    public FounderSubscriberMetricsVm Metrics { get; init; } = new();
    public FounderSubscribersQuery Query { get; init; } = new();
    public IReadOnlyList<FounderPricingGroupVm> PricingGroups { get; init; } = Array.Empty<FounderPricingGroupVm>();
    public IReadOnlyList<FounderDistributionVm> RevenueByAmount { get; init; } = Array.Empty<FounderDistributionVm>();
    public IReadOnlyList<FounderDistributionVm> SubscribersByAgent { get; init; } = Array.Empty<FounderDistributionVm>();
    public IReadOnlyList<FounderDistributionVm> SubscribersByStatus { get; init; } = Array.Empty<FounderDistributionVm>();
    public IReadOnlyList<FounderDistributionVm> RevenueByAgent { get; init; } = Array.Empty<FounderDistributionVm>();
    public IReadOnlyList<FounderSelectOptionVm> Agents { get; init; } = Array.Empty<FounderSelectOptionVm>();
    public IReadOnlyList<FounderSelectOptionVm> Amounts { get; init; } = Array.Empty<FounderSelectOptionVm>();
    public int GroupPage { get; init; }
    public int GroupPageCount { get; init; }
    public int TotalGroupCount { get; init; }
}

public sealed class FounderSubscriberMetricsVm
{
    public DateTime LastUpdatedUtc { get; init; }
    public string TotalActiveRevenue { get; init; } = "—";
    public int ActiveSubscribers { get; init; }
    public int CancelledSubscribers { get; init; }
    public string MonthlyRecurringRevenue { get; init; } = "—";
    public string AverageRevenuePerSubscriber { get; init; } = "—";
    public string HighestSubscription { get; init; } = "—";
    public string LowestSubscription { get; init; } = "—";
    public int NewSubscribersThisMonth { get; init; }
    public int LostSubscribersThisMonth { get; init; }
    public string RetentionRate { get; init; } = "—";
    public string ChurnRate { get; init; } = "—";
    public string CollectionSuccessRate { get; init; } = "—";
    public string PaymentFailureRate { get; init; } = "—";
    public string LostRevenue { get; init; } = "—";
    public string RecoveredRevenue { get; init; } = "—";
    public string ExpansionRevenue { get; init; } = "Amount history unavailable";
    public string ContractionRevenue { get; init; } = "Amount history unavailable";
    public string AverageSubscription { get; init; } = "—";
    public string MedianSubscription { get; init; } = "—";
    public string LargestSubscription { get; init; } = "—";
    public string SmallestSubscription { get; init; } = "—";
}

public sealed class FounderPricingGroupVm
{
    public int MonthlyAmountCents { get; init; }
    public string Currency { get; init; } = "USD";
    public string AmountLabel { get; init; } = "—";
    public int SubscriberCount { get; init; }
    public string MonthlyRevenue { get; init; } = "—";
    public string AnnualRevenue { get; init; } = "—";
    public string RevenueShare { get; init; } = "—";
    public string SubscriberShare { get; init; } = "—";
    public string NewestSubscriber { get; init; } = "—";
    public string OldestSubscriber { get; init; } = "—";
    public string AverageLifetime { get; init; } = "—";
    public string CollectionSuccessRate { get; init; } = "—";
    public int CancelledCount { get; init; }
    public int GrowthSinceLastMonth { get; init; }
    public int NetGainLoss { get; init; }
}

public sealed class FounderSubscriberGroupDetailVm
{
    public IReadOnlyList<FounderSubscriberRowVm> Subscribers { get; init; } = Array.Empty<FounderSubscriberRowVm>();
    public int Page { get; init; }
    public int PageCount { get; init; }
    public int TotalCount { get; init; }
}

public sealed class FounderSubscriberRowVm
{
    public string RecordId { get; init; } = string.Empty;
    public Guid ClientProfileId { get; init; }
    public string ClientUserId { get; init; } = string.Empty;
    public string Customer { get; init; } = "Client";
    public string AgentOwner { get; init; } = "Unassigned";
    public string AgentUserId { get; init; } = string.Empty;
    public string SubscriptionAmount { get; init; } = "—";
    public string Status { get; init; } = "Pending Activation";
    public string StatusTone { get; init; } = "pending";
    public DateTime? StartedUtc { get; init; }
    public DateTime? RenewalUtc { get; init; }
    public string LifetimeValue { get; init; } = "—";
    public string MonthsActive { get; init; } = "—";
    public DateTime? LastSuccessfulPaymentUtc { get; init; }
    public DateTime? LastFailedPaymentUtc { get; init; }
    public string PaymentProvider { get; init; } = "—";
    public string CurrentPlan { get; init; } = "—";
    public string ClientAppStatus { get; init; } = "Not granted";
    public string Email { get; init; } = "—";
    public string Phone { get; init; } = "—";
    public DateTime? CancelledUtc { get; init; }
    public string CancellationReason { get; init; } = "Not recorded";
    public string LastAgent { get; init; } = "Unassigned";
    public string MarketingEligibility { get; init; } = "Not recorded";
    public string DaysSinceCancellation { get; init; } = "—";
    public string PotentialWinBack { get; init; } = "—";
}

public sealed class FounderDistributionVm
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = "0";
    public string Detail { get; init; } = string.Empty;
}

public sealed class FounderSelectOptionVm
{
    public string Value { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
}
