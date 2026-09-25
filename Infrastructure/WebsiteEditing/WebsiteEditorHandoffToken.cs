using System.Security.Cryptography;
using System.Text;

namespace Infrastructure.WebsiteEditing;

public static class WebsiteEditorHandoffToken
{
    public static string Create(int byteLength = 32)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string Hash(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
