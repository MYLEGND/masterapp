using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Infrastructure.WebsiteEditing;

namespace Infrastructure.WebsiteEditing;

public sealed record WebsiteStudioAiContext(
    string SiteKey,
    string PagePath,
    string? SelectedElementId,
    string? SelectedSectionId,
    string? SelectedText,
    string? BusinessName,
    string? BusinessType,
    string? Services,
    string? Hours,
    IReadOnlyList<WebsiteBreakpointDefinition> Breakpoints,
    string? PageTitle,
    string? PageDescription);

public sealed record WebsiteStudioAiProviderRequest(
    string Mode,
    string Instruction,
    WebsiteStudioAiContext Context);

public sealed record WebsiteStudioAiProviderProposal(
    string Summary,
    IReadOnlyList<WebsiteStudioAiOperation> Operations);

public interface IWebsiteStudioAiProposalService
{
    Task<WebsiteStudioAiProviderProposal> ProposeAsync(
        WebsiteStudioAiProviderRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bounded Website Studio proposal transport. It uses the platform's existing OpenAI
/// configuration and HTTP client factory. It never receives editor tickets, owner IDs,
/// credentials, private CRM data, or analytics destinations, and it never persists.
/// </summary>
public sealed class WebsiteStudioAiProposalService(
    IHttpClientFactory clients,
    IConfiguration configuration,
    ILogger<WebsiteStudioAiProposalService> logger) : IWebsiteStudioAiProposalService
{
    private const string DefaultBaseUrl = "https://api.openai.com";
    private const int MaxInstructionChars = 2_000;
    private const int MaxContextChars = 18_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<WebsiteStudioAiProviderProposal> ProposeAsync(
        WebsiteStudioAiProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        var apiKey = ResolveApiKey();
        var model = ResolveModel();
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("website_studio_ai_not_configured");

        var mode = (request.Mode ?? string.Empty).Trim().ToLowerInvariant();
        if (mode is not ("responsive" or "create"))
            throw new ArgumentException("Website AI mode must be responsive or create.");

        var instruction = (request.Instruction ?? string.Empty).Trim();
        if (instruction.Length == 0 || instruction.Length > MaxInstructionChars)
            throw new ArgumentException("Website AI instructions must be 1–2,000 characters.");

        var payload = BuildContextPayload(request.Context);
        if (payload.Length > MaxContextChars)
            payload = payload[..MaxContextChars];

        var system = mode == "responsive"
            ? ResponsiveSystemPrompt
            : CreationSystemPrompt;
        var body = new
        {
            model,
            input = new object[]
            {
                new { role = "system", content = system },
                new
                {
                    role = "user",
                    content =
                        "USER INSTRUCTION:\n" + instruction +
                        "\n\nAUTHORIZED CURRENT-PAGE CONTEXT:\n" + payload +
                        "\n\nReturn only the structured proposal."
                }
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "website_studio_proposal",
                    strict = false,
                    schema = BuildSchema()
                }
            }
        };

        var endpoint = ResolveEndpoint();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(ResolveTimeoutSeconds()));

        try
        {
            using var response = await clients.CreateClient().SendAsync(httpRequest, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Website Studio AI provider returned HTTP {Status}. Mode={Mode}",
                    (int)response.StatusCode,
                    mode);
                throw new InvalidOperationException("website_studio_ai_provider_failed");
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            var proposal = ParseResponse(json);
            if (proposal.Operations.Count > 20)
                throw new InvalidOperationException("website_studio_ai_too_many_operations");
            return proposal;
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("website_studio_ai_timeout");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Website Studio AI transport failed.");
            throw new InvalidOperationException("website_studio_ai_provider_unavailable", ex);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Website Studio AI provider returned invalid JSON.");
            throw new InvalidOperationException("website_studio_ai_invalid_output", ex);
        }
    }

    private string ResolveApiKey() =>
        (configuration["OpenAI:ApiKey"] ??
         Environment.GetEnvironmentVariable("OPENAI_API_KEY") ??
         Environment.GetEnvironmentVariable("OpenAI__ApiKey") ??
         string.Empty).Trim();

    private string ResolveModel() =>
        (configuration["OpenAI:WebsiteStudioModel"] ??
         configuration["OpenAI:Model"] ??
         configuration["OpenAI:LegendFounderAiModel"] ??
         string.Empty).Trim();

