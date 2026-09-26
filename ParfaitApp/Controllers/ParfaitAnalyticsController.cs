using Infrastructure.Commerce;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[Route("parfait-analytics")]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("public-ingest")]
public sealed class ParfaitAnalyticsController(
    ParfaitAnalyticsService analytics,
    CommerceStoreContextService stores,
    ParfaitProductService products,
    CommerceSignalService commerceSignals) : Controller
{
    [HttpPost("track")]
    [IgnoreAntiforgeryToken]
    public Task<IActionResult> Track([FromBody] ParfaitAnalyticsEventRequest request, CancellationToken ct) =>
        TrackCoreAsync(null, request, ct);

    [HttpPost("s/{businessKey}/track")]
    [IgnoreAntiforgeryToken]
    public Task<IActionResult> TrackScoped(
        string businessKey,
        [FromBody] ParfaitAnalyticsEventRequest request,
        CancellationToken ct) =>
        TrackCoreAsync(businessKey, request, ct);

    private async Task<IActionResult> TrackCoreAsync(
        string? businessKey,
        ParfaitAnalyticsEventRequest request,
        CancellationToken ct)
    {
        var store = await stores.ResolvePublicAsync(businessKey, ct);
        if (store is null) return NotFound();

        await analytics.TrackScopedAsync(
            store.CommerceBusinessId,
            store.AgentTrackingProfileId,
            store.WebsiteContentVersionId,
            store.WebsiteSiteKey,
            store.BusinessKey,
            request,
            HttpContext,
            ct);

        if (string.Equals(request.EventName, "AddToCart", StringComparison.OrdinalIgnoreCase))
            await RecordValidatedAddToCartAsync(store, request, ct);

        return Ok(new { success = true });
    }

    private async Task RecordValidatedAddToCartAsync(
        CommerceStoreContext store,
        ParfaitAnalyticsEventRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProductId)) return;

        var product = products.GetActiveStoreProductById(store.CommerceBusinessId, request.ProductId);
        if (product is null || product.PriceCents <= 0) return;

        var requestedSize = (request.Size ?? string.Empty).Trim();
        var size = string.IsNullOrWhiteSpace(requestedSize)
            ? product.DefaultSize
            : product.Sizes.FirstOrDefault(x => string.Equals(x.Size, requestedSize, StringComparison.OrdinalIgnoreCase));
        if (size is null || !size.CanPurchase) return;

        var quantity = Math.Clamp(request.Quantity ?? 1, 1, Math.Max(1, size.StockQuantity));
        var unitPrice = product.DisplayPriceCents > 0 ? product.DisplayPriceCents : product.PriceCents;
        var stableIdentity = !string.IsNullOrWhiteSpace(request.EventId)
            ? request.EventId!
            : $"{request.SessionId}:{product.Id}:{size.Size}:{quantity}";

        string? Cookie(string name) => Request.Cookies.TryGetValue(name, out var value) ? value : null;
        var sourceUrl = string.IsNullOrWhiteSpace(request.Url)
            ? $"{Request.Scheme}://{Request.Host}{Request.Path}{Request.QueryString}"
            : request.Url!;

        await commerceSignals.RecordAsync(
            "AddToCart",
            stableIdentity,
            new CommerceSignalContext(
                store.CommerceBusinessId,
                store.AgentTrackingProfileId,
                store.WebsiteContentVersionId,
                store.WebsiteSiteKey,
                store.BusinessKey,
                store.StoreName,
                sourceUrl,
                request.SessionId ?? Cookie("pf_sid"),
                request.VisitorId ?? Cookie("pf_vid"),
                request.Referrer ?? Request.Headers.Referer.ToString(),
                Request.Headers.UserAgent.ToString(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Query["fbclid"].FirstOrDefault(),
                Cookie("_fbc"),
                Cookie("_fbp")),
            new CommerceSignalProduct(
                product.Id,
                product.Name,
                product.Slug,
                size.Size,
                quantity,
                unitPrice * quantity),
            ct: ct);
    }
}
