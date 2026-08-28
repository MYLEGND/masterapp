using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    bool Retryable = false);

internal static class LegendModelCapabilityKeys
{
    internal const string Translation = "translation";
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
    string? TargetLanguageCode = null)
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
}

internal sealed record LegendCurrentProductionEvaluationResult(
    bool Succeeded,
    string? Text,
    string? Provider,
    string? ErrorCode = null);

internal sealed record LegendModelEvaluationJudgeRequest(
    LegendConnectTrainingDatasetExample Example,
    string ChallengerText,
    string BaselineText);

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
    bool Retryable = false);

internal interface ILegendConnectModelInferenceTransport
{
    Task<LegendModelEvaluationGenerationResult> GenerateAsync(
        string model,
        LegendModelTaskRequest task,
        CancellationToken cancellationToken = default);
}

internal interface ILegendConnectModelEvaluationBackend
{
    Task<LegendModelEvaluationGenerationResult> GenerateAsync(
        string model,
        LegendConnectTrainingDatasetExample example,
        CancellationToken cancellationToken = default);

    Task<LegendModelEvaluationJudgement> JudgeAsync(
        LegendModelEvaluationJudgeRequest request,
        CancellationToken cancellationToken = default);
}

internal interface ILegendConnectCurrentProductionBaseline
{
    Task<LegendCurrentProductionEvaluationResult> TranslateAsync(
        LegendConnectTrainingDatasetExample example,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Evaluation calls the existing production translation authority rather than
/// reproducing its exact-memory, structural, contextual, or Azure precedence.
/// The current router therefore remains the one production-capability baseline.
/// </summary>
internal sealed class LegendConnectCurrentProductionBaseline
    : ILegendConnectCurrentProductionBaseline
{
    private readonly ITranslationService _translation;

    public LegendConnectCurrentProductionBaseline(
        ITranslationService translation)
    {
        _translation = translation;
    }

    public async Task<LegendCurrentProductionEvaluationResult> TranslateAsync(
        LegendConnectTrainingDatasetExample example,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result =
                await _translation.TranslateAsync(
                    example.SourceText,
                    example.TargetLanguageCode,
                    example.SourceLanguageCode,
                    cancellationToken);

            return new(
                result.Succeeded &&
                !string.IsNullOrWhiteSpace(
                    result.TranslatedText),
                result.TranslatedText,
                result.Provider,
                result.ErrorCode);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(
                false,
                null,
                null,
                "model_evaluation_baseline_failed");
        }
    }
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
        if (!TryGetConfiguration(
                out var endpoint,
                out var key) ||
            string.IsNullOrWhiteSpace(model) ||
            model.Length > 200 ||
            string.IsNullOrWhiteSpace(task.CapabilityKey) ||
            string.IsNullOrWhiteSpace(task.Instructions) ||
            string.IsNullOrWhiteSpace(task.Input) ||
            string.IsNullOrWhiteSpace(task.OutputContract))
        {
            return new(
                false,
                null,
                "model_inference_provider_unavailable");
        }

        return await SendTextAsync(
            endpoint,
            key,
            model,
            task.Instructions,
            task.Input,
            cancellationToken);
    }

    private async Task<LegendModelEvaluationGenerationResult> SendTextAsync(
        Uri endpoint,
        string key,
        string model,
        string instructions,
        string input,
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
                    instructions,
                    input
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
                    false);
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
    "reason_codes"
  ]
}
""";

    private const string JudgeInstructions = """
You are the independent adversarial model evaluator inside LEGEND.

The reference target is governed LEGEND evidence and outranks both candidate outputs.

Evaluate the challenger and the current production baseline against:
- translation accuracy;
- semantic preservation;
- context and tone;
- discourse function;
- morphology and grammatical realization;
- unseen composition/generalization;
- hallucination;
- inappropriate refusal.

Rules:
- Founder/HumanVerified reference behavior is protected.
- Never grant authority to the challenger.
- A fluent answer that changes meaning is a regression.
- Extra unsupported claims are hallucination.
- A refusal on an ordinary translation request is a regression.
- A score of 1 means fully faithful to the governed reference.
- blocking_regression must be true when the challenger violates a protected
  meaning, drops material content, invents content, refuses improperly, or is
  materially worse than the production baseline.
