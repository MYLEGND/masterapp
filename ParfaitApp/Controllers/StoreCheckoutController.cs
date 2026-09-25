using Domain.Billing;
using Infrastructure.Billing.Square;
using Infrastructure.Commerce;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[Route("store")]
public sealed class StoreCheckoutController : Controller
{
    private const string CommerceCurrency = "USD";
    private readonly SquareBillingOptions _squareOptions;
    private readonly ParfaitProductService _products;
    private readonly ParfaitOrderService _orders;
    private readonly ParfaitCustomerAutomationService _automations;
    private readonly IBillingOrchestrator _billingOrchestrator;
    private readonly IGraphMailService _mail;
    private readonly IParfaitAnalyticsService _analytics;
    private readonly CommerceSignalService? _commerceSignals;
    private readonly CommerceStoreContextService? _stores;
    private readonly ParfaitMetaSignalBridgeService? _legacyMetaSignalBridge;
    private readonly bool _legacyCompatibility;

    public StoreCheckoutController(
        SquareBillingOptions squareOptions,
        ParfaitProductService products,
        ParfaitOrderService orders,
        ParfaitCustomerAutomationService automations,
        IBillingOrchestrator billingOrchestrator,
        IGraphMailService mail,
        IParfaitAnalyticsService analytics,
        CommerceSignalService commerceSignals,
        CommerceStoreContextService stores)
    {
        _squareOptions = squareOptions;
        _products = products;
        _orders = orders;
        _automations = automations;
        _billingOrchestrator = billingOrchestrator;
        _mail = mail;
        _analytics = analytics;
        _commerceSignals = commerceSignals;
        _stores = stores;
    }

    // Compatibility constructor for existing Parfait callers/tests. It preserves the
    // original Parfait route while the production DI constructor above owns scoped stores.
    public StoreCheckoutController(
        SquareBillingOptions squareOptions,
        ParfaitProductService products,
        ParfaitOrderService orders,
        ParfaitCustomerAutomationService automations,
        IBillingOrchestrator billingOrchestrator,
        IGraphMailService mail,
        IParfaitAnalyticsService analytics,
        ParfaitMetaSignalBridgeService metaSignalBridge)
    {
        _squareOptions = squareOptions;
        _products = products;
        _orders = orders;
        _automations = automations;
        _billingOrchestrator = billingOrchestrator;
        _mail = mail;
        _analytics = analytics;
        _legacyMetaSignalBridge = metaSignalBridge;
        _legacyCompatibility = true;
    }

    [NonAction]
    public IActionResult Checkout() => RenderCheckoutAsync(null, CancellationToken.None).GetAwaiter().GetResult();

    [HttpGet("checkout")]
    public Task<IActionResult> Checkout(CancellationToken ct = default) => RenderCheckoutAsync(null, ct);

    [HttpGet("s/{businessKey}/checkout")]
    public Task<IActionResult> ScopedCheckout(string businessKey, CancellationToken ct) =>
        RenderCheckoutAsync(businessKey, ct);

