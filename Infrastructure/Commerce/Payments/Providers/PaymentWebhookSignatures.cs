using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Infrastructure.Commerce.Payments.Providers;

public static class PaymentWebhookSignatures
{
    public static bool VerifySquare(string body, string? signature, string key, string notificationUrl)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(key) ||
            !Uri.TryCreate(notificationUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https") return false;
        try
        {
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(notificationUrl + body));
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(signature));
        }
        catch (FormatException) { return false; }
    }

    public static bool VerifyStripe(string body, string? signature, string key, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(key)) return false;
        var parts = signature.Split(',').Select(x => x.Trim().Split('=', 2)).Where(x => x.Length == 2).ToArray();
        var timestamps = parts.Where(x => x[0] == "t").ToArray();
        if (timestamps.Length != 1 || !long.TryParse(timestamps[0][1], NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)) return false;
        if (timestamp < now.ToUnixTimeSeconds() - 300 || timestamp > now.ToUnixTimeSeconds() + 300) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + "." + body));
        foreach (var candidate in parts.Where(x => x[0] == "v1"))
        {
            try { if (CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(candidate[1]))) return true; }
            catch (FormatException) { }
        }
        return false;
    }
}
