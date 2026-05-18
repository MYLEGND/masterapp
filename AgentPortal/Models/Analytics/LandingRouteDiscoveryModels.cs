using System.Collections.Generic;

namespace AgentPortal.Models.Analytics;

public sealed class LandingRouteDefinition
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string BasePath { get; set; } = "";
    public string QuoteType { get; set; } = "";
    public string PageMode { get; set; } = "paid_landing";
    public string DefaultPageVariant { get; set; } = "landing";
    public List<LandingVariantDefinition> AvailableVariants { get; set; } = new();
    public List<string> EffectivePageKeys { get; set; } = new();
    public bool IsPaidLanding { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public string ControlUrl { get; set; } = "";
    public string? ComparisonHelperText { get; set; }
}

public sealed class LandingVariantDefinition
{
    public string Variant { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Url { get; set; } = "";
    public string EffectivePageKey { get; set; } = "";
    public string? Description { get; set; }
    public bool IsControl { get; set; }
    public bool IsActive { get; set; } = true;
    public string SuggestedMetaDestinationUrl { get; set; } = "";
}

public sealed class LandingRoutesOptions
{
    public string? BaseUrl { get; set; }
    public List<LandingRouteRegistryItem> Routes { get; set; } = new();
}

public sealed class LandingRouteRegistryItem
{
    public string? Key { get; set; }
    public string? DisplayName { get; set; }
    public string? BasePath { get; set; }
    public string? QuoteType { get; set; }
    public string? PageMode { get; set; }
    public string? DefaultPageVariant { get; set; }
    public List<LandingVariantRegistryItem> Variants { get; set; } = new();
    public bool? IsActive { get; set; }
    public string? Notes { get; set; }
}

public sealed class LandingVariantRegistryItem
{
    public string? Variant { get; set; }
    public string? DisplayName { get; set; }
    public string? EffectivePageKey { get; set; }
    public string? Description { get; set; }
    public bool? IsControl { get; set; }
    public bool? IsActive { get; set; }
}
