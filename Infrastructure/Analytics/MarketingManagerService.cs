using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;
using Shared.Crm;

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

    Task<LeadPrioritySnapshot> PrioritizeLeadsAsync(
        MarketingOwnerScope owner,
        ScopeContext analyticsScope,
        TimeRangeRequest range,
        int take = 25,
        CancellationToken ct = default);
}

public sealed class MarketingManagerService(
    IAdvertisingCommandCenterService advertising,
    WebsiteAnalyticsAiDataBuilder context,
    MasterAppDbContext db) : IMarketingManagerService
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

        var safeContext = await context.BuildAsync(range, analyticsScope, range.Label, owner.OwnerType,
            "All Traffic", TrafficType.All, ct, owner);
        var summary = new SummaryKpiDto { VerifiedLeads = safeContext.VerifiedLeads, Sessions = safeContext.Sessions,
            SessionConversionRate = safeContext.SessionConversionRate, TopSource = safeContext.TopSource, TopCampaign = safeContext.TopCampaign };
        var funnel = new QuoteFunnelDto { QuoteStarts = safeContext.QuoteStarts, QuoteFormSubmits = safeContext.QuoteFormSubmits };
        var health = new MarketingHealthDto { ClientTrackingErrors = safeContext.MarketingHealth?.ClientTrackingErrors ?? 0,
            UnknownAttributedLeads = safeContext.MarketingHealth?.UnknownAttributedLeads ?? 0 };
        var gpt = safeContext.Channels.FirstOrDefault(x => x.Channel == MarketingChannels.ChatGptAds);
        var channel = new UnifiedChannelPerformanceSnapshot(owner, range.FromUtc, range.ToUtc, safeContext.GeneratedUtc,
            [], new CanonicalOutcomeTotals(gpt?.Leads ?? 0, gpt?.QualifiedLeads ?? 0, gpt?.Appointments ?? 0, gpt?.Customers ?? 0, gpt?.Revenue ?? 0),
            safeContext.Channels.Select(x => new ChannelPerformanceRow(x.Channel, x.Spend, x.Impressions, x.Clicks,
                x.Leads, x.QualifiedLeads, x.Appointments, x.Customers, x.Revenue, x.Roas, x.AttributionConfidence,
                "Canonical outcomes; provider receipt does not assign campaign credit")).ToList(), safeContext.ChannelCoverageNotes);
        var command = await advertising.GetAsync(owner, ct);

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

        if (safeContext.Warnings.Count > 0 || safeContext.ChannelCoverageNotes.Count > 0)
            recommendations = new[] { new MarketingManagerRecommendation(0, "data_quality",
                "Resolve the reported coverage limitations before changing paid budgets.",
                "One or more analytics or provider modules reported limited data.",
                "Avoids treating missing metrics as observed zero.", false) }.Concat(recommendations).ToList();

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
            guardrails, safeContext);
    }

    public async Task<LeadPrioritySnapshot> PrioritizeLeadsAsync(
        MarketingOwnerScope owner,
        ScopeContext analyticsScope,
        TimeRangeRequest range,
        int take = 25,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(analyticsScope);
        ArgumentNullException.ThrowIfNull(range);
        take = Math.Clamp(take, 1, 100);

        var safeContext = await context.BuildAsync(
            range, analyticsScope, range.Label, owner.OwnerType,
            "All Traffic", TrafficType.All, ct, owner);
        var calibrations = (safeContext.OutcomeCalibration?.Signals ?? [])
            .ToDictionary(x => x.Signal, StringComparer.OrdinalIgnoreCase);

        IQueryable<Domain.Entities.WorkstationLeadProfile> leadQuery = db.WorkstationLeadProfiles.AsNoTracking();
        if (owner.CommerceBusinessId is { } businessId)
        {
            leadQuery = leadQuery.Where(x => x.CommerceBusinessId == businessId);
        }
        else if (owner.AgentTrackingProfileId is { } trackingId)
        {
            var agentUserId = await db.AgentTrackingProfiles.AsNoTracking()
                .Where(x => x.Id == trackingId)
                .Select(x => x.AgentUserId)
                .SingleOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(agentUserId))
                return EmptyPriority(owner, "The selected agent owner could not be resolved.");
            leadQuery = leadQuery.Where(x =>
                x.CommerceBusinessId == null && x.AgentUserId == agentUserId);
        }
        else
        {
            return EmptyPriority(owner,
                "Lead priority requires one exact agent or business owner. Global Founder data is not merged into one sales queue.");
        }

        var leads = await leadQuery
            .Where(x => x.CrmStatus != "Converted" && x.CrmStage != "PolicyPlaced")
            .OrderByDescending(x => x.UpdatedUtc)
            .Take(2000)
            .ToListAsync(ct);
        if (leads.Count == 0)
            return EmptyPriority(owner, "No active CRM leads exist for this exact owner.");

        var leadIds = leads.Select(x => x.LeadId).ToArray();
        var intakeRows = await db.WebsiteLeadIntakeLinks.AsNoTracking()
            .Where(x => leadIds.Contains(x.WorkstationLeadId))
            .OrderByDescending(x => x.SubmittedUtc)
            .ThenByDescending(x => x.CapturedUtc)
            .ToListAsync(ct);
        var intakeByLead = intakeRows
            .GroupBy(x => x.WorkstationLeadId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var visitorIds = intakeRows.Select(x => x.VisitorId)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Cast<string>().ToArray();
        var sessionIds = intakeRows.Select(x => x.SessionId)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Cast<string>().ToArray();

        var signalEvents = await db.AnalyticsEvents.AsNoTracking()
            .Where(x => x.EventUtc >= range.FromUtc && x.EventUtc < range.ToUtc &&
                (x.EventType == "HighIntentLeadSignal" || x.EventType == "LeadReadySignal") &&
                ((x.VisitorId != null && visitorIds.Contains(x.VisitorId)) ||
                 (x.SessionId != null && sessionIds.Contains(x.SessionId))))
            .Select(x => new { x.EventType, x.VisitorId, x.SessionId, x.EventUtc })
            .ToListAsync(ct);

        var totalPaidCustomers = safeContext.Channels.Sum(x => x.Customers);
        var totalRevenue = safeContext.Channels.Sum(x => x.Revenue);
        decimal? averagePaidValue = totalPaidCustomers > 0
            ? Math.Round(totalRevenue / totalPaidCustomers, 2)
            : null;
        var now = DateTime.UtcNow;

        var rows = new List<LeadPriorityRow>(leads.Count);
        foreach (var lead in leads)
        {
            intakeByLead.TryGetValue(lead.LeadId, out var intake);
            var matchingSignals = signalEvents.Where(x =>
                    (!string.IsNullOrWhiteSpace(intake?.VisitorId) &&
                     string.Equals(x.VisitorId, intake.VisitorId, StringComparison.Ordinal)) ||
                    (!string.IsNullOrWhiteSpace(intake?.SessionId) &&
                     string.Equals(x.SessionId, intake.SessionId, StringComparison.Ordinal)))
                .ToArray();

            var signal = matchingSignals.Any(x =>
                    string.Equals(x.EventType, "LeadReadySignal", StringComparison.OrdinalIgnoreCase))
                ? "LeadReadySignal"
                : matchingSignals.Any(x =>
                    string.Equals(x.EventType, "HighIntentLeadSignal", StringComparison.OrdinalIgnoreCase))
                    ? "HighIntentLeadSignal"
                    : "NoCalibratedIntentSignal";

            calibrations.TryGetValue(signal, out var calibration);
            var likelihood = ProgressRateForStage(lead.CrmStage, calibration);
            decimal? expectedValue = averagePaidValue.HasValue && calibration is not null
                ? Math.Round(averagePaidValue.Value * calibration.PaidRate / 100m, 2)
                : null;

            var meta = ClientCrmMetaSerializer.Deserialize(lead.CrmNotes);
            var recencyMultiplier = RecencyMultiplier(now - lead.UpdatedUtc);
            var urgencyMultiplier = UrgencyMultiplier(meta.CrmPriority, meta.CrmNextDate, now);
            var evidenceBase = expectedValue is > 0m
                ? expectedValue.Value * Math.Max(likelihood / 100m, 0.05m)
                : Math.Max(likelihood, 1m);
            var priorityIndex = Math.Round(evidenceBase * recencyMultiplier * urgencyMultiplier, 2);
            var band = priorityIndex >= (averagePaidValue ?? 100m) * 0.25m ? "High"
                : priorityIndex >= (averagePaidValue ?? 100m) * 0.10m ? "Medium"
                : "Normal";

            rows.Add(new LeadPriorityRow(
                lead.LeadId,
                lead.CrmStage,
                intake?.InterestType ?? intake?.ProductType ?? lead.OriginalLeadType,
                signal,
                likelihood,
                expectedValue,
                priorityIndex,
                band,
                lead.UpdatedUtc,
                meta.CrmNextDate,
                meta.CrmPriority ?? "Normal",
                BuildPriorityReason(signal, likelihood, expectedValue, recencyMultiplier, urgencyMultiplier)));
        }

        return new LeadPrioritySnapshot(
            owner,
            now,
            safeContext.OutcomeCalibration?.LearningScopeNote ??
                "No calibrated browser-intent evidence is available in the selected range.",
            averagePaidValue,
            rows.OrderByDescending(x => x.PriorityIndex)
                .ThenByDescending(x => x.UpdatedUtc)
                .Take(take)
                .ToList(),
            [
                "Priority is advisory only; CRM stage and canonical server outcomes remain the source of truth.",
                "No name, email, phone, address, age, gender, health, or other sensitive personal attribute is used in this ranking.",
                "Behavioral intent remains observational. Canonical downstream outcomes are the learning labels.",
                "Advertising decisions must use aggregate owner-scoped evidence, never an individual lead's priority."
            ]);
    }

    private static LeadPrioritySnapshot EmptyPriority(MarketingOwnerScope owner, string reason) =>
        new(owner, DateTime.UtcNow, reason, null, [], [reason]);

    private static decimal ProgressRateForStage(
        string? stage,
        SignalOutcomeCalibrationAiRow? calibration)
    {
        if (calibration is null) return 0m;
        return stage?.Trim().ToLowerInvariant() switch
        {
            "qualified" => calibration.AppointmentRate,
            "booked" or "meetingscheduled" => calibration.ApplicationRate,
            "needsdocs" or "applicationstarted" or "followup" => calibration.IssuedRate,
            _ => calibration.QualifiedRate
        };
    }

    private static decimal RecencyMultiplier(TimeSpan age) =>
        age.TotalDays <= 1 ? 1.25m :
        age.TotalDays <= 3 ? 1.15m :
        age.TotalDays <= 7 ? 1.05m :
        age.TotalDays <= 30 ? 1.00m : 0.85m;

    private static decimal UrgencyMultiplier(string? priority, DateTime? nextActionDate, DateTime now)
    {
        var priorityMultiplier = priority?.Trim().ToLowerInvariant() switch
        {
            "urgent" => 1.25m,
            "high" => 1.15m,
            "low" => 0.85m,
            _ => 1m
        };
        var dueMultiplier = nextActionDate.HasValue && nextActionDate.Value.Date <= now.Date ? 1.15m : 1m;
        return priorityMultiplier * dueMultiplier;
    }

    private static string BuildPriorityReason(
        string signal,
        decimal likelihood,
        decimal? expectedValue,
        decimal recencyMultiplier,
        decimal urgencyMultiplier) =>
        $"{signal}; observed next-outcome likelihood {likelihood:0.##}%; " +
        (expectedValue.HasValue ? $"historical expected downstream value {Money(expectedValue)}; " : "paid-value history unavailable; ") +
        $"recency x{recencyMultiplier:0.##}; CRM urgency x{urgencyMultiplier:0.##}.";

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

    private static string Money(decimal? value) => value.HasValue ? "$" + value.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "unavailable";
}
