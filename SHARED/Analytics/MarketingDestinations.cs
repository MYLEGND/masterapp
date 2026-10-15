namespace Shared.Analytics;

/// <summary>
/// Stable provider keys for marketing destinations. Provider keys are transport identifiers,
/// never tenant identifiers and never business truth.
/// </summary>
public static class MarketingDestinationKeys
{
    public const string Meta = "meta";
    public const string OpenAi = "openai";
    public const string Google = "google";
    public const string TikTok = "tiktok";

    public static string Normalize(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("A marketing destination provider is required.", nameof(provider));

        var value = provider.Trim().ToLowerInvariant();
        if (value.Length > 20 || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '-')))
            throw new ArgumentException("Marketing destination provider keys must be lowercase letters, digits, or hyphens and at most 20 characters.", nameof(provider));

        return value;
    }
}

/// <summary>
/// Provider-neutral projection of one canonical marketing outcome.
/// The outcome remains owned by MasterApp; destinations may only evaluate or project it.
/// </summary>
public sealed record MarketingOutcome(
    string EventName,
    string EventId,
    bool IsServerAuthority,
    string? MetadataJson = null)
{
    public string NormalizedEventName => EventName?.Trim() ?? string.Empty;
    public string NormalizedEventId => EventId?.Trim() ?? string.Empty;
}

/// <summary>
/// A destination's mapping/configuration readiness. This is never permission to send.
/// Dispatch must independently enforce persisted canonical human evidence, consent and scoped identity.
/// </summary>
public sealed record MarketingDestinationDecision(
    string DestinationKey,
    bool Supported,
    bool Configured,
    bool MappingReady,
    string Reason);

public interface IMarketingDestination
{
    string Key { get; }

    ValueTask<MarketingDestinationDecision> EvaluateAsync(
        MarketingOwnerScope owner,
        MarketingOutcome outcome,
        CancellationToken cancellationToken = default);
}

public interface IMarketingDestinationRegistry
{
    IReadOnlyCollection<string> Keys { get; }

    bool TryGet(string provider, out IMarketingDestination destination);

    IMarketingDestination GetRequired(string provider);

    ValueTask<IReadOnlyList<MarketingDestinationDecision>> EvaluateAsync(
        MarketingOwnerScope owner,
        MarketingOutcome outcome,
        CancellationToken cancellationToken = default);
}
