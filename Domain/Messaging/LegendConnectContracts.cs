using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Domain.Messaging;

public sealed record LegendLanguageDefinitionSnapshot(
    string Code,
    string BaseCode,
    string DisplayName,
    string NativeName,
    bool IsEnabled,
    bool IsTranslationEnabled,
    bool IsLearningEnabled,
    string DatasetNamespace,
    string StoragePartition);

public sealed record LegendLanguagePairSnapshot(
    string PairKey,
    string SourceLanguageCode,
    string TargetLanguageCode,
    bool IsEnabled,
    string TranslationMemoryPartition,
    int CorpusCoverage,
    string QualityState,
    string? ActiveModelVersion,
    string ProviderFallbackPolicy);

public sealed record TranslationLearningCandidate(
    Guid SourceMessageId,
    string SourceLanguageCode,
    string TargetLanguageCode,
    string SourceText,
    string TargetText,
    string Provider);

public interface ILegendLanguageRegistry
{
    Task<string?> NormalizeEnabledTranslationLanguageAsync(
        string? language,
        CancellationToken cancellationToken = default);

    Task<LegendLanguageDefinitionSnapshot?> GetLanguageAsync(
        string? language,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LegendLanguageDefinitionSnapshot>> ListEnabledTranslationLanguagesAsync(
        CancellationToken cancellationToken = default);

    Task<LegendLanguagePairSnapshot?> GetOrCreateEnabledPairAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}

public interface ITranslationLearningPublisher
{
    Task TryPublishAsync(
        TranslationLearningCandidate candidate,
        CancellationToken cancellationToken = default);
}

public interface ITranslationProvider : ITranslationService
{
    string ProviderName { get; }
}

/// <summary>
/// A Founder-approved knowledge contribution. The caller may describe context,
/// but cannot select a provider, bypass validation, or write into a separate
/// language store.
/// </summary>
public sealed record LegendConnectKnowledgeSubmission(
    string SourceLanguageCode,
    string SourceText,
    string? TargetLanguageCode,
    string? TargetText,
    string? ContextCategory,
    string? UsageRegister,
    string? RegionalVariant,
    string Provenance);

public sealed record LegendConnectKnowledgeSubmissionResult(
    bool Succeeded,
    bool DuplicatePrevented,
    string? ErrorCode,
    string? Message,
    string SourceLanguageCode,
    string? TargetLanguageCode,
    string? PairKey,
    Guid? SourceTextUnitId,
    Guid? TargetTextUnitId,
    Guid? AlignmentId);

public sealed record LegendConnectLanguageHealthSnapshot(
    string LanguageCode,
    string DisplayName,
    bool IsEnabled,
    string StoragePartition,
    long CanonicalEntryCount,
    long TranslationMemoryRelationshipCount,
    long ContextRelationshipCount,
    IReadOnlyList<string> DirectionalPairs,
    long DemandCount,
    int Coverage,
    string QualityState,
    string HealthState,
    DateTime? LastSuccessfulLearningUtc,
    DateTime? LastSuccessfulWriteUtc,
    long DuplicatePreventionCount,
    IReadOnlyList<LegendConnectOperationalEventSnapshot> RecentErrors);

public sealed record LegendConnectPairHealthSnapshot(
    string PairKey,
    string SourceLanguageCode,
    string TargetLanguageCode,
    long DemandCount,
    long TranslationCount,
    long TranslationMemoryReuseCount,
    long AzureFallbackCount,
    decimal AzureFallbackRate,
    int Coverage,
    string QualityState,
    string HealthState,
    DateTime? LastSuccessfulAlignmentUtc,
    DateTime? LastLearningActivityUtc,
    long FailureCount,
    IReadOnlyList<LegendConnectAlignmentSnapshot> RecentAlignments,
    IReadOnlyList<LegendConnectOperationalEventSnapshot> RecentErrors);

public sealed record LegendConnectAlignmentSnapshot(
    Guid Id,
    string SourceText,
    string TargetText,
    string QualityState,
    bool HumanVerified,
    DateTime UpdatedUtc);

public sealed record LegendConnectOperationalEventSnapshot(
    DateTime OccurredUtc,
    string Category,
    string Severity,
    string Status,
    string? LanguageCode,
    string? PairKey,
    string? CorrelationId,
    string? ErrorCode,
    string? Summary,
    bool IsResolved);

public sealed record LegendConnectDashboardSnapshot(
    IReadOnlyList<LegendConnectLanguageHealthSnapshot> Languages,
    IReadOnlyList<LegendConnectPairHealthSnapshot> Pairs,
    long SameLanguageBypassCount,
    long TranslationMemoryHitCount,
    long AzureFallbackCount,
    long AzureCharactersUsed,
    long ConfiguredMonthlyCapacity,
    long LiveReserveCharacters,
    long? RemainingSafeCapacity,
    long LearningJobCount,
    long FailedLearningJobCount,
    long DuplicatePreventionCount,
    DateTime? LastSuccessfulLearningUtc,
    IReadOnlyList<LegendConnectOperationalEventSnapshot> RecentOperationalEvents);

/// <summary>
/// The sole read/write authority for Legend Connect operations. Presentation
/// layers may use it only after their established Founder authorization guard
/// succeeds; it owns neither identity authorization nor mobile contracts.
/// </summary>
public interface ILegendConnectOperations
{
    Task<LegendConnectDashboardSnapshot> GetDashboardAsync(
        CancellationToken cancellationToken = default);

