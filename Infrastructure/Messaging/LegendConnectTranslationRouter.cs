using Domain.Messaging;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

/// <summary>
/// Server-side provider router. Azure remains the only production provider in
/// this milestone; the router is the stable insertion point for an evaluated
/// future Legend model without any mobile contract change.
/// </summary>
internal sealed class LegendConnectTranslationRouter : ITranslationService
{
    private readonly ITranslationProvider _azure;
    private readonly ILegendLanguageRegistry _languages;
    private readonly ITranslationCapacityAuthority _capacity;
    private readonly ITranslationDemandRecorder? _demand;
    private readonly ITranslationSystemUsageRecorder? _systemUsage;
    private readonly ILegendConnectTranslationIntelligence? _intelligence;
    private readonly ILegendConnectOperationalEventWriter? _operations;
    private readonly ILogger<LegendConnectTranslationRouter> _logger;

    public LegendConnectTranslationRouter(
        ITranslationProvider azure,
        ILegendLanguageRegistry languages,
        ITranslationCapacityAuthority capacity,
        ILogger<LegendConnectTranslationRouter> logger,
        ITranslationDemandRecorder? demand = null,
        ITranslationSystemUsageRecorder? systemUsage = null,
        ILegendConnectTranslationIntelligence? intelligence = null,
        ILegendConnectOperationalEventWriter? operations = null)
    {
        _azure = azure;
        _languages = languages;
        _capacity = capacity;
        _logger = logger;
        _demand = demand;
        _systemUsage = systemUsage;
        _intelligence = intelligence;
        _operations = operations;
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
                await _systemUsage.TryRecordSameLanguageBypassAsync(cancellationToken);
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
                    return new TranslationProviderResult(true, memory.Text, source, "LegendConnectTranslationMemory");
                }

                contextualSuggestion = await _intelligence.EvaluateContextAsync(source, target, text ?? string.Empty, cancellationToken);
                if (contextualSuggestion is not null && _intelligence.IsContextualCompositionActive)
                {
                    if (_demand is not null)
                    {
                        await _demand.TryRecordAsync(
                            pairKey!,
                            0,
                            contextualCompositionObserved: true,
                            cancellationToken: cancellationToken);
                    }
                    return new TranslationProviderResult(
                        true,
                        contextualSuggestion.Text,
                        source,
                        "LegendConnectContextualComposition");
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

        var reservation = await _capacity.TryReserveAsync(
            _azure.ProviderName,
            text?.Length ?? 0,
            TranslationCapacityPurpose.Live,
            cancellationToken);
        if (reservation is null)
        {
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
        try
        {
            var result = await _azure.TranslateAsync(text ?? string.Empty, target, source, cancellationToken);
            providerSucceeded = result.Succeeded && !string.IsNullOrWhiteSpace(result.TranslatedText);
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
        finally
        {
            try
            {
                await _capacity.CompleteAsync(reservation, providerSucceeded, cancellationToken);
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
        }
    }
}
