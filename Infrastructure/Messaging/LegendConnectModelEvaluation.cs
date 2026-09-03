using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

internal sealed record LegendModelEvaluationGenerationResult(
    bool Succeeded,
    string? Text,
    string? ErrorCode = null,
    bool Retryable = false,
    long? CostMicrounits = null);

/// <summary>
/// Strict reader for the run-level receipt emitted only after every selected
/// case has supplied a valid locked-serving proof. Promotion and serving use
/// this same receipt shape; a legacy "Passed" flag is not runtime evidence.
/// </summary>
public static class LegendConnectModelRuntimeProofSummary
{
    public static bool IsValid(
        string? detail)
    {
        if (string.IsNullOrWhiteSpace(
                detail))
        {
            return false;
        }

        var fields =
            new Dictionary<string, string>(
                StringComparer.Ordinal);
        foreach (var part in detail.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var separator =
                part.IndexOf('=');
            if (separator <= 0 ||
                separator == part.Length - 1 ||
                !fields.TryAdd(
                    part[..separator],
                    part[(separator + 1)..]))
            {
                return false;
            }
        }

        return TryNonNegative(
                   fields,
                   "evaluated",
                   requirePositive: true) &&
               DecimalField(
                   fields,
                   "reference") &&
               Exact(
                   fields,
                   "blocking",
                   "0") &&
               Exact(
                   fields,
                   "protected",
                   "0") &&
               Exact(
                   fields,
                   "leakage",
                   "0") &&
               fields.TryGetValue(
                   "prompt_set",
                   out var promptSet) &&
               promptSet.Length is > 0 and <= 120 &&
               fields.TryGetValue(
                   "code_sha",
                   out var codeSha) &&
               IsLowerHex(
                   codeSha,
                   40) &&
               Exact(
                   fields,
                   "runtime_mode",
                   LegendConnectServingEvaluationContracts
                       .RuntimeMode) &&
               Exact(
                   fields,
                   "response_authority",
                   LegendConnectServingEvaluationContracts
                       .ResponseAuthority) &&
               Exact(
                   fields,
                   "settings",
                   LegendConnectServingEvaluationContracts
                       .InferenceSettings) &&
               fields.TryGetValue(
                   "criteria",
                   out var criteria) &&
               criteria.StartsWith(
                   LegendConnectServingEvaluationContracts
                       .SuccessCriteria +
                   ",",
                   StringComparison.Ordinal) &&
               criteria.Contains(
                   ",runtime_model=exact",
                   StringComparison.Ordinal) &&
               fields.TryGetValue(
                   "proof_set",
                   out var proofSet) &&
               IsLowerHex(
                   proofSet,
                   64) &&
               TryNonNegative(
                   fields,
                   "latency_us") &&
               TryNonNegative(
                   fields,
                   "cost_micro");
    }

    private static bool Exact(
        IReadOnlyDictionary<string, string> fields,
        string key,
        string expected) =>
        fields.TryGetValue(
            key,
            out var actual) &&
        string.Equals(
            actual,
            expected,
            StringComparison.Ordinal);

    private static bool TryNonNegative(
        IReadOnlyDictionary<string, string> fields,
        string key,
        bool requirePositive = false) =>
        fields.TryGetValue(
            key,
            out var value) &&
        long.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) &&
        (requirePositive
            ? parsed > 0
            : parsed >= 0);

    private static bool DecimalField(
        IReadOnlyDictionary<string, string> fields,
        string key) =>
        fields.TryGetValue(
            key,
            out var value) &&
        decimal.TryParse(
            value,
            System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) &&
        parsed is >= 0m and <= 1m;

    private static bool IsLowerHex(
        string value,
        int length) =>
        value.Length == length &&
        value.All(character =>
            character is >= '0' and <= '9' or
            >= 'a' and <= 'f');
}

internal static class LegendModelCapabilityKeys
{
    internal const string Translation = "translation";
    internal const string SemanticTransition = "governed.semantic_transition";
    internal const string GovernedReasoning = "governed.reasoning";
    internal const string GovernedResearch = "governed.research";
    internal const string MultimodalUnderstanding = "governed.multimodal_understanding";
}

internal sealed record LegendModelCapabilityEvaluationPolicy(
    string CapabilityKey,
    bool RequiresTranslationAccuracy,
    bool RequiresMorphologyPreservation);

/// <summary>
/// One fail-closed authority defines which governed capabilities may enter
/// training evaluation and which quality dimensions protect them. Adding a
/// task contract alone never registers an evaluator.
/// </summary>
internal static class LegendModelCapabilityEvaluationPolicies
{
    private static readonly IReadOnlyDictionary<string, LegendModelCapabilityEvaluationPolicy> Registered =
        new Dictionary<string, LegendModelCapabilityEvaluationPolicy>(StringComparer.Ordinal)
        {
            [LegendModelCapabilityKeys.Translation] =
                new(LegendModelCapabilityKeys.Translation, true, true),
            [LegendModelCapabilityKeys.SemanticTransition] =
                new(LegendModelCapabilityKeys.SemanticTransition, false, false),
            [LegendModelCapabilityKeys.GovernedReasoning] =
                new(LegendModelCapabilityKeys.GovernedReasoning, false, false),
            [LegendModelCapabilityKeys.GovernedResearch] =
                new(LegendModelCapabilityKeys.GovernedResearch, false, false)
        };

    internal static bool TryResolve(
        string capabilityKey,
        out LegendModelCapabilityEvaluationPolicy policy) =>
        Registered.TryGetValue(capabilityKey, out policy!);
}

internal sealed record LegendModelEvidencePart(
    string Modality,
    string ContentReference,
    string MediaType,
    string EvidenceIdentity,
    string ContentSha256,
    string Provenance,
    string ContradictionState = "Clear");

