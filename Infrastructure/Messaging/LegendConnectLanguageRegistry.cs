using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Messaging;

/// <summary>
/// The one durable language registry. Bootstrap records retain the established
/// production languages, but every runtime decision is made from the database
/// rows so enabling a later language is a data operation, not a code branch.
/// </summary>
internal sealed class LegendLanguageRegistry : ILegendLanguageRegistry
{
    private readonly MasterAppDbContext _db;
    private readonly IConfiguration _configuration;
    private bool _baselineEnsured;

    public LegendLanguageRegistry(MasterAppDbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    public async Task<string?> NormalizeEnabledTranslationLanguageAsync(
        string? language,
        CancellationToken cancellationToken = default) =>
        (await GetLanguageAsync(language, cancellationToken))?.Code;

    public async Task<string?> NormalizeEnabledTranslationLanguageReadOnlyAsync(
        string? language,
        CancellationToken cancellationToken = default) =>
        (await ResolveEnabledLanguageAsync(
            language,
            requireTranslation: true,
            requireLearning: false,
            cancellationToken,
            provisionBaseline: false))?.Code;

    public async Task<LegendLanguageDefinitionSnapshot?> GetLanguageAsync(
        string? language,
        CancellationToken cancellationToken = default) =>
        await ResolveEnabledLanguageAsync(
            language,
            requireTranslation: true,
            requireLearning: false,
            cancellationToken);

    public async Task<LegendLanguageDefinitionSnapshot?>
        GetEnabledLearningLanguageAsync(
            string? language,
            CancellationToken cancellationToken = default) =>
        await ResolveEnabledLanguageAsync(
            language,
            requireTranslation: false,
            requireLearning: true,
            cancellationToken);

    private async Task<LegendLanguageDefinitionSnapshot?> ResolveEnabledLanguageAsync(
        string? language,
        bool requireTranslation,
        bool requireLearning,
        CancellationToken cancellationToken,
        bool provisionBaseline = true)
    {
        var candidate = language?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        if (provisionBaseline)
            await EnsureBaselineAsync(cancellationToken);
        var definition = await EnabledLanguageQuery(candidate, requireTranslation, requireLearning)
            .FirstOrDefaultAsync(cancellationToken);
        return definition is null ? null : ToSnapshot(definition);
    }

    private IQueryable<LegendLanguageDefinition> EnabledLanguageQuery(
        string candidate, bool requireTranslation, bool requireLearning)
    {
        var hasNormalizedCode = LegendLanguageIdentity.TryNormalize(candidate, out var normalized);
        var baseCode = hasNormalizedCode ? LegendLanguageIdentity.BaseCode(normalized) : string.Empty;
        return _db.Set<LegendLanguageDefinition>()
            .AsNoTracking()
            .Where(item =>
                item.IsEnabled &&
                (!requireTranslation || item.IsTranslationEnabled) &&
                (!requireLearning || item.IsLearningEnabled))
            .Where(item =>
                item.CanonicalName == candidate ||
                item.NativeName == candidate ||
                (hasNormalizedCode && (item.LanguageCode == normalized ||
                    (item.LanguageCode == item.BaseLanguageCode && item.BaseLanguageCode == baseCode))))
            .OrderByDescending(item => hasNormalizedCode && item.LanguageCode == normalized);
    }

    public async Task<IReadOnlyList<LegendLanguageDefinitionSnapshot>> ListEnabledTranslationLanguagesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureBaselineAsync(cancellationToken);
        return await ListEnabledTranslationLanguagesReadOnlyAsync(
            cancellationToken);
    }

