namespace Shared.Analytics;

public sealed record ProtectPublicRoute(string Path, string PageKey, string DisplayName,
    string? QuoteType = null, bool HasPaidLanding = false);

/// <summary>Public canonical inventory shared by sitemap and paid-route discovery.
/// Paid variants canonicalize to their public control and are not separate index entries.</summary>
public static class ProtectRouteCatalog
{
    public const string CanonicalOrigin = "https://protect.mylegnd.com";
    public static IReadOnlyList<ProtectPublicRoute> Routes { get; } = Array.AsReadOnly(new[]
    {
        new ProtectPublicRoute("/", "home", "Home"),
        new ProtectPublicRoute("/RiskAssessment", "risk_assessment", "Risk Assessment"),
        new ProtectPublicRoute("/Quote", "quote_index", "Coverage"),
        new ProtectPublicRoute("/Quote/Life", "quote_life", "Life Insurance", "life", true),
        new ProtectPublicRoute("/Quote/Whole-Life", "quote_whole_life", "Whole Life", "whole_life", true),
        new ProtectPublicRoute("/Quote/Term-Life", "quote_term_life", "Term Life", "term_life", true),
        new ProtectPublicRoute("/Quote/Final-Expense", "quote_final_expense", "Final Expense", "final_expense", true),
        new ProtectPublicRoute("/Quote/Mortgage-Protection", "quote_mortgage_protection", "Mortgage Protection", "mortgage_protection", true),
        new ProtectPublicRoute("/Quote/IUL", "quote_iul", "Indexed Universal Life (IUL)", "iul", true),
        new ProtectPublicRoute("/Quote/Disability", "quote_disability", "Disability", "disability", true),
        new ProtectPublicRoute("/Quote/Dental-Vision-Hearing", "quote_dvh", "Dental Vision Hearing", "dvh", true),
        new ProtectPublicRoute("/Quote/Auto", "quote_auto", "Auto", "auto"),
        new ProtectPublicRoute("/Quote/Home", "quote_home", "Home Insurance", "home"),
        new ProtectPublicRoute("/Quote/Commercial", "quote_commercial", "Commercial", "commercial"),
        new ProtectPublicRoute("/Contact", "contact", "Contact"),
        new ProtectPublicRoute("/Home/Privacy", "privacy", "Privacy"),
        new ProtectPublicRoute("/Home/Terms", "terms", "Terms")
    });

    public static string CanonicalPath(string? path)
    {
        var value = string.IsNullOrWhiteSpace(path) ? "/" : path.TrimEnd('/');
        if (value.Length == 0) return "/";
        // Keep the agent scope while canonicalizing aliases and paid variants.
        var prefix = "";
        if (value.StartsWith("/a/", StringComparison.OrdinalIgnoreCase))
        {
            var end = value.IndexOf('/', 3);
            if (end < 0) return value;
            prefix = value[..end];
            value = value[end..];
        }
        if (value.EndsWith("/landing", StringComparison.OrdinalIgnoreCase)) value = value[..^8];
        if (value.Equals("/Privacy", StringComparison.OrdinalIgnoreCase)) value = "/Home/Privacy";
        if (value.Equals("/Terms", StringComparison.OrdinalIgnoreCase)) value = "/Home/Terms";
        var route = Routes.FirstOrDefault(route => route.Path.Equals(value, StringComparison.OrdinalIgnoreCase));
        return prefix + (route?.Path ?? value);
    }
}
