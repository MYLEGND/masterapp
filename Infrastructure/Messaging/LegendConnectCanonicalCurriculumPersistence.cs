using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Messaging;

/// <summary>
/// The one canonical identity-admission boundary shared by normal Founder
/// intake, machine curriculum, manifest processing, and historical replay.
/// It owns only exact identity reuse and staging of canonical rows; it never
/// interprets language, changes evidence maturity, selects a transition, or
/// realizes a response.
/// </summary>
internal static class LegendConnectCanonicalCurriculumPersistence
{
    internal static async Task<LegendCurriculumFamily> AdmitFamilyAsync(
        MasterAppDbContext db,
        string familyKey,
        string? semanticCategory,
        string provenance,
        CancellationToken cancellationToken)
    {
        var existing = db.Set<LegendCurriculumFamily>().Local
            .SingleOrDefault(item => string.Equals(item.FamilyKey, familyKey, StringComparison.Ordinal))
            ?? await db.Set<LegendCurriculumFamily>()
                .SingleOrDefaultAsync(item => item.FamilyKey == familyKey, cancellationToken);
        if (existing is not null)
            return existing;

        var created = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(),
            FamilyKey = familyKey,
            SemanticCategory = semanticCategory,
            Provenance = provenance,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.Set<LegendCurriculumFamily>().Add(created);
        return created;
    }

    internal static async Task<LegendLanguageTextUnit> AdmitTextUnitAsync(
        MasterAppDbContext db,
        string languageCode,
        string normalizedText,
        string normalizedHash,
        string provenance,
        string storagePartition,
        CancellationToken cancellationToken)
    {
        var existing = db.Set<LegendLanguageTextUnit>().Local
            .SingleOrDefault(item => item.LanguageCode == languageCode && item.NormalizedHash == normalizedHash)
            ?? await db.Set<LegendLanguageTextUnit>()
                .SingleOrDefaultAsync(item => item.LanguageCode == languageCode && item.NormalizedHash == normalizedHash,
                    cancellationToken);
        if (existing is not null)
            return existing;

        var created = new LegendLanguageTextUnit
        {
            Id = Guid.NewGuid(),
            LanguageCode = languageCode,
            StoragePartition = storagePartition,
            NormalizedHash = normalizedHash,
            Text = normalizedText,
            Provenance = provenance,
            IsTrainingEligible = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.Set<LegendLanguageTextUnit>().Add(created);
        return created;
    }

    internal static async Task<Dictionary<string, LegendLanguageLexeme>> AdmitLexemesAsync(
        MasterAppDbContext db,
        string languageCode,
        IReadOnlyCollection<LegendCanonicalLexemeAdmission> candidates,
        CancellationToken cancellationToken)
    {
        var distinct = candidates
            .GroupBy(item => item.NormalizedHash, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.NormalizedHash, StringComparer.Ordinal)
            .ToArray();
        if (distinct.Length == 0)
            return new Dictionary<string, LegendLanguageLexeme>(StringComparer.Ordinal);

        var hashes = distinct.Select(item => item.NormalizedHash).ToArray();
        var existing = db.Set<LegendLanguageLexeme>().Local
            .Where(item => item.LanguageCode == languageCode && hashes.Contains(item.NormalizedHash))
            .ToDictionary(item => item.NormalizedHash, StringComparer.Ordinal);
        var persisted = await db.Set<LegendLanguageLexeme>()
            .Where(item => item.LanguageCode == languageCode && hashes.Contains(item.NormalizedHash))
            .ToListAsync(cancellationToken);
        foreach (var lexeme in persisted)
            existing.TryAdd(lexeme.NormalizedHash, lexeme);

        foreach (var candidate in distinct)
        {
            if (existing.ContainsKey(candidate.NormalizedHash))
                continue;
            var created = new LegendLanguageLexeme
            {
                Id = Guid.NewGuid(),
                LanguageCode = languageCode,
                NormalizedHash = candidate.NormalizedHash,
                SurfaceForm = candidate.SurfaceForm,
                Provenance = candidate.Provenance,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            db.Set<LegendLanguageLexeme>().Add(created);
            existing.Add(created.NormalizedHash, created);
        }
        return existing;
    }

    internal static async Task<LegendLanguageContextRelationship> AdmitContextRelationshipAsync(
        MasterAppDbContext db,
        LegendLanguageContextRelationship candidate,
        CancellationToken cancellationToken)
    {
        // The model deliberately normalizes an historic nullable PairKey
        // through the persisted CanonicalPairKey computed column.  The
        // database uniqueness constraint and the admission lookup must use
        // that identical canonical identity.  Comparing PairKey directly
        // turns a null pair scope into a broad, competing range read under
        // owned execution transactions, even when the actual relationships
        // are independent.
        var canonicalPairKey = candidate.PairKey ?? string.Empty;
        var unscopedPair = string.IsNullOrEmpty(canonicalPairKey);
        var existing = db.Set<LegendLanguageContextRelationship>().Local
            .SingleOrDefault(item =>
                (unscopedPair
                    ? string.IsNullOrEmpty(item.PairKey)
                    : item.PairKey == candidate.PairKey) &&
                item.SourceTextUnitId == candidate.SourceTextUnitId &&
                item.RelatedTextUnitId == candidate.RelatedTextUnitId &&
                item.RelationshipKind == candidate.RelationshipKind &&
                item.ContextSignature == candidate.ContextSignature &&
                item.SupersededUtc == null)
            ?? await (db.Database.IsRelational()
                ? db.Set<LegendLanguageContextRelationship>()
                    .Where(item => EF.Property<string>(item, "CanonicalPairKey") == canonicalPairKey)
                // The in-memory safety harness has no computed-column value.
                // Mirror SQL Server's COALESCE identity only for that test
                // provider; production continues to use the indexed shadow
                // column above.
                : db.Set<LegendLanguageContextRelationship>()
                    .Where(item => unscopedPair
                        ? string.IsNullOrEmpty(item.PairKey)
                        : item.PairKey == candidate.PairKey))
                .SingleOrDefaultAsync(item =>
                    item.SourceTextUnitId == candidate.SourceTextUnitId &&
                    item.RelatedTextUnitId == candidate.RelatedTextUnitId &&
                    item.RelationshipKind == candidate.RelationshipKind &&
                    item.ContextSignature == candidate.ContextSignature &&
                    item.SupersededUtc == null,
                    cancellationToken);
        if (existing is not null)
            return existing;

        db.Set<LegendLanguageContextRelationship>().Add(candidate);
        return candidate;
    }

    internal static async Task<LegendLanguageCompositionalAnchor> AdmitCompositionalAnchorAsync(
        MasterAppDbContext db,
        LegendLanguageCompositionalAnchor candidate,
        CancellationToken cancellationToken)
    {
        var existing = db.Set<LegendLanguageCompositionalAnchor>().Local
            .SingleOrDefault(item => item.CurriculumExampleId == candidate.CurriculumExampleId &&
                item.AnchorSignature == candidate.AnchorSignature)
            ?? await db.Set<LegendLanguageCompositionalAnchor>()
                .SingleOrDefaultAsync(item => item.CurriculumExampleId == candidate.CurriculumExampleId &&
                    item.AnchorSignature == candidate.AnchorSignature,
                    cancellationToken);
        if (existing is not null)
            return existing;

        db.Set<LegendLanguageCompositionalAnchor>().Add(candidate);
        return candidate;
    }
}

internal sealed record LegendCanonicalLexemeAdmission(
    string NormalizedHash,
    string SurfaceForm,
    string Provenance);