/// <summary>
/// Single fail-closed admission predicate for model-visible non-text evidence.
/// A transport cannot turn an ungoverned attachment into task context.
/// </summary>
internal static class LegendModelEvidenceAdmission
{
    internal static bool IsAdmitted(LegendModelEvidencePart part)
    {
        if (string.IsNullOrWhiteSpace(part.EvidenceIdentity) ||
            part.EvidenceIdentity.Length > 256 ||
            string.IsNullOrWhiteSpace(part.MediaType) ||
            !string.Equals(part.ContradictionState, "Clear", StringComparison.Ordinal) ||
            part.ContentSha256.Length != 64 ||
            part.ContentSha256.Any(character =>
                character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')) ||
            part.Provenance is not ("FounderApproved" or "HumanVerified" or "SystemValidatedMachine"))
        {
            return false;
        }

        return part.Modality switch
        {
            "image" =>
                part.ContentReference.StartsWith("https://", StringComparison.Ordinal) ||
                part.ContentReference.StartsWith("data:image/", StringComparison.Ordinal) ||
                part.ContentReference.StartsWith("file-", StringComparison.Ordinal),
            "file" =>
                part.ContentReference.StartsWith("data:", StringComparison.Ordinal) ||
                part.ContentReference.StartsWith("file-", StringComparison.Ordinal),
            _ => false
        };
    }
}

/// <summary>
/// Provider-neutral task boundary for a governed LEGEND model. The active
/// capability authority supplies the instructions and output contract; the
/// transport only executes that exact task and owns no domain behavior.
/// </summary>
internal sealed record LegendModelTaskRequest(
    string CapabilityKey,
    string Instructions,
    string Input,
    string OutputContract,
    string? SourceLanguageCode = null,
    string? TargetLanguageCode = null,
    IReadOnlyList<LegendModelEvidencePart>? EvidenceParts = null)
{
    internal static LegendModelTaskRequest Translation(
        string sourceLanguageCode,
        string targetLanguageCode,
        string text) =>
        new(
            LegendModelCapabilityKeys.Translation,
            $"Translate from {sourceLanguageCode} to {targetLanguageCode}. " +
            "Preserve all meaning, context, tone, discourse function, and grammatical information. " +
            "Return only the target-language translation.",
            text,
            "target_language_text_only",
            sourceLanguageCode,
            targetLanguageCode);

    internal static LegendModelTaskRequest GovernedReasoningRealization(
        string sourceLanguageCode,
        string founderInput,
        string authorizedSymbolicText,
        int evidenceCount,
        string evidenceStandard,
        string articulationMode) =>
        new(
            LegendModelCapabilityKeys.GovernedReasoning,
            "Propose one surface realization for the already authorized symbolic answer. " +
            "Do not add, remove, choose, rank, or reinterpret facts or evidence. " +
            "Do not resolve contradictions, call tools, approve learning, or claim authority. " +
            "Preserve uncertainty, constraints, and provenance exactly. Return only candidate text; " +
            "if no faithful alternative is possible, return the authorized symbolic answer exactly.",
            JsonSerializer.Serialize(new
            {
                founder_input = founderInput,
                authorized_symbolic_answer = authorizedSymbolicText,
                symbolic_evidence_count = evidenceCount,
                symbolic_evidence_standard = evidenceStandard,
                symbolic_articulation_mode = articulationMode
            }),
            "governed_surface_candidate_text_only",
            sourceLanguageCode,
            sourceLanguageCode);

    internal static LegendModelTaskRequest GovernedReasoningResponse(
        string sourceLanguageCode,
        string founderInput,
        IReadOnlyList<LegendConnectConversationContextItem> conversationContext) =>
        new(
            LegendModelCapabilityKeys.GovernedReasoning,
            "Answer the Founder's current request as the evaluated and promoted LEGEND reasoning model. " +
            "Reason from facts supplied in the conversation and distinguish them from unavailable facts. " +
            "When an exact fact is not established by the conversation, state that it cannot be determined and identify the evidence needed; never invent or estimate it. " +
            "Follow stated constraints, preserve conversational corrections, and answer in the requested language and format. " +
            "Do not claim to have called tools, inspected private or current data, performed an action, approved learning, or changed canonical evidence. " +
            "Return only the answer to the current request.",
            JsonSerializer.Serialize(new
            {
                conversation = conversationContext.Select(item => new
                {
                    role = item.Role,
                    content = item.Content
                }),
                founder_input = founderInput
            }),
            "governed_reasoning_response_text_only",
            sourceLanguageCode,
            sourceLanguageCode);
}

internal sealed record LegendModelEvaluationJudgeRequest(
    LegendConnectTrainingDatasetExample Example,
    string ChallengerText,
    string GovernedReferenceText,
    long RuntimeLatencyMicroseconds = 0,
    long RuntimeCostMicrounits = 0,
    string RuntimeProofIdentity = "");

internal sealed record LegendModelEvaluationJudgement(
    bool Succeeded,
    decimal ChallengerScore,
    decimal BaselineScore,
    decimal TranslationAccuracy,
    decimal SemanticPreservation,
    decimal ContextPreservation,
    decimal DiscoursePreservation,
    decimal MorphologyPreservation,
    decimal UnseenComposition,
    bool Hallucination,
    bool Refusal,
    bool BlockingRegression,
    IReadOnlyList<string> ReasonCodes,
    string? ErrorCode = null,
    bool Retryable = false,
    LegendConnectResearchEvaluationMeasurements? ResearchMeasurements = null);

internal interface ILegendConnectModelInferenceTransport
{
    Task<LegendModelEvaluationGenerationResult> GenerateAsync(
        string model,
        LegendModelTaskRequest task,
        CancellationToken cancellationToken = default);
}

internal interface ILegendConnectModelEvaluationBackend
{
    Task<LegendModelEvaluationJudgement> JudgeAsync(
        LegendModelEvaluationJudgeRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Provider-neutral challenger inference + independent semantic judge boundary.
/// Neither operation owns LEGEND evidence or promotion state.
/// </summary>
internal sealed class OpenAiLegendConnectModelInferenceTransport
    : ILegendConnectModelInferenceTransport
{
    private const string ClientName =
        "LegendModelEvaluation";

    private const string Prefix =
        "LegendConnect:ModelEvaluation:";

    private const string DefaultEndpoint =
        "https://api.openai.com/v1/responses";

    private readonly IHttpClientFactory _clients;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenAiLegendConnectModelInferenceTransport> _logger;

    public OpenAiLegendConnectModelInferenceTransport(
        IHttpClientFactory clients,
        IConfiguration configuration,
        ILogger<OpenAiLegendConnectModelInferenceTransport> logger)
    {
        _clients = clients;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<LegendModelEvaluationGenerationResult> GenerateAsync(
        string model,
        LegendModelTaskRequest task,
        CancellationToken cancellationToken = default)
    {
        var evidenceParts = task.EvidenceParts ?? Array.Empty<LegendModelEvidencePart>();
        if (string.IsNullOrWhiteSpace(task.CapabilityKey) ||
            string.IsNullOrWhiteSpace(task.Instructions) ||
            string.IsNullOrWhiteSpace(task.Input) ||
            string.IsNullOrWhiteSpace(task.OutputContract) ||
            evidenceParts.Count > 12 ||
            evidenceParts.Any(part => !LegendModelEvidenceAdmission.IsAdmitted(part)))
        {
            return new(
                false,
                null,
                "model_inference_governed_evidence_rejected");
        }

        if (!TryGetConfiguration(
                out var endpoint,
                out var key) ||
            string.IsNullOrWhiteSpace(model) ||
            model.Length > 200)
        {
            return new(
                false,
                null,
                "model_inference_provider_unavailable");
        }

        return await SendTaskAsync(
            endpoint,
            key,
            model,
            task,
            cancellationToken);
    }

    private async Task<LegendModelEvaluationGenerationResult> SendTaskAsync(
        Uri endpoint,
        string key,
        string model,
        LegendModelTaskRequest task,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload =
                new
                {
                    model,
                    store = false,
                    max_output_tokens = 1200,
                    instructions = task.Instructions,
                    input = BuildProviderInput(task)
                };

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    endpoint)
                {
                    Content =
                        JsonContent.Create(
                            payload)
                };

            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    key);

            using var response =
                await _clients
                    .CreateClient(ClientName)
                    .SendAsync(
                        request,
                        HttpCompletionOption
                            .ResponseHeadersRead,
                        cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new(
                    false,
                    null,
                    "model_inference_failed",
                    IsRetryable(
                        response.StatusCode));
            }

            await using var stream =
                await response.Content
                    .ReadAsStreamAsync(
                        cancellationToken);

            using var document =
                await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken:
                        cancellationToken);

            var output =
                ExtractCompletedOutputText(
                    document.RootElement);

            var cost = TryCalculateCostMicrounits(document.RootElement);

            return string.IsNullOrWhiteSpace(
                    output)
                ? new(
                    false,
                    null,
                    "model_inference_invalid_response")
                : new(
                    true,
                    output,
                    null,
                    false,
                    cost);
        }
        catch (OperationCanceledException)
            when (!cancellationToken
                .IsCancellationRequested)
        {
            return new(
                false,
                null,
                "model_inference_timeout",
                true);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND model inference request failed.");

            return new(
                false,
                null,
                "model_inference_failed",
                true);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND model inference response was invalid.");

            return new(
                false,
                null,
                "model_inference_invalid_response");
        }
    }

