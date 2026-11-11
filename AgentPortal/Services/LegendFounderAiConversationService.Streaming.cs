using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace AgentPortal.Services;

public sealed partial class LegendFounderAiConversationService
{
    // Responses SSE belongs to the existing provider transport. A partial answer
    // is provisional; only a terminal response can enter durable conversation history.
    internal static async Task<JsonDocument> ReadResponseStreamAsync(Stream stream,
        Func<string, CancellationToken, ValueTask> onText, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096, leaveOpen: true);
        var data = new StringBuilder();
        var answerCharacters = 0;
        await foreach (var line in ReadResponseLinesAsync(reader, ct))
        {
            if (line.Length != 0)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.AsSpan(5).TrimStart());
                    if (data.Length > 8 * 1024 * 1024)
                        throw new JsonException("Provider stream event exceeds the supported limit.");
                }
                continue;
            }
            if (data.Length == 0) continue;
            var payload = data.ToString();
            data.Clear();
            if (payload == "[DONE]") break;
            using var frame = JsonDocument.Parse(payload);
            var root = frame.RootElement;
            var type = root.TryGetProperty("type", out var kind) ? kind.GetString() : null;
            if (type == "response.output_text.delta" && root.TryGetProperty("delta", out var delta))
            {
                if (delta.GetString() is { Length: > 0 } text)
                {
                    answerCharacters += text.Length;
                    if (answerCharacters > 2 * 1024 * 1024)
                        throw new JsonException("Provider answer exceeds the supported limit.");
                    await onText(text, ct);
                }
            }
            else if (type is "response.completed" or "response.incomplete" or "response.failed")
            {
                if (!root.TryGetProperty("response", out var response))
                    throw new JsonException("Provider terminal stream event has no response.");
                return JsonDocument.Parse(response.GetRawText());
            }
            else if (type == "error")
                throw new JsonException("Provider reported a stream error.");
        }
        throw new JsonException("Provider stream ended without a terminal response.");
    }

    private static async IAsyncEnumerable<string> ReadResponseLinesAsync(StreamReader reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var total = 0;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            total += count;
            if (total > 32 * 1024 * 1024) throw new JsonException("Provider stream exceeds the supported limit.");
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == '\n')
                {
                    if (line.Length > 0 && line[^1] == '\r') line.Length--;
                    yield return line.ToString();
                    line.Clear();
                }
                else
                {
                    if (line.Length >= 1024 * 1024) throw new JsonException("Provider stream line exceeds the supported limit.");
                    line.Append(buffer[i]);
                }
            }
        }
        // EOF is not an SSE dispatch boundary. The caller rejects missing terminal events.
    }
}