- Do not infer that the reference is wrong.
- Return only the required structured object.
""";

    private readonly ILegendConnectModelInferenceTransport _inference;
    private readonly IHttpClientFactory _clients;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenAiLegendConnectModelEvaluationBackend> _logger;

    public OpenAiLegendConnectModelEvaluationBackend(
        ILegendConnectModelInferenceTransport inference,
        IHttpClientFactory clients,
        IConfiguration configuration,
        ILogger<OpenAiLegendConnectModelEvaluationBackend> logger)
    {
        _inference = inference;
        _clients = clients;
        _configuration = configuration;
        _logger = logger;
    }

    public Task<LegendModelEvaluationGenerationResult> GenerateAsync(
        string model,
        LegendConnectTrainingDatasetExample example,
        CancellationToken cancellationToken = default) =>
        _inference.GenerateAsync(
            model,
            example.ToTaskRequest(),
            cancellationToken);

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
                    challenger_output =
                        request.ChallengerText,
                    current_production_output =
                        request.BaselineText
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
                outputText);
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
        string json)
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
                reasonCodes);
        }
        catch (JsonException)
        {
            return Failure(
                "model_evaluation_invalid_judge_response");
        }
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
    private readonly ILegendConnectCurrentProductionBaseline _baseline;
    private readonly IConfiguration _configuration;

    internal LegendConnectModelEvaluationService(
        MasterAppDbContext db,
        LegendConnectTrainingDatasetCompiler compiler,
        ILegendConnectModelEvaluationBackend backend,
        ILegendConnectCurrentProductionBaseline baseline,
        IConfiguration configuration)
    {
        _db = db;
        _compiler = compiler;
        _backend = backend;
        _baseline = baseline;
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

        var selected =
            SelectHeldOut(
                manifest);

        if (selected.Any(item =>
                !string.Equals(
                    item.CapabilityKey,
                    LegendModelCapabilityKeys.Translation,
                    StringComparison.Ordinal)))
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
        decimal baselineWeighted = 0m;
        decimal safeWeighted = 0m;
        decimal totalWeight = 0m;

        var blockingCount = 0;
        var protectedFailureCount = 0;
        var leakageCount = 0;

        foreach (var example in selected)
        {
            var challenger =
                await _backend.GenerateAsync(
                    run.ChallengerModelVersion,
                    example,
                    cancellationToken);

            if (!challenger.Succeeded ||
                string.IsNullOrWhiteSpace(
                    challenger.Text))
            {
                await RecordInfrastructureFailureAsync(
                    run,
                    challenger.ErrorCode ??
                        "model_evaluation_challenger_failed",
                    challenger.Retryable,
                    cancellationToken);
                return;
            }

            var baseline =
                await _baseline.TranslateAsync(
                    example,
                    cancellationToken);

            if (!baseline.Succeeded ||
                string.IsNullOrWhiteSpace(
                    baseline.Text))
            {
                await RecordInfrastructureFailureAsync(
                    run,
                    baseline.ErrorCode ??
                        "model_evaluation_baseline_failed",
                    retryable: true,
                    cancellationToken);
                return;
            }

            var judgement =
                await _backend.JudgeAsync(
                    new(
                        example,
                        challenger.Text,
                        baseline.Text),
                    cancellationToken);

            if (!judgement.Succeeded)
            {
                await RecordInfrastructureFailureAsync(
                    run,
                    judgement.ErrorCode ??
                        "model_evaluation_judge_failed",
                    judgement.Retryable,
                    cancellationToken);
                return;
            }

            var leakage =
                HasMemorizationLeakage(
                    challenger.Text,
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

            var protectedFailure =
                protectedExample &&
                (
                    judgement.TranslationAccuracy <
                        protectedFloor ||
                    judgement.SemanticPreservation <
                        protectedFloor ||
                    judgement.ContextPreservation <
                        protectedFloor ||
                    judgement.DiscoursePreservation <
                        protectedFloor ||
                    judgement.MorphologyPreservation <
                        protectedFloor ||
                    judgement.Hallucination ||
                    judgement.Refusal ||
                    judgement.BlockingRegression ||
                    leakage
                );

            if (protectedFailure)
                protectedFailureCount++;

            var blocking =
                judgement.BlockingRegression ||
                judgement.Hallucination ||
                judgement.Refusal ||
                leakage;

            if (blocking)
                blockingCount++;

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

            baselineWeighted +=
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

        var baselineScore =
            Decimal.Round(
                baselineWeighted /
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

        var beatsBaseline =
            heldOutScore >
                baselineScore ||
            (heldOutScore == 1m &&
             baselineScore == 1m);

        var passed =
            blockingCount == 0 &&
            protectedFailureCount == 0 &&
            leakageCount == 0 &&
            heldOutScore >=
                minimumHeldOut &&
            regressionScore >=
                minimumRegression &&
            beatsBaseline;

        if (!passed)
        {
            await RejectAsync(
                run,
                "model_evaluation_regression",
                heldOutScore,
                regressionScore,
                $"evaluated={selected.Count};baseline={baselineScore:F6};blocking={blockingCount};protected={protectedFailureCount};leakage={leakageCount}",
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
            $"evaluated={selected.Count};baseline={baselineScore:F6};blocking=0;protected=0;leakage=0";

        run.LeaseExpiresUtc =
            null;

        run.UpdatedUtc =
            DateTime.UtcNow;

        // Phase 8 deliberately stops here.
        // PromotionState and ActiveModelVersion are untouched.

        await _db.SaveChangesAsync(
            cancellationToken);
    }

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
