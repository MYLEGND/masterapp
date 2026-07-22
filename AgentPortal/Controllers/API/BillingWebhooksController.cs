using System.Text;
using Domain.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers.API;

[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("api/billing/webhooks")]
public sealed class BillingWebhooksController : ControllerBase
{
    private readonly IBillingWebhookIngressService _billingWebhookIngressService;

    public BillingWebhooksController(IBillingWebhookIngressService billingWebhookIngressService)
    {
        _billingWebhookIngressService = billingWebhookIngressService;
    }

    [HttpPost("square")]
    public async Task<IActionResult> Square(CancellationToken cancellationToken)
    {
        Request.EnableBuffering();

        using var reader = new StreamReader(
            Request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);

        var payload = await reader.ReadToEndAsync(cancellationToken);
        Request.Body.Position = 0;

        var signature = Request.Headers["x-square-hmacsha256-signature"].ToString();
        var notificationUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}{Request.Path}";

        var result = await _billingWebhookIngressService.IngestAsync(
            new BillingWebhookIngressCommand(
                BillingProvider.Square,
                notificationUrl,
                payload,
                signature),
            cancellationToken);

        if (result.Success)
            return Ok(new { ok = true });

        return StatusCode(result.StatusCode, new
        {
            ok = false,
            code = result.SafeErrorCode,
            message = result.SanitizedSummary
        });
    }
}
