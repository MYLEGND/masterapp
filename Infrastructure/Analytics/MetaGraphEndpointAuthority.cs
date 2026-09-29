using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Infrastructure.Analytics;

/// <summary>
/// Canonical Meta Graph transport authority.
///
/// Meta Ads can reject an otherwise valid request when the application's provider-side
/// default API version becomes deprecated. The transport therefore starts from Meta's
/// configured default only when no negotiated version is known, recognizes provider
/// error #2635, extracts Meta's recommended API version, retries the same request once
/// through that explicit version, and caches the successful provider recommendation for
/// the process lifetime. No application deployment carries a permanently pinned Meta
/// API version, and individual consumers cannot drift independently.
/// </summary>
public static class MetaGraphEndpointAuthority
{
    private static readonly Uri GraphOrigin = new("https://graph.facebook.com/", UriKind.Absolute);
    private static readonly Uri LoginOrigin = new("https://www.facebook.com/", UriKind.Absolute);
    private static readonly Regex ApiVersionPattern =
        new(@"^v\d+\.\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex RecommendedVersionPattern =
        new(@"\b(v\d+\.\d+)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static string? _negotiatedVersion;

    public static string? NegotiatedVersion => Volatile.Read(ref _negotiatedVersion);

    public static string Graph(string relativePath) =>
        BuildGraphUrl(NormalizeRelativePath(relativePath), NegotiatedVersion);

    public static string OAuthDialog()
    {
        var version = NegotiatedVersion;
        var path = string.IsNullOrWhiteSpace(version) ? "dialog/oauth" : $"{version}/dialog/oauth";
        return new Uri(LoginOrigin, path).AbsoluteUri;
    }

    public static Task<MetaGraphHttpResult> GetAsync(
        HttpClient client,
        string urlOrRelativePath,
        CancellationToken cancellationToken = default) =>
        SendAsync(client, HttpMethod.Get, urlOrRelativePath, null, cancellationToken);

    public static Task<MetaGraphHttpResult> PostFormAsync(
        HttpClient client,
        string urlOrRelativePath,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            client,
            HttpMethod.Post,
            urlOrRelativePath,
            () => new FormUrlEncodedContent(fields),
            cancellationToken);

    public static string SafeErrorMessage(string? responseBody, string fallback)
    {
        if (!TryReadError(responseBody, out var code, out var subcode, out var message))
            return fallback;

        var identity = subcode.HasValue ? $"{code}/{subcode.Value}" : code.ToString();
        var safeMessage = string.IsNullOrWhiteSpace(message)
            ? fallback
            : message!.Length <= 240 ? message : message[..240];
        return $"Meta Ads API error {identity}: {safeMessage}";
    }

    internal static void ResetNegotiatedVersionForTests() =>
        Interlocked.Exchange(ref _negotiatedVersion, null);

    internal static void SetNegotiatedVersionForTests(string? version) =>
        Interlocked.Exchange(ref _negotiatedVersion, NormalizeVersion(version));

    private static async Task<MetaGraphHttpResult> SendAsync(
        HttpClient client,
        HttpMethod method,
        string urlOrRelativePath,
        Func<HttpContent?>? contentFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(urlOrRelativePath))
            throw new ArgumentException("A Meta Graph path is required.", nameof(urlOrRelativePath));

        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var version = NegotiatedVersion;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var attemptKey = version ?? "(provider-default)";
            if (!attempted.Add(attemptKey))
                break;

            var url = ResolveGraphUrl(urlOrRelativePath, version);
            var result = await SendAttemptAsync(
                client,
                method,
                url,
                contentFactory,
                version,
                attempt > 0,
                cancellationToken);

            if (result.IsSuccessStatusCode)
                return result;

            if (!TryReadRecommendedVersion(result.Body, out var recommendedVersion) ||
                attempted.Contains(recommendedVersion) ||
                attempt == 2)
                return result;

            Interlocked.Exchange(ref _negotiatedVersion, recommendedVersion);
            version = recommendedVersion;
        }

        throw new InvalidOperationException("Meta Graph transport reached an unreachable negotiation state.");
    }