    Task<LegendConnectLanguageHealthSnapshot?> GetLanguageHealthAsync(
        string languageCode,
        CancellationToken cancellationToken = default);

    Task<LegendConnectPairHealthSnapshot?> GetPairHealthAsync(
        string pairKey,
        CancellationToken cancellationToken = default);

    Task<LegendConnectKnowledgeSubmissionResult> SubmitFounderKnowledgeAsync(
        string founderUserId,
        LegendConnectKnowledgeSubmission submission,
        CancellationToken cancellationToken = default);

    Task<LegendConnectKnowledgeSubmissionResult> CorrectFounderKnowledgeAsync(
        string founderUserId,
        Guid supersededAlignmentId,
        LegendConnectKnowledgeSubmission replacement,
        CancellationToken cancellationToken = default);
}

public static class LegendLanguageIdentity
{
    public static bool TryNormalize(string? value, out string languageCode)
    {
        languageCode = string.Empty;
        var candidate = value?.Trim().Replace('_', '-');
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 32)
            return false;

        var culture = ResolveCulture(candidate);
        if (culture is null || string.IsNullOrWhiteSpace(culture.Name))
            return false;

        var name = culture.Name.Replace('_', '-');
        var segments = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => !segment.All(char.IsLetterOrDigit)))
            return false;

        languageCode = string.Join('-', segments.Select((segment, index) => index switch
        {
            0 => segment.ToLowerInvariant(),
            _ when segment.Length == 4 && segment.All(char.IsLetter) =>
                char.ToUpperInvariant(segment[0]) + segment[1..].ToLowerInvariant(),
            _ when segment.Length is 2 or 3 && segment.All(char.IsLetterOrDigit) => segment.ToUpperInvariant(),
            _ => segment
        }));
        return true;
    }

    public static string BaseCode(string languageCode) =>
        languageCode.Split('-', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();

    public static string PairKey(string sourceLanguageCode, string targetLanguageCode) =>
        $"{sourceLanguageCode}:{targetLanguageCode}";

    public static string DatasetNamespace(string languageCode) => "/" + languageCode;

    public static string TextHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeText(text))));

    public static string NormalizeText(string text) =>
        string.Join(' ', (text ?? string.Empty).Normalize(NormalizationForm.FormKC)
            .Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// A language-neutral shape used only for contextual evaluation. It does
    /// not encode language grammar or formulate production output by itself.
    /// </summary>
    public static string ContextPatternSignature(string text)
    {
        var normalized = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token =>
            {
                var letters = token.Count(char.IsLetter);
                var digits = token.Count(char.IsDigit);
                var symbols = token.Length - letters - digits;
                return $"w{letters}:d{digits}:p{Math.Max(0, symbols)}";
            }));
    }

    public static string ContextSignature(
        string? category,
        string? usageRegister,
        string? regionalVariant) =>
        string.Join('|', new[] { category, usageRegister, regionalVariant }
            .Select(value => NormalizeText(value ?? string.Empty).ToLowerInvariant()));

    private static CultureInfo? ResolveCulture(string candidate)
    {
        try
        {
            return CultureInfo.GetCultureInfo(candidate);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultures(CultureTypes.NeutralCultures | CultureTypes.SpecificCultures)
                .FirstOrDefault(culture =>
                    string.Equals(culture.EnglishName, candidate, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(culture.NativeName, candidate, StringComparison.OrdinalIgnoreCase));
        }
    }
}
