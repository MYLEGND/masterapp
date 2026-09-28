using System.Net;
using System.Security.Cryptography;
using System.Text;
using Shared.Analytics;

namespace Infrastructure.Analytics;

/// <summary>OpenAI-specific transformation of the shared canonical raw customer identity.</summary>
public static class OpenAiConversionUserMapper
{
    public static OpenAiConversionUser? Map(CanonicalMarketingIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var emails = HashMany([NormalizeEmail(identity.Email)]);
        var phones = HashMany([NormalizePhone(identity.Phone)]);
        var externalIds = HashMany(identity.ExternalIds.Select(NormalizeExternalId));
        var firstNames = HashMany([NormalizeName(identity.FirstName)]);
        var lastNames = HashMany([NormalizeName(identity.LastName)]);

        var regions = RawMany([NormalizeRegion(identity.State)]);
        var postalCodes = RawMany([NormalizePostal(identity.PostalCode)]);
        var cities = RawMany([NormalizeCity(identity.City)]);
        var countries = RawMany([NormalizeCountry(identity.Country)]);
        var ipAddress = NormalizeIp(identity.ClientIpAddress);
        var userAgent = Clean(identity.ClientUserAgent, 1024);
        var obref = OpenAiBrowserReference.Normalize(identity.Obref);

        if (emails is null && phones is null && externalIds is null &&
            firstNames is null && lastNames is null && regions is null &&
            postalCodes is null && cities is null && countries is null &&
            ipAddress is null && userAgent is null && obref is null)
            return null;

        return new(
            Obref: obref,
            EmailsSha256: emails,
            PhoneNumbersSha256: phones,
            ExternalIdsSha256: externalIds,
            FirstNamesSha256: firstNames,
            LastNamesSha256: lastNames,
            Regions: regions,
            PostalCodes: postalCodes,
            Cities: cities,
            Countries: countries,
            IpAddress: ipAddress,
            UserAgent: userAgent);
    }

    internal static string? NormalizeEmail(string? value)
    {
        var clean = value?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }

    internal static string? NormalizePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var input = value.Trim();
        var digits = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (char.IsDigit(ch))
            {
                digits.Append(ch);
                continue;
            }

            if (ch == '+' || ch == '(' || ch == ')' || ch == '.' || ch == '-' || char.IsWhiteSpace(ch))
                continue;

            return null;
        }

        var normalized = digits.ToString().TrimStart('0');
        return normalized.Length is >= 8 and <= 15 && normalized.All(char.IsDigit)
            ? normalized
            : null;
    }

    internal static string? NormalizeExternalId(string? value)
    {
        var clean = value?.Trim();
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }

    internal static string? NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (ch <= 0x7f && char.IsPunctuation(ch)) continue;
            builder.Append(ch);
        }

        var clean = builder.ToString();
        return clean.Length == 0 ? null : clean;
    }

    internal static string? Hash(string? normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static IReadOnlyList<string>? HashMany(IEnumerable<string?> values)
    {
        var hashes = values.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => Hash(x!))
            .Where(x => x is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        return hashes.Length == 0 ? null : hashes;
    }

    private static IReadOnlyList<string>? RawMany(IEnumerable<string?> values)
    {
        var clean = values.Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        return clean.Length == 0 ? null : clean;
    }

    private static string? NormalizeRegion(string? value) => Clean(value, 128)?.ToLowerInvariant();
    private static string? NormalizeCity(string? value) => Clean(value, 128)?.ToLowerInvariant();

    private static string? NormalizePostal(string? value)
    {
        var clean = Clean(value, 32);
        return clean is not null && clean.All(ch => char.IsLetterOrDigit(ch) || ch == ' ' || ch == '-')
            ? clean
            : null;
    }

    private static string? NormalizeCountry(string? value)
    {
        var clean = Clean(value, 2);
        return clean is not null && clean.Length == 2 && clean.All(char.IsLetter)
            ? clean.ToUpperInvariant()
            : null;
    }

    private static string? NormalizeIp(string? value)
    {
        var clean = value?.Trim();
        return !string.IsNullOrWhiteSpace(clean) && IPAddress.TryParse(clean, out _)
            ? clean
            : null;
    }

    private static string? Clean(string? value, int max)
    {
        var clean = value?.Trim();
        if (string.IsNullOrWhiteSpace(clean) || clean.Length > max || clean.Any(char.IsControl))
            return null;
        return clean;
    }
}