    private static async Task<MetaGraphHttpResult> SendAttemptAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        Func<HttpContent?>? contentFactory,
        string? effectiveVersion,
        bool negotiated,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        if (contentFactory is not null)
            request.Content = contentFactory();

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new MetaGraphHttpResult(
            response.StatusCode,
            body,
            response.ReasonPhrase,
            effectiveVersion,
            negotiated);
    }

    private static string ResolveGraphUrl(string urlOrRelativePath, string? version)
    {
        if (!Uri.TryCreate(urlOrRelativePath, UriKind.Absolute, out var absolute))
            return BuildGraphUrl(NormalizeRelativePath(urlOrRelativePath), version);

        if (!absolute.Host.Equals(GraphOrigin.Host, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Meta Graph transport only accepts graph.facebook.com URLs.", nameof(urlOrRelativePath));

        if (string.IsNullOrWhiteSpace(version))
            return absolute.AbsoluteUri;

        var builder = new UriBuilder(absolute);
        var path = builder.Path.TrimStart('/');
        var slash = path.IndexOf('/');
        var first = slash >= 0 ? path[..slash] : path;
        var tail = slash >= 0 ? path[(slash + 1)..] : string.Empty;

        if (!ApiVersionPattern.IsMatch(first))
            tail = path;

        builder.Path = string.IsNullOrWhiteSpace(tail)
            ? $"/{version}"
            : $"/{version}/{tail}";
        return builder.Uri.AbsoluteUri;
    }

    private static string BuildGraphUrl(string relativePath, string? version)
    {
        var path = string.IsNullOrWhiteSpace(version)
            ? relativePath
            : $"{version}/{StripLeadingVersion(relativePath)}";
        return new Uri(GraphOrigin, path).AbsoluteUri;
    }

    private static string StripLeadingVersion(string path)
    {
        var normalized = path.TrimStart('/');
        var slash = normalized.IndexOf('/');
        if (slash < 0)
            return ApiVersionPattern.IsMatch(normalized) ? string.Empty : normalized;

        var first = normalized[..slash];
        return ApiVersionPattern.IsMatch(first) ? normalized[(slash + 1)..] : normalized;
    }

    private static bool TryReadRecommendedVersion(string? body, out string version)
    {
        version = string.Empty;
        if (!TryReadError(body, out var code, out _, out var message) || code != 2635 || string.IsNullOrWhiteSpace(message))
            return false;

        var matches = RecommendedVersionPattern.Matches(message!);
        if (matches.Count == 0)
            return false;

        var candidate = NormalizeVersion(matches[matches.Count - 1].Groups[1].Value);
        if (candidate is null)
            return false;

        version = candidate;
        return true;
    }

    private static bool TryReadError(
        string? body,
        out int code,
        out int? subcode,
        out string? message)
    {
        code = 0;
        subcode = null;
        message = null;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object)
                return false;

            if (error.TryGetProperty("code", out var codeElement) &&
                codeElement.TryGetInt32(out var parsedCode))
                code = parsedCode;

            if (error.TryGetProperty("error_subcode", out var subcodeElement) &&
                subcodeElement.TryGetInt32(out var parsedSubcode))
                subcode = parsedSubcode;

            if (error.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String)
                message = messageElement.GetString();

            return code != 0 || !string.IsNullOrWhiteSpace(message);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A Meta Graph relative path is required.", nameof(value));

        var path = value.Trim().TrimStart('/');
        if (Uri.TryCreate(path, UriKind.Absolute, out _) ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            path.Contains('\\') ||
            path.Any(char.IsControl))
            throw new ArgumentException("Meta Graph paths must be relative.", nameof(value));

        return path;
    }

    private static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var version = value.Trim().ToLowerInvariant();
        return ApiVersionPattern.IsMatch(version) ? version : null;
    }
}

public sealed record MetaGraphHttpResult(
    HttpStatusCode StatusCode,
    string Body,
    string? ReasonPhrase,
    string? EffectiveVersion,
    bool Negotiated)
{
    public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;
}
