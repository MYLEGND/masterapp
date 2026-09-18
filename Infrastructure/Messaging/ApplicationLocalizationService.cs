using System.Text.Json;
using System.Diagnostics;
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
    string ReuseScope,
    IReadOnlyDictionary<string, string>? PresetTranslations = null);

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
            entry.ReuseScope) + (entry.PresetTranslations is null ? string.Empty :
                "\u001f" + string.Join("\u001e", entry.PresetTranslations.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => $"{item.Key}={item.Value}")))));
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

            if (entry.PresetTranslations is not null &&
                (entry.TranslationPolicy != ApplicationTranslationPolicies.ApprovedOnly ||
                 entry.PresetTranslations.Any(item => !LegendLanguageIdentity.TryNormalize(item.Key, out _) ||
                    !TranslationOutputValidator.IsValid(entry.Source, item.Value, string.Join(',', entry.Placeholders)))))
                throw new InvalidOperationException($"Invalid preset application copy: {entry.Id}");

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
        var source = await _languages.NormalizeEnabledTranslationLanguageReadOnlyAsync(
            _manifestSource.Manifest.SourceLanguageCode, cancellationToken) ?? _manifestSource.Manifest.SourceLanguageCode;
        var preferred = await _preferences.GetCanonicalPreferredLanguageAsync(actor, cancellationToken);
        var target = await _languages.NormalizeEnabledTranslationLanguageReadOnlyAsync(
            preferred, cancellationToken) ?? source;
        return await BuildCatalogAsync(target, maximumProviderBatches: 1, cancellationToken);
    }

    public Task<ApplicationLocalizationCatalog> InspectCatalogAsync(
        string targetLanguageCode, CancellationToken cancellationToken = default) =>
        ExplicitCatalogAsync(targetLanguageCode, maximumProviderBatches: 0, cancellationToken);

    public Task<ApplicationLocalizationCatalog> PrepareCatalogAsync(
        string targetLanguageCode, CancellationToken cancellationToken = default) =>
        ExplicitCatalogAsync(targetLanguageCode, maximumProviderBatches: 1, cancellationToken);

    private async Task<ApplicationLocalizationCatalog> ExplicitCatalogAsync(
        string targetLanguageCode, int maximumProviderBatches, CancellationToken cancellationToken)
    {
        var target = await _languages.NormalizeEnabledTranslationLanguageReadOnlyAsync(
            targetLanguageCode, cancellationToken)
            ?? throw new ArgumentException("The target language is not enabled for translation.", nameof(targetLanguageCode));
        return await BuildCatalogAsync(target, maximumProviderBatches, cancellationToken);
    }

    private async Task<ApplicationLocalizationCatalog> BuildCatalogAsync(
        string target, int maximumProviderBatches, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var manifest = _manifestSource.Manifest;
        var source = await _languages.NormalizeEnabledTranslationLanguageReadOnlyAsync(
            manifest.SourceLanguageCode, cancellationToken) ?? manifest.SourceLanguageCode;

        var approvedLookups = manifest.Entries
            .Where(entry => entry.TranslationPolicy == ApplicationTranslationPolicies.ApprovedOnly && source != target)
            .Select(entry => new LegendTrustedTranslationLookup(entry.Id, source, target, entry.Source,
                entry.Id, entry.SourceRevision, entry.Context,
                TranslationIdentityHash(string.Join(',', entry.Placeholders)), TranslationReuseScopes.Global, string.Empty))
            .ToArray();
        var approvedMatches = await _intelligence.TryGetTrustedScopedMemoriesAsync(approvedLookups, cancellationToken);
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
                if (Preset(entry, source, target) is { } preset)
                {
                    results[entry.Id] = preset;
                    continue;
                }
                approvedMatches.TryGetValue(entry.Id, out var approved);
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
            cancellationToken,
            maximumProviderBatches: maximumProviderBatches);
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
                translation.ErrorCode, translation.RetryAfterUtc);
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

        var continuation = BuildContinuation(ordered);
        ApplicationLocalizationTelemetry.Catalog(source, target, continuation.Disposition,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new ApplicationLocalizationCatalog(
            manifest.CatalogVersion,
            source,
            target,
            target,
            DateTime.UtcNow,
            failures == 0,
            ordered,
            continuation);
    }

    internal static ApplicationLocalizationContinuation BuildContinuation(IReadOnlyList<ApplicationLocalizedCopy> entries)
    {
        var failures = entries.Where(entry => entry.FailureCode is not null).ToArray();
        if (failures.Length == 0) return new("Complete", 0, null);
        var actionable = failures.Where(entry => entry.FailureCode != "approved_translation_unavailable").ToArray();
        if (actionable.Length == 0) return new("AwaitingApproval", failures.Length, null);
        // Unknown, configuration, authorization and invalid-output errors stop automatic work.
        // A pending entry may not conceal a terminal provider/capacity failure.
        if (actionable.Any(entry => entry.FailureCode is not (
            "translation_pending" or "translation_provider_timeout" or
            "translation_provider_transient_failure" or "translation_provider_rate_limited" or
            "translation_capacity_temporarily_unavailable" or "translation_capacity_reservation_pending" or
            "translation_capacity_hourly_exhausted" or "translation_capacity_monthly_exhausted")))
            return new("Blocked", failures.Length, null);
        var retryAt = actionable.Max(entry => entry.RetryAfterUtc);
        var delay = retryAt is { } date ? Math.Max(1, (int)Math.Ceiling((date - DateTime.UtcNow).TotalSeconds)) :
            actionable.Any(entry => entry.FailureCode != "translation_pending") ? 15 : 1;
        return new(actionable.Any(entry => entry.FailureCode != "translation_pending") ? "RetryableFailure" : "Pending",
            failures.Length, delay);
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
        else if (Preset(manifestEntry, sourceLanguage, targetLanguage) is { } preset)
        {
            result = preset;
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

    private static ApplicationLocalizedCopy? Preset(ApplicationCopyManifestEntry entry, string source, string target) =>
        entry.PresetTranslations?.TryGetValue(target, out var text) == true
            ? Source(entry, source, target) with
            {
                Text = text!, Provider = "ApplicationPreset", Provenance = "ApplicationCopyManifest",
                ValidationState = "Preset"
            }
            : null;

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
