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

    [HttpPost("checkout/pay")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Pay([FromBody] ParfaitCheckoutPayRequest request, CancellationToken ct)
    {
        var validationError = ValidateCustomer(request.Customer);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            return BadRequest(new ParfaitCheckoutPayResponse { Success = false, Error = validationError });
        }

        if (string.IsNullOrWhiteSpace(request.SourceId))
        {
            return BadRequest(new ParfaitCheckoutPayResponse { Success = false, Error = "Missing Square payment token." });
        }

        var validatedItems = ValidateCartItems(request.Items);

        if (validatedItems.Count == 0)
        {
            return BadRequest(new ParfaitCheckoutPayResponse { Success = false, Error = "No valid cart items were found." });
        }

        var order = _orders.CreatePendingOrder(request.Customer, validatedItems, HttpContext);

        var note = $"{order.OrderNumber}: " + string.Join(", ",
            order.Items.Select(i => $"{i.Name} / {i.Size} x{i.Quantity}"));

        var payment = await _squarePayments.CreatePaymentAsync(
            request.SourceId,
            order.TotalCents,
            note,
            ct);

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

    private List<ParfaitValidatedCartItem> ValidateCartItems(List<ParfaitCheckoutItemRequest> cartItems)
    {
        var activeProducts = _products.GetActiveStoreProducts();
        var validatedItems = new List<ParfaitValidatedCartItem>();

        foreach (var item in cartItems)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || item.Quantity <= 0)
                continue;

            var product = activeProducts.FirstOrDefault(p =>
                string.Equals(p.Id, item.Id, StringComparison.OrdinalIgnoreCase));

            if (product is null || product.PriceCents <= 0)
                continue;

            validatedItems.Add(new ParfaitValidatedCartItem
            {
                Id = product.Id,
                Name = product.Name,
                Size = string.IsNullOrWhiteSpace(item.Size) ? "N/A" : item.Size.Trim(),
                Quantity = Math.Clamp(item.Quantity, 1, 20),
                UnitPriceCents = product.PriceCents,
                ImageUrl = product.PrimaryImageUrl
            });
        }

        return validatedItems;
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
