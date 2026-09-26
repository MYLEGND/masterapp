using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[Route("store")]
public sealed class StoreController : Controller
{
    private readonly ParfaitProductService _products;
    private readonly CommerceStoreContextService _stores;

    public StoreController(
        ParfaitProductService products,
        CommerceStoreContextService stores)
    {
        _products = products;
        _stores = stores;
    }

    [HttpGet("")]
    public Task<IActionResult> Index(CancellationToken ct) => RenderIndexAsync(null, ct);

    [HttpGet("s/{businessKey}")]
    public Task<IActionResult> ScopedIndex(string businessKey, CancellationToken ct) =>
        RenderIndexAsync(businessKey, ct);

    [HttpGet("cart")]
    public Task<IActionResult> Cart(CancellationToken ct) => RenderCartAsync(null, ct);

    [HttpGet("s/{businessKey}/cart")]
    public Task<IActionResult> ScopedCart(string businessKey, CancellationToken ct) =>
        RenderCartAsync(businessKey, ct);

    [HttpGet("product/{slug}")]
    public Task<IActionResult> Product(string slug, CancellationToken ct) =>
        RenderProductAsync(null, slug, ct);

    [HttpGet("s/{businessKey}/product/{slug}")]
    public Task<IActionResult> ScopedProduct(string businessKey, string slug, CancellationToken ct) =>
        RenderProductAsync(businessKey, slug, ct);

    [HttpGet("terms")]
    public Task<IActionResult> Terms(CancellationToken ct) => RenderLegalAsync(null, "Terms", ct);

    [HttpGet("s/{businessKey}/terms")]
    public Task<IActionResult> ScopedTerms(string businessKey, CancellationToken ct) =>
        RenderLegalAsync(businessKey, "Terms", ct);

    [HttpGet("privacy")]
    public Task<IActionResult> Privacy(CancellationToken ct) => RenderLegalAsync(null, "Privacy", ct);

    [HttpGet("s/{businessKey}/privacy")]
    public Task<IActionResult> ScopedPrivacy(string businessKey, CancellationToken ct) =>
        RenderLegalAsync(businessKey, "Privacy", ct);

    private async Task<IActionResult> RenderIndexAsync(string? businessKey, CancellationToken ct)
    {
        var store = await _stores.ResolvePublicAsync(HttpContext, businessKey, ct);
        if (store is null) return NotFound();

        var redirect = await CanonicalizeScopedRequestAsync(store, businessKey, "", ct);
        if (redirect is not null) return redirect;

        ApplyStoreContext(store);
        var model = new ParfaitStorefrontViewModel
        {
            StoreName = store.StoreName,
            Headline = store.Headline,
            Subheadline = store.Subheadline,
            Products = _products.GetActiveStoreProducts(store.CommerceBusinessId)
        };

        return View("~/Views/Store/Index.cshtml", model);
    }

    private async Task<IActionResult> RenderCartAsync(string? businessKey, CancellationToken ct)
    {
        var store = await _stores.ResolvePublicAsync(HttpContext, businessKey, ct);
        if (store is null) return NotFound();

        var redirect = await CanonicalizeScopedRequestAsync(store, businessKey, "/cart", ct);
        if (redirect is not null) return redirect;

        ApplyStoreContext(store);
        ViewBag.GlobalStoreCheckoutUrl = store.GlobalCheckoutUrl;
        return View("~/Views/Store/Cart.cshtml");
    }

    private async Task<IActionResult> RenderProductAsync(string? businessKey, string slug, CancellationToken ct)
    {
        var store = await _stores.ResolvePublicAsync(HttpContext, businessKey, ct);
        if (store is null) return NotFound();

        var redirect = await CanonicalizeScopedRequestAsync(
            store,
            businessKey,
            "/product/" + Uri.EscapeDataString(slug),
            ct);
        if (redirect is not null) return redirect;

        var product = _products.GetActiveStoreProductBySlug(store.CommerceBusinessId, slug);
        if (product is null) return NotFound();

        ApplyStoreContext(store);
        return View("~/Views/Store/Product.cshtml", product);
    }

    private async Task<IActionResult> RenderLegalAsync(
        string? businessKey,
        string viewName,
        CancellationToken ct)
    {
        var store = await _stores.ResolvePublicAsync(HttpContext, businessKey, ct);
        if (store is null) return NotFound();

        var suffix = "/" + viewName.ToLowerInvariant();
        var redirect = await CanonicalizeScopedRequestAsync(store, businessKey, suffix, ct);
        if (redirect is not null) return redirect;

        ApplyStoreContext(store);
        return View("~/Views/Home/" + viewName + ".cshtml");
    }

    private async Task<IActionResult?> CanonicalizeScopedRequestAsync(
        CommerceStoreContext store,
        string? businessKey,
        string suffix,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(businessKey) ||
            store.IsParfait ||
            !_stores.IsCentralCommerceHost(HttpContext))
            return null;

        var canonicalRoot = await _stores.ResolveCanonicalPublicRootAsync(store, ct);
        return string.IsNullOrWhiteSpace(canonicalRoot)
            ? null
            : RedirectPermanent(canonicalRoot + suffix);
    }

    private void ApplyStoreContext(CommerceStoreContext store)
    {
        ViewData["CommerceStoreContext"] = store;
        ViewData["StoreRootPath"] = store.StoreRootPath;
        ViewData["StoreCartPath"] = store.CartPath;
        ViewData["StoreCheckoutPath"] = store.CheckoutPath;
        ViewData["StoreSuccessPath"] = store.SuccessPath;
    }
}
