using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Shared.Analytics;

namespace Infrastructure.Reception;

/// <summary>Authenticated provider events enter the existing durable admission authority; no call is accepted in this request.</summary>
[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[EnableRateLimiting("ingest")]
[Route("api/receptionist/openai/{ingressKey}")]
public sealed class ReceptionistIngressController : ControllerBase
{
    private readonly IServiceProvider _services;
    public ReceptionistIngressController(IServiceProvider services) => _services = services;

    [HttpPost]
    [RequestSizeLimit(OpenAiReceptionistProtocol.MaximumWebhookBytes)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Receive(string ingressKey, CancellationToken ct)
    {
        var runtime = _services.GetService<ReceptionistRuntimeAuthority>();
        if (runtime is null) return NotFound();
        if (!runtime.Enabled) return StatusCode(503);
        if (!OpenAiReceptionistProtocol.Token(ingressKey, 100)) return NotFound();
        var admission = _services.GetRequiredService<ReceptionistAdmissionService>();
        var current = await admission.GetIngressBindingAsync(MarketingDestinationKeys.OpenAi, ingressKey, ct);
        if (current is null) return NotFound();
        if (Request.ContentLength > OpenAiReceptionistProtocol.MaximumWebhookBytes) return StatusCode(413);
        string signingSecret;
        try { signingSecret = runtime.Credential(current.Owner, current.Binding, webhook: true); }
        catch (Exception error) when (error is InvalidOperationException or System.Security.Cryptography.CryptographicException)
        { return StatusCode(503); }
        using var body = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (body.Length + read > OpenAiReceptionistProtocol.MaximumWebhookBytes) return StatusCode(413);
            await body.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        var verified = OpenAiReceptionistProtocol.VerifyIncoming(body.ToArray(), Header("webhook-id"),
            Header("webhook-timestamp"), Header("webhook-signature"), signingSecret,
            _services.GetRequiredService<TimeProvider>().GetUtcNow());
        if (verified.State == "invalid_signature") return Unauthorized();
        if (verified.State == "invalid_payload") return BadRequest();
        if (verified.State == "ignored") return Ok();
        var controlledTest = runtime.IsControlledTest(current.Owner, current.Binding);
        try { _ = ReceptionistRuntimeAuthority.Ready(current.Provider, current.Configuration, current.Binding, controlledTest); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.Text.Json.JsonException) { return StatusCode(503); }
        _ = await admission.ReserveAsync(new TrustedReceptionistIngress(current.Provider, ingressKey,
            current.Binding.ProviderProjectKey, verified.SessionId!, verified.ProviderEventId!,
            controlledTest, current.MarketingConnectionId, current.ConfigurationId, current.Owner.Key), ct);
        // A persisted denial grants no provider I/O. The accepted carrier/provider fallback owns
        // unanswered calls, including its separately reconciled ringing and fallback costs.
        // Success acknowledges durable admission only; the worker separately claims acceptance.
        return Ok();
    }

    private string? Header(string name) => Request.Headers.TryGetValue(name, out var values) && values.Count == 1
        ? values[0] : null;
}
