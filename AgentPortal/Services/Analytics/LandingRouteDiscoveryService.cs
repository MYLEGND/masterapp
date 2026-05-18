using AgentPortal.Models.Analytics;
using Microsoft.Extensions.Options;

namespace AgentPortal.Services.Analytics;

public sealed class LandingRouteDiscoveryService : ILandingRouteDiscoveryService
{
    private const string DefaultPublicBaseUrl = "https://protect.mylegnd.com";
    private const string DefaultLandingVariant = "landing";

    private readonly IOptionsMonitor<LandingRoutesOptions> _options;
    private readonly IConfiguration _config;

    public LandingRouteDiscoveryService(IOptionsMonitor<LandingRoutesOptions> options, IConfiguration config)
    {
        _options = options;
        _config = config;
    }

    public string GetBaseUrl()
    {
        return NormalizeBaseUrl(_options.CurrentValue.BaseUrl)
            ?? NormalizeBaseUrl(_config["Protect:PublicBaseUrl"])
            ?? NormalizeBaseUrl(_config["ProtectWebsite:PublicBaseUrl"])
            ?? NormalizeBaseUrl(_config["ProtectWebsite:BaseUrl"])
            ?? DefaultPublicBaseUrl;
    }

    public IReadOnlyList<LandingRouteDefinition> GetAllRoutes()
    {
        var baseUrl = GetBaseUrl();
        var configuredRoutes = _options.CurrentValue.Routes ?? new List<LandingRouteRegistryItem>();
        var fallbackRoutes = BuildFallbackRoutes();

        var allRouteKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in fallbackRoutes)
        {
            var lookupKey = ResolveRouteLookupKey(route);
            if (!string.IsNullOrWhiteSpace(lookupKey))
                allRouteKeys.Add(lookupKey);
        }

        foreach (var route in configuredRoutes)
        {
            var lookupKey = ResolveRouteLookupKey(route);
            if (!string.IsNullOrWhiteSpace(lookupKey))
                allRouteKeys.Add(lookupKey);
        }

        var routes = new List<LandingRouteDefinition>();
        foreach (var lookupKey in allRouteKeys)
        {
            var fallback = fallbackRoutes.FirstOrDefault(x => string.Equals(ResolveRouteLookupKey(x), lookupKey, StringComparison.OrdinalIgnoreCase));
            var configured = configuredRoutes.FirstOrDefault(x => string.Equals(ResolveRouteLookupKey(x), lookupKey, StringComparison.OrdinalIgnoreCase));
            var merged = MergeRoute(fallback, configured);
            if (merged is null)
                continue;

            routes.Add(ToDefinition(merged, baseUrl));
        }

        return routes
            .OrderByDescending(x => x.IsActive)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<LandingRouteDefinition> GetActiveRoutes()
    {
        return GetAllRoutes()
            .Where(route => route.IsActive)
            .Select(CloneWithActiveVariants)
            .Where(route => route.AvailableVariants.Count > 0 || !string.IsNullOrWhiteSpace(route.ControlUrl))
            .ToList();
    }

