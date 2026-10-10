using Infrastructure.Data;
using Infrastructure.WebsiteRuntime;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Services;

namespace Legend.Commerce;

/// <summary>
/// Serve migrated product image bytes only for a published, verified business
/// website preview. Middleware and storage share the canonical tenant ID;
/// never expose arbitrary local files or resolve catalog media cross-tenant.
/// </summary>
public sealed class CommercePreviewMediaMiddleware(RequestDelegate next)
{
    private const string Prefix = "/uploads/parfait-products/";

    public async Task InvokeAsync(HttpContext context, MasterAppDbContext db, ParfaitStoragePaths storage)
    {
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        // No direct-origin / provider hostname access; admission is set only
        // after verified business binding and published content checks.
        if (!context.Items.TryGetValue(CommerceSharedHostPreviewGate.PreviewMediaBusinessIdItem, out var admitted)
            || admitted is not Guid businessId || businessId == Guid.Empty
            || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            || !IsSafeImagePath(path))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var knownProductImage = await db.CommerceProductImages.AsNoTracking().AnyAsync(
            img => img.ImageUrl == path && db.CommerceProducts.AsNoTracking().Any(
                product => product.Id == img.CommerceProductId &&
                           product.CommerceBusinessId == businessId), context.RequestAborted);
        if (!knownProductImage)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var verifiedFile = storage.ResolveImagePhysicalPaths(path)
            .FirstOrDefault(candidate => IsNonLinkedFile(candidate, storage.UploadRoot)
                || IsNonLinkedFile(candidate, storage.LegacyUploadRoot));
        if (verifiedFile is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.Headers.CacheControl = "private,no-store,max-age=0";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.ContentType = ContentType(path);
        if (!HttpMethods.IsHead(context.Request.Method))
            await context.Response.SendFileAsync(verifiedFile, context.RequestAborted);
    }

    private static bool IsSafeImagePath(string path)
    {
        var segments = path[Prefix.Length..].Split('/');
        if (segments.Length < 2 || segments.Any(s => s.Length is < 1 or > 255 ||
            s is "." or ".." || s.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')))
            return false;
        return ContentType(path) != "";
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => ""
    };

    private static bool IsNonLinkedFile(string candidate, string root)
    {
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate);
        if (!full.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(full)) return false;
        try
        {
            for (var dir = Path.GetDirectoryName(full);
                 dir is not null && dir.Length >= canonicalRoot.Length;
                 dir = Path.GetDirectoryName(dir))
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    return false;
            }
            return (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
