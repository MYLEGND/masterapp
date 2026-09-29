using Shared.Analytics;

namespace Infrastructure.WebsiteEditing;

/// <summary>
/// Server-owned binding between a public Website Studio page and a protected
/// application runtime. WebsiteContentDocument v3 owns presentation/content,
/// while these templates retain form state, validation, submission, results,
/// booking, attribution, and server event semantics.
/// </summary>
public static class WebsiteSystemTemplateAuthority
{
    public const string Prefix = "protect_template:";

    // One route-to-template authority. Known template keys are derived from this
    // map so validation and resolution cannot drift into parallel lists.
    private static readonly IReadOnlyDictionary<string, string> TemplatesByRoute =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/RiskAssessment"] = Prefix + "risk_assessment",
            ["/Quote/Life"] = Prefix + "life_wizard",
            ["/Quote/Whole-Life"] = Prefix + "life_wizard",
            ["/Quote/Term-Life"] = Prefix + "life_wizard",
            ["/Quote/Final-Expense"] = Prefix + "life_wizard",
            ["/Quote/Mortgage-Protection"] = Prefix + "life_wizard",
            ["/Quote/IUL"] = Prefix + "life_wizard",
            ["/Quote/Home"] = Prefix + "home_quote",
            ["/Quote/Auto"] = Prefix + "auto_quote",
            ["/Quote/Commercial"] = Prefix + "commercial_quote",
            ["/Quote/Disability"] = Prefix + "disability_quote",
            ["/Quote/Dental-Vision-Hearing"] = Prefix + "dvh_quote",
            ["/ThankYou"] = Prefix + "quote_thank_you"
        };

    private static readonly HashSet<string> KnownKeys =
        TemplatesByRoute.Values.ToHashSet(StringComparer.Ordinal);

    public static bool IsKnownTemplateKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) && KnownKeys.Contains(value.Trim());

    public static bool IsRuntimeFormSystemKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("protect_runtime_form:", StringComparison.Ordinal))
            return false;
        var suffix = value["protect_runtime_form:".Length..];
        return suffix.Length is > 0 and <= 120 &&
               suffix.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.');
    }

    public static string? Resolve(string siteKey, string? pagePath)
    {
        if (!string.Equals(siteKey, WebsiteEditorSiteKeys.Protect, StringComparison.Ordinal))
            return null;

        var path = NormalizeProtectPath(pagePath);
        return TemplatesByRoute.TryGetValue(path, out var templateKey) ? templateKey : null;
    }

    public static WebsiteContentDocument Apply(string siteKey, WebsiteContentDocument document)
    {
        foreach (var (path, page) in document.Pages)
            page.SystemTemplateKey = Resolve(siteKey, path);
        return document;
    }

    private static string NormalizeProtectPath(string? pagePath)
    {
        var path = string.IsNullOrWhiteSpace(pagePath) ? "/" : pagePath.Trim();
        if (path.StartsWith("/a/", StringComparison.OrdinalIgnoreCase))
        {
            var next = path.IndexOf('/', 3);
            path = next < 0 ? "/" : path[next..];
        }

        path = ProtectRouteCatalog.CanonicalPath(path);
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }
}
