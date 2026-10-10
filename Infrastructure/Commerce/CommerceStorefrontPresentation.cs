using ParfaitApp.Services;

namespace Infrastructure.Commerce;

/// <summary>
/// Infrastructure-owned storefront presentation selector. Parfait retains its
/// exact first-party layout until a separately approved visual-parity cutover.
/// This selector never changes tenant, payment, store, or branding data.
/// </summary>
public static class CommerceStorefrontPresentation
{
    public const string ParfaitOriginalLayout = "_Layout";
    public const string PublishedBusinessLayout = "_ScopedWebsiteStoreLayout";

    public static string ResolveLayout(CommerceStoreContext? context)
    {
        if (context is null || context.IsParfait)
            return ParfaitOriginalLayout;

        // Existing draft-preview fallback is preserved. A later stage must
        // supply a real scoped preview shell before disabling this fallback.
        return !string.IsNullOrWhiteSpace(context.WebsiteShellPrefix) &&
               !string.IsNullOrWhiteSpace(context.WebsiteShellSuffix)
            ? PublishedBusinessLayout
            : ParfaitOriginalLayout;
    }
}
