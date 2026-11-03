using System.Text.RegularExpressions;
using QRCoder;

namespace Legend.Commerce.Shipping;

public sealed record CommerceOrderReferenceLabel(string StoreName, string OrderNumber, DateTime OrderCreatedUtc,
    int UnitCount, string Reference, string QrSvg)
{
    public static CommerceOrderReferenceLabel Create(ShippingOrderView order, string storeName)
    {
        var reference = order.Shipment?.Reference ?? "";
        // Only the saved opaque shipment identity is encoded. Never accept URLs, user text or access tickets.
        if (!Regex.IsMatch(reference, "^LEGEND-[0-9a-f]{32}(-[0-9a-f]{32})?$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Prepare a shipment before printing its order-reference sticker.");
        using var data = QRCodeGenerator.GenerateQrCode(reference, QRCodeGenerator.ECCLevel.Q);
        using var renderer = new SvgQRCode(data);
        var svg = renderer.GetGraphic(8, "#000000", "#ffffff", drawQuietZones: true,
            sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
        return new(storeName, order.Order.OrderNumber, order.Order.CreatedUtc,
            order.Order.Items.Sum(item => item.Quantity), reference, svg);
    }
}
