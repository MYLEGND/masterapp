namespace Protect_Website.Models;

public sealed record TrustedCarrierStripViewModel(
    string Placement,
    string Mode,
    string PageKey,
    string QuoteType,
    string Variant,
    string TrafficType,
    string? HeadingText = null,
    string? DisclaimerText = null,
    string? InstanceKey = null)
{
    public string EffectiveHeadingText =>
        !string.IsNullOrWhiteSpace(HeadingText)
            ? HeadingText
            : string.Equals(Mode, "compact", StringComparison.OrdinalIgnoreCase)
                ? "We can review options from trusted carriers."
                : "Options may be available through trusted carriers such as:";

    public string EffectiveDisclaimerText =>
        !string.IsNullOrWhiteSpace(DisclaimerText)
            ? DisclaimerText
            : "Carrier availability, product options, and eligibility vary by state, underwriting, and appointment status.";

    public string EffectiveInstanceKey =>
        !string.IsNullOrWhiteSpace(InstanceKey)
            ? InstanceKey
            : $"{Placement}-{Mode}-{QuoteType}-{Variant}-{PageKey}".ToLowerInvariant();

    public string AriaLabel =>
        string.Equals(Mode, "compact", StringComparison.OrdinalIgnoreCase)
            ? "Trusted carrier review strip"
            : "Trusted carrier options strip";
}
