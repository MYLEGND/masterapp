using System.ComponentModel.DataAnnotations;
using Legend.Commerce.Shipping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ParfaitApp.Controllers;

public sealed partial class CommerceManagementController
{
    [HttpGet("shipping")]
    public async Task<IActionResult> Shipping(string ticket, [FromServices] CommerceShippingService shipping, CancellationToken ct)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        ApplyManagementViewData(store, ticket, "settings");
        return View("~/Views/InternalModules/Shipping.cshtml", shipping.Setup(store.CommerceBusinessId));
    }

    [HttpPost("shipping/connect")]
    public async Task<IActionResult> ConnectShipping(string ticket, [FromForm] ShippingSetupInput input, [FromServices] CommerceShippingService shipping, CancellationToken ct)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        await ShippingAction(async () => {
            if (!ModelState.IsValid) throw new InvalidOperationException("Complete the connection and ship-from details.");
            await shipping.ConnectAsync(store.CommerceBusinessId, input, ct);
        }, "Shipping connection saved.");
        return ManagerRedirect(nameof(Shipping), new { ticket });
    }

    [HttpGet("order/shipping")]
    public async Task<IActionResult> OrderShipping(string ticket, string orderNumber, [FromServices] CommerceShippingService shipping, CancellationToken ct)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        var model = shipping.Order(store.CommerceBusinessId, orderNumber);
        if (model is null) return NotFound();
        ApplyManagementViewData(store, ticket, "orders");
        return View("~/Views/InternalModules/OrderShipping.cshtml", model);
    }

    [HttpGet("order/reference-label")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> OrderReferenceLabel(string ticket, string orderNumber, [FromServices] CommerceShippingService shipping, CancellationToken ct)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        var order = shipping.Order(store.CommerceBusinessId, orderNumber);
        if (order is null) return NotFound();
        if (order.Shipment is null) return BadRequest("Prepare a shipment before printing its order-reference sticker.");
        ApplyManagementViewData(store, ticket, "orders");
        return View("~/Views/InternalModules/OrderReferenceLabel.cshtml",
            CommerceOrderReferenceLabel.Create(order, store.StoreName));
    }

    [HttpPost("order/shipping/rates")]
    public async Task<IActionResult> ShippingRates(string ticket, [FromForm] ShippingPackageInput input, [FromServices] CommerceShippingService shipping, CancellationToken ct)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        await ShippingAction(async () => {
            if (!ModelState.IsValid) throw new InvalidOperationException("Enter the packed dimensions, weight, and destination country.");
            await shipping.QuoteAsync(store.CommerceBusinessId, input, ct);
        }, "Rates ready. Select a service to review postage.");
        return ManagerRedirect(nameof(OrderShipping), new { ticket, orderNumber = input.OrderNumber });
    }

    [HttpPost("order/shipping/buy")]
    public async Task<IActionResult> BuyShippingLabel(string ticket, string orderNumber, string rateId, bool confirmed, [FromServices] CommerceShippingService shipping, CancellationToken ct)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        await ShippingAction(() => shipping.BuyAsync(store.CommerceBusinessId, orderNumber, rateId, confirmed, ct), "Purchase request recorded. Review its status below; printing does not mark the package shipped.");
        return ManagerRedirect(nameof(OrderShipping), new { ticket, orderNumber });
    }

    [HttpPost("order/shipping/reconcile")]
    public async Task<IActionResult> ReconcileShippingLabel(string ticket, string orderNumber, string? providerTransactionId, [FromServices] CommerceShippingService shipping, CancellationToken ct)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        await ShippingAction(() => shipping.ReconcileAsync(store.CommerceBusinessId, orderNumber, ct, providerTransactionId), "Shipment status refreshed.");
        return ManagerRedirect(nameof(OrderShipping), new { ticket, orderNumber });
    }

    private async Task ShippingAction(Func<Task> action, string success)
    {
        try { await action(); TempData["ShippingStatus"] = success; }
        catch (DbUpdateConcurrencyException) { TempData["ShippingError"] = "Another request changed this shipment. Reload and check its status before continuing."; }
        catch (Exception ex) when (ex is InvalidOperationException or ValidationException or ShippingProviderException)
        { TempData["ShippingError"] = ex.Message; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { TempData["ShippingError"] = "The provider response was interrupted. If a purchase started, check its status before taking any further action."; }
    }
}
