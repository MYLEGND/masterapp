using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Domain.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

/// <summary>
/// Provider transport for the canonical research lifecycle. It owns no
/// research-needed decision, Founder authorization, evidence decision,
/// presentation, learning, or serving authority. Its only role is to execute
/// bounded public searches and return an evidence packet for validation by
/// <see cref="LegendConnectOperations"/>.
/// </summary>
internal sealed class OpenAiLegendConnectInternetResearchTransport
    : ILegendConnectInternetResearchTransport
{
    private const string ClientName = "LegendInternetResearch";
    private const string DefaultEndpoint = "https://api.openai.com/v1/responses";
    private const string SearchContextSize = "medium";
    private const int MaximumOutputTokens = 6_000;

    private const string Instructions = """
You are a non-authoritative evidence extractor for one bounded LEGEND internet-research session.

Use web search for the exact supplied question. Return only the requested JSON.
- Use only sources actually returned by web search in this request.
- Prefer primary sources, official documentation, standards bodies, peer-reviewed publications, and direct institutional records.
- Keep each factual claim atomic and give it a stable claim_id.
- Attach every claim to every independent source URL that directly supports it.
- Record materially contradicting evidence separately under contradictions with the same claim_id.
- When an internal LEGEND answer is supplied, explicitly compare current external evidence against it and place every material disagreement in contradictions.
- Do not resolve conflicts, infer Founder approval, write knowledge, or claim that external material is canonical LEGEND evidence.
- Do not use authenticated, private, paywalled, mutation-capable, or non-public access.
- Source excerpts must be short evidence-bearing passages, not whole documents.
""";

    private const string ResultSchema = """
{
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "sources": {
      "type": "array",
      "maxItems": 8,
      "items": {
        "type": "object",
        "additionalProperties": false,
        "properties": {
          "url": { "type": "string", "minLength": 8, "maxLength": 2000 },
          "title": { "type": "string", "minLength": 1, "maxLength": 500 },
          "publisher": { "type": ["string", "null"], "maxLength": 300 },
          "source_class": { "type": "string", "minLength": 1, "maxLength": 80 },
          "published_utc": { "type": ["string", "null"], "maxLength": 40 },
          "excerpt": { "type": "string", "minLength": 1, "maxLength": 4000 }
        },
        "required": ["url", "title", "publisher", "source_class", "published_utc", "excerpt"]
      }
    },
    "claims": {
      "type": "array",
      "maxItems": 12,
      "items": {
        "type": "object",
        "additionalProperties": false,
        "properties": {
          "claim_id": { "type": "string", "minLength": 1, "maxLength": 160 },
          "statement": { "type": "string", "minLength": 1, "maxLength": 1200 },
          "source_urls": {
            "type": "array",
            "minItems": 1,
            "maxItems": 8,
            "items": { "type": "string", "minLength": 8, "maxLength": 2000 }
          },
          "observed_utc": { "type": ["string", "null"], "maxLength": 40 }
        },
        "required": ["claim_id", "statement", "source_urls", "observed_utc"]
      }
    },
    "contradictions": {
      "type": "array",
      "maxItems": 12,
      "items": {
        "type": "object",
        "additionalProperties": false,
        "properties": {
          "claim_id": { "type": "string", "minLength": 1, "maxLength": 160 },
          "statement": { "type": "string", "minLength": 1, "maxLength": 1200 },
          "source_urls": {
            "type": "array",
            "minItems": 1,
            "maxItems": 8,
            "items": { "type": "string", "minLength": 8, "maxLength": 2000 }
          },
          "observed_utc": { "type": ["string", "null"], "maxLength": 40 }
        },
        "required": ["claim_id", "statement", "source_urls", "observed_utc"]
      }
    }
  },
  "required": ["sources", "claims", "contradictions"]
}
""";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenAiLegendConnectInternetResearchTransport> _logger;

    public OpenAiLegendConnectInternetResearchTransport(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<OpenAiLegendConnectInternetResearchTransport> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<LegendConnectInternetResearchTransportResult> SearchAsync(
        LegendConnectInternetResearchTransportRequest request,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var settingsIdentity = "Unavailable";
        LegendConnectInternetResearchTransportResult Failure(
            string reason,
            bool retryable,
            string? model = null) =>
            new(
                false,
                "OpenAIResponsesWebSearch",
                model,
                settingsIdentity,
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                (long)Math.Ceiling(clock.Elapsed.TotalMilliseconds),
                null,
                reason,
                retryable);

        if (!TryReadConfiguration(
                out var endpoint,
                out var apiKey,
                out var model,
                out settingsIdentity))
        {
            return Failure(
                "internet_research_configuration_unavailable",
                false,
                model);
        }

        if (!IsBoundedRequest(request))
            return Failure("internet_research_request_invalid", false, model);

        try
        {
            using var schema = JsonDocument.Parse(ResultSchema);
            var payload = new
            {
                model,
                store = false,
                instructions = Instructions,
                input = JsonSerializer.Serialize(new
                {
                    question = request.Question,
                    source_language = request.SourceLanguageCode,
                    exact_bounded_queries = request.Queries.Select(item => item.Query).ToArray(),
                    internal_legend_answer = request.InternalAnswer,
                    internal_legend_reason = request.InternalReasonCode,
                    maximum_results = request.MaximumResults,
                    maximum_documents = request.MaximumDocuments,
                    maximum_claims = request.MaximumClaims,
                    maximum_excerpt_characters = request.MaximumDocumentCharacters
                }),
                tools = new[]
                {
                    new
                    {
                        type = "web_search",
                        search_context_size = SearchContextSize
                    }
                },
                tool_choice = new
                {
                    type = "web_search"
                },
                max_tool_calls = request.Queries.Count,
                include = new[]
                {
                    "web_search_call.action.sources"
                },
                reasoning = new
                {
                    effort = "low"
                },
                max_output_tokens = MaximumOutputTokens,
                text = new
                {
                    format = new
                    {
                        type = "json_schema",
                        name = "legend_governed_internet_research",
                        strict = true,
                        schema = schema.RootElement.Clone()
                    }
                }
            };

            using var providerRequest = new HttpRequestMessage(
                HttpMethod.Post,
                endpoint)
            {
                Content = JsonContent.Create(payload)
            };
            providerRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await _httpClientFactory
                .CreateClient(ClientName)
                .SendAsync(
                    providerRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "LEGEND bounded internet research transport failed. StatusCode={StatusCode}",
                    (int)response.StatusCode);
                return Failure(
                    "internet_research_provider_failed",
                    response.StatusCode is
                        System.Net.HttpStatusCode.RequestTimeout or
                        System.Net.HttpStatusCode.TooManyRequests or
                        System.Net.HttpStatusCode.InternalServerError or
                        System.Net.HttpStatusCode.BadGateway or
                        System.Net.HttpStatusCode.ServiceUnavailable or
                        System.Net.HttpStatusCode.GatewayTimeout,
                    model);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(
                cancellationToken);
            using var responseDocument = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            var root = responseDocument.RootElement;
            if (!root.TryGetProperty("status", out var status) ||
                !string.Equals(status.GetString(), "completed", StringComparison.Ordinal))
            {
                return Failure("internet_research_provider_incomplete", true, model);
            }

            var outputText = ExtractOutputText(root);
            if (string.IsNullOrWhiteSpace(outputText))
                return Failure("internet_research_provider_output_missing", false, model);
            using var structured = JsonDocument.Parse(outputText);
            if (!TryBuildEvidencePacket(
                    request,
                    root,
                    structured.RootElement,
                    out var executedQueries,
                    out var searchResults,
                    out var sources,
                    out var documents,
                    out var claims,
                    out var contradictions,
                    out var citations))
            {
                return Failure("internet_research_provider_lineage_invalid", false, model);
            }

            var returnedModel = root.TryGetProperty("model", out var actualModel) &&
                                actualModel.ValueKind == JsonValueKind.String
                ? actualModel.GetString()
                : model;
            var cost = ReadCostMicrounits(root);
            return new LegendConnectInternetResearchTransportResult(
                true,
                "OpenAIResponsesWebSearch",
                returnedModel,
                settingsIdentity,
                executedQueries,
                searchResults,
                sources,
                documents,
                claims,
                contradictions,
                citations,
                (long)Math.Ceiling(clock.Elapsed.TotalMilliseconds),
                cost,
                null,
                false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure("internet_research_provider_timeout", true, model);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "LEGEND bounded internet research transport failed.");
            return Failure("internet_research_transport_failed", true, model);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "LEGEND bounded internet research response was invalid.");
            return Failure("internet_research_provider_output_invalid", false, model);
        }
    }

    private bool TryReadConfiguration(
        out Uri endpoint,
        out string apiKey,
        out string model,
        out string settingsIdentity)
    {
        var endpointValue = (_configuration["LegendConnect:InternetResearch:Endpoint"] ??
                             DefaultEndpoint).Trim();
        apiKey = (_configuration["LegendConnect:InternetResearch:ApiKey"] ??
                  _configuration["OpenAI:ApiKey"] ??
                  Environment.GetEnvironmentVariable("OPENAI_API_KEY") ??
                  Environment.GetEnvironmentVariable("OpenAI__ApiKey") ??
                  string.Empty).Trim();
        model = (_configuration["LegendConnect:InternetResearch:Model"] ??
                 _configuration["OpenAI:LegendFounderAiModel"] ??
                 _configuration["OpenAI:Model"] ??
                 "gpt-5").Trim();
        settingsIdentity = LegendLanguageIdentity.TextHash(string.Join(
            '|',
            "legend-internet-research:v1",
            endpointValue.ToLowerInvariant(),
            model,
            SearchContextSize,
            "reasoning:low",
            "max-output:" + MaximumOutputTokens,
            "schema:" + LegendLanguageIdentity.TextHash(ResultSchema)));
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var parsedEndpoint) ||
            parsedEndpoint.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(model) ||
            model.Length > 160)
        {
            endpoint = new Uri(DefaultEndpoint);
            apiKey = string.Empty;
            return false;
        }

        endpoint = parsedEndpoint;
        return true;
    }

    private static bool IsBoundedRequest(
        LegendConnectInternetResearchTransportRequest request) =>
        request.SessionId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(request.Question) &&
        request.Question.Length <= LegendConnectResearchContracts.MaximumQueryCharacters &&
        (request.InternalAnswer?.Length ?? 0) <= 8_000 &&
        request.Queries.Count is >= 1 and <= LegendConnectResearchContracts.MaximumQueries &&
        request.Queries.All(item =>
            !string.IsNullOrWhiteSpace(item.QueryIdentity) &&
            !string.IsNullOrWhiteSpace(item.Query) &&
            item.Query.Length <= LegendConnectResearchContracts.MaximumQueryCharacters) &&
        request.MaximumResults is >= 1 and <= LegendConnectResearchContracts.MaximumResults &&
        request.MaximumDocuments is >= 1 and <= LegendConnectResearchContracts.MaximumDocuments &&
        request.MaximumClaims is >= 1 and <= LegendConnectResearchContracts.MaximumClaims &&
        request.MaximumDocumentCharacters is >= 1 and <= LegendConnectResearchContracts.MaximumDocumentCharacters;

    private static bool TryBuildEvidencePacket(
        LegendConnectInternetResearchTransportRequest request,
        JsonElement responseRoot,
        JsonElement structured,
        out IReadOnlyList<LegendConnectBoundedSearchQuery> executedQueries,
        out IReadOnlyList<LegendConnectSearchResult> searchResults,
        out IReadOnlyList<LegendConnectResearchSourceIdentity> sources,
        out IReadOnlyList<LegendConnectRetrievedDocument> documents,
        out IReadOnlyList<LegendConnectClaimEvidence> claims,
        out IReadOnlyList<LegendConnectContradictingEvidence> contradictions,
        out IReadOnlyList<LegendConnectCitation> citations)
    {
        executedQueries = [];
        searchResults = [];
        sources = [];
        documents = [];
        claims = [];
        contradictions = [];
        citations = [];
        if (structured.ValueKind != JsonValueKind.Object ||
            !TryReadExecutedSearches(responseRoot, request, out var searches) ||
            !structured.TryGetProperty("sources", out var sourceArray) ||
            sourceArray.ValueKind != JsonValueKind.Array ||
            !structured.TryGetProperty("claims", out var claimArray) ||
            claimArray.ValueKind != JsonValueKind.Array ||
            !structured.TryGetProperty("contradictions", out var contradictionArray) ||
            contradictionArray.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        executedQueries = searches.Select(item => item.Query).ToArray();
        var actualSources = searches
            .SelectMany(item => item.Sources.Select(source => new
            {
                item.Query.QueryIdentity,
                Source = source
            }))
            .GroupBy(item => item.Source.Uri, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(request.MaximumResults)
            .ToArray();
        var actualByUri = actualSources.ToDictionary(
            item => item.Source.Uri,
            item => item,
            StringComparer.Ordinal);

        var sourceRows = new List<LegendConnectResearchSourceIdentity>();
        var documentRows = new List<LegendConnectRetrievedDocument>();
        var citationRows = new List<LegendConnectCitation>();
        var sourceArtifacts = new Dictionary<string, SourceArtifacts>(StringComparer.Ordinal);
        var retrievedUtc = DateTime.UtcNow;
        foreach (var item in sourceArray.EnumerateArray())
        {
            var rawUrl = ReadString(item, "url");
            var canonicalUri = CanonicalizePublicUri(rawUrl);
            var excerpt = ReadString(item, "excerpt")?.Trim();
            if (canonicalUri is null ||
                !actualByUri.TryGetValue(canonicalUri, out var actual) ||
                string.IsNullOrWhiteSpace(excerpt))
            {
                continue;
            }
            if (excerpt.Length > request.MaximumDocumentCharacters)
                excerpt = excerpt[..request.MaximumDocumentCharacters];
            var sourceIdentity = LegendLanguageIdentity.TextHash(
                "research-source|v1|" + canonicalUri);
            if (sourceArtifacts.ContainsKey(canonicalUri))
                continue;
            var documentIdentity = LegendLanguageIdentity.TextHash(
                "research-document|v1|" + sourceIdentity + "|" + excerpt);
            var citationIdentity = LegendLanguageIdentity.TextHash(
                "research-citation|v1|" + documentIdentity);
            var title = ReadString(item, "title")?.Trim();
            if (string.IsNullOrWhiteSpace(title))
                title = actual.Source.Title;
            var publishedUtc = TryReadDateTime(item, "published_utc");
            sourceRows.Add(new LegendConnectResearchSourceIdentity(
                sourceIdentity,
                canonicalUri,
                title ?? canonicalUri,
                ReadString(item, "publisher")?.Trim(),
                ReadString(item, "source_class")?.Trim() ?? "External",
                publishedUtc,
                retrievedUtc));
            documentRows.Add(new LegendConnectRetrievedDocument(
                documentIdentity,
                sourceIdentity,
                canonicalUri,
                excerpt,
                LegendLanguageIdentity.TextHash(excerpt),
                retrievedUtc,
                true,
                null));
            citationRows.Add(new LegendConnectCitation(
                citationIdentity,
                sourceIdentity,
                documentIdentity,
                title ?? canonicalUri,
                canonicalUri,
                retrievedUtc));
            sourceArtifacts[canonicalUri] = new SourceArtifacts(
                sourceIdentity,
                documentIdentity,
                citationIdentity);
        }

        var resultRows = actualSources
            .Where(item => sourceArtifacts.ContainsKey(item.Source.Uri))
            .Select((item, index) =>
            {
                var artifact = sourceArtifacts[item.Source.Uri];
                return new LegendConnectSearchResult(
                    LegendLanguageIdentity.TextHash(
                        "research-result|v1|" + item.QueryIdentity + "|" + artifact.SourceIdentity),
                    item.QueryIdentity,
                    index + 1,
                    artifact.SourceIdentity,
                    item.Source.Title ?? item.Source.Uri,
                    item.Source.Uri,
                    null);
            })
            .ToArray();
        var claimRows = ReadClaims<LegendConnectClaimEvidence>(
            claimArray,
            sourceArtifacts,
            request.MaximumClaims,
            (evidence, claimIdentity, statement, artifact, observedUtc) =>
                new LegendConnectClaimEvidence(
                    evidence,
                    claimIdentity,
                    statement,
                    artifact.SourceIdentity,
                    artifact.DocumentIdentity,
                    artifact.CitationIdentity,
                    observedUtc));
        var contradictionRows = ReadClaims<LegendConnectContradictingEvidence>(
            contradictionArray,
            sourceArtifacts,
            request.MaximumClaims,
            (evidence, claimIdentity, statement, artifact, observedUtc) =>
                new LegendConnectContradictingEvidence(
                    evidence,
                    claimIdentity,
                    statement,
                    artifact.SourceIdentity,
                    artifact.DocumentIdentity,
                    artifact.CitationIdentity,
                    observedUtc));

        searchResults = resultRows;
        sources = sourceRows;
        documents = documentRows;
        claims = claimRows;
        contradictions = contradictionRows;
        citations = citationRows;
        return searches.Count > 0;
    }

    private static IReadOnlyList<T> ReadClaims<T>(
        JsonElement array,
        IReadOnlyDictionary<string, SourceArtifacts> sources,
        int maximumClaims,
        Func<string, string, string, SourceArtifacts, DateTime?, T> factory)
    {
        var rows = new List<T>();
        foreach (var item in array.EnumerateArray())
        {
            var claimIdentity = ReadString(item, "claim_id")?.Trim();
            var statement = ReadString(item, "statement")?.Trim();
            if (string.IsNullOrWhiteSpace(claimIdentity) ||
                string.IsNullOrWhiteSpace(statement) ||
                !item.TryGetProperty("source_urls", out var urls) ||
                urls.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            var observedUtc = TryReadDateTime(item, "observed_utc");
            foreach (var url in urls.EnumerateArray())
            {
                var canonicalUri = CanonicalizePublicUri(url.GetString());
                if (canonicalUri is null ||
                    !sources.TryGetValue(canonicalUri, out var artifact))
                {
                    continue;
                }
                var evidenceIdentity = LegendLanguageIdentity.TextHash(
                    "research-evidence|v1|" +
                    claimIdentity + "|" + artifact.SourceIdentity + "|" + statement);
                rows.Add(factory(
                    evidenceIdentity,
                    claimIdentity,
                    statement,
                    artifact,
                    observedUtc));
                if (rows.Count >= maximumClaims)
                    return rows;
            }
        }
        return rows;
    }

    private static bool TryReadExecutedSearches(
        JsonElement root,
        LegendConnectInternetResearchTransportRequest request,
        out IReadOnlyList<ExecutedSearch> searches)
    {
        var rows = new List<ExecutedSearch>();
        if (!root.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
        {
            searches = [];
            return false;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!string.Equals(ReadString(item, "type"), "web_search_call", StringComparison.Ordinal) ||
                !item.TryGetProperty("action", out var action) ||
                action.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var queryText = ReadString(action, "query")?.Trim();
            if (string.IsNullOrWhiteSpace(queryText) ||
                queryText.Length > LegendConnectResearchContracts.MaximumQueryCharacters ||
                !action.TryGetProperty("sources", out var sourceArray) ||
                sourceArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            var sourceRows = sourceArray.EnumerateArray()
                .Select(source => new
                {
                    Uri = CanonicalizePublicUri(ReadString(source, "url")),
                    Title = ReadString(source, "title")?.Trim()
                })
                .Where(source => source.Uri is not null)
                .Select(source => new ExecutedSource(
                    source.Uri!,
                    source.Title))
                .DistinctBy(source => source.Uri, StringComparer.Ordinal)
                .Take(request.MaximumResults)
                .ToArray();
            if (sourceRows.Length == 0)
                continue;
            var identity = LegendLanguageIdentity.TextHash(
                "legend-research-executed-query|v1|" +
                request.SourceLanguageCode + "|" + queryText);
            rows.Add(new ExecutedSearch(
                new LegendConnectBoundedSearchQuery(
                    identity,
                    rows.Count + 1,
                    queryText,
                    request.SourceLanguageCode,
                    request.MaximumResults),
                sourceRows));
            if (rows.Count >= LegendConnectResearchContracts.MaximumQueries)
                break;
        }

        searches = rows;
        return rows.Count > 0;
    }

    private static string? ExtractOutputText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in output.EnumerateArray())
        {
            if (!string.Equals(ReadString(item, "type"), "message", StringComparison.Ordinal) ||
                !item.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in content.EnumerateArray())
            {
                if (string.Equals(ReadString(part, "type"), "output_text", StringComparison.Ordinal))
                    return ReadString(part, "text");
            }
        }
        return null;
    }

    private static long? ReadCostMicrounits(JsonElement root) =>
        root.TryGetProperty("usage", out var usage) &&
        usage.ValueKind == JsonValueKind.Object &&
        usage.TryGetProperty("cost_microunits", out var cost) &&
        cost.TryGetInt64(out var value) && value >= 0
            ? value
            : null;

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime? TryReadDateTime(JsonElement root, string property) =>
        DateTime.TryParse(
            ReadString(root, property),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var value)
                ? value
                : null;

    private static string? CanonicalizePublicUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }
        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty
        };
        return builder.Uri.AbsoluteUri;
    }

    private sealed record ExecutedSearch(
        LegendConnectBoundedSearchQuery Query,
        IReadOnlyList<ExecutedSource> Sources);

    private sealed record ExecutedSource(
        string Uri,
        string? Title);

    private sealed record SourceArtifacts(
        string SourceIdentity,
        string DocumentIdentity,
        string CitationIdentity);
}
