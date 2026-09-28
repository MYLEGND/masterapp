using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Infrastructure.WebsiteEditing;

namespace Infrastructure.WebsiteEditing;

public sealed record WebsiteStudioAiActionContext(
    string Key,
    string Label,
    string DefaultText,
    string Group);

public sealed record WebsiteStudioAiMediaContext(
    Guid Id,
    string Name,
    string ContentType,
    long SizeBytes);

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
    string? PageDescription,
    string? SiteSource = null,
    IReadOnlyList<WebsiteStudioAiActionContext>? Actions = null,
    IReadOnlyList<WebsiteStudioAiMediaContext>? Media = null);

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
    private const int MaxContextChars = 120_000;
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
        if (mode is not ("responsive" or "create" or "build" or "transform" or "selection" or "fix"))
            throw new ArgumentException("Website AI mode is unsupported.");

        var instruction = (request.Instruction ?? string.Empty).Trim();
        if (instruction.Length == 0 || instruction.Length > MaxInstructionChars)
            throw new ArgumentException("Website AI instructions must be 1–2,000 characters.");

        var payload = BuildContextPayload(request.Context);
        if (payload.Length > MaxContextChars)
            payload = payload[..MaxContextChars];

        var system = mode switch
        {
            "responsive" => ResponsiveSystemPrompt,
            "build" => BuildSystemPrompt,
            "transform" => TransformSystemPrompt,
            "selection" => SelectionSystemPrompt,
            "fix" => FixSystemPrompt,
            _ => CreationSystemPrompt
        };
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
            },
            siteSource = Clamp(context.SiteSource, 90_000),
            canonicalActions = (context.Actions ?? []).Select(action => new
            {
                action.Key,
                action.Label,
                action.DefaultText,
                action.Group
            }),
            media = (context.Media ?? []).Take(200).Select(asset => new
            {
                asset.Id,
                asset.Name,
                asset.ContentType,
                asset.SizeBytes
            })
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
                                    "create_page", "delete_page", "set_page", "set_seo",
                                    "add_section", "add_text", "add_button", "add_node",
                                    "delete_node", "move_node", "set_action", "bind_media",
                                    "set_theme", "enable_store", "suggest_image"
                                }
                            },
                            ["pagePath"] = nullableString,
                            ["nodeId"] = nullableString,
                            ["parentId"] = nullableString,
                            ["beforeNodeId"] = nullableString,
                            ["breakpointKey"] = nullableString,
                            ["nodeType"] = nullableString,
                            ["tag"] = nullableString,
                            ["className"] = nullableString,
                            ["text"] = nullableString,
                            ["title"] = nullableString,
                            ["description"] = nullableString,
                            ["navigationLabel"] = nullableString,
                            ["actionKey"] = nullableString,
                            ["href"] = nullableString,
                            ["alt"] = nullableString,
                            ["mediaAssetId"] = nullableString,
                            ["imagePrompt"] = nullableString,
                            ["enabled"] = new { type = new[] { "boolean", "null" } },
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
                            },
                            ["theme"] = new
                            {
                                type = new[] { "object", "null" },
                                properties = new Dictionary<string, object>
                                {
                                    ["navy"] = nullableString,
                                    ["navyDeep"] = nullableString,
                                    ["gold"] = nullableString,
                                    ["goldStrong"] = nullableString,
                                    ["muted"] = nullableString,
                                    ["surface"] = nullableString,
                                    ["text"] = nullableString,
                                    ["fontFamily"] = nullableString,
                                    ["fontSize"] = nullableNumber,
                                    ["borderRadius"] = nullableNumber
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
        You are LEGEND Website Studio creation assistance operating on the canonical v3 website graph.
        Produce only typed operations. Prefer stable existing node IDs for edits.
        Use only canonicalActions supplied in context for actionKey. Never invent an action key.
        Use only supplied media asset IDs for bind_media. suggest_image is advisory only.
        Never create or alter analytics events, Meta/OpenAI provider events, credentials, owner scope,
        domains, CRM records, consent semantics, booking authority, purchases, or server outcomes.
        Never emit HTML/CSS/JavaScript. Keep claims grounded in supplied business context.
        """;

    private const string BuildSystemPrompt =
        """
        You are LEGEND Site Composer. Build a complete premium website draft through typed operations only.
        You may create pages, sections, text, CTA/link/image/video nodes, set SEO/theme/layout, bind existing
        authorized media, and choose only canonicalActions supplied in context. Preserve protected system
        components and all existing canonical actions unless the user explicitly asks to choose another listed
        canonical action. Never emit or invent analytics, Meta/OpenAI events, owner IDs, endpoints, credentials,
        booking/commerce implementations, consent semantics, server outcomes, HTML, CSS, or JavaScript.
        The siteSource is the exact editable website graph; treat stable IDs as durable identities.
        """;

    private const string TransformSystemPrompt =
        """
        You are LEGEND Site Composer in full-site transformation mode. Improve design, hierarchy, copy,
        responsiveness, CTA placement, SEO, and use of authorized media across the supplied siteSource.
        Return typed operations against stable node/page identities. Protected actions/system components
        must remain protected; only choose actionKey values present in canonicalActions. Never create provider
        events or backend behavior. Never emit HTML/CSS/JavaScript.
        """;

    private const string SelectionSystemPrompt =
        """
        You are LEGEND Site Composer in selection-only mode. Change only the selected node/section subtree
        on the current page. Use typed operations and stable IDs. Do not create/delete pages, change theme/store,
        or modify anything outside the selected subtree. Preserve protected actions and system components.
        Never emit analytics/provider events, backend behavior, HTML, CSS, or JavaScript.
        """;

    private const string FixSystemPrompt =
        """
        You are LEGEND Site Composer quality-repair mode. Inspect the supplied canonical siteSource and propose
        only typed fixes for structure, accessibility, missing alt text, responsive layout, weak hierarchy,
        incomplete SEO, broken safe links, and visual consistency. Preserve intentional content and protected
        semantic wiring. Use only listed canonicalActions and authorized media. Never invent backend/provider
        behavior or HTML/CSS/JavaScript.
        """;
}
