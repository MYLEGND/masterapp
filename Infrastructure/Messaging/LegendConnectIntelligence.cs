using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

internal sealed record LegendTranslationMemoryMatch(
    string Text,
    decimal Confidence,
    string Provenance,
    string QualityState);

internal sealed record LegendContextualTranslationSuggestion(string Text, decimal Confidence);

internal sealed record LegendProviderObservationResolution(
    bool Succeeded,
    string? ErrorCode,
    string Message,
    string? SourceLanguageCode = null,
    string? PairKey = null,
    Guid? RetiredTargetTextUnitId = null);

/// <summary>
/// Shared language-intelligence evaluator and quality-evidence authority. It
/// never owns a provider and never formulates a live translation. Provider
/// output remains an observation until an explicit human validation action
/// travels through this same server-owned authority.
/// </summary>
internal interface ILegendConnectTranslationIntelligence
{
    Task<LegendTranslationMemoryMatch?> TryGetTrustedExactMemoryAsync(
        string sourceLanguageCode,
        string targetLanguageCode,
        string text,
        CancellationToken cancellationToken = default);

    Task<LegendTranslationMemoryMatch?> TryGetReusableProviderObservationAsync(
        string sourceLanguageCode,
        string targetLanguageCode,
        string text,
        CancellationToken cancellationToken = default);

    Task<LegendContextualTranslationSuggestion?> EvaluateContextAsync(
        string sourceLanguageCode,
        string targetLanguageCode,
        string text,
        CancellationToken cancellationToken = default);

    Task EvaluateProviderObservationAsync(
        Guid alignmentId,
        CancellationToken cancellationToken = default);

    Task<int> ReevaluateHistoricalProviderObservationsAsync(
        int take,
        CancellationToken cancellationToken = default);

    Task<LegendConnectHistoricalReevaluationProgress> ReevaluateHistoricalProviderObservationsAsync(
        int take,
        Guid? afterId,
        CancellationToken cancellationToken = default);

    Task ReevaluateHistoricalProviderObservationAsync(
        Guid alignmentId,
        CancellationToken cancellationToken = default);

    Task<LegendConnectTranslationQualitySnapshot> GetTranslationQualityAsync(
        CancellationToken cancellationToken = default);

    Task<LegendProviderObservationResolution> ApproveProviderObservationAsync(
        Guid alignmentId,
        CancellationToken cancellationToken = default);

    Task<LegendProviderObservationResolution> RejectProviderObservationAsync(
        Guid alignmentId,
        CancellationToken cancellationToken = default);

    Task<LegendProviderObservationResolution> LeaveProviderObservationUnresolvedAsync(
        Guid alignmentId,
        CancellationToken cancellationToken = default);

    Task<Guid?> RecordHumanCorrectionAsync(
        Guid providerObservationAlignmentId,
        Guid correctedAlignmentId,
        CancellationToken cancellationToken = default);

    bool IsContextualCompositionActive { get; }
}

internal sealed class LegendConnectTranslationIntelligence : ILegendConnectTranslationIntelligence
{
    private readonly MasterAppDbContext _db;
    private readonly decimal _minimumConfidence;
    private readonly ILegendConnectRuntimePolicyAuthority? _runtimePolicy;

    public LegendConnectTranslationIntelligence(
        MasterAppDbContext db,
        IConfiguration configuration,
        ILegendConnectRuntimePolicyAuthority? runtimePolicy = null)
    {
        _db = db;
        _minimumConfidence = Math.Clamp(
            configuration.GetValue<decimal?>("LegendConnect:ContextualComposition:MinimumConfidence") ?? 0.98m,
            0m,
            1m);
        IsContextualCompositionActive = string.Equals(
            configuration["LegendConnect:ContextualComposition:Mode"],
            "Active",
            StringComparison.OrdinalIgnoreCase);
        _runtimePolicy = runtimePolicy;
    }

    public bool IsContextualCompositionActive { get; }

