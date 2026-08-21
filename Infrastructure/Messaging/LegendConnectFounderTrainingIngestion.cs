using System.Text;
using System.Text.RegularExpressions;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Messaging;

/// <summary>
/// The one deterministic boundary for arbitrary Founder source-seed training.
/// It keeps raw-submission provenance distinct from canonical learning assets,
/// then hands atomic units to the existing corpus, curriculum, and autonomous
/// acquisition authorities. It owns no provider, corpus, router, or worker.
/// </summary>
internal sealed class LegendConnectFounderTrainingIngestionAuthority
{
    private const int MaximumRawCharacters = 30_000;
    private const int MaximumAtomicUnits = 500;
    private readonly MasterAppDbContext _db;
    private readonly ILegendLanguageRegistry _languages;
    private readonly LegendConnectCorpusService _corpus;
    private readonly LegendConnectCurriculumService _curriculum;
    private readonly ILegendConnectOperationalEventWriter? _operations;

    private sealed record LegacyContextMetadata(
        string? ContextCategory,
        string? UsageRegister,
        string? RegionalVariant);

    private sealed record StaleCapabilitySubmissionClaim(
        Guid SubmissionId,
        DateTime LeaseExpiresUtc);

    public LegendConnectFounderTrainingIngestionAuthority(
        MasterAppDbContext db,
        ILegendLanguageRegistry languages,
        LegendConnectCorpusService corpus,
        LegendConnectCurriculumService curriculum,
        ILegendConnectOperationalEventWriter? operations = null)
    {
        _db = db;
        _languages = languages;
        _corpus = corpus;
        _curriculum = curriculum;
        _operations = operations;
    }

    public async Task<LegendConnectKnowledgeSubmissionResult> SubmitAsync(
        string founderUserId,
        LegendConnectKnowledgeSubmission submission,
        CancellationToken cancellationToken = default)
    {
        var sourceLanguage = await _languages.NormalizeEnabledTranslationLanguageAsync(
            submission.SourceLanguageCode, cancellationToken);
        if (sourceLanguage is null)
            return Rejected("invalid_source_entry", "An enabled source language is required.");

        var rawText = NormalizeRawText(submission.SourceText);
        if (string.IsNullOrWhiteSpace(rawText) || rawText.Length > MaximumRawCharacters)
        {
            return Rejected(
                "invalid_founder_training_submission",
                $"Founder training must contain 1–{MaximumRawCharacters:N0} characters.",
                sourceLanguage);
        }

        var units = LegendFounderTrainingSegmenter.Segment(rawText);
        if (units.Count is 0 or > MaximumAtomicUnits)
        {
            return Rejected(
                "invalid_founder_training_submission",
                $"Founder training must decompose into 1–{MaximumAtomicUnits:N0} reusable units.",
                sourceLanguage);
        }

        var rawHash = LegendLanguageIdentity.TextHash(rawText);
        var existing = await _db.Set<LegendFounderTrainingSubmission>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.SourceLanguageCode == sourceLanguage && item.RawTextHash == rawHash, cancellationToken);
        if (existing is not null)
            return await ExistingSubmissionResultAsync(existing, sourceLanguage, cancellationToken);

