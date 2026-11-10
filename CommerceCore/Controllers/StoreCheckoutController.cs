using Infrastructure.Commerce.Payments;
using System.Text.Json;
using Domain.Billing;
using Infrastructure.Billing.Square;
using Infrastructure.Commerce;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Services;

using Shared.Analytics;
namespace ParfaitApp.Controllers;

[Route("store")]
public sealed class StoreCheckoutController : Controller
{
    private const string CommerceCurrency = "USD";
    private readonly MerchantCheckoutService? _merchantCheckout;
    private readonly MerchantPaymentCompletionService? _completion;
    private readonly IConfiguration? _configuration;
    private readonly SquareBillingOptions _squareOptions;
    private readonly ParfaitProductService _products;
    private readonly ParfaitOrderService _orders;
    private readonly ParfaitCustomerAutomationService _automations;
    private readonly IBillingOrchestrator _billingOrchestrator;
    private readonly IGraphMailService _mail;
    private readonly CommerceSignalService? _commerceSignals;
    private readonly CommerceStoreContextService? _stores;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public StoreCheckoutController(
        SquareBillingOptions squareOptions,
        ParfaitProductService products,
        ParfaitOrderService orders,
        ParfaitCustomerAutomationService automations,
        IBillingOrchestrator billingOrchestrator,
        IGraphMailService mail,
        CommerceSignalService commerceSignals,
        CommerceStoreContextService stores,
        MerchantCheckoutService merchantCheckout,
        MerchantPaymentCompletionService completion,
        IConfiguration configuration)
    {
        _squareOptions = squareOptions;
        _products = products;
        _orders = orders;
        _automations = automations;
        _billingOrchestrator = billingOrchestrator;
        _mail = mail;
        _commerceSignals = commerceSignals;
        _stores = stores;
        _merchantCheckout = merchantCheckout;
        _completion = completion;
        _configuration = configuration;
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
        CommerceSignalService commerceSignals,
        MerchantPaymentCompletionService? completion = null)
    {
        _squareOptions = squareOptions;
        _products = products;
        _orders = orders;
        _automations = automations;
        _billingOrchestrator = billingOrchestrator;
        _mail = mail;
        _commerceSignals = commerceSignals;
        _completion = completion;
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

        var redirect = await CanonicalizeScopedRequestAsync(store, businessKey, "/checkout", ct);
        if (redirect is not null) return redirect;

        ApplyStoreContext(store);
        var merchant = UseMerchantCheckout(store);
        var browser = merchant && _merchantCheckout is not null
            ? await _merchantCheckout.GetBrowserConfigurationAsync(store.CommerceBusinessId, ct) : null;
        ViewBag.MerchantHostedCheckout = merchant && browser is null;
        ViewBag.CheckoutAvailable = merchant
            ? _merchantCheckout is not null && await _merchantCheckout.IsConfiguredAsync(store.CommerceBusinessId, ct)
            : _squareOptions.HasBrowserCredentials() && _squareOptions.HasServerCredentials();
        ViewBag.SquareApplicationId = merchant ? browser?.ApplicationId : _squareOptions.ApplicationId;
        ViewBag.SquareLocationId = merchant ? browser?.LocationId : _squareOptions.LocationId;
        ViewBag.SquareEnvironment = merchant ? browser?.Environment : _squareOptions.Environment.ToString();
        ViewBag.MerchantConnectionRevision = browser?.ConnectionRevisionId;
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

        // Contact entry is not consent. Older cached checkout clients must not
        // create file-backed leads or trigger abandoned-cart outreach.
        return StatusCode(StatusCodes.Status410Gone, new
        {
            error = "Please use the inquiry form to request help with your order."
        });
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

        // Existing intents retain their immutable server quote and processor owner.
        // Recovery never prepares another order, consent receipt, or amount.
        var prepared = _orders.GetOrderByCheckoutAttemptId(store.CommerceBusinessId, request.CheckoutAttemptId);
        if (prepared is not null && UseMerchantCheckout(store) && _merchantCheckout is not null)
        {
            var merchantAttempt = await _merchantCheckout.GetAttemptAsync(store.CommerceBusinessId, prepared.CommerceOrderId, ct);
            if (prepared.IsPaid)
            {
                if (merchantAttempt is null && _completion is not null)
                    await _completion.ResumeLegacyOrderAsync(store.CommerceBusinessId, prepared.CommerceOrderId, ct);
                return Ok(new { success = true, redirectUrl = SuccessUrl(store, prepared.OrderNumber) });
            }
            if (merchantAttempt is null && _orders.HasProcessorPayment(store.CommerceBusinessId, prepared.CommerceOrderId))
                return PaymentPending(prepared.OrderNumber); // Legacy receipts never become merchant charges.
            if (merchantAttempt is not null || prepared.IsPaymentProcessing)
                return await BeginMerchantAsync(store, prepared, request.SourceId, request.MerchantConnectionRevision, ct);
        }

        var validationError = ValidateCustomer(request.Customer);
        if (!string.IsNullOrWhiteSpace(validationError))
            return BadRequest(new ParfaitCheckoutPayResponse { Success = false, Error = validationError });

        if (!UseMerchantCheckout(store) && string.IsNullOrWhiteSpace(request.SourceId))
            return BadRequest(new ParfaitCheckoutPayResponse { Success = false, Error = "Missing payment token." });

        CheckoutPaymentStartResult paymentStart;
        if (!UseMerchantCheckout(store) && prepared is not null && (prepared.IsPaid || prepared.IsPaymentProcessing))
        {
            paymentStart = new(prepared.IsPaid ? CheckoutPaymentStartState.AlreadyPaid : CheckoutPaymentStartState.AlreadyProcessing, prepared);
        }
        else
        {
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

            try
            {
                paymentStart = _orders.BeginCheckoutPayment(
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
                HttpContext,
                request.MarketingEmailConsent ? order =>
                {
                    var environment = HttpContext.RequestServices.GetService<IHostEnvironment>();
                    _automations.RecordMarketingOptIn(store.CommerceBusinessId, order, environment?.IsProduction() != true);
                    _automations.CaptureCheckoutLead(store.CommerceBusinessId, new ParfaitAutomationCheckoutLeadCaptureRequest {
                        CheckoutAttemptId = request.CheckoutAttemptId, Customer = request.Customer, Items = request.Items, DiscountCode = request.DiscountCode
                    }, quote);
                } : null);
            }
            catch (StoreInventoryException ex)
            {
                return Conflict(new ParfaitCheckoutPayResponse { Success = false, Error = ex.Message });
            }
            catch (Exception ex) when (ex is Microsoft.EntityFrameworkCore.DbUpdateException or Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
            {
                HttpContext.RequestServices.GetService<ILogger<StoreCheckoutController>>()?.LogWarning(
                    "Checkout preparation failed before provider invocation for business {BusinessId}: {FailureType}", store.CommerceBusinessId, ex.GetType().Name);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ParfaitCheckoutPayResponse {
                    Success = false, Error = "Checkout details could not be saved. No payment was started. Please retry."
                });
            }
        }

        if (paymentStart.State == CheckoutPaymentStartState.AlreadyPaid)
        {
            if (!UseMerchantCheckout(store) && _completion is not null)
                await _completion.ResumeLegacyOrderAsync(store.CommerceBusinessId, paymentStart.Order.CommerceOrderId, ct);
            return Ok(new ParfaitCheckoutPayResponse
            {
                Success = true,
                OrderNumber = paymentStart.Order.OrderNumber,
                RedirectUrl = SuccessUrl(store, paymentStart.Order.OrderNumber)
            });
        }

        if (UseMerchantCheckout(store))
            return await BeginMerchantAsync(store, paymentStart.Order, request.SourceId, request.MerchantConnectionRevision, ct);

        if (_completion is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ParfaitCheckoutPayResponse
            { Success = false, OrderNumber = paymentStart.Order.OrderNumber, Error = "Payment completion is unavailable. No charge was started." });

        if (paymentStart.State == CheckoutPaymentStartState.AlreadyProcessing)
        {
            if (await _completion.ResumeLegacyOrderAsync(store.CommerceBusinessId, paymentStart.Order.CommerceOrderId, ct))
                return Ok(new ParfaitCheckoutPayResponse { Success = true, OrderNumber = paymentStart.Order.OrderNumber,
                    RedirectUrl = SuccessUrl(store, paymentStart.Order.OrderNumber) });
            // Preparation is durable before the billing receipt exists. A lost response
            // in that gap may resume the SAME snapshot/key; any receipt forbids a new call.
            if (_orders.HasProcessorPayment(store.CommerceBusinessId, paymentStart.Order.CommerceOrderId))
                return PaymentPending(paymentStart.Order.OrderNumber);
        }

        var order = paymentStart.Order;
        var firstItem = order.Items.FirstOrDefault();
        var signalContext = BuildSignalContext(store) with
        {
            OrderId = order.CommerceOrderId, EventUtc = order.CreatedUtc, OrderValueCents = order.TotalCents
        };
        if (_commerceSignals is not null)
        {
            await _commerceSignals.RecordAsync(
                "InitiateCheckout",
                request.CheckoutAttemptId!,
                signalContext,
                firstItem is null ? null : ProductSignal(firstItem),
                new CommerceSignalCustomer(order.FirstName, order.LastName, order.Email, order.Phone, order.City, order.State, order.PostalCode),
                order.OrderNumber,
                order.Items.Select(ProductSignal).ToArray(),
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
                    BuildPaymentCorrelationId(store, order),
                    CommerceCompletionContextJson: JsonSerializer.Serialize(signalContext)),
                ct);
        }
        catch (Exception ex) when (ParfaitOrderService.IsUniqueConstraintViolation(ex)
            && _orders.HasProcessorPayment(store.CommerceBusinessId, order.CommerceOrderId))
        {
            // The unique durable receipt owns the gateway invocation. The loser waits.
            return PaymentPending(order.OrderNumber);
        }
        catch
        {
            // Transport failure is not evidence that a charge failed; preserve pending state.
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
            if (!paymentResult.Retryable && failureStatusCode != StatusCodes.Status503ServiceUnavailable)
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

        // The genuine SubscriptionPayment owns the SAME durable completion effects
        // as merchant captures; never manufacture a merchant attempt or send twice.
        await _completion.CompleteLegacyAsync(paymentResult.PaymentRecord!.Id, store.CommerceBusinessId, ct);

        return Ok(new ParfaitCheckoutPayResponse
        {
            Success = true,
            OrderNumber = order.OrderNumber,
            RedirectUrl = SuccessUrl(store, order.OrderNumber)
        });
    }

