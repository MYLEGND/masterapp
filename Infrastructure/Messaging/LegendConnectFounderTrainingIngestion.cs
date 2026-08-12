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
            return new LegendFounderTrainingReconciliationResult(0, 0, 0, 0);

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
            .OrderBy(item => item.CreatedUtc)
            .Take(Math.Clamp(take, 1, 100))
            .ToListAsync(cancellationToken);

        var reviewed = 0;
        var reconciled = 0;
        var atomicUnits = 0;
        var supersededRelationships = 0;
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

        if ((reconciled > 0 || reviewed > 0) && _operations is not null)
        {
            await _operations.TryRecordAsync(
                "FounderTrainingReconciliation",
                "Info",
                "Processed",
                errorCode: null,
                summary: $"Reconciled {reconciled} legacy multi-unit Founder submission(s); reviewed {reviewed} already-atomic source asset(s).",
                cancellationToken: cancellationToken);
        }
        return new LegendFounderTrainingReconciliationResult(reviewed, reconciled, atomicUnits, supersededRelationships);
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
        return new LegendConnectKnowledgeSubmissionResult(
            true,
            true,
            null,
            $"This Founder training submission was already ingested as {units.Count} atomic learning unit(s); existing knowledge and coverage were reused.",
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
        foreach (var alignment in alignments)
        {
            alignment.SupersededUtc = now;
            alignment.UpdatedUtc = now;
        }

        var contexts = await _db.Set<LegendLanguageContextRelationship>()
            .Where(item => item.SupersededUtc == null &&
                (item.SourceTextUnitId == source.Id || item.RelatedTextUnitId == source.Id ||
                 retiredTargetIds.Contains(item.SourceTextUnitId) || retiredTargetIds.Contains(item.RelatedTextUnitId)))
            .ToListAsync(cancellationToken);
        foreach (var context in contexts)
        {
            context.SupersededUtc = now;
            context.UpdatedUtc = now;
        }

        var candidates = await _db.Set<LegendCorpusCandidate>()
            .Where(item => item.IsApproved && item.SourceLanguageCode == source.LanguageCode &&
                item.SourceTextHash == source.NormalizedHash)
            .ToListAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            candidate.IsApproved = false;
            candidate.ProcessingState = "Superseded";
            candidate.FailureCode = "legacy_multi_unit_reconciled";
            candidate.ProcessedUtc = now;
            candidate.LeaseExpiresUtc = null;
        }

        var learningEvents = await _db.Set<LegendTranslationLearningEvent>()
            .Where(item => item.SourceLanguageCode == source.LanguageCode &&
                item.SourceTextHash == source.NormalizedHash &&
                (item.ProcessingState == "Pending" || item.ProcessingState == "Processing"))
            .ToListAsync(cancellationToken);
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
        if (retiredTargetIds.Length > 0)
        {
            var targets = await _db.Set<LegendLanguageTextUnit>()
                .Where(item => retiredTargetIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
            foreach (var target in targets)
            {
                target.IsTrainingEligible = false;
                target.UpdatedUtc = now;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        var supersededTextIds = targetIds.Append(source.Id).ToArray();
        await _curriculum.ReconcileSupersededExamplesAsync(supersededTextIds, cancellationToken);
        await RefreshPairCoverageAsync(pairKeys, cancellationToken);
        return alignments.Count + contexts.Count;
    }

    private async Task RefreshPairCoverageAsync(
        IReadOnlyList<string> pairKeys,
        CancellationToken cancellationToken)
    {
        foreach (var pairKey in pairKeys)
        {
            var pair = await _db.Set<LegendLanguagePair>().SingleOrDefaultAsync(item => item.PairKey == pairKey, cancellationToken);
            if (pair is null)
                continue;
            pair.CorpusCoverage = await (
                from alignment in _db.Set<LegendTranslationAlignment>()
                join source in _db.Set<LegendLanguageTextUnit>() on alignment.SourceTextUnitId equals source.Id
                join target in _db.Set<LegendLanguageTextUnit>() on alignment.TargetTextUnitId equals target.Id
                where alignment.PairKey == pairKey && alignment.SupersededUtc == null &&
                    source.IsTrainingEligible && target.IsTrainingEligible
                select alignment.Id
            ).CountAsync(cancellationToken);
            pair.UpdatedUtc = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(cancellationToken);
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
    int SupersededRelationshipCount);

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

    internal static IReadOnlyList<LegendFounderTrainingAtomicUnit> Segment(string rawText)
    {
        var paragraphs = Regex.Split(rawText.Replace("\r\n", "\n").Replace('\r', '\n'), @"\n\s*\n")
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToArray();
        var discovered = new List<(string Text, string UnitType, int Paragraph)>();
        for (var paragraphIndex = 0; paragraphIndex < paragraphs.Length; paragraphIndex++)
        {
            var paragraph = paragraphs[paragraphIndex];
            var paragraphUnits = new List<(string Text, string UnitType)>();
            foreach (var rawLine in paragraph.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var line = ListPrefix.Replace(rawLine, string.Empty).Trim();
                if (line.Length == 0)
                    continue;
                paragraphUnits.AddRange(SegmentLine(line));
            }

            var isParagraph = paragraphUnits.Count > 1;
            discovered.AddRange(paragraphUnits.Select(item => (
                item.Text,
                isParagraph && item.UnitType is "Sentence" or "Question" or "Exclamation" ? "ParagraphSentence" : item.UnitType,
                paragraphIndex + 1)));
        }

        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<LegendFounderTrainingAtomicUnit>(discovered.Count);
        foreach (var item in discovered)
        {
            var text = LegendLanguageIdentity.NormalizeText(item.Text);
            if (string.IsNullOrWhiteSpace(text) || text.Length > 2_000)
                continue;
            if (!hashes.Add(LegendLanguageIdentity.TextHash(text)))
                continue;
            result.Add(new LegendFounderTrainingAtomicUnit(text, item.UnitType, result.Count + 1, item.Paragraph));
        }
        return result;
    }

    private static IReadOnlyList<(string Text, string UnitType)> SegmentLine(string line)
    {
        var recoveredList = RecoverCollapsedLeadingLexicalList(line);
        if (recoveredList is not null)
            return recoveredList;

        var segments = new List<(string Text, string UnitType)>();
        var start = 0;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            if (current is not ('.' or '?' or '!') || !IsTerminator(line, index))
                continue;
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
    private static IReadOnlyList<(string Text, string UnitType)>? RecoverCollapsedLeadingLexicalList(string line)
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

        var recovered = items.Select(item => (item, "Lexical"))
            .Concat(SegmentLine(remainder))
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
