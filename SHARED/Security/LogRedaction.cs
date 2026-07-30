using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Shared.Security;

/// <summary>
/// The one shared logging redaction authority. Provides helpers to strip
/// sensitive values from log output so secrets, tokens, authorization headers,
/// cookies, connection strings, and API keys are never written to logs. It is
/// additive to existing diagnostics — callers keep their correlation IDs and
/// operational messages and route only untrusted/sensitive fragments through it.
/// </summary>
public static partial class LogRedactor
{
    public const string Mask = "***REDACTED***";

    private static readonly HashSet<string> SensitiveHeaderNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Authorization",
            "Cookie",
            "Set-Cookie",
            "Proxy-Authorization",
            "X-Api-Key",
            "ApiKey",
            "RequestVerificationToken",
            "X-CSRF-TOKEN",
            "X-XSRF-TOKEN"
        };

    /// <summary>True when a header name must never have its value logged.</summary>
    public static bool IsSensitiveHeader(string? headerName)
        => !string.IsNullOrWhiteSpace(headerName) && SensitiveHeaderNames.Contains(headerName);

    /// <summary>Returns the header value, or the mask when the header is sensitive.</summary>
    public static string RedactHeader(string? headerName, string? headerValue)
        => IsSensitiveHeader(headerName) ? Mask : (headerValue ?? string.Empty);

    /// <summary>Fully masks a known-sensitive value (e.g. a token) for logging.</summary>
    public static string MaskValue(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : Mask;

    /// <summary>
    /// Best-effort redaction of secrets embedded in a free-text log message:
    /// bearer tokens, JWTs, key=value secrets, and connection-string secrets.
    /// </summary>
    public static string? Redact(string? message)
    {
        if (string.IsNullOrEmpty(message)) return message;

        var result = BearerTokenRegex().Replace(message, "Bearer " + Mask);
        result = JwtRegex().Replace(result, Mask);
        result = KeyValueSecretRegex().Replace(result, m => m.Groups["k"].Value + "=" + Mask);
        return result;
    }

    /// <summary>Masks the secret portions of a connection string (password / account key / shared key).</summary>
    public static string? RedactConnectionString(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return connectionString;
        return ConnectionStringSecretRegex().Replace(
            connectionString, m => m.Groups["k"].Value + "=" + Mask);
    }

    // "Bearer <token>"
    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();

    // JWT: header.payload.signature
    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+")]
    private static partial Regex JwtRegex();

    // key=value / key: value for common secret key names
    [GeneratedRegex(@"(?<k>password|pwd|secret|token|access_token|refresh_token|apikey|api_key|client_secret|clientsecret)\s*[=:]\s*[^\s;,&""']+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueSecretRegex();

    // Connection-string secret segments
    [GeneratedRegex(@"(?<k>Password|Pwd|AccountKey|SharedAccessKey|SharedAccessSignature|AccessKey)\s*=\s*[^;]+", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringSecretRegex();
}
