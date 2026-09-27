using Domain.Entities;
using Shared.Analytics;

namespace Infrastructure.Analytics;

/// <summary>
/// Canonical destination registry. It owns provider discovery only; it does not own
/// canonical events, credentials, provider delivery, or advertising mutations.
/// </summary>
public sealed class MarketingDestinationRegistry : IMarketingDestinationRegistry
{
    private readonly IReadOnlyDictionary<string, IMarketingDestination> _destinations;

    public MarketingDestinationRegistry(IEnumerable<IMarketingDestination> destinations)
    {
        var map = new Dictionary<string, IMarketingDestination>(StringComparer.OrdinalIgnoreCase);
        foreach (var destination in destinations)
        {
            var key = MarketingDestinationKeys.Normalize(destination.Key);
            if (!map.TryAdd(key, destination))
                throw new InvalidOperationException($"Marketing destination '{key}' is registered more than once.");
        }

        _destinations = map;
        Keys = map.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyCollection<string> Keys { get; }

    public bool TryGet(string provider, out IMarketingDestination destination) =>
        _destinations.TryGetValue(MarketingDestinationKeys.Normalize(provider), out destination!);

    public IMarketingDestination GetRequired(string provider) =>
        TryGet(provider, out var destination)
            ? destination
            : throw new KeyNotFoundException($"Marketing destination '{MarketingDestinationKeys.Normalize(provider)}' is not registered.");

    public async ValueTask<IReadOnlyList<MarketingDestinationDecision>> EvaluateAsync(
        MarketingOwnerScope owner,
        MarketingOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(outcome);

        var decisions = new List<MarketingDestinationDecision>(_destinations.Count);
        foreach (var key in Keys)
            decisions.Add(await _destinations[key].EvaluateAsync(owner, outcome, cancellationToken));

        return decisions;
    }
}

/// <summary>
/// Adapter over the existing Meta authorities. It intentionally does not replace
/// MetaSignalOutcomeDispatcherHostedService, MetaSendAuthority, or MetaConversionsApiService.
/// </summary>
public sealed class MetaMarketingDestination(MarketingConnectionStore connections) : IMarketingDestination
{
    public string Key => MarketingDestinationKeys.Meta;

    public async ValueTask<MarketingDestinationDecision> EvaluateAsync(
        MarketingOwnerScope owner,
        MarketingOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        var eventName = outcome.NormalizedEventName;
        if (eventName.Length == 0 || !MetaSignalEventCatalog.TryGet(eventName, out var definition))
            return Decision(false, false, "event_not_supported");

        var supported = outcome.IsServerAuthority
            ? definition.AllowServerForward && MetaSignalEventCatalog.IsServerAuthorityEvent(eventName)
            : definition.AllowBrowserPixel;

        if (!supported)
            return Decision(false, false, "delivery_mode_not_supported");

        var connection = await connections.GetStatusAsync(owner, Key, cancellationToken);
        if (connection is null || connection.DisconnectedUtc.HasValue)
            return Decision(true, false, "destination_not_configured");

        var configured = outcome.IsServerAuthority
            ? !string.IsNullOrWhiteSpace(connection.PixelId) &&
              !string.IsNullOrWhiteSpace(await connections.GetCapiTokenAsync(owner, cancellationToken))
            : !string.IsNullOrWhiteSpace(connection.PixelId);

        if (!configured)
            return Decision(true, false, "destination_not_ready");

        if (outcome.IsServerAuthority &&
            !MetaSignalSingleTruthPolicy.CanDispatchServerAuthority(eventName, outcome.MetadataJson))
            return Decision(true, true, "canonical_outcome_not_dispatch_eligible");

        return Decision(true, true, "eligible");
    }

    private MarketingDestinationDecision Decision(bool supported, bool configured, string reason) =>
        new(Key, supported, configured, supported && configured && reason == "eligible", reason);
}

/// <summary>
/// Canonical ChatGPT/OpenAI Ads destination projection. It evaluates only whether a
/// canonical outcome has a supported OpenAI conversion mapping and whether the scoped
/// owner has the required measurement connection. Provider delivery and advertising
/// mutations remain owned by their dedicated shared services.
/// </summary>
public sealed class OpenAiMarketingDestination(
    IOpenAiAdsAccountConnectionAuthority connections) : IMarketingDestination
{
    public string Key => MarketingDestinationKeys.OpenAi;

    public async ValueTask<MarketingDestinationDecision> EvaluateAsync(
        MarketingOwnerScope owner,
        MarketingOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(outcome);

        var destination = MarketingConversionDestinationCatalog.ResolveOpenAi(outcome.NormalizedEventName);
        if (!outcome.IsServerAuthority || destination is null)
            return new(Key, Supported: false, Configured: false, Eligible: false, Reason: "event_not_supported");

        var connection = await connections.GetAsync(owner, cancellationToken);
        if (!connection.Connected)
            return new(Key, Supported: true, Configured: false, Eligible: false, Reason: "destination_not_configured");

        var configured = connection.PixelConfigured && connection.ConversionsApiConfigured;
        if (!configured)
            return new(Key, Supported: true, Configured: false, Eligible: false, Reason: "destination_not_ready");

        return new(Key, Supported: true, Configured: true, Eligible: true, Reason: "eligible");
    }
}
