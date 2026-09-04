using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Infrastructure.Messaging;

/// <summary>
/// Server-side provider router. Azure remains the only production provider in
/// this milestone; the router is the stable insertion point for an evaluated
/// future Legend model without any mobile contract change.
/// </summary>
internal sealed record LegendConnectActiveModelInferenceResult(
    bool Succeeded,
    string? Text,
    string? ModelVersion,
    string? ErrorCode,
    Guid? ModelTrainingRunId = null,
    long? CostMicrounits = null,
    bool Retryable = false);

internal static class LegendConnectServingEvaluationContracts
{
    internal const string RuntimeMode =
        "LockedHeldOutEvaluation";

    internal const string ResponseAuthority =
        "LegendConnectActiveModelInference";

    internal const string InferenceSettings =
        "responses-v1,store=false,max_output_tokens=1200";

    internal const string SuccessCriteria =
        "governed-reference-policy-v1";
}

internal sealed record LegendConnectLockedServingEvaluationRequest(
    Guid ModelTrainingRunId,
    string ExpectedModelVersion,
    string DatasetIdentity,
    int DatasetEvaluatorVersion,
    string PromptSetVersion,
    string CodeSha,
    string SuccessCriteria,
    LegendConnectTrainingDatasetExample Example);

/// <summary>
/// Immutable proof that one locked held-out case ran through the same model
/// authority used by production translation and governed-reasoning serving.
/// The receipt is evaluation evidence only and grants no promotion authority.
/// </summary>
internal sealed record LegendConnectLockedServingEvaluationResult(
    bool Succeeded,
    string? Text,
    string? ModelVersion,
    Guid? ModelTrainingRunId,
    string RuntimeMode,
    string ResponseAuthority,
    string PromptSetVersion,
    string CodeSha,
    string InferenceSettings,
    string EvidenceIdentity,
    string ConfigurationIdentity,
    string ProofLineageIdentity,
    string SuccessCriteria,
    long LatencyMicroseconds,
    long? CostMicrounits,
    string? ErrorCode = null,
    bool Retryable = false,
    LegendConnectResearchEvaluationMeasurements? ResearchMeasurements = null);

internal sealed record LegendConnectGovernedReasoningCandidateRequest(
    string SourceLanguageCode,
    string FounderInput,
    string AuthorizedSymbolicText,
    int EvidenceCount,
    string EvidenceStandard,
    string ArticulationMode);

internal interface ILegendConnectActiveModelInference
{
    Task<LegendConnectActiveModelInferenceResult> TryTranslateAsync(
        string sourceLanguageCode,
        string targetLanguageCode,
        string text,
        CancellationToken cancellationToken = default);

    Task<LegendConnectActiveModelInferenceResult>
        TryGenerateGovernedReasoningCandidateAsync(
            LegendConnectGovernedReasoningCandidateRequest request,
            CancellationToken cancellationToken = default);

    Task<LegendConnectLockedServingEvaluationResult>
        EvaluateLockedCaseAsync(
            LegendConnectLockedServingEvaluationRequest request,
            CancellationToken cancellationToken = default);
}

