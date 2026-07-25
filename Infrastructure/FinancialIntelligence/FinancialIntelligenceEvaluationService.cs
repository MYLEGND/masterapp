using Domain.Entities.FinancialIntelligence;
using Domain.FinancialIntelligence;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shared.Auth;
using Shared.Finance;

namespace Infrastructure.FinancialIntelligence;

/// <summary>
/// Evaluates a single already-authorized client with deterministic rules. The
/// service owns idempotent persistence and never writes source financial data.
/// </summary>
public sealed class FinancialIntelligenceEvaluationService : IFinancialIntelligenceEvaluationService
{
    private static readonly HashSet<string> ValidFeedbackTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        FinancialFindingFeedbackTypes.Viewed,
        FinancialFindingFeedbackTypes.Helpful,
        FinancialFindingFeedbackTypes.NotHelpful,
        FinancialFindingFeedbackTypes.Accepted,
        FinancialFindingFeedbackTypes.Deferred,
        FinancialFindingFeedbackTypes.Dismissed,
        FinancialFindingFeedbackTypes.AgentReviewed,
        FinancialFindingFeedbackTypes.AgentContactedClient,
        FinancialFindingFeedbackTypes.ActionStarted,
        FinancialFindingFeedbackTypes.Completed,
        FinancialFindingFeedbackTypes.UnableToVerify
    };

    private readonly MasterAppDbContext _db;
    private readonly IReadOnlyList<IFinancialIntelligenceRule> _rules;
    private readonly ILogger<FinancialIntelligenceEvaluationService> _logger;

    public FinancialIntelligenceEvaluationService(
        MasterAppDbContext db,
        IEnumerable<IFinancialIntelligenceRule> rules,
        ILogger<FinancialIntelligenceEvaluationService> logger)
    {
        _db = db;
        _rules = rules.OrderBy(rule => rule.Identifier, StringComparer.Ordinal).ToList();
        _logger = logger;
    }

    public async Task<FinancialIntelligenceSnapshot?> GetSnapshotAsync(
        FinancialIntelligenceActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!await CanAccessClientAsync(actor, cancellationToken))
            return null;

        var profile = await _db.ClientFinancialIntelligenceProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.ClientProfileId == actor.ClientProfileId, cancellationToken);

        if (profile == null)
        {
            return EmptySnapshot(actor.ClientProfileId);
        }

        return await BuildSnapshotAsync(profile, actor.ActorType, cancellationToken);
    }

    public async Task<FinancialIntelligenceEvaluationResult> EvaluateAsync(
        FinancialIntelligenceActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!await CanAccessClientAsync(actor, cancellationToken))
            return new(false, "Unauthorized", "You are not authorized to evaluate this client.");

        try
        {
            var now = DateTime.UtcNow;
            var profile = await _db.ClientFinancialIntelligenceProfiles
                .SingleOrDefaultAsync(x => x.ClientProfileId == actor.ClientProfileId, cancellationToken);

            if (profile == null)
            {
                profile = new ClientFinancialIntelligenceProfile
                {
                    ClientProfileId = actor.ClientProfileId,
                    CreatedUtc = now,
                    UpdatedUtc = now
                };
                _db.ClientFinancialIntelligenceProfiles.Add(profile);
            }

            var connections = await _db.FinancialDataConnections
                .AsNoTracking()
                .Where(x => x.ClientProfileId == actor.ClientProfileId)
                .ToListAsync(cancellationToken);
            var recurringStreams = await _db.RecurringFinancialStreams
                .AsNoTracking()
                .Where(x => x.ClientProfileId == actor.ClientProfileId)
                .ToListAsync(cancellationToken);
            var importedAccounts = await _db.ImportedFinancialAccounts
                .AsNoTracking()
                .Where(x => x.ClientProfileId == actor.ClientProfileId)
                .ToListAsync(cancellationToken);
            var expenseLensLinks = await _db.ExpenseLensStreamLinks
                .AsNoTracking()
                .Where(x => x.ClientProfileId == actor.ClientProfileId)
                .ToListAsync(cancellationToken);
            var livingBalanceSheet = await _db.FinanceToolStates
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.ClientProfileId == actor.ClientProfileId &&
                         x.ToolId == LegendLivingBalanceSheetConstants.ToolId,
                    cancellationToken);

            var context = new FinancialIntelligenceRuleContext(
                actor.ClientProfileId,
                now,
                connections,
                importedAccounts,
                recurringStreams,
                expenseLensLinks,
                livingBalanceSheet);
            var results = _rules.Select(rule => (Rule: rule, Result: rule.Evaluate(context))).ToList();
            profile.DataCompletenessScore = CalculateDataCompleteness(connections, recurringStreams, livingBalanceSheet);

            var observations = await _db.FinancialObservations
                .Where(x => x.ClientProfileId == actor.ClientProfileId)
                .ToListAsync(cancellationToken);
            var findings = await _db.FinancialFindings
                .Where(x => x.ClientProfileId == actor.ClientProfileId)
                .ToListAsync(cancellationToken);
            var existingObservationByKey = observations.ToDictionary(x => x.ObservationKey, StringComparer.Ordinal);
            var existingFindingByKey = findings.ToDictionary(x => x.FindingKey, StringComparer.Ordinal);
            var historicalFeedbackByRule = await LoadFeedbackByRuleAsync(actor.ClientProfileId, cancellationToken);
            var observationByKey = new Dictionary<string, FinancialObservation>(existingObservationByKey, StringComparer.Ordinal);

            foreach (var (rule, result) in results)
            {
                if (!result.CanReconcile)
                    continue;

                var activeObservationKeys = result.Observations
                    .Select(x => x.ObservationKey)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var staleObservation in observations.Where(x =>
                             x.RuleIdentifier == rule.Identifier &&
                             x.Status == FinancialFindingStatuses.Active &&
                             !activeObservationKeys.Contains(x.ObservationKey)))
                {
                    staleObservation.Status = "Superseded";
                    staleObservation.SupersededUtc = now;
                    staleObservation.UpdatedUtc = now;
                }

                foreach (var candidate in result.Observations)
                {
                    if (!observationByKey.TryGetValue(candidate.ObservationKey, out var observation))
                    {
                        observation = new FinancialObservation
                        {
                            ClientProfileId = actor.ClientProfileId,
                            ObservationKey = candidate.ObservationKey,
                            RuleIdentifier = rule.Identifier,
                            RuleVersion = rule.Version,
                            CreatedUtc = now
                        };
                        _db.FinancialObservations.Add(observation);
                        observations.Add(observation);
                        observationByKey.Add(candidate.ObservationKey, observation);
                    }

                    ApplyObservation(observation, candidate, rule, now);
                }

                var activeFindingKeys = result.Findings
                    .Select(x => x.FindingKey)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var staleFinding in findings.Where(x =>
                             x.RuleIdentifier == rule.Identifier &&
                             (x.Status == FinancialFindingStatuses.Active || x.Status == FinancialFindingStatuses.Deferred) &&
                             !activeFindingKeys.Contains(x.FindingKey)))
                {
                    staleFinding.Status = FinancialFindingStatuses.Resolved;
                    staleFinding.ResolvedUtc = now;
                    staleFinding.UpdatedUtc = now;
                }

                foreach (var candidate in result.Findings)
                {
                    if (!existingFindingByKey.TryGetValue(candidate.FindingKey, out var finding))
                    {
                        finding = new FinancialFinding
                        {
                            ClientProfileId = actor.ClientProfileId,
                            FindingKey = candidate.FindingKey,
                            RuleIdentifier = rule.Identifier,
                            RuleVersion = rule.Version,
                            FirstDetectedUtc = now,
                            CreatedUtc = now
                        };
                        _db.FinancialFindings.Add(finding);
                        findings.Add(finding);
                        existingFindingByKey.Add(candidate.FindingKey, finding);
                    }

                    historicalFeedbackByRule.TryGetValue(rule.Identifier, out var priorFeedback);
                    ApplyFinding(
                        finding,
                        candidate,
                        rule,
                        now,
                        profile.DataCompletenessScore,
                        priorFeedback ?? Array.Empty<string>());

                    await EnsureEvidenceLinksAsync(finding, candidate.ObservationKeys, observationByKey, cancellationToken);
                }
            }

            profile.BehavioralBaselineStatus = recurringStreams.Count > 0 ? "Available" : "NotEstablished";
            profile.PersonalizationMaturity = await GetPersonalizationMaturityAsync(actor.ClientProfileId, cancellationToken);
            profile.RecommendationResponseSummary = await BuildResponseSummaryAsync(actor.ClientProfileId, cancellationToken);
            profile.CurrentRiskSummary = BuildCategorySummary(findings, FinancialFindingCategories.Risk);
            profile.CurrentOpportunitySummary = BuildCategorySummary(findings, FinancialFindingCategories.Opportunity);
            profile.CurrentLeakageSummary = BuildCategorySummary(findings, FinancialFindingCategories.Leakage);
            profile.Status = "Ready";
            profile.EvaluationSequence++;
            profile.LastEvaluatedUtc = now;
            profile.UpdatedUtc = now;

            await _db.SaveChangesAsync(cancellationToken);
            var snapshot = await BuildSnapshotAsync(profile, actor.ActorType, cancellationToken);
            return new(true, null, "Financial intelligence was evaluated.", snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Financial intelligence evaluation failed for client profile {ClientProfileId}.", actor.ClientProfileId);
            return new(false, "EvaluationFailed", "Financial intelligence could not be evaluated right now.");
        }
    }

    public async Task<FinancialIntelligenceFeedbackResult> RecordFeedbackAsync(
        FinancialIntelligenceActor actor,
        FinancialIntelligenceFeedbackCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!await CanAccessClientAsync(actor, cancellationToken))
            return new(false, "Unauthorized", "You are not authorized to update this finding.");

        var feedbackType = (command.FeedbackType ?? string.Empty).Trim();
        if (!ValidFeedbackTypes.Contains(feedbackType) ||
            command.ReasonCode?.Length > 120 ||
            command.Note?.Length > 1000)
        {
            return new(false, "InvalidFeedback", "The selected finding response is not valid.");
        }

        var finding = await _db.FinancialFindings
            .SingleOrDefaultAsync(
                x => x.Id == command.FinancialFindingId && x.ClientProfileId == actor.ClientProfileId,
                cancellationToken);
        if (finding == null)
            return new(false, "FindingNotFound", "The requested finding was not found.");

        if (actor.ActorType == FinancialIntelligenceActorTypes.Client &&
            finding.RequiresAgentReview &&
            finding.AgentReviewedUtc == null)
        {
            return new(false, "FindingUnavailable", "This finding is awaiting agent review.");
        }

        if (actor.ActorType == FinancialIntelligenceActorTypes.Client &&
            feedbackType is FinancialFindingFeedbackTypes.AgentReviewed or FinancialFindingFeedbackTypes.AgentContactedClient)
        {
            return new(false, "InvalidFeedback", "Clients cannot record that finding response.");
        }

        if (actor.ActorType != FinancialIntelligenceActorTypes.Agent &&
            feedbackType is FinancialFindingFeedbackTypes.AgentReviewed or FinancialFindingFeedbackTypes.AgentContactedClient)
        {
            return new(false, "InvalidFeedback", "Only an authorized agent can record that finding response.");
        }

        var now = DateTime.UtcNow;
        var existingFeedbackTypes = await _db.FinancialFindingFeedback
            .AsNoTracking()
            .Where(x => x.ClientProfileId == actor.ClientProfileId)
            .Select(x => x.FeedbackType)
            .ToListAsync(cancellationToken);
        _db.FinancialFindingFeedback.Add(new FinancialFindingFeedback
        {
            FinancialFindingId = finding.Id,
            ClientProfileId = actor.ClientProfileId,
            ActorType = actor.ActorType,
            ActorUserId = Normalize(actor.UserId),
            FeedbackType = feedbackType,
            ReasonCode = string.IsNullOrWhiteSpace(command.ReasonCode) ? null : command.ReasonCode.Trim(),
            Note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim(),
            CreatedUtc = now
        });

        if (feedbackType == FinancialFindingFeedbackTypes.AgentReviewed)
        {
            finding.AgentReviewedUtc = now;
            finding.AgentReviewedByUserId = Normalize(actor.UserId);
        }
        else if (feedbackType == FinancialFindingFeedbackTypes.Deferred)
        {
            finding.Status = FinancialFindingStatuses.Deferred;
        }
        else if (feedbackType == FinancialFindingFeedbackTypes.Completed)
        {
            finding.Status = FinancialFindingStatuses.Completed;
        }
        else if (feedbackType == FinancialFindingFeedbackTypes.Dismissed &&
                 !string.Equals(finding.Urgency, "High", StringComparison.OrdinalIgnoreCase))
        {
            finding.Status = FinancialFindingStatuses.Dismissed;
        }

        finding.UpdatedUtc = now;
        var profile = await _db.ClientFinancialIntelligenceProfiles
            .SingleOrDefaultAsync(x => x.ClientProfileId == actor.ClientProfileId, cancellationToken);
        if (profile != null)
        {
            existingFeedbackTypes.Add(feedbackType);
            profile.RecommendationResponseSummary = BuildResponseSummary(existingFeedbackTypes);
            profile.PersonalizationMaturity = GetPersonalizationMaturity(existingFeedbackTypes.Count);
            profile.UpdatedUtc = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        var snapshot = profile == null
            ? EmptySnapshot(actor.ClientProfileId)
            : await BuildSnapshotAsync(profile, actor.ActorType, cancellationToken);
        return new(true, null, "Finding response recorded.", snapshot);
    }

    private async Task<bool> CanAccessClientAsync(
        FinancialIntelligenceActor actor,
        CancellationToken cancellationToken)
    {
        var userId = Normalize(actor.UserId);
        if (actor.ClientProfileId == Guid.Empty || string.IsNullOrWhiteSpace(userId))
            return false;

        if (string.Equals(actor.ActorType, FinancialIntelligenceActorTypes.Client, StringComparison.Ordinal))
        {
            return await _db.ClientProfiles
                .AsNoTracking()
                .AnyAsync(
                    x => x.Id == actor.ClientProfileId &&
                         (x.ClientUserId ?? string.Empty).ToLower() == userId,
                    cancellationToken);
        }

        if (!string.Equals(actor.ActorType, FinancialIntelligenceActorTypes.Agent, StringComparison.Ordinal))
            return false;

        var clientUserId = await _db.ClientProfiles
            .AsNoTracking()
            .Where(x => x.Id == actor.ClientProfileId)
            .Select(x => x.ClientUserId)
            .SingleOrDefaultAsync(cancellationToken);

        return !string.IsNullOrWhiteSpace(clientUserId) &&
               await _db.AgentOwnsClientAsync(
                   userId,
                   clientUserId,
                   actor.AgentUpn,
                   actor.AgentIdCandidates,
                   cancellationToken);
    }

    private static void ApplyObservation(
        FinancialObservation observation,
        FinancialIntelligenceObservationCandidate candidate,
        IFinancialIntelligenceRule rule,
        DateTime now)
    {
        observation.RuleIdentifier = rule.Identifier;
        observation.RuleVersion = rule.Version;
        observation.ObservationType = candidate.ObservationType;
        observation.SourceType = candidate.SourceType;
        observation.SourceReference = candidate.SourceReference;
        observation.PeriodStartUtc = candidate.PeriodStartUtc;
        observation.PeriodEndUtc = candidate.PeriodEndUtc;
        observation.NumericValue = candidate.NumericValue;
        observation.PreviousValue = candidate.PreviousValue;
        observation.Unit = candidate.Unit;
        observation.Confidence = ClampUnit(candidate.Confidence);
        observation.EvidenceSummary = candidate.EvidenceSummary;
        observation.Status = FinancialFindingStatuses.Active;
        observation.SupersededUtc = null;
        observation.UpdatedUtc = now;
    }

    private static void ApplyFinding(
        FinancialFinding finding,
        FinancialIntelligenceFindingCandidate candidate,
        IFinancialIntelligenceRule rule,
        DateTime now,
        decimal dataCompleteness,
        IReadOnlyList<string> priorFeedback)
    {
        finding.RuleIdentifier = rule.Identifier;
        finding.RuleVersion = rule.Version;
        finding.Category = candidate.Category;
        finding.FindingType = candidate.FindingType;
        finding.Title = candidate.Title;
        finding.Explanation = candidate.Explanation;
        finding.EstimatedImpact = candidate.EstimatedImpact;
        finding.ImpactUnit = candidate.ImpactUnit;
        finding.Confidence = ClampUnit(candidate.Confidence);
        finding.PriorityScore = FinancialIntelligencePrioritization.Calculate(
            candidate.EstimatedImpact,
            candidate.Urgency,
            finding.Confidence,
            dataCompleteness,
            candidate.Difficulty,
            priorFeedback);
        finding.Urgency = candidate.Urgency;
        finding.Difficulty = candidate.Difficulty;
        finding.EvidenceSummary = candidate.EvidenceSummary;
        finding.ClientFacingSummary = candidate.ClientFacingSummary;
        finding.AgentFacingSummary = candidate.AgentFacingSummary;
        finding.Disclaimer = candidate.Disclaimer;
        finding.RequiresAgentReview = candidate.RequiresAgentReview;
        finding.LastDetectedUtc = now;
        finding.ResolvedUtc = null;
        finding.UpdatedUtc = now;

        if (finding.Status == FinancialFindingStatuses.Resolved ||
            (finding.Status == FinancialFindingStatuses.Dismissed &&
             string.Equals(candidate.Urgency, "High", StringComparison.OrdinalIgnoreCase)))
        {
            finding.Status = FinancialFindingStatuses.Active;
        }
    }

    private async Task EnsureEvidenceLinksAsync(
        FinancialFinding finding,
        IReadOnlyList<string> observationKeys,
        IReadOnlyDictionary<string, FinancialObservation> observationByKey,
        CancellationToken cancellationToken)
    {
        if (observationKeys.Count == 0)
            return;

        var observationIds = observationKeys
            .Where(observationByKey.ContainsKey)
            .Select(key => observationByKey[key].Id)
            .ToArray();
        if (observationIds.Length == 0)
            return;

        var existingIds = await _db.FinancialFindingObservations
            .Where(x => x.FinancialFindingId == finding.Id && observationIds.Contains(x.FinancialObservationId))
            .Select(x => x.FinancialObservationId)
            .ToListAsync(cancellationToken);

        foreach (var observationId in observationIds.Except(existingIds))
        {
            _db.FinancialFindingObservations.Add(new FinancialFindingObservation
            {
                FinancialFindingId = finding.Id,
                FinancialObservationId = observationId
            });
        }
    }

    private async Task<Dictionary<string, IReadOnlyList<string>>> LoadFeedbackByRuleAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken)
    {
        var rows = await (
                from feedback in _db.FinancialFindingFeedback.AsNoTracking()
                join finding in _db.FinancialFindings.AsNoTracking() on feedback.FinancialFindingId equals finding.Id
                where feedback.ClientProfileId == clientProfileId
                select new { finding.RuleIdentifier, feedback.FeedbackType })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(x => x.RuleIdentifier, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(x => x.FeedbackType).ToList(),
                StringComparer.Ordinal);
    }

    private async Task<string> GetPersonalizationMaturityAsync(Guid clientProfileId, CancellationToken cancellationToken)
    {
        var feedbackCount = await _db.FinancialFindingFeedback
            .AsNoTracking()
            .CountAsync(x => x.ClientProfileId == clientProfileId, cancellationToken);
        return GetPersonalizationMaturity(feedbackCount);
    }

    private async Task<string> BuildResponseSummaryAsync(Guid clientProfileId, CancellationToken cancellationToken)
    {
        var feedback = await _db.FinancialFindingFeedback
            .AsNoTracking()
            .Where(x => x.ClientProfileId == clientProfileId)
            .Select(x => x.FeedbackType)
            .ToListAsync(cancellationToken);
        return BuildResponseSummary(feedback);
    }

    private static string BuildResponseSummary(IReadOnlyCollection<string> feedback)
    {
        if (feedback.Count == 0)
            return "No feedback recorded.";

        var positiveCount = feedback.Count(type => type is FinancialFindingFeedbackTypes.Helpful or FinancialFindingFeedbackTypes.Accepted or FinancialFindingFeedbackTypes.ActionStarted or FinancialFindingFeedbackTypes.Completed);
        var negativeCount = feedback.Count(type => type is FinancialFindingFeedbackTypes.NotHelpful or FinancialFindingFeedbackTypes.Dismissed);
        return $"{positiveCount} positive and {negativeCount} negative finding responses recorded.";
    }

    private static string GetPersonalizationMaturity(int feedbackCount) => feedbackCount switch
    {
        0 => "Initial",
        < 4 => "Emerging",
        _ => "Responsive"
    };

    private async Task<FinancialIntelligenceSnapshot> BuildSnapshotAsync(
        ClientFinancialIntelligenceProfile profile,
        string actorType,
        CancellationToken cancellationToken)
    {
        var query = _db.FinancialFindings
            .AsNoTracking()
            .Where(x => x.ClientProfileId == profile.ClientProfileId &&
                        (x.Status == FinancialFindingStatuses.Active || x.Status == FinancialFindingStatuses.Deferred));

        if (actorType == FinancialIntelligenceActorTypes.Client)
        {
            query = query.Where(x => !x.RequiresAgentReview || x.AgentReviewedUtc != null);
        }

        var findings = await query
            .OrderByDescending(x => x.PriorityScore)
            .ThenByDescending(x => x.LastDetectedUtc)
            .Select(x => new FinancialIntelligenceFindingView(
                x.Id,
                x.Category,
                x.FindingType,
                x.Title,
                actorType == FinancialIntelligenceActorTypes.Agent ? x.AgentFacingSummary : x.ClientFacingSummary,
                x.EstimatedImpact,
                x.ImpactUnit,
                x.Confidence,
                x.PriorityScore,
                x.Urgency,
                x.Difficulty,
                x.EvidenceSummary,
                x.Disclaimer,
                x.Status,
                x.RequiresAgentReview,
                x.AgentReviewedUtc != null,
                x.LastDetectedUtc))
            .ToListAsync(cancellationToken);

        return new FinancialIntelligenceSnapshot(
            profile.ClientProfileId,
            profile.Status,
            profile.DataCompletenessScore,
            profile.BehavioralBaselineStatus,
            profile.PersonalizationMaturity,
            profile.RecommendationResponseSummary,
            profile.CurrentRiskSummary,
            profile.CurrentOpportunitySummary,
            profile.CurrentLeakageSummary,
            profile.EvaluationSequence,
            profile.LastEvaluatedUtc,
            findings);
    }

    private static FinancialIntelligenceSnapshot EmptySnapshot(Guid clientProfileId) => new(
        clientProfileId,
        "NotEvaluated",
        0m,
        "NotEstablished",
        "Initial",
        "No feedback recorded.",
        "No current risk findings.",
        "No current opportunity findings.",
        "No current leakage findings.",
        0,
        null,
        Array.Empty<FinancialIntelligenceFindingView>());

    private static decimal CalculateDataCompleteness(
        IReadOnlyCollection<FinancialDataConnection> connections,
        IReadOnlyCollection<RecurringFinancialStream> recurringStreams,
        Domain.Entities.FinanceToolState? livingBalanceSheet)
    {
        var score = 0m;
        if (connections.Count > 0)
            score += 0.30m;
        if (connections.Any(x => x.LastSyncCompletedUtc != null))
            score += 0.20m;
        if (recurringStreams.Count > 0)
            score += 0.15m;
        if (HasLivingBalanceSheetCashFlow(livingBalanceSheet))
            score += 0.35m;
        return score;
    }

    private static bool HasLivingBalanceSheetCashFlow(Domain.Entities.FinanceToolState? state)
    {
        if (state == null || string.IsNullOrWhiteSpace(state.JsonState))
            return false;

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<LegendLivingBalanceSheetState>(
                state.JsonState,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                {
                    PropertyNameCaseInsensitive = true
                });
            return parsed?.CashFlow?.Earnings > 0m;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static string BuildCategorySummary(IReadOnlyCollection<FinancialFinding> findings, string category)
    {
        var count = findings.Count(x => x.Category == category &&
                                       (x.Status == FinancialFindingStatuses.Active || x.Status == FinancialFindingStatuses.Deferred));
        return count == 0
            ? $"No current {category.ToLowerInvariant()} findings."
            : $"{count} current {category.ToLowerInvariant()} finding{(count == 1 ? string.Empty : "s")} requires attention.";
    }

    private static decimal ClampUnit(decimal value) => Math.Clamp(value, 0m, 1m);

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}
