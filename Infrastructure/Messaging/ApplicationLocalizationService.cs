using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Domain.Messaging;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

internal static class ApplicationTranslationPolicies
{
    internal const string AzureAllowed = "AzureAllowed";
    internal const string ApprovedOnly = "ApprovedOnly";
    internal const string NonTranslatable = "NonTranslatable";
}

internal sealed record ApplicationCopyManifest(
    string CatalogVersion,
    string SourceLanguageCode,
    IReadOnlyList<ApplicationCopyManifestEntry> Entries);

internal sealed record ApplicationCopyManifestEntry(
    string Id,
    string Source,
    string Context,
    string SourceRevision,
    IReadOnlyList<string> Placeholders,
    string TranslationPolicy,
    string ReuseScope);

internal interface IApplicationCopyManifestSource
{
    ApplicationCopyManifest Manifest { get; }
}

internal sealed class EmbeddedApplicationCopyManifestSource : IApplicationCopyManifestSource
{
    public EmbeddedApplicationCopyManifestSource()
    {
        using var stream = typeof(EmbeddedApplicationCopyManifestSource).Assembly
            .GetManifestResourceStream("Legend.ApplicationCopy.json")
            ?? throw new InvalidOperationException("The canonical application-copy manifest is missing.");
        Manifest = JsonSerializer.Deserialize<ApplicationCopyManifest>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("The canonical application-copy manifest is invalid.");
        Validate(Manifest);
    }

    public ApplicationCopyManifest Manifest { get; }

    private static void Validate(ApplicationCopyManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.CatalogVersion) ||
            !LegendLanguageIdentity.TryNormalize(manifest.SourceLanguageCode, out _) ||
            manifest.Entries.Count == 0)
            throw new InvalidOperationException("The canonical application-copy manifest is incomplete.");

        var duplicate = manifest.Entries
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate application-copy identity: {duplicate.Key}");

        var catalogIdentity = string.Join('\n', manifest.Entries.Select(entry => string.Join('\u001f',
            entry.Id,
            entry.Source,
            entry.Context,
            entry.SourceRevision,
            string.Join(',', entry.Placeholders),
            entry.TranslationPolicy,
            entry.ReuseScope)));
        var expectedVersion = "application-copy-v1-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(catalogIdentity)))[..16].ToLowerInvariant();
        if (!string.Equals(manifest.CatalogVersion, expectedVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("The canonical application-copy version does not match its content.");

        foreach (var entry in manifest.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || entry.Id.Length > 180 ||
                string.IsNullOrWhiteSpace(entry.Source) || entry.Source.Length > 10_000 ||
                string.IsNullOrWhiteSpace(entry.Context) || entry.Context.Length > 180 ||
                string.IsNullOrWhiteSpace(entry.SourceRevision) || entry.SourceRevision.Length > 80 ||
                entry.ReuseScope != TranslationReuseScopes.Global ||
                entry.TranslationPolicy is not (
                    ApplicationTranslationPolicies.AzureAllowed or
                    ApplicationTranslationPolicies.ApprovedOnly or
                    ApplicationTranslationPolicies.NonTranslatable))
                throw new InvalidOperationException($"Invalid application-copy definition: {entry.Id}");

            var placeholders = TranslationOutputValidator.PlaceholderNames(entry.Source);
            if (!placeholders.SequenceEqual(
                    entry.Placeholders.Order(StringComparer.Ordinal),
                    StringComparer.Ordinal))
                throw new InvalidOperationException($"Application-copy placeholder contract is invalid: {entry.Id}");
        }
    }
}

/// <summary>
/// Resolves a complete server-approved application-copy catalog through the
/// canonical preference, registry, router, and translation-memory authorities.
/// It owns no provider and no cache.
/// </summary>
internal sealed class ApplicationLocalizationService : IApplicationLocalizationService
{
    private readonly IApplicationCopyManifestSource _manifestSource;
    private readonly IControlledResourceAccessService _preferences;
    private readonly ILegendLanguageRegistry _languages;
    private readonly IRetainedTranslationService _translations;
    private readonly ILegendConnectTranslationIntelligence _intelligence;
    private readonly ILogger<ApplicationLocalizationService> _logger;

    public ApplicationLocalizationService(
        IApplicationCopyManifestSource manifestSource,
        IControlledResourceAccessService preferences,
        ILegendLanguageRegistry languages,
        IRetainedTranslationService translations,
        ILegendConnectTranslationIntelligence intelligence,
        ILogger<ApplicationLocalizationService> logger)
    {
        _manifestSource = manifestSource;
        _preferences = preferences;
        _languages = languages;
        _translations = translations;
        _intelligence = intelligence;
        _logger = logger;
    }

