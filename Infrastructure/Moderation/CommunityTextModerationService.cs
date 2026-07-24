using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Domain.Moderation;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Moderation;

/// <summary>Single server-authoritative boundary for community and messaging text.</summary>
internal sealed class CommunityTextModerationService : ICommunityTextModerationService
{
    private const string PolicyVersion = "2026.07";
    private readonly IReadOnlyDictionary<string, (string Category, string Severity, bool Review)> _terms;

    public CommunityTextModerationService(IConfiguration configuration)
    {
        var configured = configuration.GetSection("CommunityModeration:Terms").Get<string[]>() ?? Array.Empty<string>();
        var terms = new Dictionary<string, (string, string, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            ["fuck"] = ("Profanity", "Medium", false),
            ["shit"] = ("Profanity", "Medium", false),
            ["bitch"] = ("Harassment", "Medium", false),
            ["kill yourself"] = ("ThreatOrSelfHarm", "High", true),
            ["i will kill"] = ("Threat", "High", true),
            ["send nudes"] = ("SexualSolicitation", "High", true),
            ["nudes"] = ("SexuallyExplicit", "High", true),
            ["racial slur"] = ("Hate", "High", true)
        };

        foreach (var configuredTerm in configured)
        {
            var term = Normalize(configuredTerm);
            if (!string.IsNullOrWhiteSpace(term))
                terms[term] = ("ConfiguredPolicy", "High", true);
        }

        _terms = terms;
    }

    public CommunityTextModerationResult Evaluate(string? content, string surface)
    {
        if (string.IsNullOrWhiteSpace(content))
            return CommunityTextModerationResult.Allowed();

        var normalized = Normalize(content);
        var compact = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
        foreach (var term in _terms)
        {
            if (ContainsTerm(normalized, term.Key) || (!term.Key.Contains(' ') && compact.Contains(term.Key, StringComparison.Ordinal)))
            {
                var metadata = term.Value;
                return new CommunityTextModerationResult(false, metadata.Category, metadata.Severity,
                    $"COMMUNITY_{metadata.Category.ToUpperInvariant()}", metadata.Review);
            }
        }

        return CommunityTextModerationResult.Allowed();
    }

    private static bool ContainsTerm(string content, string term)
    {
        if (term.Contains(' '))
            return content.Contains(term, StringComparison.Ordinal);

        return Regex.IsMatch(content, $@"(?<![a-z]){Regex.Escape(term)}(?![a-z])", RegexOptions.CultureInvariant);
    }

    private static string Normalize(string? value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (char.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark || character is '\u200B' or '\u200C' or '\u200D' or '\uFEFF')
                continue;

            var mapped = char.ToLowerInvariant(character) switch
            {
                '0' => 'o', '1' => 'i', '3' => 'e', '4' => 'a', '5' => 's', '7' => 't', '$' => 's', '@' => 'a', _ => char.ToLowerInvariant(character)
            };
            builder.Append(char.IsLetterOrDigit(mapped) ? mapped : ' ');
        }

        var compact = Regex.Replace(builder.ToString(), "([a-z])\\1{2,}", "$1$1", RegexOptions.CultureInvariant);
        return Regex.Replace(compact, "\\s+", " ", RegexOptions.CultureInvariant).Trim();
    }
}
