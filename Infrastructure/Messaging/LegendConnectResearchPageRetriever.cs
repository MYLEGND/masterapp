using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.Messaging;

namespace Infrastructure.Messaging;

/// <summary>
/// The one canonical page-retrieval path beneath governed research. It only
/// issues unauthenticated GET requests to independently validated public HTTP
/// or HTTPS destinations and returns bounded untrusted text plus receipts.
/// </summary>
internal sealed class LegendConnectResearchPageRetriever
    : ILegendConnectResearchPageRetriever
{
    internal const string ClientName = "LegendResearchPageRetrieval";
    private const string TransportName = "LegendCanonicalPageRetrieval";
    private const string ProviderName = "PublicInternet";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILegendLanguageRegistry _languages;

    public LegendConnectResearchPageRetriever(
        IHttpClientFactory httpClientFactory,
        ILegendLanguageRegistry languages)
    {
        _httpClientFactory = httpClientFactory;
        _languages = languages;
    }

    public async Task<LegendConnectResearchPageRetrievalResult> RetrieveAsync(
        LegendConnectResearchPageRetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var settingsIdentity = LegendLanguageIdentity.TextHash(string.Join(
            '|',
            "legend-canonical-page-retrieval:v2",
            "methods:GET",
            "schemes:http,https",
            "redirects:" + LegendConnectResearchContracts.MaximumRedirects,
            "request-timeout:" + LegendConnectResearchContracts.RequestTimeoutSeconds,
            "page-bytes:" + LegendConnectResearchContracts.MaximumPageBytes,
            "mime:text/html,text/plain,application/xhtml+xml",
            "cookies:false",
            "credentials:false",
            "scripts:false",
            "forms:false",
            "files:false",
            "private-network:false"));

        LegendConnectResearchPageRetrievalResult Failure(string reason, bool retryable = false) =>
            new(false, TransportName, settingsIdentity, [], [], [], [], [], [],
                (long)Math.Ceiling(clock.Elapsed.TotalMilliseconds), reason, retryable);

        if (!IsBoundedRequest(request))
            return Failure("internet_research_page_request_invalid");

        var sourcesByUri = request.Sources
            .Select(item => new
            {
                CanonicalUri = LegendConnectResearchNetworkPolicy.NormalizePublicHttpUri(item.CanonicalUri),
                Source = item
            })
            .Where(item => item.CanonicalUri is not null)
            .GroupBy(item => item.CanonicalUri!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Source, StringComparer.Ordinal);
        var candidates = request.SearchResults
            .Select(item => new
            {
                Result = item,
                CanonicalUri = LegendConnectResearchNetworkPolicy.NormalizePublicHttpUri(item.CanonicalUri)
            })
            .Where(item => item.CanonicalUri is not null && sourcesByUri.ContainsKey(item.CanonicalUri))
            .GroupBy(item => item.CanonicalUri!, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (candidates.Length == 0)
            return Failure("internet_research_no_public_page_candidates");

        using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remainingDeadline = request.DeadlineUtc - DateTime.UtcNow;
        if (remainingDeadline <= TimeSpan.Zero)
            return Failure("internet_research_total_deadline_exceeded", true);
        deadlineCancellation.CancelAfter(remainingDeadline);

        var results = new List<LegendConnectSearchResult>();
        var sources = new List<LegendConnectResearchSourceIdentity>();
        var documents = new List<LegendConnectRetrievedDocument>();
        var citations = new List<LegendConnectCitation>();
        var lineage = new List<LegendConnectRetrievedPageLineage>();
        var receipts = new List<LegendConnectResearchPageReceipt>();
        var acceptedFinalUris = new HashSet<string>(StringComparer.Ordinal);
        var returnedCharacters = 0;

        foreach (var candidate in candidates)
        {
            if (DateTime.UtcNow >= request.DeadlineUtc ||
                returnedCharacters >= request.MaximumTotalCharacters ||
                documents.Count >= request.MaximumDocuments)
                break;
            var source = sourcesByUri[candidate.CanonicalUri!];
            PageAttempt attempt;
            try
            {
                attempt = await RetrieveOneAsync(
                    candidate.CanonicalUri!,
                    request.MaximumDocumentCharacters,
                    request.MaximumTotalCharacters - returnedCharacters,
                    request.DeadlineUtc,
                    source.DocumentLanguageCode,
                    deadlineCancellation.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                attempt = PageAttempt.Failure(
                    candidate.CanonicalUri!,
                    "internet_research_page_timeout",
                    requestTimedOut: true);
            }
            receipts.Add(attempt.Receipt);
            if (!attempt.Succeeded ||
                attempt.FinalCanonicalUri is null ||
                attempt.ContentExcerpt is null ||
                !acceptedFinalUris.Add(attempt.FinalCanonicalUri))
            {
                continue;
            }

            var normalizedDocumentLanguage = await NormalizeDocumentLanguageAsync(
                attempt.DocumentLanguageCode ?? source.DocumentLanguageCode,
                deadlineCancellation.Token);
            var sourceIdentity = LegendConnectResearchExternalDataPolicy.SourceIdentityForUri(
                attempt.FinalCanonicalUri);
            var documentIdentity = LegendLanguageIdentity.TextHash(
                "research-document|v3|" + sourceIdentity + "|" + attempt.ContentHash);
            var citationIdentity = LegendLanguageIdentity.TextHash(
                "research-citation|v3|" + documentIdentity);
            var retrievedUtc = attempt.Receipt.CompletedUtc;
            var sameOrigin = IsSameOrigin(candidate.CanonicalUri!, attempt.FinalCanonicalUri);
            var title = sameOrigin
                ? LegendConnectResearchExternalDataPolicy.SanitizeDisplayMetadata(source.Title, 500) ??
                  attempt.FinalCanonicalUri
                : attempt.FinalCanonicalUri;
            var retrievedSource = source with
            {
                SourceIdentity = sourceIdentity,
                CanonicalUri = attempt.FinalCanonicalUri,
                Title = title,
                RetrievedUtc = retrievedUtc,
                DocumentLanguageCode = normalizedDocumentLanguage,
                PublishedUtc = attempt.PublishedUtc ?? source.PublishedUtc,
                UpdatedUtc = attempt.UpdatedUtc ?? source.UpdatedUtc,
                IsUntrustedExternalData = true
            };
            if (!sameOrigin)
            {
                // A redirect destination is a different source. It may remain a useful
                // external observation, but authority metadata supplied for the requested
                // origin cannot follow it and become evidence or citation authority.
                retrievedSource = retrievedSource with
                {
                    Publisher = null,
                    SourceClass = LegendConnectResearchSourceClass.UnknownSource,
                    PublishedUtc = null,
                    Author = null,
                    UpdatedUtc = null,
                    EffectiveUtc = null,
                    MethodologyAvailable = false,
                    ProvenanceComplete = false,
                    LineageKind = LegendConnectResearchSourceLineageKind.Unknown,
                    OriginalSourceIdentity = null,
                    CommonOriginIdentity = null,
                    CitationTargetSourceIdentities = [],
                    AuthorityScopes = [],
                    IsControllingRecord = false
                };
            }
            sources.Add(retrievedSource);
            results.Add(candidate.Result with
            {
                SearchResultIdentity = LegendLanguageIdentity.TextHash(
                    "research-result|v3|" + candidate.Result.QueryIdentity + "|" + sourceIdentity),
                SourceIdentity = sourceIdentity,
                Title = title,
                CanonicalUri = attempt.FinalCanonicalUri,
                Snippet = sameOrigin ? candidate.Result.Snippet : null,
                DocumentLanguageCode = normalizedDocumentLanguage,
                IsUntrustedExternalData = true
            });
            documents.Add(new LegendConnectRetrievedDocument(
                documentIdentity,
                sourceIdentity,
                attempt.FinalCanonicalUri,
                attempt.ContentExcerpt,
                attempt.ContentHash!,
                retrievedUtc,
                true,
                null,
                normalizedDocumentLanguage,
                attempt.ContentType,
                attempt.Receipt.RedirectCount,
                attempt.Receipt.ReturnedBytes,
                true,
                LegendConnectResearchExternalDataPolicy.IsPotentialInstruction(attempt.ContentExcerpt)));
            citations.Add(new LegendConnectCitation(
                citationIdentity,
                sourceIdentity,
                documentIdentity,
                title,
                attempt.FinalCanonicalUri,
                retrievedUtc,
                normalizedDocumentLanguage,
                true));
            lineage.Add(new LegendConnectRetrievedPageLineage(
                candidate.CanonicalUri!,
                attempt.FinalCanonicalUri,
                sourceIdentity,
                documentIdentity,
                citationIdentity));
            returnedCharacters += attempt.ContentExcerpt.Length;
        }

        var succeeded = documents.Count > 0;
        return new LegendConnectResearchPageRetrievalResult(
            succeeded,
            TransportName,
            settingsIdentity,
            results,
            sources,
            documents,
            citations,
            lineage,
            receipts,
            (long)Math.Ceiling(clock.Elapsed.TotalMilliseconds),
            succeeded
                ? null
                : receipts.LastOrDefault()?.FailureReason ?? "internet_research_page_retrieval_failed",
            !succeeded && receipts.Any(item => item.FailureReason is
                "internet_research_page_timeout" or
                "internet_research_page_transport_failed"));
    }

    internal static bool IsBoundedRequest(LegendConnectResearchPageRetrievalRequest request) =>
        request.SessionId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(request.UserLanguageCode) &&
        request.SearchResults.Count is >= 1 and <= LegendConnectResearchContracts.MaximumResults &&
        request.Sources.Count is >= 1 and <= LegendConnectResearchContracts.MaximumResults &&
        request.MaximumDocuments is >= 1 and <= LegendConnectResearchContracts.MaximumDocuments &&
        request.MaximumDocumentCharacters is >= 1 and <= LegendConnectResearchContracts.MaximumDocumentCharacters &&
        request.MaximumTotalCharacters is >= 1 and <= LegendConnectResearchContracts.MaximumTotalDocumentCharacters &&
        request.DeadlineUtc > DateTime.UtcNow &&
        request.SearchResults.All(item => item.IsUntrustedExternalData) &&
        request.Sources.All(item => item.IsUntrustedExternalData);

    private async Task<PageAttempt> RetrieveOneAsync(
        string requestedCanonicalUri,
        int maximumDocumentCharacters,
        int remainingTotalCharacters,
        DateTime deadlineUtc,
        string? candidateDocumentLanguage,
        CancellationToken cancellationToken)
    {
        var startedUtc = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();
        var currentUri = requestedCanonicalUri;
        var visited = new HashSet<string>(StringComparer.Ordinal) { currentUri };
        var redirectCount = 0;
        var requestCount = 0;

        PageAttempt Failure(
            string reason,
            int? statusCode = null,
            string? contentType = null,
            long returnedBytes = 0,
            bool requestTimedOut = false) =>
            PageAttempt.Failure(
                requestedCanonicalUri,
                reason,
                currentUri,
                startedUtc,
                requestCount,
                redirectCount,
                statusCode,
                contentType,
                returnedBytes,
                (long)Math.Ceiling(clock.Elapsed.TotalMilliseconds),
                requestTimedOut);

        while (true)
        {
            var normalized = LegendConnectResearchNetworkPolicy.NormalizePublicHttpUri(currentUri);
            if (normalized is null)
                return Failure("internet_research_url_not_public");
            currentUri = normalized;
            var remainingDeadline = deadlineUtc - DateTime.UtcNow;
            if (remainingDeadline <= TimeSpan.Zero)
                return Failure("internet_research_total_deadline_exceeded", requestTimedOut: true);

            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCancellation.CancelAfter(
                remainingDeadline < TimeSpan.FromSeconds(LegendConnectResearchContracts.RequestTimeoutSeconds)
                    ? remainingDeadline
                    : TimeSpan.FromSeconds(LegendConnectResearchContracts.RequestTimeoutSeconds));
            using var pageRequest = new HttpRequestMessage(HttpMethod.Get, currentUri);
            pageRequest.Headers.Accept.ParseAdd("text/html, application/xhtml+xml, text/plain;q=0.9");
            pageRequest.Headers.UserAgent.ParseAdd("LEGEND-Governed-Research/1.0");
            requestCount++;

            HttpResponseMessage response;
            try
            {
                response = await _httpClientFactory.CreateClient(ClientName).SendAsync(
                    pageRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCancellation.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failure("internet_research_page_timeout", requestTimedOut: true);
            }
            catch (HttpRequestException)
            {
                return Failure("internet_research_page_transport_failed");
            }

            using (response)
            {
                if (IsRedirect(response.StatusCode))
                {
                    if (redirectCount >= LegendConnectResearchContracts.MaximumRedirects)
                        return Failure("internet_research_redirect_limit_exceeded", (int)response.StatusCode);
                    var location = response.Headers.Location;
                    if (location is null)
                        return Failure("internet_research_redirect_location_missing", (int)response.StatusCode);
                    var resolved = location.IsAbsoluteUri
                        ? location
                        : new Uri(new Uri(currentUri), location);
                    var redirectedUri = LegendConnectResearchNetworkPolicy.NormalizePublicHttpUri(resolved.AbsoluteUri);
                    if (redirectedUri is null)
                        return Failure("internet_research_redirect_not_public", (int)response.StatusCode);
                    if (!visited.Add(redirectedUri))
                        return Failure("internet_research_redirect_loop", (int)response.StatusCode);
                    redirectCount++;
                    currentUri = redirectedUri;
                    continue;
                }

                var statusCode = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                    return Failure("internet_research_page_http_failed", statusCode);
                if (response.Content.Headers.ContentDisposition?.DispositionType is { } disposition &&
                    disposition.Equals("attachment", StringComparison.OrdinalIgnoreCase))
                    return Failure("internet_research_file_download_blocked", statusCode);
                var contentType = response.Content.Headers.ContentType?.MediaType?.Trim().ToLowerInvariant();
                if (!LegendConnectResearchNetworkPolicy.IsSupportedContentType(contentType))
                    return Failure("internet_research_content_type_unsupported", statusCode, contentType);
                if (response.Content.Headers.ContentLength is > LegendConnectResearchContracts.MaximumPageBytes)
                    return Failure("internet_research_page_content_oversized", statusCode, contentType);

                var bytes = await ReadBoundedBytesAsync(
                    response.Content,
                    LegendConnectResearchContracts.MaximumPageBytes,
                    requestCancellation.Token);
                if (bytes is null)
                    return Failure("internet_research_page_content_oversized", statusCode, contentType);
                var encoding = ResolveEncoding(response.Content.Headers.ContentType?.CharSet);
                var rawText = encoding.GetString(bytes);
                var pageTimestamps = LegendConnectResearchPageMetadata.Extract(
                    rawText,
                    contentType!,
                    response.Content.Headers.LastModified);
                var plainText = LegendConnectResearchPageText.ExtractUntrustedText(rawText, contentType!);
                var characterLimit = Math.Min(maximumDocumentCharacters, remainingTotalCharacters);
                if (plainText.Length > characterLimit)
                    plainText = plainText[..characterLimit];
                if (string.IsNullOrWhiteSpace(plainText))
                    return Failure("internet_research_page_content_empty", statusCode, contentType, bytes.LongLength);

                var contentHash = LegendLanguageIdentity.TextHash(plainText);
                var completedUtc = DateTime.UtcNow;
                var documentLanguage = response.Content.Headers.ContentLanguage.FirstOrDefault() ??
                                       candidateDocumentLanguage;
                var receipt = new LegendConnectResearchPageReceipt(
                    LegendLanguageIdentity.TextHash(
                        "research-page-receipt|v1|" + requestedCanonicalUri + "|" + currentUri + "|" + contentHash),
                    requestedCanonicalUri,
                    currentUri,
                    startedUtc,
                    completedUtc,
                    TransportName,
                    ProviderName,
                    requestCount,
                    redirectCount,
                    statusCode,
                    contentType,
                    bytes.LongLength,
                    (long)Math.Ceiling(clock.Elapsed.TotalMilliseconds),
                    null,
                    "NotMeteredByTransport",
                    true,
                    null,
                    true,
                    true);
                return new PageAttempt(
                    true,
                    currentUri,
                    plainText,
                    contentHash,
                    contentType,
                    documentLanguage,
                    pageTimestamps.PublishedUtc,
                    pageTimestamps.UpdatedUtc,
                    receipt,
                    false);
            }
        }
    }

    private async Task<string?> NormalizeDocumentLanguageAsync(
        string? language,
        CancellationToken cancellationToken)
    {
        var candidate = language?.Split(',', ';').FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(candidate)
            ? null
            : await _languages.NormalizeEnabledTranslationLanguageReadOnlyAsync(candidate, cancellationToken);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool IsSameOrigin(string requestedCanonicalUri, string finalCanonicalUri) =>
        Uri.TryCreate(requestedCanonicalUri, UriKind.Absolute, out var requested) &&
        Uri.TryCreate(finalCanonicalUri, UriKind.Absolute, out var final) &&
        string.Equals(requested.Scheme, final.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(requested.IdnHost, final.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        requested.Port == final.Port;

    private static async Task<byte[]?> ReadBoundedBytesAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[8_192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return memory.ToArray();
            if (memory.Length + read > maximumBytes)
                return null;
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        try
        {
            return string.IsNullOrWhiteSpace(charset)
                ? Encoding.UTF8
                : Encoding.GetEncoding(charset.Trim('"', '\''));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private sealed record PageAttempt(
        bool Succeeded,
        string? FinalCanonicalUri,
        string? ContentExcerpt,
        string? ContentHash,
        string? ContentType,
        string? DocumentLanguageCode,
        DateTime? PublishedUtc,
        DateTime? UpdatedUtc,
        LegendConnectResearchPageReceipt Receipt,
        bool RequestTimedOut)
    {
        public static PageAttempt Failure(
            string requestedCanonicalUri,
            string reason,
            string? finalCanonicalUri = null,
            DateTime? requestedUtc = null,
            int requestCount = 0,
            int redirectCount = 0,
            int? statusCode = null,
            string? contentType = null,
            long returnedBytes = 0,
            long latencyMilliseconds = 0,
            bool requestTimedOut = false)
        {
            var started = requestedUtc ?? DateTime.UtcNow;
            var completed = DateTime.UtcNow;
            return new PageAttempt(
                false,
                finalCanonicalUri,
                null,
                null,
                contentType,
                null,
                null,
                null,
                new LegendConnectResearchPageReceipt(
                    LegendLanguageIdentity.TextHash(
                        "research-page-receipt|v1|" + requestedCanonicalUri + "|" + reason + "|" + completed.Ticks),
                    requestedCanonicalUri,
                    finalCanonicalUri,
                    started,
                    completed,
                    TransportName,
                    ProviderName,
                    requestCount,
                    redirectCount,
                    statusCode,
                    contentType,
                    returnedBytes,
                    latencyMilliseconds,
                    null,
                    "NotMeteredByTransport",
                    false,
                    reason,
                    true,
                    true),
                requestTimedOut);
        }
    }
}

/// <summary>
/// Extracts only explicit publication and update timestamps declared by the
/// retrieved same-origin representation. Ambiguous, conflicting, timezone-free,
/// or generic page dates are deliberately ignored rather than inferred.
/// </summary>
internal static class LegendConnectResearchPageMetadata
{
    private const int MaximumStructuredDataScripts = 32;
    private const int MaximumTimestampCandidates = 64;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex MetaTag = new(
        "<meta\\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex TimeTag = new(
        "<time\\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex Attribute = new(
        "(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+))",
        RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex JsonLdScript = new(
        "<script\\b(?=[^>]*\\btype\\s*=\\s*(?:\"application/ld\\+json\"|'application/ld\\+json'|application/ld\\+json))[^>]*>(?<json>.*?)</script\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
        RegexTimeout);
    private static readonly Regex ExplicitTimezone = new(
        "(?:Z|[+-]\\d{2}:?\\d{2}|GMT|UTC)\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly HashSet<string> PublishedMetadataNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "article:published_time",
        "og:published_time",
        "datepublished",
        "date_published",
        "publication_date",
        "publishdate",
        "dcterms.created",
        "dcterms.issued"
    };

    private static readonly HashSet<string> UpdatedMetadataNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "article:modified_time",
        "og:updated_time",
        "datemodified",
        "date_modified",
        "modified_date",
        "lastmodified",
        "dcterms.modified"
    };

    internal static LegendConnectResearchPageTimestamps Extract(
        string rawText,
        string contentType,
        DateTimeOffset? lastModified)
    {
        if (!contentType.Equals("text/html", StringComparison.OrdinalIgnoreCase) &&
            !contentType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
        {
            return new(null, lastModified?.UtcDateTime);
        }

        try
        {
            var published = new List<DateTime>();
            var updated = new List<DateTime>();
            CollectElementMetadata(rawText, MetaTag, published, updated);
            CollectElementMetadata(rawText, TimeTag, published, updated);
            CollectJsonLdMetadata(rawText, published, updated);

            var publishedUtc = ResolveUnambiguous(published);
            var updatedUtc = ResolveUnambiguous(updated);
            return new(
                publishedUtc.Value,
                updatedUtc.HasCandidates ? updatedUtc.Value : lastModified?.UtcDateTime);
        }
        catch (RegexMatchTimeoutException)
        {
            return new(null, lastModified?.UtcDateTime);
        }
    }

    private static void CollectElementMetadata(
        string html,
        Regex elementPattern,
        ICollection<DateTime> published,
        ICollection<DateTime> updated)
    {
        foreach (Match match in elementPattern.Matches(html))
        {
            var attributes = ReadAttributes(match.Value);
            if (!attributes.TryGetValue("content", out var value) &&
                !attributes.TryGetValue("datetime", out value))
                continue;
            if (!TryParseExplicitUtc(value, out var timestamp))
                continue;

            var metadataName = attributes.TryGetValue("property", out var property)
                ? property
                : attributes.TryGetValue("name", out var name)
                    ? name
                    : attributes.TryGetValue("itemprop", out var itemprop)
                        ? itemprop
                        : null;
            if (metadataName is null)
                continue;
            if (PublishedMetadataNames.Contains(metadataName))
                AddBounded(published, timestamp);
            else if (UpdatedMetadataNames.Contains(metadataName))
                AddBounded(updated, timestamp);
        }
    }

    private static Dictionary<string, string> ReadAttributes(string element)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in Attribute.Matches(element))
        {
            var name = attribute.Groups["name"].Value;
            if (!attributes.ContainsKey(name))
                attributes[name] = WebUtility.HtmlDecode(attribute.Groups["value"].Value).Trim();
        }
        return attributes;
    }

    private static void CollectJsonLdMetadata(
        string html,
        ICollection<DateTime> published,
        ICollection<DateTime> updated)
    {
        var scriptCount = 0;
        foreach (Match match in JsonLdScript.Matches(html))
        {
            if (++scriptCount > MaximumStructuredDataScripts)
                break;
            try
            {
                using var document = JsonDocument.Parse(match.Groups["json"].Value, new JsonDocumentOptions
                {
                    MaxDepth = 32,
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
                CollectJsonLdElement(document.RootElement, published, updated);
            }
            catch (JsonException)
            {
                // Malformed untrusted structured data is not evidence metadata.
            }
        }
    }

    private static void CollectJsonLdElement(
        JsonElement element,
        ICollection<DateTime> published,
        ICollection<DateTime> updated)
    {
        if (published.Count + updated.Count >= MaximumTimestampCandidates)
            return;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectJsonLdElement(item, published, updated);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("datePublished"))
                CollectJsonTimestamp(property.Value, published);
            else if (property.NameEquals("dateModified"))
                CollectJsonTimestamp(property.Value, updated);
            else if (property.NameEquals("@graph") || property.NameEquals("mainEntity"))
                CollectJsonLdElement(property.Value, published, updated);
        }
    }

    private static void CollectJsonTimestamp(JsonElement value, ICollection<DateTime> destination)
    {
        if (value.ValueKind == JsonValueKind.String &&
            TryParseExplicitUtc(value.GetString(), out var timestamp))
            AddBounded(destination, timestamp);
    }

    private static void AddBounded(ICollection<DateTime> destination, DateTime timestamp)
    {
        if (destination.Count < MaximumTimestampCandidates)
            destination.Add(timestamp);
    }

    private static TimestampResolution ResolveUnambiguous(IEnumerable<DateTime> candidates)
    {
        var values = candidates.Select(value => value.ToUniversalTime()).Distinct().Take(2).ToArray();
        return new(values.Length == 1 ? values[0] : null, values.Length > 0);
    }

    private static bool TryParseExplicitUtc(string? value, out DateTime timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 100 ||
            !ExplicitTimezone.IsMatch(value))
            return false;
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
            return false;
        timestamp = parsed.UtcDateTime;
        return true;
    }

    private readonly record struct TimestampResolution(DateTime? Value, bool HasCandidates);
}

internal sealed record LegendConnectResearchPageTimestamps(
    DateTime? PublishedUtc,
    DateTime? UpdatedUtc);

/// <summary>
/// URL and socket policy shared by search-candidate validation and the single
/// direct page client. DNS is re-evaluated inside ConnectCallback and a request
/// is rejected if any answer is not a public unicast address.
/// </summary>
internal static class LegendConnectResearchNetworkPolicy
{
    private static readonly HashSet<string> SupportedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/html",
        "application/xhtml+xml",
        "text/plain"
    };

    internal static string? NormalizePublicHttpUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 2_000 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !HasSafeUrlQuery(uri) ||
            IsBlockedHost(uri.IdnHost))
            return null;
        var addressHost = uri.IdnHost.Trim('[', ']');
        if (IPAddress.TryParse(addressHost, out var address) && !IsPublicAddress(address))
            return null;
        try
        {
            var builder = new UriBuilder(uri)
            {
                Host = uri.IdnHost.ToLowerInvariant(),
                Fragment = string.Empty
            };
            if ((builder.Scheme == Uri.UriSchemeHttp && builder.Port == 80) ||
                (builder.Scheme == Uri.UriSchemeHttps && builder.Port == 443))
                builder.Port = -1;
            return builder.Uri.AbsoluteUri;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    internal static bool IsSupportedContentType(string? contentType) =>
        contentType is not null && SupportedContentTypes.Contains(contentType);

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.IPv6None) ||
            address.IsIPv6LinkLocal ||
            address.IsIPv6SiteLocal ||
            address.IsIPv6Multicast)
            return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var globalUnicast = (bytes[0] & 0xE0) == 0x20;
            var documentation = bytes[0] == 0x20 && bytes[1] == 0x01 &&
                                bytes[2] == 0x0D && bytes[3] == 0xB8;
            return globalUnicast && !documentation;
        }
        if (bytes.Length != 4)
            return false;
        return bytes[0] != 0 &&
               bytes[0] != 10 &&
               bytes[0] != 127 &&
               !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
               !(bytes[0] == 169 && bytes[1] == 254) &&
               !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
               !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2) &&
               !(bytes[0] == 192 && bytes[1] == 168) &&
               !(bytes[0] == 198 && bytes[1] is 18 or 19) &&
               !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) &&
               !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) &&
               bytes[0] < 224;
    }

    internal static SocketsHttpHandler CreatePublicReadOnlyHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip |
                                 DecompressionMethods.Deflate |
                                 DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        Credentials = null,
        MaxConnectionsPerServer = 2,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ActivityHeadersPropagator = null,
        UseCookies = false,
        UseProxy = false,
        ConnectCallback = ConnectToPublicAddressAsync
    };

    private static bool IsBlockedHost(string host)
    {
        var normalized = host.TrimEnd('.').ToLowerInvariant();
        return normalized is "localhost" or "metadata" or "metadata.google.internal" ||
               normalized.EndsWith(".localhost", StringComparison.Ordinal) ||
               normalized.EndsWith(".local", StringComparison.Ordinal) ||
               normalized.EndsWith(".internal", StringComparison.Ordinal) ||
               normalized.EndsWith(".home", StringComparison.Ordinal) ||
               normalized.EndsWith(".lan", StringComparison.Ordinal);
    }

    private static bool HasSafeUrlQuery(Uri uri)
    {
        try
        {
            return LegendConnectResearchExternalDataPolicy.IsSafeExternalUrlQuery(
                Uri.UnescapeDataString(uri.Query));
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new HttpRequestException("Research destination did not resolve exclusively to public addresses.");
        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = exception;
                if (exception is OperationCanceledException)
                    throw;
            }
        }
        throw new HttpRequestException("No public research destination was reachable.", lastError);
    }
}

internal static partial class LegendConnectResearchPageText
{
    [GeneratedRegex(
        @"<(script|style|noscript|iframe|object|embed|form|svg)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DangerousBlocksRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TagsRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    internal static string ExtractUntrustedText(string rawText, string contentType)
    {
        if (string.Equals(contentType, "text/plain", StringComparison.OrdinalIgnoreCase))
            return Normalize(rawText);
        var withoutExecutableBlocks = DangerousBlocksRegex().Replace(rawText, " ");
        var withoutComments = CommentRegex().Replace(withoutExecutableBlocks, " ");
        var withoutTags = TagsRegex().Replace(withoutComments, " ");
        return Normalize(WebUtility.HtmlDecode(withoutTags));
    }

    private static string Normalize(string value) =>
        WhitespaceRegex().Replace(
            new string(value
                .Where(character => !char.IsControl(character) || char.IsWhiteSpace(character))
                .ToArray()),
            " ").Trim();
}
