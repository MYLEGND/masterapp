using Shared.Analytics;

namespace Infrastructure.Analytics;

public interface IMarketingManagerService
{
    Task<MarketingManagerPlan> PlanAsync(
        MarketingOwnerScope owner,
        ScopeContext analyticsScope,
        TimeRangeRequest range,
        MarketingManagerGoalRequest request,
        CancellationToken ct = default);

    Task<AdvertisingActionProposalSnapshot> ProposeChatGptPromotionAsync(
        MarketingOwnerScope owner,
        PromotionProposalRequest request,
        string actorUserId,
        CancellationToken ct = default);
}

public sealed class MarketingManagerService(
    IAnalyticsQueryService analytics,
    IAdvertisingCommandCenterService advertising,
    IUnifiedMarketingPerformanceService performance) : IMarketingManagerService
{
    public async Task<MarketingManagerPlan> PlanAsync(
        MarketingOwnerScope owner,
        ScopeContext analyticsScope,
        TimeRangeRequest range,
        MarketingManagerGoalRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(analyticsScope);
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(request);

        var goal = Clean(request.Goal, 1000);
        var targetIncrement = request.TargetIncrement is > 0 ? request.TargetIncrement.Value : ParseTargetIncrement(goal);

        var summaryTask = analytics.GetSummaryAsync(range, analyticsScope, TrafficType.All);
        var funnelTask = analytics.GetQuoteFunnelAsync(range, analyticsScope, TrafficType.All);
        var trafficTask = analytics.GetTrafficAsync(range, analyticsScope, TrafficType.All);
        var healthTask = analytics.GetMarketingHealthAsync(range, analyticsScope, TrafficType.All);
        var performanceTask = performance.GetAsync(owner, analyticsScope, range, ct);
        var advertisingTask = advertising.GetAsync(owner, ct);

        await Task.WhenAll(summaryTask, funnelTask, trafficTask, healthTask, performanceTask, advertisingTask);

        var summary = await summaryTask;
        var funnel = await funnelTask;
        var traffic = await trafficTask;
        var health = await healthTask;
        var channel = await performanceTask;
        var command = await advertisingTask;

        var evidence = new List<MarketingManagerEvidence>
        {
            new("goal", "Growth goal", goal, "operator"),
            new("verified_leads", "Verified leads", summary.VerifiedLeads.ToString(), "canonical analytics"),
            new("sessions", "Sessions", summary.Sessions.ToString(), "canonical analytics"),
            new("session_conversion", "Session conversion", $"{summary.SessionConversionRate:0.##}%", "canonical analytics"),
            new("quote_starts", "Quote starts", funnel.QuoteStarts.ToString(), "canonical analytics"),
            new("quote_submits", "Quote submits", funnel.QuoteFormSubmits.ToString(), "canonical analytics"),
            new("top_source", "Top source", summary.TopSource ?? "No source in range", "canonical analytics"),
            new("top_campaign", "Top campaign", summary.TopCampaign ?? "No campaign in range", "canonical analytics"),
            new("tracking_errors", "Client tracking errors", health.ClientTrackingErrors.ToString(), "canonical analytics"),
            new("chatgpt_spend", "ChatGPT Ads spend", Money(channel.Channels.FirstOrDefault(x => x.Channel == MarketingChannels.ChatGptAds)?.Spend ?? 0), "OpenAI Ads + canonical attribution"),
            new("meta_spend", "Meta Ads spend", Money(channel.Channels.FirstOrDefault(x => x.Channel == MarketingChannels.MetaAds)?.Spend ?? 0), "Meta Ads + canonical attribution")
        };

        if (targetIncrement > 0)
            evidence.Add(new("target_increment", "Requested incremental outcomes", targetIncrement.ToString(), "operator"));

        var recommendations = BuildRecommendations(
            targetIncrement,
            summary,
            funnel,
            health,
            channel,
            command,
            request.MonthlyBudget);

        var pending = command.Proposals.Count(x => string.Equals(x.State, AdvertisingActionStates.Proposed, StringComparison.Ordinal));
        var approved = command.Proposals.Count(x => string.Equals(x.State, AdvertisingActionStates.Approved, StringComparison.Ordinal));

        var guardrails = new List<string>
        {
            "The Marketing Manager may prepare and propose changes, but provider mutations execute only through the existing exact-action advertising authorization ledger.",
            "No campaign, budget, status, landing-page, or CRM mutation is executed from planning output alone.",
            "ChatGPT Ads downstream outcomes use canonical oppref lineage; unproven campaign-level revenue attribution is not inferred.",
            "Scope is server-resolved. Cross-agent and cross-business data are never combined into a single owner plan."
        };

        if (health.ClientTrackingErrors > 0 || health.UnknownAttributedLeads > 0)
            guardrails.Add("Tracking/data-quality issues are present in this range; scaling recommendations should remain conservative until the affected signals are verified.");

        return new MarketingManagerPlan(
            owner,
            goal,
            DateTime.UtcNow,
            command.Sources,
            evidence,
            recommendations,
            new AdvertisingCommandCenterSummary(
                command.Connection.Connected,
                command.Connection.HasManagementCredential,
                CountProviderRows(command.Campaigns),
                pending,
                approved),
            channel,
            guardrails);
    }

    public Task<AdvertisingActionProposalSnapshot> ProposeChatGptPromotionAsync(
        MarketingOwnerScope owner,
        PromotionProposalRequest request,
        string actorUserId,
        CancellationToken ct = default) =>
        advertising.ProposePromotionAsync(owner, request, actorUserId, ct);

    private static IReadOnlyList<MarketingManagerRecommendation> BuildRecommendations(
        int targetIncrement,
        SummaryKpiDto summary,
        QuoteFunnelDto funnel,
        MarketingHealthDto health,
        UnifiedChannelPerformanceSnapshot performance,
        AdvertisingCommandCenterSnapshot command,
        decimal? monthlyBudget)
    {
        var result = new List<MarketingManagerRecommendation>();
        var priority = 1;

        if (health.ClientTrackingErrors > 0 || health.UnknownAttributedLeads > 0)
        {
            result.Add(new(
                priority++,
                "tracking",
                "Resolve the current analytics/attribution warnings before aggressive paid scaling.",
                $"The selected range contains {health.ClientTrackingErrors} client tracking errors and {health.UnknownAttributedLeads} unknown-attributed leads.",
                "Protects budget decisions from incomplete or misattributed conversion data.",
                false));
        }

        var sessionsPerLead = summary.VerifiedLeads > 0
            ? (decimal)summary.Sessions / summary.VerifiedLeads
            : 0m;

        if (targetIncrement > 0 && sessionsPerLead > 0)
        {
            var incrementalSessions = (long)Math.Ceiling(targetIncrement * sessionsPerLead);
            result.Add(new(
                priority++,
                "funnel",
                $"Plan for approximately {incrementalSessions:N0} additional qualified sessions at the current observed lead rate, then reduce that requirement through conversion improvements.",
                $"Current observed ratio is about {sessionsPerLead:0.##} sessions per verified lead.",
                "Connects the requested outcome target to a measurable traffic and conversion requirement.",
                false));
        }

        if (funnel.QuoteStarts > 0 && funnel.QuoteFormSubmits < funnel.QuoteStarts)
        {
            result.Add(new(
                priority++,
                "landing_page",
                "Prioritize the highest-drop-off quote/estimate path before sending materially more paid traffic.",
                $"{funnel.QuoteStarts:N0} quote starts produced {funnel.QuoteFormSubmits:N0} successful submits in the selected range.",
                "Raises conversion yield from the traffic already being acquired.",
                true,
                ProposalKind: "landing_page_change"));
        }

        var chatGpt = performance.Channels.FirstOrDefault(x => x.Channel == MarketingChannels.ChatGptAds);
        if (command.Connection.Connected && command.Connection.HasManagementCredential)
        {
            var budgetText = monthlyBudget is > 0 ? $" within the requested {Money(monthlyBudget.Value)} monthly budget" : string.Empty;
            result.Add(new(
                priority++,
                "advertising",
                "Prepare a scoped ChatGPT Ads promotion from an existing published service, product, or landing page" + budgetText + ".",
                chatGpt is null
                    ? "ChatGPT Ads is connected but no comparable delivery row is available in this range."
                    : $"Current ChatGPT Ads spend is {Money(chatGpt.Spend)} with {chatGpt.Leads:N0} canonical oppref-attributed leads.",
                "Creates a reviewable exact-action proposal without bypassing approval.",
                true,
                MarketingChannels.ChatGptAds,
                "promote_this"));
        }
        else
        {
            result.Add(new(
                priority++,
                "advertising",
                "Complete the scoped ChatGPT Ads management connection before proposing provider changes.",
                "The canonical advertising command center does not currently report management-ready credentials for this owner.",
                "Keeps provider execution fail-closed.",
                false,
                MarketingChannels.ChatGptAds));
        }

        var meta = performance.Channels.FirstOrDefault(x => x.Channel == MarketingChannels.MetaAds);
        if (meta is not null && meta.Spend > 0)
        {
            result.Add(new(
                priority++,
                "advertising",
                "Review Meta budget allocation against downstream qualified leads, appointments, customers, revenue, and ROAS before increasing total spend.",
                $"Meta spend is {Money(meta.Spend)} with {meta.QualifiedLeads:N0} qualified leads, {meta.Appointments:N0} appointments, and {Money(meta.Revenue)} attributed revenue.",
                "Directs budget toward channels producing business outcomes rather than clicks alone.",
                true,
                MarketingChannels.MetaAds,
                "meta_budget_review"));
        }

        result.Add(new(
            priority,
            "crm",
            "Use the canonical lead and appointment outcomes as the follow-up denominator for the growth goal.",
            $"The selected range contains {summary.VerifiedLeads:N0} verified leads.",
            "Prevents marketing success from being measured only at the form-submit stage.",
            false));

        return result.Take(8).ToList();
    }

    private static int ParseTargetIncrement(string goal)
    {
        var digits = new string(goal.TakeWhile(ch => !char.IsDigit(ch)).Any()
            ? goal.SkipWhile(ch => !char.IsDigit(ch)).TakeWhile(char.IsDigit).ToArray()
            : goal.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) && value > 0 ? value : 0;
    }

    private static int CountProviderRows(System.Text.Json.JsonElement payload) =>
        payload.ValueKind == System.Text.Json.JsonValueKind.Object &&
        payload.TryGetProperty("data", out var data) &&
        data.ValueKind == System.Text.Json.JsonValueKind.Array
            ? data.GetArrayLength()
            : 0;

    private static string Clean(string? value, int max)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("A marketing goal is required.");
        if (text.Length > max || text.Any(char.IsControl)) throw new ArgumentException("Marketing goal is invalid.");
        return text;
    }

    private static string Money(decimal value) => "$" + value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}
