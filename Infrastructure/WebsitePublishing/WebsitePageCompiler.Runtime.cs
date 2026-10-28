using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace Infrastructure.WebsitePublishing;

public sealed partial class WebsitePageCompiler
{
    private readonly ConcurrentDictionary<string, (long Length, DateTime Written, DateTime Created, string Hash)> runtimeVersions = new(StringComparer.Ordinal);
    // These are compiler-owned dependencies, never authored links or media.
    private static readonly HashSet<string> RuntimePaths = new(StringComparer.Ordinal)
    {
        "/site.css", "/public-inquiry-form.css", "/public-inquiry-form.mjs",
        "/legend-public-web.js", "/legend-public-cms.js", "/legend-public-inquiry.js",
        "/legend-public-tracking.js", "/legend-public-meta-signal-intelligence.js",
        "/legend-public-openai-measurement.js", "/js/page-health.js", "/js/legend-site-tools.js"
    };

    // Tokenize complete tags (including quoted >), comments and raw-text bodies.
    // In particular, apparent markup inside JSON, JavaScript or CSS is not HTML.
    private static readonly Regex HtmlTokens = new(
        "<!--.*?(?:-->|$)|<(?<tag>script|style|textarea|title)\\b(?<attributes>(?:[^>\"']|\"[^\"]*\"|'[^']*')*)>(?<body>.*?)</\\k<tag>\\s*>|<(?<tag>[^\\s/!<>]+)\\b(?<attributes>(?:[^>\"']|\"[^\"]*\"|'[^']*')*)>|<[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex Attributes = new(
        "(?<name>[^\\s=/>]+)(?:\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+)))?",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary>Bind derived delivery HTML to this package without mutating its publication.</summary>
    public async Task<string> BindRuntimeAssetsAsync(string html, CancellationToken cancellationToken)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        // Hash bytes actually present in the package, not the historical compiler SHA.
        // Revalidate package file identity each request; changed/missing files cannot
        // reuse the previous hash. Cached hashes contain no tenant or content data.
        async Task ReadVersion(string path)
        {
            if (versions.ContainsKey(path)) return;
            try
            {
                var root = configuration["WebsitePublishing:CompilerRoot"]
                    ?? Path.Combine(environment.ContentRootPath, "WebsiteCompiler");
                var file = new FileInfo(Path.Combine(root, "dist", path.TrimStart('/')));
                if (!file.Exists) throw new IOException();
                var identity = (file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
                if (runtimeVersions.TryGetValue(file.FullName, out var cached) &&
                    identity == (cached.Length, cached.Written, cached.Created))
                {
                    versions[path] = cached.Hash;
                    return;
                }
                await using var stream = file.OpenRead();
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                file.Refresh();
                if (!file.Exists || identity != (file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc))
                    throw new IOException();
                runtimeVersions[file.FullName] = (identity.Length, identity.LastWriteTimeUtc, identity.CreationTimeUtc, hash);
                versions[path] = hash;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("website_runtime_asset_unavailable");
            }
        }

        var edits = new List<(int Start, int Length, string Value)>();
        foreach (Match token in HtmlTokens.Matches(html))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tag = token.Groups["tag"].Value.ToLowerInvariant();
            if (tag is not ("script" or "link")) continue;
            var attributes = Attributes.Matches(token.Groups["attributes"].Value).Cast<Match>().ToArray();
            string? Attribute(string name) => attributes.FirstOrDefault(a =>
                a.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase))?.Groups["value"].Value;
            var attributeName = tag == "script" ? "src" : "href";
            if (tag == "script" || string.Equals(Attribute("rel"), "stylesheet", StringComparison.OrdinalIgnoreCase))
            {
                var attribute = attributes.FirstOrDefault(a => a.Groups["name"].Value.Equals(attributeName, StringComparison.OrdinalIgnoreCase));
                if (attribute is not null)
                {
                    var value = attribute.Groups["value"];
                    var url = WebUtility.HtmlDecode(value.Value);
                    var path = RuntimePath(url);
                    if (path is not null)
                    {
                        await ReadVersion(path);
                        edits.Add((token.Groups["attributes"].Index + value.Index, value.Length,
                            WebUtility.HtmlEncode(VersionUrl(url, versions[path]))));
                        if (path == "/public-inquiry-form.mjs")
                        {
                            const string dependency = "/legend-public-inquiry.js";
                            await ReadVersion(dependency);
                            var dependencyUrl = VersionUrl(dependency, versions[dependency]);
                            var existing = attributes.FirstOrDefault(a => a.Groups["name"].Value == "data-inquiry-runtime");
                            if (existing is null)
                                edits.Add((token.Groups["attributes"].Index + token.Groups["attributes"].Length, 0,
                                    " data-inquiry-runtime=\"" + dependencyUrl + "\""));
                            else
                                edits.Add((token.Groups["attributes"].Index + existing.Groups["value"].Index,
                                    existing.Groups["value"].Length, dependencyUrl));
                        }
                    }
                }
            }
            if (tag != "script" || Attribute("id") != "legend-cms-published-document" ||
                Attribute("type") != "application/json") continue;
            // Replace only the top-level runtime object's URL values. Authored
            // document bytes, schema, catalog and publication provenance stay intact.
            var body = token.Groups["body"];
            foreach (var slot in RuntimeSlots(body.Value))
            {
                var path = RuntimePath(slot.Url);
                if (path is null) continue;
                await ReadVersion(path);
                edits.Add((body.Index + slot.Start, slot.Length, JsonSerializer.Serialize(VersionUrl(slot.Url, versions[path]))));
            }
        }
        var result = new StringBuilder(html);
        foreach (var edit in edits.OrderByDescending(e => e.Start))
            result.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Value);
        return result.ToString();
    }

    private static string? RuntimePath(string url)
    {
        var end = url.IndexOfAny(['?', '#']);
        var path = end < 0 ? url : url[..end];
        return RuntimePaths.Contains(path) ? path : null;
    }

    private static string VersionUrl(string url, string hash)
    {
        var fragmentAt = url.IndexOf('#');
        var fragment = fragmentAt < 0 ? "" : url[fragmentAt..];
        var address = fragmentAt < 0 ? url : url[..fragmentAt];
        var queryAt = address.IndexOf('?');
        var path = queryAt < 0 ? address : address[..queryAt];
        var query = queryAt < 0 ? [] : address[(queryAt + 1)..].Split('&')
            .Where(part => part.Length > 0 && Uri.UnescapeDataString(part.Split('=')[0]) != "v").ToArray();
        return path + "?" + string.Join("&", query.Append("v=" + hash)) + fragment;
    }

    private static List<(int Start, int Length, string Url)> RuntimeSlots(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes);
        var result = new List<(int, int, string)>();
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 ||
                !reader.ValueTextEquals("runtime")) continue;
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
            while (reader.Read() && !(reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 1))
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 2 ||
                    reader.GetString() is not ("trackingAsset" or "metaSignalAsset" or "openAiMeasurementAsset")) continue;
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) throw new JsonException();
                var start = checked((int)reader.TokenStartIndex);
                var end = checked((int)reader.BytesConsumed);
                result.Add((Encoding.UTF8.GetCharCount(bytes.AsSpan(0, start)),
                    Encoding.UTF8.GetCharCount(bytes.AsSpan(start, end - start)), reader.GetString()!));
            }
        }
        return result;
    }
}
