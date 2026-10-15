using Domain.Entities;

namespace Infrastructure.WebsiteEditing;

/// <summary>
/// One canonical inventory of scoped media referenced by a website document.
/// Publication preflight and public media authorization must use this same graph
/// traversal so MediaAssetId, canonical media URLs, shell media, reusable
/// components, and the published favicon cannot drift into separate rules.
/// </summary>
public static class WebsiteMediaReferenceCatalog
{
    private const string MediaPathMarker = "/api/website-content/media/";

    public static HashSet<Guid> Collect(WebsiteContentDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var ids = new HashSet<Guid>();
        AddUrl(ids, document.FaviconImageDataUrl);

        Visit(document.Shell.Header, ids);
        Visit(document.Shell.Footer, ids);

        foreach (var page in document.Pages.Values)
            Visit(page.Composition, ids);

        foreach (var component in document.ReusableComponents.Values)
            Visit(component.Composition, ids);

        return ids;
    }

    public static bool References(WebsiteContentDocument document, Guid assetId) =>
        assetId != Guid.Empty && Collect(document).Contains(assetId);

    private static void Visit(IEnumerable<WebsiteCompositionNode>? nodes, HashSet<Guid> ids)
    {
        foreach (var node in nodes ?? [])
        {
            if (node.MediaAssetId is Guid assetId && assetId != Guid.Empty)
                ids.Add(assetId);

            AddUrl(ids, node.MediaUrl);
            Visit(node.Children, ids);
        }
    }

    private static void AddUrl(HashSet<Guid> ids, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var uri))
            return;

        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : value.Split('?', '#')[0];
        var index = path.IndexOf(MediaPathMarker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return;

        var idText = path[(index + MediaPathMarker.Length)..].Trim('/');
        if (Guid.TryParse(idText, out var id) && id != Guid.Empty)
            ids.Add(id);
    }
}
