using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Infrastructure.Reception;

internal static class ReceptionistSidebandProtocol
{
    // A conservative UTF-8 byte bound is below the provider's 500-token append limit,
    // including text whose characters consume multiple tokens. The application chunks complete facts.
    internal const int MaximumAppendBytes = 480;

    internal static byte[] Close(string eventId)
    {
        if (!OpenAiReceptionistProtocol.Token(eventId, 200))
            throw new ArgumentException("A valid close event identity is required.");
        return JsonSerializer.SerializeToUtf8Bytes(new { type = "session.close", event_id = eventId });
    }

    internal static byte[] Append(string eventId, string? delegationId, string type, string content)
    {
        if (!OpenAiReceptionistProtocol.Token(eventId, 200) ||
            (delegationId is not null && !OpenAiReceptionistProtocol.Token(delegationId, 200)) ||
            type is not ("session.commentary.append" or "session.thinking.append" or "session.instructions.append") ||
            string.IsNullOrWhiteSpace(content) || Encoding.UTF8.GetByteCount(content) > MaximumAppendBytes)
            throw new ArgumentException("A bounded supported Live context update is required.");
        return JsonSerializer.SerializeToUtf8Bytes(new { type, event_id = eventId, delegation_id = delegationId, content });
    }

    internal static async Task<JsonDocument?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        using var body = new MemoryStream();
        var chunk = new byte[4096];
        ValueWebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(chunk.AsMemory(), ct);
            if (part.MessageType == WebSocketMessageType.Close) return null;
            if (part.MessageType != WebSocketMessageType.Text || body.Length + part.Count > OpenAiReceptionistProtocol.MaximumEventBytes)
                throw new InvalidOperationException("The Live sideband event is unsupported or exceeds its supported size.");
            await body.WriteAsync(chunk.AsMemory(0, part.Count), ct);
        } while (!part.EndOfMessage);
        return JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }

    internal static bool SessionMatches(JsonElement message, string sessionId, string model, bool required, bool requireModel = false)
    {
        if (!message.TryGetProperty("session", out var session)) return !required;
        return OpenAiReceptionistProtocol.String(session, "id", out var id) && id == sessionId &&
            ((!requireModel && !session.TryGetProperty("model", out _)) ||
                (OpenAiReceptionistProtocol.String(session, "model", out var actualModel) && actualModel == model));
    }
}