internal sealed class LegendConnectActiveModelInference
    : ILegendConnectActiveModelInference
{
    private readonly MasterAppDbContext _db;
    private readonly ILegendConnectModelInferenceTransport _transport;

    public LegendConnectActiveModelInference(
        MasterAppDbContext db,
        ILegendConnectModelInferenceTransport transport)
    {
        _db = db;
        _transport = transport;
    }

    public async Task<LegendConnectActiveModelInferenceResult> TryTranslateAsync(
        string sourceLanguageCode,
        string targetLanguageCode,
        string text,
        CancellationToken cancellationToken = default)
    {
        var pairKey =
            LegendLanguageIdentity.PairKey(
                sourceLanguageCode,
                targetLanguageCode);

        var pair =
            await _db.Set<Domain.Entities.LegendLanguagePair>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item =>
                        item.PairKey == pairKey &&
                        item.IsEnabled,
                    cancellationToken);

        if (pair is null ||
            string.IsNullOrWhiteSpace(
                pair.ActiveModelVersion))
        {
            return new(
                false,
                null,
                null,
                "active_model_unavailable");
        }

        var activeRun =
            await (
                from lineage in _db.Set<LegendConnectModelPromotionPair>()
                    .AsNoTracking()
                join run in _db.Set<LegendConnectModelTrainingRun>()
                        .AsNoTracking()
                    on lineage.ModelTrainingRunId equals run.Id
                where lineage.PairKey == pairKey &&
                      lineage.RolledBackUtc == null &&
                      lineage.PromotedModelVersion ==
                          pair.ActiveModelVersion &&
                      run.ChallengerModelVersion ==
                          pair.ActiveModelVersion &&
                      run.State == "TrainingCompleted" &&
                      run.EvaluationState == "Passed" &&
                      run.PromotionState == "Promoted" &&
                      run.FailureDetail != null &&
                      run.FailureDetail.Contains(
                          "runtime_mode=LockedHeldOutEvaluation") &&
                      run.FailureDetail.Contains(
                          "response_authority=LegendConnectActiveModelInference")
                orderby run.Generation descending,
                    run.PromotedUtc descending
                select new
                {
                    run.Id,
                    run.FailureDetail
                })
            .FirstOrDefaultAsync(
                cancellationToken);

        if (activeRun is null ||
            !LegendConnectModelRuntimeProofSummary.IsValid(
                activeRun.FailureDetail))
        {
            return new(
                false,
                null,
                null,
                "active_model_runtime_proof_unavailable");
        }

        var result =
            await _transport.GenerateAsync(
                pair.ActiveModelVersion,
                LegendModelTaskRequest.Translation(
                    sourceLanguageCode,
                    targetLanguageCode,
                    text),
                cancellationToken);

        if (!result.Succeeded ||
            string.IsNullOrWhiteSpace(
                result.Text))
        {
            return new(
                false,
                null,
                pair.ActiveModelVersion,
                result.ErrorCode ??
                    "active_model_inference_failed",
                activeRun.Id,
                CostMicrounits:
                    result.CostMicrounits,
                Retryable:
                    result.Retryable);
        }

        return new(
            true,
            result.Text,
            pair.ActiveModelVersion,
            null,
            activeRun.Id,
            CostMicrounits:
                result.CostMicrounits);
    }

    public async Task<LegendConnectActiveModelInferenceResult>
        TryGenerateGovernedReasoningCandidateAsync(
            LegendConnectGovernedReasoningCandidateRequest request,
            CancellationToken cancellationToken = default)
    {
        var scopeKey =
            $"capability:{LegendModelCapabilityKeys.GovernedReasoning}";
        var promoted =
            await _db.Set<LegendConnectModelTrainingRun>()
                .AsNoTracking()
                .Where(item =>
                    item.ScopeKey == scopeKey &&
                    item.State == "TrainingCompleted" &&
                    item.EvaluationState == "Passed" &&
                    item.PromotionState == "Promoted" &&
                    item.TrainingProvider == "OpenAI" &&
                    item.CompletedUtc != null &&
                    item.PromotedUtc != null &&
                    item.HeldOutScore != null &&
                    item.RegressionScore != null &&
                    item.FailureCode == null &&
                    item.FailureDetail != null &&
                    item.FailureDetail.Contains(
                        "runtime_mode=LockedHeldOutEvaluation") &&
                    item.FailureDetail.Contains(
                        "response_authority=LegendConnectActiveModelInference") &&
                    item.DatasetIdentity != "" &&
                    item.ChallengerModelVersion != null &&
                    item.ChallengerModelVersion != "")
                .OrderByDescending(item => item.Generation)
                .ThenByDescending(item => item.PromotedUtc)
                .Select(item => new
                {
                    item.Id,
                    item.ChallengerModelVersion,
                    item.FailureDetail
                })
                .FirstOrDefaultAsync(cancellationToken);

        if (promoted is null ||
            !LegendConnectModelRuntimeProofSummary.IsValid(
                promoted.FailureDetail))
        {
            return new(
                false,
                null,
                null,
                "active_reasoning_model_unavailable");
        }

        var result =
            await _transport.GenerateAsync(
                promoted.ChallengerModelVersion!,
                LegendModelTaskRequest.GovernedReasoningRealization(
                    request.SourceLanguageCode,
                    request.FounderInput,
                    request.AuthorizedSymbolicText,
                    request.EvidenceCount,
                    request.EvidenceStandard,
                    request.ArticulationMode),
                cancellationToken);

        if (!result.Succeeded)
        {
            return new(
                false,
                null,
                promoted.ChallengerModelVersion,
                result.ErrorCode ??
                    "active_reasoning_model_inference_failed",
                promoted.Id,
                result.CostMicrounits,
                result.Retryable);
        }

        var candidate = result.Text?.Trim();
        if (string.IsNullOrWhiteSpace(candidate) ||
            candidate.Length > 2000 ||
            candidate.Any(character =>
                char.IsControl(character) &&
                character is not '\n' and not '\r' and not '\t'))
        {
            return new(
                false,
                null,
                promoted.ChallengerModelVersion,
                "active_reasoning_model_malformed_output",
                promoted.Id,
                result.CostMicrounits);
        }

        return new(
            true,
            candidate,
            promoted.ChallengerModelVersion,
            null,
            promoted.Id,
            result.CostMicrounits);
    }

    public async Task<LegendConnectLockedServingEvaluationResult>
        EvaluateLockedCaseAsync(
            LegendConnectLockedServingEvaluationRequest request,
            CancellationToken cancellationToken = default)
    {
        var task =
            request.Example.ToTaskRequest();
        var configurationIdentity =
            StableHash(
                "legend-serving-configuration-v1",
                request.ExpectedModelVersion,
                request.PromptSetVersion,
                request.CodeSha,
                request.SuccessCriteria,
                LegendConnectServingEvaluationContracts
                    .InferenceSettings,
                task.CapabilityKey,
                task.Instructions,
                task.Input,
                task.OutputContract,
                task.SourceLanguageCode ?? string.Empty,
                task.TargetLanguageCode ?? string.Empty);
        var proofLineageIdentity =
            StableHash(
                "legend-locked-serving-proof-v1",
                request.ModelTrainingRunId.ToString("N"),
                request.ExpectedModelVersion,
                request.DatasetIdentity,
                request.DatasetEvaluatorVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                request.PromptSetVersion,
                request.CodeSha,
                request.SuccessCriteria,
                request.Example.EvidenceIdentity,
                request.Example.SplitGroupIdentity,
                request.Example.SourceTextHash,
                request.Example.TargetTextHash,
                configurationIdentity);

        if (request.ModelTrainingRunId == Guid.Empty ||
            string.IsNullOrWhiteSpace(
                request.ExpectedModelVersion) ||
            !IsLowerHex(
                request.DatasetIdentity,
                64) ||
            request.DatasetEvaluatorVersion <= 0 ||
            request.PromptSetVersion.Length is <= 0 or > 120 ||
            request.SuccessCriteria.Length is <= 0 or > 240 ||
            !IsLowerHex(
                request.CodeSha,
                40))
        {
            return Failure(
                request,
                configurationIdentity,
                proofLineageIdentity,
                "model_evaluation_runtime_configuration_invalid");
        }

        var run =
            await _db.Set<LegendConnectModelTrainingRun>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item =>
                        item.Id ==
                        request.ModelTrainingRunId,
                    cancellationToken);

        if (run is null ||
            run.State != "TrainingCompleted" ||
            run.TrainingProvider != "OpenAI" ||
            string.IsNullOrWhiteSpace(
                run.ChallengerModelVersion) ||
            !string.Equals(
                run.ChallengerModelVersion,
                request.ExpectedModelVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                run.DatasetIdentity,
                request.DatasetIdentity,
                StringComparison.Ordinal) ||
            run.DatasetEvaluatorVersion !=
                request.DatasetEvaluatorVersion ||
            !ScopeAdmits(
                run.ScopeKey,
                request.Example))
        {
            return Failure(
                request,
                configurationIdentity,
                proofLineageIdentity,
                "model_evaluation_inactive_model");
        }

        var started =
            Stopwatch.GetTimestamp();
        var generated =
            await _transport.GenerateAsync(
                run.ChallengerModelVersion,
                task,
                cancellationToken);
        var latencyMicroseconds =
            ElapsedMicroseconds(
                started);

        if (!generated.Succeeded ||
            string.IsNullOrWhiteSpace(
                generated.Text))
        {
            return Failure(
                request,
                configurationIdentity,
                proofLineageIdentity,
                generated.ErrorCode ??
                    "model_evaluation_runtime_failed",
                latencyMicroseconds,
                generated.CostMicrounits,
                generated.Retryable,
                run.ChallengerModelVersion,
                run.Id);
        }

        if (generated.CostMicrounits is null or < 0)
        {
            return Failure(
                request,
                configurationIdentity,
                proofLineageIdentity,
                "model_evaluation_runtime_cost_unavailable",
                latencyMicroseconds,
                generated.CostMicrounits,
                modelVersion:
                    run.ChallengerModelVersion,
                modelTrainingRunId:
                    run.Id);
        }

        return new(
            true,
            generated.Text,
            run.ChallengerModelVersion,
            run.Id,
            LegendConnectServingEvaluationContracts
                .RuntimeMode,
            LegendConnectServingEvaluationContracts
                .ResponseAuthority,
            request.PromptSetVersion,
            request.CodeSha,
            LegendConnectServingEvaluationContracts
                .InferenceSettings,
            request.Example.EvidenceIdentity,
            configurationIdentity,
            proofLineageIdentity,
            request.SuccessCriteria,
            latencyMicroseconds,
            generated.CostMicrounits);
    }

    private static LegendConnectLockedServingEvaluationResult Failure(
        LegendConnectLockedServingEvaluationRequest request,
        string configurationIdentity,
        string proofLineageIdentity,
        string errorCode,
        long latencyMicroseconds = 0,
        long? costMicrounits = null,
        bool retryable = false,
        string? modelVersion = null,
        Guid? modelTrainingRunId = null) =>
        new(
            false,
            null,
            modelVersion,
            modelTrainingRunId,
            LegendConnectServingEvaluationContracts
                .RuntimeMode,
            LegendConnectServingEvaluationContracts
                .ResponseAuthority,
            request.PromptSetVersion,
            request.CodeSha,
            LegendConnectServingEvaluationContracts
                .InferenceSettings,
            request.Example.EvidenceIdentity,
            configurationIdentity,
            proofLineageIdentity,
            request.SuccessCriteria,
            latencyMicroseconds,
            costMicrounits,
            errorCode,
            retryable);

    private static bool ScopeAdmits(
        string scopeKey,
        LegendConnectTrainingDatasetExample example) =>
        string.Equals(
            scopeKey,
            "Global",
            StringComparison.Ordinal) ||
        string.Equals(
            scopeKey,
            example.PairKey,
            StringComparison.Ordinal) ||
        string.Equals(
            scopeKey,
            $"capability:{example.CapabilityKey}",
            StringComparison.Ordinal);

    private static long ElapsedMicroseconds(
        long startedTimestamp)
    {
        var elapsed =
            Stopwatch.GetElapsedTime(
                startedTimestamp);
        var microseconds =
            decimal.Round(
                (decimal)elapsed.TotalMilliseconds *
                1000m,
                0,
                MidpointRounding.AwayFromZero);
        return microseconds > long.MaxValue
            ? long.MaxValue
            : Math.Max(
                0,
                (long)microseconds);
    }

    private static string StableHash(
        params string[] values) =>
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
}