    public async Task<ApplicationLocalizationCatalog> GetCatalogAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default)
    {
        var manifest = _manifestSource.Manifest;
        var source = await _languages.NormalizeEnabledTranslationLanguageReadOnlyAsync(
            manifest.SourceLanguageCode,
            cancellationToken) ?? manifest.SourceLanguageCode;
        var preferred = await _preferences.GetCanonicalPreferredLanguageAsync(actor, cancellationToken);
        var target = await _languages.NormalizeEnabledTranslationLanguageReadOnlyAsync(
            preferred,
            cancellationToken) ?? source;

        var results = new Dictionary<string, ApplicationLocalizedCopy>(StringComparer.Ordinal);
        var providerEntries = new List<ApplicationCopyManifestEntry>();
        foreach (var entry in manifest.Entries)
        {
            if (entry.TranslationPolicy == ApplicationTranslationPolicies.NonTranslatable ||
                string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                results[entry.Id] = Source(entry, source, target);
                continue;
            }

            if (entry.TranslationPolicy == ApplicationTranslationPolicies.ApprovedOnly)
            {
                var approved = await _intelligence.TryGetTrustedScopedMemoryAsync(
                    source,
                    target,
                    entry.Source,
                    entry.Id,
                    entry.SourceRevision,
                    entry.Context,
                    TranslationIdentityHash(string.Join(',', entry.Placeholders)),
                    TranslationReuseScopes.Global,
                    string.Empty,
                    cancellationToken);
                results[entry.Id] = approved is not null && TranslationOutputValidator.IsValid(
                        entry.Source,
                        approved.Text,
                        string.Join(',', entry.Placeholders))
                    ? new ApplicationLocalizedCopy(
                        entry.Id,
                        entry.Source,
                        approved.Text,
                        entry.Context,
                        entry.SourceRevision,
                        entry.Placeholders,
                        "LegendConnectTranslationMemory",
                        approved.Provenance,
                        approved.QualityState,
                        approved.CreatedUtc,
                        Reused: true)
                    : Source(entry, source, target, "approved_translation_unavailable");
                continue;
            }

            providerEntries.Add(entry);
        }

        var translated = await _translations.TranslateRetainedBatchAsync(
            providerEntries.Select(entry => new RetainedTranslationRequest(
                entry.Id,
                entry.Source,
                source,
                target,
                entry.SourceRevision,
                entry.Context,
                string.Join(',', entry.Placeholders.Order(StringComparer.Ordinal)),
                TranslationReuseScopes.Global)).ToArray(),
            cancellationToken);
        for (var index = 0; index < providerEntries.Count; index++)
        {
            var entry = providerEntries[index];
            var translation = translated[index];
            results[entry.Id] = new ApplicationLocalizedCopy(
                entry.Id,
                entry.Source,
                translation.Text,
                entry.Context,
                entry.SourceRevision,
                entry.Placeholders,
                translation.Provider,
                translation.Provenance,
                translation.ValidationState,
                translation.CreatedUtc,
                translation.Reused,
                translation.ErrorCode);
        }

        var ordered = manifest.Entries.Select(entry => results[entry.Id]).ToArray();
        var failures = ordered.Count(entry => entry.FailureCode is not null);
        if (failures > 0)
        {
            _logger.LogWarning(
                "Application localization returned source fallbacks. SourceLanguage={SourceLanguage} TargetLanguage={TargetLanguage} FailureCount={FailureCount} EntryCount={EntryCount}",
                source,
                target,
                failures,
                ordered.Length);
        }

        return new ApplicationLocalizationCatalog(
            manifest.CatalogVersion,
            source,
            target,
            target,
            DateTime.UtcNow,
            failures == 0,
            ordered);
    }

    public async Task<ApplicationLocalizedCopy> LocalizeAsync(
        MessagingActor actor,
        string source,
        string context,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = _manifestSource.Manifest;
        var manifestEntry = manifest.Entries.SingleOrDefault(entry =>
            string.Equals(entry.Source, source, StringComparison.Ordinal) &&
            string.Equals(entry.Context, context, StringComparison.Ordinal));
        if (manifestEntry is null)
        {
            _logger.LogWarning(
                "Unregistered application copy was requested. Context={Context}",
                context);
            return Unregistered(source, context, "application_copy_unregistered");
        }

        var suppliedArguments = arguments ?? new Dictionary<string, string>();
        if (!manifestEntry.Placeholders.Order(StringComparer.Ordinal).SequenceEqual(
                suppliedArguments.Keys.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            return Interpolate(
                Source(manifestEntry, manifest.SourceLanguageCode, manifest.SourceLanguageCode,
                    "translation_arguments_invalid"),
                suppliedArguments);
        }

        var sourceLanguage = await _languages.NormalizeEnabledTranslationLanguageReadOnlyAsync(
            manifest.SourceLanguageCode,
            cancellationToken) ?? manifest.SourceLanguageCode;
        var preferred = await _preferences.GetCanonicalPreferredLanguageAsync(actor, cancellationToken);
        var targetLanguage = await _languages.NormalizeEnabledTranslationLanguageReadOnlyAsync(
            preferred,
            cancellationToken) ?? sourceLanguage;

        ApplicationLocalizedCopy result;
        if (manifestEntry.TranslationPolicy == ApplicationTranslationPolicies.NonTranslatable ||
            string.Equals(sourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            result = Source(manifestEntry, sourceLanguage, targetLanguage);
        }
        else if (manifestEntry.TranslationPolicy == ApplicationTranslationPolicies.ApprovedOnly)
        {
            var approved = await _intelligence.TryGetTrustedScopedMemoryAsync(
                sourceLanguage,
                targetLanguage,
                manifestEntry.Source,
                manifestEntry.Id,
                manifestEntry.SourceRevision,
                manifestEntry.Context,
                TranslationIdentityHash(string.Join(',', manifestEntry.Placeholders)),
                TranslationReuseScopes.Global,
                string.Empty,
                cancellationToken);
            result = approved is not null && TranslationOutputValidator.IsValid(
                    manifestEntry.Source,
                    approved.Text,
                    string.Join(',', manifestEntry.Placeholders))
                ? new ApplicationLocalizedCopy(
                    manifestEntry.Id,
                    manifestEntry.Source,
                    approved.Text,
                    manifestEntry.Context,
                    manifestEntry.SourceRevision,
                    manifestEntry.Placeholders,
                    "LegendConnectTranslationMemory",
                    approved.Provenance,
                    approved.QualityState,
                    approved.CreatedUtc,
                    Reused: true)
                : Source(
                    manifestEntry,
                    sourceLanguage,
                    targetLanguage,
                    "approved_translation_unavailable");
        }
        else
        {
            var translated = await _translations.TranslateRetainedAsync(
                new RetainedTranslationRequest(
                    manifestEntry.Id,
                    manifestEntry.Source,
                    sourceLanguage,
                    targetLanguage,
                    manifestEntry.SourceRevision,
                    manifestEntry.Context,
                    string.Join(',', manifestEntry.Placeholders),
                    TranslationReuseScopes.Global),
                cancellationToken);
            result = new ApplicationLocalizedCopy(
                manifestEntry.Id,
                manifestEntry.Source,
                translated.Text,
                manifestEntry.Context,
                manifestEntry.SourceRevision,
                manifestEntry.Placeholders,
                translated.Provider,
                translated.Provenance,
                translated.ValidationState,
                translated.CreatedUtc,
                translated.Reused,
                translated.ErrorCode);
        }

        return Interpolate(result, suppliedArguments);
    }

    private static ApplicationLocalizedCopy Interpolate(
        ApplicationLocalizedCopy copy,
        IReadOnlyDictionary<string, string> arguments) => copy with
        {
            Text = arguments.Aggregate(
                copy.Text,
                (text, argument) => text.Replace(
                    $"{{{argument.Key}}}",
                    argument.Value,
                    StringComparison.Ordinal))
        };

    private static ApplicationLocalizedCopy Unregistered(
        string source,
        string context,
        string failureCode) => new(
        "application.copy.unregistered",
        source,
        source,
        context,
        "unregistered",
        Array.Empty<string>(),
        "Source",
        "Source",
        "Unregistered",
        DateTime.UtcNow,
        Reused: true,
        failureCode);

    private static ApplicationLocalizedCopy Source(
        ApplicationCopyManifestEntry entry,
        string source,
        string target,
        string? failureCode = null) => new(
            entry.Id,
            entry.Source,
            entry.Source,
            entry.Context,
            entry.SourceRevision,
            entry.Placeholders,
            "Source",
            "Source",
            string.Equals(source, target, StringComparison.OrdinalIgnoreCase)
                ? "SourceLanguage"
                : "NonTranslatable",
            DateTime.UtcNow,
            Reused: true,
            failureCode);

    private static string TranslationIdentityHash(string value) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();
}
