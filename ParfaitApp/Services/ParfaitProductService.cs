using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;

namespace ParfaitApp.Services;

public sealed class ParfaitProductService
{
    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif"
    };

    private static readonly object Lock = new();

    private readonly ParfaitStoragePaths _storagePaths;
    private readonly MasterAppDbContext _db;

    public ParfaitProductService(ParfaitStoragePaths storagePaths, MasterAppDbContext db)
    {
        _storagePaths = storagePaths;
        _db = db;
    }

    private string UploadRoot => _storagePaths.UploadRoot;

    public IReadOnlyList<ParfaitProductEditorViewModel> GetAllProducts()
    {
        var businessId = GetBusinessId();

        return _db.CommerceProducts
            .AsNoTracking()
            .Include(x => x.Images)
            .Include(x => x.InventoryItems)
            .Include(x => x.Discounts)
            .Where(x => x.CommerceBusinessId == businessId)
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.Name)
            .ToList()
            .Select(MapEditorProduct)
            .Select(product => NormalizeProduct(product))
            .OrderBy(product => product.DisplayOrder)
            .ThenBy(product => product.Name)
            .ToList();
    }

    public IReadOnlyList<ParfaitStoreProductViewModel> GetActiveStoreProducts()
    {
        var settings = GetCommerceSettings();

        return GetAllProducts()
            .Where(product => product.IsActive)
            .OrderBy(product => product.DisplayOrder)
            .ThenBy(product => product.Name)
            .Select(product => MapStoreProduct(product, settings))
            .ToList();
    }

    public ParfaitStoreProductViewModel? GetActiveStoreProductBySlug(string slug)
    {
        return GetActiveStoreProducts()
            .FirstOrDefault(product => string.Equals(product.Slug, slug, StringComparison.OrdinalIgnoreCase));
    }

    public ParfaitStoreProductViewModel? GetActiveStoreProductById(string id)
    {
        return GetActiveStoreProducts()
            .FirstOrDefault(product => string.Equals(product.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public ParfaitCartQuoteResponse QuoteCart(IReadOnlyList<ParfaitCheckoutItemRequest> cartItems, string? discountCode)
    {
        var settings = GetCommerceSettings();
        var products = GetAllProducts()
            .Where(product => product.IsActive)
            .ToDictionary(product => product.Id, StringComparer.OrdinalIgnoreCase);

        var quote = new ParfaitCartQuoteResponse
        {
            Success = true,
            IsValid = true,
            DiscountCode = string.IsNullOrWhiteSpace(discountCode)
                ? null
                : ParfaitProductCatalogDefaults.NormalizeDiscountCode(discountCode)
        };

        foreach (var item in cartItems)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
                continue;

            if (!products.TryGetValue(item.Id.Trim(), out var product) || product.PriceCents <= 0)
            {
                quote.Messages.Add("A product in the cart is no longer available.");
                quote.IsValid = false;
                continue;
            }

            var requestedQuantity = Math.Clamp(item.Quantity, 1, 20);
            var normalizedSize = ParfaitProductCatalogDefaults.NormalizeSize(item.Size);
            var selectedSize = string.IsNullOrWhiteSpace(normalizedSize)
                ? product.InventoryBySize.OrderBy(size => size.DisplayOrder).FirstOrDefault(size => size.IsEnabled)
                : product.InventoryBySize.OrderBy(size => size.DisplayOrder).FirstOrDefault(size => string.Equals(size.Size, normalizedSize, StringComparison.OrdinalIgnoreCase));

            if (selectedSize is null)
            {
                quote.Messages.Add($"{product.Name} no longer offers the selected size.");
                quote.IsValid = false;
                continue;
            }

            var effectiveQuantity = requestedQuantity;
            var issue = "";
            var isAvailable = true;
            var availabilityTone = selectedSize.StatusTone;
            var availabilityLabel = selectedSize.StatusLabel;

            if (!selectedSize.IsEnabled)
            {
                effectiveQuantity = 0;
                isAvailable = false;
                availabilityTone = "muted";
                availabilityLabel = "Hidden";
                issue = $"{product.Name} {selectedSize.Size} is hidden.";
            }
            else if (selectedSize.StockQuantity <= 0)
            {
                effectiveQuantity = 0;
                isAvailable = false;
                availabilityTone = "danger";
                availabilityLabel = "Sold Out";
                issue = $"{product.Name} {selectedSize.Size} is sold out.";
            }
            else if (requestedQuantity > selectedSize.StockQuantity)
            {
                effectiveQuantity = selectedSize.StockQuantity;
                isAvailable = effectiveQuantity > 0;
                availabilityTone = effectiveQuantity <= Math.Max(1, selectedSize.LowStockThreshold) ? "warning" : "success";
                availabilityLabel = effectiveQuantity > 0 ? $"{effectiveQuantity} Left" : "Sold Out";
                issue = effectiveQuantity > 0
                    ? $"{product.Name} {selectedSize.Size} was adjusted to {effectiveQuantity} available."
                    : $"{product.Name} {selectedSize.Size} is sold out.";
            }

            var line = new ParfaitCartLineQuote
            {
                Key = $"{product.Id}:{selectedSize.Size}",
                Id = product.Id,
                Name = product.Name,
                Slug = product.Slug,
                Size = selectedSize.Size,
                Badge = product.Badge,
                RequestedQuantity = requestedQuantity,
                Quantity = effectiveQuantity,
                UnitPriceCents = product.PriceCents,
                CompareAtPriceCents = product.CompareAtPriceCents,
                ImageUrl = product.Images.OrderBy(image => image.DisplayOrder).FirstOrDefault(image => image.IsPrimary)?.ImageUrl
                    ?? product.Images.OrderBy(image => image.DisplayOrder).FirstOrDefault()?.ImageUrl
                    ?? "/images/favicon/parfait-logo.png",
                IsAvailable = isAvailable,
                IsLowStock = selectedSize.IsLowStock || (effectiveQuantity > 0 && effectiveQuantity <= Math.Max(1, selectedSize.LowStockThreshold)),
                AvailabilityTone = availabilityTone,
                AvailabilityLabel = availabilityLabel,
                Issue = string.IsNullOrWhiteSpace(issue) ? null : issue
            };

            quote.Items.Add(line);

            if (!string.IsNullOrWhiteSpace(issue))
            {
                quote.Messages.Add(issue);
                quote.IsValid = false;
            }
        }

        if (quote.Items.Count == 0 || quote.Items.All(item => item.Quantity <= 0))
        {
            quote.IsValid = false;
            quote.Error = "No valid cart items were found.";
            quote.Messages.Add("No valid cart items were found.");
            quote.Messages = quote.Messages.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return quote;
        }

        quote.SubtotalCents = quote.Items.Sum(item => item.LineTotalCents);
        quote.ItemCount = quote.Items.Sum(item => item.Quantity);

        var normalizedDiscountCode = quote.DiscountCode;
        var appliedDiscounts = quote.Items
            .Where(item => item.Quantity > 0)
            .Select(item => new
            {
                Item = item,
                Discount = string.IsNullOrWhiteSpace(normalizedDiscountCode)
                    ? FindPreferredDisplayDiscount(products[item.Id], settings)
                    : FindMatchingDiscount(products[item.Id], normalizedDiscountCode!, settings)
            })
            .Where(match => match.Discount is not null)
            .ToList();

        if (!string.IsNullOrWhiteSpace(normalizedDiscountCode))
        {
            if (appliedDiscounts.Count == 0)
            {
                quote.IsValid = false;
                quote.Messages.Add("Discount code is not available for the current cart.");
            }
            else
            {
                var firstMatch = appliedDiscounts[0].Discount!;
                quote.DiscountLabel = firstMatch.SummaryLabel;
                quote.DiscountCents = appliedDiscounts.Sum(match => CalculateDiscountCents(match.Item.LineTotalCents, match.Discount!));
                quote.DiscountCents = Math.Min(quote.DiscountCents, quote.SubtotalCents);
            }
        }
        else if (appliedDiscounts.Count > 0)
        {
            quote.DiscountLabel = "Automatic Savings";
            quote.DiscountCents = appliedDiscounts.Sum(match => CalculateDiscountCents(match.Item.LineTotalCents, match.Discount!));
            quote.DiscountCents = Math.Min(quote.DiscountCents, quote.SubtotalCents);
        }

        var discountedSubtotal = Math.Max(0, quote.SubtotalCents - quote.DiscountCents);
        quote.ShippingCents = discountedSubtotal > 0 ? settings.ShippingFeeCents : 0;
        var taxableTotal = discountedSubtotal + quote.ShippingCents;
        quote.TaxCents = taxableTotal > 0 && settings.TaxPercent > 0
            ? (int)Math.Round(taxableTotal * (settings.TaxPercent / 100m), MidpointRounding.AwayFromZero)
            : 0;
        quote.TotalCents = Math.Max(0, discountedSubtotal + quote.ShippingCents + quote.TaxCents);
        quote.Messages = quote.Messages.Where(message => !string.IsNullOrWhiteSpace(message)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (!quote.IsValid && string.IsNullOrWhiteSpace(quote.Error))
            quote.Error = quote.Messages.FirstOrDefault() ?? "The cart needs attention before checkout.";

        return quote;
    }

    public ParfaitCommerceSettingsViewModel GetCommerceSettings()
    {
        var businessId = GetBusinessId();
        var settings = _db.CommerceBusinessSettings.AsNoTracking().SingleOrDefault(x => x.CommerceBusinessId == businessId);

        return NormalizeCommerceSettings(settings is null
            ? new ParfaitCommerceSettingsViewModel()
            : new ParfaitCommerceSettingsViewModel
            {
                ShippingFeeCents = settings.ShippingFeeCents,
                TaxPercent = settings.TaxPercent,
                GlobalDiscount = new ParfaitProductDiscountCodeEditorViewModel
                {
                    Code = settings.GlobalDiscountCode,
                    DiscountType = settings.GlobalDiscountType,
                    Amount = settings.GlobalDiscountAmount,
                    IsActive = settings.GlobalDiscountIsActive
                }
            });
    }

    public void SaveCommerceSettings(ParfaitCommerceSettingsViewModel settings)
    {
        lock (Lock)
        {
            var businessId = GetBusinessId();
            var normalized = NormalizeCommerceSettings(settings);

            var entity = _db.CommerceBusinessSettings.SingleOrDefault(x => x.CommerceBusinessId == businessId);
            if (entity is null)
            {
                entity = new CommerceBusinessSettings { CommerceBusinessId = businessId };
                _db.CommerceBusinessSettings.Add(entity);
            }

            entity.ShippingFeeCents = normalized.ShippingFeeCents;
            entity.TaxPercent = normalized.TaxPercent;
            entity.GlobalDiscountCode = normalized.GlobalDiscount.Code;
            entity.GlobalDiscountType = normalized.GlobalDiscount.DiscountType;
            entity.GlobalDiscountAmount = normalized.GlobalDiscount.Amount;
            entity.GlobalDiscountIsActive = normalized.GlobalDiscount.IsActive;
            entity.UpdatedUtc = DateTime.UtcNow;

            _db.SaveChanges();
        }
    }

    public void SaveProduct(ParfaitProductEditorViewModel product)
    {
        lock (Lock)
        {
            var businessId = GetBusinessId();

            var existing = _db.CommerceProducts
                .Include(x => x.Images)
                .Include(x => x.InventoryItems)
                .Include(x => x.Discounts)
                .SingleOrDefault(x => x.CommerceBusinessId == businessId && x.ExternalProductKey == product.Id);

            var existingImages = existing is null
                ? []
                : existing.Images
                    .OrderBy(x => x.DisplayOrder)
                    .Select(MapImage)
                    .ToList();

            var normalized = NormalizeProduct(product, existingImages);

            if (existing is null)
            {
                if (normalized.DisplayOrder <= 0)
                {
                    var maxOrder = _db.CommerceProducts
                        .Where(x => x.CommerceBusinessId == businessId)
                        .Select(x => (int?)x.DisplayOrder)
                        .Max() ?? 0;

                    normalized.DisplayOrder = maxOrder <= 0 ? 10 : maxOrder + 10;
                }

                existing = new CommerceProduct
                {
                    CommerceBusinessId = businessId,
                    ExternalProductKey = normalized.Id,
                    CreatedUtc = DateTime.UtcNow
                };
                _db.CommerceProducts.Add(existing);
            }
            else if (existing.DisplayOrder > 0)
            {
                normalized.DisplayOrder = existing.DisplayOrder;
            }

            ApplyProduct(existing, normalized);
            _db.SaveChanges();
        }
    }

    public void DeleteProduct(string id)
    {
        lock (Lock)
        {
            var businessId = GetBusinessId();
            var product = _db.CommerceProducts
                .Include(x => x.Images)
                .Include(x => x.InventoryItems)
                .Include(x => x.Discounts)
                .SingleOrDefault(x => x.CommerceBusinessId == businessId && x.ExternalProductKey == id);

            if (product is null)
                return;

            var productFolder = Path.Combine(UploadRoot, product.ExternalProductKey);
            if (Directory.Exists(productFolder))
                Directory.Delete(productFolder, recursive: true);

            _db.CommerceProducts.Remove(product);
            _db.SaveChanges();
        }
    }

    public void ReorderProducts(IReadOnlyList<string> productIds)
    {
        lock (Lock)
        {
            var businessId = GetBusinessId();
            var products = _db.CommerceProducts
                .Where(x => x.CommerceBusinessId == businessId)
                .ToList();

            if (productIds.Count == 0 || products.Count == 0)
                return;

            var lookup = products.ToDictionary(x => x.ExternalProductKey, StringComparer.OrdinalIgnoreCase);
            var ordered = new List<CommerceProduct>();

            foreach (var productId in productIds)
            {
                if (lookup.TryGetValue(productId, out var product) && !ordered.Contains(product))
                    ordered.Add(product);
            }

            ordered.AddRange(products.Where(product => !ordered.Contains(product)).OrderBy(x => x.DisplayOrder).ThenBy(x => x.Name));

            for (var index = 0; index < ordered.Count; index++)
            {
                ordered[index].DisplayOrder = (index + 1) * 10;
                ordered[index].UpdatedUtc = DateTime.UtcNow;
            }

            _db.SaveChanges();
        }
    }

    public async Task UploadImagesAsync(string productId, IReadOnlyList<IFormFile> files)
    {
        if (files.Count == 0)
            return;

        var businessId = GetBusinessId();
        var product = _db.CommerceProducts
            .Include(x => x.Images)
            .SingleOrDefault(x => x.CommerceBusinessId == businessId && x.ExternalProductKey == productId);

        if (product is null)
            return;

        var productFolder = Path.Combine(UploadRoot, product.ExternalProductKey);
        _storagePaths.EnsureInitialized();
        Directory.CreateDirectory(productFolder);

        var nextOrder = product.Images.Count == 0 ? 10 : product.Images.Max(image => image.DisplayOrder) + 10;
        var added = false;

        foreach (var file in files.Where(file => file.Length > 0))
        {
            var extension = Path.GetExtension(file.FileName);
            if (!AllowedImageExtensions.Contains(extension))
                continue;

            var imageId = Guid.NewGuid().ToString("N");
            var safeFileName = $"{imageId}{extension.ToLowerInvariant()}";
            var physicalPath = Path.Combine(productFolder, safeFileName);

            await using (var stream = File.Create(physicalPath))
            {
                await file.CopyToAsync(stream);
            }

            product.Images.Add(new CommerceProductImage
            {
                ExternalImageKey = imageId,
                FileName = safeFileName,
                ImageUrl = _storagePaths.GetImageUrl(product.ExternalProductKey, safeFileName),
                AltText = product.Name,
                IsPrimary = product.Images.Count == 0,
                DisplayOrder = nextOrder,
                ObjectFit = "cover",
                ObjectPositionX = 50,
                ObjectPositionY = 50,
                Zoom = 1.0m
            });

            nextOrder += 10;
            added = true;
        }

        if (added)
        {
            EnsureOnePrimaryImage(product);
            product.UpdatedUtc = DateTime.UtcNow;
            _db.SaveChanges();
        }
    }

    public void DeleteImage(string productId, string imageId)
    {
        lock (Lock)
        {
            var businessId = GetBusinessId();
            var product = _db.CommerceProducts
                .Include(x => x.Images)
                .SingleOrDefault(x => x.CommerceBusinessId == businessId && x.ExternalProductKey == productId);

            if (product is null)
                return;

            var image = product.Images.FirstOrDefault(x => string.Equals(x.ExternalImageKey, imageId, StringComparison.OrdinalIgnoreCase));
            if (image is null)
                return;

            foreach (var physicalPath in _storagePaths.ResolveImagePhysicalPaths(image.ImageUrl))
            {
                if (File.Exists(physicalPath))
                    File.Delete(physicalPath);
            }

            product.Images.Remove(image);
            EnsureOnePrimaryImage(product);
            product.UpdatedUtc = DateTime.UtcNow;
            _db.SaveChanges();
        }
    }

    public void ReorderImages(string productId, IReadOnlyList<string> imageIds)
    {
        lock (Lock)
        {
            var businessId = GetBusinessId();
            var product = _db.CommerceProducts
                .Include(x => x.Images)
                .SingleOrDefault(x => x.CommerceBusinessId == businessId && x.ExternalProductKey == productId);

            if (product is null || imageIds.Count == 0)
                return;

            var lookup = product.Images.ToDictionary(x => x.ExternalImageKey, StringComparer.OrdinalIgnoreCase);
            var ordered = new List<CommerceProductImage>();

            foreach (var imageId in imageIds)
            {
                if (lookup.TryGetValue(imageId, out var image) && !ordered.Contains(image))
                    ordered.Add(image);
            }

            ordered.AddRange(product.Images.Where(image => !ordered.Contains(image)).OrderBy(x => x.DisplayOrder));

            for (var index = 0; index < ordered.Count; index++)
                ordered[index].DisplayOrder = (index + 1) * 10;

            EnsureOnePrimaryImage(product);
            product.UpdatedUtc = DateTime.UtcNow;
            _db.SaveChanges();
        }
    }

    public void SaveImageDisplaySettings(string productId, string imageId, string objectFit, int objectPositionX, int objectPositionY, decimal zoom)
    {
        lock (Lock)
        {
            var businessId = GetBusinessId();
            var image = _db.CommerceProductImages
                .Include(x => x.CommerceProduct)
                .SingleOrDefault(x =>
                    x.CommerceProduct != null
                    && x.CommerceProduct.CommerceBusinessId == businessId
                    && x.CommerceProduct.ExternalProductKey == productId
                    && x.ExternalImageKey == imageId);

            if (image is null)
                return;

            image.ObjectFit = string.Equals(objectFit, "contain", StringComparison.OrdinalIgnoreCase) ? "contain" : "cover";
            image.ObjectPositionX = Math.Clamp(objectPositionX, 0, 100);
            image.ObjectPositionY = Math.Clamp(objectPositionY, 0, 100);
            image.Zoom = Math.Clamp(zoom, 1.0m, 2.5m);

            if (image.CommerceProduct is not null)
                image.CommerceProduct.UpdatedUtc = DateTime.UtcNow;

            _db.SaveChanges();
        }
    }

    public void CommitPaidInventory(IReadOnlyList<ParfaitValidatedCartItem> items)
    {
        if (items.Count == 0)
            return;

        lock (Lock)
        {
            var businessId = GetBusinessId();
            var updated = false;

            foreach (var item in items)
            {
                var normalizedSize = ParfaitProductCatalogDefaults.NormalizeSize(item.Size);
                var inventory = _db.CommerceProductInventoryItems
                    .Include(x => x.CommerceProduct)
                    .FirstOrDefault(x =>
                        x.CommerceProduct != null
                        && x.CommerceProduct.CommerceBusinessId == businessId
                        && x.CommerceProduct.ExternalProductKey == item.Id
                        && x.Size == normalizedSize);

                if (inventory is null)
                    continue;

                inventory.StockQuantity = Math.Max(0, inventory.StockQuantity - Math.Max(item.Quantity, 0));
                if (inventory.CommerceProduct is not null)
                    inventory.CommerceProduct.UpdatedUtc = DateTime.UtcNow;
                updated = true;
            }

            if (updated)
                _db.SaveChanges();
        }
    }

    private Guid GetBusinessId()
    {
        var business = _db.CommerceBusinesses.SingleOrDefault(x => x.Key == ParfaitBusinessScopeService.ParfaitBusinessKey);

        if (business is not null)
            return business.Id;

        business = new CommerceBusiness
        {
            Key = ParfaitBusinessScopeService.ParfaitBusinessKey,
            DisplayName = "Parfait",
            LegalName = "MyLegnd LLC",
            BusinessType = "Apparel / Ecommerce",
            PrimaryDomain = "shopparfait.com",
            Status = "Active",
            IsActive = true,
            OwnerEmail = "parfait@mylegnd.com",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        _db.CommerceBusinesses.Add(business);
        _db.SaveChanges();

        return business.Id;
    }

    private static ParfaitProductEditorViewModel MapEditorProduct(CommerceProduct product)
    {
        return new ParfaitProductEditorViewModel
        {
            Id = product.ExternalProductKey,
            Name = product.Name,
            Slug = product.Slug,
            Description = product.Description,
            PriceLabel = product.PriceLabel,
            Badge = product.Badge,
            PriceCents = product.PriceCents,
            CompareAtPriceCents = product.CompareAtPriceCents,
            IsFeatured = product.IsFeatured,
            IsActive = product.IsActive,
            DisplayOrder = product.DisplayOrder,
            Images = product.Images.OrderBy(x => x.DisplayOrder).Select(MapImage).ToList(),
            InventoryBySize = product.InventoryItems.OrderBy(x => x.DisplayOrder).Select(MapInventory).ToList(),
            DiscountCodes = product.Discounts.OrderBy(x => x.Code).Select(MapDiscount).ToList()
        };
    }

    private static ParfaitProductImageEditorViewModel MapImage(CommerceProductImage image)
    {
        return new ParfaitProductImageEditorViewModel
        {
            Id = image.ExternalImageKey,
            ImageUrl = image.ImageUrl,
            FileName = image.FileName,
            AltText = image.AltText,
            IsPrimary = image.IsPrimary,
            DisplayOrder = image.DisplayOrder,
            ObjectFit = image.ObjectFit,
            ObjectPositionX = image.ObjectPositionX,
            ObjectPositionY = image.ObjectPositionY,
            Zoom = image.Zoom
        };
    }

    private static ParfaitProductSizeInventoryEditorViewModel MapInventory(CommerceProductInventoryItem item)
    {
        return new ParfaitProductSizeInventoryEditorViewModel
        {
            Id = item.ExternalInventoryKey,
            Size = item.Size,
            IsEnabled = item.IsEnabled,
            StockQuantity = item.StockQuantity,
            LowStockThreshold = item.LowStockThreshold,
            DisplayOrder = item.DisplayOrder
        };
    }

    private static ParfaitProductDiscountCodeEditorViewModel MapDiscount(CommerceProductDiscount discount)
    {
        return new ParfaitProductDiscountCodeEditorViewModel
        {
            Id = discount.ExternalDiscountKey,
            Code = discount.Code,
            DiscountType = discount.DiscountType,
            Amount = discount.Amount,
            IsActive = discount.IsActive
        };
    }

    private static void ApplyProduct(CommerceProduct entity, ParfaitProductEditorViewModel product)
    {
        entity.ExternalProductKey = product.Id;
        entity.Name = product.Name;
        entity.Slug = product.Slug;
        entity.Description = product.Description;
        entity.PriceLabel = product.PriceLabel;
        entity.Badge = product.Badge;
        entity.PriceCents = product.PriceCents;
        entity.CompareAtPriceCents = product.CompareAtPriceCents;
        entity.IsFeatured = product.IsFeatured;
        entity.IsActive = product.IsActive;
        entity.DisplayOrder = product.DisplayOrder;
        entity.UpdatedUtc = DateTime.UtcNow;

        SyncImages(entity, product.Images);
        SyncInventory(entity, product.InventoryBySize);
        SyncDiscounts(entity, product.DiscountCodes);
    }

    private static void SyncImages(CommerceProduct entity, IReadOnlyList<ParfaitProductImageEditorViewModel> images)
    {
        var normalized = NormalizeImages(images, entity.Name);
        entity.Images.Clear();

        foreach (var image in normalized)
        {
            entity.Images.Add(new CommerceProductImage
            {
                ExternalImageKey = image.Id,
                ImageUrl = image.ImageUrl,
                FileName = image.FileName,
                AltText = image.AltText,
                IsPrimary = image.IsPrimary,
                DisplayOrder = image.DisplayOrder,
                ObjectFit = image.ObjectFit,
                ObjectPositionX = image.ObjectPositionX,
                ObjectPositionY = image.ObjectPositionY,
                Zoom = image.Zoom
            });
        }
    }

    private static void SyncInventory(CommerceProduct entity, List<ParfaitProductSizeInventoryEditorViewModel> inventory)
    {
        entity.InventoryItems.Clear();

        foreach (var item in NormalizeInventory(inventory))
        {
            entity.InventoryItems.Add(new CommerceProductInventoryItem
            {
                ExternalInventoryKey = item.Id,
                Size = item.Size,
                IsEnabled = item.IsEnabled,
                StockQuantity = item.StockQuantity,
                LowStockThreshold = item.LowStockThreshold,
                DisplayOrder = item.DisplayOrder
            });
        }
    }

    private static void SyncDiscounts(CommerceProduct entity, List<ParfaitProductDiscountCodeEditorViewModel> discounts)
    {
        entity.Discounts.Clear();

        foreach (var discount in NormalizeDiscountCodes(discounts))
        {
            entity.Discounts.Add(new CommerceProductDiscount
            {
                ExternalDiscountKey = discount.Id,
                Code = discount.Code,
                DiscountType = discount.DiscountType,
                Amount = discount.Amount,
                IsActive = discount.IsActive
            });
        }
    }

    private static void EnsureOnePrimaryImage(CommerceProduct product)
    {
        if (product.Images.Count == 0)
            return;

        foreach (var image in product.Images)
            image.IsPrimary = false;

        product.Images.OrderBy(image => image.DisplayOrder).First().IsPrimary = true;
    }

    private ParfaitProductDiscountCodeEditorViewModel? FindMatchingDiscount(ParfaitProductEditorViewModel product, string code, ParfaitCommerceSettingsViewModel settings)
        => FindActiveDiscount(product, code) ?? FindActiveGlobalDiscount(settings, code);

    private ParfaitProductDiscountCodeEditorViewModel? FindActiveDiscount(ParfaitProductEditorViewModel product, string code)
        => product.DiscountCodes.Select(NormalizeDiscountCode).FirstOrDefault(discount => IsDiscountAvailable(discount) && string.Equals(discount.Code, code, StringComparison.OrdinalIgnoreCase));

    private ParfaitProductDiscountCodeEditorViewModel? FindActiveGlobalDiscount(ParfaitCommerceSettingsViewModel settings, string code)
    {
        var discount = NormalizeDiscountCode(settings.GlobalDiscount ?? new ParfaitProductDiscountCodeEditorViewModel());
        return IsDiscountAvailable(discount) && string.Equals(discount.Code, code, StringComparison.OrdinalIgnoreCase) ? discount : null;
    }

    private ParfaitProductDiscountCodeEditorViewModel? FindPreferredDisplayDiscount(ParfaitProductEditorViewModel product, ParfaitCommerceSettingsViewModel settings)
    {
        if (product.PriceCents <= 0)
            return null;

        var productDiscount = product.DiscountCodes
            .Select(NormalizeDiscountCode)
            .Where(IsDiscountAvailable)
            .OrderByDescending(discount => CalculateDiscountCents(product.PriceCents, discount))
            .ThenBy(discount => discount.Code)
            .FirstOrDefault();

        if (productDiscount is not null && CalculateDiscountCents(product.PriceCents, productDiscount) > 0)
            return productDiscount;

        var globalDiscount = NormalizeDiscountCode(settings.GlobalDiscount ?? new ParfaitProductDiscountCodeEditorViewModel());
        return IsDiscountAvailable(globalDiscount) && CalculateDiscountCents(product.PriceCents, globalDiscount) > 0 ? globalDiscount : null;
    }

    private static int CalculateDiscountCents(int subtotalCents, ParfaitProductDiscountCodeEditorViewModel discount)
    {
        if (subtotalCents <= 0)
            return 0;

        if (string.Equals(discount.DiscountType, "Fixed", StringComparison.OrdinalIgnoreCase))
            return Math.Min(subtotalCents, (int)Math.Round(discount.Amount * 100m, MidpointRounding.AwayFromZero));

        var percent = Math.Clamp(discount.Amount, 0m, 100m);
        return Math.Min(subtotalCents, (int)Math.Round(subtotalCents * (percent / 100m), MidpointRounding.AwayFromZero));
    }

    private bool IsDiscountAvailable(ParfaitProductDiscountCodeEditorViewModel discount)
        => discount.IsActive && !string.IsNullOrWhiteSpace(discount.Code) && discount.Amount > 0;

    private ParfaitStoreProductViewModel MapStoreProduct(ParfaitProductEditorViewModel product, ParfaitCommerceSettingsViewModel settings)
    {
        var displayDiscount = FindPreferredDisplayDiscount(product, settings);
        var displayDiscountCents = displayDiscount is null ? 0 : CalculateDiscountCents(product.PriceCents, displayDiscount);
        var displayPriceCents = product.PriceCents > 0 ? Math.Max(0, product.PriceCents - displayDiscountCents) : 0;

        return new ParfaitStoreProductViewModel
        {
            Id = product.Id,
            Name = product.Name,
            Slug = product.Slug,
            Description = product.Description,
            PriceLabel = product.PriceCents > 0 ? $"${product.PriceCents / 100m:0.00}" : product.PriceLabel,
            PriceCents = product.PriceCents,
            CompareAtPriceCents = product.CompareAtPriceCents,
            DisplayPriceCents = displayPriceCents > 0 || displayDiscountCents > 0 ? displayPriceCents : product.PriceCents,
            DisplayCompareAtPriceCents = displayDiscountCents > 0 ? product.PriceCents : 0,
            DisplayDiscountLabel = displayDiscount is not null && displayDiscountCents > 0 ? displayDiscount.SummaryLabel : "",
            Badge = product.Badge,
            IsFeatured = product.IsFeatured,
            Images = product.Images.OrderBy(image => image.DisplayOrder).Select(image => new ParfaitStoreProductImageViewModel
            {
                Id = image.Id,
                ImageUrl = image.ImageUrl,
                AltText = image.AltText,
                IsPrimary = image.IsPrimary,
                DisplayOrder = image.DisplayOrder,
                ObjectFit = string.IsNullOrWhiteSpace(image.ObjectFit) ? "cover" : image.ObjectFit,
                ObjectPositionX = image.ObjectPositionX,
                ObjectPositionY = image.ObjectPositionY,
                Zoom = image.Zoom <= 0 ? 1.0m : image.Zoom
            }).ToList(),
            Sizes = product.InventoryBySize.Where(size => size.IsEnabled).OrderBy(size => size.DisplayOrder).Select(size => new ParfaitStoreProductSizeViewModel
            {
                Id = size.Id,
                Size = size.Size,
                IsEnabled = size.IsEnabled,
                StockQuantity = Math.Max(size.StockQuantity, 0),
                LowStockThreshold = Math.Max(1, size.LowStockThreshold),
                DisplayOrder = size.DisplayOrder
            }).ToList()
        };
    }

    private static ParfaitProductEditorViewModel NormalizeProduct(ParfaitProductEditorViewModel product, IReadOnlyList<ParfaitProductImageEditorViewModel>? existingImages = null)
    {
        var slug = string.IsNullOrWhiteSpace(product.Slug) ? product.Name : product.Slug;
        slug = slug.Trim().ToLowerInvariant().Replace(" ", "-");

        var normalizedPriceCents = Math.Max(0, product.PriceCents);
        var normalizedCompareAtCents = product.CompareAtPriceCents > normalizedPriceCents ? Math.Max(0, product.CompareAtPriceCents) : 0;

        return new ParfaitProductEditorViewModel
        {
            Id = string.IsNullOrWhiteSpace(product.Id) ? Guid.NewGuid().ToString("N") : product.Id,
            Name = product.Name.Trim(),
            Slug = slug,
            Description = product.Description.Trim(),
            PriceLabel = normalizedPriceCents > 0 ? $"${normalizedPriceCents / 100m:0.00}" : "Coming Soon",
            PriceCents = normalizedPriceCents,
            CompareAtPriceCents = normalizedCompareAtCents,
            Badge = string.IsNullOrWhiteSpace(product.Badge) ? "Parfait" : product.Badge.Trim(),
            IsFeatured = product.IsFeatured,
            IsActive = product.IsActive,
            DisplayOrder = product.DisplayOrder,
            Images = NormalizeImages(existingImages ?? product.Images, product.Name),
            InventoryBySize = NormalizeInventory(product.InventoryBySize),
            DiscountCodes = NormalizeDiscountCodes(product.DiscountCodes)
        };
    }

    private static List<ParfaitProductImageEditorViewModel> NormalizeImages(IReadOnlyList<ParfaitProductImageEditorViewModel>? images, string productName)
    {
        var normalized = (images ?? [])
            .OrderBy(image => image.DisplayOrder)
            .Select(image => new ParfaitProductImageEditorViewModel
            {
                Id = string.IsNullOrWhiteSpace(image.Id) ? Guid.NewGuid().ToString("N") : image.Id,
                ImageUrl = image.ImageUrl,
                FileName = image.FileName,
                AltText = string.IsNullOrWhiteSpace(image.AltText) ? productName.Trim() : image.AltText.Trim(),
                IsPrimary = image.IsPrimary,
                DisplayOrder = image.DisplayOrder,
                ObjectFit = string.IsNullOrWhiteSpace(image.ObjectFit) ? "cover" : image.ObjectFit,
                ObjectPositionX = image.ObjectPositionX,
                ObjectPositionY = image.ObjectPositionY,
                Zoom = image.Zoom <= 0 ? 1.0m : image.Zoom
            })
            .ToList();

        var wrapper = new ParfaitProductEditorViewModel { Images = normalized };
        EnsureOnePrimaryImage(wrapper);
        return wrapper.Images;
    }

    private static List<ParfaitProductSizeInventoryEditorViewModel> NormalizeInventory(List<ParfaitProductSizeInventoryEditorViewModel>? inventory)
    {
        var incoming = (inventory ?? [])
            .Select(size => new ParfaitProductSizeInventoryEditorViewModel
            {
                Id = string.IsNullOrWhiteSpace(size.Id) ? Guid.NewGuid().ToString("N") : size.Id,
                Size = ParfaitProductCatalogDefaults.NormalizeSize(size.Size),
                IsEnabled = size.IsEnabled,
                StockQuantity = Math.Max(0, size.StockQuantity),
                LowStockThreshold = Math.Max(1, size.LowStockThreshold),
                DisplayOrder = size.DisplayOrder
            })
            .Where(size => !string.IsNullOrWhiteSpace(size.Size))
            .GroupBy(size => size.Size, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(size => size.DisplayOrder).First())
            .ToList();

        var normalized = new List<ParfaitProductSizeInventoryEditorViewModel>();

        foreach (var standard in ParfaitProductCatalogDefaults.StandardSizes.Select((size, index) => new { Size = size, Index = index }))
        {
            var existing = incoming.FirstOrDefault(size => string.Equals(size.Size, standard.Size, StringComparison.OrdinalIgnoreCase));
            normalized.Add(existing ?? new ParfaitProductSizeInventoryEditorViewModel
            {
                Size = standard.Size,
                DisplayOrder = (standard.Index + 1) * 10,
                IsEnabled = true,
                StockQuantity = 0,
                LowStockThreshold = 20
            });
        }

        var custom = incoming
            .Where(size => !ParfaitProductCatalogDefaults.StandardSizes.Contains(size.Size, StringComparer.OrdinalIgnoreCase))
            .OrderBy(size => size.DisplayOrder)
            .ThenBy(size => size.Size)
            .ToList();

        var nextOrder = normalized.Count == 0 ? 10 : normalized.Max(size => size.DisplayOrder) + 10;
        foreach (var size in custom)
        {
            size.DisplayOrder = size.DisplayOrder <= 0 ? nextOrder : size.DisplayOrder;
            normalized.Add(size);
            nextOrder = Math.Max(nextOrder, size.DisplayOrder) + 10;
        }

        for (var index = 0; index < normalized.Count; index++)
        {
            if (normalized[index].DisplayOrder <= 0)
                normalized[index].DisplayOrder = (index + 1) * 10;
        }

        return normalized.OrderBy(size => size.DisplayOrder).ThenBy(size => size.Size).ToList();
    }

    private static List<ParfaitProductDiscountCodeEditorViewModel> NormalizeDiscountCodes(List<ParfaitProductDiscountCodeEditorViewModel>? codes)
    {
        return (codes ?? [])
            .Select(NormalizeDiscountCode)
            .Where(code => !string.IsNullOrWhiteSpace(code.Code) && code.Amount > 0)
            .GroupBy(code => code.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(code => code.Code)
            .ToList();
    }

    private static ParfaitProductDiscountCodeEditorViewModel NormalizeDiscountCode(ParfaitProductDiscountCodeEditorViewModel code)
    {
        var discountType = string.Equals(code.DiscountType, "Fixed", StringComparison.OrdinalIgnoreCase) ? "Fixed" : "Percent";
        var amount = discountType == "Fixed"
            ? Math.Round(Math.Max(0m, code.Amount), 2, MidpointRounding.AwayFromZero)
            : Math.Clamp(Math.Round(code.Amount, 2, MidpointRounding.AwayFromZero), 0m, 100m);

        return new ParfaitProductDiscountCodeEditorViewModel
        {
            Id = string.IsNullOrWhiteSpace(code.Id) ? Guid.NewGuid().ToString("N") : code.Id,
            Code = ParfaitProductCatalogDefaults.NormalizeDiscountCode(code.Code),
            DiscountType = discountType,
            Amount = amount,
            IsActive = code.IsActive
        };
    }

    private static ParfaitCommerceSettingsViewModel NormalizeCommerceSettings(ParfaitCommerceSettingsViewModel settings)
    {
        var normalizedDiscount = NormalizeDiscountCode(settings.GlobalDiscount ?? new ParfaitProductDiscountCodeEditorViewModel());

        return new ParfaitCommerceSettingsViewModel
        {
            ShippingFeeCents = Math.Max(0, settings.ShippingFeeCents),
            TaxPercent = Math.Clamp(Math.Round(settings.TaxPercent, 2, MidpointRounding.AwayFromZero), 0m, 100m),
            GlobalDiscount = normalizedDiscount
        };
    }

    private static void EnsureOnePrimaryImage(ParfaitProductEditorViewModel product)
    {
        if (product.Images.Count == 0)
            return;

        foreach (var image in product.Images)
            image.IsPrimary = false;

        product.Images.OrderBy(image => image.DisplayOrder).First().IsPrimary = true;
    }
}