    private static LandingRouteDefinition CloneWithActiveVariants(LandingRouteDefinition route)
    {
        var activeVariants = route.AvailableVariants
            .Where(variant => variant.IsActive)
            .OrderByDescending(variant => variant.IsControl)
            .ThenBy(variant => variant.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var controlVariant = activeVariants.FirstOrDefault(variant => variant.IsControl)
            ?? route.AvailableVariants.FirstOrDefault(variant => variant.IsControl);

        return new LandingRouteDefinition
        {
            Key = route.Key,
            DisplayName = route.DisplayName,
            BasePath = route.BasePath,
            QuoteType = route.QuoteType,
            PageMode = route.PageMode,
            DefaultPageVariant = route.DefaultPageVariant,
            AvailableVariants = activeVariants,
            EffectivePageKeys = activeVariants
                .Select(variant => variant.EffectivePageKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            IsPaidLanding = route.IsPaidLanding,
            IsActive = route.IsActive,
            Notes = route.Notes,
            ControlUrl = controlVariant?.Url ?? route.ControlUrl,
            ComparisonHelperText = BuildComparisonHelperText(activeVariants)
        };
    }

    private static LandingRouteDefinition ToDefinition(LandingRouteRegistryItem route, string baseUrl)
    {
        var routeKey = ResolveRouteKey(route);
        var basePath = NormalizeBasePath(route.BasePath);
        var defaultPageVariant = NormalizeVariantToken(route.DefaultPageVariant) ?? DefaultLandingVariant;
        var pageMode = NormalizeToken(route.PageMode) ?? "paid_landing";
        var quoteType = NormalizeToken(route.QuoteType) ?? BuildQuoteTypeFromBasePath(basePath) ?? "unknown";
        var displayName = route.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = ResolveRouteDisplayName(quoteType);

        var variants = (route.Variants ?? new List<LandingVariantRegistryItem>())
            .Select(variant =>
            {
                var variantName = NormalizeVariantToken(variant.Variant) ?? defaultPageVariant;
                var isControl = variant.IsControl ?? string.Equals(variantName, defaultPageVariant, StringComparison.OrdinalIgnoreCase);
                var effectivePageKey = NormalizeIdentifier(variant.EffectivePageKey)
                    ?? BuildVariantEffectivePageKey(routeKey, variantName, isControl);
                var url = BuildVariantUrl(baseUrl, basePath, defaultPageVariant, variantName, isControl);
                return new LandingVariantDefinition
                {
                    Variant = variantName,
                    DisplayName = string.IsNullOrWhiteSpace(variant.DisplayName)
                        ? ResolveVariantDisplayName(variantName, isControl)
                        : variant.DisplayName.Trim(),
                    Url = url,
                    EffectivePageKey = effectivePageKey,
                    Description = string.IsNullOrWhiteSpace(variant.Description) ? null : variant.Description.Trim(),
                    IsControl = isControl,
                    IsActive = variant.IsActive ?? true,
                    SuggestedMetaDestinationUrl = url
                };
            })
            .OrderByDescending(variant => variant.IsControl)
            .ThenBy(variant => variant.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var controlVariant = variants.FirstOrDefault(variant => variant.IsControl) ?? variants.FirstOrDefault();

        return new LandingRouteDefinition
        {
            Key = routeKey,
            DisplayName = displayName ?? routeKey,
            BasePath = basePath,
            QuoteType = quoteType,
            PageMode = pageMode,
            DefaultPageVariant = defaultPageVariant,
            AvailableVariants = variants,
            EffectivePageKeys = variants
                .Select(variant => variant.EffectivePageKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            IsPaidLanding = string.Equals(pageMode, "paid_landing", StringComparison.OrdinalIgnoreCase),
            IsActive = route.IsActive ?? true,
            Notes = string.IsNullOrWhiteSpace(route.Notes) ? null : route.Notes.Trim(),
            ControlUrl = controlVariant?.Url ?? string.Empty,
            ComparisonHelperText = BuildComparisonHelperText(variants.Where(variant => variant.IsActive).ToList())
        };
    }

    private static LandingRouteRegistryItem? MergeRoute(LandingRouteRegistryItem? fallback, LandingRouteRegistryItem? configured)
    {
        var basePath = NormalizeBasePath(FirstNonEmpty(configured?.BasePath, fallback?.BasePath));
        var key = ResolveRouteKey(configured) ?? ResolveRouteKey(fallback) ?? BuildRouteKeyFromBasePath(basePath);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(basePath))
            return null;

        var quoteType = NormalizeToken(FirstNonEmpty(configured?.QuoteType, fallback?.QuoteType))
            ?? BuildQuoteTypeFromBasePath(basePath)
            ?? "unknown";

        var merged = new LandingRouteRegistryItem
        {
            Key = key,
            DisplayName = FirstNonEmpty(configured?.DisplayName, fallback?.DisplayName) ?? ResolveRouteDisplayName(quoteType),
            BasePath = basePath,
            QuoteType = quoteType,
            PageMode = FirstNonEmpty(configured?.PageMode, fallback?.PageMode) ?? "paid_landing",
            DefaultPageVariant = NormalizeVariantToken(FirstNonEmpty(configured?.DefaultPageVariant, fallback?.DefaultPageVariant)) ?? DefaultLandingVariant,
            IsActive = configured?.IsActive ?? fallback?.IsActive ?? true,
            Notes = FirstNonEmpty(configured?.Notes, fallback?.Notes),
            Variants = MergeVariants(fallback?.Variants, configured?.Variants, key, basePath, quoteType, NormalizeVariantToken(FirstNonEmpty(configured?.DefaultPageVariant, fallback?.DefaultPageVariant)) ?? DefaultLandingVariant)
        };

        if (merged.Variants.Count == 0)
            merged.Variants.Add(CreateDefaultControlVariant(key, merged.DefaultPageVariant ?? DefaultLandingVariant, quoteType));

        return merged;
    }

    private static List<LandingVariantRegistryItem> MergeVariants(
        IReadOnlyCollection<LandingVariantRegistryItem>? fallbackVariants,
        IReadOnlyCollection<LandingVariantRegistryItem>? configuredVariants,
        string routeKey,
        string basePath,
        string quoteType,
        string defaultPageVariant)
    {
        var variants = new List<LandingVariantRegistryItem>();
        fallbackVariants ??= Array.Empty<LandingVariantRegistryItem>();
        configuredVariants ??= Array.Empty<LandingVariantRegistryItem>();

        var allVariantKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variant in fallbackVariants)
        {
            var key = ResolveVariantLookupKey(variant, defaultPageVariant);
            if (!string.IsNullOrWhiteSpace(key))
                allVariantKeys.Add(key);
        }

        foreach (var variant in configuredVariants)
        {
            var key = ResolveVariantLookupKey(variant, defaultPageVariant);
            if (!string.IsNullOrWhiteSpace(key))
                allVariantKeys.Add(key);
        }

        foreach (var variantKey in allVariantKeys)
        {
            var fallback = fallbackVariants.FirstOrDefault(x => string.Equals(ResolveVariantLookupKey(x, defaultPageVariant), variantKey, StringComparison.OrdinalIgnoreCase));
            var configured = configuredVariants.FirstOrDefault(x => string.Equals(ResolveVariantLookupKey(x, defaultPageVariant), variantKey, StringComparison.OrdinalIgnoreCase));
            var normalizedVariant = NormalizeVariantToken(FirstNonEmpty(configured?.Variant, fallback?.Variant)) ?? defaultPageVariant;
            var isControl = configured?.IsControl ?? fallback?.IsControl ?? string.Equals(normalizedVariant, defaultPageVariant, StringComparison.OrdinalIgnoreCase);
            variants.Add(new LandingVariantRegistryItem
            {
                Variant = normalizedVariant,
                DisplayName = FirstNonEmpty(configured?.DisplayName, fallback?.DisplayName) ?? ResolveVariantDisplayName(normalizedVariant, isControl),
                EffectivePageKey = NormalizeIdentifier(FirstNonEmpty(configured?.EffectivePageKey, fallback?.EffectivePageKey))
                    ?? BuildVariantEffectivePageKey(routeKey, normalizedVariant, isControl),
                Description = FirstNonEmpty(configured?.Description, fallback?.Description),
                IsControl = isControl,
                IsActive = configured?.IsActive ?? fallback?.IsActive ?? true
            });
        }

        if (variants.Count == 0)
            variants.Add(CreateDefaultControlVariant(routeKey, defaultPageVariant, quoteType));

        return variants
            .OrderByDescending(variant => variant.IsControl ?? false)
            .ThenBy(variant => variant.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static LandingVariantRegistryItem CreateDefaultControlVariant(string routeKey, string defaultPageVariant, string quoteType)
    {
        return new LandingVariantRegistryItem
        {
            Variant = defaultPageVariant,
            DisplayName = "Control",
            EffectivePageKey = BuildVariantEffectivePageKey(routeKey, defaultPageVariant, isControl: true),
            Description = $"Default paid landing for {ResolveRouteDisplayName(quoteType)}.",
            IsControl = true,
            IsActive = true
        };
    }

    private static string BuildComparisonHelperText(IReadOnlyCollection<LandingVariantDefinition> variants)
    {
        if (variants.Count == 0)
            return string.Empty;

        var controlKey = variants.FirstOrDefault(variant => variant.IsControl)?.EffectivePageKey;
        var activeTestKeys = variants
            .Where(variant => !variant.IsControl)
            .Select(variant => variant.EffectivePageKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (string.IsNullOrWhiteSpace(controlKey) || activeTestKeys.Count == 0)
            return "Compare these rows in Page Performance using Ads Only traffic.";

        return activeTestKeys.Count == 1
            ? $"Compare these rows in Page Performance using Ads Only traffic. Control: {controlKey}. Variant: {activeTestKeys[0]}."
            : $"Compare these rows in Page Performance using Ads Only traffic. Control: {controlKey}. Variants: {string.Join(", ", activeTestKeys)}.";
    }

    private static string BuildVariantUrl(string baseUrl, string basePath, string defaultPageVariant, string variant, bool isControl)
    {
        var absoluteUrl = CombineUrl(baseUrl, basePath);
        if (string.IsNullOrWhiteSpace(absoluteUrl))
            return string.Empty;

        if (isControl || string.Equals(variant, defaultPageVariant, StringComparison.OrdinalIgnoreCase))
            return absoluteUrl;

        if (Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var uri))
        {
            var builder = new UriBuilder(uri);
            var variantParam = $"variant={Uri.EscapeDataString(variant)}";
            builder.Query = string.IsNullOrWhiteSpace(builder.Query)
                ? variantParam
                : $"{builder.Query.TrimStart('?')}&{variantParam}";
            return builder.Uri.ToString();
        }

        var separator = absoluteUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{absoluteUrl}{separator}variant={Uri.EscapeDataString(variant)}";
    }

    private static string CombineUrl(string baseUrl, string basePath)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(basePath))
            return string.Empty;

        if (Uri.TryCreate(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute, out var baseUri))
        {
            return new Uri(baseUri, basePath).ToString();
        }

        return $"{baseUrl.TrimEnd('/')}/{basePath.TrimStart('/')}";
    }

    private static string ResolveRouteKey(LandingRouteRegistryItem? route)
    {
        return NormalizeIdentifier(route?.Key)
            ?? BuildRouteKeyFromBasePath(route?.BasePath)
            ?? string.Empty;
    }

    private static string ResolveRouteLookupKey(LandingRouteRegistryItem? route)
    {
        return ResolveRouteKey(route);
    }

    private static string ResolveVariantLookupKey(LandingVariantRegistryItem? variant, string defaultPageVariant)
    {
        return NormalizeVariantToken(variant?.Variant) ?? defaultPageVariant;
    }

    private static string BuildVariantEffectivePageKey(string routeKey, string variant, bool isControl)
    {
        var normalizedRouteKey = NormalizeIdentifier(routeKey) ?? routeKey;
        if (string.IsNullOrWhiteSpace(normalizedRouteKey))
            return string.Empty;

        if (isControl)
            return normalizedRouteKey;

        var normalizedVariant = NormalizeIdentifier(variant) ?? variant;
        return string.IsNullOrWhiteSpace(normalizedVariant)
            ? normalizedRouteKey
            : $"{normalizedRouteKey}_{normalizedVariant}";
    }

    private static string? BuildRouteKeyFromBasePath(string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
            return null;

        var segments = basePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(segment => NormalizeIdentifier(segment))
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToList();

        return segments.Count == 0 ? null : string.Join("_", segments!);
    }

    private static string? BuildQuoteTypeFromBasePath(string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
            return null;

        var segments = basePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
            return null;

        return NormalizeToken(segments[1]);
    }

    private static string NormalizeBasePath(string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
            return string.Empty;

        var trimmed = basePath.Trim();
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    private static string? NormalizeBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        var trimmed = baseUrl.Trim().TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out _) ? trimmed : null;
    }

    private static string? NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value
            .Trim()
            .ToLowerInvariant()
            .Replace(' ', '_')
            .Replace('-', '_');
    }

