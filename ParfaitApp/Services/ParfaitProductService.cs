using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ParfaitApp.Models;

namespace ParfaitApp.Services;

public sealed class ParfaitProductService
{
    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif"
    };

    private readonly IWebHostEnvironment _environment;
    private readonly ParfaitOrderService _orders;
    private readonly object _lock = new();

    public ParfaitProductService(IWebHostEnvironment environment, ParfaitOrderService orders)
    {
        _environment = environment;
        _orders = orders;
    }

    private string DataPath => Path.Combine(_environment.ContentRootPath, "App_Data", "parfait-products.json");
    private string CommerceSettingsPath => Path.Combine(_environment.ContentRootPath, "App_Data", "parfait-commerce-settings.json");
    private string UploadRoot => Path.Combine(_environment.WebRootPath, "uploads", "parfait-products");

    public IReadOnlyList<ParfaitProductEditorViewModel> GetAllProducts()
    {
        EnsureSeedData();

        lock (_lock)
        {
            var json = File.ReadAllText(DataPath);
            var products = JsonSerializer.Deserialize<List<ParfaitProductEditorViewModel>>(json) ?? [];
            return products
                .Select(product => NormalizeProduct(product))
                .OrderBy(product => product.DisplayOrder)
                .ThenBy(product => product.Name)
                .ToList();
        }
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
            {
                continue;
            }

            if (!products.TryGetValue(item.Id.Trim(), out var product) || product.PriceCents <= 0)
            {
                quote.Messages.Add("A product in the cart is no longer available.");
                quote.IsValid = false;
                continue;
            }

            var requestedQuantity = Math.Clamp(item.Quantity, 1, 20);
            var normalizedSize = ParfaitProductCatalogDefaults.NormalizeSize(item.Size);
            var selectedSize = string.IsNullOrWhiteSpace(normalizedSize)
                ? product.InventoryBySize
                    .OrderBy(size => size.DisplayOrder)
                    .FirstOrDefault(size => size.IsEnabled)
                : product.InventoryBySize
                    .OrderBy(size => size.DisplayOrder)
                    .FirstOrDefault(size => string.Equals(size.Size, normalizedSize, StringComparison.OrdinalIgnoreCase));

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
                ImageUrl = product.Images
                    .OrderBy(image => image.DisplayOrder)
                    .FirstOrDefault(image => image.IsPrimary)?.ImageUrl
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
        quote.Messages = quote.Messages
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!quote.IsValid && string.IsNullOrWhiteSpace(quote.Error))
        {
            quote.Error = quote.Messages.FirstOrDefault() ?? "The cart needs attention before checkout.";
        }

        return quote;
    }

    public ParfaitCommerceSettingsViewModel GetCommerceSettings()
    {
        EnsureCommerceSettingsData();

        lock (_lock)
        {
            var json = File.ReadAllText(CommerceSettingsPath);
            var settings = JsonSerializer.Deserialize<ParfaitCommerceSettingsViewModel>(json) ?? new ParfaitCommerceSettingsViewModel();
            return NormalizeCommerceSettings(settings);
        }
    }

    public void SaveCommerceSettings(ParfaitCommerceSettingsViewModel settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CommerceSettingsPath)!);
        var normalized = NormalizeCommerceSettings(settings);

        lock (_lock)
        {
            File.WriteAllText(
                CommerceSettingsPath,
                JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public void SaveProduct(ParfaitProductEditorViewModel product)
    {
        var products = GetAllProducts().ToList();
        var existingIndex = products.FindIndex(existing => existing.Id == product.Id);
        var existingImages = existingIndex >= 0 ? products[existingIndex].Images : [];

        var normalized = NormalizeProduct(product, existingImages);
        if (existingIndex >= 0)
        {
            normalized.DisplayOrder = products[existingIndex].DisplayOrder > 0
                ? products[existingIndex].DisplayOrder
                : normalized.DisplayOrder;
        }
        else if (normalized.DisplayOrder <= 0)
        {
            normalized.DisplayOrder = products.Count == 0
                ? 10
                : products.Max(existing => Math.Max(existing.DisplayOrder, 0)) + 10;
        }

        if (existingIndex >= 0)
        {
            products[existingIndex] = normalized;
        }
        else
        {
            products.Add(normalized);
        }

        SaveAll(products);
    }

    public void DeleteProduct(string id)
    {
        var products = GetAllProducts().ToList();
        var product = products.FirstOrDefault(existing => string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase));

        if (product is not null)
        {
            var productFolder = Path.Combine(UploadRoot, product.Id);
            if (Directory.Exists(productFolder))
            {
                Directory.Delete(productFolder, recursive: true);
            }
        }

        SaveAll(products.Where(product => !string.Equals(product.Id, id, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    public void ReorderProducts(IReadOnlyList<string> productIds)
    {
        var products = GetAllProducts().ToList();
        if (productIds.Count == 0 || products.Count == 0)
        {
            return;
        }

        var lookup = products.ToDictionary(product => product.Id, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ParfaitProductEditorViewModel>();

        foreach (var productId in productIds)
        {
            if (lookup.TryGetValue(productId, out var product) && !ordered.Contains(product))
            {
                ordered.Add(product);
            }
        }

        ordered.AddRange(products.Where(product => !ordered.Contains(product)));

        for (var index = 0; index < ordered.Count; index++)
        {
            ordered[index].DisplayOrder = (index + 1) * 10;
        }

        SaveAll(ordered);
    }

    public async Task UploadImagesAsync(string productId, IReadOnlyList<IFormFile> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        var products = GetAllProducts().ToList();
        var product = products.FirstOrDefault(existing => string.Equals(existing.Id, productId, StringComparison.OrdinalIgnoreCase));

        if (product is null)
        {
            return;
        }

        var productFolder = Path.Combine(UploadRoot, product.Id);
        Directory.CreateDirectory(productFolder);

        var nextOrder = product.Images.Count == 0 ? 10 : product.Images.Max(image => image.DisplayOrder) + 10;

        foreach (var file in files.Where(file => file.Length > 0))
        {
            var extension = Path.GetExtension(file.FileName);

            if (!AllowedImageExtensions.Contains(extension))
            {
                continue;
            }

            var imageId = Guid.NewGuid().ToString("N");
            var safeFileName = $"{imageId}{extension.ToLowerInvariant()}";
            var physicalPath = Path.Combine(productFolder, safeFileName);

            await using (var stream = File.Create(physicalPath))
            {
                await file.CopyToAsync(stream);
            }

            product.Images.Add(new ParfaitProductImageEditorViewModel
            {
                Id = imageId,
                FileName = safeFileName,
                ImageUrl = $"/uploads/parfait-products/{product.Id}/{safeFileName}",
                AltText = product.Name,
                IsPrimary = product.Images.Count == 0,
                DisplayOrder = nextOrder,
                ObjectFit = "cover",
                ObjectPositionX = 50,
                ObjectPositionY = 50,
                Zoom = 1.0m
            });

            nextOrder += 10;
        }

        EnsureOnePrimaryImage(product);
        SaveAll(products);
    }

    public void DeleteImage(string productId, string imageId)
    {
        var products = GetAllProducts().ToList();
        var product = products.FirstOrDefault(existing => string.Equals(existing.Id, productId, StringComparison.OrdinalIgnoreCase));

        if (product is null)
        {
            return;
        }

        var image = product.Images.FirstOrDefault(existing => string.Equals(existing.Id, imageId, StringComparison.OrdinalIgnoreCase));

        if (image is null)
        {
            return;
        }

        var physicalPath = Path.Combine(_environment.WebRootPath, image.ImageUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        if (File.Exists(physicalPath))
        {
            File.Delete(physicalPath);
        }

        product.Images.Remove(image);
        EnsureOnePrimaryImage(product);
        SaveAll(products);
    }

    public void ReorderImages(string productId, IReadOnlyList<string> imageIds)
    {
        var products = GetAllProducts().ToList();
        var product = products.FirstOrDefault(existing => string.Equals(existing.Id, productId, StringComparison.OrdinalIgnoreCase));

        if (product is null || imageIds.Count == 0)
        {
            return;
        }

        var lookup = product.Images.ToDictionary(image => image.Id, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ParfaitProductImageEditorViewModel>();

        foreach (var imageId in imageIds)
        {
            if (lookup.TryGetValue(imageId, out var image) && !ordered.Contains(image))
            {
                ordered.Add(image);
            }
        }

        ordered.AddRange(product.Images.Where(image => !ordered.Contains(image)));

        for (var index = 0; index < ordered.Count; index++)
        {
            ordered[index].DisplayOrder = (index + 1) * 10;
        }

        SaveAll(products);
    }

    public void SaveImageDisplaySettings(string productId, string imageId, string objectFit, int objectPositionX, int objectPositionY, decimal zoom)
    {
        var products = GetAllProducts().ToList();
        var product = products.FirstOrDefault(existing => string.Equals(existing.Id, productId, StringComparison.OrdinalIgnoreCase));

        if (product is null)
        {
            return;
        }

        var image = product.Images.FirstOrDefault(existing => string.Equals(existing.Id, imageId, StringComparison.OrdinalIgnoreCase));

        if (image is null)
        {
            return;
        }

        image.ObjectFit = string.Equals(objectFit, "contain", StringComparison.OrdinalIgnoreCase) ? "contain" : "cover";
        image.ObjectPositionX = Math.Clamp(objectPositionX, 0, 100);
        image.ObjectPositionY = Math.Clamp(objectPositionY, 0, 100);
        image.Zoom = Math.Clamp(zoom, 1.0m, 2.5m);

        SaveAll(products);
    }

    public void CommitPaidInventory(IReadOnlyList<ParfaitValidatedCartItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var products = GetAllProducts().ToList();
        var updated = false;

        foreach (var item in items)
        {
            var product = products.FirstOrDefault(existing => string.Equals(existing.Id, item.Id, StringComparison.OrdinalIgnoreCase));
            if (product is null)
            {
                continue;
            }

            var size = product.InventoryBySize.FirstOrDefault(existing =>
                string.Equals(existing.Size, ParfaitProductCatalogDefaults.NormalizeSize(item.Size), StringComparison.OrdinalIgnoreCase));

            if (size is null)
            {
                continue;
            }

            size.StockQuantity = Math.Max(0, size.StockQuantity - Math.Max(item.Quantity, 0));
            updated = true;
        }

        if (updated)
        {
            SaveAll(products);
        }
    }

    private ParfaitProductDiscountCodeEditorViewModel? FindMatchingDiscount(
        ParfaitProductEditorViewModel product,
        string code,
        ParfaitCommerceSettingsViewModel settings)
    {
        return FindActiveDiscount(product, code) ?? FindActiveGlobalDiscount(settings, code);
    }

    private ParfaitProductDiscountCodeEditorViewModel? FindActiveDiscount(ParfaitProductEditorViewModel product, string code)
    {
        return product.DiscountCodes
            .Select(NormalizeDiscountCode)
            .FirstOrDefault(discount => IsDiscountAvailable(discount)
                && string.Equals(discount.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    private ParfaitProductDiscountCodeEditorViewModel? FindActiveGlobalDiscount(ParfaitCommerceSettingsViewModel settings, string code)
    {
        var discount = NormalizeDiscountCode(settings.GlobalDiscount ?? new ParfaitProductDiscountCodeEditorViewModel());
        return IsDiscountAvailable(discount) && string.Equals(discount.Code, code, StringComparison.OrdinalIgnoreCase)
            ? discount
            : null;
    }

    private ParfaitProductDiscountCodeEditorViewModel? FindPreferredDisplayDiscount(
        ParfaitProductEditorViewModel product,
        ParfaitCommerceSettingsViewModel settings)
    {
        if (product.PriceCents <= 0)
        {
            return null;
        }

        var productDiscount = product.DiscountCodes
            .Select(NormalizeDiscountCode)
            .Where(IsDiscountAvailable)
            .OrderByDescending(discount => CalculateDiscountCents(product.PriceCents, discount))
            .ThenBy(discount => discount.Code)
            .FirstOrDefault();

        if (productDiscount is not null && CalculateDiscountCents(product.PriceCents, productDiscount) > 0)
        {
            return productDiscount;
        }

        var globalDiscount = NormalizeDiscountCode(settings.GlobalDiscount ?? new ParfaitProductDiscountCodeEditorViewModel());
        return IsDiscountAvailable(globalDiscount) && CalculateDiscountCents(product.PriceCents, globalDiscount) > 0
            ? globalDiscount
            : null;
    }

    private static int CalculateDiscountCents(int subtotalCents, ParfaitProductDiscountCodeEditorViewModel discount)
    {
        if (subtotalCents <= 0)
        {
            return 0;
        }

        if (string.Equals(discount.DiscountType, "Fixed", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Min(subtotalCents, (int)Math.Round(discount.Amount * 100m, MidpointRounding.AwayFromZero));
        }

        var percent = Math.Clamp(discount.Amount, 0m, 100m);
        return Math.Min(subtotalCents, (int)Math.Round(subtotalCents * (percent / 100m), MidpointRounding.AwayFromZero));
    }

    private bool IsDiscountAvailable(ParfaitProductDiscountCodeEditorViewModel discount)
    {
        return discount.IsActive
            && !string.IsNullOrWhiteSpace(discount.Code)
            && discount.Amount > 0;
    }

    private ParfaitStoreProductViewModel MapStoreProduct(
        ParfaitProductEditorViewModel product,
        ParfaitCommerceSettingsViewModel settings)
    {
        var displayDiscount = FindPreferredDisplayDiscount(product, settings);
        var displayDiscountCents = displayDiscount is null
            ? 0
            : CalculateDiscountCents(product.PriceCents, displayDiscount);
        var displayPriceCents = product.PriceCents > 0
            ? Math.Max(0, product.PriceCents - displayDiscountCents)
            : 0;

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
            DisplayDiscountLabel = displayDiscount is not null && displayDiscountCents > 0
                ? displayDiscount.SummaryLabel
                : "",
            Badge = product.Badge,
            IsFeatured = product.IsFeatured,
            Images = product.Images
                .OrderBy(image => image.DisplayOrder)
                .Select(image => new ParfaitStoreProductImageViewModel
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
                })
                .ToList(),
            Sizes = product.InventoryBySize
                .Where(size => size.IsEnabled)
                .OrderBy(size => size.DisplayOrder)
                .Select(size => new ParfaitStoreProductSizeViewModel
                {
                    Id = size.Id,
                    Size = size.Size,
                    IsEnabled = size.IsEnabled,
                    StockQuantity = Math.Max(size.StockQuantity, 0),
                    LowStockThreshold = Math.Max(1, size.LowStockThreshold),
                    DisplayOrder = size.DisplayOrder
                })
                .ToList()
        };
    }

    private static ParfaitProductEditorViewModel NormalizeProduct(
        ParfaitProductEditorViewModel product,
        IReadOnlyList<ParfaitProductImageEditorViewModel>? existingImages = null)
    {
        var slug = string.IsNullOrWhiteSpace(product.Slug) ? product.Name : product.Slug;
        slug = slug.Trim().ToLowerInvariant().Replace(" ", "-");

        var normalizedPriceCents = Math.Max(0, product.PriceCents);
        var normalizedCompareAtCents = product.CompareAtPriceCents > normalizedPriceCents
            ? Math.Max(0, product.CompareAtPriceCents)
            : 0;

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

    private static List<ParfaitProductImageEditorViewModel> NormalizeImages(
        IReadOnlyList<ParfaitProductImageEditorViewModel>? images,
        string productName)
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
            {
                normalized[index].DisplayOrder = (index + 1) * 10;
            }
        }

        return normalized
            .OrderBy(size => size.DisplayOrder)
            .ThenBy(size => size.Size)
            .ToList();
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
        {
            return;
        }

        foreach (var image in product.Images)
        {
            image.IsPrimary = false;
        }

        product.Images.OrderBy(image => image.DisplayOrder).First().IsPrimary = true;
    }

    private void SaveAll(List<ParfaitProductEditorViewModel> products)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
        Directory.CreateDirectory(UploadRoot);

        var ordered = products
            .Select(product => NormalizeProduct(product))
            .OrderBy(product => product.DisplayOrder)
            .ThenBy(product => product.Name)
            .ToList();

        foreach (var product in ordered)
        {
            EnsureOnePrimaryImage(product);
        }

        lock (_lock)
        {
            File.WriteAllText(
                DataPath,
                JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private void EnsureSeedData()
    {
        if (File.Exists(DataPath))
        {
            EnsureCommerceSettingsData();
            return;
        }

        SaveAll(
        [
            new()
            {
                Name = "Parfait Signature Package",
                Slug = "parfait-signature-package",
                Description = "A premium starter package for the Parfait brand experience.",
                PriceLabel = "Coming Soon",
                Badge = "Featured",
                IsFeatured = true,
                IsActive = true,
                DisplayOrder = 10
            },
            new()
            {
                Name = "Parfait Training Package",
                Slug = "parfait-training-package",
                Description = "Training-focused offer placeholder for the owned Parfait store.",
                PriceLabel = "Coming Soon",
                Badge = "Training",
                IsActive = true,
                DisplayOrder = 20
            },
            new()
            {
                Name = "Parfait Lifestyle Package",
                Slug = "parfait-lifestyle-package",
                Description = "Lifestyle product placeholder managed by Parfait internal commerce.",
                PriceLabel = "Coming Soon",
                Badge = "Lifestyle",
                IsActive = true,
                DisplayOrder = 30
            }
        ]);

        EnsureCommerceSettingsData();
    }

    private void EnsureCommerceSettingsData()
    {
        if (File.Exists(CommerceSettingsPath))
        {
            return;
        }

        SaveCommerceSettings(new ParfaitCommerceSettingsViewModel());
    }
}
