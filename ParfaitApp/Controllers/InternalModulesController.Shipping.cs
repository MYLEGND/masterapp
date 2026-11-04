using System.ComponentModel.DataAnnotations;
using Legend.Commerce.Shipping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Security;

namespace ParfaitApp.Controllers;

// Native Parfait authentication/page permissions adapt to the same shipping owner
// used by signed-ticket stores. No business ID is accepted from the request.
public sealed partial class InternalModulesController
{
    [HttpGet("settings/shipping")]
    [ParfaitInternalPage("Shipping", "Settings", "Postage connection and ship-from address.", 2, 3)]
    public async Task<IActionResult> Shipping([FromServices] CommerceShippingService shipping, CancellationToken ct)
    {
        var business = await _parfaitScope.GetParfaitAsync(ct);
        return View("~/Views/InternalModules/Shipping.cshtml", shipping.Setup(business.Id));
    }

    [HttpPost("settings/shipping/connect")]
    [ParfaitInternalPageAccess("/internal/settings/shipping")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConnectShipping([FromForm] ShippingSetupInput input, [FromServices] CommerceShippingService shipping, CancellationToken ct)
    {
        var business = await _parfaitScope.GetParfaitAsync(ct);
        await NativeShippingAction(async () => {
            if (!ModelState.IsValid) throw new InvalidOperationException("Complete the connection and ship-from details.");
            await shipping.ConnectAsync(business.Id, input, ct);
        }, "Shipping connection saved.");
        return RedirectToAction(nameof(Shipping));
    }

    [HttpGet("commerce/orders/shipping")]
    [ParfaitInternalPage("Shipping label", "Commerce", "Order postage.", 1, 3, ShowInNavigation = false)]
    [ParfaitInternalPageAccess("/internal/commerce/orders")]
    public async Task<IActionResult> OrderShipping(string orderNumber, [FromServices] CommerceShippingService shipping, CancellationToken ct)
    {
        var business = await _parfaitScope.GetParfaitAsync(ct);
        var model = shipping.Order(business.Id, orderNumber);
        return model is null ? NotFound() : View("~/Views/InternalModules/OrderShipping.cshtml", model);
    }

    [HttpGet("commerce/orders/reference-label")]
    [ParfaitInternalPage("Order-reference sticker", "Commerce", "Companion QR sticker; not postage.", 1, 4, ShowInNavigation = false)]
    [ParfaitInternalPageAccess("/internal/commerce/orders")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> OrderReferenceLabel(string orderNumber, [FromServices] CommerceShippingService shipping, CancellationToken ct)
    {
        var business = await _parfaitScope.GetParfaitAsync(ct);
        var order = shipping.Order(business.Id, orderNumber);
        if (order is null) return NotFound();
        if (order.Shipment is null) return BadRequest("Prepare a shipment before printing its order-reference sticker.");
        return View("~/Views/InternalModules/OrderReferenceLabel.cshtml", CommerceOrderReferenceLabel.Create(order, business.DisplayName));
    }

    [HttpPost("commerce/orders/shipping/rates")]
    [ParfaitInternalPageAccess("/internal/commerce/orders")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ShippingRates([FromForm] ShippingPackageInput input, [FromServices] CommerceShippingService shipping, CancellationToken ct)
    {
        var business = await _parfaitScope.GetParfaitAsync(ct);
        await NativeShippingAction(async () => {
            if (!ModelState.IsValid) throw new InvalidOperationException("Enter the packed dimensions, weight, and destination country.");
            await shipping.QuoteAsync(business.Id, input, ct);
        }, "Rates ready. Select a service to review postage.");
        return RedirectToAction(nameof(OrderShipping), new { orderNumber = input.OrderNumber });
    }

    [HttpPost("commerce/orders/shipping/buy")]
    [ParfaitInternalPageAccess("/internal/commerce/orders")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BuyShippingLabel(string orderNumber, string rateId, bool confirmed, [FromServices] CommerceShippingService shipping, CancellationToken ct)
    {
        var business = await _parfaitScope.GetParfaitAsync(ct);
        await NativeShippingAction(() => shipping.BuyAsync(business.Id, orderNumber, rateId, confirmed, ct), "Purchase request recorded. Review its status below; printing does not mark the package shipped.");
        return RedirectToAction(nameof(OrderShipping), new { orderNumber });
    }

    [HttpPost("commerce/orders/shipping/reconcile")]
    [ParfaitInternalPageAccess("/internal/commerce/orders")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReconcileShippingLabel(string orderNumber, string? providerTransactionId, [FromServices] CommerceShippingService shipping, CancellationToken ct)
    {
        var business = await _parfaitScope.GetParfaitAsync(ct);
        await NativeShippingAction(() => shipping.ReconcileAsync(business.Id, orderNumber, ct, providerTransactionId), "Shipment status refreshed.");
        return RedirectToAction(nameof(OrderShipping), new { orderNumber });
    }

    private async Task NativeShippingAction(Func<Task> action, string success)
    {
        try { await action(); TempData["ShippingStatus"] = success; }
        catch (DbUpdateConcurrencyException) { TempData["ShippingError"] = "Another request changed this shipment. Reload and check its status before continuing."; }
        catch (Exception ex) when (ex is InvalidOperationException or ValidationException or ShippingProviderException)
        { TempData["ShippingError"] = ex.Message; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { TempData["ShippingError"] = "The provider response was interrupted. If a purchase started, check its status before taking any further action."; }
    }
}