    private IActionResult PaymentPending(string orderNumber) => Conflict(new ParfaitCheckoutPayResponse
    {
        Success = false, OrderNumber = orderNumber,
        Error = "Payment is already processing for this order. Please wait."
    });

    private bool UseMerchantCheckout(CommerceStoreContext store) =>
        !store.IsParfait || (_configuration?.GetValue<bool>("MerchantPayments:ParfaitCutoverEnabled") ?? false);

    private async Task<IActionResult> BeginMerchantAsync(CommerceStoreContext store, ParfaitOrderRecord order, string? sourceToken, Guid? connectionRevision, CancellationToken ct)
    {
        if (_merchantCheckout is null) return StatusCode(503, new { success = false, error = "Merchant payments are unavailable." });
        try
        {
            var returnPath = $"{store.CheckoutPath}/return?orderNumber={Uri.EscapeDataString(order.OrderNumber)}&attempt={Uri.EscapeDataString(order.CheckoutAttemptId!)}";
            var publicRoot = _stores is null ? null : await _stores.ResolveCanonicalPublicRootAsync(store, ct);
            var returnUrl = string.IsNullOrWhiteSpace(publicRoot)
                ? $"{Request.Scheme}://{Request.Host}{returnPath}"
                : new Uri(new Uri(publicRoot), returnPath).AbsoluteUri;
            var signal = BuildSignalContext(store) with { OrderId = order.CommerceOrderId, EventUtc = order.CreatedUtc, OrderValueCents = order.TotalCents };
            if (_commerceSignals is not null)
                await _commerceSignals.RecordAsync("InitiateCheckout", order.CheckoutAttemptId!, signal,
                    order.Items.FirstOrDefault() is { } first ? ProductSignal(first) : null,
                    new CommerceSignalCustomer(order.FirstName, order.LastName, order.Email, order.Phone, order.City, order.State, order.PostalCode),
                    order.OrderNumber, order.Items.Select(ProductSignal).ToArray(), ct);
            var evidence = await _merchantCheckout.BeginAsync(store.CommerceBusinessId, order.CommerceOrderId, returnUrl, JsonSerializer.Serialize(signal), ct, sourceToken, connectionRevision);
            if (evidence.State == "Captured")
            {
                var saved = await _merchantCheckout.GetAttemptAsync(store.CommerceBusinessId, order.CommerceOrderId, ct);
                if (saved is not null && _completion is not null) await _completion.CompleteAsync(saved.Id, ct);
                return Ok(new { success = true, redirectUrl = SuccessUrl(store, order.OrderNumber) });
            }
            if (evidence.State == "Failed")
            {
                var failed = await _merchantCheckout.GetAttemptAsync(store.CommerceBusinessId, order.CommerceOrderId, ct);
                if (failed is not null && _completion is not null) await _completion.CompleteAsync(failed.Id, ct);
                return UnprocessableEntity(new { success = false, paymentDeclined = true,
                    error = "Your card was declined. No payment was taken. You can try another card." });
            }
            var pending = await _merchantCheckout.GetAttemptAsync(store.CommerceBusinessId, order.CommerceOrderId, ct);
            return pending?.Provider == "Square" || string.IsNullOrWhiteSpace(evidence.CheckoutUrl)
                ? StatusCode(503, new { success = false, paymentPending = true, orderNumber = order.OrderNumber, error = "Payment verification is pending. Check its status before starting another payment." })
                : Ok(new { success = true, redirectUrl = evidence.CheckoutUrl });
        }
        catch (MerchantPaymentException ex) when (ex.Message == "PAYMENT_FORM_CHANGED")
        {
            return Conflict(new { success = false, error = "Payment setup changed. Refresh this page before trying again." });
        }
        catch (MerchantPaymentException)
        {
            return StatusCode(503, new { success = false, error = "Payments are unavailable or verification is pending. Please retry this checkout later." });
        }
    }

