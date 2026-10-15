using Infrastructure.Commerce;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Services;

using Shared.Analytics;
namespace ParfaitApp.Controllers;

[Route("store")]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("public-ingest")]
public sealed class StoreCartController(
    CommerceStoreContextService stores,
    ParfaitProductService products,
    CommerceSignalService commerceSignals) : Controller
{
    private static readonly SemaphoreSlim[] CartLocks = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    [HttpPost("cart/items")]
    [IgnoreAntiforgeryToken]
    public Task<IActionResult> AddItem([FromBody] CartAddItemRequest request, CancellationToken ct) =>
        AddItemCoreAsync(null, request, ct);

    [HttpPost("s/{businessKey}/cart/items")]
    [IgnoreAntiforgeryToken]
    public Task<IActionResult> AddScopedItem(
        string businessKey,
        [FromBody] CartAddItemRequest request,
        CancellationToken ct) =>
        AddItemCoreAsync(businessKey, request, ct);

    private async Task<IActionResult> AddItemCoreAsync(
        string? businessKey,
        CartAddItemRequest request,
        CancellationToken ct)
    {
        var store = await stores.ResolvePublicAsync(HttpContext, businessKey, ct);
        if (store is null) return NotFound();

        if (!Guid.TryParse(request.EventId, out var commandId)) return BadRequest(new { error = "cart_command_id_required" });
        request.EventId = commandId.ToString("D");
        return await RecordValidatedAddToCartAsync(store, request, ct);
    }

    private async Task<IActionResult> RecordValidatedAddToCartAsync(
        CommerceStoreContext store,
        CartAddItemRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProductId)) return BadRequest();

        var product = products.GetActiveStoreProductById(store.CommerceBusinessId, request.ProductId);
        if (product is null || product.PriceCents <= 0) return BadRequest();

        var requestedSize = (request.Size ?? string.Empty).Trim();
        var size = string.IsNullOrWhiteSpace(requestedSize)
            ? product.DefaultSize
            : product.Sizes.FirstOrDefault(x => string.Equals(x.Size, requestedSize, StringComparison.OrdinalIgnoreCase));
        if (size is null || !size.CanPurchase) return BadRequest();

        var quantity = Math.Clamp(request.Quantity ?? 1, 1, Math.Max(1, size.StockQuantity));
        var unitPrice = product.DisplayPriceCents > 0 ? product.DisplayPriceCents : product.PriceCents;
        var stableIdentity = request.EventId!;

        var cartLock = CartLocks[(uint)StringComparer.Ordinal.GetHashCode(HttpContext.Session.Id) % (uint)CartLocks.Length];
        await cartLock.WaitAsync(ct);
        try {
        await HttpContext.Session.LoadAsync(ct);
        var receiptKey = $"cart-command:{store.CommerceBusinessId:N}:{request.EventId}";
        var fingerprint = $"{product.Id}:{size.Size}:{quantity}";
        var existingReceipt = HttpContext.Session.GetString(receiptKey);
        var receipt = existingReceipt is null ? null : System.Text.Json.JsonSerializer.Deserialize<CartReceipt>(existingReceipt);
        if (receipt is not null && receipt.Fingerprint != fingerprint) return Conflict();
        var cartKey = $"cart:{store.CommerceBusinessId:N}";
        var cart = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(HttpContext.Session.GetString(cartKey) ?? "{}")!;
        var itemKey = $"{product.Id}:{size.Size}";
        if (receipt is null) {
            var previousQuantity = cart.GetValueOrDefault(itemKey);
            var acceptedQuantity = Math.Max(0, Math.Min(size.StockQuantity - previousQuantity, quantity));
            receipt = new CartReceipt(fingerprint, acceptedQuantity, DateTime.UtcNow, unitPrice);
            cart[itemKey] = previousQuantity + acceptedQuantity;
            HttpContext.Session.SetString(cartKey, System.Text.Json.JsonSerializer.Serialize(cart));
            HttpContext.Session.SetString(receiptKey, System.Text.Json.JsonSerializer.Serialize(receipt));
            await HttpContext.Session.CommitAsync(ct);
        }
        if (receipt.AcceptedQuantity == 0)
            return Ok(new { success = true, eventId = request.EventId, quantity = cart[itemKey], priceCents = receipt.UnitPrice });

        string? Cookie(string name) => Request.Cookies.TryGetValue(name, out var value) ? value : null;
        var sourceUrl = $"https://{stores.ResolveEffectivePublicHost(HttpContext)}{store.StoreRootPath}";

        await commerceSignals.RecordAsync(
            "AddToCart",
            stableIdentity,
            CommerceSignalAttribution.Apply(new CommerceSignalContext(
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
                OpenAiClickReference.Normalize(Cookie("pf_oppref") ?? Request.Query["oppref"].FirstOrDefault()),
                Cookie("_fbc"),
                Cookie("_fbp"), EventUtc: receipt.EventUtc), Request),
            new CommerceSignalProduct(
                product.Id,
                product.Name,
                product.Slug,
                size.Size,
                receipt.AcceptedQuantity,
                receipt.UnitPrice * receipt.AcceptedQuantity),
            ct: ct);
        return Ok(new { success = true, eventId = request.EventId, quantity = cart[itemKey], priceCents = receipt.UnitPrice });
        } finally { cartLock.Release(); }
    }
    private sealed record CartReceipt(string Fingerprint, int AcceptedQuantity, DateTime EventUtc, int UnitPrice);
    public sealed class CartAddItemRequest
    {
        public string? EventId { get; set; }
        public string? ProductId { get; set; }
        public string? Size { get; set; }
        public int? Quantity { get; set; }
        public string? SessionId { get; set; }
        public string? VisitorId { get; set; }
        public string? Referrer { get; set; }
    }

}