        try
        {
            return await IngestCoreAsync(
                founderUserId,
                sourceLanguage,
                rawText,
                rawHash,
                submission.ContextCategory,
                submission.UsageRegister,
                submission.RegionalVariant,
                units,
                legacySource: null,
                processingState: "Ingested",
                cancellationToken);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            var concurrent = await _db.Set<LegendFounderTrainingSubmission>()
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.SourceLanguageCode == sourceLanguage && item.RawTextHash == rawHash, cancellationToken);
            return concurrent is null
                ? Rejected("founder_training_conflict", "Founder training could not be saved. Please try again.", sourceLanguage)
                : await ExistingSubmissionResultAsync(concurrent, sourceLanguage, cancellationToken);
        }
    }

    /// <summary>
    /// Bounded, idempotent migration of pre-decomposition Founder source seeds.
    /// It never keys on literal content or a target language and preserves the
    /// old rows as historical evidence while removing them from active reuse.
    /// </summary>
    public async Task<LegendFounderTrainingReconciliationResult> ReconcileLegacyAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        var enabledLanguages = await _languages.ListEnabledTranslationLanguagesAsync(cancellationToken);
        var enabledCodes = enabledLanguages.Select(item => item.Code).ToArray();
        if (enabledCodes.Length == 0)
            return new LegendFounderTrainingReconciliationResult(0, 0, 0, 0, 0);

        // A prior reconciliation can already have recorded the raw
        // submission while a process was interrupted before every derived
        // target artifact was retired. Revisit only those explicit legacy
        // lineages; do not infer corruption from target text or language.
        var supersededRelationships = await ReconcileKnownLegacyDerivedArtifactsAsync(take, cancellationToken);

        // Run this before selecting legacy source assets so a historical
        // provider target that was mislabeled FounderApproved cannot be
        // mistaken for another Founder source submission.
        await ReconcileProviderDerivedTargetProvenanceAsync(take, cancellationToken);

        var sources = await _db.Set<LegendLanguageTextUnit>()
            .Where(item => item.IsTrainingEligible && item.Provenance == "FounderApproved" &&
                enabledCodes.Contains(item.LanguageCode))
            .Where(item => !_db.Set<LegendFounderTrainingSubmissionUnit>()
                .Any(unit => unit.TextUnitId == item.Id))
            .Where(item => !_db.Set<LegendFounderTrainingSubmission>()
                .Any(submission => submission.LegacySourceTextUnitId == item.Id))
            .Where(item => !_db.Set<LegendTranslationAlignment>()
                .Any(alignment => alignment.SourceTextUnitId == item.Id &&
                    alignment.SupersededUtc == null && alignment.HumanVerified))
            // A human-verified target is a legitimate explicit language
            // asset, not a pre-decomposition Founder source block. This also
            // preserves valid multi-sentence translations without a
            // content-based exception.
            .Where(item => !_db.Set<LegendTranslationAlignment>()
                .Any(alignment => alignment.TargetTextUnitId == item.Id &&
                    alignment.SupersededUtc == null && alignment.HumanVerified))
            .OrderBy(item => item.CreatedUtc)
            .Take(Math.Clamp(take, 1, 100))
            .ToListAsync(cancellationToken);

        var reviewed = 0;
        var reconciled = 0;
        var atomicUnits = 0;
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var units = LegendFounderTrainingSegmenter.Segment(source.Text);
            if (units.Count == 0)
                continue;

            var context = await _db.Set<LegendLanguageContextRelationship>()
                .AsNoTracking()
                .Where(item => item.SourceTextUnitId == source.Id && item.RelatedTextUnitId == source.Id &&
                    item.PairKey == null && item.SupersededUtc == null)
                .OrderByDescending(item => item.UpdatedUtc)
                .Select(item => new LegacyContextMetadata(item.ContextCategory, item.UsageRegister, item.RegionalVariant))
                .FirstOrDefaultAsync(cancellationToken);
            var founder = await _db.Set<LegendConnectKnowledgeAuditEntry>()
                .AsNoTracking()
                .Where(item => item.TextUnitId == source.Id && item.Action == "FounderKnowledgeSubmitted")
                .OrderByDescending(item => item.OccurredUtc)
                .Select(item => item.FounderUserId)
                .FirstOrDefaultAsync(cancellationToken);

            if (units.Count == 1)
            {
                await MarkLegacyAtomicReviewedAsync(source, founder, context, units[0], cancellationToken);
                reviewed++;
                continue;
            }

            var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null
                ? await _db.Database.BeginTransactionAsync(cancellationToken)
                : null;
            try
            {
                var result = await IngestCoreAsync(
                    founder ?? "legacy-founder-reconciliation",
                    source.LanguageCode,
                    source.Text,
                    LegendLanguageIdentity.TextHash(source.Text),
                    context?.ContextCategory,
                    context?.UsageRegister,
                    context?.RegionalVariant,
                    units,
                    source,
                    "Reconciled",
                    cancellationToken);
                if (!result.Succeeded)
                {
                    if (transaction is not null)
                        await transaction.RollbackAsync(cancellationToken);
                    continue;
                }

                supersededRelationships += await DecommissionLegacySourceAsync(source, cancellationToken);
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);
                reconciled++;
                atomicUnits += result.AtomicUnitCount;
            }
            catch
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(CancellationToken.None);
                _db.ChangeTracker.Clear();
                throw;
            }
            finally
            {
                if (transaction is not null)
                    await transaction.DisposeAsync();
            }
        }

        // Raw-source deduplication is intentionally unrelated to evaluator
        // freshness. Revisit retained atomic submissions only when this
        // deployment has advanced the canonical intelligence capability. The
        // replay performs the same atomic observation used by new source
        // ingestion; it never guesses a semantic transition from adjacency.
        var capabilityReplayed = await ReconcileStaleCapabilityProcessingAsync(
            take,
            enabledCodes,
            cancellationToken);

        if ((reconciled > 0 || reviewed > 0 || supersededRelationships > 0 || capabilityReplayed > 0) && _operations is not null)
        {
            await _operations.TryRecordAsync(
                "FounderTrainingReconciliation",
                "Info",
                "Processed",
                errorCode: null,
                summary: $"Reconciled {reconciled} legacy multi-unit Founder submission(s); reviewed {reviewed} already-atomic source asset(s); replayed {capabilityReplayed} retained submission(s) for evaluator v{LegendConnectLanguageIntelligenceEvaluatorVersion.Current}; retired {supersededRelationships} lineage-bound derived artifact(s).",
                cancellationToken: cancellationToken);
        }
        return new LegendFounderTrainingReconciliationResult(
            reviewed,
            reconciled,
            atomicUnits,
            supersededRelationships,
            capabilityReplayed);
    }

    private async Task<int> ReconcileStaleCapabilityProcessingAsync(
        int take,
        IReadOnlyCollection<string> enabledCodes,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var evaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current;
        var candidateIds = await _db.Set<LegendFounderTrainingSubmission>()
            .AsNoTracking()
            .Where(item => enabledCodes.Contains(item.SourceLanguageCode) &&
                item.CompletedLanguageIntelligenceEvaluatorVersion < evaluatorVersion &&
                (item.LanguageIntelligenceReevaluationLeaseExpiresUtc == null ||
                 item.LanguageIntelligenceReevaluationLeaseExpiresUtc < now))
            .OrderBy(item => item.CreatedUtc)
            .ThenBy(item => item.Id)
            .Take(Math.Clamp(take, 1, 100))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);

        var replayed = 0;
        foreach (var id in candidateIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claim = await TryClaimStaleCapabilitySubmissionAsync(
                id,
                evaluatorVersion,
                cancellationToken);
            if (claim is null)
                continue;

            try
            {
                var submission = await _db.Set<LegendFounderTrainingSubmission>()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
                if (submission is null)
                    continue;

                // The current governed semantic-transition capability is
                // English-only. Other enabled source languages have no
                // cross-language fallback or inferred transition path; their
                // version watermark simply records that no English-only work
                // applies to this isolated submission.
                if (string.Equals(submission.SourceLanguageCode, "en", StringComparison.OrdinalIgnoreCase))
                {
                    var sourceUnits = await (
                        from unit in _db.Set<LegendFounderTrainingSubmissionUnit>().AsNoTracking()
                        join textUnit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                            on unit.TextUnitId equals textUnit.Id
                        where unit.SubmissionId == submission.Id &&
                            textUnit.LanguageCode == submission.SourceLanguageCode &&
                            textUnit.IsTrainingEligible
                        orderby unit.SequenceNumber
                        select new { unit, textUnit }
                    ).ToListAsync(cancellationToken);

                    // A retained submission without its atomic canonical
                    // units is not current. Leave its claim releasable for
                    // diagnosis/recovery rather than marking incomplete data
                    // as capability-complete.
                    if (sourceUnits.Count != submission.AtomicUnitCount)
                    {
                        await ReleaseStaleCapabilityClaimAsync(claim, cancellationToken);
                        continue;
                    }

                    var atomicUnits = sourceUnits
                        .Select(item => new LegendFounderTrainingAtomicUnit(
                            item.textUnit.Text,
                            item.unit.UnitType,
                            item.unit.SequenceNumber,
                            item.unit.ParagraphNumber))
                        .ToArray();
                    var textUnitsByHash = sourceUnits
                        .Select(item => item.textUnit)
                        .ToDictionary(item => item.NormalizedHash, StringComparer.Ordinal);

                    await _curriculum.ObserveFounderEnglishAtomicUnitsAsync(
                        submission.Id,
                        atomicUnits,
                        textUnitsByHash,
                        cancellationToken);
                }

                if (await CompleteStaleCapabilityClaimAsync(claim, evaluatorVersion, cancellationToken))
                    replayed++;
            }
            finally
            {
                _db.ChangeTracker.Clear();
            }
        }

        return replayed;
    }

    private async Task<StaleCapabilitySubmissionClaim?> TryClaimStaleCapabilitySubmissionAsync(
        Guid id,
        int evaluatorVersion,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var leaseExpires = now.AddMinutes(10);
        if (_db.Database.IsRelational())
        {
            var updated = await _db.Set<LegendFounderTrainingSubmission>()
                .Where(item => item.Id == id &&
                    item.CompletedLanguageIntelligenceEvaluatorVersion < evaluatorVersion &&
                    (item.LanguageIntelligenceReevaluationLeaseExpiresUtc == null ||
                     item.LanguageIntelligenceReevaluationLeaseExpiresUtc < now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.LanguageIntelligenceReevaluationLeaseExpiresUtc, leaseExpires),
                    cancellationToken);
            return updated == 1
                ? new StaleCapabilitySubmissionClaim(id, leaseExpires)
                : null;
        }

        var submission = await _db.Set<LegendFounderTrainingSubmission>()
            .SingleOrDefaultAsync(item => item.Id == id &&
                item.CompletedLanguageIntelligenceEvaluatorVersion < evaluatorVersion &&
                (item.LanguageIntelligenceReevaluationLeaseExpiresUtc == null ||
                 item.LanguageIntelligenceReevaluationLeaseExpiresUtc < now),
                cancellationToken);
        if (submission is null)
            return null;

        submission.LanguageIntelligenceReevaluationLeaseExpiresUtc = leaseExpires;
        await _db.SaveChangesAsync(cancellationToken);
        return new StaleCapabilitySubmissionClaim(id, leaseExpires);
    }

    private async Task<bool> CompleteStaleCapabilityClaimAsync(
        StaleCapabilitySubmissionClaim claim,
        int evaluatorVersion,
        CancellationToken cancellationToken)
    {
        if (_db.Database.IsRelational())
        {
            var updated = await _db.Set<LegendFounderTrainingSubmission>()
                .Where(item => item.Id == claim.SubmissionId &&
                    item.LanguageIntelligenceReevaluationLeaseExpiresUtc == claim.LeaseExpiresUtc)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.CompletedLanguageIntelligenceEvaluatorVersion, evaluatorVersion)
                    .SetProperty(item => item.ProcessedUtc, DateTime.UtcNow)
                    .SetProperty(item => item.LanguageIntelligenceReevaluationLeaseExpiresUtc, (DateTime?)null),
                    cancellationToken);
            return updated == 1;
        }

        var submission = await _db.Set<LegendFounderTrainingSubmission>()
            .SingleOrDefaultAsync(item => item.Id == claim.SubmissionId &&
                item.LanguageIntelligenceReevaluationLeaseExpiresUtc == claim.LeaseExpiresUtc,
                cancellationToken);
        if (submission is null)
            return false;

        submission.CompletedLanguageIntelligenceEvaluatorVersion = evaluatorVersion;
        submission.ProcessedUtc = DateTime.UtcNow;
        submission.LanguageIntelligenceReevaluationLeaseExpiresUtc = null;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task ReleaseStaleCapabilityClaimAsync(
        StaleCapabilitySubmissionClaim claim,
        CancellationToken cancellationToken)
    {
        if (_db.Database.IsRelational())
        {
            await _db.Set<LegendFounderTrainingSubmission>()
                .Where(item => item.Id == claim.SubmissionId &&
                    item.LanguageIntelligenceReevaluationLeaseExpiresUtc == claim.LeaseExpiresUtc)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.LanguageIntelligenceReevaluationLeaseExpiresUtc, (DateTime?)null),
                    cancellationToken);
            return;
        }

        var submission = await _db.Set<LegendFounderTrainingSubmission>()
            .SingleOrDefaultAsync(item => item.Id == claim.SubmissionId &&
                item.LanguageIntelligenceReevaluationLeaseExpiresUtc == claim.LeaseExpiresUtc,
                cancellationToken);
        if (submission is null)
            return;

        submission.LanguageIntelligenceReevaluationLeaseExpiresUtc = null;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> ReconcileKnownLegacyDerivedArtifactsAsync(
        int take,
        CancellationToken cancellationToken)
    {
        var legacySourceIds = await _db.Set<LegendFounderTrainingSubmission>()
            .Where(item => item.LegacySourceTextUnitId != null && item.AtomicUnitCount > 1)
            .OrderBy(item => item.CreatedUtc)
            .Take(Math.Clamp(take, 1, 100))
            .Select(item => item.LegacySourceTextUnitId!.Value)
            .ToListAsync(cancellationToken);
        if (legacySourceIds.Count == 0)
            return 0;

        var sources = await _db.Set<LegendLanguageTextUnit>()
            .Where(item => legacySourceIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        var reconciled = 0;
        foreach (var source in sources)
            reconciled += await DecommissionLegacySourceAsync(source, cancellationToken);
        return reconciled;
    }

    private async Task ReconcileProviderDerivedTargetProvenanceAsync(
        int take,
        CancellationToken cancellationToken)
    {
        var providerDerived = await (
            from alignment in _db.Set<LegendTranslationAlignment>()
            join source in _db.Set<LegendLanguageTextUnit>() on alignment.SourceTextUnitId equals source.Id
            join target in _db.Set<LegendLanguageTextUnit>() on alignment.TargetTextUnitId equals target.Id
            where alignment.SupersededUtc == null && !alignment.HumanVerified &&
                target.IsTrainingEligible && target.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                _db.Set<LegendFounderTrainingSubmissionUnit>().Any(unit => unit.TextUnitId == source.Id)
            orderby alignment.UpdatedUtc
            select new { alignment, target }
        ).Take(Math.Clamp(take, 1, 100)).ToListAsync(cancellationToken);

        foreach (var item in providerDerived)
        {
            var hasHumanVerifiedReference = await _db.Set<LegendTranslationAlignment>()
                .AnyAsync(alignment => alignment.TargetTextUnitId == item.target.Id &&
                    alignment.SupersededUtc == null && alignment.HumanVerified, cancellationToken);
            if (hasHumanVerifiedReference)
                continue;

            var changed = false;
            if (!string.Equals(item.target.Provenance, LegendConnectKnowledgeProvenance.ProviderDerived, StringComparison.Ordinal))
            {
                item.target.Provenance = LegendConnectKnowledgeProvenance.ProviderDerived;
                item.target.UpdatedUtc = DateTime.UtcNow;
                changed = true;
            }

            var contexts = await _db.Set<LegendLanguageContextRelationship>()
                .Where(context => context.SupersededUtc == null &&
                    context.PairKey == item.alignment.PairKey &&
                    context.SourceTextUnitId == item.alignment.SourceTextUnitId &&
                    context.RelatedTextUnitId == item.alignment.TargetTextUnitId)
                .ToListAsync(cancellationToken);
            foreach (var context in contexts)
            {
                if (string.Equals(context.Provenance, LegendConnectKnowledgeProvenance.ProviderDerived, StringComparison.Ordinal) &&
                    string.Equals(context.QualityState, "Observation", StringComparison.Ordinal) &&
                    context.Confidence <= .5m)
                    continue;
                context.Provenance = LegendConnectKnowledgeProvenance.ProviderDerived;
                context.QualityState = "Observation";
                context.Confidence = Math.Min(context.Confidence, .5m);
                context.UpdatedUtc = DateTime.UtcNow;
                changed = true;
            }

            var examples = await _db.Set<LegendCurriculumExample>()
                .Where(example => example.TextUnitId == item.target.Id && example.SupersededUtc == null &&
                    example.DerivedFromCurriculumExampleId != null)
                .ToListAsync(cancellationToken);
            foreach (var example in examples)
            {
                if (string.Equals(example.Provenance, LegendConnectKnowledgeProvenance.ProviderDerived, StringComparison.Ordinal))
                    continue;
                example.Provenance = LegendConnectKnowledgeProvenance.ProviderDerived;
                example.UpdatedUtc = DateTime.UtcNow;
                changed = true;
            }

            if (examples.Count > 0)
            {
                var exampleIds = examples.Select(example => example.Id).ToArray();
                var evidence = await _db.Set<LegendLanguageStructuralEvidence>()
                    .Where(evidence => evidence.SupersededUtc == null &&
                        (exampleIds.Contains(evidence.BaselineCurriculumExampleId) ||
                         exampleIds.Contains(evidence.ComparedCurriculumExampleId)))
                    .ToListAsync(cancellationToken);
                foreach (var structuralEvidence in evidence)
                {
                    if (string.Equals(structuralEvidence.Provenance, LegendConnectKnowledgeProvenance.ProviderDerived, StringComparison.Ordinal))
                        continue;
                    structuralEvidence.Provenance = LegendConnectKnowledgeProvenance.ProviderDerived;
                    changed = true;
                }

                var patternIds = evidence.Select(evidence => evidence.StructuralPatternId).Distinct().ToArray();
                if (patternIds.Length > 0)
                {
                    var patterns = await _db.Set<LegendLanguageStructuralPattern>()
                        .Where(pattern => patternIds.Contains(pattern.Id) && pattern.SupersededUtc == null)
                        .ToListAsync(cancellationToken);
                    foreach (var pattern in patterns)
                    {
                        if (string.Equals(pattern.Provenance, LegendConnectKnowledgeProvenance.ProviderDerived, StringComparison.Ordinal))
                            continue;
                        pattern.Provenance = LegendConnectKnowledgeProvenance.ProviderDerived;
                        pattern.UpdatedUtc = DateTime.UtcNow;
                        changed = true;
                    }
                }
            }

            if (changed)
                await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<LegendConnectKnowledgeSubmissionResult> IngestCoreAsync(
        string founderUserId,
        string sourceLanguage,
        string rawText,
        string rawHash,
        string? contextCategory,
        string? usageRegister,
        string? regionalVariant,
        IReadOnlyList<LegendFounderTrainingAtomicUnit> units,
        LegendLanguageTextUnit? legacySource,
        string processingState,
        CancellationToken cancellationToken)
    {
        var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var submission = new LegendFounderTrainingSubmission
            {
                Id = Guid.NewGuid(),
                FounderUserId = Bound(founderUserId, 450),
                SourceLanguageCode = sourceLanguage,
                RawText = rawText,
                RawTextHash = rawHash,
                ContextCategory = Bound(contextCategory, 120),
                UsageRegister = Bound(usageRegister, 80),
                RegionalVariant = Bound(regionalVariant, 80),
                LegacySourceTextUnitId = legacySource?.Id,
                RawCharacterCount = rawText.Length,
                AtomicUnitCount = units.Count,
                CompletedLanguageIntelligenceEvaluatorVersion = 0,
                ProcessingState = processingState,
                CreatedUtc = DateTime.UtcNow,
                ProcessedUtc = null
            };
            _db.Set<LegendFounderTrainingSubmission>().Add(submission);
            await _db.SaveChangesAsync(cancellationToken);

            var batch = await _corpus.SubmitApprovedSourceSeedsAsync(
                sourceLanguage,
                units.Select(item => item.Text).ToArray(),
                contextCategory,
                usageRegister,
                regionalVariant,
                cancellationToken);
            // Retain the receipt while its inputs are authoritative. Later
            // corpus reuse cannot reliably reconstruct whether a unit was new
            // at the time this raw Founder submission was accepted.
            submission.NewCanonicalUnitCount = batch.CreatedUnitCount;
            submission.ReusedCanonicalUnitCount = batch.ReusedUnitCount;
            submission.QueuedCoverageCount = batch.QueuedCoverageCount;
            foreach (var unit in units)
            {
                var hash = LegendLanguageIdentity.TextHash(unit.Text);
                _db.Set<LegendFounderTrainingSubmissionUnit>().Add(new LegendFounderTrainingSubmissionUnit
                {
                    Id = Guid.NewGuid(),
                    SubmissionId = submission.Id,
                    TextUnitId = batch.TextUnitsByHash[hash].Id,
                    SequenceNumber = unit.SequenceNumber,
                    ParagraphNumber = unit.ParagraphNumber,
                    UnitType = unit.UnitType,
                    CreatedUtc = DateTime.UtcNow
                });
            }
            await _db.SaveChangesAsync(cancellationToken);

            if (string.Equals(sourceLanguage, "en", StringComparison.OrdinalIgnoreCase))
            {
                await _curriculum.ObserveFounderEnglishAtomicUnitsAsync(
                    submission.Id,
                    units,
                    batch.TextUnitsByHash,
                    cancellationToken);
            }

            submission.ProcessingState = processingState;
            submission.CompletedLanguageIntelligenceEvaluatorVersion =
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current;
            submission.ProcessedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            var first = units[0];
            return new LegendConnectKnowledgeSubmissionResult(
                true,
                false,
                null,
                $"Accepted {units.Count} atomic learning unit(s): {batch.CreatedUnitCount} new, {batch.ReusedUnitCount} reused, and {batch.QueuedCoverageCount} coverage item(s) queued through the existing autonomous pipeline.",
                sourceLanguage,
                null,
                null,
                batch.TextUnitsByHash[LegendLanguageIdentity.TextHash(first.Text)].Id,
                null,
                null,
                submission.Id,
                units.Count,
                batch.CreatedUnitCount,
                batch.ReusedUnitCount,
                batch.QueuedCoverageCount);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private async Task<LegendConnectKnowledgeSubmissionResult> ExistingSubmissionResultAsync(
        LegendFounderTrainingSubmission submission,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        var units = await _db.Set<LegendFounderTrainingSubmissionUnit>()
            .Where(item => item.SubmissionId == submission.Id)
            .OrderBy(item => item.SequenceNumber)
            .ToListAsync(cancellationToken);
        var capabilityCurrent = submission.CompletedLanguageIntelligenceEvaluatorVersion >=
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current;
        return new LegendConnectKnowledgeSubmissionResult(
            true,
            true,
            null,
            capabilityCurrent
                ? $"This Founder training submission was already ingested as {units.Count} atomic learning unit(s); existing knowledge and coverage were reused."
                : $"This Founder training submission was already ingested as {units.Count} atomic learning unit(s); existing knowledge and coverage were reused, and its retained atomic evidence is eligible for bounded evaluator v{LegendConnectLanguageIntelligenceEvaluatorVersion.Current} reconciliation.",
            sourceLanguage,
            null,
            null,
            units.FirstOrDefault()?.TextUnitId,
            null,
            null,
            submission.Id,
            units.Count,
            0,
            units.Count,
            0);
    }

    private async Task MarkLegacyAtomicReviewedAsync(
        LegendLanguageTextUnit source,
        string? founderUserId,
        LegacyContextMetadata? context,
        LegendFounderTrainingAtomicUnit unit,
        CancellationToken cancellationToken)
    {
        var rawHash = LegendLanguageIdentity.TextHash(source.Text);
        if (await _db.Set<LegendFounderTrainingSubmission>().AnyAsync(item =>
                item.SourceLanguageCode == source.LanguageCode && item.RawTextHash == rawHash, cancellationToken))
            return;
        var submission = new LegendFounderTrainingSubmission
        {
            Id = Guid.NewGuid(),
            FounderUserId = founderUserId,
            SourceLanguageCode = source.LanguageCode,
            RawText = source.Text,
            RawTextHash = rawHash,
            ContextCategory = context?.ContextCategory,
            UsageRegister = context?.UsageRegister,
            RegionalVariant = context?.RegionalVariant,
            LegacySourceTextUnitId = source.Id,
            RawCharacterCount = source.Text.Length,
            AtomicUnitCount = 1,
            ProcessingState = "LegacyAtomicReviewed",
            CreatedUtc = DateTime.UtcNow,
            ProcessedUtc = DateTime.UtcNow
        };
        _db.Set<LegendFounderTrainingSubmission>().Add(submission);
        _db.Set<LegendFounderTrainingSubmissionUnit>().Add(new LegendFounderTrainingSubmissionUnit
        {
            Id = Guid.NewGuid(),
            SubmissionId = submission.Id,
            TextUnitId = source.Id,
            SequenceNumber = unit.SequenceNumber,
            ParagraphNumber = unit.ParagraphNumber,
            UnitType = unit.UnitType,
            CreatedUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> DecommissionLegacySourceAsync(
        LegendLanguageTextUnit source,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var alignments = await _db.Set<LegendTranslationAlignment>()
            .Where(item => item.SourceTextUnitId == source.Id && item.SupersededUtc == null)
            .ToListAsync(cancellationToken);
        var alignmentIds = alignments.Select(item => item.Id).ToHashSet();
        var pairKeys = alignments.Select(item => item.PairKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var targetIds = alignments.Select(item => item.TargetTextUnitId).Distinct().ToArray();
        var activeTargetReferences = await _db.Set<LegendTranslationAlignment>().AsNoTracking()
            .Where(item => item.SupersededUtc == null && targetIds.Contains(item.TargetTextUnitId))
            .Select(item => new { item.Id, item.TargetTextUnitId })
            .ToListAsync(cancellationToken);
        var retainedTargetIds = activeTargetReferences
            .Where(item => !alignmentIds.Contains(item.Id))
            .Select(item => item.TargetTextUnitId)
            .ToHashSet();
        var retiredTargetIds = targetIds.Where(item => !retainedTargetIds.Contains(item)).ToArray();
        var targets = retiredTargetIds.Length == 0
            ? new List<LegendLanguageTextUnit>()
            : await _db.Set<LegendLanguageTextUnit>()
                .Where(item => retiredTargetIds.Contains(item.Id))
                .ToListAsync(cancellationToken);

        var contexts = await _db.Set<LegendLanguageContextRelationship>()
            .Where(item => item.SupersededUtc == null &&
                (item.SourceTextUnitId == source.Id || item.RelatedTextUnitId == source.Id ||
                 retiredTargetIds.Contains(item.SourceTextUnitId) || retiredTargetIds.Contains(item.RelatedTextUnitId)))
            .ToListAsync(cancellationToken);
        var candidates = await _db.Set<LegendCorpusCandidate>()
            .Where(item => item.SourceLanguageCode == source.LanguageCode &&
                item.SourceTextHash == source.NormalizedHash &&
                (item.IsApproved || item.ProcessingState != "Superseded" ||
                 item.FailureCode != "legacy_multi_unit_reconciled"))
            .ToListAsync(cancellationToken);
        var learningEvents = await _db.Set<LegendTranslationLearningEvent>()
            .Where(item => item.SourceLanguageCode == source.LanguageCode &&
                item.SourceTextHash == source.NormalizedHash && item.ProcessingState != "Superseded")
            .ToListAsync(cancellationToken);
        var targetsToRetire = targets.Where(item => item.IsTrainingEligible).ToList();
        if (alignments.Count == 0 && contexts.Count == 0 && candidates.Count == 0 && learningEvents.Count == 0 &&
            !source.IsTrainingEligible && targetsToRetire.Count == 0)
            return 0;

        foreach (var alignment in alignments)
        {
            alignment.SupersededUtc = now;
            alignment.UpdatedUtc = now;
        }

        foreach (var context in contexts)
        {
            context.SupersededUtc = now;
            context.UpdatedUtc = now;
        }

        foreach (var candidate in candidates)
        {
            candidate.IsApproved = false;
            candidate.ProcessingState = "Superseded";
            candidate.FailureCode = "legacy_multi_unit_reconciled";
            candidate.ProcessedUtc = now;
            candidate.LeaseExpiresUtc = null;
        }

        foreach (var learningEvent in learningEvents)
        {
            learningEvent.ProcessingState = "Superseded";
            learningEvent.PromotionOutcome = "Superseded";
            learningEvent.FailureCode = "legacy_multi_unit_reconciled";
            learningEvent.ProcessedUtc = now;
            learningEvent.LeaseExpiresUtc = null;
        }

        source.IsTrainingEligible = false;
        source.UpdatedUtc = now;
        foreach (var target in targetsToRetire)
        {
            target.IsTrainingEligible = false;
            target.UpdatedUtc = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        var supersededTextIds = retiredTargetIds.Append(source.Id).ToArray();
        await _curriculum.ReconcileSupersededExamplesAsync(supersededTextIds, cancellationToken);
        foreach (var pairKey in pairKeys.Distinct(StringComparer.OrdinalIgnoreCase))
            await _corpus.RefreshPairCoverageAsync(pairKey, cancellationToken);
        return alignments.Count + contexts.Count + candidates.Count + learningEvents.Count + targetsToRetire.Count + 1;
    }

    private static LegendConnectKnowledgeSubmissionResult Rejected(string errorCode, string message, string sourceLanguage = "") => new(
        false, false, errorCode, message, sourceLanguage, null, null, null, null, null);

    private static string NormalizeRawText(string? value) =>
        (value ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim();

    private static string? Bound(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized[..Math.Min(normalized.Length, maximumLength)];
    }
}

internal sealed record LegendFounderTrainingReconciliationResult(
    int LegacyAtomicReviewedCount,
    int ReconciledSubmissionCount,
    int AtomicUnitCount,
    int SupersededRelationshipCount,
    int CapabilityReplayedSubmissionCount);

internal sealed record LegendFounderTrainingAtomicUnit(
    string Text,
    string UnitType,
    int SequenceNumber,
    int ParagraphNumber);

internal static class LegendFounderTrainingSegmenter
{
    private static readonly Regex ListPrefix = new(@"^\s*(?:(?:[-*•]+)|(?:\d+[.)]))\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "e.g", "i.e", "a.m", "p.m"
    };

    internal static IReadOnlyList<LegendFounderTrainingAtomicUnit> Segment(
        string rawText) =>
        SegmentCore(
            rawText,
            deduplicate: true,
            splitStrongClauses: false);

    internal static IReadOnlyList<LegendFounderTrainingAtomicUnit>
        SegmentForComposition(string rawText) =>
        SegmentCore(
            rawText,
            deduplicate: false,
            splitStrongClauses: true);

    private static IReadOnlyList<LegendFounderTrainingAtomicUnit>
        SegmentCore(
            string rawText,
            bool deduplicate,
            bool splitStrongClauses)
    {
        var paragraphs = Regex.Split(
                rawText
                    .Replace("\r\n", "\n")
                    .Replace('\r', '\n'),
                @"\n\s*\n")
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToArray();

        var discovered =
            new List<(string Text, string UnitType, int Paragraph)>();

        for (var paragraphIndex = 0;
             paragraphIndex < paragraphs.Length;
             paragraphIndex++)
        {
            var paragraph = paragraphs[paragraphIndex];

            var paragraphUnits =
                new List<(string Text, string UnitType)>();

            foreach (var rawLine in paragraph.Split(
                         '\n',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                var line = ListPrefix
                    .Replace(rawLine, string.Empty)
                    .Trim();

                if (line.Length == 0)
                    continue;

                paragraphUnits.AddRange(
                    SegmentLine(
                        line,
                        splitStrongClauses));
            }

            var isParagraph =
                paragraphUnits.Count > 1;

            discovered.AddRange(
                paragraphUnits.Select(item => (
                    item.Text,
                    isParagraph &&
                    item.UnitType is
                        "Sentence" or
                        "Question" or
                        "Exclamation"
                        ? "ParagraphSentence"
                        : item.UnitType,
                    paragraphIndex + 1)));
        }

        var hashes =
            new HashSet<string>(StringComparer.Ordinal);

        var result =
            new List<LegendFounderTrainingAtomicUnit>(
                discovered.Count);

        foreach (var item in discovered)
        {
            var text =
                LegendLanguageIdentity.NormalizeText(
                    item.Text);

            if (string.IsNullOrWhiteSpace(text) ||
                text.Length > 2_000)
            {
                continue;
            }

            var hash =
                LegendLanguageIdentity.TextHash(text);

            if (deduplicate &&
                !hashes.Add(hash))
            {
                continue;
            }

            if (!deduplicate)
                hashes.Add(hash);

            result.Add(
                new LegendFounderTrainingAtomicUnit(
                    text,
                    item.UnitType,
                    result.Count + 1,
                    item.Paragraph));
        }

        return result;
    }

    private static IReadOnlyList<(string Text, string UnitType)> SegmentLine(
        string line,
        bool splitStrongClauses = false)
    {
        var recoveredList =
            RecoverCollapsedLeadingLexicalList(
                line,
                splitStrongClauses);
        if (recoveredList is not null)
            return recoveredList;

        var segments = new List<(string Text, string UnitType)>();
        var start = 0;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];

            if (splitStrongClauses &&
                current == ';')
            {
                var clause =
                    line[start..index].Trim();

                if (clause.Length > 0)
                    segments.Add((clause, "Clause"));

                start = index + 1;
                continue;
            }

            if (current is not ('.' or '?' or '!') ||
                !IsTerminator(line, index))
            {
                continue;
            }

            var end = index + 1;
            while (end < line.Length && line[end] is '.' or '!' or '?' or '”' or '"' or '\'' or ')' or ']')
                end++;
            var sentence = line[start..end].Trim();
            if (sentence.Length > 0)
                segments.Add((sentence, Classify(sentence)));
            start = end;
        }
        var remainder = line[start..].Trim();
        if (remainder.Length > 0)
            segments.Add((remainder, Classify(remainder)));
        return segments;
    }

    /// <summary>
    /// Older canonical text storage normalized whitespace, so a handwritten
    /// list immediately followed by a capitalized sentence can lose its line
    /// breaks (for example, "person family I understand you."). Recover only
    /// this unambiguous shape; ordinary prose remains sentence-scanned.
    /// </summary>
    private static IReadOnlyList<(string Text, string UnitType)>?
        RecoverCollapsedLeadingLexicalList(
            string line,
            bool splitStrongClauses = false)
    {
        var sentenceStart = Regex.Match(line,
            @"\s+(?=\p{Lu})",
            RegexOptions.CultureInvariant);
        if (!sentenceStart.Success)
            return null;

        var prefix = line[..sentenceStart.Index].Trim(' ', ',', ';', ':');
        var remainder = line[(sentenceStart.Index + sentenceStart.Length)..].Trim();
        var items = prefix.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (items.Length is 0 or > 20 || remainder.Length == 0 ||
            items.Any(item => item.Any(char.IsPunctuation) || item.Length > 80 || !char.IsLower(item[0])))
            return null;

        var recovered = items
            .Select(item => (item, "Lexical"))
            .Concat(
                SegmentLine(
                    remainder,
                    splitStrongClauses))
            .ToList();
        return recovered;
    }

    private static bool IsTerminator(string line, int index)
    {
        var current = line[index];
        if (current is '?' or '!')
            return true;
        if (index + 1 < line.Length && !char.IsWhiteSpace(line[index + 1]) && line[index + 1] is not '”' and not '"' and not '\'')
            return false;
        var tokenStart = index - 1;
        while (tokenStart >= 0 && (char.IsLetter(line[tokenStart]) || line[tokenStart] == '.'))
            tokenStart--;
        var token = line[(tokenStart + 1)..index].Trim('.');
        var isInitialism = Regex.IsMatch(token, @"^(?:\p{L}\.)+\p{L}$", RegexOptions.CultureInvariant);
        return !Abbreviations.Contains(token) && !isInitialism;
    }

    private static string Classify(string text)
    {
        if (text.EndsWith('?'))
            return "Question";
        if (text.EndsWith('!'))
            return "Exclamation";
        if (text.EndsWith('.'))
            return "Sentence";
        return LegendLanguageIdentity.NormalizeText(text).Contains(' ') ? "Phrase" : "Lexical";
    }

}
