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
/// Reserved provider adapter for ChatGPT/OpenAI Ads. Step 1 registers the destination
/// identity but deliberately fails closed. Account ownership, Pixel/CAPI, attribution,
/// and delivery are added only by later validated steps.
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

        var connection = await connections.GetAsync(owner, cancellationToken);
        if (!connection.Connected)
            return new(Key, Supported: false, Configured: false, Eligible: false, Reason: "destination_not_configured");

        // Step 2 establishes only account/measurement connection authority. Event mapping
        // and delivery remain intentionally unavailable until their dedicated steps.
        return new(Key, Supported: false, Configured: true, Eligible: false, Reason: "delivery_not_implemented");
    }
}
