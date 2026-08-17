using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

/// <summary>
/// One governed piece of evidence supplied to an external language teacher.
///
/// This type carries evidence into a proposal boundary only. Merely appearing
/// here does not change provenance, quality, training eligibility, or any
/// canonical LEGEND language state.
/// </summary>
internal sealed record LegendLanguageTeacherEvidence(
    string EvidenceIdentity,
    string SourceText,
    string? TargetText,
    string Provenance,
    string QualityState);

/// <summary>
/// One explicit semantic component proposed inside one controlled example.
/// It is a proposal, not an established semantic anchor.
/// </summary>
internal sealed record LegendLanguageTeacherSemanticComponent(
    string Dimension,
    string Value,
    string SurfaceForm);

/// <summary>
/// One controlled example proposed by the external teacher.
/// </summary>
internal sealed record LegendLanguageTeacherExampleProposal(
    string SourceText,
    string? TargetText,
    IReadOnlyList<LegendLanguageTeacherSemanticComponent> Components);

/// <summary>
/// One candidate curriculum family proposed for later LEGEND validation.
///
/// Nothing in this record is FounderApproved, SystemValidated, production
/// eligible, or durable merely because a teacher produced it.
/// </summary>
internal sealed record LegendLanguageTeacherFamilyProposal(
    string FamilyKey,
    string SemanticCategory,
    string Rationale,
    decimal Confidence,
    IReadOnlyList<LegendLanguageTeacherExampleProposal> Examples);

internal sealed record LegendLanguageTeacherProposalRequest(
    string SourceLanguageCode,
    string TargetLanguageCode,
    string LearningGoal,
    IReadOnlyList<LegendLanguageTeacherEvidence> Evidence,
    int MaximumFamilies = 2);

internal sealed record LegendLanguageTeacherProposalResult(
    bool Succeeded,
    IReadOnlyList<LegendLanguageTeacherFamilyProposal> Families,
    string? ErrorCode = null);

internal sealed record LegendLanguageTeacherCritiqueRequest(
    LegendLanguageTeacherProposalRequest Context,
    LegendLanguageTeacherFamilyProposal Proposal);

internal sealed record LegendLanguageTeacherCritiqueResult(
    bool Succeeded,
    bool Approved,
    decimal? Confidence,
    IReadOnlyList<string> ReasonCodes,
    string? ErrorCode = null);

/// <summary>
/// Non-authoritative external reasoning boundary.
///
/// The teacher can propose and critique controlled language-learning material.
/// It cannot persist evidence, mutate corpus state, grant quality maturity,
/// promote a model, serve a translation, or change Founder authority.
/// </summary>
internal interface ILegendConnectLanguageTeacher
{
    Task<LegendLanguageTeacherProposalResult> ProposeAsync(
        LegendLanguageTeacherProposalRequest request,
        CancellationToken cancellationToken = default);