internal sealed class LegendConnectTranslationRouter : IAccountScopedTranslationService, IRetainedTranslationService
{
    private readonly ITranslationProvider _azure;
    private readonly ILegendLanguageRegistry _languages;
    private readonly ITranslationCapacityAuthority _capacity;
    private readonly ITranslationDemandRecorder? _demand;
    private readonly ITranslationSystemUsageRecorder? _systemUsage;
    private readonly ILegendConnectTranslationIntelligence? _intelligence;
    private readonly ILegendConnectOperationalEventWriter? _operations;
    private readonly ITranslationEntitlementAuthority? _entitlements;
    private readonly ILegendConnectRuntimePolicyAuthority? _runtimePolicy;
    private readonly ILegendConnectStructuralCompositionGate? _structuralComposition;
    private readonly ILegendConnectActiveModelInference? _activeModelInference;
    private readonly ITranslationRequestCoalescer _coalescer;
    private readonly ILogger<LegendConnectTranslationRouter> _logger;

    public LegendConnectTranslationRouter(
        ITranslationProvider azure,
        ILegendLanguageRegistry languages,
        ITranslationCapacityAuthority capacity,
        ILogger<LegendConnectTranslationRouter> logger,
        ITranslationDemandRecorder? demand = null,
        ITranslationSystemUsageRecorder? systemUsage = null,
        ILegendConnectTranslationIntelligence? intelligence = null,
        ILegendConnectOperationalEventWriter? operations = null,
        ITranslationEntitlementAuthority? entitlements = null,
        ILegendConnectRuntimePolicyAuthority? runtimePolicy = null,
        ILegendConnectStructuralCompositionGate? structuralComposition = null,
        ILegendConnectActiveModelInference? activeModelInference = null,
        ITranslationRequestCoalescer? coalescer = null)
    {
        _azure = azure;
        _languages = languages;
        _capacity = capacity;
        _logger = logger;
        _demand = demand;
        _systemUsage = systemUsage;
        _intelligence = intelligence;
        _operations = operations;
        _entitlements = entitlements;
        _runtimePolicy = runtimePolicy;
        _structuralComposition = structuralComposition;
        _activeModelInference = activeModelInference;
        _coalescer = coalescer ?? new TranslationRequestCoalescer();
    }

    public Task<TranslationDetectionResult> DetectLanguageAsync(
        string text,
        CancellationToken cancellationToken = default) =>
        DetectLanguageAsync(text, cancellationToken, null);

    public async Task<TranslationDetectionResult> DetectLanguageAsync(
        string text,
        CancellationToken cancellationToken,
        LegendConnectExternalProviderPolicy? providerPolicy)
    {
        var policy = LegendConnectExternalProviderPolicy.Resolve(providerPolicy);
        if (_structuralComposition is not null &&
            !string.IsNullOrWhiteSpace(LegendLanguageIdentity.NormalizeText(text)))
        {
            var governedMatches = new List<string>();
            var languages = await _languages
                .ListEnabledTranslationLanguagesReadOnlyAsync(
                    cancellationToken);
            foreach (var candidate in languages)
            {
                var understanding = await _structuralComposition
                    .AnalyzeShadowSourceSemanticsAsync(
                        candidate.Code,
                        text,
                        cancellationToken);
                if (understanding.State ==
                        LegendShadowSourceUnderstanding
                            .SupportedForShadowEvaluation &&
                    understanding.Components.Count > 0)
                {
                    governedMatches.Add(candidate.Code);
                    if (governedMatches.Count > 1)
                        break;
                }
            }

            if (governedMatches.Count == 1)
            {
                return new TranslationDetectionResult(
                    true,
                    governedMatches[0],
                    Confidence: 1m);
            }
        }

        // Governed structural composition above is the only identification
        // authority a native-only request may use. When it cannot name exactly
        // one governed language the request fails closed here: the external
        // detection provider is not consulted and no client is constructed.
        if (policy.ForbidsExternalProviders)
        {
            return new TranslationDetectionResult(
                false,
                null,
                "native_only_governed_source_language_undetermined");
        }

        var result = await _azure.DetectLanguageAsync(
            text,
            cancellationToken,
            policy);
        if (!result.Succeeded)
            return result;

        var language = await _languages.NormalizeEnabledTranslationLanguageAsync(result.Language, cancellationToken);
        return language is null
            ? new TranslationDetectionResult(false, null, "translation_language_unsupported")
            : new TranslationDetectionResult(
                true,
                language,
                Confidence: result.Confidence);
    }

