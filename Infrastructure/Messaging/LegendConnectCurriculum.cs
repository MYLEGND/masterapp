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
                item.NormalizedHash == candidate.SourceTextHash, cancellationToken);
        if (source is null)
            return;

        var sourceExamples = await _db.Set<LegendCurriculumExample>()
            .Where(item => item.TextUnitId == source.Id && item.LanguageCode == pair.SourceLanguageCode)
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
                item.LanguageCode == pair.TargetLanguageCode, cancellationToken);
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
            .SingleOrDefaultAsync(item => item.Id == patternId, cancellationToken);
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
            item.LanguageCode == source && item.MaturityState == "Validated" && item.IsProductionEligible,
            cancellationToken);
        return null;
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
                .SingleOrDefaultAsync(item => item.Id == alignment.TargetTextUnitId, cancellationToken);
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
            where example.CurriculumFamilyId == familyId && example.LanguageCode == languageCode
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
                    // or an English grammatical transformation.
                    var signature = RealizationSignature(left.Text, right.Text);
                    var pattern = await _db.Set<LegendLanguageStructuralPattern>()
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

                    var exists = await _db.Set<LegendLanguageStructuralEvidence>().AnyAsync(item =>
                        item.CurriculumFamilyId == familyId && item.LanguageCode == languageCode &&
                        item.VariationDimension == dimension &&
                        item.BaselineCurriculumExampleId == left.Example.Id &&
                        item.ComparedCurriculumExampleId == right.Example.Id, cancellationToken);
                    if (!exists)
                    {
                        _db.Set<LegendLanguageStructuralEvidence>().Add(new LegendLanguageStructuralEvidence
                        {
                            Id = Guid.NewGuid(),
                            StructuralPatternId = pattern.Id,
                            CurriculumFamilyId = familyId,
                            LanguageCode = languageCode,
                            VariationDimension = dimension,
                            BaselineCurriculumExampleId = left.Example.Id,
                            ComparedCurriculumExampleId = right.Example.Id,
                            BaselineVariationValue = leftValue,
                            ComparedVariationValue = rightValue,
                            EvidenceSignature = signature,
                            Provenance = "FounderApproved",
                            CreatedUtc = DateTime.UtcNow
                        });
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
                item.VariationDimension == dimension)
            .ToListAsync(cancellationToken);
        pattern.SupportCount = evidence.Count(item => item.StructuralPatternId == pattern.Id);
        pattern.ContradictionCount = evidence.Count - pattern.SupportCount;
        pattern.IsProductionEligible = false;
        pattern.MaturityState = pattern.ContradictionCount > 0
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
