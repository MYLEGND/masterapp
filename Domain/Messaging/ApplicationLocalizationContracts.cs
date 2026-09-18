namespace Domain.Messaging;

public sealed record ApplicationLocalizedCopy(
    string Id,
    string Source,
    string Text,
    string Context,
    string SourceRevision,
    IReadOnlyList<string> Placeholders,
    string Provider,
    string Provenance,
    string ValidationState,
    DateTime CreatedUtc,
    bool Reused,
    string? FailureCode = null,
    DateTime? RetryAfterUtc = null);

public sealed record ApplicationLocalizationCatalog(
    string CatalogVersion,
    string SourceLanguageCode,
    string LanguageCode,
    string Locale,
    DateTime GeneratedUtc,
    bool IsComplete,
    IReadOnlyList<ApplicationLocalizedCopy> Entries,
    ApplicationLocalizationContinuation? Continuation = null);

// Operational continuation is prescribed by the same catalog authority on every
// platform. It never authorizes another provider or changes account preferences.
public sealed record ApplicationLocalizationContinuation(
    string Disposition,
    int RemainingEntries,
    int? RetryAfterSeconds,
    int MaximumConsecutiveNoProgress = 3,
    int MaximumDurationSeconds = 180,
    int MaximumRequestsPerPass = 64,
    int CooldownSeconds = 60);

public interface IApplicationLocalizationService
{
    Task<ApplicationTranslationAdmissionResult> AdmitArtifactAsync(
        string artifactJson, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Artifact admission is unavailable.");

    Task<ApplicationLocalizationCatalog> GetCatalogAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default);

    Task<ApplicationLocalizationCatalog> InspectCatalogAsync(
        string targetLanguageCode,
        CancellationToken cancellationToken = default);

    Task<ApplicationLocalizationCatalog> PrepareCatalogAsync(
        string targetLanguageCode,
        CancellationToken cancellationToken = default);

    Task<ApplicationLocalizedCopy> LocalizeAsync(
        MessagingActor actor,
        string source,
        string context,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken cancellationToken = default);
}

public sealed record ApplicationTranslationArtifact(
    string SchemaVersion,
    string CatalogVersion,
    string SourceLanguageCode,
    string AuthoringModel,
    string AuthoringContext,
    IReadOnlyList<ApplicationTranslationArtifactEntry> Entries);

public sealed record ApplicationTranslationArtifactEntry(
    string Id,
    string Source,
    string SourceRevision,
    string Context,
    string TranslationPolicy,
    string ReuseScope,
    string LanguageCode,
    string Text,
    IReadOnlyList<string> Placeholders);

public sealed record ApplicationTranslationAdmissionResult(
    string CatalogVersion, string ArtifactSha256, int Imported, int Reused);

/// <summary>
/// Source marker for shared/server-owned application copy. It deliberately
/// returns the input unchanged; the generated canonical manifest and
/// IApplicationLocalizationService remain the only localization authority.
/// </summary>
public static class ApplicationCopyText
{
    public static string Source(string value) => value;
}
