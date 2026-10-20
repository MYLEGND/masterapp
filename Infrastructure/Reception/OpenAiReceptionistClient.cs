using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;

namespace Infrastructure.Reception;

internal sealed record OpenAiReceptionistHttpOutcome(bool Confirmed, int? StatusCode, string Receipt);

/// <summary>Single-attempt provider controls. A 200 accepts a command; it does not prove call closure, billing or inquiry success.</summary>
internal sealed class OpenAiReceptionistClient(IHttpClientFactory clients) : IOpenAiReceptionistClient
{
    internal const string ClientName = "Receptionist.OpenAi";

    public Task<OpenAiReceptionistHttpOutcome> AcceptAsync(string projectId, string sessionId, string credential,
        OpenAiReceptionistSessionSettings settings, CancellationToken ct) =>
        ControlAsync(projectId, sessionId, credential, "accept", OpenAiReceptionistProtocol.AcceptBody(settings), ct);

    internal Task<OpenAiReceptionistHttpOutcome> RejectAsync(string projectId, string sessionId, string credential,
        CancellationToken ct) => ControlAsync(projectId, sessionId, credential, "reject",
            JsonSerializer.SerializeToUtf8Bytes(new { status_code = 503 }), ct);

    public Task<OpenAiReceptionistHttpOutcome> HangupAsync(string projectId, string sessionId, string credential,
        CancellationToken ct) => ControlAsync(projectId, sessionId, credential, "hangup", null, ct);

    private async Task<OpenAiReceptionistHttpOutcome> ControlAsync(string projectId, string sessionId, string credential,
        string operation, byte[]? body, CancellationToken ct)
    {
        using var request = Request(projectId, sessionId, credential, operation, HttpMethod.Post);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        try
        {
            using var client = clients.CreateClient(ClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var ids = response.Headers.TryGetValues("x-request-id", out var values) ? values.Take(2).ToArray() : [];
            var id = ids.Length == 1 ? ids[0] : null;
            var receipt = "openai_http:" + operation + ":" + (int)response.StatusCode + ":" +
                (OpenAiReceptionistProtocol.Token(id, 200) ? id : "request_id_unavailable");
            return new(response.StatusCode == System.Net.HttpStatusCode.OK, (int)response.StatusCode, receipt);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            // No response means the provider may have accepted the original request.
            // Durable coordination must reconcile that session, never resubmit acceptance.
            return new(false, null, "openai_http:" + operation + ":outcome_unknown");
        }
    }

    public async Task<WebSocket> AttachAsync(string projectId, string sessionId, string credential,
        CancellationToken ct)
    {
        ValidateCredentials(projectId, credential);
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + credential);
        socket.Options.SetRequestHeader("OpenAI-Project", projectId);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        try
        {
            await socket.ConnectAsync(OpenAiReceptionistProtocol.SessionUri(sessionId, "attach", sideband: true), ct);
            // An attached SIP sideband belongs to an accepted session. Never send session.start.
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    private static HttpRequestMessage Request(string projectId, string sessionId, string credential,
        string operation, HttpMethod method)
    {
        ValidateCredentials(projectId, credential);
        var request = new HttpRequestMessage(method, OpenAiReceptionistProtocol.SessionUri(sessionId, operation));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.Add("OpenAI-Project", projectId);
        return request;
    }

    internal static void ValidateCredentials(string projectId, string credential)
    {
        if (!OpenAiReceptionistProtocol.Token(projectId, 200) || string.IsNullOrWhiteSpace(credential) ||
            credential.Length > 4096 || credential.Any(char.IsWhiteSpace) || credential.Any(char.IsControl))
            throw new InvalidOperationException("The verified provider credential binding is unavailable.");
    }
}
