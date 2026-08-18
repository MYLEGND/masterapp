using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
    string? ErrorCode);

internal interface ILegendConnectActiveModelInference
{
    Task<LegendConnectActiveModelInferenceResult> TryTranslateAsync(
        string sourceLanguageCode,
        string targetLanguageCode,
        string text,
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

        var result =
            await _transport.GenerateAsync(
                pair.ActiveModelVersion,
                sourceLanguageCode,
                targetLanguageCode,
                text,
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
                    "active_model_inference_failed");
        }

        return new(
            true,
            result.Text,
            pair.ActiveModelVersion,
            null);
    }
}

internal sealed class LegendConnectTranslationRouter : IAccountScopedTranslationService
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
        ILegendConnectActiveModelInference? activeModelInference = null)
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
    }

    public async Task<TranslationDetectionResult> DetectLanguageAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var result = await _azure.DetectLanguageAsync(text, cancellationToken);
        if (!result.Succeeded)
            return result;

        var language = await _languages.NormalizeEnabledTranslationLanguageAsync(result.Language, cancellationToken);
        return language is null
            ? new TranslationDetectionResult(false, null, "translation_language_unsupported")
            : new TranslationDetectionResult(true, language);
    }

    public async Task<TranslationProviderResult> TranslateAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default)
        => await TranslateCoreAsync(
            text,
            targetLanguage,
            sourceLanguage,
            account: null,
            requestReference: null,
            cancellationToken);

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

    private async Task<TranslationProviderResult> TranslateCoreAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage,
        MessagingActor? account,
        string? requestReference,
        CancellationToken cancellationToken)
    {
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
        var pairKey = source is null ? null : LegendLanguageIdentity.PairKey(source, target);
        if (source is not null && _intelligence is not null)
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

                if (_activeModelInference is not null)
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
                        return new TranslationProviderResult(
                            true,
                            neural.Text,
                            source,
                            "LegendConnectNeuralModel");
                    }
                }

                var providerObservation =
                    await _intelligence.TryGetReusableProviderObservationAsync(
                        source,
                        target,
                        text ?? string.Empty,
                        cancellationToken);

                if (providerObservation is not null)
                {
                    if (_demand is not null)
                    {
                        await _demand.TryRecordAsync(
                            pairKey!,
                            0,
                            translationMemoryHit: true,
                            cancellationToken:
                                cancellationToken);
                    }

                    LegendConnectTelemetry.TranslationMemoryHit(
                        pairKey!);

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
                cancellationToken: cancellationToken);
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
