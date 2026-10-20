using System.Net.WebSockets;

namespace Infrastructure.Reception;

/// <summary>The existing provider transport boundary, allowing acceptance to exercise the real worker without provider effects.</summary>
internal interface IOpenAiReceptionistClient
{
    Task<OpenAiReceptionistHttpOutcome> AcceptAsync(string projectId, string sessionId, string credential,
        OpenAiReceptionistSessionSettings settings, CancellationToken ct);
    Task<OpenAiReceptionistHttpOutcome> HangupAsync(string projectId, string sessionId, string credential, CancellationToken ct);
    Task<WebSocket> AttachAsync(string projectId, string sessionId, string credential, CancellationToken ct);
}