    Task<LegendLanguageTeacherCritiqueResult> CritiqueAsync(
        LegendLanguageTeacherCritiqueRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// OpenAI Responses API implementation of the non-authoritative LEGEND
/// teacher/critic boundary.
///
/// Configuration is deliberately required for the role-specific model names.
/// If configuration is absent or invalid, the provider fails closed without
/// making a network request.
/// </summary>
internal sealed class OpenAiLegendConnectLanguageTeacher
    : ILegendConnectLanguageTeacher
{
    private const string ClientName = "LegendLanguageTeacher";
    private const string ConfigurationPrefix =
        "LegendConnect:LanguageTeacher:";
    private const string DefaultEndpoint =
        "https://api.openai.com/v1/responses";

    private const int MaximumEvidenceItems = 32;
    private const int MaximumFamilies = 4;
    private const int MaximumExamplesPerFamily = 8;
    private const int MaximumComponentsPerExample = 16;
    private const int MaximumTextLength = 2_000;

    private const string ProposalInstructions = """
You are the non-authoritative language-teaching proposal generator inside LEGEND.

Your job is to propose controlled linguistic teaching material that may help close the supplied learning gap.

Rules:
- Treat all supplied evidence as observations with the provenance and quality states shown.
- Never claim that you are Founder authority, human verification, system validation, or production authority.
- Never upgrade evidence quality.
- Never invent a Founder approval.
- Produce controlled contrasts rather than isolated canned sentences.
- Keep semantic components explicit and tied to material visibly realized in each source example.
- Preserve the requested source-to-target language direction.
- If a target realization is not supportable with high confidence, use null rather than inventing one.
- Do not include private facts or personally identifying information.
- Return only the requested structured result.
""";

    private const string CriticInstructions = """
You are the independent adversarial critic for a non-authoritative LEGEND language-teaching proposal.

Evaluate the proposed family against the supplied governed evidence and general linguistic coherence.

Approve only when:
- the requested language direction is preserved;
- examples form useful controlled contrasts;
- semantic component labels agree with what is visibly realized;
- proposed target text is linguistically coherent when supplied;
- the proposal does not contradict stronger supplied evidence;
- the proposal does not falsely claim Founder or human authority;
- the material is suitable to enter a later LEGEND validation stage.

Your approval is NOT SystemValidated, FounderApproved, production eligibility, or model promotion.
It is only permission for a future canonical LEGEND validator to examine the proposal.

Return only the requested structured result.
""";

    private const string ProposalSchema = """
{
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "families": {
      "type": "array",
      "minItems": 1,
      "maxItems": 4,
      "items": {
        "type": "object",
        "additionalProperties": false,
        "properties": {
          "family_key": {
            "type": "string",
            "minLength": 3,
            "maxLength": 120
          },
          "semantic_category": {
            "type": "string",
            "minLength": 1,
            "maxLength": 120
          },
          "rationale": {
            "type": "string",
            "minLength": 1,
            "maxLength": 1000
          },
          "confidence": {
            "type": "number",
            "minimum": 0,
            "maximum": 1
          },
          "examples": {
            "type": "array",
            "minItems": 2,
            "maxItems": 8,
            "items": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "source_text": {
                  "type": "string",
                  "minLength": 1,
                  "maxLength": 2000
                },
                "target_text": {
                  "type": ["string", "null"],
                  "maxLength": 2000
                },
                "components": {
                  "type": "array",
                  "minItems": 1,
                  "maxItems": 16,
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "dimension": {
                        "type": "string",
                        "minLength": 1,
                        "maxLength": 80
                      },
                      "value": {
                        "type": "string",
                        "minLength": 1,
                        "maxLength": 240
                      },
                      "surface_form": {
                        "type": "string",
                        "minLength": 1,
                        "maxLength": 500
                      }
                    },
                    "required": [
                      "dimension",
                      "value",
                      "surface_form"
                    ]
                  }
                }
              },
              "required": [
                "source_text",
                "target_text",
                "components"
              ]
            }
          }
        },
        "required": [
          "family_key",
          "semantic_category",
          "rationale",
          "confidence",
          "examples"
        ]
      }
    }
  },
  "required": ["families"]
}
""";

    private const string CritiqueSchema = """
{
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "approved": {
      "type": "boolean"
    },
    "confidence": {
      "type": "number",
      "minimum": 0,
      "maximum": 1
    },
    "reason_codes": {
      "type": "array",
      "minItems": 1,
      "maxItems": 8,
      "items": {
        "type": "string",
        "minLength": 1,
        "maxLength": 120
      }
    }
  },
  "required": [
    "approved",
    "confidence",
    "reason_codes"
  ]
}
""";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenAiLegendConnectLanguageTeacher> _logger;

    public OpenAiLegendConnectLanguageTeacher(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<OpenAiLegendConnectLanguageTeacher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<LegendLanguageTeacherProposalResult> ProposeAsync(
        LegendLanguageTeacherProposalRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateProposalRequest(request, out var normalized))
        {
            return new LegendLanguageTeacherProposalResult(
                false,
                [],
                "language_teacher_invalid_request");
        }

        if (!TryGetConfiguration(
                "TeacherModel",
                out var endpoint,
                out var key,
                out var model))
        {
            return new LegendLanguageTeacherProposalResult(
                false,
                [],
                "language_teacher_unavailable");
        }

        var input = JsonSerializer.Serialize(new
        {
            source_language_code = normalized.SourceLanguageCode,
            target_language_code = normalized.TargetLanguageCode,
            learning_goal = normalized.LearningGoal,
            maximum_families = normalized.MaximumFamilies,
            evidence = normalized.Evidence.Select(item => new
            {
                evidence_identity = item.EvidenceIdentity,
                source_text = item.SourceText,
                target_text = item.TargetText,
                provenance = item.Provenance,
                quality_state = item.QualityState
            })
        });

        var provider = await SendStructuredAsync(
            endpoint,
            key,
            model,
            ProposalInstructions,
            input,
            "legend_language_teacher",
            ProposalSchema,
            cancellationToken);

        if (!provider.Succeeded || provider.OutputText is null)
        {
            return new LegendLanguageTeacherProposalResult(
                false,
                [],
                provider.ErrorCode);
        }

        if (!TryParseProposalResult(
                provider.OutputText,
                normalized.MaximumFamilies,
                out var families))
        {
            return new LegendLanguageTeacherProposalResult(
                false,
                [],
                "language_teacher_invalid_response");
        }

        return new LegendLanguageTeacherProposalResult(
            true,
            families);
    }

    public async Task<LegendLanguageTeacherCritiqueResult> CritiqueAsync(
        LegendLanguageTeacherCritiqueRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateCritiqueRequest(request, out var normalized))
        {
            return new LegendLanguageTeacherCritiqueResult(
                false,
                false,
                null,
                [],
                "language_teacher_invalid_request");
        }

        if (!TryGetConfiguration(
                "CriticModel",
                out var endpoint,
                out var key,
                out var model))
        {
            return new LegendLanguageTeacherCritiqueResult(
                false,
                false,
                null,
                [],
                "language_teacher_unavailable");
        }

        var input = JsonSerializer.Serialize(new
        {
            source_language_code =
                normalized.Context.SourceLanguageCode,
            target_language_code =
                normalized.Context.TargetLanguageCode,
            learning_goal =
                normalized.Context.LearningGoal,
            evidence =
                normalized.Context.Evidence.Select(item => new
                {
                    evidence_identity = item.EvidenceIdentity,
                    source_text = item.SourceText,
                    target_text = item.TargetText,
                    provenance = item.Provenance,
                    quality_state = item.QualityState
                }),
            proposal = new
            {
                family_key = normalized.Proposal.FamilyKey,
                semantic_category =
                    normalized.Proposal.SemanticCategory,
                rationale = normalized.Proposal.Rationale,
                confidence = normalized.Proposal.Confidence,
                examples =
                    normalized.Proposal.Examples.Select(example => new
                    {
                        source_text = example.SourceText,
                        target_text = example.TargetText,
                        components =
                            example.Components.Select(component => new
                            {
                                dimension = component.Dimension,
                                value = component.Value,
                                surface_form =
                                    component.SurfaceForm
                            })
                    })
            }
        });

        var provider = await SendStructuredAsync(
            endpoint,
            key,
            model,
            CriticInstructions,
            input,
            "legend_language_critic",
            CritiqueSchema,
            cancellationToken);

        if (!provider.Succeeded || provider.OutputText is null)
        {
            return new LegendLanguageTeacherCritiqueResult(
                false,
                false,
                null,
                [],
                provider.ErrorCode);
        }

        if (!TryParseCritiqueResult(
                provider.OutputText,
                out var approved,
                out var confidence,
                out var reasonCodes))
        {
            return new LegendLanguageTeacherCritiqueResult(
                false,
                false,
                null,
                [],
                "language_teacher_invalid_response");
        }

        return new LegendLanguageTeacherCritiqueResult(
            true,
            approved,
            confidence,
            reasonCodes);
    }

    private async Task<ProviderResponse> SendStructuredAsync(
        Uri endpoint,
        string key,
        string model,
        string instructions,
        string input,
        string schemaName,
        string schemaJson,
        CancellationToken cancellationToken)
    {
        try
        {
            using var schemaDocument =
                JsonDocument.Parse(schemaJson);

            var payload = new
            {
                model,
                store = false,
                max_output_tokens = 1600,
                instructions,
                input,
                text = new
                {
                    format = new
                    {
                        type = "json_schema",
                        name = schemaName,
                        strict = true,
                        schema =
                            schemaDocument.RootElement.Clone()
                    }
                }
            };

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    endpoint)
                {
                    Content = JsonContent.Create(payload)
                };

            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    key);

            using var response =
                await _httpClientFactory
                    .CreateClient(ClientName)
                    .SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "LEGEND language teacher provider failed. StatusCode={StatusCode}",
                    (int)response.StatusCode);

