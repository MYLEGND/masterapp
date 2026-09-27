using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public interface IOpenAiProductFeedService
{
    Task<OpenAiProductFeedSnapshot> GetAsync(MarketingOwnerScope owner, CancellationToken ct = default);
    Task<OpenAiProductFeedPublishReceipt> PublishAsync(MarketingOwnerScope owner, CancellationToken ct = default);
}

public sealed class OpenAiProductFeedService(
    MasterAppDbContext db,
    IConfiguration configuration,
    IBusinessPublicUrlResolver publicUrls,
    IOpenAiAdsExecutionService ads,
    IOpenAiAdsAccountConnectionAuthority connections) : IOpenAiProductFeedService
{
    private const string Provider = "openai";

    public async Task<OpenAiProductFeedSnapshot> GetAsync(MarketingOwnerScope owner, CancellationToken ct = default)
    {
        var businessId = RequireBusiness(owner);
        var products = await LoadProductsAsync(businessId, ct);
        var projections = await db.OpenAiProductFeedProjections.AsNoTracking()
            .Where(x => x.CommerceBusinessId == businessId && x.Provider == Provider)
            .ToListAsync(ct);

        var errors = new List<string>();
        var mapped = new List<OpenAiProductFeedProjectionRow>();
        var currency = (configuration["Commerce:CurrencyCode"] ?? "USD").Trim().ToUpperInvariant();
        if (currency.Length != 3) currency = "USD";
        string? root = null;
        try { root = await publicUrls.ResolveAsync(businessId, ct); }
        catch (InvalidOperationException ex) { errors.Add(ex.Message); }

        foreach (var product in products)
        {
            var eligibility = root is null ? null : ToItem(product, root, currency, errors);
            var row = projections.FirstOrDefault(x => x.CommerceProductId == product.Id);
            mapped.Add(new(
                product.Id,
                product.Name,
                row?.ProviderFeedId,
                row?.ProviderProductId,
                row?.Status ?? (eligibility is null ? OpenAiProductFeedStatuses.Error : OpenAiProductFeedStatuses.Ready),
                row?.LastError,
                row?.LastPublishedUtc,
                row?.CanonicalFingerprint ?? eligibility?.CanonicalFingerprint ?? string.Empty));
        }

        return new(
            owner,
            projections.Select(x => x.ProviderFeedId).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
            products.Count,
            root is null ? 0 : products.Count(p => ToItem(p, root, currency, null) is not null),
            projections.Count(x => x.Status == OpenAiProductFeedStatuses.Published),
            projections.Count(x => x.Status is OpenAiProductFeedStatuses.Error or OpenAiProductFeedStatuses.Rejected),
            mapped,
            errors.Distinct(StringComparer.Ordinal).ToList());
    }

    public async Task<OpenAiProductFeedPublishReceipt> PublishAsync(MarketingOwnerScope owner, CancellationToken ct = default)
    {
        var businessId = RequireBusiness(owner);
        var connection = await connections.GetAsync(owner, ct);
        if (!connection.Connected || !connection.HasManagementCredential)
            throw new InvalidOperationException("Connect the scoped ChatGPT Ads account before publishing the product feed.");

        var root = await publicUrls.ResolveAsync(businessId, ct);
        var business = await db.CommerceBusinesses.AsNoTracking()
            .SingleAsync(x => x.Id == businessId && x.IsActive && x.Status == "Active", ct);
        var products = await LoadProductsAsync(businessId, ct);
        var currency = (configuration["Commerce:CurrencyCode"] ?? "USD").Trim().ToUpperInvariant();
        if (currency.Length != 3) currency = "USD";

        var eligible = products.Select(p => (Product: p, Item: ToItem(p, root, currency, null)))
            .Where(x => x.Item is not null)
            .Select(x => (x.Product, Item: x.Item!))
            .ToList();

        if (eligible.Count == 0)
            throw new InvalidOperationException("No active canonical products are eligible for ChatGPT Ads publication.");

        var existing = await db.OpenAiProductFeedProjections
            .Where(x => x.CommerceBusinessId == businessId && x.Provider == Provider)
            .ToListAsync(ct);
        var feedId = existing.Select(x => x.ProviderFeedId).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

        if (string.IsNullOrWhiteSpace(feedId))
        {
            var created = await ads.CreateProductFeedAsync(
                owner,
                new OpenAiAdsProductFeedCreateRequest(
                    $"{business.DisplayName} · LEGEND Catalog",
                    currency,
                    $"legend-feed-{businessId:N}"),
                ct);
            feedId = ReadId(created.Payload, "id", "product_feed_id")
                ?? throw new InvalidOperationException("OpenAI Ads did not return a product feed identifier.");
        }

        var published = 0;
        var failed = 0;
        foreach (var (product, item) in eligible)
        {
            var projection = existing.FirstOrDefault(x => x.CommerceProductId == product.Id);
            if (projection is null)
            {
                projection = new OpenAiProductFeedProjection
                {
                    CommerceBusinessId = businessId,
                    CommerceProductId = product.Id,
                    Provider = Provider
                };
                db.OpenAiProductFeedProjections.Add(projection);
                existing.Add(projection);
            }

            projection.ProviderFeedId = feedId;
            projection.CanonicalFingerprint = item.CanonicalFingerprint;
            projection.UpdatedUtc = DateTime.UtcNow;
            projection.Revision = Guid.NewGuid();

            try
            {
                var receipt = await ads.UpsertProductFeedItemAsync(owner, new OpenAiAdsProductFeedItemUpsertRequest(
                    feedId,
                    item.CanonicalProductId.ToString("D"),
                    item.Title,
                    item.Description,
                    item.PriceMicros,
                    item.Currency,
                    item.LandingUrl,
                    item.ImageUrl,
                    item.Available ? "in_stock" : "out_of_stock",
                    $"legend-product-{item.CanonicalProductId:N}-{item.CanonicalFingerprint[..16]}"), ct);

                projection.ProviderProductId = ReadId(receipt.Payload, "id", "product_id") ?? projection.ProviderProductId;
                projection.Status = ReadString(receipt.Payload, "status") is { } status &&
                    string.Equals(status, "rejected", StringComparison.OrdinalIgnoreCase)
                        ? OpenAiProductFeedStatuses.Rejected
                        : OpenAiProductFeedStatuses.Published;
                projection.LastError = projection.Status == OpenAiProductFeedStatuses.Rejected
                    ? ReadString(receipt.Payload, "error") ?? "Provider rejected this product."
                    : null;
                projection.LastPublishedUtc = DateTime.UtcNow;
                if (projection.Status == OpenAiProductFeedStatuses.Published) published++; else failed++;
            }
            catch (OpenAiAdsExecutionException ex)
            {
                projection.Status = OpenAiProductFeedStatuses.Error;
                projection.LastError = Clamp(ex.Message, 2000);
                failed++;
            }

            await db.SaveChangesAsync(ct);
        }

        foreach (var inactive in products.Where(x => !x.IsActive))
        {
            var row = existing.FirstOrDefault(x => x.CommerceProductId == inactive.Id);
            if (row is not null)
            {
                row.Status = OpenAiProductFeedStatuses.Inactive;
                row.UpdatedUtc = DateTime.UtcNow;
                row.Revision = Guid.NewGuid();
            }
        }
        await db.SaveChangesAsync(ct);

        var snapshot = await GetAsync(owner, ct);
        return new(feedId, eligible.Count, published, failed, snapshot.Products);
    }

    private async Task<List<CommerceProduct>> LoadProductsAsync(Guid businessId, CancellationToken ct) =>
        await db.CommerceProducts.AsNoTracking()
            .Include(x => x.Images)
            .Where(x => x.CommerceBusinessId == businessId)
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.Name)
            .ToListAsync(ct);

    private static OpenAiProductFeedItem? ToItem(CommerceProduct product, string root, string currency, ICollection<string>? errors)
    {
        if (!product.IsActive) return null;
        if (string.IsNullOrWhiteSpace(product.Name))
        {
            errors?.Add($"Product {product.Id:D} has no name.");
            return null;
        }
        if (product.PriceCents <= 0)
        {
            errors?.Add($"{product.Name} does not have a positive canonical price.");
            return null;
        }
        var image = product.Images.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.DisplayOrder)
            .Select(x => x.ImageUrl?.Trim())
            .FirstOrDefault(x => Uri.TryCreate(x, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp));
        if (string.IsNullOrWhiteSpace(image))
        {
            errors?.Add($"{product.Name} does not have an eligible HTTP(S) product image.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(product.Slug))
        {
            errors?.Add($"{product.Name} does not have a canonical storefront slug.");
            return null;
        }

        var description = string.IsNullOrWhiteSpace(product.Description) ? product.Name.Trim() : product.Description.Trim();
        if (description.Length > 4000) description = description[..4000];
        var landing = $"{root}/store/product/{Uri.EscapeDataString(product.Slug.Trim())}";
        var fingerprintSource = string.Join("\n", product.Id, product.UpdatedUtc.Ticks, product.Name, description,
            product.PriceCents, landing, image, product.IsActive);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource))).ToLowerInvariant();

        return new(product.Id, product.ExternalProductKey, product.Name.Trim(), description,
            checked((long)product.PriceCents * 10_000L), currency, landing, image!, true, fingerprint);
    }

    private static Guid RequireBusiness(MarketingOwnerScope owner) =>
        owner.CommerceBusinessId is { } id && id != Guid.Empty
            ? id
            : throw new InvalidOperationException("ChatGPT Ads product feeds are published only from a scoped business catalog.");

    private static string? ReadId(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!.Trim();
        return null;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static string Clamp(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
