using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;

namespace ParfaitApp.Services;

public sealed record ParfaitCommerceImportReport(
    int JsonProducts,
    int DbProducts,
    int JsonOrders,
    int DbOrders,
    int DbOrderLines,
    int DbImages,
    int DbInventoryItems,
    int DbDiscounts);

public sealed class ParfaitCommerceJsonImportService
{
    private readonly IWebHostEnvironment _environment;
    private readonly MasterAppDbContext _db;
    private readonly ParfaitBusinessScopeService _businessScope;

    public ParfaitCommerceJsonImportService(
        IWebHostEnvironment environment,
        MasterAppDbContext db,
        ParfaitBusinessScopeService businessScope)
    {
        _environment = environment;
        _db = db;
        _businessScope = businessScope;
    }

    private string ProductsPath => Path.Combine(_environment.ContentRootPath, "App_Data", "parfait-products.json");
    private string OrdersPath => Path.Combine(_environment.ContentRootPath, "App_Data", "parfait-orders.json");

    public async Task<ParfaitCommerceImportReport> ImportAsync(CancellationToken ct = default)
    {
        var business = await _businessScope.GetParfaitAsync(ct);

        var products = ReadJson<List<ParfaitProductEditorViewModel>>(ProductsPath);
        var orders = ReadJson<List<ParfaitOrderRecord>>(OrdersPath);

        foreach (var product in products)
        {
            await UpsertProductAsync(business.Id, product, ct);
        }

        foreach (var order in orders)
        {
            await UpsertOrderAsync(business.Id, order, ct);
        }

        await _db.SaveChangesAsync(ct);

        return await ReconcileAsync(ct);
    }

    public async Task<ParfaitCommerceImportReport> ReconcileAsync(CancellationToken ct = default)
    {
        var business = await _businessScope.GetParfaitAsync(ct);

        var jsonProducts = ReadJson<List<ParfaitProductEditorViewModel>>(ProductsPath).Count;
        var jsonOrders = ReadJson<List<ParfaitOrderRecord>>(OrdersPath).Count;

        var dbProducts = await _db.CommerceProducts.CountAsync(x => x.CommerceBusinessId == business.Id, ct);
        var dbOrders = await _db.CommerceOrders.CountAsync(x => x.CommerceBusinessId == business.Id, ct);
        var productIds = await _db.CommerceProducts
            .Where(x => x.CommerceBusinessId == business.Id)
            .Select(x => x.Id)
            .ToListAsync(ct);
        var orderIds = await _db.CommerceOrders
            .Where(x => x.CommerceBusinessId == business.Id)
            .Select(x => x.Id)
            .ToListAsync(ct);

        return new ParfaitCommerceImportReport(
            jsonProducts,
            dbProducts,
            jsonOrders,
            dbOrders,
            await _db.CommerceOrderLines.CountAsync(x => orderIds.Contains(x.CommerceOrderId), ct),
            await _db.CommerceProductImages.CountAsync(x => productIds.Contains(x.CommerceProductId), ct),
            await _db.CommerceProductInventoryItems.CountAsync(x => productIds.Contains(x.CommerceProductId), ct),
            await _db.CommerceProductDiscounts.CountAsync(x => productIds.Contains(x.CommerceProductId), ct));
    }

    private async Task UpsertProductAsync(Guid businessId, ParfaitProductEditorViewModel source, CancellationToken ct)
    {
        var externalKey = CleanRequired(source.Id, Guid.NewGuid().ToString("N"));
        var product = await _db.CommerceProducts
            .Include(x => x.Images)
            .Include(x => x.InventoryItems)
            .Include(x => x.Discounts)
            .SingleOrDefaultAsync(x => x.CommerceBusinessId == businessId && x.ExternalProductKey == externalKey, ct);

        if (product is null)
        {
            product = new CommerceProduct
            {
                CommerceBusinessId = businessId,
                ExternalProductKey = externalKey,
                CreatedUtc = DateTime.UtcNow
            };
            _db.CommerceProducts.Add(product);
        }

        product.Name = CleanRequired(source.Name, "Untitled Product");
        product.Slug = CleanRequired(source.Slug, externalKey);
        product.Description = source.Description ?? "";
        product.PriceLabel = CleanRequired(source.PriceLabel, "Coming Soon");
        product.Badge = CleanRequired(source.Badge, "Parfait");
        product.PriceCents = source.PriceCents;
        product.CompareAtPriceCents = source.CompareAtPriceCents;
        product.IsFeatured = source.IsFeatured;
        product.IsActive = source.IsActive;
        product.DisplayOrder = source.DisplayOrder;
        product.UpdatedUtc = DateTime.UtcNow;

        ReplaceImages(product, source.Images);
        ReplaceInventory(product, source.InventoryBySize);
        ReplaceDiscounts(product, source.DiscountCodes);
    }

    private async Task UpsertOrderAsync(Guid businessId, ParfaitOrderRecord source, CancellationToken ct)
    {
        var orderNumber = CleanRequired(source.OrderNumber, $"PF-{Guid.NewGuid():N}");
        var order = await _db.CommerceOrders
            .Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.CommerceBusinessId == businessId && x.OrderNumber == orderNumber, ct);

        if (order is null)
        {
            order = new CommerceOrder
            {
                CommerceBusinessId = businessId,
                OrderNumber = orderNumber
            };
            _db.CommerceOrders.Add(order);
        }