    public async Task<IReadOnlyList<LegendLanguageDefinitionSnapshot>>
        ListEnabledTranslationLanguagesReadOnlyAsync(
            CancellationToken cancellationToken = default)
    {
        return await _db.Set<LegendLanguageDefinition>()
            .AsNoTracking()
            .Where(item => item.IsEnabled && item.IsTranslationEnabled)
            .OrderBy(item => item.CreatedUtc)
            .ThenBy(item => item.LanguageCode)
            .Select(item => new LegendLanguageDefinitionSnapshot(
                item.LanguageCode,
                item.BaseLanguageCode,
                item.CanonicalName,
                item.NativeName,
                item.IsEnabled,
                item.IsTranslationEnabled,
                item.IsLearningEnabled,
                item.DatasetNamespace,
                item.StoragePartition))
            .ToListAsync(cancellationToken);
    }

    public async Task<LegendLanguagePairSnapshot?> GetOrCreateEnabledPairAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var source = await NormalizeEnabledTranslationLanguageAsync(sourceLanguage, cancellationToken);
        var target = await NormalizeEnabledTranslationLanguageAsync(targetLanguage, cancellationToken);
        if (source is null || target is null || string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            return null;

        var pairKey = LegendLanguageIdentity.PairKey(source, target);
        var pair = await _db.Set<LegendLanguagePair>()
            .SingleOrDefaultAsync(item => item.PairKey == pairKey, cancellationToken);
        if (pair is null)
        {
            pair = new LegendLanguagePair
            {
                Id = Guid.NewGuid(),
                PairKey = pairKey,
                SourceLanguageCode = source,
                TargetLanguageCode = target,
                IsEnabled = true,
                TranslationMemoryPartition = "/" + pairKey,
                CorpusCoverage = 0,
                QualityState = "Observation",
                ProviderFallbackPolicy = "AzureTranslator",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            _db.Set<LegendLanguagePair>().Add(pair);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                _db.Entry(pair).State = EntityState.Detached;
                pair = await _db.Set<LegendLanguagePair>()
                    .SingleOrDefaultAsync(item => item.PairKey == pairKey, cancellationToken);
            }
        }

        return pair is { IsEnabled: true } ? ToSnapshot(pair) : null;
    }