    public async Task<LegendTranslationMemoryMatch?> TryGetTrustedExactMemoryAsync(
        string sourceLanguageCode,
        string targetLanguageCode,
        string text,
        CancellationToken cancellationToken = default)
    {
        var hash = LegendLanguageIdentity.TextHash(text);
        var pairKey = LegendLanguageIdentity.PairKey(
            sourceLanguageCode,
            targetLanguageCode);

        var candidates = await (
            from alignment in _db.Set<LegendTranslationAlignment>().AsNoTracking()
            join source in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on alignment.SourceTextUnitId equals source.Id
            join target in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on alignment.TargetTextUnitId equals target.Id
            where alignment.PairKey == pairKey &&
                  alignment.SupersededUtc == null &&
                  source.IsTrainingEligible &&
                  target.IsTrainingEligible &&
                  source.LanguageCode == sourceLanguageCode &&
                  target.LanguageCode == targetLanguageCode &&
                  source.NormalizedHash == hash &&
                  (
                      alignment.HumanVerified ||
                      (
                          alignment.Provenance ==
                              LegendConnectKnowledgeProvenance.SystemValidatedMachine &&
                          alignment.QualityState == "SystemValidated" &&
                          alignment.Confidence >= 0.98m
                      ) ||
                      (
                          alignment.Provenance ==
                              LegendConnectKnowledgeProvenance.ConsentedLiveTranslation &&
                          alignment.QualityState == "ConsentedLive" &&
                          alignment.Confidence >= 0.98m
                      )
                  )
            select new
            {
                target.Text,
                alignment.Confidence,
                alignment.Provenance,
                alignment.QualityState,
                alignment.HumanVerified,
                alignment.Id
            }
        ).ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return null;

        var eligible = candidates
            .OrderByDescending(item =>
                item.HumanVerified ? 4 :
                item.QualityState == "SystemValidated" ? 3 :
                item.Provenance ==
                    LegendConnectKnowledgeProvenance.ConsentedLiveTranslation ? 2 :
                1)
            .ThenByDescending(item => item.Confidence ?? 0m)
            .ThenByDescending(item => item.Id)
            .ToList();

        if (eligible.Count == 0)
            return null;

        var bestRank =
            eligible[0].HumanVerified ? 4 :
            eligible[0].QualityState == "SystemValidated" ? 3 :
            eligible[0].Provenance ==
                LegendConnectKnowledgeProvenance.ConsentedLiveTranslation ? 2 :
            1;

        var sameRank = eligible
            .Where(item =>
                (item.HumanVerified ? 4 :
                 item.QualityState == "SystemValidated" ? 3 :
                 item.Provenance ==
                    LegendConnectKnowledgeProvenance.ConsentedLiveTranslation ? 2 :
                 1) == bestRank)
            .Select(item => item.Text)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();

        // Never guess between competing targets at the same authority level.
        if (sameRank.Count != 1)
            return null;

        var match = eligible[0];

        return new LegendTranslationMemoryMatch(
            match.Text,
            match.Confidence ?? (match.Text.Length > 0 ? 1m : 0m),
            match.Provenance,
            match.QualityState);
    }

