using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Infrastructure.WebsiteEditing;

/// <summary>Published snapshots are immutable. Draft changes never alter the public pointer.</summary>
public static class WebsiteContentStore
{
    // An allowlisted reading of the immutable publication, never draft state,
    // form submissions, bindings, media bytes, credentials or runtime records.
    public static WebsitePublishedTextProjection ProjectPublishedText(string json)
    {
        var document = WebsiteContentSanitizer.ReadPersisted(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var remaining = 6000;
        var visited = 0;
        var partial = false;
        string? Take(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            var length = Math.Min(text.Length, Math.Min(remaining, 1000));
            if (length < text.Length) partial = true;
            remaining -= length;
            return length == 0 ? null : text[..length];
        }
        var pages = new List<WebsitePublishedPageText>();
        foreach (var page in document.Pages.OrderBy(x => x.Key, StringComparer.Ordinal).Take(12))
        {
            var title = Take(page.Value.Title);
            var description = Take(page.Value.Description);
            var fragments = new List<string>();
            void Visit(IEnumerable<WebsiteCompositionNode> nodes, int depth)
            {
                foreach (var node in nodes)
                {
                    if (++visited > 256 || depth > 32 || remaining == 0) { partial = true; return; }
                    if (node.Hidden == true) continue;
                    foreach (var value in new[] { node.Title, node.Text, node.Alt })
                        if (Take(value) is { } text) fragments.Add(text);
                    Visit(node.Children, depth + 1);
                }
            }
            Visit(page.Value.Composition, 0);
            pages.Add(new(page.Key, title, description, fragments));
        }
        if (document.Pages.Count > 12) partial = true;
        return new(pages, partial, "published_page_text_only; dynamic_data_forms_shell_and_reusable_content_not_expanded");
    }
    // Immutable publication payloads are also large SQL text values. Reuse the
    // StateAsync large-LOB workaround without changing predicates, cardinality,
    // command timeout, or async writes. The blocking read cannot cancel midway;
    // never return its payload if request cancellation arrived while it ran.
    public static T ReadImmutablePublication<T>(Func<T> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = read();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }


    public static async Task<WebsiteContentVersion?> PublishedBusinessAsync(MasterAppDbContext db, Guid businessId, CancellationToken cancellationToken = default)
    {
        if (!await db.CommerceBusinesses.AnyAsync(b => b.Id == businessId && b.IsActive && b.Status == "Active", cancellationToken)) return null;
        var owner = WebsiteEditorSiteKeys.BusinessOwnerKey(businessId);
        return await (from state in db.Set<WebsiteContentState>().AsNoTracking()
                      join version in db.Set<WebsiteContentVersion>().AsNoTracking() on state.PublishedVersionId equals version.Id
                      where state.OwnerKey == owner && state.SiteKey == WebsiteEditorSiteKeys.Business && version.StateId == state.Id
                      select version).SingleOrDefaultAsync(cancellationToken);
    }
}

public sealed record WebsitePublishedPageText(string Route, string? Title, string? Description, IReadOnlyList<string> Text);
public sealed record WebsitePublishedTextProjection(IReadOnlyList<WebsitePublishedPageText> Pages, bool Partial, string Coverage);