    public async Task<LegendLanguagePairSnapshot?> GetEnabledPairAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var sourceCandidate = sourceLanguage?.Trim();
        var targetCandidate = targetLanguage?.Trim();
        if (string.IsNullOrWhiteSpace(sourceCandidate) || string.IsNullOrWhiteSpace(targetCandidate))
            return null;
        await EnsureBaselineAsync(cancellationToken);
        var pair = await EnabledPairQuery(sourceCandidate, targetCandidate).SingleOrDefaultAsync(cancellationToken);
        return pair is null ? null : ToSnapshot(pair);
    }

    internal IQueryable<LegendLanguagePair> EnabledPairQuery(string sourceCandidate, string targetCandidate)
    {
        // Resolve both languages and directional eligibility in one current
        // database read. The ordered singleton subqueries preserve canonical,
        // name and base-language fallback semantics without a policy cache.
        var sources = EnabledLanguageQuery(sourceCandidate, requireTranslation: true, requireLearning: false).Take(1);
        var targets = EnabledLanguageQuery(targetCandidate, requireTranslation: true, requireLearning: false).Take(1);
        return
            from source in sources
            from target in targets
            from candidate in _db.Set<LegendLanguagePair>().AsNoTracking()
            where source.LanguageCode != target.LanguageCode && candidate.IsEnabled &&
                candidate.PairKey == source.LanguageCode + ":" + target.LanguageCode
            select candidate;
    }

    private async Task EnsureBaselineAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_baselineEnsured) return;
        // Runtime is intentionally idempotent. Migrations seed this data for
        // production; this provisioner keeps isolated test/dev databases and a
        // newly initialized deployment on the same authority.
        var configured = ReadBaseline();
        if (configured.Count == 0)
            return;

        var knownCodes = await _db.Set<LegendLanguageDefinition>()
            .Select(item => item.LanguageCode)
            .ToListAsync(cancellationToken);
        var known = knownCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var additions = configured
            .Select((item, index) => new { Language = item, Index = index })
            .Where(item => !known.Contains(item.Language.Code))
            .Select(item => new LegendLanguageDefinition
            {
                Id = Guid.NewGuid(),
                LanguageCode = item.Language.Code,
                BaseLanguageCode = LegendLanguageIdentity.BaseCode(item.Language.Code),
                CanonicalName = item.Language.Name,
                NativeName = item.Language.NativeName,
                IsEnabled = true,
                IsTranslationEnabled = true,
                IsLearningEnabled = true,
                DatasetNamespace = LegendLanguageIdentity.DatasetNamespace(item.Language.Code),
                StoragePartition = LegendLanguageIdentity.DatasetNamespace(item.Language.Code),
                CreatedUtc = now.AddTicks(item.Index),
                UpdatedUtc = now.AddTicks(item.Index)
            })
            .ToArray();
        if (additions.Length == 0)
        {
            _baselineEnsured = true;
            return;
        }

        _db.Set<LegendLanguageDefinition>().AddRange(additions);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            _baselineEnsured = true;
        }
        catch (DbUpdateException)
        {
            // Another application instance seeded the same deterministic data.
            foreach (var definition in additions)
                _db.Entry(definition).State = EntityState.Detached;
        }
    }

    private IReadOnlyList<BaselineLanguage> ReadBaseline()
    {
        var fromConfiguration = _configuration
            .GetSection("LegendConnect:LanguageRegistry:Baseline")
            .Get<List<BaselineLanguage>>();
        return fromConfiguration is { Count: > 0 }
            ? fromConfiguration.Where(IsValid).ToArray()
            : LegendConnectBaseline.Languages;
    }

    private static bool IsValid(BaselineLanguage language) =>
        LegendLanguageIdentity.TryNormalize(language.Code, out _) &&
        !string.IsNullOrWhiteSpace(language.Name) &&
        !string.IsNullOrWhiteSpace(language.NativeName);

    private static LegendLanguageDefinitionSnapshot ToSnapshot(LegendLanguageDefinition item) => new(
        item.LanguageCode,
        item.BaseLanguageCode,
        item.CanonicalName,
        item.NativeName,
        item.IsEnabled,
        item.IsTranslationEnabled,
        item.IsLearningEnabled,
        item.DatasetNamespace,
        item.StoragePartition);

    private static LegendLanguagePairSnapshot ToSnapshot(LegendLanguagePair item) => new(
        item.PairKey,
        item.SourceLanguageCode,
        item.TargetLanguageCode,
        item.IsEnabled,
        item.TranslationMemoryPartition,
        item.CorpusCoverage,
        item.QualityState,
        item.ActiveModelVersion,
        item.ProviderFallbackPolicy);

    public sealed class BaselineLanguage
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string NativeName { get; set; } = string.Empty;
    }
}

internal static class LegendConnectBaseline
{
    // Data-only compatibility seed. There are no language-specific execution
    // paths here; once seeded, Founder-controlled database rows are canonical.
    internal static readonly IReadOnlyList<LegendLanguageRegistry.BaselineLanguage> Languages =
    [
        new() { Code = "en", Name = "English", NativeName = "English" },
        new() { Code = "ht", Name = "Haitian Creole", NativeName = "Kreyòl ayisyen" },
        new() { Code = "es", Name = "Spanish", NativeName = "Español" },
        new() { Code = "fr", Name = "French", NativeName = "Français" },
        new() { Code = "pt", Name = "Portuguese", NativeName = "Português" },
        new() { Code = "de", Name = "German", NativeName = "Deutsch" },
        new() { Code = "ja", Name = "Japanese", NativeName = "日本語" },
        new() { Code = "ko", Name = "Korean", NativeName = "한국어" },
        new() { Code = "zh-Hans", Name = "Chinese (Simplified)", NativeName = "简体中文" },
        new() { Code = "ar", Name = "Arabic", NativeName = "العربية" }
    ];
}