    public Task<TranslationProviderResult> TranslateAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default) =>
        TranslateAsync(text, targetLanguage, sourceLanguage, cancellationToken, null);

    public async Task<TranslationProviderResult> TranslateAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage,
        CancellationToken cancellationToken,
        LegendConnectExternalProviderPolicy? providerPolicy)
    {
        // Native-only forbids the external boundary, not Legend's own
        // translation authority. The policy is carried into the core so every
        // internal stage - same-language, trusted exact memory, structural
        // composition, contextual composition and reusable governed
        // observation - still runs, and only the external model and the
        // quota/capacity/Azure fallback are refused.
        return await TranslateCoreAsync(
            text,
            targetLanguage,
            sourceLanguage,
            account: null,
            requestReference: null,
            cancellationToken,
            providerPolicy: providerPolicy);
    }

    public async Task<TranslationProviderResult> TranslateForAccountAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage,
        MessagingActor account,
        string requestReference,
        CancellationToken cancellationToken = default)
        => await TranslateCoreAsync(
            text,
            targetLanguage,
            sourceLanguage,
            account,
            requestReference,
            cancellationToken);

    public async Task<RetainedTranslationResult> TranslateRetainedAsync(
        RetainedTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        var source = await _languages.NormalizeEnabledTranslationLanguageAsync(
            request.SourceLanguageCode,
            cancellationToken);
        var target = await _languages.NormalizeEnabledTranslationLanguageAsync(
            request.TargetLanguageCode,
            cancellationToken);
        var validationError = ValidateRetainedRequest(request, source, target);
        if (validationError is not null)
        {
            ApplicationLocalizationTelemetry.Failure(validationError, source, target);
            return RetainedFailure(request, source, target, validationError);
        }

        source ??= request.SourceLanguageCode;
        target ??= request.TargetLanguageCode;
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            ApplicationLocalizationTelemetry.SameLanguage(source);
            return new RetainedTranslationResult(
                true,
                request.SourceText,
                source,
                target,
                "LegendConnectSameLanguage",
                "Source",
                "Source",
                DateTime.UtcNow,
                Reused: true);
        }

        if (_intelligence is null)
            return RetainedFailure(request, source, target, "translation_memory_unavailable");

        var identity = RetainedIdentity(request, source, target, _azure.ProviderName, _azure.ProviderVersion);
        var trusted = await _intelligence.TryGetTrustedScopedMemoryAsync(
            source,
            target,
            request.SourceText,
            request.StableSourceContentId.Trim(),
            request.SourceRevision.Trim(),
            request.TranslationContext.Trim(),
            Hash(request.PlaceholderContract),
            request.ReuseScope,
            request.ScopeIdentityHash,
            cancellationToken);
        if (trusted is not null && TranslationOutputValidator.IsValid(
                request.SourceText,
                trusted.Text,
                request.PlaceholderContract))
        {
            ApplicationLocalizationTelemetry.ApprovedMemoryHit(source, target);
            return new RetainedTranslationResult(
                true,
                trusted.Text,
                source,
                target,
                "LegendConnectTranslationMemory",
                trusted.Provenance,
                trusted.QualityState,
                DateTime.UtcNow,
                Reused: true);
        }

        var retained = await _intelligence.TryGetRetainedTranslationAsync(identity, cancellationToken);
        if (retained is not null && TranslationOutputValidator.IsValid(
                request.SourceText,
                retained.Text,
                request.PlaceholderContract))
        {
            ApplicationLocalizationTelemetry.RetainedHit(source, target);
            return ToRetainedResult(retained, source, target, reused: true);
        }
        if (retained is not null)
        {
            await _intelligence.InvalidateRetainedTranslationAsync(identity, cancellationToken);
            ApplicationLocalizationTelemetry.Failure("translation_memory_invalid", source, target);
        }

        ApplicationLocalizationTelemetry.Miss(source, target);
        var coalesced = await _coalescer.ExecuteAsync(identity, async () =>
        {
            var afterFence = await _intelligence.TryGetRetainedTranslationAsync(identity, cancellationToken);
            if (afterFence is not null && TranslationOutputValidator.IsValid(
                    request.SourceText,
                    afterFence.Text,
                    request.PlaceholderContract))
            {
                return ToRetainedResult(afterFence, source, target, reused: true);
            }
            if (afterFence is not null)
                await _intelligence.InvalidateRetainedTranslationAsync(identity, cancellationToken);

            var reservationReference = $"retained:{identity}:{DateTime.UtcNow:yyyyMMddHHmm}";
            var protectedSource = TranslationOutputValidator.ProtectNonTranslatableBrands(
                request.SourceText,
                request.PlaceholderContract);
            var result = await TranslateCoreAsync(
                protectedSource.Text,
                target,
                source,
                account: null,
                requestReference: reservationReference,
                cancellationToken,
                allowProviderObservationReuse: false,
                allowLegacyIntelligence: false);
            if (string.Equals(result.Provider, _azure.ProviderName, StringComparison.Ordinal))
            {
                ApplicationLocalizationTelemetry.ProviderOperation(
                    source,
                    target,
                    protectedSource.Text.Length,
                    result.Succeeded && !string.IsNullOrWhiteSpace(result.TranslatedText));
            }

            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.TranslatedText))
            {
                if (result.ErrorCode == "translation_capacity_unavailable")
                {
                    var concurrent = await WaitForRetainedTranslationAsync(identity, cancellationToken);
                    if (concurrent is not null && TranslationOutputValidator.IsValid(
                            request.SourceText,
                            concurrent.Text,
                            request.PlaceholderContract))
                        return ToRetainedResult(concurrent, source, target, reused: true);
                }

                ApplicationLocalizationTelemetry.Failure(
                    result.ErrorCode ?? "translation_provider_failed",
                    source,
                    target);
                return RetainedFailure(
                    request,
                    source,
                    target,
                    result.ErrorCode ?? "translation_provider_failed");
            }

            if (!TranslationOutputValidator.IsValid(
                    protectedSource.Text,
                    result.TranslatedText,
                    protectedSource.PlaceholderContract))
            {
                ApplicationLocalizationTelemetry.Failure(
                    "translation_output_invalid",
                    source,
                    target);
                return RetainedFailure(request, source, target, "translation_output_invalid");
            }

            var restoredTranslation = protectedSource.Restore(result.TranslatedText);
            if (!TranslationOutputValidator.IsValid(
                    request.SourceText,
                    restoredTranslation,
                    request.PlaceholderContract))
            {
                ApplicationLocalizationTelemetry.Failure(
                    "translation_output_invalid",
                    source,
                    target);
                return RetainedFailure(request, source, target, "translation_output_invalid");
            }
            result = result with { TranslatedText = restoredTranslation };

            if (!string.Equals(result.Provider, _azure.ProviderName, StringComparison.Ordinal))
            {
                return new RetainedTranslationResult(
                    true,
                    result.TranslatedText,
                    source,
                    target,
                    result.Provider,
                    "LegendHeld",
                    "Approved",
                    DateTime.UtcNow,
                    Reused: true);
            }

            var stored = await _intelligence.RetainProviderTranslationAsync(
                new LegendRetainedTranslationWrite(
                    identity,
                    request.StableSourceContentId.Trim(),
                    request.SourceText,
                    result.TranslatedText,
                    source,
                    target,
                    request.SourceRevision.Trim(),
                    request.TranslationContext.Trim(),
                    Hash(request.PlaceholderContract),
                    request.ReuseScope,
                    request.ScopeIdentityHash,
                    result.Provider,
                    _azure.ProviderVersion),
                cancellationToken);
            ApplicationLocalizationTelemetry.ProviderPersisted(source, target);
            return ToRetainedResult(stored, source, target, reused: false);
        });

        if (coalesced.JoinedExistingRequest)
            ApplicationLocalizationTelemetry.Coalesced(source, target);
        return coalesced.Result;
    }

    public async Task<IReadOnlyList<RetainedTranslationResult>> TranslateRetainedBatchAsync(
        IReadOnlyList<RetainedTranslationRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0)
            return Array.Empty<RetainedTranslationResult>();

        var first = requests[0];
        if (requests.Any(request =>
                !string.Equals(request.SourceLanguageCode, first.SourceLanguageCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(request.TargetLanguageCode, first.TargetLanguageCode, StringComparison.OrdinalIgnoreCase)))
        {
            var individual = new List<RetainedTranslationResult>(requests.Count);
            foreach (var request in requests)
                individual.Add(await TranslateRetainedAsync(request, cancellationToken));
            return individual;
        }

        var source = await _languages.NormalizeEnabledTranslationLanguageAsync(
            first.SourceLanguageCode,
            cancellationToken);
        var target = await _languages.NormalizeEnabledTranslationLanguageAsync(
            first.TargetLanguageCode,
            cancellationToken);
        if (source is null || target is null || requests.Any(request =>
                ValidateRetainedRequest(request, source, target) is not null))
        {
            var invalid = new List<RetainedTranslationResult>(requests.Count);
            foreach (var request in requests)
                invalid.Add(await TranslateRetainedAsync(request, cancellationToken));
            return invalid;
        }

        var identities = requests.Select(request => RetainedIdentity(
            request,
            source,
            target,
            _azure.ProviderName,
            _azure.ProviderVersion)).ToArray();
        var batchIdentity = "retained-batch:" + Hash(string.Join('\n', identities.Order(StringComparer.Ordinal)));
        var coalesced = await _coalescer.ExecuteAsync(batchIdentity, () =>
            TranslateRetainedBatchCoreAsync(requests, identities, source, target, cancellationToken));
        if (coalesced.JoinedExistingRequest)
            ApplicationLocalizationTelemetry.Coalesced(source, target);
        return coalesced.Result;
    }

    private async Task<IReadOnlyList<RetainedTranslationResult>> TranslateRetainedBatchCoreAsync(
        IReadOnlyList<RetainedTranslationRequest> requests,
        IReadOnlyList<string> identities,
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        var results = new RetainedTranslationResult?[requests.Count];
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            for (var index = 0; index < requests.Count; index++)
            {
                results[index] = new RetainedTranslationResult(
                    true,
                    requests[index].SourceText,
                    source,
                    target,
                    "LegendConnectSameLanguage",
                    "Source",
                    "Source",
                    DateTime.UtcNow,
                    Reused: true);
            }
            return results.Select(result => result!).ToArray();
        }

        if (_intelligence is null)
            return requests.Select(request =>
                RetainedFailure(request, source, target, "translation_memory_unavailable")).ToArray();

        var groups = identities
            .Select((identity, index) => (identity, index))
            .GroupBy(item => item.identity, StringComparer.Ordinal)
            .ToArray();
        var misses = new List<(string Identity, int RepresentativeIndex, int[] Indices)>();
        var groupedRequests = groups.Select(group =>
        {
            var indices = group.Select(item => item.index).ToArray();
            var representative = indices[0];
            return (Identity: group.Key, RepresentativeIndex: representative, Indices: indices);
        }).ToArray();
        var trustedLookups = groupedRequests.Select(group =>
        {
            var request = requests[group.RepresentativeIndex];
            return new LegendTrustedTranslationLookup(
                group.Identity,
                source,
                target,
                request.SourceText,
                request.StableSourceContentId.Trim(),
                request.SourceRevision.Trim(),
                request.TranslationContext.Trim(),
                Hash(request.PlaceholderContract),
                request.ReuseScope,
                request.ScopeIdentityHash);
        }).ToArray();
        var trustedMatches = await _intelligence.TryGetTrustedScopedMemoriesAsync(
            trustedLookups,
            cancellationToken);
        var unresolved = new List<(string Identity, int RepresentativeIndex, int[] Indices)>();
        foreach (var group in groupedRequests)
        {
            var request = requests[group.RepresentativeIndex];
            trustedMatches.TryGetValue(group.Identity, out var trusted);
            if (trusted is not null && TranslationOutputValidator.IsValid(
                    request.SourceText,
                    trusted.Text,
                    request.PlaceholderContract))
            {
                var resolved = new RetainedTranslationResult(
                    true,
                    trusted.Text,
                    source,
                    target,
                    "LegendConnectTranslationMemory",
                    trusted.Provenance,
                    trusted.QualityState,
                    trusted.CreatedUtc,
                    Reused: true);
                foreach (var index in group.Indices)
                    results[index] = resolved;
                ApplicationLocalizationTelemetry.ApprovedMemoryHit(source, target);
                continue;
            }
            if (trusted is not null)
                ApplicationLocalizationTelemetry.Failure("trusted_translation_invalid", source, target);
            unresolved.Add(group);
        }

        var retainedMatches = await _intelligence.TryGetRetainedTranslationsAsync(
            unresolved.Select(group => group.Identity).ToArray(),
            cancellationToken);
        foreach (var group in unresolved)
        {
            var request = requests[group.RepresentativeIndex];
            retainedMatches.TryGetValue(group.Identity, out var retained);
            if (retained is not null && TranslationOutputValidator.IsValid(
                    request.SourceText,
                    retained.Text,
                    request.PlaceholderContract))
            {
                var resolved = ToRetainedResult(retained, source, target, reused: true);
                foreach (var index in group.Indices)
                    results[index] = resolved;
                ApplicationLocalizationTelemetry.RetainedHit(source, target);
                continue;
            }
            if (retained is not null)
            {
                await _intelligence.InvalidateRetainedTranslationAsync(group.Identity, cancellationToken);
                ApplicationLocalizationTelemetry.Failure("translation_memory_invalid", source, target);
            }

            ApplicationLocalizationTelemetry.Miss(source, target);
            misses.Add(group);
        }

        foreach (var chunk in BatchChunks(misses, requests))
        {
            var protectedSources = chunk.Select(item =>
                TranslationOutputValidator.ProtectNonTranslatableBrands(
                    requests[item.RepresentativeIndex].SourceText,
                    requests[item.RepresentativeIndex].PlaceholderContract)).ToArray();
            var characters = protectedSources.Sum(item => item.Text.Length);
            var chunkIdentity = Hash(string.Join('\n', chunk.Select(item => item.Identity).Order(StringComparer.Ordinal)));
            var reservation = await _capacity.TryReserveAsync(
                _azure.ProviderName,
                characters,
                TranslationCapacityPurpose.Live,
                $"retained-batch:{chunkIdentity}:{DateTime.UtcNow:yyyyMMddHHmm}",
                cancellationToken);
            if (reservation is null)
            {
                await ResolveCrossInstanceBatchAsync(
                    chunk,
                    requests,
                    results,
                    source,
                    target,
                    cancellationToken);
                continue;
            }

            IReadOnlyList<TranslationProviderResult> providerResults;
            var providerExecuted = false;
            var providerSucceeded = false;
            try
            {
                providerExecuted = true;
                providerResults = await _azure.TranslateBatchAsync(
                    protectedSources.Select(item => item.Text).ToArray(),
                    target,
                    source,
                    cancellationToken);
                providerSucceeded = providerResults.Count == chunk.Count &&
                    providerResults.All(result =>
                        result.Succeeded && !string.IsNullOrWhiteSpace(result.TranslatedText));
            }
            finally
            {
                await _capacity.CompleteAsync(reservation, providerExecuted, cancellationToken);
                if (providerExecuted)
                    ApplicationLocalizationTelemetry.ProviderOperation(
                        source,
                        target,
                        characters,
                        providerSucceeded);
            }

            if (providerResults.Count != chunk.Count)
                providerResults = Enumerable.Range(0, chunk.Count)
                    .Select(_ => new TranslationProviderResult(
                        false,
                        null,
                        source,
                        _azure.ProviderName,
                        "translation_provider_failed"))
                    .ToArray();

            var validWrites = new List<(int Offset, LegendRetainedTranslationWrite Write)>();
            for (var offset = 0; offset < chunk.Count; offset++)
            {
                var miss = chunk[offset];
                var request = requests[miss.RepresentativeIndex];
                var provider = providerResults[offset];
                RetainedTranslationResult resolved;
                if (!provider.Succeeded || string.IsNullOrWhiteSpace(provider.TranslatedText))
                {
                    var error = provider.ErrorCode ?? "translation_provider_failed";
                    ApplicationLocalizationTelemetry.Failure(error, source, target);
                    resolved = RetainedFailure(request, source, target, error);
                }
                else if (!TranslationOutputValidator.IsValid(
                             protectedSources[offset].Text,
                             provider.TranslatedText,
                             protectedSources[offset].PlaceholderContract))
                {
                    ApplicationLocalizationTelemetry.Failure(
                        "translation_output_invalid",
                        source,
                        target);
                    resolved = RetainedFailure(
                        request,
                        source,
                        target,
                        "translation_output_invalid");
                }
                else
                {
                    var restoredTranslation = protectedSources[offset].Restore(provider.TranslatedText);
                    if (!TranslationOutputValidator.IsValid(
                            request.SourceText,
                            restoredTranslation,
                            request.PlaceholderContract))
                    {
                        ApplicationLocalizationTelemetry.Failure(
                            "translation_output_invalid",
                            source,
                            target);
                        resolved = RetainedFailure(
                            request,
                            source,
                            target,
                            "translation_output_invalid");
                        foreach (var index in miss.Indices)
                            results[index] = resolved;
                        continue;
                    }
                    validWrites.Add((offset, new LegendRetainedTranslationWrite(
                            miss.Identity,
                            request.StableSourceContentId.Trim(),
                            request.SourceText,
                            restoredTranslation,
                            source,
                            target,
                            request.SourceRevision.Trim(),
                            request.TranslationContext.Trim(),
                            Hash(request.PlaceholderContract),
                            request.ReuseScope,
                            request.ScopeIdentityHash,
                            provider.Provider,
                            _azure.ProviderVersion)));
                    continue;
                }

                foreach (var index in miss.Indices)
                    results[index] = resolved;
            }

            if (validWrites.Count > 0)
            {
                var stored = await _intelligence.RetainProviderTranslationsAsync(
                    validWrites.Select(item => item.Write).ToArray(),
                    cancellationToken);
                for (var index = 0; index < validWrites.Count; index++)
                {
                    var miss = chunk[validWrites[index].Offset];
                    var resolved = ToRetainedResult(stored[index], source, target, reused: false);
                    foreach (var resultIndex in miss.Indices)
                        results[resultIndex] = resolved;
                    ApplicationLocalizationTelemetry.ProviderPersisted(source, target);
                }
            }
        }

        return results.Select((result, index) => result ?? RetainedFailure(
            requests[index],
            source,
            target,
            "translation_provider_failed")).ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<(string Identity, int RepresentativeIndex, int[] Indices)>> BatchChunks(
        IReadOnlyList<(string Identity, int RepresentativeIndex, int[] Indices)> misses,
        IReadOnlyList<RetainedTranslationRequest> requests)
    {
        var chunks = new List<IReadOnlyList<(string Identity, int RepresentativeIndex, int[] Indices)>>();
        var current = new List<(string Identity, int RepresentativeIndex, int[] Indices)>();
        var characters = 0;
        foreach (var miss in misses)
        {
            var length = requests[miss.RepresentativeIndex].SourceText.Length;
            if (current.Count == 100 || characters + length > 50_000)
            {
                chunks.Add(current.ToArray());
                current.Clear();
                characters = 0;
            }
            current.Add(miss);
            characters += length;
        }
        if (current.Count > 0)
            chunks.Add(current.ToArray());
        return chunks;
    }

    private async Task ResolveCrossInstanceBatchAsync(
        IReadOnlyList<(string Identity, int RepresentativeIndex, int[] Indices)> chunk,
        IReadOnlyList<RetainedTranslationRequest> requests,
        RetainedTranslationResult?[] results,
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        var unresolved = chunk.ToDictionary(item => item.Identity, StringComparer.Ordinal);
        for (var attempt = 0; attempt < 14 && unresolved.Count > 0; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            foreach (var identity in unresolved.Keys.ToArray())
            {
                var retained = await _intelligence!.TryGetRetainedTranslationAsync(identity, cancellationToken);
                if (retained is null)
                    continue;
                var miss = unresolved[identity];
                var request = requests[miss.RepresentativeIndex];
                if (!TranslationOutputValidator.IsValid(
                        request.SourceText,
                        retained.Text,
                        request.PlaceholderContract))
                    continue;
                var resolved = ToRetainedResult(retained, source, target, reused: true);
                foreach (var index in miss.Indices)
                    results[index] = resolved;
                unresolved.Remove(identity);
            }
        }

        foreach (var miss in unresolved.Values)
        {
            var failure = RetainedFailure(
                requests[miss.RepresentativeIndex],
                source,
                target,
                "translation_capacity_unavailable");
            foreach (var index in miss.Indices)
                results[index] = failure;
        }
    }

    private async Task<TranslationProviderResult> TranslateCoreAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage,
        MessagingActor? account,
        string? requestReference,
        CancellationToken cancellationToken,
        bool allowProviderObservationReuse = true,
        bool allowLegacyIntelligence = true,
        LegendConnectExternalProviderPolicy? providerPolicy = null)
    {
        var externalProviderPolicy =
            LegendConnectExternalProviderPolicy.Resolve(providerPolicy);
        var target = await _languages.NormalizeEnabledTranslationLanguageAsync(targetLanguage, cancellationToken);
        if (target is null)
            return new TranslationProviderResult(false, null, null, _azure.ProviderName, "translation_language_unsupported");

        var source = sourceLanguage is null
            ? null
            : await _languages.NormalizeEnabledTranslationLanguageAsync(sourceLanguage, cancellationToken);
        if (sourceLanguage is not null && source is null)
            return new TranslationProviderResult(false, null, null, _azure.ProviderName, "translation_language_unsupported");

        LegendConnectTelemetry.TranslationRequested(source, target);

        if (source is not null && string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            LegendConnectTelemetry.SameLanguageBypass(source);
            if (_systemUsage is not null)
                await _systemUsage.TryRecordSameLanguageBypassAsync(text?.Length ?? 0, cancellationToken);
            await RecordAvoidedSafelyAsync(account, TranslationAvoidedPath.SameLanguage, text?.Length ?? 0, cancellationToken);
            return new TranslationProviderResult(true, text, source, "LegendConnectSameLanguage");
        }

        LegendContextualTranslationSuggestion? contextualSuggestion = null;
        var promotedTranslationModelFailed = false;
        var pairKey = source is null ? null : LegendLanguageIdentity.PairKey(source, target);
        LegendLanguagePairSnapshot? enabledPair = null;
        if (source is not null)
        {
            try
            {
                enabledPair = await _languages.GetEnabledPairAsync(
                    source,
                    target,
                    cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Pair eligibility is a fail-closed gate for internal stages,
                // not permission to strand recipient translation. Preserve the
                // established provider path and make the internal outage clear.
                _logger.LogWarning(
                    exception,
                    "Legend Connect pair eligibility was unavailable; Azure fallback remains active. Pair={PairKey}",
                    pairKey);
            }
        }
        if (allowLegacyIntelligence && enabledPair is not null && _intelligence is not null && source is not null)
        {
            try
            {
                var memory = await _intelligence.TryGetTrustedExactMemoryAsync(source, target, text ?? string.Empty, cancellationToken);
                if (memory is not null)
                {
                    if (_demand is not null)
                        await _demand.TryRecordAsync(pairKey!, 0, translationMemoryHit: true, cancellationToken: cancellationToken);
                    LegendConnectTelemetry.TranslationMemoryHit(pairKey!);
                    if (_systemUsage is not null)
                    {
                        await _systemUsage.TryRecordAsync(new TranslationSystemUsageDelta(
                            TranslationMemoryCharactersAvoided: text?.Length ?? 0), cancellationToken);
                    }
                    await RecordAvoidedSafelyAsync(account, TranslationAvoidedPath.TranslationMemory, text?.Length ?? 0, cancellationToken);
                    return new TranslationProviderResult(true, memory.Text, source, "LegendConnectTranslationMemory");
                }

                // Structural curriculum is evaluated after exact memory and
                // before Azure fallback. Phase 2 exposes only a safe gate: it
                // returns no formulation until an independently approved
                // composition engine exists.
                if (_structuralComposition is not null)
                {
                    var structural = await _structuralComposition.TryComposeAsync(
                        source,
                        target,
                        text ?? string.Empty,
                        cancellationToken);
                    if (structural is not null)
                    {
                        if (_demand is not null)
                        {
                            await _demand.TryRecordAsync(
                                pairKey!,
                                0,
                                structuralInternalServed: true,
                                cancellationToken: cancellationToken);
                        }

                        if (_systemUsage is not null)
                        {
                            await _systemUsage.TryRecordAsync(
                                new TranslationSystemUsageDelta(
                                    StructuralCompositionCharactersAvoided:
                                        text?.Length ?? 0),
                                cancellationToken);
                        }

                        await RecordAvoidedSafelyAsync(
                            account,
                            TranslationAvoidedPath.StructuralComposition,
                            text?.Length ?? 0,
                            cancellationToken);

                        return new TranslationProviderResult(
                            true,
                            structural.Text,
                            source,
                            "LegendConnectStructuralComposition");
                    }
                }

                contextualSuggestion = await _intelligence.EvaluateContextAsync(source, target, text ?? string.Empty, cancellationToken);
                var contextualCompositionActive = _intelligence.IsContextualCompositionActive;
                if (_runtimePolicy is not null)
                {
                    contextualCompositionActive = string.Equals(
                        (await _runtimePolicy.GetEffectiveAsync(cancellationToken)).ContextualCompositionMode,
                        "Active",
                        StringComparison.OrdinalIgnoreCase);
                }
                if (contextualSuggestion is not null && contextualCompositionActive)
                {
                    if (_demand is not null)
                    {
                        await _demand.TryRecordAsync(
                            pairKey!,
                            0,
                            contextualCompositionObserved: true,
                            contextualInternalServed: true,
                            cancellationToken: cancellationToken);
                    }
                    if (_systemUsage is not null)
                    {
                        await _systemUsage.TryRecordAsync(new TranslationSystemUsageDelta(
                            ContextualCharactersAvoided: text?.Length ?? 0), cancellationToken);
                    }
                    await RecordAvoidedSafelyAsync(account, TranslationAvoidedPath.ContextualComposition, text?.Length ?? 0, cancellationToken);
                    return new TranslationProviderResult(
                        true,
                        contextualSuggestion.Text,
                        source,
                        "LegendConnectContextualComposition");
                }

                if (_activeModelInference is not null &&
                    !externalProviderPolicy.ForbidsExternalProviders)
                {
                    var neural =
                        await _activeModelInference.TryTranslateAsync(
                            source,
                            target,
                            text ?? string.Empty,
                            cancellationToken);

                    if (neural.Succeeded &&
                        !string.IsNullOrWhiteSpace(
                            neural.Text))
                    {
                        if (_demand is not null)
                        {
                            await _demand.TryRecordAsync(
                                pairKey!,
                                0,
                                neuralModelServed: true,
                                cancellationToken: cancellationToken);
                        }

                        if (_systemUsage is not null)
                        {
                            await _systemUsage.TryRecordAsync(
                                new TranslationSystemUsageDelta(
                                    PromotedTranslationModelCharactersAvoided:
                                        text?.Length ?? 0),
                                cancellationToken);
                        }

                        await RecordAvoidedSafelyAsync(
                            account,
                            TranslationAvoidedPath.PromotedTranslationModel,
                            text?.Length ?? 0,
                            cancellationToken);

                        return new TranslationProviderResult(
                            true,
                            neural.Text,
                            source,
                            "LegendConnectPromotedTranslationModel");
                    }

                    promotedTranslationModelFailed =
                        !string.Equals(
                            neural.ErrorCode,
                            "active_model_unavailable",
                            StringComparison.Ordinal);
                }

                var providerObservation = allowProviderObservationReuse
                    ? await _intelligence.TryGetReusableProviderObservationAsync(
                        source,
                        target,
                        text ?? string.Empty,
                        cancellationToken)
                    : null;

                if (providerObservation is not null)
                {
                    if (_demand is not null)
                    {
                        await _demand.TryRecordAsync(
                            pairKey!,
                            0,
                            neuralModelFailed: promotedTranslationModelFailed,
                            providerObservationReused: true,
                            cancellationToken:
                                cancellationToken);
                    }

                    if (_systemUsage is not null)
                    {
                        await _systemUsage.TryRecordAsync(
                            new TranslationSystemUsageDelta(
                                ProviderObservationCharactersAvoided:
                                    text?.Length ?? 0),
                            cancellationToken);
                    }

                    await RecordAvoidedSafelyAsync(
                        account,
                        TranslationAvoidedPath.ProviderObservationReuse,
                        text?.Length ?? 0,
                        cancellationToken);

                    return new TranslationProviderResult(
                        true,
                        providerObservation.Text,
                        source,
                        "LegendConnectProviderObservation");
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Legend Connect intelligence evaluation failed; Azure fallback remains authoritative. Pair={PairKey}", pairKey);
                if (_operations is not null)
                {
                    await _operations.TryRecordAsync(
                        "ContextEvaluation",
                        "Warning",
                        "Failed",
                        source,
                        pairKey,
                        "context_evaluation_failed",
                        summary: "Context evaluation failed; the Azure fallback path remained active.",
                        cancellationToken: cancellationToken);
                }
            }
        }

        if (pairKey is not null && _demand is not null)
        {
            await _demand.TryRecordAsync(
                pairKey,
                text?.Length ?? 0,
                azureFallback: true,
                contextualCompositionObserved: contextualSuggestion is not null,
                neuralModelFailed: promotedTranslationModelFailed,
                cancellationToken: cancellationToken);
        }

        // Every internal Legend stage above has been given its chance. What
        // remains below is the external boundary: quota and capacity
        // accounting for the external provider, and the Azure fallback call
        // itself. A native-only request fails closed here, with no provider
        // identity claimed, rather than being attributed to Azure.
        if (externalProviderPolicy.ForbidsExternalProviders)
        {
            return new TranslationProviderResult(
                false,
                null,
                source,
                "None",
                "external_provider_forbidden_by_native_only_policy");
        }

        TranslationQuotaReservation? quotaReservation = null;
        if (account is not null && _entitlements is not null)
        {
            TranslationQuotaReservationResult quota;
            try
            {
                quota = await _entitlements.TryReserveAsync(
                    new TranslationQuotaReservationRequest(
                        account,
                        requestReference ?? string.Empty,
                        source ?? string.Empty,
                        target,
                        _azure.ProviderName,
                        text?.Length ?? 0),
                    cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(exception, "Legend Connect account quota reservation failed. Target={TargetLanguage}", target);
                return new TranslationProviderResult(false, null, source, _azure.ProviderName, "translation_accounting_unavailable");
            }

            if (!quota.Succeeded)
            {
                if (_systemUsage is not null &&
                    (quota.ErrorCode is "translation_quota_exhausted" or "translation_access_revoked"))
                {
                    await _systemUsage.TryRecordAsync(
                        new TranslationSystemUsageDelta(QuotaDeniedRequests: 1),
                        cancellationToken);
                }
                return new TranslationProviderResult(false, null, source, _azure.ProviderName, quota.ErrorCode ?? "translation_accounting_unavailable");
            }
            quotaReservation = quota.Reservation;
        }

        var reservation = await _capacity.TryReserveAsync(
            _azure.ProviderName,
            text?.Length ?? 0,
            TranslationCapacityPurpose.Live,
            reservationReference: requestReference,
            cancellationToken: cancellationToken);
        if (reservation is null)
        {
            await CompleteQuotaSafelyAsync(
                quotaReservation,
                providerExecuted: false,
                providerSucceeded: false,
                failureCode: "translation_capacity_unavailable",
                cancellationToken);
            if (_operations is not null)
            {
                await _operations.TryRecordAsync(
                    "CapacityReservation",
                    "Warning",
                    "Unavailable",
                    source,
                    pairKey,
                    "translation_capacity_unavailable",
                    summary: "Live translation capacity could not be reserved.",
                    cancellationToken: cancellationToken);
            }
            return new TranslationProviderResult(false, null, source, _azure.ProviderName, "translation_capacity_unavailable");
        }

        var providerSucceeded = false;
        var providerExecuted = false;
        string? providerFailureCode = null;
        try
        {
            providerExecuted = true;
            var result = await _azure.TranslateAsync(text ?? string.Empty, target, source, cancellationToken);
            providerSucceeded = result.Succeeded && !string.IsNullOrWhiteSpace(result.TranslatedText);
            providerFailureCode = providerSucceeded ? null : result.ErrorCode ?? "translation_provider_failed";
            if (providerSucceeded && source is not null)
                LegendConnectTelemetry.ProviderCharactersServed(_azure.ProviderName, reservation.Characters, source, target);
            else if (!providerSucceeded && _operations is not null)
            {
                await _operations.TryRecordAsync(
                    "AzureProvider",
                    "Error",
                    "Failed",
                    source,
                    pairKey,
                    result.ErrorCode ?? "translation_provider_failed",
                    summary: "Azure translation did not return a usable result.",
                    cancellationToken: cancellationToken);
            }
            return result;
        }
        catch
        {
            providerFailureCode = "translation_provider_failed";
            throw;
        }
        finally
        {
            try
            {
                // Once the HTTP request starts, Azure may have accepted and
                // billed its input even if a response is lost. Retain that
                // character cost in the rolling ledger rather than releasing
                // it and risking a tier overrun on a retry.
                await _capacity.CompleteAsync(reservation, providerExecuted, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A ledger-write failure must not alter a successfully returned
                // provider result. It remains observable without logging text.
                _logger.LogError(exception, "Legend Connect capacity finalization failed. Provider={Provider} Characters={Characters}", _azure.ProviderName, reservation.Characters);
                if (_operations is not null)
                {
                    await _operations.TryRecordAsync(
                        "CapacityFinalization",
                        "Error",
                        "Failed",
                        source,
                        pairKey,
                        "capacity_finalization_failed",
                        summary: "Provider capacity finalization failed after the translation path completed.",
                        cancellationToken: CancellationToken.None);
                }
            }

            await CompleteQuotaSafelyAsync(
                quotaReservation,
                providerExecuted,
                providerSucceeded,
                providerFailureCode,
                cancellationToken);
            if (_systemUsage is not null && providerExecuted)
            {
                await _systemUsage.TryRecordAsync(new TranslationSystemUsageDelta(
                    ProviderOperations: 1,
                    ProviderBillableCharacters: providerSucceeded ? reservation.Characters : 0,
                    ProviderFailures: providerSucceeded ? 0 : 1), cancellationToken);
            }
        }
    }

    private async Task<LegendRetainedTranslationMemoryMatch?> WaitForRetainedTranslationAsync(
        string identity,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 70; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            var match = await _intelligence!.TryGetRetainedTranslationAsync(identity, cancellationToken);
            if (match is not null)
                return match;
        }
        return null;
    }

    private static string? ValidateRetainedRequest(
        RetainedTranslationRequest request,
        string? source,
        string? target)
    {
        if (source is null || target is null)
            return "translation_language_unsupported";
        if (string.IsNullOrWhiteSpace(request.SourceText) || request.SourceText.Length > 10_000)
            return "translation_source_invalid";
        if (string.IsNullOrWhiteSpace(request.StableSourceContentId) || request.StableSourceContentId.Length > 180 ||
            string.IsNullOrWhiteSpace(request.SourceRevision) || request.SourceRevision.Length > 80 ||
            string.IsNullOrWhiteSpace(request.TranslationContext) || request.TranslationContext.Length > 180)
            return "translation_identity_invalid";
        if (!TranslationReuseScopes.IsSupported(request.ReuseScope))
            return "translation_scope_invalid";
        if (request.ReuseScope == TranslationReuseScopes.Global)
            return string.IsNullOrEmpty(request.ScopeIdentityHash) ? null : "translation_scope_invalid";
        return IsLowerHex(request.ScopeIdentityHash, 64)
            ? null
            : "translation_scope_invalid";
    }

    private static RetainedTranslationResult RetainedFailure(
        RetainedTranslationRequest request,
        string? source,
        string? target,
        string errorCode) => new(
            false,
            request.SourceText,
            source ?? request.SourceLanguageCode,
            target ?? request.TargetLanguageCode,
            "SourceFallback",
            "Source",
            "Fallback",
            DateTime.UtcNow,
            Reused: false,
            errorCode);

    private static RetainedTranslationResult ToRetainedResult(
        LegendRetainedTranslationMemoryMatch match,
        string source,
        string target,
        bool reused) => new(
            true,
            match.Text,
            source,
            target,
            match.Provider,
            match.Provenance,
            match.QualityState,
            match.CreatedUtc,
            reused);

    private static string RetainedIdentity(
        RetainedTranslationRequest request,
        string source,
        string target,
        string provider,
        string providerVersion) => Hash(string.Join('\n',
            "retained-translation-v1",
            request.StableSourceContentId.Trim(),
            LegendLanguageIdentity.TextHash(request.SourceText),
            source,
            target,
            request.SourceRevision.Trim(),
            request.TranslationContext.Trim(),
            Hash(request.PlaceholderContract),
            provider,
            providerVersion,
            request.ReuseScope,
            request.ScopeIdentityHash));

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)))
        .ToLowerInvariant();

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private async Task RecordAvoidedSafelyAsync(
        MessagingActor? account,
        TranslationAvoidedPath path,
        int characters,
        CancellationToken cancellationToken)
    {
        if (account is null || _entitlements is null)
            return;
        try
        {
            await _entitlements.RecordAvoidedAsync(account, path, characters, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Legend Connect avoided-translation usage write failed. Path={Path}", path);
        }
    }

    private async Task CompleteQuotaSafelyAsync(
        TranslationQuotaReservation? reservation,
        bool providerExecuted,
        bool providerSucceeded,
        string? failureCode,
        CancellationToken cancellationToken)
    {
        if (reservation is null || _entitlements is null)
            return;
        try
        {
            await _entitlements.CompleteAsync(
                reservation,
                providerExecuted,
                providerSucceeded,
                failureCode,
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The translation result must remain non-blocking for messaging.
            // The reservation ledger is durable and therefore auditable if a
            // finalization outage needs reconciliation.
            _logger.LogError(exception, "Legend Connect account quota finalization failed.");
        }
    }
}