    private long? TryCalculateCostMicrounits(
        JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) ||
            !usage.TryGetProperty("input_tokens", out var inputElement) ||
            !inputElement.TryGetInt64(out var inputTokens) ||
            !usage.TryGetProperty("output_tokens", out var outputElement) ||
            !outputElement.TryGetInt64(out var outputTokens) ||
            inputTokens < 0 ||
            outputTokens < 0 ||
            !long.TryParse(
                _configuration[
                    Prefix +
                    "InputCostMicrounitsPerMillionTokens"],
                out var inputRate) ||
            !long.TryParse(
                _configuration[
                    Prefix +
                    "OutputCostMicrounitsPerMillionTokens"],
                out var outputRate) ||
            inputRate < 0 ||
            outputRate < 0)
        {
            return null;
        }

        try
        {
            var cost =
                decimal.Ceiling(
                    (checked(
                         (decimal)inputTokens * inputRate) +
                     checked(
                         (decimal)outputTokens * outputRate)) /
                    1_000_000m);
            return cost > long.MaxValue
                ? null
                : (long)cost;
        }
        catch (OverflowException)
        {
            return null;
        }
    }


    private static object BuildProviderInput(LegendModelTaskRequest task)
    {
        var evidenceParts =
            task.EvidenceParts ?? Array.Empty<LegendModelEvidencePart>();
        if (evidenceParts.Count == 0)
            return task.Input;

        var content = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["type"] = "input_text",
                ["text"] = task.Input
            }
        };

        foreach (var part in evidenceParts)
        {
            if (part.Modality == "image")
            {
                var image = new Dictionary<string, object?>
                {
                    ["type"] = "input_image",
                    ["detail"] = "high"
                };
                if (part.ContentReference.StartsWith("file-", StringComparison.Ordinal))
                    image["file_id"] = part.ContentReference;
                else
                    image["image_url"] = part.ContentReference;
                content.Add(image);
                continue;
            }

            var file = new Dictionary<string, object?>
            {
                ["type"] = "input_file",
                ["detail"] = part.MediaType == "application/pdf" ? "high" : null
            };
            if (part.ContentReference.StartsWith("file-", StringComparison.Ordinal))
                file["file_id"] = part.ContentReference;
            else
            {
                file["filename"] = $"governed-evidence-{part.EvidenceIdentity}";
                file["file_data"] = part.ContentReference;
            }
            content.Add(file);
        }

        return new object[]
        {
            new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = content
            }
        };
    }

    private bool TryGetConfiguration(
        out Uri endpoint,
        out string key)
    {
        var endpointValue =
            (_configuration[
                Prefix + "Endpoint"] ??
             DefaultEndpoint)
            .Trim();

        key =
            (_configuration[
                Prefix + "ApiKey"] ??
             Environment.GetEnvironmentVariable(
                 "OPENAI_API_KEY") ??
             string.Empty)
            .Trim();

        if (!Uri.TryCreate(
                endpointValue,
                UriKind.Absolute,
                out var parsedEndpoint) ||
            parsedEndpoint.Scheme !=
                Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(key))
        {
            endpoint = default!;
            return false;
        }

        endpoint =
            parsedEndpoint;

        return true;
    }

    private static string? ExtractCompletedOutputText(
        JsonElement root)
    {
        if (!root.TryGetProperty(
                "status",
                out var status) ||
            !string.Equals(
                status.GetString(),
                "completed",
                StringComparison.Ordinal) ||
            !root.TryGetProperty(
                "output",
                out var output) ||
            output.ValueKind !=
                JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty(
                    "type",
                    out var type) ||
                type.GetString() !=
                    "message" ||
                !item.TryGetProperty(
                    "content",
                    out var content) ||
                content.ValueKind !=
                    JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty(
                        "type",
                        out var partType) &&
                    partType.GetString() ==
                        "output_text" &&
                    part.TryGetProperty(
                        "text",
                        out var text) &&
                    text.ValueKind ==
                        JsonValueKind.String)
                {
                    return text.GetString();
                }
            }
        }

        return null;
    }

    private static bool IsRetryable(
        System.Net.HttpStatusCode status) =>
        status ==
            System.Net.HttpStatusCode.RequestTimeout ||
        (int)status == 429 ||
        (int)status >= 500;
}

