using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Domain.Entities;
using Legend.Commerce;
using Legend.Commerce.Shipping;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Services;
using Xunit;
using ZXing.Common;
using ZXing.QrCode.Internal;

namespace AgentPortal.Tests;

public sealed class CommerceOrderReferenceLabelTests
{
    [Fact]
    public void RenderedSvgDecodesOpaqueReferenceWithFourModuleQuietZone()
    {
        var reference = "LEGEND-" + Guid.NewGuid().ToString("N") + "-" + Guid.NewGuid().ToString("N");
        var model = CommerceOrderReferenceLabel.Create(View(reference), "Test Store");
        var svg = XDocument.Parse(model.QrSvg).Root!;
        var size = int.Parse(svg.Attribute("viewBox")!.Value.Split(' ')[2]);
        var pixels = new BitMatrix(size);
        // Rasterize the renderer's SVG module runs, then decode with an independent implementation.
        var path = svg.Elements().Single(x => x.Name.LocalName == "path").Attribute("d")!.Value;
        var runs = Regex.Matches(path, @"M(\d+) (\d+)h(\d+)v1h-\d+z");
        Assert.NotEmpty(runs);
        foreach (Match run in runs)
        {
            var x = int.Parse(run.Groups[1].Value); var y = int.Parse(run.Groups[2].Value); var width = int.Parse(run.Groups[3].Value);
            Assert.InRange(x, 4, size - 5); Assert.InRange(y, 4, size - 5);
            Assert.True(x + width <= size - 4);
            for (var offset = 0; offset < width; offset++) pixels[x + offset, y] = true;
        }
        var inner = new BitMatrix(size - 8);
        for (var y = 4; y < size - 4; y++) for (var x = 4; x < size - 4; x++) inner[x - 4, y - 4] = pixels[x, y];
        Assert.Equal(reference, new Decoder().decode(inner, null).Text);
        Assert.DoesNotContain("buyer@example.test", model.QrSvg);
        Assert.DoesNotContain("private-address", model.QrSvg);
        Assert.DoesNotContain("http", reference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://store.example.test/order?ticket=private")]
    [InlineData("buyer@example.test")]
    public void NonOpaqueReferenceCannotBeEncoded(string reference) =>
        Assert.Throws<InvalidOperationException>(() => CommerceOrderReferenceLabel.Create(View(reference), "Store"));

    [Theory]
    [InlineData("/commerce/manage/order/reference-label")]
    [InlineData("/internal/commerce/orders/reference-label")]
    public void ReferenceLabelRequiresOrdersCapabilityAndIsReadOnly(string path)
    {
        Assert.Equal("orders", CommerceManagementCapabilityPolicy.ForRequest(path, "GET"));
        Assert.Equal("orders", CommerceManagementCapabilityPolicy.ForRequest(path, "HEAD"));
        Assert.Null(CommerceManagementCapabilityPolicy.ForRequest(path, "POST"));
    }

    private static ShippingOrderView View(string reference) => new(new ParfaitOrderRecord {
        OrderNumber = "ORDER-1", FirstName = "Test", LastName = "Buyer", Email = "buyer@example.test", Phone = "", AddressLine1 = "private-address",
        City = "Test", State = "AZ", PostalCode = "85001", CreatedUtc = DateTime.UtcNow
    }, new([], null, true, null, 0), new ShippingOrderState { Reference = reference }, true, null);
}

public sealed partial class CommerceManagementCapabilityPolicyTests
{
    [Fact]
    public async Task ReferenceStickerUsesScopedOrderAndRejectsForeignOrder()
    {
        await using var f = new NavigationFixture("owner", "Active", true);
        f.Controller.Request.Path = "/commerce/manage/order/reference-label";
        var own = new CommerceOrder { CommerceBusinessId = f.Business.Id, OrderNumber = "OWN-QR",
            ShippingLabelStateJson = JsonSerializer.Serialize(new ShippingOrderState { Reference = "LEGEND-" + Guid.NewGuid().ToString("N") }) };
        f.Db.CommerceOrders.Add(own);
        f.Db.CommerceOrders.Add(new CommerceOrder { CommerceBusinessId = Guid.NewGuid(), OrderNumber = "FOREIGN-QR" });
        await f.Db.SaveChangesAsync();
        var shipping = new CommerceShippingService(f.Db, [], null!, new ParfaitOrderService(f.Db));
        var view = Assert.IsType<ViewResult>(await f.Controller.OrderReferenceLabel(f.Ticket, own.OrderNumber, shipping, default));
        Assert.Equal("OWN-QR", Assert.IsType<CommerceOrderReferenceLabel>(view.Model).OrderNumber);
        Assert.IsType<NotFoundResult>(await f.Controller.OrderReferenceLabel(f.Ticket, "FOREIGN-QR", shipping, default));
        f.Member.Status = "Revoked"; await f.Db.SaveChangesAsync();
        Assert.IsType<UnauthorizedResult>(await f.Controller.OrderReferenceLabel(f.Ticket, own.OrderNumber, shipping, default));
    }
}