    private static string? NormalizeToken(string? value) => NormalizeIdentifier(value);

    private static string? NormalizeVariantToken(string? value) => NormalizeIdentifier(value);

    private static string? FirstNonEmpty(string? first, string? second)
    {
        if (!string.IsNullOrWhiteSpace(first))
            return first.Trim();
        if (!string.IsNullOrWhiteSpace(second))
            return second.Trim();
        return null;
    }

    private static string ResolveRouteDisplayName(string quoteType)
    {
        return NormalizeToken(quoteType) switch
        {
            "life" => "Life Insurance Landing",
            "mortgage_protection" => "Mortgage Protection Landing",
            "term_life" => "Term Life Landing",
            "whole_life" => "Whole Life Landing",
            "final_expense" => "Final Expense Landing",
            "iul" => "Indexed Universal Life (IUL) Landing",
            _ => $"{HumanizeToken(quoteType)} Landing"
        };
    }

    private static string ResolveVariantDisplayName(string variant, bool isControl)
    {
        if (isControl)
            return "Control";

        return HumanizeToken(variant);
    }

    private static string HumanizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Join(" ",
            value
                .Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Length == 0
                    ? string.Empty
                    : part.Length == 1
                        ? part.ToUpperInvariant()
                        : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static List<LandingRouteRegistryItem> BuildFallbackRoutes()
    {
        return new List<LandingRouteRegistryItem>
        {
            new()
            {
                Key = "quote_life_landing",
                DisplayName = "Life Insurance Landing",
                BasePath = "/Quote/Life/landing",
                QuoteType = "life",
                PageMode = "paid_landing",
                DefaultPageVariant = "landing",
                IsActive = true,
                Notes = "Paid Meta life landing with control and the current emotional continuity copy test.",
                Variants = new List<LandingVariantRegistryItem>
                {
                    new()
                    {
                        Variant = "landing",
                        DisplayName = "Control",
                        EffectivePageKey = "quote_life_landing",
                        Description = "Current paid Meta control landing.",
                        IsControl = true,
                        IsActive = true
                    },
                    new()
                    {
                        Variant = "emotional_continuity_v1",
                        DisplayName = "Emotional Continuity V1",
                        EffectivePageKey = "quote_life_landing_emotional_continuity_v1",
                        Description = "Emotional consequence continuity copy with a softer trust block.",
                        IsControl = false,
                        IsActive = true
                    }
                }
            },
            new()
            {
                Key = "quote_mortgage_protection_landing",
                DisplayName = "Mortgage Protection Landing",
                BasePath = "/Quote/Mortgage-Protection/landing",
                QuoteType = "mortgage_protection",
                PageMode = "paid_landing",
                DefaultPageVariant = "landing",
                IsActive = true,
                Notes = "Default paid landing route for mortgage protection.",
                Variants = new List<LandingVariantRegistryItem>
                {
                    new()
                    {
                        Variant = "landing",
                        DisplayName = "Control",
                        EffectivePageKey = "quote_mortgage_protection_landing",
                        Description = "Current mortgage protection paid landing.",
                        IsControl = true,
                        IsActive = true
                    }
                }
            },
            new()
            {
                Key = "quote_term_life_landing",
                DisplayName = "Term Life Landing",
                BasePath = "/Quote/Term-Life/landing",
                QuoteType = "term_life",
                PageMode = "paid_landing",
                DefaultPageVariant = "landing",
                IsActive = true,
                Notes = "Default paid landing route for term life.",
                Variants = new List<LandingVariantRegistryItem>
                {
                    new()
                    {
                        Variant = "landing",
                        DisplayName = "Control",
                        EffectivePageKey = "quote_term_life_landing",
                        Description = "Current term life paid landing.",
                        IsControl = true,
                        IsActive = true
                    }
                }
            },
            new()
            {
                Key = "quote_whole_life_landing",
                DisplayName = "Whole Life Landing",
                BasePath = "/Quote/Whole-Life/landing",
                QuoteType = "whole_life",
                PageMode = "paid_landing",
                DefaultPageVariant = "landing",
                IsActive = true,
                Notes = "Default paid landing route for whole life.",
                Variants = new List<LandingVariantRegistryItem>
                {
                    new()
                    {
                        Variant = "landing",
                        DisplayName = "Control",
                        EffectivePageKey = "quote_whole_life_landing",
                        Description = "Current whole life paid landing.",
                        IsControl = true,
                        IsActive = true
                    }
                }
            },
            new()
            {
                Key = "quote_final_expense_landing",
                DisplayName = "Final Expense Landing",
                BasePath = "/Quote/Final-Expense/landing",
                QuoteType = "final_expense",
                PageMode = "paid_landing",
                DefaultPageVariant = "landing",
                IsActive = true,
                Notes = "Default paid landing route for final expense.",
                Variants = new List<LandingVariantRegistryItem>
                {
                    new()
                    {
                        Variant = "landing",
                        DisplayName = "Control",
                        EffectivePageKey = "quote_final_expense_landing",
                        Description = "Current final expense paid landing.",
                        IsControl = true,
                        IsActive = true
                    }
                }
            },
            new()
            {
                Key = "quote_iul_landing",
                DisplayName = "Indexed Universal Life (IUL) Landing",
                BasePath = "/Quote/IUL/landing",
                QuoteType = "iul",
                PageMode = "paid_landing",
                DefaultPageVariant = "landing",
                IsActive = true,
                Notes = "Default paid landing route for IUL.",
                Variants = new List<LandingVariantRegistryItem>
                {
                    new()
                    {
                        Variant = "landing",
                        DisplayName = "Control",
                        EffectivePageKey = "quote_iul_landing",
                        Description = "Current IUL paid landing.",
                        IsControl = true,
                        IsActive = true
                    }
                }
            }
        };
    }
}
