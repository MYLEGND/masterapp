using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Messaging;

/// <summary>
/// Generic, server-side curriculum authority. Founder-authored English examples
/// describe controlled semantic changes; every language derives its structural
/// observations only by comparing canonical examples in that same language.
/// This service owns neither a corpus nor a provider: it extends the existing
/// corpus candidate and Azure-expansion pipeline.
/// </summary>
internal interface ILegendConnectStructuralCompositionGate
{
    Task<LegendContextualTranslationSuggestion?> TryComposeAsync(
        string sourceLanguageCode,
        string targetLanguageCode,
        string text,
        CancellationToken cancellationToken = default);
}

internal sealed class LegendConnectCurriculumService : ILegendConnectStructuralCompositionGate
{
    private const int MaximumExamplesPerBatch = 100;
    private readonly MasterAppDbContext _db;
    private readonly ILegendLanguageRegistry _languages;
    private readonly LegendConnectCorpusService _corpus;

    public LegendConnectCurriculumService(
        MasterAppDbContext db,
        ILegendLanguageRegistry languages,
        LegendConnectCorpusService corpus)
    {
        _db = db;
        _languages = languages;
        _corpus = corpus;
    }

    public async Task<LegendConnectCurriculumSubmissionResult> SubmitFounderEnglishBatchAsync(
        LegendConnectCurriculumBatchSubmission submission,
        CancellationToken cancellationToken = default)
    {
        var familyKey = NormalizeFamilyKey(submission.FamilyKey);
        var examples = NormalizeExamples(submission.Examples);
        var english = await _languages.NormalizeEnabledTranslationLanguageAsync("en", cancellationToken);
        if (english is null || !string.Equals(english, "en", StringComparison.OrdinalIgnoreCase))
        {
            return Rejected("english_training_unavailable", "English must be an enabled direct Founder training language.", familyKey);
        }
        if (familyKey is null)
            return Rejected("invalid_curriculum_family", "Use a concise semantic family key such as possession.basic.", null);
        if (examples is null)
            return Rejected("invalid_curriculum_examples", "A structured curriculum family requires 2–100 distinct English examples with controlled variations.", familyKey);

        LegendCurriculumFamily family;
        try
        {
            family = await _db.Set<LegendCurriculumFamily>()
                .SingleOrDefaultAsync(item => item.FamilyKey == familyKey, cancellationToken)
                ?? new LegendCurriculumFamily
                {
                    Id = Guid.NewGuid(),
                    FamilyKey = familyKey,
                    SemanticCategory = NormalizeOptional(submission.SemanticCategory, 120),
                    Provenance = "FounderApproved",
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                };
            if (_db.Entry(family).State == EntityState.Detached)
                _db.Set<LegendCurriculumFamily>().Add(family);
            else if (!string.IsNullOrWhiteSpace(submission.SemanticCategory) && family.SemanticCategory is null)
            {
                family.SemanticCategory = NormalizeOptional(submission.SemanticCategory, 120);
                family.UpdatedUtc = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            family = await _db.Set<LegendCurriculumFamily>()
                .SingleAsync(item => item.FamilyKey == familyKey, cancellationToken);
        }

        var sourceExamples = new List<LegendCurriculumExample>(examples.Count);
        var createdSourceCount = 0;
        foreach (var example in examples)
        {
            var textHash = LegendLanguageIdentity.TextHash(example.Text);
            var textUnit = await _db.Set<LegendLanguageTextUnit>()
                .SingleOrDefaultAsync(item => item.LanguageCode == english && item.NormalizedHash == textHash, cancellationToken);
            if (textUnit is null)
            {
                var submitted = await _corpus.SubmitApprovedKnowledgeAsync(
                    new LegendConnectKnowledgeSubmission(
                        english,
                        example.Text,
                        null,
                        null,
                        family.SemanticCategory,
                        null,
                        null,
                        "FounderApproved"),
                    cancellationToken);
                if (!submitted.Succeeded && !submitted.DuplicatePrevented)
                    return Rejected(submitted.ErrorCode ?? "curriculum_source_rejected", submitted.Message ?? "The English curriculum example could not be retained.", familyKey);

                textUnit = submitted.SourceTextUnitId is { } textUnitId
                    ? await _db.Set<LegendLanguageTextUnit>().SingleAsync(item => item.Id == textUnitId, cancellationToken)
                    : await _db.Set<LegendLanguageTextUnit>().SingleAsync(item =>
                        item.LanguageCode == english && item.NormalizedHash == textHash, cancellationToken);
                createdSourceCount++;
            }

            var curriculumExample = await GetOrCreateExampleAsync(
                family,
                textUnit,
                english,
                derivedFromCurriculumExampleId: null,
                cancellationToken);
            await EnsureVariationsAsync(curriculumExample, example.Variations, cancellationToken);
            sourceExamples.Add(curriculumExample);
        }
        await _db.SaveChangesAsync(cancellationToken);

        var structuredSourceUnitIds = sourceExamples.Select(item => item.TextUnitId).Distinct().ToArray();
        var structuredSourceUnits = await _db.Set<LegendLanguageTextUnit>()
            .Where(item => structuredSourceUnitIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var structuredEnglishInputs = sourceExamples
            .DistinctBy(item => item.TextUnitId)
            .Select(item => new EnglishAtomicInput(
                structuredSourceUnits[item.TextUnitId],
                "StructuredExample",
                null,
                null,
                null))
            .ToList();
        await EnsureEnglishLexicalObservationsAsync(structuredEnglishInputs, cancellationToken);
        await AttachExplicitEnglishSemanticAnchorsAsync(family, sourceExamples, cancellationToken);

        // This is the existing expansion authority. It is idempotent by source
        // asset and directional pair, and it carries curriculum lineage only as
        // metadata for the already-approved work.
        foreach (var sourceExample in sourceExamples.DistinctBy(item => item.Id))
        {
            var sourceUnit = await _db.Set<LegendLanguageTextUnit>()
                .SingleAsync(item => item.Id == sourceExample.TextUnitId, cancellationToken);
            await _corpus.EnsureFounderSeedCandidatesAsync(
                sourceUnit,
                family.Id,
                sourceExample.Id,
                cancellationToken);
            await AttachExistingExpansionsAsync(sourceExample, sourceUnit, cancellationToken);
        }

        await AnalyzeFamilyLanguageAsync(family.Id, english, cancellationToken);
        var targetExpansionCount = await _db.Set<LegendCurriculumExample>()
            .CountAsync(item => item.CurriculumFamilyId == family.Id && item.LanguageCode != english, cancellationToken);
        return new LegendConnectCurriculumSubmissionResult(
            true,
            createdSourceCount == 0,
            null,
            createdSourceCount == 0
                ? "The curriculum family already matched canonical English entries; no duplicate learning confidence was created."
                : "Founder English curriculum was saved and queued through the existing Azure expansion pipeline.",
            family.FamilyKey,
            family.Id,
            sourceExamples.Select(item => item.Id).Distinct().Count(),
            targetExpansionCount);
    }

    /// <summary>
    /// Extends the existing curriculum authority with direct English surface
    /// evidence after the canonical Founder ingestion boundary has produced
    /// atomic units. This records only observed lexical identities, ordered
    /// boundaries, and neighboring sentences; semantic or grammatical claims
    /// still require controlled curriculum variations below.
    /// </summary>
    internal async Task ObserveFounderEnglishAtomicUnitsAsync(
        Guid trainingSubmissionId,
        IReadOnlyList<LegendFounderTrainingAtomicUnit> units,
        IReadOnlyDictionary<string, LegendLanguageTextUnit> textUnitsByHash,
        CancellationToken cancellationToken = default)
    {
        var inputs = units
            .Select(unit => new EnglishAtomicInput(
                textUnitsByHash[LegendLanguageIdentity.TextHash(unit.Text)],
                unit.UnitType,
                trainingSubmissionId,
                unit.SequenceNumber,
                unit.ParagraphNumber))
            .Where(item => item.TextUnit.IsTrainingEligible &&
                string.Equals(item.TextUnit.LanguageCode, "en", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (inputs.Count == 0)
            return;

        await EnsureEnglishLexicalObservationsAsync(inputs, cancellationToken);
        await EnsureParagraphNeighborRelationshipsAsync(inputs, cancellationToken);
    }

    /// <summary>
    /// Retires curriculum and structural observations that depend on canonical
    /// assets invalidated by legacy raw-submission reconciliation. New atomic
    /// examples are then analyzed normally by the same authority.
    /// </summary>
    internal async Task ReconcileSupersededExamplesAsync(
        IReadOnlyCollection<Guid> textUnitIds,
        CancellationToken cancellationToken = default)
    {
        if (textUnitIds.Count == 0)
            return;
        var now = DateTime.UtcNow;
        var lexicalOccurrences = await _db.Set<LegendLanguageLexicalOccurrence>()
            .Where(item => item.SupersededUtc == null && textUnitIds.Contains(item.TextUnitId))
            .ToListAsync(cancellationToken);
        foreach (var occurrence in lexicalOccurrences)
        {
            occurrence.SupersededUtc = now;
            occurrence.UpdatedUtc = now;
        }
        var lexicalRelationships = await _db.Set<LegendLanguageLexicalRelationship>()
            .Where(item => item.SupersededUtc == null && textUnitIds.Contains(item.TextUnitId))
            .ToListAsync(cancellationToken);
        foreach (var relationship in lexicalRelationships)
        {
            relationship.SupersededUtc = now;
            relationship.UpdatedUtc = now;
        }
        var anchors = await _db.Set<LegendLanguageCompositionalAnchor>()
            .Where(item => item.SupersededUtc == null && textUnitIds.Contains(item.TextUnitId))
            .ToListAsync(cancellationToken);
        foreach (var anchor in anchors)
            anchor.SupersededUtc = now;
        if (lexicalOccurrences.Count > 0 || lexicalRelationships.Count > 0 || anchors.Count > 0)
            await _db.SaveChangesAsync(cancellationToken);

        var examples = await _db.Set<LegendCurriculumExample>()
            .Where(item => item.SupersededUtc == null && textUnitIds.Contains(item.TextUnitId))
            .ToListAsync(cancellationToken);
        if (examples.Count == 0)
            return;
        foreach (var example in examples)
        {
            example.SupersededUtc = now;
            example.UpdatedUtc = now;
        }
        var exampleIds = examples.Select(item => item.Id).ToArray();
        var evidence = await _db.Set<LegendLanguageStructuralEvidence>()
            .Where(item => item.SupersededUtc == null &&
                (exampleIds.Contains(item.BaselineCurriculumExampleId) ||
                 exampleIds.Contains(item.ComparedCurriculumExampleId)))
            .ToListAsync(cancellationToken);
        foreach (var item in evidence)
            item.SupersededUtc = now;
        await _db.SaveChangesAsync(cancellationToken);

        var affectedPatterns = evidence.Select(item => item.StructuralPatternId).Distinct().ToArray();
        foreach (var patternId in affectedPatterns)
        {
            var pattern = await _db.Set<LegendLanguageStructuralPattern>()
                .SingleAsync(item => item.Id == patternId, cancellationToken);
            await RefreshPatternMaturityAsync(
                pattern.CurriculumFamilyId,
                pattern.LanguageCode,
                pattern.VariationDimension,
                pattern.RealizationSignature,
                cancellationToken);
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Called after the established candidate → Azure → corpus path has
    /// persisted an alignment. It attaches the new target asset to every
    /// relevant family and runs only same-language structural comparisons.
    /// </summary>
    public async Task AttachProcessedExpansionAsync(
        LegendCorpusCandidate candidate,
        LegendLanguagePairSnapshot pair,
        CancellationToken cancellationToken = default)
    {
        var source = await _db.Set<LegendLanguageTextUnit>()
            .SingleOrDefaultAsync(item => item.LanguageCode == pair.SourceLanguageCode &&
                item.NormalizedHash == candidate.SourceTextHash && item.IsTrainingEligible, cancellationToken);
        if (source is null)
            return;

        var sourceExamples = await _db.Set<LegendCurriculumExample>()
            .Where(item => item.TextUnitId == source.Id && item.LanguageCode == pair.SourceLanguageCode && item.SupersededUtc == null)
            .ToListAsync(cancellationToken);
        if (sourceExamples.Count == 0)
            return;

        var alignment = await _db.Set<LegendTranslationAlignment>()
            .Where(item => item.PairKey == pair.PairKey && item.SourceTextUnitId == source.Id && item.SupersededUtc == null)
            .OrderByDescending(item => item.UpdatedUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (alignment is null)
            return;

        var target = await _db.Set<LegendLanguageTextUnit>()
            .SingleOrDefaultAsync(item => item.Id == alignment.TargetTextUnitId &&
                item.LanguageCode == pair.TargetLanguageCode && item.IsTrainingEligible, cancellationToken);
        if (target is null)
            return;

        var affectedFamilies = new HashSet<Guid>();
        foreach (var sourceExample in sourceExamples)
        {
            var family = await _db.Set<LegendCurriculumFamily>()
                .SingleAsync(item => item.Id == sourceExample.CurriculumFamilyId, cancellationToken);
            var targetExample = await GetOrCreateExampleAsync(
                family,
                target,
                pair.TargetLanguageCode,
                sourceExample.Id,
                cancellationToken);
            await CopyVariationsAsync(sourceExample, targetExample, cancellationToken);
            affectedFamilies.Add(family.Id);
        }
        await _db.SaveChangesAsync(cancellationToken);

        foreach (var familyId in affectedFamilies)
            await AnalyzeFamilyLanguageAsync(familyId, pair.TargetLanguageCode, cancellationToken);
    }

    /// <summary>
    /// Explicit validation is deliberately separate from automatic evidence
    /// accumulation. Even a validated pattern remains production-ineligible
    /// until a future composition authority is independently approved.
    /// </summary>
    internal async Task<bool> TryValidatePatternAsync(
        Guid patternId,
        CancellationToken cancellationToken = default)
    {
        var pattern = await _db.Set<LegendLanguageStructuralPattern>()
            .SingleOrDefaultAsync(item => item.Id == patternId && item.SupersededUtc == null, cancellationToken);
        if (pattern is null || pattern.SupportCount < 3 || pattern.ContradictionCount != 0)
            return false;

        pattern.MaturityState = "Validated";
        pattern.IsProductionEligible = false;
        pattern.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Stable router boundary for future evidence-backed composition. Phase 2
    /// records and validates observations but deliberately has no formulation
    /// engine, so Azure remains the fallback even if a pattern is supported or
    /// explicitly validated. This prevents structural evidence from being
    /// mistaken for executable grammar.
    /// </summary>
    public async Task<LegendContextualTranslationSuggestion?> TryComposeAsync(
        string sourceLanguageCode,
        string targetLanguageCode,
        string text,
        CancellationToken cancellationToken = default)
    {
        var source = await _languages.NormalizeEnabledTranslationLanguageAsync(sourceLanguageCode, cancellationToken);
        var target = await _languages.NormalizeEnabledTranslationLanguageAsync(targetLanguageCode, cancellationToken);
        if (source is null || target is null || string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            return null;

        // Querying the maturity gate keeps its runtime boundary explicit and
        // auditable. There is intentionally no text generator in this phase;
        // no result may be returned merely because evidence exists.
        _ = await _db.Set<LegendLanguageStructuralPattern>().AsNoTracking().AnyAsync(item =>
            item.LanguageCode == source && item.SupersededUtc == null && item.MaturityState == "Validated" && item.IsProductionEligible,
            cancellationToken);
        return null;
    }

    private async Task EnsureEnglishLexicalObservationsAsync(
        IReadOnlyList<EnglishAtomicInput> inputs,
        CancellationToken cancellationToken)
    {
        var activeInputs = inputs
            .Where(item => item.TextUnit.IsTrainingEligible &&
                string.Equals(item.TextUnit.LanguageCode, "en", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.TextUnit.Id)
            .Select(group => group.First())
            .ToList();
        if (activeInputs.Count == 0)
            return;

        var componentsByTextUnit = activeInputs.ToDictionary(
            item => item.TextUnit.Id,
            item => SurfaceComponents(item.TextUnit.Text));
        var allComponents = componentsByTextUnit.Values.SelectMany(item => item)
            .DistinctBy(item => item.NormalizedHash, StringComparer.Ordinal)
            .ToList();
        var hashes = allComponents.Select(item => item.NormalizedHash).ToArray();
        var lexemesByHash = await _db.Set<LegendLanguageLexeme>()
            .Where(item => item.LanguageCode == "en" && hashes.Contains(item.NormalizedHash))
            .ToDictionaryAsync(item => item.NormalizedHash, StringComparer.Ordinal, cancellationToken);
        foreach (var component in allComponents.Where(item => !lexemesByHash.ContainsKey(item.NormalizedHash)))
        {
            var lexeme = new LegendLanguageLexeme
            {
                Id = Guid.NewGuid(),
                LanguageCode = "en",
                NormalizedHash = component.NormalizedHash,
                SurfaceForm = component.NormalizedText,
                Provenance = "FounderApproved",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            _db.Set<LegendLanguageLexeme>().Add(lexeme);
            lexemesByHash.Add(lexeme.NormalizedHash, lexeme);
        }
        if (_db.ChangeTracker.HasChanges())
            await _db.SaveChangesAsync(cancellationToken);

        var textUnitIds = activeInputs.Select(item => item.TextUnit.Id).ToArray();
        var existingOccurrences = await _db.Set<LegendLanguageLexicalOccurrence>()
            .Where(item => textUnitIds.Contains(item.TextUnitId))
            .ToDictionaryAsync(item => (item.TextUnitId, item.TokenIndex), cancellationToken);
        var observationsChanged = false;
        foreach (var input in activeInputs)
        {
            foreach (var component in componentsByTextUnit[input.TextUnit.Id])
            {
                var key = (input.TextUnit.Id, component.TokenIndex);
                if (existingOccurrences.TryGetValue(key, out var existing))
                {
                    if (existing.SupersededUtc is not null)
                    {
                        existing.SupersededUtc = null;
                        existing.UpdatedUtc = DateTime.UtcNow;
                        observationsChanged = true;
                    }
                    continue;
                }

                _db.Set<LegendLanguageLexicalOccurrence>().Add(new LegendLanguageLexicalOccurrence
                {
                    Id = Guid.NewGuid(),
                    TextUnitId = input.TextUnit.Id,
                    LexemeId = lexemesByHash[component.NormalizedHash].Id,
                    TokenIndex = component.TokenIndex,
                    CharacterOffset = component.CharacterOffset,
                    CharacterLength = component.CharacterLength,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                });
                observationsChanged = true;
            }
        }
        if (observationsChanged)
            await _db.SaveChangesAsync(cancellationToken);

        var existingRelationships = await _db.Set<LegendLanguageLexicalRelationship>()
            .Where(item => textUnitIds.Contains(item.TextUnitId))
            .ToDictionaryAsync(item => (item.TextUnitId, item.SourceTokenIndex, item.RelatedTokenIndex), cancellationToken);
        var relationshipsChanged = false;
        foreach (var input in activeInputs)
        {
            var components = componentsByTextUnit[input.TextUnit.Id];
            for (var index = 0; index < components.Count - 1; index++)
            {
                var key = (input.TextUnit.Id, components[index].TokenIndex, components[index + 1].TokenIndex);
                if (existingRelationships.TryGetValue(key, out var existing))
                {
                    if (existing.SupersededUtc is not null)
                    {
                        existing.SupersededUtc = null;
                        existing.UpdatedUtc = DateTime.UtcNow;
                        relationshipsChanged = true;
                    }
                    continue;
                }

                _db.Set<LegendLanguageLexicalRelationship>().Add(new LegendLanguageLexicalRelationship
                {
                    Id = Guid.NewGuid(),
                    TextUnitId = input.TextUnit.Id,
                    SourceLexemeId = lexemesByHash[components[index].NormalizedHash].Id,
                    RelatedLexemeId = lexemesByHash[components[index + 1].NormalizedHash].Id,
                    RelationshipKind = "AdjacentToken",
                    SourceTokenIndex = components[index].TokenIndex,
                    RelatedTokenIndex = components[index + 1].TokenIndex,
                    ObservationCount = 1,
                    Provenance = "FounderApproved",
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                });
                relationshipsChanged = true;
            }
        }
        if (relationshipsChanged)
            await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task AttachExplicitEnglishSemanticAnchorsAsync(
        LegendCurriculumFamily family,
        IReadOnlyList<LegendCurriculumExample> examples,
        CancellationToken cancellationToken)
    {
        var englishExamples = examples
            .Where(item => string.Equals(item.LanguageCode, "en", StringComparison.OrdinalIgnoreCase) && item.SupersededUtc is null)
            .DistinctBy(item => item.Id)
            .ToList();
        if (englishExamples.Count == 0)
            return;

        var exampleIds = englishExamples.Select(item => item.Id).ToArray();
        var textUnitIds = englishExamples.Select(item => item.TextUnitId).ToArray();
        var exampleVariations = await _db.Set<LegendCurriculumExampleVariation>()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId))
            .ToListAsync(cancellationToken);
        var variationsByExample = exampleVariations
            .GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var occurrencesByTextUnit = await (
            from occurrence in _db.Set<LegendLanguageLexicalOccurrence>()
            join lexeme in _db.Set<LegendLanguageLexeme>() on occurrence.LexemeId equals lexeme.Id
            where textUnitIds.Contains(occurrence.TextUnitId) && occurrence.SupersededUtc == null
            select new { occurrence.TextUnitId, occurrence.TokenIndex, occurrence.LexemeId, lexeme.SurfaceForm }
        ).ToListAsync(cancellationToken);
        var existingSignatures = await _db.Set<LegendLanguageCompositionalAnchor>()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId))
            .Select(item => item.AnchorSignature)
            .ToHashSetAsync(cancellationToken);
        var pending = false;
        foreach (var example in englishExamples)
        {
            if (!variationsByExample.TryGetValue(example.Id, out var variations))
                continue;
            var occurrences = occurrencesByTextUnit.Where(item => item.TextUnitId == example.TextUnitId).ToList();
            foreach (var variation in variations)
            {
                var sentenceSignature = AnchorSignature(example.Id, null, variation.Dimension, variation.Value);
                if (existingSignatures.Add(sentenceSignature))
                {
                    _db.Set<LegendLanguageCompositionalAnchor>().Add(new LegendLanguageCompositionalAnchor
                    {
                        Id = Guid.NewGuid(),
                        LanguageCode = "en",
                        TextUnitId = example.TextUnitId,
                        CurriculumFamilyId = family.Id,
                        CurriculumExampleId = example.Id,
                        Dimension = variation.Dimension,
                        Value = variation.Value,
                        AnchorSignature = sentenceSignature,
                        Provenance = "FounderApproved",
                        CreatedUtc = DateTime.UtcNow
                    });
                    pending = true;
                }

                var controlledComponents = SurfaceComponents(variation.Value);
                if (controlledComponents.Count == 0)
                    continue;
                var orderedOccurrences = occurrences.OrderBy(item => item.TokenIndex).ToList();
                for (var start = 0; start <= orderedOccurrences.Count - controlledComponents.Count; start++)
                {
                    if (!controlledComponents.Select(item => item.NormalizedText).SequenceEqual(
                            orderedOccurrences.Skip(start).Take(controlledComponents.Count).Select(item => item.SurfaceForm),
                            StringComparer.Ordinal))
                        continue;
                    var occurrence = orderedOccurrences[start];
                    var lexicalSignature = AnchorSignature(example.Id, occurrence.LexemeId, variation.Dimension,
                        variation.Value + ":" + occurrence.TokenIndex + ":" + controlledComponents.Count);
                    if (!existingSignatures.Add(lexicalSignature))
                        continue;
                    _db.Set<LegendLanguageCompositionalAnchor>().Add(new LegendLanguageCompositionalAnchor
                    {
                        Id = Guid.NewGuid(),
                        LanguageCode = "en",
                        TextUnitId = example.TextUnitId,
                        LexemeId = occurrence.LexemeId,
                        ComponentStartTokenIndex = occurrence.TokenIndex,
                        ComponentLength = controlledComponents.Count,
                        CurriculumFamilyId = family.Id,
                        CurriculumExampleId = example.Id,
                        Dimension = variation.Dimension,
                        Value = variation.Value,
                        AnchorSignature = lexicalSignature,
                        Provenance = "FounderApproved",
                        CreatedUtc = DateTime.UtcNow
                    });
                    pending = true;
                }
            }
        }
        if (pending)
            await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureParagraphNeighborRelationshipsAsync(
        IReadOnlyList<EnglishAtomicInput> inputs,
        CancellationToken cancellationToken)
    {
        var sequenced = inputs.Where(item => item.TrainingSubmissionId is not null && item.SequenceNumber is not null && item.ParagraphNumber is not null)
            .GroupBy(item => (item.TrainingSubmissionId!.Value, item.ParagraphNumber!.Value));
        var pending = false;
        foreach (var paragraph in sequenced)
        {
            var ordered = paragraph.OrderBy(item => item.SequenceNumber).ToList();
            for (var index = 0; index < ordered.Count - 1; index++)
            {
                var source = ordered[index];
                var related = ordered[index + 1];
                var signature = $"paragraph-sequence:{paragraph.Key.Item1:D}:{paragraph.Key.Item2}";
                var existing = await _db.Set<LegendLanguageContextRelationship>().SingleOrDefaultAsync(item =>
                    item.PairKey == null && item.SourceTextUnitId == source.TextUnit.Id &&
                    item.RelatedTextUnitId == related.TextUnit.Id && item.RelationshipKind == "AdjacentSentence" &&
                    item.ContextSignature == signature, cancellationToken);
                if (existing is not null)
                {
                    if (existing.SupersededUtc is not null)
                    {
                        existing.SupersededUtc = null;
                        existing.UpdatedUtc = DateTime.UtcNow;
                        pending = true;
                    }
                    continue;
                }
                _db.Set<LegendLanguageContextRelationship>().Add(new LegendLanguageContextRelationship
                {
                    Id = Guid.NewGuid(),
                    SourceTextUnitId = source.TextUnit.Id,
                    RelatedTextUnitId = related.TextUnit.Id,
                    RelationshipKind = "AdjacentSentence",
                    ContextSignature = signature,
                    SourcePatternSignature = $"sequence:{source.SequenceNumber}>{related.SequenceNumber}",
                    ContextCategory = "ParagraphSequence",
                    Confidence = 1m,
                    QualityState = "Observation",
                    Provenance = "FounderApproved",
                    ObservationCount = 1,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                });
                pending = true;
            }
        }
        if (pending)
            await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task AttachExistingExpansionsAsync(
        LegendCurriculumExample sourceExample,
        LegendLanguageTextUnit source,
        CancellationToken cancellationToken)
    {
        var alignments = await _db.Set<LegendTranslationAlignment>()
            .Where(item => item.SourceTextUnitId == source.Id && item.SupersededUtc == null)
            .ToListAsync(cancellationToken);
        if (alignments.Count == 0)
            return;

        var family = await _db.Set<LegendCurriculumFamily>()
            .SingleAsync(item => item.Id == sourceExample.CurriculumFamilyId, cancellationToken);
        foreach (var alignment in alignments)
        {
            var target = await _db.Set<LegendLanguageTextUnit>()
                .SingleOrDefaultAsync(item => item.Id == alignment.TargetTextUnitId && item.IsTrainingEligible, cancellationToken);
            if (target is null)
                continue;
            var targetExample = await GetOrCreateExampleAsync(
                family,
                target,
                target.LanguageCode,
                sourceExample.Id,
                cancellationToken);
            await CopyVariationsAsync(sourceExample, targetExample, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await AnalyzeFamilyLanguageAsync(family.Id, target.LanguageCode, cancellationToken);
        }
    }

    private async Task<LegendCurriculumExample> GetOrCreateExampleAsync(
        LegendCurriculumFamily family,
        LegendLanguageTextUnit textUnit,
        string languageCode,
        Guid? derivedFromCurriculumExampleId,
        CancellationToken cancellationToken)
    {
        var existing = await _db.Set<LegendCurriculumExample>()
            .SingleOrDefaultAsync(item => item.CurriculumFamilyId == family.Id && item.TextUnitId == textUnit.Id, cancellationToken);
        if (existing is not null)
        {
            if (existing.SupersededUtc is not null)
            {
                existing.SupersededUtc = null;
                existing.UpdatedUtc = DateTime.UtcNow;
            }
            if (existing.DerivedFromCurriculumExampleId is null && derivedFromCurriculumExampleId is not null)
            {
                existing.DerivedFromCurriculumExampleId = derivedFromCurriculumExampleId;
                existing.UpdatedUtc = DateTime.UtcNow;
            }
            return existing;
        }

        var created = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(),
            CurriculumFamilyId = family.Id,
            TextUnitId = textUnit.Id,
            LanguageCode = languageCode,
            DerivedFromCurriculumExampleId = derivedFromCurriculumExampleId,
            Provenance = "FounderApproved",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        _db.Set<LegendCurriculumExample>().Add(created);
        return created;
    }

    private async Task EnsureVariationsAsync(
        LegendCurriculumExample curriculumExample,
        IReadOnlyDictionary<string, string> variations,
        CancellationToken cancellationToken)
    {
        foreach (var variation in variations)
        {
            var existing = await _db.Set<LegendCurriculumExampleVariation>()
                .SingleOrDefaultAsync(item => item.CurriculumExampleId == curriculumExample.Id &&
                    item.Dimension == variation.Key, cancellationToken);
            if (existing is null)
            {
                _db.Set<LegendCurriculumExampleVariation>().Add(new LegendCurriculumExampleVariation
                {
                    Id = Guid.NewGuid(),
                    CurriculumExampleId = curriculumExample.Id,
                    Dimension = variation.Key,
                    Value = variation.Value,
                    CreatedUtc = DateTime.UtcNow
                });
                continue;
            }

            if (!string.Equals(existing.Value, variation.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A curriculum example cannot silently change a controlled variation after evidence has been recorded.");
            }
        }
    }

    private async Task CopyVariationsAsync(
        LegendCurriculumExample source,
        LegendCurriculumExample target,
        CancellationToken cancellationToken)
    {
        var sourceVariations = await _db.Set<LegendCurriculumExampleVariation>()
            .Where(item => item.CurriculumExampleId == source.Id)
            .ToListAsync(cancellationToken);
        foreach (var variation in sourceVariations)
        {
            var existing = await _db.Set<LegendCurriculumExampleVariation>()
                .SingleOrDefaultAsync(item => item.CurriculumExampleId == target.Id &&
                    item.Dimension == variation.Dimension, cancellationToken);
            if (existing is null)
            {
                _db.Set<LegendCurriculumExampleVariation>().Add(new LegendCurriculumExampleVariation
                {
                    Id = Guid.NewGuid(),
                    CurriculumExampleId = target.Id,
                    Dimension = variation.Dimension,
                    Value = variation.Value,
                    CreatedUtc = DateTime.UtcNow
                });
            }
            else if (!string.Equals(existing.Value, variation.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "One target example cannot carry conflicting controlled semantic variation values.");
            }
        }
    }

    private async Task AnalyzeFamilyLanguageAsync(
        Guid familyId,
        string languageCode,
        CancellationToken cancellationToken)
    {
        var examples = await (
            from example in _db.Set<LegendCurriculumExample>()
            join textUnit in _db.Set<LegendLanguageTextUnit>() on example.TextUnitId equals textUnit.Id
            where example.CurriculumFamilyId == familyId && example.LanguageCode == languageCode &&
                example.SupersededUtc == null && textUnit.IsTrainingEligible
            select new { Example = example, Text = textUnit.Text }
        ).OrderBy(item => item.Example.Id).ToListAsync(cancellationToken);
        if (examples.Count < 2)
            return;

        var exampleIds = examples.Select(item => item.Example.Id).ToArray();
        var variations = await _db.Set<LegendCurriculumExampleVariation>()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId))
            .ToListAsync(cancellationToken);
        var variationsByExample = variations
            .GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(item => item.Dimension, item => item.Value, StringComparer.Ordinal));
        var affected = new HashSet<(string Dimension, string Signature)>();

        for (var leftIndex = 0; leftIndex < examples.Count - 1; leftIndex++)
        {
            var left = examples[leftIndex];
            if (!variationsByExample.TryGetValue(left.Example.Id, out var leftVariations))
                continue;
            for (var rightIndex = leftIndex + 1; rightIndex < examples.Count; rightIndex++)
            {
                var right = examples[rightIndex];
                if (!variationsByExample.TryGetValue(right.Example.Id, out var rightVariations))
                    continue;
                foreach (var dimension in leftVariations.Keys.Intersect(rightVariations.Keys, StringComparer.Ordinal))
                {
                    var leftValue = leftVariations[dimension];
                    var rightValue = rightVariations[dimension];
                    if (string.Equals(leftValue, rightValue, StringComparison.Ordinal))
                        continue;

                    // This signature is deliberately derived from the two
                    // examples in the target language, not from English words
                    // or an English grammatical transformation. Its direction
                    // is normalized by the Founder-controlled values rather
                    // than random record identifiers, allowing the same
                    // evidence to accumulate across later batches.
                    var comparison = CanonicalComparison(
                        left.Text, leftValue, left.Example.Id,
                        right.Text, rightValue, right.Example.Id);
                    var signature = comparison.Signature;
                    var pattern = _db.Set<LegendLanguageStructuralPattern>().Local
                        .SingleOrDefault(item => item.CurriculumFamilyId == familyId &&
                            item.LanguageCode == languageCode &&
                            item.VariationDimension == dimension &&
                            item.RealizationSignature == signature)
                        ?? await _db.Set<LegendLanguageStructuralPattern>()
                            .SingleOrDefaultAsync(item => item.CurriculumFamilyId == familyId &&
                                item.LanguageCode == languageCode &&
                                item.VariationDimension == dimension &&
                                item.RealizationSignature == signature, cancellationToken);
                    if (pattern is null)
                    {
                        pattern = new LegendLanguageStructuralPattern
                        {
                            Id = Guid.NewGuid(),
                            CurriculumFamilyId = familyId,
                            LanguageCode = languageCode,
                            VariationDimension = dimension,
                            RealizationSignature = signature,
                            MaturityState = "Observation",
                            Provenance = "FounderApproved",
                            CreatedUtc = DateTime.UtcNow,
                            UpdatedUtc = DateTime.UtcNow
                        };
                        _db.Set<LegendLanguageStructuralPattern>().Add(pattern);
                    }
                    else if (pattern.SupersededUtc is not null)
                    {
                        pattern.SupersededUtc = null;
                        pattern.UpdatedUtc = DateTime.UtcNow;
                    }

                    var existingEvidence = await _db.Set<LegendLanguageStructuralEvidence>().SingleOrDefaultAsync(item =>
                        item.CurriculumFamilyId == familyId && item.LanguageCode == languageCode &&
                        item.VariationDimension == dimension &&
                        item.BaselineCurriculumExampleId == comparison.BaselineExampleId &&
                        item.ComparedCurriculumExampleId == comparison.ComparedExampleId, cancellationToken);
                    if (existingEvidence is null)
                    {
                        _db.Set<LegendLanguageStructuralEvidence>().Add(new LegendLanguageStructuralEvidence
                        {
                            Id = Guid.NewGuid(),
                            StructuralPatternId = pattern.Id,
                            CurriculumFamilyId = familyId,
                            LanguageCode = languageCode,
                            VariationDimension = dimension,
                            BaselineCurriculumExampleId = comparison.BaselineExampleId,
                            ComparedCurriculumExampleId = comparison.ComparedExampleId,
                            BaselineVariationValue = comparison.BaselineValue,
                            ComparedVariationValue = comparison.ComparedValue,
                            EvidenceSignature = signature,
                            BaselineComponentSignature = comparison.BaselineComponentSignature,
                            ComparedComponentSignature = comparison.ComparedComponentSignature,
                            Provenance = "FounderApproved",
                            CreatedUtc = DateTime.UtcNow
                        });
                    }
                    else if (existingEvidence.SupersededUtc is not null)
                    {
                        existingEvidence.SupersededUtc = null;
                    }
                    affected.Add((dimension, signature));
                }
            }
        }

        if (affected.Count == 0)
            return;
        await _db.SaveChangesAsync(cancellationToken);
        foreach (var (dimension, signature) in affected)
            await RefreshPatternMaturityAsync(familyId, languageCode, dimension, signature, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task RefreshPatternMaturityAsync(
        Guid familyId,
        string languageCode,
        string dimension,
        string signature,
        CancellationToken cancellationToken)
    {
        var pattern = await _db.Set<LegendLanguageStructuralPattern>()
            .SingleAsync(item => item.CurriculumFamilyId == familyId && item.LanguageCode == languageCode &&
                item.VariationDimension == dimension && item.RealizationSignature == signature, cancellationToken);
        var evidence = await _db.Set<LegendLanguageStructuralEvidence>()
            .Where(item => item.CurriculumFamilyId == familyId && item.LanguageCode == languageCode &&
                item.VariationDimension == dimension && item.SupersededUtc == null)
            .ToListAsync(cancellationToken);
        pattern.SupportCount = evidence.Count(item => item.StructuralPatternId == pattern.Id);
        pattern.ContradictionCount = evidence.Count - pattern.SupportCount;
        pattern.IsProductionEligible = false;
        pattern.SupersededUtc = evidence.Count == 0 ? DateTime.UtcNow : null;
        pattern.MaturityState = evidence.Count == 0
            ? "Superseded"
            : pattern.ContradictionCount > 0
            ? "Observation"
            : pattern.SupportCount switch
            {
                >= 3 => "Supported",
                2 => "Candidate",
                _ => "Observation"
            };
        pattern.UpdatedUtc = DateTime.UtcNow;
    }

    private static string RealizationSignature(string left, string right) =>
        $"{TextShape(left)}>{TextShape(right)}";

    private static StructuralComparison CanonicalComparison(
        string leftText,
        string leftValue,
        Guid leftExampleId,
        string rightText,
        string rightValue,
        Guid rightExampleId)
    {
        var forward = string.Compare(leftValue, rightValue, StringComparison.Ordinal) <= 0;
        var baselineText = forward ? leftText : rightText;
        var comparedText = forward ? rightText : leftText;
        return new StructuralComparison(
            RealizationSignature(baselineText, comparedText),
            forward ? leftExampleId : rightExampleId,
            forward ? rightExampleId : leftExampleId,
            forward ? leftValue : rightValue,
            forward ? rightValue : leftValue,
            ComponentSignature(baselineText),
            ComponentSignature(comparedText));
    }

    private static string ComponentSignature(string text)
    {
        var components = SurfaceComponents(text);
        var composition = string.Join('|', components.Select(item =>
            $"{item.TokenIndex}:{item.CharacterOffset}:{item.CharacterLength}:{item.NormalizedHash}"));
        return $"components:{components.Count}:{LegendLanguageIdentity.TextHash(composition)}";
    }

    private static IReadOnlyList<EnglishSurfaceComponent> SurfaceComponents(string text)
    {
        var normalized = LegendLanguageIdentity.NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        var components = new List<EnglishSurfaceComponent>();
        var cursor = 0;
        foreach (var rawToken in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var rawOffset = normalized.IndexOf(rawToken, cursor, StringComparison.Ordinal);
            cursor = rawOffset + rawToken.Length;
            var surface = rawToken.Trim(SurfaceBoundaryCharacters);
            if (string.IsNullOrWhiteSpace(surface) || !surface.Any(character => char.IsLetterOrDigit(character)))
                continue;
            var surfaceOffset = rawOffset + rawToken.IndexOf(surface, StringComparison.Ordinal);
            var normalizedSurface = surface.Normalize().ToLowerInvariant();
            components.Add(new EnglishSurfaceComponent(
                components.Count,
                surfaceOffset,
                surface.Length,
                normalizedSurface,
                LegendLanguageIdentity.TextHash(normalizedSurface)));
        }
        return components;
    }

    private static string AnchorSignature(
        Guid curriculumExampleId,
        Guid? lexemeId,
        string dimension,
        string value) => LegendLanguageIdentity.TextHash(
            $"{curriculumExampleId:D}|{lexemeId?.ToString("D") ?? "sentence"}|{dimension}|{value}");

    private static string TextShape(string text)
    {
        var tokens = LegendLanguageIdentity.NormalizeText(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokenShapes = tokens.Select(token =>
        {
            var letters = token.Count(char.IsLetter);
            var digits = token.Count(char.IsDigit);
            var punctuation = token.Length - letters - digits;
            return $"{letters}l{digits}d{punctuation}p";
        });
        return $"{tokens.Length}t[{string.Join(',', tokenShapes)}]";
    }

    private static readonly char[] SurfaceBoundaryCharacters =
        [' ', '.', ',', ';', ':', '!', '?', '"', '\'', '“', '”', '‘', '’', '(', ')', '[', ']', '{', '}', '—', '–'];

    private sealed record EnglishAtomicInput(
        LegendLanguageTextUnit TextUnit,
        string UnitType,
        Guid? TrainingSubmissionId,
        int? SequenceNumber,
        int? ParagraphNumber);

    private sealed record EnglishSurfaceComponent(
        int TokenIndex,
        int CharacterOffset,
        int CharacterLength,
        string NormalizedText,
        string NormalizedHash);

    private sealed record StructuralComparison(
        string Signature,
        Guid BaselineExampleId,
        Guid ComparedExampleId,
        string BaselineValue,
        string ComparedValue,
        string BaselineComponentSignature,
        string ComparedComponentSignature);

    private static IReadOnlyList<NormalizedCurriculumExample>? NormalizeExamples(
        IReadOnlyList<LegendConnectCurriculumExampleSubmission>? examples)
    {
        if (examples is null || examples.Count is < 2 or > MaximumExamplesPerBatch)
            return null;

        var normalized = new List<NormalizedCurriculumExample>(examples.Count);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var example in examples)
        {
            var text = LegendLanguageIdentity.NormalizeText(example.Text);
            if (string.IsNullOrWhiteSpace(text) || text.Length > 10_000 || !hashes.Add(LegendLanguageIdentity.TextHash(text)))
                return null;
            if (example.Variations is null || example.Variations.Count == 0 || example.Variations.Count > 12)
                return null;

            var variations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var variation in example.Variations)
            {
                var dimension = NormalizeDimension(variation.Key);
                var value = NormalizeOptional(variation.Value, 160);
                if (dimension is null || value is null || !variations.TryAdd(dimension, value))
                    return null;
            }
            normalized.Add(new NormalizedCurriculumExample(text, variations));
        }
        return normalized;
    }

    private static string? NormalizeFamilyKey(string? value)
    {
        var key = value?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(key) || key.Length > 160 ||
            key.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_'))
            ? null
            : key;
    }

    private static string? NormalizeDimension(string? value)
    {
        var dimension = value?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(dimension) || dimension.Length > 80 ||
            dimension.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_'))
            ? null
            : dimension;
    }

    private static string? NormalizeOptional(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized[..Math.Min(normalized.Length, maximumLength)];
    }

    private static LegendConnectCurriculumSubmissionResult Rejected(
        string errorCode,
        string message,
        string? familyKey) => new(false, false, errorCode, message, familyKey, null, 0, 0);

    private sealed record NormalizedCurriculumExample(
        string Text,
        IReadOnlyDictionary<string, string> Variations);
}
