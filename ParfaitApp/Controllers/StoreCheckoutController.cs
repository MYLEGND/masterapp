using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[Route("store")]
public sealed class StoreCheckoutController : Controller
{
    private readonly IConfiguration _configuration;
    private readonly ParfaitProductService _products;
    private readonly ParfaitOrderService _orders;
    private readonly SquarePaymentService _squarePayments;
    private readonly IGraphMailService _mail;
    private readonly ParfaitAnalyticsService _analytics;
    private readonly ParfaitMetaSignalBridgeService _metaSignalBridge;

    public StoreCheckoutController(
        IConfiguration configuration,
        ParfaitProductService products,
        ParfaitOrderService orders,
        SquarePaymentService squarePayments,
        IGraphMailService mail,
        ParfaitAnalyticsService analytics,
        ParfaitMetaSignalBridgeService metaSignalBridge)
    {
        _configuration = configuration;
        _products = products;
        _orders = orders;
        _squarePayments = squarePayments;
        _mail = mail;
        _analytics = analytics;
        _metaSignalBridge = metaSignalBridge;
    }

    [HttpGet("checkout")]
    public IActionResult Checkout()
    {
        ViewBag.SquareApplicationId = _configuration["Square:ApplicationId"];
        ViewBag.SquareLocationId = _configuration["Square:LocationId"];
        ViewBag.SquareEnvironment = _configuration["Square:Environment"] ?? "Sandbox";
        return View("~/Views/Store/Checkout.cshtml");
    }

    [HttpPost("checkout/quote")]
    [ValidateAntiForgeryToken]
    public IActionResult Quote([FromBody] ParfaitCartQuoteRequest request)
    {
        request ??= new ParfaitCartQuoteRequest();
        request.Items ??= [];

        return Ok(_products.QuoteCart(request.Items, request.DiscountCode));
    }

    [HttpPost("checkout/pay")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Pay([FromBody] ParfaitCheckoutPayRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CheckoutAttemptId))
        {
            return BadRequest(new ParfaitCheckoutPayResponse { Success = false, Error = "Checkout session expired. Refresh and try again." });
        }

        var validationError = ValidateCustomer(request.Customer);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            return BadRequest(new ParfaitCheckoutPayResponse { Success = false, Error = validationError });
        }

        if (string.IsNullOrWhiteSpace(request.SourceId))
        {
            return BadRequest(new ParfaitCheckoutPayResponse { Success = false, Error = "Missing Square payment token." });
        }

        var quote = _products.QuoteCart(request.Items ?? [], request.DiscountCode);
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
        {
            return BadRequest(new ParfaitCheckoutPayResponse { Success = false, Error = "No valid cart items were found." });
        }

        var paymentStart = _orders.BeginCheckoutPayment(
            request.CheckoutAttemptId,
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
                RedirectUrl = $"/store/success?orderNumber={Uri.EscapeDataString(paymentStart.Order.OrderNumber)}"
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

        var note = $"{order.OrderNumber}: " + string.Join(", ",
            order.Items.Select(i => $"{i.Name} / {i.Size} x{i.Quantity}"));

        (bool Success, string? PaymentId, string? Error) payment;
        try
        {
            payment = await _squarePayments.CreatePaymentAsync(
                request.SourceId,
                order.TotalCents,
                note,
                order.OrderNumber,
                ct);
        }
        catch (Exception)
        {
            _orders.MarkPaymentFailed(order.OrderNumber, "Square payment request could not be completed.");

            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ParfaitCheckoutPayResponse
            {
                Success = false,
                OrderNumber = order.OrderNumber,
                Error = "Payment could not be completed right now. Please try again."
            });
        }

        if (!payment.Success)
        {
            _orders.MarkPaymentFailed(order.OrderNumber, payment.Error ?? "Square payment failed.");

            return BadRequest(new ParfaitCheckoutPayResponse
            {
                Success = false,
                OrderNumber = order.OrderNumber,
                Error = payment.Error
            });
        }

        _orders.MarkPaid(order.OrderNumber, payment.PaymentId);
        _products.CommitPaidInventory(validatedItems);

        var paidOrder = _orders.GetOrder(order.OrderNumber) ?? order;
        paidOrder.SquarePaymentId = payment.PaymentId;
        paidOrder.PaymentStatus = "Paid";
        paidOrder.Status = "Paid";

        try
        {
            await _analytics.TrackPurchaseAsync(paidOrder, HttpContext, ct);
        }
        catch
        {
            // Payment succeeded. Analytics failure should not reverse the customer purchase.
        }

        try
        {
            await _metaSignalBridge.RecordPurchaseAsync(paidOrder, HttpContext, ct);
        }
        catch
        {
            // Payment succeeded. Meta signal bridge failure should not reverse the customer purchase.
        }

        try
        {
            await _mail.SendOrderReceiptAsync(paidOrder, ct);
            await _mail.SendOrderNotificationAsync(paidOrder, ct);
        }
        catch
        {
            // Payment succeeded. Email failure should not reverse the customer purchase.
        }

        return Ok(new ParfaitCheckoutPayResponse
        {
            Success = true,
            OrderNumber = order.OrderNumber,
            RedirectUrl = $"/store/success?orderNumber={Uri.EscapeDataString(order.OrderNumber)}"
        });
    }

    [HttpGet("success")]
    public IActionResult Success(string orderNumber)
    {
        return View("~/Views/Store/Success.cshtml", new ParfaitOrderSuccessViewModel
        {
            Order = string.IsNullOrWhiteSpace(orderNumber) ? null : _orders.GetOrder(orderNumber)
        });
    }

    private static List<ParfaitValidatedCartItem> BuildValidatedItems(ParfaitCartQuoteResponse quote)
    {
        return quote.Items
            .Where(item => item.Quantity > 0 && item.IsAvailable)
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
            })
            .ToList();
    }

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

        if (!customer.Email.Contains('@') || !customer.Email.Contains('.'))
            return "Enter a valid email address.";

        return null;
    }
}
