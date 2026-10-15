using System.Globalization;

namespace Infrastructure.Analytics;

public sealed record CanonicalConversionValue(long AmountMinorUnits, string Currency);

/// <summary>Provider-neutral monetary value projection for canonical conversion outcomes.</summary>
public static class CanonicalConversionValueProjection
{
    private static readonly string[] MinorUnitKeys = ["valueCents", "totalCents", "revenueCents"];
    private static readonly string[] MajorUnitKeys = ["amount", "personalAmount", "revenue", "value", "paidPremium", "orderTotal"];

    public static CanonicalConversionValue? Resolve(string? metadataJson)
    {
        var currency = NormalizeCurrency(CanonicalAdvertisingEventProjection.ReadString(metadataJson, "currency"));

        foreach (var key in MinorUnitKeys)
        {
            var raw = CanonicalAdvertisingEventProjection.ReadString(metadataJson, key);
            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) || amount < 0)
                continue;
            return new(amount, currency ?? "USD");
        }

        foreach (var key in MajorUnitKeys)
        {
            var raw = CanonicalAdvertisingEventProjection.ReadString(metadataJson, key);
            if (!decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) || amount < 0)
                continue;
            if (currency is null) return null;
            var minor = decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
            return minor >= 0 ? new(minor, currency) : null;
        }

        return null;
    }

    private static string? NormalizeCurrency(string? value)
    {
        var clean = value?.Trim().ToUpperInvariant();
        return clean is { Length: 3 } && clean.All(char.IsLetter) ? clean : null;
    }
}