                return new ProviderResponse(
                    false,
                    null,
                    "language_teacher_provider_failed");
            }

            await using var stream =
                await response.Content.ReadAsStreamAsync(
                    cancellationToken);

            using var document =
                await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty(
                    "status",
                    out var status) ||
                !string.Equals(
                    status.GetString(),
                    "completed",
                    StringComparison.Ordinal))
            {
                return new ProviderResponse(
                    false,
                    null,
                    "language_teacher_provider_failed");
            }

            var outputText =
                ExtractOutputText(document.RootElement);

            return string.IsNullOrWhiteSpace(outputText)
                ? new ProviderResponse(
                    false,
                    null,
                    "language_teacher_invalid_response")
                : new ProviderResponse(
                    true,
                    outputText,
                    null);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return new ProviderResponse(
                false,
                null,
                "language_teacher_timeout");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND language teacher request failed.");

            return new ProviderResponse(
                false,
                null,
                "language_teacher_provider_failed");
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND language teacher response was invalid.");

            return new ProviderResponse(
                false,
                null,
                "language_teacher_invalid_response");
        }
    }

    private bool TryGetConfiguration(
        string modelKey,
        out Uri endpoint,
        out string key,
        out string model)
    {
        var endpointValue =
            (_configuration[
                ConfigurationPrefix + "Endpoint"] ??
             DefaultEndpoint)
            .Trim();

        key =
            (_configuration[
                ConfigurationPrefix + "ApiKey"] ??
             Environment.GetEnvironmentVariable(
                 "OPENAI_API_KEY") ??
             string.Empty)
            .Trim();

        model =
            (_configuration[
                ConfigurationPrefix + modelKey] ??
             string.Empty)
            .Trim();

        if (!Uri.TryCreate(
                endpointValue,
                UriKind.Absolute,
                out var parsedEndpoint) ||
            parsedEndpoint.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(key) ||
            string.IsNullOrWhiteSpace(model) ||
            model.Length > 160)
        {
            endpoint = default!;
            return false;
        }

        endpoint = parsedEndpoint;
        return true;
    }

    private static string? ExtractOutputText(
        JsonElement root)
    {
        if (!root.TryGetProperty(
                "output",
                out var output) ||
            output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty(
                    "type",
                    out var itemType) ||
                !string.Equals(
                    itemType.GetString(),
                    "message",
                    StringComparison.Ordinal) ||
                !item.TryGetProperty(
                    "content",
                    out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty(
                        "type",
                        out var partType) &&
                    string.Equals(
                        partType.GetString(),
                        "output_text",
                        StringComparison.Ordinal) &&
                    part.TryGetProperty(
                        "text",
                        out var text) &&
                    text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString();
                }
            }
        }

        return null;
    }

    private static bool TryValidateProposalRequest(
        LegendLanguageTeacherProposalRequest request,
        out LegendLanguageTeacherProposalRequest normalized)
    {
        normalized = request;

        if (!TryNormalizeRequired(
                request.SourceLanguageCode,
                35,
                out var sourceLanguage) ||
            !TryNormalizeRequired(
                request.TargetLanguageCode,
                35,
                out var targetLanguage) ||
            !TryNormalizeRequired(
                request.LearningGoal,
                500,
                out var learningGoal) ||
            request.MaximumFamilies is < 1 or > MaximumFamilies ||
            request.Evidence is null ||
            request.Evidence.Count is < 1 or > MaximumEvidenceItems)
        {
            return false;
        }

        var evidence =
            new List<LegendLanguageTeacherEvidence>(
                request.Evidence.Count);

        var identities =
            new HashSet<string>(
                StringComparer.Ordinal);

        foreach (var item in request.Evidence)
        {
            if (!TryNormalizeRequired(
                    item.EvidenceIdentity,
                    160,
                    out var identity) ||
                !identities.Add(identity) ||
                !TryNormalizeRequired(
                    item.SourceText,
                    MaximumTextLength,
                    out var sourceText) ||
                !TryNormalizeOptional(
                    item.TargetText,
                    MaximumTextLength,
                    out var targetText) ||
                !TryNormalizeRequired(
                    item.Provenance,
                    80,
                    out var provenance) ||
                !TryNormalizeRequired(
                    item.QualityState,
                    40,
                    out var qualityState))
            {
                return false;
            }

            evidence.Add(
                new LegendLanguageTeacherEvidence(
                    identity,
                    sourceText,
                    targetText,
                    provenance,
                    qualityState));
        }

        normalized =
            new LegendLanguageTeacherProposalRequest(
                sourceLanguage,
                targetLanguage,
                learningGoal,
                evidence,
                request.MaximumFamilies);

        return true;
    }

    private static bool TryValidateCritiqueRequest(
        LegendLanguageTeacherCritiqueRequest request,
        out LegendLanguageTeacherCritiqueRequest normalized)
    {
        normalized = request;

        if (!TryValidateProposalRequest(
                request.Context,
                out var context) ||
            !TryNormalizeFamilyProposal(
                request.Proposal,
                out var proposal))
        {
            return false;
        }

        normalized =
            new LegendLanguageTeacherCritiqueRequest(
                context,
                proposal);

        return true;
    }

    private static bool TryNormalizeFamilyProposal(
        LegendLanguageTeacherFamilyProposal proposal,
        out LegendLanguageTeacherFamilyProposal normalized)
    {
        normalized = proposal;

        if (!TryNormalizeRequired(
                proposal.FamilyKey,
                120,
                out var familyKey) ||
            !TryNormalizeRequired(
                proposal.SemanticCategory,
                120,
                out var semanticCategory) ||
            !TryNormalizeRequired(
                proposal.Rationale,
                1000,
                out var rationale) ||
            proposal.Confidence is < 0m or > 1m ||
            proposal.Examples is null ||
            proposal.Examples.Count is < 2 or > MaximumExamplesPerFamily)
        {
            return false;
        }

        var examples =
            new List<LegendLanguageTeacherExampleProposal>(
                proposal.Examples.Count);

        var exampleIdentities =
            new HashSet<string>(
                StringComparer.Ordinal);

        foreach (var example in proposal.Examples)
        {
            if (!TryNormalizeRequired(
                    example.SourceText,
                    MaximumTextLength,
                    out var sourceText) ||
                !TryNormalizeOptional(
                    example.TargetText,
                    MaximumTextLength,
                    out var targetText) ||
                example.Components is null ||
                example.Components.Count is < 1
                    or > MaximumComponentsPerExample)
            {
                return false;
            }

            var exampleIdentity =
                sourceText + "\n" + (targetText ?? string.Empty);

            if (!exampleIdentities.Add(exampleIdentity))
                return false;

            var components =
                new List<LegendLanguageTeacherSemanticComponent>(
                    example.Components.Count);

            foreach (var component in example.Components)
            {
                if (!TryNormalizeRequired(
                        component.Dimension,
                        80,
                        out var dimension) ||
                    !TryNormalizeRequired(
                        component.Value,
                        240,
                        out var value) ||
                    !TryNormalizeRequired(
                        component.SurfaceForm,
                        500,
                        out var surfaceForm))
                {
                    return false;
                }

                components.Add(
                    new LegendLanguageTeacherSemanticComponent(
                        dimension,
                        value,
                        surfaceForm));
            }

            examples.Add(
                new LegendLanguageTeacherExampleProposal(
                    sourceText,
                    targetText,
                    components));
        }

        normalized =
            new LegendLanguageTeacherFamilyProposal(
                familyKey,
                semanticCategory,
                rationale,
                proposal.Confidence,
                examples);

        return true;
    }

    private static bool TryParseProposalResult(
        string json,
        int maximumFamilies,
        out IReadOnlyList<LegendLanguageTeacherFamilyProposal> families)
    {
        families = [];

        try
        {
            using var document =
                JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty(
                    "families",
                    out var familyArray) ||
                familyArray.ValueKind != JsonValueKind.Array ||
                familyArray.GetArrayLength() is < 1 or > MaximumFamilies ||
                familyArray.GetArrayLength() > maximumFamilies)
            {
                return false;
            }

            var parsed =
                new List<LegendLanguageTeacherFamilyProposal>(
                    familyArray.GetArrayLength());

            var familyKeys =
                new HashSet<string>(
                    StringComparer.Ordinal);

            foreach (var familyElement in familyArray.EnumerateArray())
            {
                if (!TryReadRequiredString(
                        familyElement,
                        "family_key",
                        120,
                        out var familyKey) ||
                    !familyKeys.Add(familyKey) ||
                    !TryReadRequiredString(
                        familyElement,
                        "semantic_category",
                        120,
                        out var semanticCategory) ||
                    !TryReadRequiredString(
                        familyElement,
                        "rationale",
                        1000,
                        out var rationale) ||
                    !familyElement.TryGetProperty(
                        "confidence",
                        out var confidenceElement) ||
                    !confidenceElement.TryGetDecimal(
                        out var confidence) ||
                    confidence is < 0m or > 1m ||
                    !familyElement.TryGetProperty(
                        "examples",
                        out var exampleArray) ||
                    exampleArray.ValueKind != JsonValueKind.Array ||
                    exampleArray.GetArrayLength() is < 2
                        or > MaximumExamplesPerFamily)
                {
                    return false;
                }

                var examples =
                    new List<LegendLanguageTeacherExampleProposal>(
                        exampleArray.GetArrayLength());

                foreach (var exampleElement in exampleArray.EnumerateArray())
                {
                    if (!TryReadRequiredString(
                            exampleElement,
                            "source_text",
                            MaximumTextLength,
                            out var sourceText) ||
                        !TryReadOptionalString(
                            exampleElement,
                            "target_text",
                            MaximumTextLength,
                            out var targetText) ||
                        !exampleElement.TryGetProperty(
                            "components",
                            out var componentArray) ||
                        componentArray.ValueKind != JsonValueKind.Array ||
                        componentArray.GetArrayLength() is < 1
                            or > MaximumComponentsPerExample)
                    {
                        return false;
                    }

                    var components =
                        new List<LegendLanguageTeacherSemanticComponent>(
                            componentArray.GetArrayLength());

                    foreach (var componentElement
                             in componentArray.EnumerateArray())
                    {
                        if (!TryReadRequiredString(
                                componentElement,
                                "dimension",
                                80,
                                out var dimension) ||
                            !TryReadRequiredString(
                                componentElement,
                                "value",
                                240,
                                out var value) ||
                            !TryReadRequiredString(
                                componentElement,
                                "surface_form",
                                500,
                                out var surfaceForm))
                        {
                            return false;
                        }

                        components.Add(
                            new LegendLanguageTeacherSemanticComponent(
                                dimension,
                                value,
                                surfaceForm));
                    }

                    examples.Add(
                        new LegendLanguageTeacherExampleProposal(
                            sourceText,
                            targetText,
                            components));
                }

                var candidate =
                    new LegendLanguageTeacherFamilyProposal(
                        familyKey,
                        semanticCategory,
                        rationale,
                        confidence,
                        examples);

                if (!TryNormalizeFamilyProposal(
                        candidate,
                        out var normalized))
                {
                    return false;
                }

                parsed.Add(normalized);
            }

            families = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseCritiqueResult(
        string json,
        out bool approved,
        out decimal confidence,
        out IReadOnlyList<string> reasonCodes)
    {
        approved = false;
        confidence = 0m;
        reasonCodes = [];

        try
        {
            using var document =
                JsonDocument.Parse(json);

            var root = document.RootElement;

            if (!root.TryGetProperty(
                    "approved",
                    out var approvedElement) ||
                approvedElement.ValueKind is not
                    (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty(
                    "confidence",
                    out var confidenceElement) ||
                !confidenceElement.TryGetDecimal(
                    out confidence) ||
                confidence is < 0m or > 1m ||
                !root.TryGetProperty(
                    "reason_codes",
                    out var reasons) ||
                reasons.ValueKind != JsonValueKind.Array ||
                reasons.GetArrayLength() is < 1 or > 8)
            {
                return false;
            }

            var parsedReasons =
                new List<string>(
                    reasons.GetArrayLength());

            foreach (var item in reasons.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String ||
                    !TryNormalizeRequired(
                        item.GetString(),
                        120,
                        out var reason))
                {
                    return false;
                }

                parsedReasons.Add(reason);
            }

            approved = approvedElement.GetBoolean();
            reasonCodes = parsedReasons;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadRequiredString(
        JsonElement element,
        string propertyName,
        int maximumLength,
        out string value)
    {
        value = string.Empty;

        return element.TryGetProperty(
                   propertyName,
                   out var property) &&
               property.ValueKind == JsonValueKind.String &&
               TryNormalizeRequired(
                   property.GetString(),
                   maximumLength,
                   out value);
    }

    private static bool TryReadOptionalString(
        JsonElement element,
        string propertyName,
        int maximumLength,
        out string? value)
    {
        value = null;

        if (!element.TryGetProperty(
                propertyName,
                out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
            return true;

        return property.ValueKind == JsonValueKind.String &&
               TryNormalizeOptional(
                   property.GetString(),
                   maximumLength,
                   out value);
    }

    private static bool TryNormalizeRequired(
        string? value,
        int maximumLength,
        out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;

        return normalized.Length > 0 &&
               normalized.Length <= maximumLength;
    }

    private static bool TryNormalizeOptional(
        string? value,
        int maximumLength,
        out string? normalized)
    {
        normalized = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = null;
            return true;
        }

        return normalized.Length <= maximumLength;
    }

    private sealed record ProviderResponse(
        bool Succeeded,
        string? OutputText,
        string? ErrorCode);
}
