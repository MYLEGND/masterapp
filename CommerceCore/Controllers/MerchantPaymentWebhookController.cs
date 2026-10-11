using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Infrastructure.Commerce.Payments;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("commerce/payments/webhooks")]
public sealed class MerchantPaymentWebhookController(MerchantCheckoutService checkout, MerchantPaymentCompletionService completion, IConfiguration configuration) : ControllerBase
{
    [HttpPost("{provider}/{environment}")]
    [RequestSizeLimit(1048576)]
    public async Task<IActionResult> Receive(string provider, string environment, CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        var headers = Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        // Signature verification uses the registered endpoint, never an untrusted Host header.
        var endpoint = configuration[$"MerchantPayments:{provider}:{environment}:WebhookUrl"];
        if (string.IsNullOrWhiteSpace(endpoint)) return StatusCode(503);
        try
        {
            var attempt = await checkout.HandleWebhookAsync(provider, environment, body, headers, endpoint, ct);
            if (attempt is null) return BadRequest();
            await completion.CompleteAsync(attempt.Value, ct);
            return Ok();
        }
        catch (MerchantPaymentException) { return BadRequest(); }
    }
}
