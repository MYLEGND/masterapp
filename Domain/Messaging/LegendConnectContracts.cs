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
    Guid? AlignmentId,
    Guid? TrainingSubmissionId = null,
    int AtomicUnitCount = 0,
    int NewCanonicalUnitCount = 0,
    int ReusedCanonicalUnitCount = 0,
    int QueuedCoverageCount = 0);

/// <summary>
/// A controlled semantic curriculum example. Variations identify meaning that
/// changed; their realization is learned independently in every language.
/// </summary>
public sealed record LegendConnectCurriculumExampleSubmission(
    string Text,
    IReadOnlyDictionary<string, string> Variations);

public sealed record LegendConnectCurriculumBatchSubmission(
    string FamilyKey,
    string? SemanticCategory,
    IReadOnlyList<LegendConnectCurriculumExampleSubmission> Examples);

public sealed record LegendConnectCurriculumSubmissionResult(
    bool Succeeded,
    bool DuplicatePrevented,
    string? ErrorCode,
    string? Message,
    string? FamilyKey,
    Guid? CurriculumFamilyId,
    int EnglishExampleCount,
    int TargetExpansionCount);

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
    IReadOnlyList<LegendConnectOperationalEventSnapshot> RecentErrors,
    long ApprovedCandidateCount = 0,
    long PendingCandidateCount = 0,
    decimal AzureDependencyRate = 0m,
    DateTime? LastProviderAcquisitionUtc = null,
    DateTime? LastFounderTrainingUtc = null);

public sealed record LegendConnectPairHealthSnapshot(
    string PairKey,
    string SourceLanguageCode,
    string TargetLanguageCode,
    long DemandCount,
    long TranslationCount,
    long TranslationMemoryReuseCount,
    // Provider-backed work required after internal routing. The established
    // property name remains for wire compatibility; provider calls and billed
    // characters are represented separately by the dashboard metrics.
    long AzureFallbackCount,
    decimal AzureFallbackRate,
    int Coverage,
    string QualityState,
    string HealthState,
    DateTime? LastSuccessfulAlignmentUtc,
    DateTime? LastLearningActivityUtc,
    long FailureCount,
    IReadOnlyList<LegendConnectAlignmentSnapshot> RecentAlignments,
    IReadOnlyList<LegendConnectOperationalEventSnapshot> RecentErrors,
    long ContextualInternalServeCount = 0,
    decimal ProviderAvoidanceRate = 0m,
    decimal AzureDependencyRate = 0m,
    decimal InternalCoverageRate = 0m,
    decimal InternalQualityConfidence = 0m,
    int CoverageAdditionsLast30Days = 0,
    long ApprovedBacklog = 0,
    DateTime? LastProviderAcquisitionUtc = null);

public sealed record LegendConnectAlignmentSnapshot(
    Guid Id,
    string SourceText,
    string TargetText,
    string QualityState,
    bool HumanVerified,
    DateTime UpdatedUtc);

