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
    private readonly object _lock = new();

    public ParfaitProductService(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    private string DataPath => Path.Combine(_environment.ContentRootPath, "App_Data", "parfait-products.json");
    private string UploadRoot => Path.Combine(_environment.WebRootPath, "uploads", "parfait-products");

    public IReadOnlyList<ParfaitProductEditorViewModel> GetAllProducts()
    {
        EnsureSeedData();

        lock (_lock)
        {
            var json = File.ReadAllText(DataPath);
            return JsonSerializer.Deserialize<List<ParfaitProductEditorViewModel>>(json) ?? [];
        }
    }

    public IReadOnlyList<ParfaitStoreProductViewModel> GetActiveStoreProducts()
    {
        return GetAllProducts()
            .Where(p => p.IsActive)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .Select(p => new ParfaitStoreProductViewModel
            {
                Id = p.Id,
                Name = p.Name,
                Slug = p.Slug,
                Description = p.Description,
                PriceLabel = p.PriceLabel,
                Badge = p.Badge,
                IsFeatured = p.IsFeatured,
                Images = p.Images
                    .OrderBy(i => i.DisplayOrder)
                    .Select(i => new ParfaitStoreProductImageViewModel
                    {
                        Id = i.Id,
                        ImageUrl = i.ImageUrl,
                        AltText = i.AltText,
                        IsPrimary = i.IsPrimary,
                        DisplayOrder = i.DisplayOrder
                    })
                    .ToList()
            })
            .ToList();
    }

    public void SaveProduct(ParfaitProductEditorViewModel product)
    {
        var products = GetAllProducts().ToList();
        var existingIndex = products.FindIndex(p => p.Id == product.Id);
        var existingImages = existingIndex >= 0 ? products[existingIndex].Images : [];

        var normalized = NormalizeProduct(product, existingImages);

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
        var product = products.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        if (product is not null)
        {
            var productFolder = Path.Combine(UploadRoot, product.Id);
            if (Directory.Exists(productFolder))
            {
                Directory.Delete(productFolder, recursive: true);
            }
        }

        SaveAll(products.Where(p => !string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    public async Task UploadImagesAsync(string productId, IReadOnlyList<IFormFile> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        var products = GetAllProducts().ToList();
        var product = products.FirstOrDefault(p => string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));

        if (product is null)
        {
            return;
        }

        var productFolder = Path.Combine(UploadRoot, product.Id);
        Directory.CreateDirectory(productFolder);

        var nextOrder = product.Images.Count == 0 ? 10 : product.Images.Max(i => i.DisplayOrder) + 10;

        foreach (var file in files.Where(f => f.Length > 0))
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
                DisplayOrder = nextOrder
            });

            nextOrder += 10;
        }

        EnsureOnePrimaryImage(product);
        SaveAll(products);
    }

    public void DeleteImage(string productId, string imageId)
    {
        var products = GetAllProducts().ToList();
        var product = products.FirstOrDefault(p => string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));

        if (product is null)
        {
            return;
        }

        var image = product.Images.FirstOrDefault(i => string.Equals(i.Id, imageId, StringComparison.OrdinalIgnoreCase));

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

    public void SetPrimaryImage(string productId, string imageId)
    {
        var products = GetAllProducts().ToList();
        var product = products.FirstOrDefault(p => string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));

        if (product is null)
        {
            return;
        }

        foreach (var image in product.Images)
        {
            image.IsPrimary = string.Equals(image.Id, imageId, StringComparison.OrdinalIgnoreCase);
        }

        SaveAll(products);
    }

    public void MoveImage(string productId, string imageId, string direction)
    {
        var products = GetAllProducts().ToList();
        var product = products.FirstOrDefault(p => string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));

        if (product is null)
        {
            return;
        }

        var ordered = product.Images.OrderBy(i => i.DisplayOrder).ToList();
        var index = ordered.FindIndex(i => string.Equals(i.Id, imageId, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            return;
        }

        var swapIndex = string.Equals(direction, "up", StringComparison.OrdinalIgnoreCase)
            ? index - 1
            : index + 1;

        if (swapIndex < 0 || swapIndex >= ordered.Count)
        {
            return;
        }

        (ordered[index].DisplayOrder, ordered[swapIndex].DisplayOrder) = (ordered[swapIndex].DisplayOrder, ordered[index].DisplayOrder);

        SaveAll(products);
    }

    private static ParfaitProductEditorViewModel NormalizeProduct(
        ParfaitProductEditorViewModel product,
        IReadOnlyList<ParfaitProductImageEditorViewModel> existingImages)
    {
        var slug = string.IsNullOrWhiteSpace(product.Slug) ? product.Name : product.Slug;
        slug = slug.Trim().ToLowerInvariant().Replace(" ", "-");

        return new ParfaitProductEditorViewModel
        {
            Id = string.IsNullOrWhiteSpace(product.Id) ? Guid.NewGuid().ToString("N") : product.Id,
            Name = product.Name.Trim(),
            Slug = slug,
            Description = product.Description.Trim(),
            PriceLabel = string.IsNullOrWhiteSpace(product.PriceLabel) ? "Coming Soon" : product.PriceLabel.Trim(),
            Badge = string.IsNullOrWhiteSpace(product.Badge) ? "Parfait" : product.Badge.Trim(),
            IsFeatured = product.IsFeatured,
            IsActive = product.IsActive,
            DisplayOrder = product.DisplayOrder,
            Images = existingImages
                .OrderBy(i => i.DisplayOrder)
                .Select(i => new ParfaitProductImageEditorViewModel
                {
                    Id = i.Id,
                    ImageUrl = i.ImageUrl,
                    FileName = i.FileName,
                    AltText = string.IsNullOrWhiteSpace(i.AltText) ? product.Name.Trim() : i.AltText.Trim(),
                    IsPrimary = i.IsPrimary,
                    DisplayOrder = i.DisplayOrder
                })
                .ToList()
        };
    }

    private static void EnsureOnePrimaryImage(ParfaitProductEditorViewModel product)
    {
        if (product.Images.Count == 0)
        {
            return;
        }

        if (product.Images.Count(i => i.IsPrimary) == 1)
        {
            return;
        }

        foreach (var image in product.Images)
        {
            image.IsPrimary = false;
        }

        product.Images.OrderBy(i => i.DisplayOrder).First().IsPrimary = true;
    }

    private void SaveAll(List<ParfaitProductEditorViewModel> products)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
        Directory.CreateDirectory(UploadRoot);

        foreach (var product in products)
        {
            EnsureOnePrimaryImage(product);
        }

        var ordered = products
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToList();

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
    }
}
