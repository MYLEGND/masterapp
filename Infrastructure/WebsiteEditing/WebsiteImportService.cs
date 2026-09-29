using System.Net;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Infrastructure.Messaging;

namespace Infrastructure.WebsiteEditing;

public sealed record WebsiteImportPage(string Url, string Sha256, DateTime RetrievedUtc, string Title, string[] Links, string[] Images, int Forms, string[] Warnings, string[] TextBlocks);
public sealed record WebsiteImportReport(string SourceUrl, DateTime CreatedUtc, IReadOnlyList<WebsiteImportPage> Pages, IReadOnlyList<string> Warnings, int AddedComponents, int PreservedComponents);
public sealed record WebsiteImportResult(WebsiteContentDocument Document, WebsiteImportReport Report);

/// <summary>
/// Bounded public content import/export for the canonical v3 composition graph.
/// Pre-v3 override documents are read-only migration inputs and are never written here.
/// </summary>
public sealed class WebsiteImportService(WebsiteMediaService media)
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static MatchCollection Matches(string text, string pattern) =>
        Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, PatternTimeout);

    private static string Text(string html) =>
        WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", " ", RegexOptions.Singleline, PatternTimeout)).Trim();

    public async Task<WebsiteImportResult> PrepareAsync(
        string sourceUrl,
        WebsiteContentDocument existing,
        bool authorized,
        string ownerKey,
        string mediaBaseUrl,
        CancellationToken ct = default)
    {
        if (!authorized) throw new UnauthorizedAccessException("Website owner authorization is required before importing.");
        if (existing.LegacyMigration is not null)
            throw new InvalidOperationException("Materialize the existing website into canonical v3 before importing content.");

        var normalized = LegendConnectResearchNetworkPolicy.NormalizePublicHttpUri(sourceUrl);
        if (normalized is null) throw new ArgumentException("Enter a public website address.");

        var origin = new Uri(normalized);
        var draft = WebsiteContentSanitizer.Sanitize(existing);
        var pages = new List<WebsiteImportPage>();
        var warnings = new List<string>
        {
            "Review imported content before publishing. Private data, booking, payments, forms, scripts, and third-party integrations are not transferred."
        };
        var queue = new Queue<Uri>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue(origin);
        var added = 0;
        var preserved = 0;

        using var handler = LegendConnectResearchNetworkPolicy.CreatePublicReadOnlyHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LEGEND-Website-Import/2.0");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));

        while (queue.Count > 0 && pages.Count < 20)
        {
            var uri = queue.Dequeue();
            if (!visited.Add(uri.AbsoluteUri)) continue;

            string html;
            try { html = await ReadAsync(client, uri, deadline.Token); }
            catch (HttpRequestException)
            {
                warnings.Add("Could not import " + uri.AbsoluteUri);
                continue;
            }

            // Imported source code never enters the website graph.
            var safe = Regex.Replace(
                html,
                "<(script|style|noscript|template)\\b[^>]*>.*?</\\1\\s*>",
                "",
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                PatternTimeout);

            var titleMatch = Matches(safe, "<title\\b[^>]*>(.*?)</title>").Cast<Match>().FirstOrDefault();
            var title = titleMatch is null ? uri.AbsolutePath : Text(titleMatch.Groups[1].Value);
            var linkLabels = ExtractLinks(safe, uri)
                .GroupBy(value => value.Url)
                .ToDictionary(group => group.Key, group => group.First().Label);
            var links = linkLabels.Keys.Take(160).ToArray();
            var images = ExtractUrls(safe, "img", "src", uri).Distinct().Take(50).ToArray();
            var forms = Matches(safe, "<form\\b").Count;
            var pageWarnings = new List<string>();
            if (forms > 0)
                pageWarnings.Add("Forms require the canonical scoped inquiry authority and are not imported.");
            if (Matches(safe, "<(video|iframe)\\b").Count > 0)
                pageWarnings.Add("Video and embedded integrations require review and are not imported automatically.");

            var route = uri.AbsolutePath.TrimEnd('/');
            if (route.Length == 0) route = "/";
            if (!draft.Pages.TryGetValue(route, out var page))
            {
                page = new WebsitePageDocument
                {
                    Title = title,
                    Navigation = new WebsitePageNavigation
                    {
                        Label = string.IsNullOrWhiteSpace(title) ? route : title,
                        ShowInNavigation = true,
                        Order = draft.Pages.Count * 10
                    }
                };
                draft.Pages[route] = page;
            }

            var pageKey = "import-" + Hash(uri.AbsoluteUri)[..20];
            var sectionId = pageKey + "-section";
            if (FindNode(page.Composition, sectionId) is not null)
            {
                preserved++;
            }
            else
            {
                var section = new WebsiteCompositionNode
                {
                    Id = sectionId,
                    Type = "section",
                    Tag = "section",
                    ClassName = "section",
                    Layout = new WebsiteCompositionLayout { Mode = "stack", Direction = "column" }
                };

                if (!string.IsNullOrWhiteSpace(title))
                {
                    section.Children.Add(new WebsiteCompositionNode
                    {
                        Id = pageKey + "-title",
                        Type = "heading",
                        Tag = "h1",
                        Text = title
                    });
                    added++;
                }

                var paragraphs = Matches(safe, "<(h[1-6]|p|li)\\b[^>]*>(.*?)</\\1\\s*>")
                    .Cast<Match>()
                    .Select(match => Text(match.Groups[2].Value))
                    .Where(value => value.Length > 0)
                    .Take(80)
                    .ToArray();

                for (var i = 0; i < paragraphs.Length; i++)
                {
                    section.Children.Add(new WebsiteCompositionNode
                    {
                        Id = pageKey + "-text-" + i,
                        Type = "text",
                        Tag = "p",
                        Text = paragraphs[i]
                    });
                    added++;
                }

                for (var i = 0; i < images.Length; i++)
                {
                    try
                    {
                        var bytes = await ReadBytesAsync(client, new Uri(images[i]), 5_000_000, deadline.Token);
                        var asset = await media.StoreImageAsync(ownerKey, images[i], bytes, deadline.Token);
                        section.Children.Add(new WebsiteCompositionNode
                        {
                            Id = pageKey + "-image-" + i,
                            Type = "image",
                            Tag = "img",
                            Alt = "Imported image",
                            MediaAssetId = asset.Id
                        });
                        added++;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or ArgumentException or InvalidOperationException)
                    {
                        pageWarnings.Add("Image could not be copied: " + images[i]);
                    }
                }

                for (var i = 0; i < links.Length; i++)
                {
                    var target = new Uri(links[i]);
                    var href = target.Authority == origin.Authority
                        ? target.PathAndQuery + target.Fragment
                        : links[i];
                    section.Children.Add(new WebsiteCompositionNode
                    {
                        Id = pageKey + "-link-" + i,
                        Type = "link",
                        Tag = "a",
                        Text = linkLabels[links[i]],
                        Href = href,
                        Target = target.Authority == origin.Authority ? "_self" : "_blank"
                    });
                    added++;
                }

                page.Composition.Add(section);
                added++;
                pages.Add(new WebsiteImportPage(
                    uri.AbsoluteUri,
                    Hash(html),
                    DateTime.UtcNow,
                    title,
                    links,
                    images,
                    forms,
                    pageWarnings.ToArray(),
                    paragraphs));
            }

            foreach (var link in links)
            {
                var next = new Uri(link);
                if (next.Scheme == origin.Scheme &&
                    next.Authority == origin.Authority &&
                    next.Query.Length == 0 &&
                    !visited.Contains(next.AbsoluteUri) &&
                    queue.Count < 100)
                    queue.Enqueue(next);
            }
        }

        if (queue.Count > 0)
            warnings.Add("Import reached its 20-page limit. Remaining pages need a separate import.");
        if (pages.Count == 0 && added == 0)
            throw new InvalidOperationException("No new accessible website content could be imported.");

        draft = WebsiteContentSanitizer.Sanitize(draft);
        return new WebsiteImportResult(
            draft,
            new WebsiteImportReport(normalized, DateTime.UtcNow, pages, warnings, added, preserved));
    }

    public async Task<byte[]> PreparePortableExportAsync(
        string ownerKey,
        WebsiteContentDocument document,
        CancellationToken ct = default)
    {
        if (document.LegacyMigration is not null)
            throw new InvalidOperationException("Materialize the website into canonical v3 before exporting it.");

        var copy = WebsiteContentSanitizer.Sanitize(document);
        using var output = new MemoryStream();
        long total = 0;
        var paths = new Dictionary<Guid, string>();

        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            await ExportNodes(copy.Shell.Header);
            await ExportNodes(copy.Shell.Footer);
            foreach (var page in copy.Pages.Values) await ExportNodes(page.Composition);
            foreach (var component in copy.ReusableComponents.Values) await ExportNodes(component.Composition);

            var manifest = archive.CreateEntry("document.json");
            await using var manifestStream = manifest.Open();
            await JsonSerializer.SerializeAsync(manifestStream, copy, JsonOptions, ct);

            async Task ExportNodes(IEnumerable<WebsiteCompositionNode> nodes)
            {
                foreach (var node in nodes ?? [])
                {
                    var assetId = node.MediaAssetId ?? ParseManagedMediaId(node.MediaUrl);
                    if (assetId.HasValue)
                    {
                        node.MediaUrl = await ExportAsset(assetId.Value);
                        node.MediaAssetId = null;
                    }
                    await ExportNodes(node.Children);
                }
            }

            async Task<string> ExportAsset(Guid id)
            {
                if (paths.TryGetValue(id, out var prior)) return prior;
                var opened = await media.OpenAsync(ownerKey, id, ct)
                    ?? throw new InvalidOperationException("An owned media asset is unavailable for export.");
                await using var content = opened.Content;
                total += opened.Asset.SizeBytes;
                if (total > 20_000_000)
                    throw new InvalidOperationException("Portable export exceeds 20 MB of media. Export media separately.");

                var extension = opened.Asset.ContentType switch
                {
                    "image/png" => ".png",
                    "image/webp" => ".webp",
                    "video/mp4" => ".mp4",
                    "video/webm" => ".webm",
                    _ => ".jpg"
                };
                var path = "media/" + id.ToString("N") + extension;
                var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
                await using var destination = entry.Open();
                await content.CopyToAsync(destination, ct);
                paths[id] = path;
                return path;
            }
        }

        return output.ToArray();
    }

    /// <summary>Imports only canonical v3 portable JSON/ZIP. Legacy exports must be materialized first.</summary>
    public async Task<WebsiteImportResult> PrepareExportAsync(
        Stream input,
        bool zip,
        WebsiteContentDocument existing,
        bool authorized,
        string ownerKey,
        string mediaBaseUrl,
        CancellationToken ct = default)
    {
        if (!authorized) throw new UnauthorizedAccessException("Export owner authorization is required.");
        if (existing.LegacyMigration is not null)
            throw new InvalidOperationException("Materialize the existing website into canonical v3 before importing a portable website.");

        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (bounded.Length + read > 25_000_000)
                throw new ArgumentException("Export exceeds 25 MB.");
            bounded.Write(buffer, 0, read);
        }
        bounded.Position = 0;

        using var archive = zip ? new ZipArchive(bounded, ZipArchiveMode.Read, leaveOpen: true) : null;
        string manifestJson;
        if (archive is not null)
        {
            if (archive.Entries.Count > 150 ||
                archive.Entries.Sum(entry => entry.Length) > 50_000_000 ||
                archive.Entries.Any(entry =>
                    entry.FullName.StartsWith('/') ||
                    entry.FullName.Contains('\\') ||
                    entry.FullName.Split('/').Contains("..") ||
                    entry.Length > 25_000_000) ||
                archive.Entries.GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
                throw new ArgumentException("Unsafe or oversized export archive.");

            var manifest = archive.GetEntry("document.json")
                ?? throw new ArgumentException("Export requires document.json.");
            await using var stream = manifest.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            manifestJson = await reader.ReadToEndAsync(ct);
        }
        else
        {
            manifestJson = Encoding.UTF8.GetString(bounded.ToArray());
            using var json = JsonDocument.Parse(manifestJson);
            if (json.RootElement.TryGetProperty("draft", out var wrapped))
                manifestJson = wrapped.GetRawText();
        }

        var incoming = WebsiteContentSanitizer.DeserializeCanonicalUntrusted(manifestJson, JsonOptions);
        await RestoreNodes(incoming.Shell.Header);
        await RestoreNodes(incoming.Shell.Footer);
        foreach (var page in incoming.Pages.Values) await RestoreNodes(page.Composition);
        foreach (var component in incoming.ReusableComponents.Values) await RestoreNodes(component.Composition);
        incoming = WebsiteContentSanitizer.Sanitize(incoming);

        var draft = WebsiteContentSanitizer.Sanitize(existing);
        var warnings = new List<string>();
        var added = 0;
        var preserved = 0;

        var existingIsEmpty =
            draft.Pages.Count == 0 &&
            draft.Shell.Header.Count == 0 &&
            draft.Shell.Footer.Count == 0 &&
            draft.ReusableComponents.Count == 0 &&
            draft.Collections.Count == 0;

        if (existingIsEmpty)
        {
            draft = incoming;
            added = CountNodes(incoming) + incoming.Pages.Count + incoming.ReusableComponents.Count;
        }
        else
        {
            foreach (var (path, page) in incoming.Pages.Take(100))
            {
                if (draft.Pages.ContainsKey(path)) { preserved++; continue; }
                draft.Pages[path] = page;
                added += 1 + CountNodes(page.Composition);
            }

            foreach (var (id, component) in incoming.ReusableComponents.Take(60))
            {
                if (draft.ReusableComponents.ContainsKey(id)) { preserved++; continue; }
                draft.ReusableComponents[id] = component;
                added += 1 + CountNodes(component.Composition);
            }

            foreach (var (id, collection) in incoming.Collections.Take(24))
            {
                if (draft.Collections.ContainsKey(id)) { preserved++; continue; }
                draft.Collections[id] = collection;
                added++;
            }

            warnings.Add("Existing pages, shared shell, theme, store, and existing component identities were preserved. Imported v3 pages/components were added without creating a second authority.");
        }

        draft = WebsiteContentSanitizer.Sanitize(draft);
        return new WebsiteImportResult(
            draft,
            new WebsiteImportReport("uploaded-v3-export", DateTime.UtcNow, [], warnings, added, preserved));

        async Task RestoreNodes(IEnumerable<WebsiteCompositionNode> nodes)
        {
            foreach (var node in nodes ?? [])
            {
                if (!string.IsNullOrWhiteSpace(node.MediaUrl) &&
                    node.MediaUrl.StartsWith("media/", StringComparison.Ordinal))
                {
                    var entry = archive?.GetEntry(node.MediaUrl)
                        ?? throw new ArgumentException("Portable website media is missing; upload the complete ZIP archive.");
                    await using var mediaStream = entry.Open();
                    using var bytes = new MemoryStream();
                    await mediaStream.CopyToAsync(bytes, ct);
                    var asset = await media.StoreAsync(
                        ownerKey,
                        "uploaded-v3-export:" + entry.FullName,
                        entry.FullName,
                        bytes.ToArray(),
                        ct);
                    node.MediaAssetId = asset.Id;
                    node.MediaUrl = null;
                }
                else
                {
                    var assetId = node.MediaAssetId ?? ParseManagedMediaId(node.MediaUrl);
                    if (assetId.HasValue)
                    {
                        var owned = await media.OpenAsync(ownerKey, assetId.Value, ct);
                        if (owned is null)
                            throw new ArgumentException("Portable website references another website's media.");
                        await owned.Value.Content.DisposeAsync();
                        node.MediaAssetId = assetId.Value;
                        node.MediaUrl = null;
                    }
                }

                await RestoreNodes(node.Children);
            }
        }
    }

    private static int CountNodes(WebsiteContentDocument document) =>
        CountNodes(document.Shell.Header) +
        CountNodes(document.Shell.Footer) +
        document.Pages.Values.Sum(page => CountNodes(page.Composition)) +
        document.ReusableComponents.Values.Sum(component => CountNodes(component.Composition));

    private static int CountNodes(IEnumerable<WebsiteCompositionNode> nodes) =>
        (nodes ?? []).Sum(node => 1 + CountNodes(node.Children));

    private static WebsiteCompositionNode? FindNode(IEnumerable<WebsiteCompositionNode> nodes, string id)
    {
        foreach (var node in nodes ?? [])
        {
            if (node.Id == id) return node;
            var child = FindNode(node.Children, id);
            if (child is not null) return child;
        }
        return null;
    }

    private static Guid? ParseManagedMediaId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var uri)) return null;
        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : value.Split('?', '#')[0];
        const string marker = "/api/website-content/media/";
        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 && Guid.TryParse(path[(index + marker.Length)..].Trim('/'), out var id)
            ? id
            : null;
    }

    private static IEnumerable<(string Url, string Label)> ExtractLinks(string html, Uri page)
    {
        foreach (Match match in Matches(html, "<a\\b[^>]*?\\bhref\\s*=\\s*([\"'])(.*?)\\1[^>]*>(.*?)</a\\s*>"))
        {
            if (!Uri.TryCreate(page, WebUtility.HtmlDecode(match.Groups[2].Value), out var uri)) continue;
            var safe = LegendConnectResearchNetworkPolicy.NormalizePublicHttpUri(uri.AbsoluteUri);
            if (safe is null) continue;
            var label = Text(match.Groups[3].Value);
            yield return (safe, label.Length == 0 ? uri.Host : label);
        }
    }

    private static IEnumerable<string> ExtractUrls(string html, string tag, string attribute, Uri page)
    {
        foreach (Match match in Matches(html, "<" + tag + "\\b[^>]*?\\b" + attribute + "\\s*=\\s*([\"'])(.*?)\\1"))
        {
            if (!Uri.TryCreate(page, WebUtility.HtmlDecode(match.Groups[2].Value), out var uri)) continue;
            var safe = LegendConnectResearchNetworkPolicy.NormalizePublicHttpUri(uri.AbsoluteUri);
            if (safe is not null) yield return safe;
        }
    }

    private static async Task<string> ReadAsync(HttpClient client, Uri uri, CancellationToken ct)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (!LegendConnectResearchNetworkPolicy.IsSupportedContentType(response.Content.Headers.ContentType?.MediaType))
            throw new HttpRequestException("Unsupported import content type.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > 2_000_000)
                throw new HttpRequestException("Import page exceeds limit.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static async Task<byte[]> ReadBytesAsync(HttpClient client, Uri uri, int maximum, CancellationToken ct)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > maximum)
                throw new HttpRequestException("Imported asset exceeds limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
