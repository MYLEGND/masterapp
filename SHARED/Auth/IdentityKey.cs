namespace Shared.Auth;

/// <summary>
/// Canonical normalization/equality helpers for user/resource identity keys.
/// Keep all ownership checks on a single comparer to avoid subtle drift.
/// </summary>
public static class IdentityKey
{
    public static string Normalize(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsMissing(string? value)
        => string.IsNullOrWhiteSpace(Normalize(value));

    public static bool EqualsNormalized(string? left, string? right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    public static HashSet<string> NormalizeSet(IEnumerable<string?> values)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var normalized = Normalize(value);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                set.Add(normalized);
            }
        }

        return set;
    }
}