    [HttpPost("checkout/quote")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Quote([FromBody] ParfaitCartQuoteRequest request, CancellationToken ct) =>
        QuoteCoreAsync(null, request, ct);

    [HttpPost("s/{businessKey}/checkout/quote")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ScopedQuote(
        string businessKey,
        [FromBody] ParfaitCartQuoteRequest request,
        CancellationToken ct) =>
        QuoteCoreAsync(businessKey, request, ct);

    [HttpPost("checkout/lead")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> CaptureLead(
        [FromBody] ParfaitAutomationCheckoutLeadCaptureRequest request,
        CancellationToken ct) =>
        CaptureLeadCoreAsync(null, request, ct);

    [HttpPost("s/{businessKey}/checkout/lead")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ScopedCaptureLead(
        string businessKey,
        [FromBody] ParfaitAutomationCheckoutLeadCaptureRequest request,
        CancellationToken ct) =>
        CaptureLeadCoreAsync(businessKey, request, ct);

    [HttpPost("checkout/pay")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Pay([FromBody] ParfaitCheckoutPayRequest request, CancellationToken ct) =>
        PayCoreAsync(null, request, ct);

    [HttpPost("s/{businessKey}/checkout/pay")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ScopedPay(
        string businessKey,
        [FromBody] ParfaitCheckoutPayRequest request,
        CancellationToken ct) =>
        PayCoreAsync(businessKey, request, ct);

    [HttpGet("success")]
    public Task<IActionResult> Success(string orderNumber, CancellationToken ct) =>
        SuccessCoreAsync(null, orderNumber, ct);

    [HttpGet("s/{businessKey}/success")]
    public Task<IActionResult> ScopedSuccess(
        string businessKey,
        string orderNumber,
        CancellationToken ct) =>
        SuccessCoreAsync(businessKey, orderNumber, ct);

    private async Task<IActionResult> RenderCheckoutAsync(string? businessKey, CancellationToken ct)
    {
        var store = await ResolveStoreAsync(businessKey, ct);
        if (store is null) return NotFound();

        ApplyStoreContext(store);
        ViewBag.SquareApplicationId = _squareOptions.ApplicationId;
        ViewBag.SquareLocationId = _squareOptions.LocationId;
        ViewBag.SquareEnvironment = _squareOptions.Environment.ToString();
        return View("~/Views/Store/Checkout.cshtml");
    }

    private async Task<IActionResult> QuoteCoreAsync(
        string? businessKey,
        ParfaitCartQuoteRequest? request,
        CancellationToken ct)
    {
        var store = await ResolveStoreAsync(businessKey, ct);
        if (store is null) return NotFound();

        request ??= new ParfaitCartQuoteRequest();
        request.Items ??= [];
        return Ok(_products.QuoteCart(store.CommerceBusinessId, request.Items, request.DiscountCode));
    }

    private async Task<IActionResult> CaptureLeadCoreAsync(
        string? businessKey,
        ParfaitAutomationCheckoutLeadCaptureRequest? request,
        CancellationToken ct)
    {
        var store = await ResolveStoreAsync(businessKey, ct);
        if (store is null) return NotFound();

        request ??= new ParfaitAutomationCheckoutLeadCaptureRequest();
        request.Items ??= [];
        var quote = _products.QuoteCart(store.CommerceBusinessId, request.Items, request.DiscountCode);

        // Parfait retains its existing automation authority. Website-scoped stores
        // use the existing website/CRM authorities and do not receive Parfait-branded automation.
        if (store.IsParfait)
            _automations.CaptureCheckoutLead(request, quote);

        return NoContent();
    }

    private async Task<IActionResult> PayCoreAsync(
        string? businessKey,
        ParfaitCheckoutPayRequest? request,
        CancellationToken ct)
    {
        var store = await ResolveStoreAsync(businessKey, ct);
        if (store is null) return NotFound();

        request ??= new ParfaitCheckoutPayRequest();
        request.Customer ??= new ParfaitCheckoutCustomerRequest();
        request.Items ??= [];

        if (string.IsNullOrWhiteSpace(request.CheckoutAttemptId))
            return BadRequest(new ParfaitCheckoutPayResponse { Success = false, Error = "Checkout session expired. Refresh and try again." });

        var validationError = ValidateCustomer(request.Customer);
        if (!string.IsNullOrWhiteSpace(validationError))
            return BadRequest(new ParfaitCheckoutPayResponse { Success = false, Error = validationError });

        if (string.IsNullOrWhiteSpace(request.SourceId))
            return BadRequest(new ParfaitCheckoutPayResponse { Success = false, Error = "Missing payment token." });

        var quote = _products.QuoteCart(store.CommerceBusinessId, request.Items, request.DiscountCode);
        if (!quote.IsValid)
        {
            return BadRequest(new ParfaitCheckoutPayResponse
            {
                Success = false,
                Error = quote.Error ?? quote.Messages.FirstOrDefault() ?? "The cart needs attention before checkout."
            });
        }

        var validatedItems = BuildValidatedItems(quote);
        if (validatedItems.Count == 0)
            return BadRequest(new ParfaitCheckoutPayResponse { Success = false, Error = "No valid cart items were found." });

        var paymentStart = _orders.BeginCheckoutPayment(
            store.CommerceBusinessId,
            request.CheckoutAttemptId!,
            request.Customer,
            validatedItems,
            quote.SubtotalCents,
            quote.DiscountCode,
            quote.DiscountLabel,
            quote.DiscountCents,
            quote.ShippingCents,
            quote.TaxCents,
            HttpContext);

        if (paymentStart.State == CheckoutPaymentStartState.AlreadyPaid)
        {
            return Ok(new ParfaitCheckoutPayResponse
            {
                Success = true,
                OrderNumber = paymentStart.Order.OrderNumber,
                RedirectUrl = SuccessUrl(store, paymentStart.Order.OrderNumber)
            });
        }

        if (paymentStart.State == CheckoutPaymentStartState.AlreadyProcessing)
        {
            return Conflict(new ParfaitCheckoutPayResponse
            {
                Success = false,
                OrderNumber = paymentStart.Order.OrderNumber,
                Error = "Payment is already processing for this order. Please wait."
            });
        }

        var order = paymentStart.Order;
        var firstItem = validatedItems.FirstOrDefault();
        var signalContext = BuildSignalContext(store);
        if (_commerceSignals is not null)
        {
            await _commerceSignals.RecordAsync(
                "InitiateCheckout",
                request.CheckoutAttemptId!,
                signalContext,
                firstItem is null ? null : ProductSignal(firstItem),
                CustomerSignal(request.Customer),
                order.OrderNumber,
                validatedItems.Select(ProductSignal).ToArray(),
                ct);
        }

        var note = $"{store.StoreName} {order.OrderNumber}: " +
            string.Join(", ", order.Items.Select(i => $"{i.Name} / {i.Size} x{i.Quantity}"));

        ExecuteCommerceOneTimePaymentResult paymentResult;
        try
        {
            paymentResult = await _billingOrchestrator.ExecuteCommerceOneTimePaymentAsync(
                new ExecuteCommerceOneTimePaymentCommand(
                    request.SourceId!,
                    order.TotalCents,
                    CommerceCurrency,
                    note,
                    BuildPaymentIdempotencyKey(store, order),
                    order.CommerceOrderId,
                    BuildPaymentCorrelationId(store, order)),
                ct);
        }
        catch
        {
            _orders.MarkPaymentFailed(store.CommerceBusinessId, order.OrderNumber, "Payment request could not be completed right now.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ParfaitCheckoutPayResponse
            {
                Success = false,
                OrderNumber = order.OrderNumber,
                Error = "Payment could not be completed right now. Please try again."
            });
        }

        if (!paymentResult.Success)
        {
            var failureSummary = paymentResult.SanitizedSummary ?? "Payment could not be completed right now. Please try again.";
            var failureStatusCode = ResolvePaymentFailureStatusCode(paymentResult);
            _orders.MarkPaymentFailed(store.CommerceBusinessId, order.OrderNumber, failureSummary);
            return StatusCode(failureStatusCode, new ParfaitCheckoutPayResponse
            {
                Success = false,
                OrderNumber = order.OrderNumber,
                Error = failureStatusCode == StatusCodes.Status503ServiceUnavailable
                    ? "Payment could not be completed right now. Please try again."
                    : failureSummary
            });
        }

        _orders.MarkPaymentCaptured(store.CommerceBusinessId, order.OrderNumber, paymentResult.ProviderResult.ExternalId);
        _products.CommitPaidInventory(store.CommerceBusinessId, validatedItems);

        var paidOrder = _orders.GetOrder(store.CommerceBusinessId, order.OrderNumber) ?? order;

        if (store.IsParfait)
            _automations.MarkOrderConverted(paidOrder);

        try
        {
            if (_legacyCompatibility)
                await _analytics.TrackPurchaseAsync(paidOrder, HttpContext, ct);
            else
                await _analytics.TrackPurchaseScopedAsync(
                    store.CommerceBusinessId,
                    store.WebsiteContentVersionId,
                    store.WebsiteSiteKey,
                    store.BusinessKey,
                    store.CheckoutPath,
                    paidOrder,
                    HttpContext,
                    ct);
        }
        catch
        {
            // Payment authority is independent from optional analytics.
        }

        try
        {
            if (_commerceSignals is not null)
            {
                await _commerceSignals.RecordAsync(
                    "Purchase",
                    paidOrder.OrderNumber,
                    signalContext,
                    firstItem is null ? null : ProductSignal(firstItem),
                    CustomerSignal(request.Customer),
                    paidOrder.OrderNumber,
                    validatedItems.Select(ProductSignal).ToArray(),
                    ct);
            }
            else if (_legacyMetaSignalBridge is not null)
            {
                await _legacyMetaSignalBridge.RecordPurchaseAsync(paidOrder, HttpContext, ct);
            }
        }
        catch
        {
            // Payment authority is independent from downstream Meta delivery.
        }

        if (store.IsParfait)
        {
            try { await _mail.SendOrderReceiptAsync(paidOrder, ct); } catch { }
            try { await _mail.SendOrderNotificationAsync(paidOrder, ct); } catch { }
        }

        return Ok(new ParfaitCheckoutPayResponse
        {
            Success = true,
            OrderNumber = order.OrderNumber,
            RedirectUrl = SuccessUrl(store, order.OrderNumber)
        });
    }

    private async Task<IActionResult> SuccessCoreAsync(
        string? businessKey,
        string orderNumber,
        CancellationToken ct)
    {
        var store = await ResolveStoreAsync(businessKey, ct);
        if (store is null) return NotFound();

        ApplyStoreContext(store);
        return View("~/Views/Store/Success.cshtml", new ParfaitOrderSuccessViewModel
        {
            Order = string.IsNullOrWhiteSpace(orderNumber)
                ? null
                : _orders.GetOrder(store.CommerceBusinessId, orderNumber)
        });
    }

    private Task<CommerceStoreContext?> ResolveStoreAsync(string? businessKey, CancellationToken ct)
    {
        if (_stores is not null)
            return _stores.ResolvePublicAsync(businessKey, ct);

        if (!string.IsNullOrWhiteSpace(businessKey))
            return Task.FromResult<CommerceStoreContext?>(null);

        var businessId = _products.GetDefaultBusinessId();
        return Task.FromResult<CommerceStoreContext?>(new CommerceStoreContext(
            businessId,
            WebsiteContentVersionId: null,
            WebsiteSiteKey: "ParfaitApp",
            BusinessKey: "parfait",
            StoreName: "Parfait",
            NavigationLabel: "Shop",
            Headline: "Parfait",
            Subheadline: "Parfait storefront.",
            StoreRootPath: "/store",
            CartPath: "/store/cart",
            CheckoutPath: "/store/checkout",
            SuccessPath: "/store/success",
            CartStorageKey: "parfaitCart",
            IsParfait: true,
            AccentColor: "",
            LogoUrl: null,
            GlobalCheckoutUrl: null,
            Theme: new WebsiteThemeOverride()));
    }

    private void ApplyStoreContext(CommerceStoreContext store)
    {
        ViewData["CommerceStoreContext"] = store;
        ViewData["StoreRootPath"] = store.StoreRootPath;
        ViewData["StoreCartPath"] = store.CartPath;
        ViewData["StoreCheckoutPath"] = store.CheckoutPath;
        ViewData["StoreSuccessPath"] = store.SuccessPath;
    }

    private CommerceSignalContext BuildSignalContext(CommerceStoreContext store)
    {
        string? Cookie(string name) => Request.Cookies.TryGetValue(name, out var value) ? value : null;
        return new CommerceSignalContext(
            store.CommerceBusinessId,
            store.WebsiteContentVersionId,
            store.WebsiteSiteKey,
            store.BusinessKey,
            store.StoreName,
            $"{Request.Scheme}://{Request.Host}{store.CheckoutPath}",
            Cookie("pf_sid"),
            Cookie("pf_vid"),
            Request.Headers.Referer.ToString(),
            Request.Headers.UserAgent.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Query["fbclid"].FirstOrDefault(),
            Cookie("_fbc"),
            Cookie("_fbp"));
    }

    private static CommerceSignalCustomer CustomerSignal(ParfaitCheckoutCustomerRequest customer) =>
        new(customer.FirstName, customer.LastName, customer.Email, customer.Phone, customer.City, customer.State, customer.PostalCode);

    private static CommerceSignalProduct ProductSignal(ParfaitValidatedCartItem item) =>
        new(item.Id, item.Name, item.Slug, item.Size, item.Quantity, item.LineTotalCents);

    private static List<ParfaitValidatedCartItem> BuildValidatedItems(ParfaitCartQuoteResponse quote) =>
        quote.Items.Where(item => item.Quantity > 0 && item.IsAvailable)
            .Select(item => new ParfaitValidatedCartItem
            {
                Id = item.Id,
                Name = item.Name,
                Slug = item.Slug,
                Size = string.IsNullOrWhiteSpace(item.Size) ? "N/A" : item.Size.Trim(),
                Quantity = Math.Clamp(item.Quantity, 1, 20),
                UnitPriceCents = item.UnitPriceCents,
                CompareAtPriceCents = item.CompareAtPriceCents,
                ImageUrl = item.ImageUrl
            }).ToList();

    private static string? ValidateCustomer(ParfaitCheckoutCustomerRequest customer)
    {
        if (string.IsNullOrWhiteSpace(customer.FirstName)) return "First name is required.";
        if (string.IsNullOrWhiteSpace(customer.LastName)) return "Last name is required.";
        if (string.IsNullOrWhiteSpace(customer.Email)) return "Email is required.";
        if (string.IsNullOrWhiteSpace(customer.Phone)) return "Phone is required.";
        if (string.IsNullOrWhiteSpace(customer.AddressLine1)) return "Shipping address is required.";
        if (string.IsNullOrWhiteSpace(customer.City)) return "City is required.";
        if (string.IsNullOrWhiteSpace(customer.State)) return "State is required.";
        if (string.IsNullOrWhiteSpace(customer.PostalCode)) return "ZIP code is required.";
        if (!customer.Email.Contains('@') || !customer.Email.Contains('.')) return "Enter a valid email address.";
        return null;
    }

    private static string SuccessUrl(CommerceStoreContext store, string orderNumber) =>
        $"{store.SuccessPath}?orderNumber={Uri.EscapeDataString(orderNumber)}";

    private static string BuildPaymentIdempotencyKey(CommerceStoreContext store, ParfaitOrderRecord order) =>
        store.IsParfait ? order.OrderNumber : $"{store.BusinessKey}:{order.OrderNumber}";

    private static string BuildPaymentCorrelationId(CommerceStoreContext store, ParfaitOrderRecord order) =>
        store.IsParfait ? $"ParfaitCheckout:{order.OrderNumber}" : $"CommerceCheckout:{store.BusinessKey}:{order.OrderNumber}";

    private static int ResolvePaymentFailureStatusCode(ExecuteCommerceOneTimePaymentResult result)
    {
        if (result.Retryable) return StatusCodes.Status503ServiceUnavailable;
        if (string.Equals(result.SafeErrorCode, "UNHANDLED_BILLING_ERROR", StringComparison.OrdinalIgnoreCase))
            return StatusCodes.Status503ServiceUnavailable;
        if (!string.IsNullOrWhiteSpace(result.SafeErrorCode) &&
            result.SafeErrorCode.StartsWith("SQUARE_", StringComparison.OrdinalIgnoreCase) &&
            result.SafeErrorCode.EndsWith("_MISSING", StringComparison.OrdinalIgnoreCase))
            return StatusCodes.Status503ServiceUnavailable;
        if (!string.IsNullOrWhiteSpace(result.SafeErrorCode) &&
            result.SafeErrorCode.StartsWith("HTTP_5", StringComparison.OrdinalIgnoreCase))
            return StatusCodes.Status503ServiceUnavailable;
        return StatusCodes.Status400BadRequest;
    }
}