    private Uri ResolveEndpoint()
    {
        var configured = configuration["OpenAI:BaseUrl"];
        var root = string.IsNullOrWhiteSpace(configured)
            ? DefaultBaseUrl
            : configured.Trim().TrimEnd('/');
        if (!Uri.TryCreate(root + "/v1/responses", UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("website_studio_ai_endpoint_invalid");
        return uri;
    }

    private int ResolveTimeoutSeconds() =>
        int.TryParse(configuration["OpenAI:WebsiteStudioTimeoutSeconds"], out var value) &&
        value is >= 5 and <= 120
            ? value
            : 45;

    private static string BuildContextPayload(WebsiteStudioAiContext context)
    {
        var selectedText = context.SelectedText;
        if (selectedText?.Length > 4_000) selectedText = selectedText[..4_000];
        var safe = new
        {
            context.SiteKey,
            context.PagePath,
            context.SelectedElementId,
            context.SelectedSectionId,
            selectedText,
            business = new
            {
                context.BusinessName,
                context.BusinessType,
                services = Clamp(context.Services, 4_000),
                hours = Clamp(context.Hours, 2_000)
            },
            breakpoints = context.Breakpoints.Select(value => new
            {
                value.Key,
                value.Label,
                value.MinWidth,
                value.MaxWidth
            }),
            page = new
            {
                context.PageTitle,
                context.PageDescription
            }
        };
        return JsonSerializer.Serialize(safe, JsonOptions);
    }

    private static string? Clamp(string? value, int maximum)
    {
        if (value is null) return null;
        var clean = value.Trim();
        return clean.Length <= maximum ? clean : clean[..maximum];
    }

    private static WebsiteStudioAiProviderProposal ParseResponse(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        if (!document.RootElement.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("website_studio_ai_empty_output");

        string? text = null;
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var textValue) &&
                    textValue.ValueKind == JsonValueKind.String)
                {
                    text = textValue.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) break;
                }
            }
            if (!string.IsNullOrWhiteSpace(text)) break;
        }

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("website_studio_ai_empty_output");

        var parsed = JsonSerializer.Deserialize<ProviderEnvelope>(text, JsonOptions)
            ?? throw new InvalidOperationException("website_studio_ai_invalid_output");
        return new WebsiteStudioAiProviderProposal(
            parsed.Summary?.Trim() ?? "Website Studio AI proposal",
            parsed.Operations ?? []);
    }

    private static object BuildSchema()
    {
        var nullableNumber = new { type = new[] { "number", "null" } };
        var nullableInteger = new { type = new[] { "integer", "null" } };
        var nullableString = new { type = new[] { "string", "null" } };
        return new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["summary"] = new { type = "string", maxLength = 1200 },
                ["operations"] = new
                {
                    type = "array",
                    maxItems = 20,
                    items = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["kind"] = new
                            {
                                type = "string",
                                @enum = new[]
                                {
                                    "set_text", "set_style", "set_layout",
                                    "add_section", "add_text", "add_button", "suggest_image"
                                }
                            },
                            ["breakpointKey"] = nullableString,
                            ["text"] = nullableString,
                            ["title"] = nullableString,
                            ["href"] = nullableString,
                            ["imagePrompt"] = nullableString,
                            ["style"] = new
                            {
                                type = new[] { "object", "null" },
                                properties = new Dictionary<string, object>
                                {
                                    ["textAlign"] = nullableString,
                                    ["fontScale"] = nullableNumber,
                                    ["widthPercent"] = nullableNumber,
                                    ["heightPx"] = nullableNumber,
                                    ["paddingTop"] = nullableNumber,
                                    ["paddingBottom"] = nullableNumber,
                                    ["offsetXPercent"] = nullableNumber,
                                    ["offsetYPx"] = nullableNumber,
                                    ["fontSize"] = nullableNumber,
                                    ["lineHeight"] = nullableNumber,
                                    ["letterSpacing"] = nullableNumber,
                                    ["paddingLeft"] = nullableNumber,
                                    ["paddingRight"] = nullableNumber,
                                    ["borderRadius"] = nullableNumber
                                },
                                additionalProperties = false
                            },
                            ["layout"] = new
                            {
                                type = new[] { "object", "null" },
                                properties = new Dictionary<string, object>
                                {
                                    ["mode"] = nullableString,
                                    ["direction"] = nullableString,
                                    ["gapPx"] = nullableNumber,
                                    ["columns"] = nullableInteger,
                                    ["minItemWidthPx"] = nullableNumber,
                                    ["alignItems"] = nullableString,
                                    ["justifyContent"] = nullableString,
                                    ["wrap"] = nullableString
                                },
                                additionalProperties = false
                            }
                        },
                        required = new[] { "kind" },
                        additionalProperties = false
                    }
                }
            },
            required = new[] { "summary", "operations" },
            additionalProperties = false
        };
    }

    private sealed class ProviderEnvelope
    {
        public string? Summary { get; set; }
        public List<WebsiteStudioAiOperation> Operations { get; set; } = new();
    }

    private const string ResponsiveSystemPrompt =
        """
        You are LEGEND Website Studio responsive design assistance.
        Produce only safe typed proposals for the CURRENT SELECTED ELEMENT.
        Allowed operations: set_style and set_layout only.
        Use only breakpoint keys supplied in context, or null for base.
        Prefer mobile-first readability, no horizontal overflow, touch-friendly spacing,
        and stack/grid/flex choices that preserve content and semantics.
        Do not rewrite text. Do not add/delete pages. Do not create analytics or Meta events.
        Do not emit CSS, HTML, JavaScript, credentials, IDs, or claims not present in context.
        """;

    private const string CreationSystemPrompt =
        """
        You are LEGEND Website Studio creation assistance.
        Produce only bounded typed proposals for the CURRENT PAGE and SELECTED ELEMENT/SECTION.
        Allowed operations: set_text, set_style, set_layout, add_section, add_text, add_button,
        and suggest_image. suggest_image is advisory only and must never invent an image URL.
        Never create or alter analytics/Meta outcomes, credentials, domains, CRM records,
        business ownership, bookings, purchases, or database data.
        Never emit HTML/CSS/JavaScript. Keep copy factual and grounded only in supplied context.
        A button may include a URL only when the user's instruction/context provides a real
        destination; otherwise omit the URL so the editor can require a valid action before publish.
        """;
}
