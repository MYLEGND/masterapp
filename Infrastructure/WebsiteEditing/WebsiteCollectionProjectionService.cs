using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.WebsiteEditing;

public sealed record WebsiteCollectionSourceDefinition(
    string Key,
    string Label,
    bool IsList,
    IReadOnlyList<string> Fields);

public static class WebsiteCollectionSourcePolicy
{
    private static readonly IReadOnlyDictionary<string, WebsiteCollectionSourceDefinition> Definitions =
        new Dictionary<string, WebsiteCollectionSourceDefinition>(StringComparer.Ordinal)
        {
            ["business_facts"] = new(
                "business_facts",
                "Business details",
                false,
                ["contactEmail", "phone", "hours", "locations", "services"]),
            ["commerce_products"] = new(
                "commerce_products",
                "Products",
                true,
                ["id", "name", "slug", "description", "priceLabel", "priceCents",
                 "compareAtPriceCents", "badge", "isFeatured", "primaryImageUrl", "primaryImageAlt"])
        };

    public static IReadOnlyList<WebsiteCollectionSourceDefinition> Catalog => Definitions.Values.ToArray();

    public static bool TryGet(string? source, out WebsiteCollectionSourceDefinition definition) =>
        Definitions.TryGetValue((source ?? string.Empty).Trim().ToLowerInvariant(), out definition!);
}

public sealed record WebsiteCollectionItem(
    string Key,
    IReadOnlyDictionary<string, object?> Fields);

public sealed record WebsiteCollectionProjection(
    string Id,
    string Source,
    bool IsList,
    IReadOnlyList<WebsiteCollectionItem> Items);

/// <summary>
/// Read-only public projection over existing business-owned entities. This service
/// never creates website-side business records and never returns a row from another business.
/// </summary>
public sealed class WebsiteCollectionProjectionService(MasterAppDbContext db)
{
    public async Task<IReadOnlyDictionary<string, WebsiteCollectionProjection>> LoadAsync(
        WebsiteContentDocument document,
        Guid commerceBusinessId,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, WebsiteCollectionProjection>(StringComparer.Ordinal);
        var definitions = (document.Collections ?? new Dictionary<string, WebsiteCollectionDefinition>())
            .Where(pair => pair.Value is not null && WebsiteCollectionSourcePolicy.TryGet(pair.Value.Source, out _))
            .ToArray();

        WebsiteBusinessFacts? facts = null;
        List<CommerceProduct>? products = null;

        foreach (var (id, collection) in definitions)
        {
            if (!WebsiteCollectionSourcePolicy.TryGet(collection.Source, out var source))
                continue;
            var allowedFields = collection.Fields.Where(source.Fields.Contains)
                .Distinct(StringComparer.Ordinal).ToArray();

            if (source.Key == "business_facts")
            {
                facts ??= await WebsiteBusinessFacts.LoadAsync(db, commerceBusinessId, cancellationToken);
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var field in allowedFields)
                    fields[field] = field switch
                    {
                        "contactEmail" => facts.ContactEmail,
                        "phone" => facts.Phone,
                        "hours" => facts.Hours,
                        "locations" => facts.Locations,
                        "services" => facts.Services,
                        _ => null
                    };
                result[id] = new WebsiteCollectionProjection(
                    id, source.Key, false, [new WebsiteCollectionItem("business", fields)]);
                continue;
            }

            if (source.Key == "commerce_products")
            {
                products ??= await db.CommerceProducts.AsNoTracking()
                    .Where(product => product.CommerceBusinessId == commerceBusinessId && product.IsActive)
                    .Include(product => product.Images)
                    .OrderBy(product => product.DisplayOrder)
                    .ThenBy(product => product.Name)
                    .ThenBy(product => product.Id)
                    .Take(500)
                    .ToListAsync(cancellationToken);

                var items = new List<WebsiteCollectionItem>(products.Count);
                foreach (var product in products)
                {
                    var primary = product.Images
                        .OrderByDescending(image => image.IsPrimary)
                        .ThenBy(image => image.DisplayOrder)
                        .ThenBy(image => image.Id)
                        .FirstOrDefault();
                    var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var field in allowedFields)
                        values[field] = field switch
                        {
                            "id" => product.Id.ToString("D"),
                            "name" => product.Name,
                            "slug" => product.Slug,
                            "description" => product.Description,
                            "priceLabel" => product.PriceLabel,
                            "priceCents" => product.PriceCents,
                            "compareAtPriceCents" => product.CompareAtPriceCents,
                            "badge" => product.Badge,
                            "isFeatured" => product.IsFeatured,
                            "primaryImageUrl" => WebsiteContentSanitizer.SanitizeUrl(primary?.ImageUrl, true),
                            "primaryImageAlt" => primary?.AltText,
                            _ => null
                        };
                    items.Add(new WebsiteCollectionItem(
                        string.IsNullOrWhiteSpace(product.Slug) ? product.Id.ToString("N") : product.Slug,
                        values));
                }
                result[id] = new WebsiteCollectionProjection(id, source.Key, true, items);
            }
        }

        return result;
    }
}