    [HttpGet("checkout/return")]
    [HttpGet("s/{businessKey}/checkout/return")]
    public async Task<IActionResult> MerchantReturn(string? businessKey, string? orderNumber, string attempt, CancellationToken ct)
    {
        var store = await ResolveStoreAsync(businessKey, ct);
        if (store is null && _stores is not null)
            store = await _stores.ResolveRetainedTransactionAsync(HttpContext, businessKey, orderNumber, attempt, ct);
        if (store is null || !UseMerchantCheckout(store) || _merchantCheckout is null) return NotFound();
        var order = string.IsNullOrWhiteSpace(orderNumber)
            ? _orders.GetOrderByCheckoutAttemptId(store.CommerceBusinessId, attempt)
            : _orders.GetOrder(store.CommerceBusinessId, orderNumber);
        if (order is null || string.IsNullOrWhiteSpace(attempt) || order.CheckoutAttemptId != attempt)
            return StatusCode(503, new { success = false, paymentPending = true, error = "The original checkout has not been found yet. Check its status again; do not start another payment." });
        orderNumber = order.OrderNumber;
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Cache-Control"] = "no-store";
        var processor = await _merchantCheckout.GetAttemptAsync(store.CommerceBusinessId, order.CommerceOrderId, ct);
        if (processor is null && !_orders.HasProcessorPayment(store.CommerceBusinessId, order.CommerceOrderId))
        {
            var browser = await _merchantCheckout.GetBrowserConfigurationAsync(store.CommerceBusinessId, ct);
            if (browser is not null)
                return Ok(new { success = false, paymentNotStarted = true, orderNumber, recovery = new {
                    checkoutAttemptId = order.CheckoutAttemptId, totalCents = order.TotalCents,
                    merchantConnectionRevision = browser.ConnectionRevisionId,
                    customer = new { firstName = order.FirstName, lastName = order.LastName, email = order.Email, phone = order.Phone,
                        addressLine1 = order.AddressLine1, addressLine2 = order.AddressLine2, city = order.City, state = order.State, postalCode = order.PostalCode },
                    items = order.Items.Select(x => new { id = x.Id, size = x.Size, quantity = x.Quantity })
                }, error = "No processor payment was started. You can retry this same saved order." });
        }
        var evidence = await _merchantCheckout.ReconcileAsync(store.CommerceBusinessId, order.CommerceOrderId, ct);
        var json = Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase);
        var saved = await _merchantCheckout.GetAttemptAsync(store.CommerceBusinessId, order.CommerceOrderId, ct);
        if ((evidence?.State is "Captured" or "Failed") && saved is not null && _completion is not null)
            await _completion.CompleteAsync(saved.Id, ct);
        if (evidence?.State == "Captured")
            return json ? Ok(new { success = true, redirectUrl = SuccessUrl(store, orderNumber) }) : Redirect(SuccessUrl(store, orderNumber));
        if (evidence?.State == "Failed")
            return json ? UnprocessableEntity(new { success = false, paymentDeclined = true, error = "Your card was declined. No payment was taken. You can try another card." })
                : Content("Your card was declined. No payment was taken. Return to checkout to try another card.");
        return json ? StatusCode(503, new { success = false, paymentPending = true, orderNumber, error = "Payment verification is pending. Check its status before starting another payment." })
            : Content("Your payment is awaiting verification. Refresh this page to check again. Do not start a second payment.");
    }

    private async Task<IActionResult> SuccessCoreAsync(
        string? businessKey,
        string orderNumber,
        CancellationToken ct)
    {
        var store = await ResolveStoreAsync(businessKey, ct);
        var attempt = Request.Query["attempt"].ToString();
        if (store is null && _stores is not null)
            store = await _stores.ResolveRetainedTransactionAsync(HttpContext, businessKey, orderNumber, attempt, ct);
        if (store is null && _stores is not null)
            store = await _stores.ResolveRetainedPaidReceiptAsync(HttpContext, businessKey, orderNumber, ct);
        if (store is null) return NotFound();

        var suffix = "/success?orderNumber=" + Uri.EscapeDataString(orderNumber ?? string.Empty) + (string.IsNullOrEmpty(attempt) ? "" : "&attempt=" + Uri.EscapeDataString(attempt));
        var redirect = await CanonicalizeScopedRequestAsync(store, businessKey, suffix, ct);
        if (redirect is not null) return redirect;

        var confirmed = string.IsNullOrWhiteSpace(orderNumber) ? null : _orders.GetOrder(store.CommerceBusinessId, orderNumber);
        if (confirmed is not null && !confirmed.IsPaid) return Content("Your payment has not been confirmed.");
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Cache-Control"] = "no-store";
        ApplyStoreContext(store);
        return View("~/Views/Store/Success.cshtml", new ParfaitOrderSuccessViewModel
        {
            Order = confirmed
        });
    }

    private Task<CommerceStoreContext?> ResolveStoreAsync(string? businessKey, CancellationToken ct)
    {
        if (_stores is not null)
            return _stores.ResolvePublicAsync(HttpContext, businessKey, ct);

        if (!string.IsNullOrWhiteSpace(businessKey))
            return Task.FromResult<CommerceStoreContext?>(null);

        var businessId = _products.GetDefaultBusinessId();
        return Task.FromResult<CommerceStoreContext?>(new CommerceStoreContext(
            businessId,
            WebsiteContentVersionId: null,
            AgentTrackingProfileId: null,
            WebsiteSiteKey: "ParfaitApp",
            BusinessKey: "parfait",
            StoreName: "Parfait",
            NavigationLabel: "Shop",
            CartIcon: "cart",
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
            Theme: new WebsiteDesignTheme(),
            WebsiteShellPrefix: null,
            WebsiteShellSuffix: null));
    }

    private async Task<IActionResult?> CanonicalizeScopedRequestAsync(
        CommerceStoreContext store,
        string? businessKey,
        string suffix,
        CancellationToken ct)
    {
        if (_stores is null ||
            string.IsNullOrWhiteSpace(businessKey) ||
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

    private CommerceSignalContext BuildSignalContext(CommerceStoreContext store)
    {
        string? Cookie(string name) => Request.Cookies.TryGetValue(name, out var value) ? value : null;
        return CommerceSignalAttribution.Apply(new CommerceSignalContext(
            store.CommerceBusinessId,
            store.AgentTrackingProfileId,
            store.WebsiteContentVersionId,
            store.WebsiteSiteKey,
            store.BusinessKey,
            store.StoreName,
            $"{Request.Scheme}://{(_stores?.ResolveEffectivePublicHost(HttpContext) ?? Request.Host.Host)}{store.CheckoutPath}",
            Cookie("pf_sid"),
            Cookie("pf_vid"),
            Request.Headers.Referer.ToString(),
            Request.Headers.UserAgent.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Query["fbclid"].FirstOrDefault(),
            OpenAiClickReference.Normalize(Cookie("pf_oppref") ?? Request.Query["oppref"].FirstOrDefault()),
            Cookie("_fbc"),
            Cookie("_fbp")), Request);
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
                ImageUrl = item.ImageUrl,
                InventoryRequirementsJson = item.InventoryRequirementsJson
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

    private string SuccessUrl(CommerceStoreContext store, string orderNumber)
    {
        var attempt = _orders.GetOrder(store.CommerceBusinessId, orderNumber)?.CheckoutAttemptId;
        return $"{store.SuccessPath}?orderNumber={Uri.EscapeDataString(orderNumber)}" +
            (string.IsNullOrEmpty(attempt) ? "" : "&attempt=" + Uri.EscapeDataString(attempt));
    }

    private static string BuildPaymentIdempotencyKey(CommerceStoreContext store, ParfaitOrderRecord order) =>
        store.IsParfait ? order.OrderNumber : $"{store.BusinessKey}:{order.OrderNumber}";

    private static string BuildPaymentCorrelationId(CommerceStoreContext store, ParfaitOrderRecord order) =>
        store.IsParfait ? $"ParfaitCheckout:{order.OrderNumber}" : $"CommerceCheckout:{store.BusinessKey}:{order.OrderNumber}";

    private static int ResolvePaymentFailureStatusCode(ExecuteCommerceOneTimePaymentResult result)
    {
        if (result.Retryable || string.Equals(result.SafeErrorCode, "PAYMENT_RECONCILIATION_REQUIRED", StringComparison.Ordinal))
            return StatusCodes.Status503ServiceUnavailable;
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