    public async Task<LegendTranslationMemoryMatch?> TryGetReusableProviderObservationAsync(
        string sourceLanguageCode,
        string targetLanguageCode,
        string text,
        CancellationToken cancellationToken = default)
    {
        var hash = LegendLanguageIdentity.TextHash(text);
        var pairKey = LegendLanguageIdentity.PairKey(
            sourceLanguageCode,
            targetLanguageCode);

        var candidates = await (
            from alignment in _db.Set<LegendTranslationAlignment>().AsNoTracking()
            join source in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on alignment.SourceTextUnitId equals source.Id
            join target in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on alignment.TargetTextUnitId equals target.Id
            where alignment.PairKey == pairKey &&
                  alignment.SupersededUtc == null &&
                  source.IsTrainingEligible &&
                  target.IsTrainingEligible &&
                  source.LanguageCode == sourceLanguageCode &&
                  target.LanguageCode == targetLanguageCode &&
                  source.NormalizedHash == hash &&
                  alignment.Provenance ==
                      LegendConnectKnowledgeProvenance.ProviderDerived &&
                  (
                      alignment.QualityState == "SystemValidated" ||
                      alignment.QualityState == "Observation"
                  )
            select new
            {
                target.Text,
                alignment.Confidence,
                alignment.Provenance,
                alignment.QualityState,
                alignment.Id
            }
        ).ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return null;

        var ids = candidates
            .Select(item => item.Id)
            .ToArray();

        var contradicted = (
            await _db.Set<LegendTranslationQualityEvidence>()
                .AsNoTracking()
                .Where(item =>
                    ids.Contains(item.ObservedAlignmentId) &&
                    item.Signal == "Contradictory" &&
                    item.ResolutionState == "Open" &&
                    item.SupersededUtc == null)
                .Select(item => item.ObservedAlignmentId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var eligible = candidates
            .Where(item =>
                !contradicted.Contains(item.Id))
            .OrderByDescending(item =>
                item.QualityState == "SystemValidated" ? 2 : 1)
            .ThenByDescending(item =>
                item.Confidence ?? 0m)
            .ThenByDescending(item =>
                item.Id)
            .ToList();

        if (eligible.Count == 0)
            return null;

        var bestRank =
            eligible[0].QualityState == "SystemValidated"
                ? 2
                : 1;

        var sameRank = eligible
            .Where(item =>
                (item.QualityState == "SystemValidated" ? 2 : 1) ==
                bestRank)
            .Select(item => item.Text)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();

        if (sameRank.Count != 1)
            return null;

        var match = eligible[0];

        return new LegendTranslationMemoryMatch(
            match.Text,
            match.Confidence ??
                (match.Text.Length > 0 ? 1m : 0m),
            match.Provenance,
            match.QualityState);
    }

    public async Task<LegendContextualTranslationSuggestion?> EvaluateContextAsync(
        string sourceLanguageCode,
        string targetLanguageCode,
        string text,
        CancellationToken cancellationToken = default)
    {
        var pairKey = LegendLanguageIdentity.PairKey(sourceLanguageCode, targetLanguageCode);
        var minimumConfidence = await MinimumConfidenceAsync(cancellationToken);
        var pattern = LegendLanguageIdentity.ContextPatternSignature(text);
        if (string.IsNullOrWhiteSpace(pattern))
            return null;

        var candidates = await (
            from relationship in _db.Set<LegendLanguageContextRelationship>().AsNoTracking()
            join source in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on relationship.SourceTextUnitId equals source.Id
            join target in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on relationship.RelatedTextUnitId equals target.Id
            where relationship.PairKey == pairKey &&
                  relationship.SupersededUtc == null &&
                  relationship.SourcePatternSignature == pattern &&
                  relationship.QualityState == "Verified" &&
                  relationship.Confidence >= minimumConfidence &&
                  source.IsTrainingEligible &&
                  target.IsTrainingEligible &&
                  target.LanguageCode == targetLanguageCode
            select new { target.Text, relationship.Confidence }
        ).Distinct().Take(2).ToListAsync(cancellationToken);

        // Ambiguous structural matches are observations, never a formulation.
        return candidates.Count == 1
            ? new LegendContextualTranslationSuggestion(candidates[0].Text, candidates[0].Confidence)
            : null;
    }

    /// <summary>
    /// Evaluates a persisted provider observation against only active,
    /// pair-specific and target-language-specific canonical evidence. The
    /// method records evidence; it does not alter the observation's maturity,
    /// create a replacement translation, or treat provider repetition as new
    /// support.
    /// </summary>
    public async Task EvaluateProviderObservationAsync(
        Guid alignmentId,
        CancellationToken cancellationToken = default)
    {
        var observation = await LoadProviderObservationAsync(alignmentId, includeSuperseded: false, cancellationToken);
        if (observation is null || observation.Alignment.HumanVerified)
            return;

        var changed = false;
        var signalsRecorded = false;
        var conflicts = await _db.Set<LegendTranslationAlignment>()
            .AsNoTracking()
            .Where(item => item.PairKey == observation.Alignment.PairKey &&
                item.Id != observation.Alignment.Id &&
                item.SupersededUtc == null &&
                item.HumanVerified &&
                item.SourceTextUnitId == observation.Alignment.SourceTextUnitId &&
                item.TargetTextUnitId != observation.Alignment.TargetTextUnitId)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        foreach (var conflictingAlignmentId in conflicts)
        {
            changed |= await AddEvidenceIfAbsentAsync(
                observation,
                "Contradictory",
                "human_verified_directional_conflict",
                conflictingAlignmentId,
                null,
                null,
                "Open",
                null,
                cancellationToken);
            signalsRecorded = true;
        }

        // Founder-controlled semantic anchors describe only facts explicitly
        // present in a structured source curriculum. When a different
        // human-verified target exists for this exact directional source, the
        // provider result is marked as insufficient for each known component.
        // This is deliberately component evidence, not a claim that a word
        // count or a target-language token proves a particular grammar rule.
        if (conflicts.Count > 0)
        {
            var semanticComponents = await _db.Set<LegendLanguageCompositionalAnchor>().AsNoTracking()
                .Where(item => item.TextUnitId == observation.Source.Id && item.SupersededUtc == null &&
                    item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                    item.SemanticSignature != null && item.SemanticSignature != string.Empty)
                .Select(item => new { item.SemanticSignature, item.Dimension, item.Value })
                .Distinct()
                .ToListAsync(cancellationToken);
            foreach (var component in semanticComponents)
            {
                foreach (var conflictingAlignmentId in conflicts)
                {
                    changed |= await AddEvidenceIfAbsentAsync(
                        observation,
                        "Insufficient",
                        "known_semantic_component_not_realized",
                        conflictingAlignmentId,
                        null,
                        null,
                        "Open",
                        null,
                        cancellationToken,
                        component.SemanticSignature);
                    signalsRecorded = true;
                }
            }
        }

        // The context signature is an existing canonical structural summary,
        // not a token or English-word substitution rule. A relationship is
        // usable only when it is Founder-approved verified evidence in this
        // exact directional pair and resolves to the observed target asset.
        var sourcePattern = LegendLanguageIdentity.ContextPatternSignature(observation.Source.Text);
        if (!string.IsNullOrWhiteSpace(sourcePattern))
        {
            var supportingContextIds = await (
                from relationship in _db.Set<LegendLanguageContextRelationship>().AsNoTracking()
                join contextSource in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                    on relationship.SourceTextUnitId equals contextSource.Id
                where relationship.PairKey == observation.Alignment.PairKey &&
                    relationship.RelatedTextUnitId == observation.Alignment.TargetTextUnitId &&
                    relationship.SupersededUtc == null &&
                    relationship.SourcePatternSignature == sourcePattern &&
                    relationship.QualityState == "Verified" &&
                    relationship.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                    contextSource.IsTrainingEligible &&
                    contextSource.Provenance == LegendConnectKnowledgeProvenance.FounderApproved
                select relationship.Id
            ).Distinct().ToListAsync(cancellationToken);
            foreach (var contextId in supportingContextIds)
            {
                changed |= await AddEvidenceIfAbsentAsync(
                    observation,
                    "Supported",
                    "trusted_target_context",
                    null,
                    null,
                    contextId,
                    "Open",
                    null,
                    cancellationToken);
                signalsRecorded = true;
            }
        }

        // A target-language structural pattern may support an observation only
        // when the pattern and its evidence are themselves Founder-approved
        // and mature. Provider-derived structural output never self-confirms a
        // provider-derived observation.
        var targetExampleIds = await _db.Set<LegendCurriculumExample>().AsNoTracking()
            .Where(item => item.TextUnitId == observation.Alignment.TargetTextUnitId &&
                item.LanguageCode == observation.Target.LanguageCode && item.SupersededUtc == null)
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken);
        List<Guid> supportingPatternIds = targetExampleIds.Length == 0
            ? []
            : await (
                from evidence in _db.Set<LegendLanguageStructuralEvidence>().AsNoTracking()
                join pattern in _db.Set<LegendLanguageStructuralPattern>().AsNoTracking()
                    on evidence.StructuralPatternId equals pattern.Id
                where (targetExampleIds.Contains(evidence.BaselineCurriculumExampleId) ||
                       targetExampleIds.Contains(evidence.ComparedCurriculumExampleId)) &&
                    evidence.SupersededUtc == null &&
                    evidence.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                    pattern.SupersededUtc == null &&
                    pattern.PairKey == observation.Alignment.PairKey &&
                    pattern.LanguageCode == observation.Target.LanguageCode &&
                    pattern.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                    (pattern.MaturityState == "Supported" || pattern.MaturityState == "Validated")
                select pattern.Id
            ).Distinct().ToListAsync(cancellationToken);
        foreach (var patternId in supportingPatternIds)
        {
            changed |= await AddEvidenceIfAbsentAsync(
                observation,
                "Supported",
                "trusted_target_structural_pattern",
                null,
                patternId,
                null,
                "Open",
                null,
                cancellationToken);
            signalsRecorded = true;
        }

        if (signalsRecorded)
        {
            // Only retire the generic fallback once concrete evidence exists.
            // Component-level insufficiency is itself durable anomaly evidence
            // about this ProviderDerived observation and must remain available
            // for Founder review alongside its contradictory signal.
            var insufficient = await _db.Set<LegendTranslationQualityEvidence>()
                .Where(item => item.ObservedAlignmentId == observation.Alignment.Id &&
                    item.Signal == "Insufficient" &&
                    item.ReasonCode == "no_established_pair_specific_evidence" &&
                    item.SupersededUtc == null)
                .ToListAsync(cancellationToken);
            foreach (var item in insufficient)
            {
                item.SupersededUtc = DateTime.UtcNow;
                item.UpdatedUtc = DateTime.UtcNow;
                changed = true;
            }
        }
        else
        {
            changed |= await AddEvidenceIfAbsentAsync(
                observation,
                "Insufficient",
                "no_established_pair_specific_evidence",
                null,
                null,
                null,
                "Open",
                null,
                cancellationToken);
        }

        // Automatic validation is an evidence-derived state on the existing
        // ProviderDerived alignment. It never changes provenance and never
        // fabricates HumanVerified/Founder approval.
        var persistedEvidence = await _db.Set<LegendTranslationQualityEvidence>()
            .AsNoTracking()
            .Where(item =>
                item.ObservedAlignmentId == observation.Alignment.Id &&
                item.SupersededUtc == null)
            .Select(item => new
            {
                item.Signal,
                item.ResolutionState
            })
            .ToListAsync(cancellationToken);

        // AddEvidenceIfAbsentAsync may have created evidence in this same
        // evaluation pass. Include the tracked active rows so maturity is
        // derived from the exact evidence this canonical evaluator just
        // established, without requiring a second persistence boundary.
        var trackedEvidence = _db.Set<LegendTranslationQualityEvidence>()
            .Local
            .Where(item =>
                item.ObservedAlignmentId == observation.Alignment.Id &&
                item.SupersededUtc == null &&
                _db.Entry(item).State != EntityState.Deleted)
            .Select(item => new
            {
                item.Signal,
                item.ResolutionState
            })
            .ToList();

        var hasIndependentSupport =
            persistedEvidence.Any(item => item.Signal == "Supported") ||
            trackedEvidence.Any(item => item.Signal == "Supported");

        var hasOpenContradiction =
            persistedEvidence.Any(item =>
                item.Signal == "Contradictory" &&
                item.ResolutionState == "Open") ||
            trackedEvidence.Any(item =>
                item.Signal == "Contradictory" &&
                item.ResolutionState == "Open");

        if (!observation.Alignment.HumanVerified &&
            observation.Alignment.Provenance ==
                LegendConnectKnowledgeProvenance.ProviderDerived &&
            hasIndependentSupport &&
            !hasOpenContradiction)
        {
            if (observation.Alignment.QualityState != "SystemValidated" ||
                observation.Alignment.Confidence < 0.98m)
            {
                observation.Alignment.QualityState = "SystemValidated";
                observation.Alignment.Confidence =
                    Math.Max(observation.Alignment.Confidence ?? 0m, 0.98m);
                observation.Alignment.UpdatedUtc = DateTime.UtcNow;
                changed = true;
            }
        }
        else if (!observation.Alignment.HumanVerified &&
                 observation.Alignment.Provenance ==
                     LegendConnectKnowledgeProvenance.ProviderDerived &&
                 observation.Alignment.QualityState == "SystemValidated")
        {
            // Current evidence owns current maturity. Historical validation
            // cannot survive a newly discovered contradiction.
            observation.Alignment.QualityState = "Observation";
            observation.Alignment.Confidence =
                Math.Min(observation.Alignment.Confidence ?? 0.5m, 0.5m);
            observation.Alignment.UpdatedUtc = DateTime.UtcNow;
            changed = true;
        }

        if (changed)
            await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> ReevaluateHistoricalProviderObservationsAsync(
        int take,
        CancellationToken cancellationToken = default) =>
        (await ReevaluateHistoricalProviderObservationsAsync(take, afterId: null, cancellationToken)).ProcessedCount;

    /// <summary>
    /// Processes one bounded stable-identity page through this existing
    /// provider-quality evaluator. It remains observation-only and is used by
    /// the same learning worker as the curriculum replay.
    /// </summary>
    public async Task<LegendConnectHistoricalReevaluationProgress> ReevaluateHistoricalProviderObservationsAsync(
        int take,
        Guid? afterId,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(take, 1, 250);
        var observationIds = await _db.Set<LegendTranslationAlignment>().AsNoTracking()
            .Where(item => item.Provenance == LegendConnectKnowledgeProvenance.ProviderDerived &&
                item.SupersededUtc == null && (!afterId.HasValue || item.Id.CompareTo(afterId.Value) > 0))
            .OrderBy(item => item.Id)
            .Take(pageSize)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        foreach (var observationId in observationIds)
            await EvaluateProviderObservationAsync(observationId, cancellationToken);
        return new LegendConnectHistoricalReevaluationProgress(
            observationIds.Count,
            observationIds.Count == 0 ? null : observationIds[^1],
            observationIds.Count < pageSize);
    }

    public Task ReevaluateHistoricalProviderObservationAsync(
        Guid alignmentId,
        CancellationToken cancellationToken = default) =>
        EvaluateProviderObservationAsync(alignmentId, cancellationToken);

    public async Task<LegendConnectTranslationQualitySnapshot> GetTranslationQualityAsync(
        CancellationToken cancellationToken = default)
    {
        var observations =
            from alignment in _db.Set<LegendTranslationAlignment>().AsNoTracking()
            join source in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on alignment.SourceTextUnitId equals source.Id
            join target in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on alignment.TargetTextUnitId equals target.Id
            where alignment.Provenance == LegendConnectKnowledgeProvenance.ProviderDerived &&
                alignment.SupersededUtc == null && source.IsTrainingEligible && target.IsTrainingEligible
            select new ProviderObservationProjection(
                alignment.Id,
                alignment.PairKey,
                alignment.HumanVerified,
                alignment.QualityState,
                alignment.Provider,
                alignment.CreatedUtc,
                source.LanguageCode,
                source.Text,
                source.Provenance,
                target.LanguageCode,
                target.Text)
        ;
        var providerObservationCount = await observations.LongCountAsync(cancellationToken);
        var activeEvidence =
            from evidence in _db.Set<LegendTranslationQualityEvidence>().AsNoTracking()
            join alignment in _db.Set<LegendTranslationAlignment>().AsNoTracking()
                on evidence.ObservedAlignmentId equals alignment.Id
            join source in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on alignment.SourceTextUnitId equals source.Id
            join target in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on alignment.TargetTextUnitId equals target.Id
            where evidence.SupersededUtc == null &&
                alignment.Provenance == LegendConnectKnowledgeProvenance.ProviderDerived &&
                alignment.SupersededUtc == null &&
                source.IsTrainingEligible && target.IsTrainingEligible
            select evidence;
        var supportedObservationCount = await activeEvidence
            .Where(item => item.Signal == "Supported")
            .Select(item => item.ObservedAlignmentId)
            .Distinct()
            .LongCountAsync(cancellationToken);
        var contradictionCount = await activeEvidence
            .Where(item => item.Signal == "Contradictory")
            .Select(item => item.ObservedAlignmentId)
            .Distinct()
            .LongCountAsync(cancellationToken);

        // The page can render at most 250 review rows. First select that
        // bounded Founder-review queue in SQL, then load evidence only for
        // those rows instead of materializing every provider observation and
        // every quality-evidence record on the initial dashboard request.
        var reviewCandidates = await (
            from alignment in _db.Set<LegendTranslationAlignment>().AsNoTracking()
            join source in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on alignment.SourceTextUnitId equals source.Id
            join target in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on alignment.TargetTextUnitId equals target.Id
            where alignment.Provenance == LegendConnectKnowledgeProvenance.ProviderDerived &&
                alignment.SupersededUtc == null &&
                !alignment.HumanVerified &&
                source.IsTrainingEligible &&
                target.IsTrainingEligible &&
                source.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                _db.Set<LegendTranslationQualityEvidence>().Any(evidence =>
                    evidence.ObservedAlignmentId == alignment.Id &&
                    evidence.SupersededUtc == null &&
                    evidence.Signal == "Contradictory" &&
                    evidence.ResolutionState == "Open")
            orderby alignment.CreatedUtc descending
            select new ProviderObservationProjection(
                alignment.Id,
                alignment.PairKey,
                alignment.HumanVerified,
                alignment.QualityState,
                alignment.Provider,
                alignment.CreatedUtc,
                source.LanguageCode,
                source.Text,
                source.Provenance,
                target.LanguageCode,
                target.Text)
        ).Take(250).ToListAsync(cancellationToken);
        var reviewCandidateIds = reviewCandidates.Select(item => item.AlignmentId).ToArray();
        List<LegendTranslationQualityEvidence> reviewEvidence = reviewCandidateIds.Length == 0
            ? []
            : await _db.Set<LegendTranslationQualityEvidence>().AsNoTracking()
                .Where(item => reviewCandidateIds.Contains(item.ObservedAlignmentId) && item.SupersededUtc == null)
                .ToListAsync(cancellationToken);
        var evidenceByObservation = reviewEvidence
            .GroupBy(item => item.ObservedAlignmentId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var reviewItems = reviewCandidates
            .Select(item =>
            {
                var qualityEvidence = evidenceByObservation[item.AlignmentId];
                var contradictions = qualityEvidence
                    .Where(evidence => evidence.Signal == "Contradictory" && evidence.ResolutionState == "Open")
                    .OrderByDescending(evidence => evidence.UpdatedUtc)
                    .ToList();
                return new LegendConnectTranslationQualityReviewSnapshot(
                    item.AlignmentId,
                    item.PairKey,
                    item.SourceLanguageCode,
                    item.SourceText,
                    item.TargetLanguageCode,
                    item.TargetText,
                    item.Provider,
                    LegendConnectKnowledgeProvenance.ProviderDerived,
                    item.QualityState,
                    qualityEvidence.Count(evidence => evidence.Signal == "Supported"),
                    contradictions.Count,
                    contradictions[0].ReasonCode,
                    qualityEvidence.Select(evidence => evidence.ReasonCode).Distinct(StringComparer.Ordinal).Order().ToList(),
                    item.ObservedUtc);
            })
            .ToList();
        var humanVerified = await (
            from alignment in _db.Set<LegendTranslationAlignment>().AsNoTracking()
            join source in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on alignment.SourceTextUnitId equals source.Id
            join target in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on alignment.TargetTextUnitId equals target.Id
            where alignment.SupersededUtc == null && alignment.HumanVerified &&
                source.IsTrainingEligible && target.IsTrainingEligible
            select alignment.Id
        ).LongCountAsync(cancellationToken);

        return new LegendConnectTranslationQualitySnapshot(
            reviewItems.Count,
            providerObservationCount,
            supportedObservationCount,
            contradictionCount,
            humanVerified,
            reviewItems);
    }

    public async Task<LegendProviderObservationResolution> ApproveProviderObservationAsync(
        Guid alignmentId,
        CancellationToken cancellationToken = default)
    {
        var observation = await LoadProviderObservationAsync(alignmentId, includeSuperseded: false, cancellationToken);
        if (observation is null)
            return MissingObservation();

        observation.Alignment.HumanVerified = true;
        observation.Alignment.QualityState = "Verified";
        observation.Alignment.Confidence = 1m;
        observation.Alignment.UpdatedUtc = DateTime.UtcNow;
        await ResolveOpenEvidenceAsync(observation.Alignment.Id, "Approved", null, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return new LegendProviderObservationResolution(
            true, null,
            "The provider observation was explicitly human-verified. Its provider provenance remains historical.",
            observation.Source.LanguageCode, observation.Alignment.PairKey);
    }

    public async Task<LegendProviderObservationResolution> RejectProviderObservationAsync(
        Guid alignmentId,
        CancellationToken cancellationToken = default)
    {
        var observation = await LoadProviderObservationAsync(alignmentId, includeSuperseded: false, cancellationToken);
        if (observation is null)
            return MissingObservation();

        var now = DateTime.UtcNow;
        observation.Alignment.SupersededUtc = now;
        observation.Alignment.QualityState = "Superseded";
        observation.Alignment.UpdatedUtc = now;
        var relatedContexts = await _db.Set<LegendLanguageContextRelationship>()
            .Where(item => item.PairKey == observation.Alignment.PairKey &&
                item.SourceTextUnitId == observation.Alignment.SourceTextUnitId &&
                item.RelatedTextUnitId == observation.Alignment.TargetTextUnitId &&
                item.Provenance == LegendConnectKnowledgeProvenance.ProviderDerived &&
                item.SupersededUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var context in relatedContexts)
        {
            context.SupersededUtc = now;
            context.UpdatedUtc = now;
        }
        await ResolveOpenEvidenceAsync(observation.Alignment.Id, "Rejected", null, cancellationToken);
        var retiredTargetId = await RetireProviderTargetIfUnreferencedAsync(
            observation.Target,
            observation.Alignment.Id,
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return new LegendProviderObservationResolution(
            true, null,
            "The provider observation was retired from active learning authority; its audit history remains intact.",
            observation.Source.LanguageCode, observation.Alignment.PairKey, retiredTargetId);
    }

    public async Task<LegendProviderObservationResolution> LeaveProviderObservationUnresolvedAsync(
        Guid alignmentId,
        CancellationToken cancellationToken = default)
    {
        var observation = await LoadProviderObservationAsync(alignmentId, includeSuperseded: false, cancellationToken);
        return observation is null
            ? MissingObservation()
            : new LegendProviderObservationResolution(
                true, null,
                "The provider observation remains unresolved and available for later Founder review.",
                observation.Source.LanguageCode, observation.Alignment.PairKey);
    }

    public async Task<Guid?> RecordHumanCorrectionAsync(
        Guid providerObservationAlignmentId,
        Guid correctedAlignmentId,
        CancellationToken cancellationToken = default)
    {
        var observation = await LoadProviderObservationAsync(providerObservationAlignmentId, includeSuperseded: true, cancellationToken);
        if (observation is null)
            return null;
        var corrected = await _db.Set<LegendTranslationAlignment>()
            .SingleOrDefaultAsync(item => item.Id == correctedAlignmentId && item.HumanVerified && item.SupersededUtc == null, cancellationToken);
        if (corrected is null || !string.Equals(corrected.PairKey, observation.Alignment.PairKey, StringComparison.Ordinal))
            return null;

        var changed = await AddEvidenceIfAbsentAsync(
            observation,
            "Contradictory",
            "human_verified_directional_correction",
            corrected.Id,
            null,
            null,
            "Corrected",
            corrected.Id,
            cancellationToken);
        changed |= await ResolveOpenEvidenceAsync(observation.Alignment.Id, "Corrected", corrected.Id, cancellationToken);
        var now = DateTime.UtcNow;
        var relatedContexts = await _db.Set<LegendLanguageContextRelationship>()
            .Where(item => item.PairKey == observation.Alignment.PairKey &&
                item.SourceTextUnitId == observation.Alignment.SourceTextUnitId &&
                item.RelatedTextUnitId == observation.Alignment.TargetTextUnitId &&
                item.Provenance == LegendConnectKnowledgeProvenance.ProviderDerived &&
                item.SupersededUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var context in relatedContexts)
        {
            context.SupersededUtc = now;
            context.UpdatedUtc = now;
            changed = true;
        }
        var retiredTargetId = await RetireProviderTargetIfUnreferencedAsync(
            observation.Target,
            observation.Alignment.Id,
            cancellationToken);
        changed |= retiredTargetId is not null;
        if (changed)
            await _db.SaveChangesAsync(cancellationToken);
        return retiredTargetId;
    }

    private async Task<ProviderObservation?> LoadProviderObservationAsync(
        Guid alignmentId,
        bool includeSuperseded,
        CancellationToken cancellationToken)
    {
        var alignment = await _db.Set<LegendTranslationAlignment>()
            .SingleOrDefaultAsync(item => item.Id == alignmentId, cancellationToken);
        if (alignment is null ||
            alignment.Provenance != LegendConnectKnowledgeProvenance.ProviderDerived ||
            (!includeSuperseded && alignment.SupersededUtc is not null))
        {
            return null;
        }
        var source = await _db.Set<LegendLanguageTextUnit>()
            .SingleOrDefaultAsync(item => item.Id == alignment.SourceTextUnitId, cancellationToken);
        var target = await _db.Set<LegendLanguageTextUnit>()
            .SingleOrDefaultAsync(item => item.Id == alignment.TargetTextUnitId, cancellationToken);
        if (source is null || target is null ||
            (!includeSuperseded && (!source.IsTrainingEligible || !target.IsTrainingEligible)))
        {
            return null;
        }
        return new ProviderObservation(alignment, source, target);
    }

    private async Task<bool> AddEvidenceIfAbsentAsync(
        ProviderObservation observation,
        string signal,
        string reasonCode,
        Guid? relatedAlignmentId,
        Guid? structuralPatternId,
        Guid? contextRelationshipId,
        string resolutionState,
        Guid? resolvedByAlignmentId,
        CancellationToken cancellationToken,
        string? semanticSignature = null)
    {
        var identity = LegendLanguageIdentity.TextHash(string.Join('|',
            observation.Alignment.Id.ToString("D"), signal, reasonCode,
            relatedAlignmentId?.ToString("D") ?? "none",
            structuralPatternId?.ToString("D") ?? "none",
            contextRelationshipId?.ToString("D") ?? "none",
            semanticSignature ?? "none"));
        if (_db.Set<LegendTranslationQualityEvidence>().Local.Any(item => item.EvidenceIdentity == identity) ||
            await _db.Set<LegendTranslationQualityEvidence>().AnyAsync(item => item.EvidenceIdentity == identity, cancellationToken))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        _db.Set<LegendTranslationQualityEvidence>().Add(new LegendTranslationQualityEvidence
        {
            Id = Guid.NewGuid(),
            ObservedAlignmentId = observation.Alignment.Id,
            PairKey = observation.Alignment.PairKey,
            SourceTextUnitId = observation.Alignment.SourceTextUnitId,
            TargetTextUnitId = observation.Alignment.TargetTextUnitId,
            RelatedAlignmentId = relatedAlignmentId,
            StructuralPatternId = structuralPatternId,
            ContextRelationshipId = contextRelationshipId,
            Signal = signal,
            ReasonCode = reasonCode,
            ResolutionState = resolutionState,
            EvidenceIdentity = identity,
            SemanticSignature = semanticSignature,
            ResolvedUtc = resolutionState == "Open" ? null : now,
            ResolvedByAlignmentId = resolvedByAlignmentId,
            CreatedUtc = now,
            UpdatedUtc = now
        });
        return true;
    }

    private async Task<bool> ResolveOpenEvidenceAsync(
        Guid observedAlignmentId,
        string resolutionState,
        Guid? resolvedByAlignmentId,
        CancellationToken cancellationToken)
    {
        var items = await _db.Set<LegendTranslationQualityEvidence>()
            .Where(item => item.ObservedAlignmentId == observedAlignmentId &&
                item.SupersededUtc == null && item.ResolutionState == "Open")
            .ToListAsync(cancellationToken);
        if (items.Count == 0)
            return false;
        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            item.ResolutionState = resolutionState;
            item.ResolvedUtc = now;
            item.ResolvedByAlignmentId = resolvedByAlignmentId;
            item.UpdatedUtc = now;
        }
        return true;
    }

    private async Task<Guid?> RetireProviderTargetIfUnreferencedAsync(
        LegendLanguageTextUnit target,
        Guid excludedAlignmentId,
        CancellationToken cancellationToken)
    {
        if (target.Provenance != LegendConnectKnowledgeProvenance.ProviderDerived)
            return null;
        var stillReferenced = await _db.Set<LegendTranslationAlignment>()
            .AnyAsync(item => item.TargetTextUnitId == target.Id &&
                item.Id != excludedAlignmentId && item.SupersededUtc == null, cancellationToken);
        if (stillReferenced)
            return null;
        target.IsTrainingEligible = false;
        target.UpdatedUtc = DateTime.UtcNow;
        return target.Id;
    }

    private static LegendProviderObservationResolution MissingObservation() => new(
        false,
        "provider_observation_not_found",
        "The selected active provider observation is unavailable for quality review.");

    private async Task<decimal> MinimumConfidenceAsync(CancellationToken cancellationToken) =>
        _runtimePolicy is null
            ? _minimumConfidence
            : (await _runtimePolicy.GetEffectiveAsync(cancellationToken)).ContextualMinimumConfidence;

    private sealed record ProviderObservation(
        LegendTranslationAlignment Alignment,
        LegendLanguageTextUnit Source,
        LegendLanguageTextUnit Target);

    private sealed record ProviderObservationProjection(
        Guid AlignmentId,
        string PairKey,
        bool HumanVerified,
        string QualityState,
        string Provider,
        DateTime ObservedUtc,
        string SourceLanguageCode,
        string SourceText,
        string SourceProvenance,
        string TargetLanguageCode,
        string TargetText);
}

internal interface ILegendConnectOperationalEventWriter
{
    Task TryRecordAsync(
        string category,
        string severity,
        string status,
        string? languageCode = null,
        string? pairKey = null,
        string? errorCode = null,
        string? correlationId = null,
        string? summary = null,
        bool isResolved = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One bounded diagnostic authority for Legend Connect. Callers provide only
/// operational metadata; message/corpus bodies and provider secrets are never
/// accepted by this API.
/// </summary>
internal sealed class LegendConnectOperationalEventWriter : ILegendConnectOperationalEventWriter
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<LegendConnectOperationalEventWriter> _logger;

    public LegendConnectOperationalEventWriter(
        MasterAppDbContext db,
        ILogger<LegendConnectOperationalEventWriter> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task TryRecordAsync(
        string category,
        string severity,
        string status,
        string? languageCode = null,
        string? pairKey = null,
        string? errorCode = null,
        string? correlationId = null,
        string? summary = null,
        bool isResolved = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _db.Set<LegendConnectOperationalEvent>().Add(new LegendConnectOperationalEvent
            {
                Id = Guid.NewGuid(),
                Category = Bounded(category, 80),
                Severity = Bounded(severity, 20),
                Status = Bounded(status, 80),
                LanguageCode = Optional(languageCode, 32),
                PairKey = Optional(pairKey, 72),
                ErrorCode = Optional(errorCode, 80),
                CorrelationId = Optional(correlationId, 128),
                Summary = Optional(summary, 500),
                IsResolved = isResolved,
                OccurredUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _db.ChangeTracker.Clear();
            _logger.LogWarning(exception, "Legend Connect operational event write failed. Category={Category} Status={Status}", category, status);
        }
    }

    private static string Bounded(string value, int length) =>
        (value ?? string.Empty).Trim()[..Math.Min((value ?? string.Empty).Trim().Length, length)];

    private static string? Optional(string? value, int length)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized[..Math.Min(normalized.Length, length)];
    }
}
