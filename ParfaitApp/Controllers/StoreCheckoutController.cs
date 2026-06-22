using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[Route("store")]
public sealed class StoreCheckoutController : Controller
{
    private readonly IConfiguration _configuration;
    private readonly ParfaitProductService _products;
    private readonly SquarePaymentService _squarePayments;

    public StoreCheckoutController(
        IConfiguration configuration,
        ParfaitProductService products,
        SquarePaymentService squarePayments)
    {
        _configuration = configuration;
        _products = products;
        _squarePayments = squarePayments;
    }

    [HttpGet("checkout")]
    public IActionResult Checkout()
    {
        ViewBag.SquareApplicationId = _configuration["Square:ApplicationId"];
        ViewBag.SquareLocationId = _configuration["Square:LocationId"];
        ViewBag.SquareEnvironment = _configuration["Square:Environment"] ?? "Sandbox";

        Console.WriteLine($"Square checkout config loaded: AppId={(!string.IsNullOrWhiteSpace(_configuration["Square:ApplicationId"]))}, LocationId={(!string.IsNullOrWhiteSpace(_configuration["Square:LocationId"]))}, AccessToken={(!string.IsNullOrWhiteSpace(_configuration["Square:AccessToken"]))}");

        return View("~/Views/Store/Checkout.cshtml");
    }

    [HttpPost("checkout/pay")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Pay([FromBody] ParfaitCheckoutPayRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SourceId))
        {
            return BadRequest(new ParfaitCheckoutPayResponse
            {
                Success = false,
                Error = "Missing Square payment token."
            });
        }

        var activeProducts = _products.GetActiveStoreProducts();

        var validatedItems = new List<ParfaitValidatedCartItem>();

        foreach (var item in request.Items)
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
                UnitPriceCents = product.PriceCents
            });
        }

        if (validatedItems.Count == 0)
        {
            return BadRequest(new ParfaitCheckoutPayResponse
            {
                Success = false,
                Error = "No valid cart items were found."
            });
        }

        var totalCents = validatedItems.Sum(i => i.LineTotalCents);
        var note = "Parfait Store Order: " + string.Join(", ",
            validatedItems.Select(i => $"{i.Name} / {i.Size} x{i.Quantity}"));

        var payment = await _squarePayments.CreatePaymentAsync(
            request.SourceId,
            totalCents,
            note,
            ct);

        if (!payment.Success)
        {
            return BadRequest(new ParfaitCheckoutPayResponse
            {
                Success = false,
                Error = payment.Error
            });
        }

        TempData["ParfaitPaymentId"] = payment.PaymentId;

        return Ok(new ParfaitCheckoutPayResponse
        {
            Success = true,
            RedirectUrl = "/store/success"
        });
    }

    [HttpGet("success")]
    public IActionResult Success()
    {
        return View("~/Views/Store/Success.cshtml");
    }
}