        order.CreatedUtc = source.CreatedUtc == default ? DateTime.UtcNow : source.CreatedUtc;
        order.UpdatedUtc = source.UpdatedUtc;
        order.PaidUtc = source.PaidUtc;
        order.ShippedUtc = source.ShippedUtc;
        order.FulfilledUtc = source.FulfilledUtc;
        order.Status = CleanRequired(source.Status, "Created");
        order.PaymentStatus = CleanRequired(source.PaymentStatus, "Pending");
        order.FulfillmentStatus = CleanRequired(source.FulfillmentStatus, "Unfulfilled");
        order.ReturnStatus = CleanRequired(source.ReturnStatus, "None");
        order.CheckoutAttemptId = CleanOptional(source.CheckoutAttemptId);
        order.IsPaymentProcessing = source.IsPaymentProcessing;
        order.PaymentProcessingStartedUtc = source.PaymentProcessingStartedUtc;
        order.SquarePaymentId = CleanOptional(source.SquarePaymentId);
        order.SquareError = CleanOptional(source.SquareError);
        order.TrackingCarrier = CleanOptional(source.TrackingCarrier);
        order.TrackingNumber = CleanOptional(source.TrackingNumber);
        order.AdminNotes = CleanOptional(source.AdminNotes);
        order.FirstName = CleanRequired(source.FirstName, "Customer");
        order.LastName = CleanRequired(source.LastName, "");
        order.Email = CleanRequired(source.Email, "unknown@example.com");
        order.Phone = CleanRequired(source.Phone, "");
        order.AddressLine1 = CleanRequired(source.AddressLine1, "");
        order.AddressLine2 = CleanOptional(source.AddressLine2);
        order.City = CleanRequired(source.City, "");
        order.State = CleanRequired(source.State, "");
        order.PostalCode = CleanRequired(source.PostalCode, "");
        order.Source = CleanRequired(source.Source, "Public Store");
        order.UserAgent = CleanOptional(source.UserAgent);
        order.RequestIp = CleanOptional(source.RequestIp);
        order.SubtotalCents = source.SubtotalCents;
        order.DiscountCode = CleanOptional(source.DiscountCode);
        order.DiscountLabel = CleanOptional(source.DiscountLabel);
        order.DiscountCents = source.DiscountCents;
        order.RefundedCents = source.RefundedCents;
        order.ShippingCents = source.ShippingCents;
        order.TaxCents = source.TaxCents;
        order.TotalCents = source.TotalCents;

        order.Lines.Clear();
        foreach (var item in source.Items)
        {
            order.Lines.Add(new CommerceOrderLine
            {
                ProductExternalKey = CleanRequired(item.Id, ""),
                ProductName = CleanRequired(item.Name, "Product"),
                ProductSlug = CleanRequired(item.Slug, ""),
                Size = CleanRequired(item.Size, ""),
                Quantity = item.Quantity,
                UnitPriceCents = item.UnitPriceCents,
                CompareAtPriceCents = item.CompareAtPriceCents,
                ImageUrl = CleanOptional(item.ImageUrl)
            });
        }
    }

    private static void ReplaceImages(CommerceProduct product, IEnumerable<ParfaitProductImageEditorViewModel> source)
    {
        product.Images.Clear();
        foreach (var image in source)
        {
            product.Images.Add(new CommerceProductImage
            {
                ExternalImageKey = CleanRequired(image.Id, Guid.NewGuid().ToString("N")),
                ImageUrl = CleanRequired(image.ImageUrl, ""),
                FileName = CleanRequired(image.FileName, ""),
                AltText = image.AltText ?? "",
                IsPrimary = image.IsPrimary,
                DisplayOrder = image.DisplayOrder,
                ObjectFit = CleanRequired(image.ObjectFit, "cover"),
                ObjectPositionX = image.ObjectPositionX,
                ObjectPositionY = image.ObjectPositionY,
                Zoom = image.Zoom
            });
        }
    }

    private static void ReplaceInventory(CommerceProduct product, IEnumerable<ParfaitProductSizeInventoryEditorViewModel> source)
    {
        product.InventoryItems.Clear();
        foreach (var item in source)
        {
            product.InventoryItems.Add(new CommerceProductInventoryItem
            {
                ExternalInventoryKey = CleanRequired(item.Id, Guid.NewGuid().ToString("N")),
                Size = CleanRequired(item.Size, ""),
                IsEnabled = item.IsEnabled,
                StockQuantity = item.StockQuantity,
                LowStockThreshold = item.LowStockThreshold,
                DisplayOrder = item.DisplayOrder
            });
        }
    }

    private static void ReplaceDiscounts(CommerceProduct product, IEnumerable<ParfaitProductDiscountCodeEditorViewModel> source)
    {
        product.Discounts.Clear();
        foreach (var discount in source)
        {
            product.Discounts.Add(new CommerceProductDiscount
            {
                ExternalDiscountKey = CleanRequired(discount.Id, Guid.NewGuid().ToString("N")),
                Code = discount.Code ?? "",
                DiscountType = CleanRequired(discount.DiscountType, "Percent"),
                Amount = discount.Amount,
                IsActive = discount.IsActive
            });
        }
    }

    private static T ReadJson<T>(string path) where T : new()
    {
        if (!File.Exists(path))
            return new T();

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T();
    }

    private static string CleanRequired(string? value, string fallback)
    {
        var cleaned = value?.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private static string? CleanOptional(string? value)
    {
        var cleaned = value?.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }
}