/// <summary>
/// A retained canonical text asset that the central eligibility policy has
/// approved for Legend Connect learning. This is deliberately distinct from
/// message history: private message text is never projected through this type.
/// </summary>
public sealed record LegendConnectLanguageTextUnitSnapshot(
    Guid Id,
    string Text,
    string Provenance,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// <summary>
/// A safe, active directional alignment between two approved canonical text
/// assets. Both texts have passed the existing retention/eligibility policy.
/// </summary>
public sealed record LegendConnectLanguageAlignmentDetailSnapshot(
    Guid Id,
    string PairKey,
    string SourceLanguageCode,
    string SourceText,
    string TargetLanguageCode,
    string TargetText,
    string Provider,
    string? ProviderModel,
    decimal? Confidence,
    string QualityState,
    bool HumanVerified,
    int ObservationCount,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// <summary>
/// A generic contextual relationship between approved language assets. It
/// carries stored context metadata without inventing language-specific rules.
/// </summary>
public sealed record LegendConnectLanguageContextRelationshipSnapshot(
    Guid Id,
    string? PairKey,
    string SourceLanguageCode,
    string SourceText,
    string RelatedLanguageCode,
    string RelatedText,
    string RelationshipKind,
    string? ContextCategory,
    string? UsageRegister,
    string? RegionalVariant,
    decimal Confidence,
    string QualityState,
    string Provenance,
    int ObservationCount,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// <summary>
/// Metadata for learning pipeline activity. Text is intentionally omitted so
/// private message payloads cannot become visible in Founder operations.
/// Approved text is available only through the canonical asset/alignment types.
/// </summary>
public sealed record LegendConnectLanguageLearningActivitySnapshot(
    Guid Id,
    string PairKey,
    string SourceLanguageCode,
    string TargetLanguageCode,
    string Provider,
    string Provenance,
    string EligibilityState,
    string ProcessingState,
    int AttemptCount,
    DateTime CreatedUtc,
    DateTime? ProcessedUtc,
    string? FailureCode,
    string? PromotionOutcome = null);

/// <summary>
/// Founder-only inspection projection for one language's real server-owned
/// learning dataset. Collections are deliberately bounded by DetailRecordLimit
/// for a responsive modal; their health counters remain the exact totals.
/// </summary>
public sealed record LegendConnectLanguageKnowledgeSnapshot(
    LegendConnectLanguageHealthSnapshot Health,
    int DetailRecordLimit,
    long LearningActivityCount,
    IReadOnlyList<LegendConnectLanguageTextUnitSnapshot> CanonicalEntries,
    IReadOnlyList<LegendConnectLanguageAlignmentDetailSnapshot> ActiveAlignments,
    IReadOnlyList<LegendConnectLanguageContextRelationshipSnapshot> ContextRelationships,
    IReadOnlyList<LegendConnectPairHealthSnapshot> DirectionalPairs,
    IReadOnlyList<LegendConnectLanguageLearningActivitySnapshot> RecentLearningActivity,
    IReadOnlyList<LegendConnectStructuralPatternSnapshot>? StructuralPatterns = null);

/// <summary>
/// Privacy-safe structural-learning projection. It intentionally contains no
/// message body or candidate translation text.
/// </summary>
public sealed record LegendConnectStructuralPatternSnapshot(
    string FamilyKey,
    string LanguageCode,
    string VariationDimension,
    string MaturityState,
    int SupportCount,
    int ContradictionCount,
    bool IsProductionEligible,
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
    // Provider-backed work required after internal routing, not completed
    // Azure calls. See ProviderOperationCount for actual provider attempts.
    long AzureFallbackCount,
    long AzureCharactersUsed,
    long ConfiguredMonthlyCapacity,
    long LiveReserveCharacters,
    long? RemainingSafeCapacity,
    long LearningJobCount,
    long FailedLearningJobCount,
    long DuplicatePreventionCount,
    DateTime? LastSuccessfulLearningUtc,
    IReadOnlyList<LegendConnectOperationalEventSnapshot> RecentOperationalEvents,
    long ProviderOperationCount = 0,
    long ProviderBillableCharacters = 0,
    long SameLanguageCharactersAvoided = 0,
    long TranslationMemoryCharactersAvoided = 0,
    long ContextualCharactersAvoided = 0,
    long QuotaDeniedRequestCount = 0,
    long ProviderFailureCount = 0,
    long GroupUniqueTargetReuseCount = 0,
    long ContextualInternalServeCount = 0,
    decimal ProviderAvoidanceRate = 0m,
    decimal AzureDependencyRate = 0m,
    decimal InternalCoverageRate = 0m,
    long ConsumedLiveCharacters = 0,
    long ConsumedCorpusCharacters = 0,
    long ReservedProviderCharacters = 0,
    long? SafeAcquisitionCapacity = null,
    DateOnly? BillingPeriodStart = null,
    DateOnly? BillingPeriodEnd = null,
    long ConsentedLiveLearningAccountCount = 0,
    long EligibleConsentedLiveTranslationCount = 0,
    long PromotedConsentedLiveTranslationCount = 0,
    long ReusedConsentedLiveTranslationCount = 0,
    long PendingConsentedLiveTranslationCount = 0,
    long FounderRawSubmissionCount = 0,
    long FounderAtomicLearningUnitCount = 0,
    long SupersededLegacyMultiUnitAssetCount = 0,
    long ActiveDirectionalAtomicAlignmentCount = 0);

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

    Task<LegendConnectLanguageKnowledgeSnapshot?> GetLanguageKnowledgeAsync(
        string languageCode,
        CancellationToken cancellationToken = default);

    Task<LegendConnectPairHealthSnapshot?> GetPairHealthAsync(
        string pairKey,
        CancellationToken cancellationToken = default);

    Task<LegendConnectKnowledgeSubmissionResult> SubmitFounderKnowledgeAsync(
        string founderUserId,
        LegendConnectKnowledgeSubmission submission,
        CancellationToken cancellationToken = default,
        Guid? reusableSourceTextUnitId = null);

    Task<LegendConnectKnowledgeSubmissionResult> CorrectFounderKnowledgeAsync(
        string founderUserId,
        Guid supersededAlignmentId,
        LegendConnectKnowledgeSubmission replacement,
        CancellationToken cancellationToken = default);

    Task<LegendConnectCurriculumSubmissionResult> SubmitFounderCurriculumAsync(
        string founderUserId,
        LegendConnectCurriculumBatchSubmission submission,
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
