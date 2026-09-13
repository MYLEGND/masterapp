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
    string? FailureCode = null);

public sealed record ApplicationLocalizationCatalog(
    string CatalogVersion,
    string SourceLanguageCode,
    string LanguageCode,
    string Locale,
    DateTime GeneratedUtc,
    bool IsComplete,
    IReadOnlyList<ApplicationLocalizedCopy> Entries);

public interface IApplicationLocalizationService
{
    Task<ApplicationLocalizationCatalog> GetCatalogAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default);

    Task<ApplicationLocalizedCopy> LocalizeAsync(
        MessagingActor actor,
        string source,
        string context,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Source marker for shared/server-owned application copy. It deliberately
/// returns the input unchanged; the generated canonical manifest and
/// IApplicationLocalizationService remain the only localization authority.
/// </summary>
public static class ApplicationCopyText
{
    public static string Source(string value) => value;
}