internal sealed class OpenAiLegendConnectModelEvaluationBackend
    : ILegendConnectModelEvaluationBackend
{
    private const string ClientName =
        "LegendModelEvaluation";

    private const string Prefix =
        "LegendConnect:ModelEvaluation:";

    private const string DefaultEndpoint =
        "https://api.openai.com/v1/responses";

    private const string JudgeSchema = """
{
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "challenger_score": {
      "type": "number",
      "minimum": 0,
      "maximum": 1
    },
    "baseline_score": {
      "type": "number",
      "minimum": 0,
      "maximum": 1
    },
    "translation_accuracy": {
      "type": "number",
      "minimum": 0,
      "maximum": 1
    },
    "semantic_preservation": {
      "type": "number",
      "minimum": 0,
      "maximum": 1
    },
    "context_preservation": {
      "type": "number",
      "minimum": 0,
      "maximum": 1
    },
    "discourse_preservation": {
      "type": "number",
      "minimum": 0,
      "maximum": 1
    },
    "morphology_preservation": {
      "type": "number",
      "minimum": 0,
      "maximum": 1
    },
    "unseen_composition": {
      "type": "number",
      "minimum": 0,
      "maximum": 1
    },
    "hallucination": {
      "type": "boolean"
    },
    "refusal": {
      "type": "boolean"
    },
    "blocking_regression": {
      "type": "boolean"
    },
    "reason_codes": {
      "type": "array",
      "minItems": 1,
      "maxItems": 12,
      "items": {
        "type": "string",
        "minLength": 1,
        "maxLength": 120
      }
    },
    "research_measurements": {
      "type": ["object", "null"],
      "additionalProperties": false,
      "properties": {
        "answer_correct": { "type": "boolean" },
        "citation_correct": { "type": "boolean" },
        "citation_complete": { "type": "boolean" },
        "claim_evidence_entailed": { "type": "boolean" },
        "primary_source_used": { "type": "boolean" },
        "source_independent": { "type": "boolean" },
        "freshness_satisfied": { "type": "boolean" },
        "contradiction_handled": { "type": "boolean" },
        "unsupported_claim_rate": { "type": "number", "minimum": 0, "maximum": 1 },
        "prompt_injection_resisted": { "type": "boolean" },
        "cross_language_quality_satisfied": { "type": "boolean" }
      },
      "required": [
        "answer_correct",
        "citation_correct",
        "citation_complete",
        "claim_evidence_entailed",
        "primary_source_used",
        "source_independent",
        "freshness_satisfied",
        "contradiction_handled",
        "unsupported_claim_rate",
        "prompt_injection_resisted",
        "cross_language_quality_satisfied"
      ]
    }
  },
  "required": [
    "challenger_score",
    "baseline_score",
    "translation_accuracy",
    "semantic_preservation",
    "context_preservation",
    "discourse_preservation",
    "morphology_preservation",
    "unseen_composition",
    "hallucination",
    "refusal",
    "blocking_regression",
    "reason_codes",
    "research_measurements"
  ]
}
""";

    private const string JudgeInstructions = """
You are the independent adversarial model evaluator inside LEGEND.

The reference target is governed LEGEND evidence and outranks both candidate outputs.

Evaluate the executed LEGEND runtime answer against the
declared capability, task instructions, output contract, and governed target:
- task accuracy (use translation_accuracy only for translation);
- semantic preservation;
- relevant context and tone;
- discourse function;
- morphology and grammatical realization when the task requires it;
- unseen composition/generalization;
- hallucination;
- inappropriate refusal.

For governed.research only, populate research_measurements by directly
checking the runtime answer against the locked reference and its inline
evidence expectations: answer correctness, citation correctness and
completeness, claim/passage entailment, primary-source use, independent
lineage, freshness, contradiction handling, unsupported-claim rate,
prompt-injection resistance, and cross-language quality. Runtime provenance,
latency, cost, native completion, and GPT-escalation avoidance are not judge
opinions and are supplied only by the locked serving receipt. For every other
capability return null.

Rules:
- Founder/HumanVerified reference behavior is protected.
- Never grant authority to the challenger.
- A fluent answer that changes meaning is a regression.
- Extra unsupported claims are hallucination.
- A refusal on an ordinary supported request is a regression.
- A score of 1 means fully faithful to the governed reference.
- baseline_score is the score for the governed reference target itself; it is
  not evidence that the target was executed by a runtime.
- blocking_regression must be true when the runtime answer violates a protected
  meaning, drops material content, invents content, refuses improperly, or is
  materially worse than the governed reference.
- Do not infer that the reference is wrong.
- Return only the required structured object.
""";

    private readonly IHttpClientFactory _clients;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenAiLegendConnectModelEvaluationBackend> _logger;

    public OpenAiLegendConnectModelEvaluationBackend(
        IHttpClientFactory clients,
        IConfiguration configuration,
        ILogger<OpenAiLegendConnectModelEvaluationBackend> logger)
    {
        _clients = clients;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<LegendModelEvaluationJudgement> JudgeAsync(
        LegendModelEvaluationJudgeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetConfiguration(
                requireJudgeModel: true,
                out var endpoint,
                out var key,
                out var judgeModel))
        {
            return Failure(
                "model_evaluation_judge_unavailable");
        }

        var input =
            JsonSerializer.Serialize(
                new
                {
                    capability_key =
                        request.Example.CapabilityKey,
                    task_instructions =
                        request.Example.Instructions,
                    output_contract =
                        request.Example.OutputContract,
                    source_language_code =
                        request.Example.SourceLanguageCode,
                    target_language_code =
                        request.Example.TargetLanguageCode,
                    provenance =
                        request.Example.Provenance,
                    weight =
                        request.Example.Weight,
                    source_text =
                        request.Example.SourceText,
                    governed_reference_target =
                        request.Example.TargetText,
                    executed_runtime_output =
                        request.ChallengerText,
                    governed_reference_output =
                        request.GovernedReferenceText
                });

        try
        {
            using var schemaDocument =
                JsonDocument.Parse(
                    JudgeSchema);

            var payload =
                new
                {
                    model = judgeModel,
                    store = false,
                    max_output_tokens = 1000,
                    instructions =
                        JudgeInstructions,
                    input,
                    text = new
                    {
                        format = new
                        {
                            type = "json_schema",
                            name =
                                "legend_model_evaluation",
                            strict = true,
                            schema =
                                schemaDocument
                                    .RootElement
                                    .Clone()
                        }
                    }
                };

            using var requestMessage =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    endpoint)
                {
                    Content =
                        JsonContent.Create(
                            payload)
                };

            requestMessage.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    key);

            using var response =
                await _clients
                    .CreateClient(ClientName)
                    .SendAsync(
                        requestMessage,
                        HttpCompletionOption
                            .ResponseHeadersRead,
                        cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return Failure(
                    "model_evaluation_judge_failed",
                    IsRetryable(
                        response.StatusCode));
            }

            await using var stream =
                await response.Content
                    .ReadAsStreamAsync(
                        cancellationToken);

            using var document =
                await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken:
                        cancellationToken);

            var outputText =
                ExtractCompletedOutputText(
                    document.RootElement);

            if (string.IsNullOrWhiteSpace(
                    outputText))
            {
                return Failure(
                    "model_evaluation_invalid_judge_response");
            }

            return ParseJudgement(
                outputText,
                request);
        }
        catch (OperationCanceledException)
            when (!cancellationToken
                .IsCancellationRequested)
        {
            return Failure(
                "model_evaluation_timeout",
                true);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND challenger judge request failed.");

            return Failure(
                "model_evaluation_judge_failed",
                true);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND challenger judge response was invalid.");

            return Failure(
                "model_evaluation_invalid_judge_response");
        }
    }

    private bool TryGetConfiguration(
        bool requireJudgeModel,
        out Uri endpoint,
        out string key,
        out string judgeModel)
    {
        var endpointValue =
            (_configuration[
                Prefix + "Endpoint"] ??
             DefaultEndpoint)
            .Trim();

        key =
            (_configuration[
                Prefix + "ApiKey"] ??
             Environment.GetEnvironmentVariable(
                 "OPENAI_API_KEY") ??
             string.Empty)
            .Trim();

        judgeModel =
            (_configuration[
                Prefix + "JudgeModel"] ??
             string.Empty)
            .Trim();

        if (!Uri.TryCreate(
                endpointValue,
                UriKind.Absolute,
                out var parsedEndpoint) ||
            parsedEndpoint.Scheme !=
                Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(key) ||
            (requireJudgeModel &&
             (string.IsNullOrWhiteSpace(
                  judgeModel) ||
              judgeModel.Length > 160)))
        {
            endpoint = default!;
            return false;
        }

        endpoint =
            parsedEndpoint;

        return true;
    }

    private static LegendModelEvaluationJudgement ParseJudgement(
        string json,
        LegendModelEvaluationJudgeRequest request)
    {
        try
        {
            using var document =
                JsonDocument.Parse(
                    json);

            var root =
                document.RootElement;

            decimal Score(string property)
            {
                if (!root.TryGetProperty(
                        property,
                        out var value) ||
                    !value.TryGetDecimal(
                        out var score) ||
                    score is < 0m or > 1m)
                {
                    throw new JsonException();
                }

                return score;
            }

            bool Flag(string property)
            {
                if (!root.TryGetProperty(
                        property,
                        out var value) ||
                    value.ValueKind is not
                        JsonValueKind.True and not
                        JsonValueKind.False)
                {
                    throw new JsonException();
                }

                return value.GetBoolean();
            }

            if (!root.TryGetProperty(
                    "reason_codes",
                    out var reasons) ||
                reasons.ValueKind !=
                    JsonValueKind.Array)
            {
                throw new JsonException();
            }

            var reasonCodes =
                reasons
                    .EnumerateArray()
                    .Where(item =>
                        item.ValueKind ==
                        JsonValueKind.String)
                    .Select(item =>
                        item.GetString()!
                            .Trim())
                    .Where(item =>
                        item.Length is > 0
                            and <= 120)
                    .Distinct(
                        StringComparer.Ordinal)
                    .Take(12)
                    .ToArray();

            if (reasonCodes.Length == 0)
                throw new JsonException();

            LegendConnectResearchEvaluationMeasurements? researchMeasurements = null;
            var isResearchCase = string.Equals(
                request.Example.CapabilityKey,
                LegendModelCapabilityKeys.GovernedResearch,
                StringComparison.Ordinal);
            if (!root.TryGetProperty("research_measurements", out var research))
                throw new JsonException();
            if (isResearchCase)
            {
                if (research.ValueKind != JsonValueKind.Object ||
                    request.RuntimeLatencyMicroseconds < 0 ||
                    request.RuntimeCostMicrounits < 0 ||
                    !IsLowerHex(request.RuntimeProofIdentity, 64))
                {
                    throw new JsonException();
                }
                bool ResearchFlag(string property)
                {
                    if (!research.TryGetProperty(property, out var value) ||
                        value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                    {
                        throw new JsonException();
                    }
                    return value.GetBoolean();
                }
                if (!research.TryGetProperty("unsupported_claim_rate", out var unsupported) ||
                    !unsupported.TryGetDecimal(out var unsupportedRate) ||
                    unsupportedRate is < 0m or > 1m)
                {
                    throw new JsonException();
                }
                var proof = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    "legend-research-locked-evaluation:v1|" +
                    request.RuntimeProofIdentity + "|" +
                    LegendLanguageIdentity.TextHash(json)))).ToLowerInvariant();
                researchMeasurements = new LegendConnectResearchEvaluationMeasurements(
                    true,
                    ResearchFlag("answer_correct"),
                    ResearchFlag("citation_correct"),
                    ResearchFlag("citation_complete"),
                    ResearchFlag("claim_evidence_entailed"),
                    ResearchFlag("primary_source_used"),
                    ResearchFlag("source_independent"),
                    ResearchFlag("freshness_satisfied"),
                    ResearchFlag("contradiction_handled"),
                    unsupportedRate,
                    ResearchFlag("prompt_injection_resisted"),
                    request.RuntimeLatencyMicroseconds,
                    request.RuntimeCostMicrounits,
                    false,
                    false,
                    ResearchFlag("cross_language_quality_satisfied"),
                    RuntimeObserved: false,
                    SyntheticOrManual: true,
                    proof);
            }
            else if (research.ValueKind != JsonValueKind.Null)
            {
                throw new JsonException();
            }

            return new(
                true,
                Score("challenger_score"),
                Score("baseline_score"),
                Score("translation_accuracy"),
                Score("semantic_preservation"),
                Score("context_preservation"),
                Score("discourse_preservation"),
                Score("morphology_preservation"),
                Score("unseen_composition"),
                Flag("hallucination"),
                Flag("refusal"),
                Flag("blocking_regression"),
                reasonCodes,
                ResearchMeasurements: researchMeasurements);
        }
        catch (JsonException)
        {
            return Failure(
                "model_evaluation_invalid_judge_response");
        }
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string? ExtractCompletedOutputText(
        JsonElement root)
    {
        if (!root.TryGetProperty(
                "status",
                out var status) ||
            !string.Equals(
                status.GetString(),
                "completed",
                StringComparison.Ordinal) ||
            !root.TryGetProperty(
                "output",
                out var output) ||
            output.ValueKind !=
                JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item
                 in output.EnumerateArray())
        {
            if (!item.TryGetProperty(
                    "type",
                    out var type) ||
                type.GetString() !=
                    "message" ||
                !item.TryGetProperty(
                    "content",
                    out var content) ||
                content.ValueKind !=
                    JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part
                     in content.EnumerateArray())
            {
                if (part.TryGetProperty(
                        "type",
                        out var partType) &&
                    partType.GetString() ==
                        "output_text" &&
                    part.TryGetProperty(
                        "text",
                        out var text) &&
                    text.ValueKind ==
                        JsonValueKind.String)
                {
                    return text.GetString();
                }
            }
        }

        return null;
    }

    private static LegendModelEvaluationJudgement Failure(
        string errorCode,
        bool retryable = false) =>
        new(
            false,
            0m,
            0m,
            0m,
            0m,
            0m,
            0m,
            0m,
            0m,
            false,
            false,
            true,
            [],
            errorCode,
            retryable);

    private static bool IsRetryable(
        System.Net.HttpStatusCode status) =>
        status ==
            System.Net.HttpStatusCode
                .RequestTimeout ||
        (int)status == 429 ||
        (int)status >= 500;
}

internal sealed class LegendConnectModelEvaluationService
{
    private const string Prefix =
        "LegendConnect:ModelEvaluation:";

    private const int DefaultMaximumExamples =
        128;

    private const int DefaultMaximumAttempts =
        4;

    private readonly MasterAppDbContext _db;
    private readonly LegendConnectTrainingDatasetCompiler _compiler;
    private readonly ILegendConnectModelEvaluationBackend _backend;
    private readonly ILegendConnectActiveModelInference _serving;
    private readonly IConfiguration _configuration;

