using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;

namespace Infrastructure.Messaging;

internal static partial class ApplicationLocalizationTelemetry
{
    private static readonly Meter Meter = new("Legend.ApplicationLocalization", "1.0.0");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("legend.localization.requests");
    private static readonly Counter<long> CacheHits = Meter.CreateCounter<long>("legend.localization.cache.hits");
    private static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>("legend.localization.cache.misses");
    private static readonly Counter<long> CoalescedRequests = Meter.CreateCounter<long>("legend.localization.requests.coalesced");
    private static readonly Counter<long> ProviderWrites = Meter.CreateCounter<long>("legend.localization.provider.persisted");
    private static readonly Counter<long> ProviderOperations = Meter.CreateCounter<long>("legend.localization.provider.operations");
    private static readonly Counter<long> ProviderCharacters = Meter.CreateCounter<long>("legend.localization.provider.characters");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("legend.localization.failures");

    internal static void SameLanguage(string source) =>
        Requests.Add(1, Tags(source, source, "same-language"));

    internal static void ApprovedMemoryHit(string source, string target)
    {
        Requests.Add(1, Tags(source, target, "approved-memory"));
        CacheHits.Add(1, Tags(source, target, "approved"));
    }

    internal static void RetainedHit(string source, string target)
    {
        Requests.Add(1, Tags(source, target, "retained-memory"));
        CacheHits.Add(1, Tags(source, target, "retained"));
    }

    internal static void Miss(string source, string target)
    {
        Requests.Add(1, Tags(source, target, "miss"));
        CacheMisses.Add(1, Tags(source, target, "retained"));
    }

    internal static void Coalesced(string source, string target) =>
        CoalescedRequests.Add(1, Tags(source, target, "in-flight"));

    internal static void ProviderPersisted(string source, string target) =>
        ProviderWrites.Add(1, Tags(source, target, "azure"));

    internal static void ProviderOperation(
        string source,
        string target,
        int characters,
        bool succeeded)
    {
        var tags = Tags(source, target, succeeded ? "succeeded" : "failed");
        ProviderOperations.Add(1, tags);
        ProviderCharacters.Add(characters, tags);
    }

    internal static void Failure(string reason, string? source, string? target) =>
        Failures.Add(1, new TagList
        {
            { "source_language", source ?? "unknown" },
            { "target_language", target ?? "unknown" },
            { "reason", reason }
        });

    private static TagList Tags(string source, string target, string path) => new()
    {
        { "source_language", source },
        { "target_language", target },
        { "path", path }
    };
}

/// <summary>
/// Fail-closed structural validation for provider output. It compares only
/// non-secret structure and never emits source or translated text.
/// </summary>
internal static partial class TranslationOutputValidator
{
    internal static IReadOnlyList<string> PlaceholderNames(string source) =>
        PlaceholderRegex().Matches(source)
            .Select(match => match.Groups[1].Value)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    internal static bool IsValid(
        string source,
        string translated,
        string placeholderContract)
    {
        if (string.IsNullOrWhiteSpace(translated) || translated.Length > 10_000)
            return false;

        var expectedPlaceholders = placeholderContract
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.Trim('{', '}'))
            .Where(name => name.Length > 0)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var sourcePlaceholders = PlaceholderRegex().Matches(source)
            .Select(match => match.Groups[1].Value)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var targetPlaceholders = PlaceholderRegex().Matches(translated)
            .Select(match => match.Groups[1].Value)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (!sourcePlaceholders.SequenceEqual(expectedPlaceholders, StringComparer.Ordinal) ||
            !targetPlaceholders.SequenceEqual(expectedPlaceholders, StringComparer.Ordinal))
            return false;

        if (!TokenMultiset(MarkupRegex(), source).SequenceEqual(
                TokenMultiset(MarkupRegex(), translated),
                StringComparer.Ordinal))
            return false;
        if (!TokenMultiset(UrlRegex(), source).SequenceEqual(
                TokenMultiset(UrlRegex(), translated),
                StringComparer.Ordinal))
            return false;

        return source.Count(character => character == '\n') ==
               translated.Count(character => character == '\n');
    }

    internal static ProtectedTranslationSource ProtectNonTranslatableBrands(
        string source,
        string placeholderContract)
    {
        var protectedText = source;
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        protectedText = BrandRegex().Replace(protectedText, match =>
        {
            string name;
            do
            {
                index++;
                name = $"legendBrand{index}";
            }
            while (source.Contains($"{{{name}}}", StringComparison.Ordinal));
            tokens[name] = match.Value;
            return $"{{{name}}}";
        });

        var placeholders = placeholderContract
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(tokens.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return new ProtectedTranslationSource(
            protectedText,
            string.Join(',', placeholders),
            tokens);
    }

    private static IEnumerable<string> TokenMultiset(Regex regex, string value) =>
        regex.Matches(value)
            .Select(match => match.Value)
            .OrderBy(token => token, StringComparer.Ordinal);

    [GeneratedRegex("\\{([A-Za-z][A-Za-z0-9_]*)\\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex("</?[A-Za-z][^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex MarkupRegex();

    [GeneratedRegex("https://[^\\s<>{}]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex("(?<![A-Za-z0-9])(?:Legend® Ai|Legend AI|OpenAI|LEGEND®|LEGEND|Legend)(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex BrandRegex();
}

internal sealed record ProtectedTranslationSource(
    string Text,
    string PlaceholderContract,
    IReadOnlyDictionary<string, string> Tokens)
{
    internal string Restore(string translated) => Tokens.Aggregate(
        translated,
        (text, token) => text.Replace(
            $"{{{token.Key}}}",
            token.Value,
            StringComparison.Ordinal));
}
