using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Infrastructure.Reception;

internal sealed record OpenAiReceptionistIngressResult(
    string State, string? DeliveryId = null, string? ProviderEventId = null, string? SessionId = null);

internal sealed record OpenAiReceptionistSessionSettings(
    string Model, string Voice, string ConversationInstructions, string BackendMode);

/// <summary>Wire protocol only. Owner selection, admission and effect claims remain with the existing authorities.</summary>
internal static class OpenAiReceptionistProtocol
{
    internal const int MaximumWebhookBytes = 65536;
    internal const int MaximumEventBytes = 65536;
    internal const int SignatureToleranceSeconds = 300;
    // Conservative application character bound, not the provider's token limit.
    internal const int MaximumInstructionCharacters = 16384;
    internal const string BackendMode = "application_services";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static OpenAiReceptionistIngressResult VerifyIncoming(
        ReadOnlyMemory<byte> body, string? deliveryId, string? timestamp, string? signatures,
        string signingSecret, DateTimeOffset utcNow)
    {
        if (body.Length == 0 || body.Length > MaximumWebhookBytes ||
            !Token(deliveryId, 200) || timestamp is not { Length: > 0 and <= 20 } ||
            timestamp.Any(ch => ch is < '0' or > '9') ||
            !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            signatures is not { Length: > 0 and <= 2048 } || string.IsNullOrWhiteSpace(signingSecret))
            return new("invalid_signature");
        var now = utcNow.ToUnixTimeSeconds();
        if (seconds < now - SignatureToleranceSeconds || seconds > now + SignatureToleranceSeconds)
            return new("invalid_signature");

        byte[] key;
        try { key = SigningKey(signingSecret); }
        catch (InvalidOperationException) { return new("invalid_signature"); }
        try
        {
            var prefix = Encoding.UTF8.GetBytes(deliveryId + "." + timestamp + ".");
            var signed = new byte[prefix.Length + body.Length];
            prefix.CopyTo(signed, 0);
            body.Span.CopyTo(signed.AsSpan(prefix.Length));
            var expected = HMACSHA256.HashData(key, signed);
            CryptographicOperations.ZeroMemory(signed);
            var valid = false;
            Span<byte> supplied = stackalloc byte[32];
            foreach (var part in signatures.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!part.StartsWith("v1,", StringComparison.Ordinal)) continue;
                if (Convert.TryFromBase64String(part[3..], supplied, out var length) && length == expected.Length)
                    valid |= CryptographicOperations.FixedTimeEquals(expected, supplied);
            }
            if (!valid) return new("invalid_signature");
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (!String(root, "object", out var kind) || kind != "event" ||
                !String(root, "id", out var eventId) || !Token(eventId, 200) ||
                !String(root, "type", out var type)) return new("invalid_payload");
            // Only the configured Live SIP event owns acceptance. Legacy and Realtime
            // events never create a second acceptance path or substitute a voice model.
            if (type != "live.transport.incoming") return new("ignored", deliveryId, eventId);
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !String(data, "type", out var transport) || transport != "sip" ||
                !String(data, "session_id", out var sessionId) || !Token(sessionId, 200))
                return new("invalid_payload");
            // SIP headers and caller-supplied project/owner fields are deliberately not projected.
            return new("incoming", deliveryId, eventId, sessionId);
        }
        catch (JsonException) { return new("invalid_payload"); }
    }

    internal static void ValidateSigningSecret(string signingSecret)
    {
        var key = SigningKey(signingSecret);
        CryptographicOperations.ZeroMemory(key);
    }

    private static byte[] SigningKey(string signingSecret)
    {
        if (string.IsNullOrWhiteSpace(signingSecret) || signingSecret.Length > 16384)
            throw new InvalidOperationException("The verified webhook signing credential is unavailable.");
        byte[] key;
        try { key = signingSecret.StartsWith("whsec_", StringComparison.Ordinal)
            ? Convert.FromBase64String(signingSecret[6..]) : Encoding.UTF8.GetBytes(signingSecret); }
        catch (FormatException) { throw new InvalidOperationException("The verified webhook signing credential is unavailable."); }
        if (key.Length < 16)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException("The verified webhook signing credential is unavailable.");
        }
        return key;
    }

    internal static byte[] AcceptBody(OpenAiReceptionistSessionSettings settings)
    {
        if (settings.Model != "gpt-live-1" || settings.BackendMode != BackendMode ||
            !Token(settings.Voice, 80) || string.IsNullOrWhiteSpace(settings.ConversationInstructions) ||
            settings.ConversationInstructions.Length > MaximumInstructionCharacters)
            throw new InvalidOperationException("The pinned Live configuration is unsupported or incomplete.");
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            session = new
            {
                type = "live", model = settings.Model, instructions = settings.ConversationInstructions,
                audio = new { output = new { voice = settings.Voice } },
                delegation = new { type = "client" }, store = false,
                input = new[] { new { type = "message", role = "developer", content = new[] {
                    new { type = "input_text", text = "Begin with your configured AI introduction and greeting now, then pause to listen." }
                } } }
            }
        }, Json);
    }

    internal static Uri SessionUri(string sessionId, string operation, bool sideband = false)
    {
        if (!Token(sessionId, 200) || operation is not ("accept" or "reject" or "hangup" or "attach"))
            throw new ArgumentException("Unsupported Live session operation.");
        if (sideband != (operation == "attach")) throw new ArgumentException("The transport does not match the operation.");
        return new Uri((sideband ? "wss" : "https") + "://api.openai.com/v1/live/sessions/" +
            Uri.EscapeDataString(sessionId) + "/" + operation);
    }

    internal static bool TryCumulativeUsage(JsonElement message, out decimal seconds, out bool final)
    {
        seconds = 0m; final = false;
        if (!String(message, "type", out var type) || type is not ("session.usage.updated" or "session.closed") ||
            !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object ||
            !usage.TryGetProperty("seconds", out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDecimal(out seconds) || seconds < 0m)
            return false;
        final = type == "session.closed";
        return true;
    }

    internal static bool String(JsonElement item, string key, out string value)
    {
        value = string.Empty;
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(key, out var property) ||
            property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }

    internal static bool Token(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum &&
        value is not ("." or "..") && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.');
}