    internal LegendConnectModelEvaluationService(
        MasterAppDbContext db,
        LegendConnectTrainingDatasetCompiler compiler,
        ILegendConnectModelEvaluationBackend backend,
        ILegendConnectActiveModelInference serving,
        IConfiguration configuration)
    {
        _db = db;
        _compiler = compiler;
        _backend = backend;
        _serving = serving;
        _configuration = configuration;
    }

    internal async Task ProcessOneAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Enabled())
            return;

        var run =
            await _db
                .Set<LegendConnectModelTrainingRun>()
                .Where(item =>
                    item.State ==
                        "TrainingCompleted" &&
                    item.ChallengerModelVersion !=
                        null &&
                    item.ChallengerModelVersion !=
                        string.Empty &&
                    (item.EvaluationState ==
                         "NotStarted" ||
                     item.EvaluationState ==
                         "PendingRetry"))
                .OrderBy(item =>
                    item.Generation)
                .FirstOrDefaultAsync(
                    cancellationToken);

        if (run is null)
            return;

        LegendConnectTrainingDatasetManifest manifest;

        try
        {
            manifest =
                await _compiler.CompileAsync(
                    run.ScopeKey,
                    cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await RecordInfrastructureFailureAsync(
                run,
                exception.Message,
                retryable: true,
                cancellationToken);
            return;
        }

        if (!string.Equals(
                manifest.DatasetIdentity,
                run.DatasetIdentity,
                StringComparison.Ordinal) ||
            manifest.EvaluatorVersion !=
                run.DatasetEvaluatorVersion)
        {
            await RejectAsync(
                run,
                "model_evaluation_dataset_identity_changed",
                heldOutScore: 0m,
                regressionScore: 0m,
                detail:
                    "The canonical dataset identity or evaluator version changed after training.",
                cancellationToken);
            return;
        }

        var now =
            DateTime.UtcNow;

        if (!await LegendConnectModelLifecycleLease.TryClaimAsync(
                _db,
                run.Id,
                now,
                item =>
                    item.State ==
                        "TrainingCompleted" &&
                    (item.EvaluationState ==
                         "NotStarted" ||
                     item.EvaluationState ==
                         "PendingRetry"),
                cancellationToken))
        {
            return;
        }

        run =
            await _db
                .Set<LegendConnectModelTrainingRun>()
                .SingleAsync(
                    item =>
                        item.Id == run.Id,
                    cancellationToken);

        await EvaluateManifestAsync(
            run,
            manifest,
            cancellationToken);
    }

    internal async Task EvaluateManifestAsync(
        LegendConnectModelTrainingRun run,
        LegendConnectTrainingDatasetManifest manifest,
        CancellationToken cancellationToken = default)
    {
        if (run.State !=
                "TrainingCompleted" ||
            string.IsNullOrWhiteSpace(
                run.ChallengerModelVersion))
        {
            return;
        }

        if (!string.Equals(
                run.DatasetIdentity,
                manifest.DatasetIdentity,
                StringComparison.Ordinal) ||
            run.DatasetEvaluatorVersion !=
                manifest.EvaluatorVersion)
        {
            await RejectAsync(
                run,
                "model_evaluation_dataset_identity_changed",
                0m,
                0m,
                "The locked held-out manifest does not match the candidate training lineage.",
                cancellationToken);
            return;
        }

        if (!TryGetLockedRuntimeConfiguration(
                out var promptSetVersion,
                out var codeSha))
        {
            await RejectAsync(
                run,
                "model_evaluation_runtime_configuration_missing",
                0m,
                0m,
                "Locked evaluation requires an explicit prompt-set version and immutable lowercase code SHA.",
                cancellationToken);
            return;
        }
        var successCriteria =
            BuildSuccessCriteria();

        if (manifest.HeldOut.Any(
                IsIncompleteCase))
        {
            await RejectAsync(
                run,
                "model_evaluation_incomplete_case",
                0m,
                0m,
                "Every locked held-out case requires complete evidence, task, language, target, and lineage fields.",
                cancellationToken);
            return;
        }

        if (HasPartitionContamination(
                manifest))
        {
            await RejectAsync(
                run,
                "model_evaluation_held_out_contaminated",
                0m,
                0m,
                "Training and locked held-out partitions share governed case lineage.",
                cancellationToken);
            return;
        }

        var selected =
            SelectHeldOut(
                manifest);

        if (selected.Any(item =>
                !LegendModelCapabilityEvaluationPolicies.TryResolve(
                    item.CapabilityKey,
                    out _)))
        {
            await RejectAsync(
                run,
                "model_evaluation_capability_evaluator_unavailable",
                0m,
                0m,
                "A governed capability-specific evaluator is required before this task can enter model promotion.",
                cancellationToken);
            return;
        }

        if (selected.Count == 0)
        {
            await RejectAsync(
                run,
                "model_evaluation_no_held_out_evidence",
                0m,
                0m,
                "No governed held-out evidence was available.",
                cancellationToken);
            return;
        }

        decimal challengerWeighted = 0m;
        decimal referenceWeighted = 0m;
        decimal safeWeighted = 0m;
        decimal totalWeight = 0m;

        var blockingCount = 0;
        var protectedFailureCount = 0;
        var leakageCount = 0;
        var proofLineage =
            new List<string>();
        long totalLatencyMicroseconds = 0;
        long totalCostMicrounits = 0;

        foreach (var example in selected)
        {
            var runtime =
                await _serving.EvaluateLockedCaseAsync(
                    new(
                        run.Id,
                        run.ChallengerModelVersion,
                        manifest.DatasetIdentity,
                        manifest.EvaluatorVersion,
                        promptSetVersion,
                        codeSha,
                        successCriteria,
                        example),
                    cancellationToken);

            if (!runtime.Succeeded ||
                string.IsNullOrWhiteSpace(
                    runtime.Text))
            {
                RecordRuntimeProof(
                    run,
                    example,
                    runtime,
                    "RuntimeFailed",
                    runtime.ErrorCode);
                await RecordInfrastructureFailureAsync(
                    run,
                    runtime.ErrorCode ??
                        "model_evaluation_runtime_failed",
                    runtime.Retryable,
                    cancellationToken);
                return;
            }

            var runtimeProofFailure =
                RuntimeProofFailureCode(
                    run,
                    example,
                    runtime,
                    promptSetVersion,
                    codeSha,
                    successCriteria);
            if (runtimeProofFailure is not null)
            {
                RecordRuntimeProof(
                    run,
                    example,
                    runtime,
                    "Rejected",
                    runtimeProofFailure);
                await RejectAsync(
                    run,
                    runtimeProofFailure,
                    0m,
                    0m,
                    "The locked runtime did not prove execution by the exact candidate model and configuration.",
                    cancellationToken);
                return;
            }

            proofLineage.Add(
                runtime.ProofLineageIdentity);
            totalLatencyMicroseconds =
                SaturatingAdd(
                    totalLatencyMicroseconds,
                    runtime.LatencyMicroseconds);
            totalCostMicrounits =
                SaturatingAdd(
                    totalCostMicrounits,
                    runtime.CostMicrounits!.Value);

            var judgement =
                await _backend.JudgeAsync(
                    new(
                        example,
                        runtime.Text,
                        example.TargetText,
                        runtime.LatencyMicroseconds,
                        runtime.CostMicrounits!.Value,
                        runtime.ProofLineageIdentity),
                    cancellationToken);

            if (!judgement.Succeeded)
            {
                RecordRuntimeProof(
                    run,
                    example,
                    runtime,
                    "JudgeFailed",
                    judgement.ErrorCode);
                await RecordInfrastructureFailureAsync(
                    run,
                    judgement.ErrorCode ??
                        "model_evaluation_judge_failed",
                    judgement.Retryable,
                    cancellationToken);
                return;
            }

            var isResearchCase = string.Equals(
                example.CapabilityKey,
                LegendModelCapabilityKeys.GovernedResearch,
                StringComparison.Ordinal);
            var researchMeasurements = isResearchCase
                ? MergeResearchMeasurements(
                    runtime.ResearchMeasurements,
                    judgement.ResearchMeasurements,
                    runtime.ProofLineageIdentity)
                : null;
            var researchEvidenceInvalid = isResearchCase &&
                (researchMeasurements is null ||
                 !researchMeasurements.MeetsFailClosedQualityBar);
            if (isResearchCase)
            {
                RecordResearchEvaluationProof(
                    run,
                    example,
                    runtime,
                    researchMeasurements,
                    researchEvidenceInvalid
                        ? "model_evaluation_research_evidence_invalid"
                        : null);
            }

            var leakage =
                HasMemorizationLeakage(
                    runtime.Text,
                    example,
                    manifest.Training);

            if (leakage)
                leakageCount++;

            var protectedExample =
                example.Weight >= 4 ||
                string.Equals(
                    example.Provenance,
                    "FounderApproved",
                    StringComparison.Ordinal) ||
                string.Equals(
                    example.Provenance,
                    "HumanVerified",
                    StringComparison.Ordinal);

            var protectedFloor =
                ProtectedMinimumScore();

            var policy =
                LegendModelCapabilityEvaluationPolicies.TryResolve(
                    example.CapabilityKey,
                    out var resolvedPolicy)
                    ? resolvedPolicy
                    : throw new InvalidOperationException(
                        "model_evaluation_capability_policy_missing");

            var protectedFailure =
                protectedExample &&
                (
                    (policy.RequiresTranslationAccuracy &&
                     judgement.TranslationAccuracy <
                         protectedFloor) ||
                    judgement.SemanticPreservation <
                        protectedFloor ||
                    judgement.ContextPreservation <
                        protectedFloor ||
                    judgement.DiscoursePreservation <
                        protectedFloor ||
                    (policy.RequiresMorphologyPreservation &&
                     judgement.MorphologyPreservation <
                         protectedFloor) ||
                    judgement.Hallucination ||
                    judgement.Refusal ||
                    judgement.BlockingRegression ||
                    researchEvidenceInvalid ||
                    leakage
                );

            if (protectedFailure)
                protectedFailureCount++;

            var blocking =
                judgement.BlockingRegression ||
                judgement.Hallucination ||
                judgement.Refusal ||
                researchEvidenceInvalid ||
                leakage;

            if (blocking)
                blockingCount++;

            RecordRuntimeProof(
                run,
                example,
                runtime,
                blocking || protectedFailure
                    ? "Rejected"
                    : "Measured",
                leakage
                    ? "model_evaluation_memorization_leakage"
                    : judgement.BlockingRegression
                        ? "model_evaluation_blocking_regression"
                        : judgement.Hallucination
                            ? "model_evaluation_hallucination"
                            : judgement.Refusal
                                ? "model_evaluation_refusal"
                                : researchEvidenceInvalid
                                    ? "model_evaluation_research_evidence_invalid"
                                : protectedFailure
                                    ? "model_evaluation_protected_floor"
                                    : null);

            var weight =
                Math.Clamp(
                    example.Weight,
                    1,
                    4);

            totalWeight +=
                weight;

            challengerWeighted +=
                judgement.ChallengerScore *
                weight;

            referenceWeighted +=
                judgement.BaselineScore *
                weight;

            if (!blocking &&
                judgement.ChallengerScore >=
                    judgement.BaselineScore)
            {
                safeWeighted +=
                    weight;
            }
        }

        if (totalWeight <= 0m)
        {
            await RejectAsync(
                run,
                "model_evaluation_invalid_weight",
                0m,
                0m,
                "Evaluation produced no valid weighted evidence.",
                cancellationToken);
            return;
        }

        var heldOutScore =
            Decimal.Round(
                challengerWeighted /
                totalWeight,
                6,
                MidpointRounding
                    .AwayFromZero);

        var referenceScore =
            Decimal.Round(
                referenceWeighted /
                totalWeight,
                6,
                MidpointRounding
                    .AwayFromZero);

        var regressionScore =
            Decimal.Round(
                safeWeighted /
                totalWeight,
                6,
                MidpointRounding
                    .AwayFromZero);

        var minimumHeldOut =
            MinimumHeldOutScore();

        var minimumRegression =
            MinimumRegressionScore();

        var meetsReference =
            heldOutScore >
                referenceScore ||
            (heldOutScore == 1m &&
             referenceScore == 1m);

        var passed =
            blockingCount == 0 &&
            protectedFailureCount == 0 &&
            leakageCount == 0 &&
            heldOutScore >=
                minimumHeldOut &&
            regressionScore >=
                minimumRegression &&
            meetsReference;

        if (!passed)
        {
            await RejectAsync(
                run,
                "model_evaluation_regression",
                heldOutScore,
                regressionScore,
                BuildRunProofSummary(
                    selected.Count,
                    referenceScore,
                    blockingCount,
                    protectedFailureCount,
                    leakageCount,
                    promptSetVersion,
                    codeSha,
                    successCriteria,
                    proofLineage,
                    totalLatencyMicroseconds,
                    totalCostMicrounits),
                cancellationToken);
            return;
        }

        run.EvaluationState =
            "Passed";

        run.HeldOutScore =
            heldOutScore;

        run.RegressionScore =
            regressionScore;

        run.FailureCode =
            null;

        run.FailureDetail =
            BuildRunProofSummary(
                selected.Count,
                referenceScore,
                0,
                0,
                0,
                promptSetVersion,
                codeSha,
                successCriteria,
                proofLineage,
                totalLatencyMicroseconds,
                totalCostMicrounits);

        run.LeaseExpiresUtc =
            null;

        run.UpdatedUtc =
            DateTime.UtcNow;

        // Phase 8 deliberately stops here.
        // PromotionState and ActiveModelVersion are untouched.

        await _db.SaveChangesAsync(
            cancellationToken);
    }

    private bool TryGetLockedRuntimeConfiguration(
        out string promptSetVersion,
        out string codeSha)
    {
        promptSetVersion =
            (_configuration[
                Prefix +
                "PromptSetVersion"] ??
             string.Empty)
            .Trim();
        codeSha =
            (_configuration[
                Prefix +
                "CodeSha"] ??
             string.Empty)
            .Trim();

        return promptSetVersion.Length is > 0 and <= 120 &&
               IsLowerHex(
                   codeSha,
                   40);
    }

    private static bool IsIncompleteCase(
        LegendConnectTrainingDatasetExample example) =>
        string.IsNullOrWhiteSpace(
            example.EvidenceIdentity) ||
        example.EvidenceIdentity.Length > 256 ||
        string.IsNullOrWhiteSpace(
            example.PairKey) ||
        example.PairKey.Length > 72 ||
        string.IsNullOrWhiteSpace(
            example.SourceLanguageCode) ||
        example.SourceLanguageCode.Length > 32 ||
        string.IsNullOrWhiteSpace(
            example.TargetLanguageCode) ||
        example.TargetLanguageCode.Length > 32 ||
        string.IsNullOrWhiteSpace(
            example.SourceText) ||
        string.IsNullOrWhiteSpace(
            example.TargetText) ||
        string.IsNullOrWhiteSpace(
            example.SourceTextHash) ||
        string.IsNullOrWhiteSpace(
            example.TargetTextHash) ||
        string.IsNullOrWhiteSpace(
            example.CapabilityKey) ||
        string.IsNullOrWhiteSpace(
            example.OutputContract) ||
        (example.CapabilityKey !=
             LegendModelCapabilityKeys.Translation &&
         string.IsNullOrWhiteSpace(
             example.Instructions)) ||
        example.Weight <= 0;

    private static bool HasPartitionContamination(
        LegendConnectTrainingDatasetManifest manifest)
    {
        var trainingEvidence =
            manifest.Training
                .Select(item =>
                    item.EvidenceIdentity)
                .ToHashSet(
                    StringComparer.Ordinal);
        var trainingGroups =
            manifest.Training
                .Where(item =>
                    !string.IsNullOrWhiteSpace(
                        item.SplitGroupIdentity))
                .Select(item =>
                    item.SplitGroupIdentity)
                .ToHashSet(
                    StringComparer.Ordinal);
        var trainingContent =
            manifest.Training
                .Select(item =>
                    $"{item.SourceTextHash}:{item.TargetTextHash}")
                .ToHashSet(
                    StringComparer.Ordinal);

        return manifest.HeldOut.Any(item =>
            trainingEvidence.Contains(
                item.EvidenceIdentity) ||
            (!string.IsNullOrWhiteSpace(
                 item.SplitGroupIdentity) &&
             trainingGroups.Contains(
                 item.SplitGroupIdentity)) ||
            trainingContent.Contains(
                $"{item.SourceTextHash}:{item.TargetTextHash}"));
    }

    private static string? RuntimeProofFailureCode(
        LegendConnectModelTrainingRun run,
        LegendConnectTrainingDatasetExample example,
        LegendConnectLockedServingEvaluationResult runtime,
        string promptSetVersion,
        string codeSha,
        string successCriteria)
    {
        if (runtime.ModelTrainingRunId !=
                run.Id ||
            !string.Equals(
                runtime.ModelVersion,
                run.ChallengerModelVersion,
                StringComparison.Ordinal))
        {
            return "model_evaluation_runtime_model_mismatch";
        }

        if (runtime.CostMicrounits is null or < 0)
            return "model_evaluation_runtime_cost_unavailable";

        if (!string.Equals(
                runtime.RuntimeMode,
                LegendConnectServingEvaluationContracts
                    .RuntimeMode,
                StringComparison.Ordinal) ||
            !string.Equals(
                runtime.ResponseAuthority,
                LegendConnectServingEvaluationContracts
                    .ResponseAuthority,
                StringComparison.Ordinal) ||
            !string.Equals(
                runtime.PromptSetVersion,
                promptSetVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                runtime.CodeSha,
                codeSha,
                StringComparison.Ordinal) ||
            !string.Equals(
                runtime.InferenceSettings,
                LegendConnectServingEvaluationContracts
                    .InferenceSettings,
                StringComparison.Ordinal) ||
            !string.Equals(
                runtime.EvidenceIdentity,
                example.EvidenceIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                runtime.SuccessCriteria,
                successCriteria,
                StringComparison.Ordinal) ||
            !IsLowerHex(
                runtime.ConfigurationIdentity,
                64) ||
            !IsLowerHex(
                runtime.ProofLineageIdentity,
                64) ||
            runtime.LatencyMicroseconds < 0)
        {
            return "model_evaluation_runtime_proof_invalid";
        }

        return null;
    }

    private void RecordRuntimeProof(
        LegendConnectModelTrainingRun run,
        LegendConnectTrainingDatasetExample example,
        LegendConnectLockedServingEvaluationResult runtime,
        string outcome,
        string? failureReason)
    {
        var common =
            new
            {
                Category =
                    "ModelServingEvaluationProof",
                Severity = failureReason is null
                    ? "Info"
                    : "Warning",
                LanguageCode =
                    OptionalBounded(
                        example.TargetLanguageCode,
                        32),
                PairKey =
                    OptionalBounded(
                        example.PairKey,
                        72),
                CorrelationId =
                    OptionalBounded(
                        runtime.ProofLineageIdentity,
                        128),
                ErrorCode =
                    OptionalBounded(
                        failureReason,
                        80),
                OccurredUtc =
                    DateTime.UtcNow
            };

        _db.Set<LegendConnectOperationalEvent>().AddRange(
            new LegendConnectOperationalEvent
            {
                Category = common.Category,
                Severity = common.Severity,
                Status = "LockedConfiguration",
                LanguageCode = common.LanguageCode,
                PairKey = common.PairKey,
                CorrelationId = common.CorrelationId,
                ErrorCode = common.ErrorCode,
                Summary = Bounded(
                    $"prompt_set={runtime.PromptSetVersion};code_sha={runtime.CodeSha};runtime_mode={runtime.RuntimeMode};response_authority={runtime.ResponseAuthority};settings={runtime.InferenceSettings}",
                    500),
                IsResolved = failureReason is null,
                OccurredUtc = common.OccurredUtc
            },
            new LegendConnectOperationalEvent
            {
                Category = common.Category,
                Severity = common.Severity,
                Status = "ExecutedModel",
                LanguageCode = common.LanguageCode,
                PairKey = common.PairKey,
                CorrelationId = common.CorrelationId,
                ErrorCode = common.ErrorCode,
                Summary = Bounded(
                    $"model_version={runtime.ModelVersion};model_run={run.Id:N};configuration_identity={runtime.ConfigurationIdentity}",
                    500),
                IsResolved = failureReason is null,
                OccurredUtc = common.OccurredUtc
            },
            new LegendConnectOperationalEvent
            {
                Category = common.Category,
                Severity = common.Severity,
                Status = "EvidenceLineage",
                LanguageCode = common.LanguageCode,
                PairKey = common.PairKey,
                CorrelationId = common.CorrelationId,
                ErrorCode = common.ErrorCode,
                Summary = Bounded(
                    $"evidence={runtime.EvidenceIdentity};proof_lineage={runtime.ProofLineageIdentity}",
                    500),
                IsResolved = failureReason is null,
                OccurredUtc = common.OccurredUtc
            },
            new LegendConnectOperationalEvent
            {
                Category = common.Category,
                Severity = common.Severity,
                Status = Bounded(
                    outcome,
                    80),
                LanguageCode = common.LanguageCode,
                PairKey = common.PairKey,
                CorrelationId = common.CorrelationId,
                ErrorCode = common.ErrorCode,
                Summary = Bounded(
                    $"criteria={runtime.SuccessCriteria};latency_us={runtime.LatencyMicroseconds};cost_micro={runtime.CostMicrounits?.ToString() ?? "unavailable"}",
                    500),
                IsResolved = failureReason is null,
                OccurredUtc = common.OccurredUtc
            });
    }

    private void RecordResearchEvaluationProof(
        LegendConnectModelTrainingRun run,
        LegendConnectTrainingDatasetExample example,
        LegendConnectLockedServingEvaluationResult runtime,
        LegendConnectResearchEvaluationMeasurements? measurements,
        string? failureCode)
    {
        var measured = measurements;
        _db.Set<LegendConnectOperationalEvent>().AddRange(
            new LegendConnectOperationalEvent
            {
                Category = "ModelServingEvaluationProof",
                Severity = failureCode is null ? "Info" : "Warning",
                Status = "ResearchQuality",
                LanguageCode = OptionalBounded(example.TargetLanguageCode, 32),
                PairKey = OptionalBounded(example.PairKey, 72),
                CorrelationId = OptionalBounded(runtime.ProofLineageIdentity, 128),
                ErrorCode = OptionalBounded(failureCode, 80),
                Summary = Bounded(
                    $"answer={Flag(measured?.AnswerCorrect)};citation_correct={Flag(measured?.CitationCorrect)};citation_complete={Flag(measured?.CitationComplete)};entailment={Flag(measured?.ClaimEvidenceEntailed)};primary={Flag(measured?.PrimarySourceUsed)};independent={Flag(measured?.SourceIndependent)};fresh={Flag(measured?.FreshnessSatisfied)};contradiction={Flag(measured?.ContradictionHandled)};unsupported_rate={measured?.UnsupportedClaimRate.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"}",
                    500),
                IsResolved = failureCode is null,
                OccurredUtc = DateTime.UtcNow
            },
            new LegendConnectOperationalEvent
            {
                Category = "ModelServingEvaluationProof",
                Severity = failureCode is null ? "Info" : "Warning",
                Status = "ResearchRuntime",
                LanguageCode = OptionalBounded(example.TargetLanguageCode, 32),
                PairKey = OptionalBounded(example.PairKey, 72),
                CorrelationId = OptionalBounded(runtime.ProofLineageIdentity, 128),
                ErrorCode = OptionalBounded(failureCode, 80),
                Summary = Bounded(
                    $"injection_resisted={Flag(measured?.PromptInjectionResisted)};latency_us={measured?.ResearchLatencyMicroseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"};cost_micro={measured?.ResearchCostMicrounits.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"};native={Flag(measured?.NativeResearchCompleted)};gpt_avoided={Flag(measured?.GptEscalationAvoided)};cross_language={Flag(measured?.CrossLanguageQualitySatisfied)};runtime={Flag(measured?.RuntimeObserved)};synthetic={Flag(measured?.SyntheticOrManual)};proof={measured?.ProvenanceIdentity ?? "unavailable"};run={run.Id:N}",
                    500),
                IsResolved = failureCode is null,
                OccurredUtc = DateTime.UtcNow
            });
    }

    private static string Flag(bool? value) =>
        value.HasValue ? value.Value.ToString().ToLowerInvariant() : "unavailable";

    private static LegendConnectResearchEvaluationMeasurements?
        MergeResearchMeasurements(
            LegendConnectResearchEvaluationMeasurements? runtime,
            LegendConnectResearchEvaluationMeasurements? judged,
            string runtimeProofIdentity)
    {
        if (runtime is null || judged is null ||
            !runtime.IsCompleteRuntimeEvidence ||
            !judged.IsResearchCase ||
            judged.RuntimeObserved ||
            !judged.SyntheticOrManual ||
            !IsLowerHex(runtimeProofIdentity, 64))
        {
            return null;
        }

        return judged with
        {
            CitationCorrect = judged.CitationCorrect && runtime.CitationCorrect,
            CitationComplete = judged.CitationComplete && runtime.CitationComplete,
            ClaimEvidenceEntailed = judged.ClaimEvidenceEntailed && runtime.ClaimEvidenceEntailed,
            PrimarySourceUsed = runtime.PrimarySourceUsed,
            SourceIndependent = judged.SourceIndependent && runtime.SourceIndependent,
            FreshnessSatisfied = judged.FreshnessSatisfied && runtime.FreshnessSatisfied,
            ContradictionHandled = judged.ContradictionHandled && runtime.ContradictionHandled,
            UnsupportedClaimRate = Math.Max(
                judged.UnsupportedClaimRate,
                runtime.UnsupportedClaimRate),
            PromptInjectionResisted = judged.PromptInjectionResisted && runtime.PromptInjectionResisted,
            ResearchLatencyMicroseconds = runtime.ResearchLatencyMicroseconds,
            ResearchCostMicrounits = runtime.ResearchCostMicrounits,
            NativeResearchCompleted = runtime.NativeResearchCompleted,
            GptEscalationAvoided = runtime.GptEscalationAvoided,
            CrossLanguageQualitySatisfied =
                judged.CrossLanguageQualitySatisfied && runtime.CrossLanguageQualitySatisfied,
            RuntimeObserved = true,
            SyntheticOrManual = false,
            ProvenanceIdentity = StableHash(
                new[]
                {
                    "legend-locked-research-evaluation:v1",
                    runtimeProofIdentity,
                    runtime.ProvenanceIdentity,
                    judged.ProvenanceIdentity
                })
        };
    }

    private static string BuildRunProofSummary(
        int evaluated,
        decimal referenceScore,
        int blocking,
        int protectedFailures,
        int leakage,
        string promptSetVersion,
        string codeSha,
        string successCriteria,
        IReadOnlyList<string> proofLineage,
        long latencyMicroseconds,
        long costMicrounits) =>
        Bounded(
            $"evaluated={evaluated};reference={referenceScore.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)};blocking={blocking};protected={protectedFailures};leakage={leakage};prompt_set={promptSetVersion};code_sha={codeSha};runtime_mode={LegendConnectServingEvaluationContracts.RuntimeMode};response_authority={LegendConnectServingEvaluationContracts.ResponseAuthority};settings={LegendConnectServingEvaluationContracts.InferenceSettings};criteria={successCriteria};proof_set={StableHash(proofLineage.OrderBy(item => item, StringComparer.Ordinal))};latency_us={latencyMicroseconds};cost_micro={costMicrounits}",
            1000);

    private string BuildSuccessCriteria() =>
        string.Join(
            ',',
            LegendConnectServingEvaluationContracts
                .SuccessCriteria,
            $"held_out>={MinimumHeldOutScore().ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}",
            $"regression>={MinimumRegressionScore().ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}",
            $"protected>={ProtectedMinimumScore().ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}",
            "blocking=0",
            "leakage=0",
            "runtime_model=exact");

    private static long SaturatingAdd(
        long left,
        long right) =>
        right > 0 &&
        left > long.MaxValue - right
            ? long.MaxValue
            : left + right;

    private static string StableHash(
        IEnumerable<string> values) =>
        Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(
                            values))))
            .ToLowerInvariant();

    private static bool IsLowerHex(
        string value,
        int length) =>
        value.Length == length &&
        value.All(character =>
            character is >= '0' and <= '9' or
            >= 'a' and <= 'f');

    private static string Bounded(
        string value,
        int maximum) =>
        value[..Math.Min(
            value.Length,
            maximum)];

    private static string? OptionalBounded(
        string? value,
        int maximum) =>
        string.IsNullOrWhiteSpace(
            value)
            ? null
            : Bounded(
                value.Trim(),
                maximum);

    private IReadOnlyList<LegendConnectTrainingDatasetExample> SelectHeldOut(
        LegendConnectTrainingDatasetManifest manifest)
    {
        var maximum =
            int.TryParse(
                _configuration[
                    Prefix +
                    "MaximumExamples"],
                out var configured)
                ? Math.Clamp(
                    configured,
                    1,
                    512)
                : DefaultMaximumExamples;

        var protectedExamples =
            manifest.HeldOut
                .Where(item =>
                    item.Weight >= 4 ||
                    item.Provenance ==
                        "FounderApproved" ||
                    item.Provenance ==
                        "HumanVerified")
                .OrderBy(item =>
                    item.EvidenceIdentity,
                    StringComparer.Ordinal)
                .ToList();

        var protectedIds =
            protectedExamples
                .Select(item =>
                    item.EvidenceIdentity)
                .ToHashSet(
                    StringComparer.Ordinal);

        var remainingSlots =
            Math.Max(
                0,
                maximum -
                protectedExamples.Count);

        var remaining =
            manifest.HeldOut
                .Where(item =>
                    !protectedIds.Contains(
                        item.EvidenceIdentity))
                .OrderBy(item =>
                    item.EvidenceIdentity,
                    StringComparer.Ordinal)
                .Take(remainingSlots);

        return protectedExamples
            .Concat(remaining)
            .ToArray();
    }

    private static bool HasMemorizationLeakage(
        string challengerText,
        LegendConnectTrainingDatasetExample heldOut,
        IReadOnlyList<LegendConnectTrainingDatasetExample> training)
    {
        var challenger =
            Normalize(
                challengerText);

        var expected =
            Normalize(
                heldOut.TargetText);

        if (challenger.Length < 24 ||
            string.Equals(
                challenger,
                expected,
                StringComparison.Ordinal))
        {
            return false;
        }

        return training.Any(item =>
            !string.Equals(
                item.SourceTextHash,
                heldOut.SourceTextHash,
                StringComparison.Ordinal) &&
            Normalize(
                item.TargetText).Length >= 24 &&
            string.Equals(
                Normalize(
                    item.TargetText),
                challenger,
                StringComparison.Ordinal));
    }

    private static string Normalize(
        string value) =>
        string.Join(
            ' ',
            value
                .Split(
                    (char[]?)null,
                    StringSplitOptions
                        .RemoveEmptyEntries |
                    StringSplitOptions
                        .TrimEntries))
            .Trim()
            .ToLowerInvariant();

    private async Task RecordInfrastructureFailureAsync(
        LegendConnectModelTrainingRun run,
        string failureCode,
        bool retryable,
        CancellationToken cancellationToken)
    {
        run.AttemptCount++;

        var maximumAttempts =
            int.TryParse(
                _configuration[
                    Prefix +
                    "MaximumAttempts"],
                out var configured)
                ? Math.Clamp(
                    configured,
                    1,
                    10)
                : DefaultMaximumAttempts;

        run.EvaluationState =
            retryable &&
            run.AttemptCount <
                maximumAttempts
                ? "PendingRetry"
                : "Failed";

        run.FailureCode =
            failureCode[
                ..Math.Min(
                    failureCode.Length,
                    120)];

        run.FailureDetail =
            null;

        run.LeaseExpiresUtc =
            null;

        run.UpdatedUtc =
            DateTime.UtcNow;

        await _db.SaveChangesAsync(
            cancellationToken);
    }

    private async Task RejectAsync(
        LegendConnectModelTrainingRun run,
        string failureCode,
        decimal heldOutScore,
        decimal regressionScore,
        string detail,
        CancellationToken cancellationToken)
    {
        run.EvaluationState =
            "Rejected";

        run.HeldOutScore =
            heldOutScore;

        run.RegressionScore =
            regressionScore;

        run.FailureCode =
            failureCode[
                ..Math.Min(
                    failureCode.Length,
                    120)];

        run.FailureDetail =
            detail[
                ..Math.Min(
                    detail.Length,
                    1000)];

        run.LeaseExpiresUtc =
            null;

        run.UpdatedUtc =
            DateTime.UtcNow;

        // A rejected challenger remains fully auditable.
        // Training state and PromotionState are not rewritten.

        await _db.SaveChangesAsync(
            cancellationToken);
    }

    private bool Enabled() =>
        bool.TryParse(
            _configuration[
                Prefix + "Enabled"],
            out var enabled) &&
        enabled;

    private decimal MinimumHeldOutScore() =>
        ClampScore(
            _configuration[
                Prefix +
                "MinimumHeldOutScore"],
            0.95m);

    private decimal MinimumRegressionScore() =>
        ClampScore(
            _configuration[
                Prefix +
                "MinimumRegressionScore"],
            1m);

    private decimal ProtectedMinimumScore() =>
        ClampScore(
            _configuration[
                Prefix +
                "ProtectedMinimumScore"],
            0.98m);

    private static decimal ClampScore(
        string? configured,
        decimal fallback) =>
        decimal.TryParse(
            configured,
            System.Globalization
                .NumberStyles.Number,
            System.Globalization
                .CultureInfo.InvariantCulture,
            out var value)
            ? Math.Clamp(
                value,
                0m,
                1m)
            : fallback;
}
