using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Infrastructure.Reception;

namespace AgentPortal.Services;

public sealed record BusinessAssistantDictationRequest(string Workspace, Guid ConfigurationRevision,
    Guid OperationId, string AudioBase64);

public sealed partial class LegendFounderAiConversationService
{
    // A canonical, uncompressed mono PCM envelope makes duration independently
    // verifiable. Compressed audio and client duration claims are not cost bounds.
    internal static void ValidateDictationWave(byte[] audio)
    {
        if (audio.Length < 3244 || audio.Length > 1_920_044 ||
            !audio.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !audio.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(audio.AsSpan(4)) != audio.Length - 8 ||
            BinaryPrimitives.ReadUInt32LittleEndian(audio.AsSpan(16)) != 16 ||
            BinaryPrimitives.ReadUInt16LittleEndian(audio.AsSpan(20)) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(audio.AsSpan(22)) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(audio.AsSpan(24)) != 16000 ||
            BinaryPrimitives.ReadUInt32LittleEndian(audio.AsSpan(28)) != 32000 ||
            BinaryPrimitives.ReadUInt16LittleEndian(audio.AsSpan(32)) != 2 ||
            BinaryPrimitives.ReadUInt16LittleEndian(audio.AsSpan(34)) != 16 ||
            !audio.AsSpan(36, 4).SequenceEqual("data"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(audio.AsSpan(40)) != audio.Length - 44 || (audio.Length - 44) % 2 != 0)
            throw new ArgumentException("Record between 0.1 and 60 seconds of supported microphone audio.");
    }

    public async Task<string> TranscribeBusinessAsync(ClaimsPrincipal principal, BusinessAssistantDictationRequest request, CancellationToken ct)
    {
        if (request.OperationId == Guid.Empty || request.AudioBase64 is null || request.AudioBase64.Length > 2_560_060)
            throw new ArgumentException("A bounded recording and operation are required.");
        byte[] audio;
        try { audio = Convert.FromBase64String(request.AudioBase64); }
        catch (FormatException) { throw new ArgumentException("Recording format is invalid."); }
        ValidateDictationWave(audio);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45)); ct = timeout.Token;
        await using var scope = _historyScopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var (_, owner) = await AuthorizeBusinessAsync(principal, request.Workspace,
            services.GetRequiredService<MasterAppDbContext>(), ct);
        var store = services.GetRequiredService<MarketingConnectionStore>();
        var snapshot = await store.GetAssistantConnectionAsync(owner, "openai", ct);
        if (snapshot.Configuration is not { Enabled: true, DictationEnabled: true, TranscriptionModel: not null } config ||
            snapshot.Revision != request.ConfigurationRevision)
            throw new InvalidOperationException("Enable reviewed dictation for this workspace and reload its configuration.");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "dictation:" + owner.Key + ":" + snapshot.Revision + ":" + Convert.ToHexStringLower(SHA256.HashData(audio)))));
        var evidence = await services.GetRequiredService<ReceptionistOperatorService>().ReadAssistantFundingEvidenceAsync(snapshot, ct);
        var admission = await services.GetRequiredService<ReceptionistAdmissionService>().ReserveAssistantAsync(owner, snapshot,
            evidence, request.OperationId, fingerprint, async db => { await AuthorizeBusinessAsync(principal, owner.Key, db, ct); }, ct);
        if (!admission.Admitted) throw new InvalidOperationException("Dictation funding admission denied: " + admission.Reason);
        var credential = await store.GetAssistantCredentialAsync(owner, "openai", snapshot.Revision, ct)
            ?? throw new InvalidOperationException("The provider connection changed. Reload before recording again.");
        await AuthorizeBusinessAsync(principal, owner.Key, services.GetRequiredService<MasterAppDbContext>(), ct);
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        message.Headers.Add("OpenAI-Project", config.ProjectId);
        if (config.OrganizationId is not null) message.Headers.Add("OpenAI-Organization", config.OrganizationId);
        var form = new MultipartFormDataContent(); message.Content = form;
        form.Add(new StringContent(config.TranscriptionModel), "model");
        form.Add(new StringContent("json"), "response_format");
        var part = new ByteArrayContent(audio); part.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(part, "file", "dictation.wav");
        // One admitted attempt. Unknown cost keeps the existing reservation held;
        // neither raw audio nor an unsent draft is persisted by this endpoint.
        using var client = _httpClientFactory.CreateClient("OpenAI");
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Transcription failed; no automatic retry was made.");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var json = JsonDocument.Parse(await ReadBoundedMediaAsync(body, 100_000, ct));
        var text = json.RootElement.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 20000) throw new InvalidOperationException("No bounded transcript was returned. Try a shorter recording.");
        await AuthorizeBusinessAsync(principal, owner.Key, services.GetRequiredService<MasterAppDbContext>(), ct);
        if (await store.GetAssistantCredentialAsync(owner, "openai", snapshot.Revision, ct) is null)
            throw new InvalidOperationException("Provider connection changed during transcription.");
        return text;
    }
}
