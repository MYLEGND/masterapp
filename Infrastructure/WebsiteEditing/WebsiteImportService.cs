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

/// <summary>Bounded public content import into a caller-owned draft. Never publishes or overwrites edits.</summary>
public sealed class WebsiteImportService(WebsiteMediaService media)
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);
    private static MatchCollection Matches(string text, string pattern) => Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, PatternTimeout);
    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", " ", RegexOptions.Singleline, PatternTimeout)).Trim();

    public async Task<WebsiteImportResult> PrepareAsync(string sourceUrl, WebsiteContentDocument existing, bool authorized, string ownerKey, string mediaBaseUrl, CancellationToken ct = default)
    {
        if (!authorized) throw new UnauthorizedAccessException("Website owner authorization is required before importing.");
        var normalized = LegendConnectResearchNetworkPolicy.NormalizePublicHttpUri(sourceUrl);
        if (normalized is null) throw new ArgumentException("Enter a public website address.");
        var origin = new Uri(normalized);
        var draft = JsonSerializer.Deserialize<WebsiteContentDocument>(JsonSerializer.Serialize(existing))!;
        var pages = new List<WebsiteImportPage>();
        var warnings = new List<string> { "Review imported content before publishing. Private data, booking, payments, forms, scripts, and third-party integrations are not transferred." };
        var queue = new Queue<Uri>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue(origin);
        var added = 0;
        var preserved = 0;
        using var handler = LegendConnectResearchNetworkPolicy.CreatePublicReadOnlyHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LEGEND-Website-Import/1.0");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        while (queue.Count > 0 && pages.Count < 20)
        {
            var uri = queue.Dequeue();
            if (!visited.Add(uri.AbsoluteUri)) continue;
            string html;
            try { html = await ReadAsync(client, uri, deadline.Token); }
            catch (HttpRequestException) { warnings.Add("Could not import " + uri.AbsoluteUri); continue; }
            // Imported scripts/styles never enter the draft. No source HTML is executed.
            var safe = Regex.Replace(html, "<(script|style|noscript|template)\\b[^>]*>.*?</\\1\\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline, PatternTimeout);
            var titleMatch = Matches(safe, "<title\\b[^>]*>(.*?)</title>").Cast<Match>().FirstOrDefault();
            var title = titleMatch is null ? uri.AbsolutePath : Text(titleMatch.Groups[1].Value);
            var linkLabels = ExtractLinks(safe, uri).GroupBy(x => x.Url).ToDictionary(x => x.Key, x => x.First().Label);
            var links = linkLabels.Keys.Take(300).ToArray();
            var images = ExtractUrls(safe, "img", "src", uri).Distinct().Take(50).ToArray();
            var forms = Matches(safe, "<form\\b").Count;
            var pageWarnings = new List<string>();
            if (forms > 0) pageWarnings.Add("Forms require a scoped destination and must be rebuilt before launch.");
            if (Matches(safe, "<(video|iframe)\\b").Count > 0) pageWarnings.Add("Video and embedded integrations require review.");
            var route = uri.AbsolutePath.TrimEnd('/');
            if (route.Length == 0) route = "/";
            if (!draft.Pages.TryGetValue(route, out var page))
            {
                page = new WebsitePageDocument { Title = title };
                draft.Pages[route] = page;
            }
            var pageKey = "import-" + Hash(uri.AbsoluteUri)[..20];
            var sectionId = pageKey + "-section";
            Add(page, new WebsiteExtraComponent { Id = sectionId, SectionId = "main", Type = "section", Text = title });
            var paragraphs = Matches(safe, "<(h[1-6]|p|li)\\b[^>]*>(.*?)</\\1\\s*>")
                .Cast<Match>().Select(x => Text(x.Groups[2].Value)).Where(x => x.Length > 0).Take(80).ToArray();
            for (var i = 0; i < paragraphs.Length; i++)
                Add(page, new WebsiteExtraComponent { Id = pageKey + "-text-" + i, SectionId = sectionId, Type = "text", Text = paragraphs[i] });
            for (var i = 0; i < images.Length; i++)
            {
                var imageId = pageKey + "-image-" + i;
                if (page.Extras.Any(x => x.Id == imageId)) { preserved++; continue; }
                try
                {
                    var bytes = await ReadBytesAsync(client, new Uri(images[i]), 5_000_000, deadline.Token);
                    var asset = await media.StoreImageAsync(ownerKey, images[i], bytes, deadline.Token);
                    Add(page, new WebsiteExtraComponent { Id = imageId, SectionId = sectionId, Type = "image", ImageDataUrl = mediaBaseUrl.TrimEnd('/') + "/api/website-content/media/" + asset.Id });
                }
                catch (Exception ex) when (ex is HttpRequestException or ArgumentException or InvalidOperationException)
                { pageWarnings.Add("Image could not be copied: " + images[i]); }
            }
            for (var i = 0; i < links.Length; i++)
            {
                var target = new Uri(links[i]);
                var href = target.Authority == origin.Authority ? target.PathAndQuery + target.Fragment : links[i];
                Add(page, new WebsiteExtraComponent { Id = pageKey + "-link-" + i, SectionId = sectionId, Type = "button", Text = linkLabels[links[i]], Href = href });
            }
            pages.Add(new WebsiteImportPage(uri.AbsoluteUri, Hash(html), DateTime.UtcNow, title, links, images, forms, pageWarnings.ToArray(), paragraphs));
            foreach (var link in links)
            {
                var next = new Uri(link);
                if (next.Scheme == origin.Scheme && next.Authority == origin.Authority && next.Query.Length == 0 &&
                    !visited.Contains(next.AbsoluteUri) && queue.Count < 100)
                    queue.Enqueue(next);
            }
        }
        if (queue.Count > 0) warnings.Add("Import reached its 20-page limit. Remaining pages need a separate import or authorized platform export.");
        if (pages.Count == 0) throw new InvalidOperationException("No accessible website pages could be imported.");
        return new WebsiteImportResult(draft, new WebsiteImportReport(normalized, DateTime.UtcNow, pages, warnings, added, preserved));

        void Add(WebsitePageDocument page, WebsiteExtraComponent component)
        {
            if (page.Extras.Any(x => x.Id == component.Id)) { preserved++; return; }
            if (page.Extras.Count >= 120) { if (!warnings.Contains("Component capacity reached; remaining content requires review.")) warnings.Add("Component capacity reached; remaining content requires review."); return; }
            page.Extras.Add(component);
            added++;
        }
    }

    public async Task<byte[]> PreparePortableExportAsync(string ownerKey, WebsiteContentDocument document, CancellationToken ct = default)
    {
        var copy = JsonSerializer.Deserialize<WebsiteContentDocument>(JsonSerializer.Serialize(document))!;
        using var output = new MemoryStream();
        long total = 0;
        var paths = new Dictionary<Guid, string>();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var element in copy.Elements.Values) { element.ImageDataUrl = await ExportAsset(element.ImageDataUrl); element.VideoUrl = await ExportAsset(element.VideoUrl); }
            foreach (var extra in copy.Extras) { extra.ImageDataUrl = await ExportAsset(extra.ImageDataUrl); extra.VideoUrl = await ExportAsset(extra.VideoUrl); }
            foreach (var page in copy.Pages.Values)
            {
                foreach (var element in page.Elements.Values) { element.ImageDataUrl = await ExportAsset(element.ImageDataUrl); element.VideoUrl = await ExportAsset(element.VideoUrl); }
                foreach (var extra in page.Extras) { extra.ImageDataUrl = await ExportAsset(extra.ImageDataUrl); extra.VideoUrl = await ExportAsset(extra.VideoUrl); }
            }
            var manifest = archive.CreateEntry("document.json");
            await using var manifestStream = manifest.Open();
            await JsonSerializer.SerializeAsync(manifestStream, copy, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);

            async Task<string?> ExportAsset(string? url)
            {
                if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    !uri.AbsolutePath.StartsWith("/api/website-content/media/", StringComparison.Ordinal) ||
                    !Guid.TryParse(uri.Segments.LastOrDefault(), out var id)) return url;
                if (paths.TryGetValue(id, out var prior)) return prior;
                var opened = await media.OpenAsync(ownerKey, id, ct) ?? throw new InvalidOperationException("An owned media asset is unavailable for export.");
                await using var content = opened.Content;
                total += opened.Asset.SizeBytes;
                if (total > 20_000_000) throw new InvalidOperationException("Portable export exceeds 20 MB of media. Export media separately.");
                var extension = opened.Asset.ContentType switch { "image/png" => ".png", "image/webp" => ".webp", "video/mp4" => ".mp4", "video/webm" => ".webm", _ => ".jpg" };
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

    /// <summary>Imports LEGEND portable JSON, or a ZIP containing document.json and referenced media files.</summary>
    public async Task<WebsiteImportResult> PrepareExportAsync(Stream input, bool zip, WebsiteContentDocument existing,
        bool authorized, string ownerKey, string mediaBaseUrl, CancellationToken ct = default)
    {
        if (!authorized) throw new UnauthorizedAccessException("Export owner authorization is required.");
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (bounded.Length + read > 25_000_000) throw new ArgumentException("Export exceeds 25 MB.");
            bounded.Write(buffer, 0, read);
        }
        bounded.Position = 0;
        WebsiteContentDocument incoming;
        using var archive = zip ? new ZipArchive(bounded, ZipArchiveMode.Read, leaveOpen: true) : null;
        if (archive is not null)
        {
            if (archive.Entries.Count > 150 || archive.Entries.Sum(x => x.Length) > 50_000_000 ||
                archive.Entries.Any(x => x.FullName.StartsWith('/') || x.FullName.Contains('\\') || x.FullName.Split('/').Contains("..") || x.Length > 25_000_000) ||
                archive.Entries.GroupBy(x => x.FullName, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1))
                throw new ArgumentException("Unsafe or oversized export archive.");
            var manifest = archive.GetEntry("document.json") ?? throw new ArgumentException("Export requires document.json.");
            await using var stream = manifest.Open();
            incoming = await JsonSerializer.DeserializeAsync<WebsiteContentDocument>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct)
                ?? throw new ArgumentException("Invalid export document.");
        }
        else {
            using var json = await JsonDocument.ParseAsync(bounded, cancellationToken: ct);
            var source = json.RootElement.TryGetProperty("draft", out var wrapped) ? wrapped : json.RootElement;
            incoming = source.Deserialize<WebsiteContentDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ArgumentException("Invalid export document.");
        }
        var draft = JsonSerializer.Deserialize<WebsiteContentDocument>(JsonSerializer.Serialize(existing))!;
        var warnings = new List<string>();
        var added = 0;
        var preserved = 0;
        foreach (var pair in incoming.Pages.Take(20))
        {
            if (!pair.Key.StartsWith('/') || pair.Key.StartsWith("//") || pair.Key.Contains('\\') || pair.Key.Contains(".."))
                throw new ArgumentException("Invalid export page path.");
            if (draft.Pages.ContainsKey(pair.Key)) { preserved++; continue; }
            foreach (var element in pair.Value.Elements.Values) { element.ImageDataUrl = await RestoreAsset(element.ImageDataUrl); element.VideoUrl = await RestoreAsset(element.VideoUrl); }
            foreach (var extra in pair.Value.Extras) { extra.ImageDataUrl = await RestoreAsset(extra.ImageDataUrl); extra.VideoUrl = await RestoreAsset(extra.VideoUrl); }
            draft.Pages.Add(pair.Key, pair.Value);
            added++;
        }
        if (incoming.Pages.Count == 0)
        {
            foreach (var pair in incoming.Elements)
            {
                if (draft.Elements.ContainsKey(pair.Key)) { preserved++; continue; }
                pair.Value.ImageDataUrl = await RestoreAsset(pair.Value.ImageDataUrl);
                pair.Value.VideoUrl = await RestoreAsset(pair.Value.VideoUrl);
                draft.Elements.Add(pair.Key, pair.Value); added++;
            }
            foreach (var extra in incoming.Extras)
            {
                if (draft.Extras.Any(x => x.Id == extra.Id)) { preserved++; continue; }
                extra.ImageDataUrl = await RestoreAsset(extra.ImageDataUrl);
                extra.VideoUrl = await RestoreAsset(extra.VideoUrl);
                draft.Extras.Add(extra); added++;
            }
        }
        foreach (var order in incoming.SectionOrder) draft.SectionOrder.TryAdd(order.Key, order.Value);
        if (existing.Elements.Count == 0 && existing.Extras.Count == 0 && existing.Pages.Count == 0 &&
            JsonSerializer.Serialize(existing.Theme) == JsonSerializer.Serialize(new WebsiteThemeOverride())) draft.Theme = incoming.Theme;
        warnings.Add("Existing pages and elements were preserved. Review imported content, navigation, integrations, and theme before publishing.");
        return new WebsiteImportResult(draft, new WebsiteImportReport("uploaded-export", DateTime.UtcNow, [], warnings, added, preserved));

        async Task<string?> RestoreAsset(string? path)
        {
            if (path?.StartsWith("media/", StringComparison.Ordinal) == true)
            {
                var entry = archive?.GetEntry(path) ?? throw new ArgumentException("Export media missing; upload the full portable archive.");
                await using var mediaStream = entry.Open();
                using var bytes = new MemoryStream();
                await mediaStream.CopyToAsync(bytes, ct);
                var asset = await media.StoreAsync(ownerKey, "uploaded-export:" + entry.FullName, entry.FullName, bytes.ToArray(), ct);
                return mediaBaseUrl.TrimEnd('/') + "/api/website-content/media/" + asset.Id;
            }
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.AbsolutePath.StartsWith("/api/website-content/media/", StringComparison.Ordinal) && Guid.TryParse(uri.Segments.LastOrDefault(), out var id))
            {
                var owned = await media.OpenAsync(ownerKey, id, ct);
                if (owned is null) throw new ArgumentException("Export references another website's media; upload a portable archive including assets.");
                await owned.Value.Content.DisposeAsync();
            }
            return path;
        }
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
        // Redirects deliberately require another explicit source: no credentials or cross-host redirects.
        response.EnsureSuccessStatusCode();
        if (!LegendConnectResearchNetworkPolicy.IsSupportedContentType(response.Content.Headers.ContentType?.MediaType))
            throw new HttpRequestException("Unsupported import content type.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > 2_000_000) throw new HttpRequestException("Import page exceeds limit.");
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
            if (output.Length + read > maximum) throw new HttpRequestException("Imported asset exceeds limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
