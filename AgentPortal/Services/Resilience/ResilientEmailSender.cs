using System.Net.Mail;
using AgentPortal.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentPortal.Services.Resilience;

/// <summary>
/// Decorator that applies simple retries and timeout to IEmailSender when enabled via flag.
/// Defaults to pass-through so behavior is unchanged until configured.
/// </summary>
public sealed class ResilientEmailSender : IEmailSender
{
    private readonly IEmailSender _inner;
    private readonly ILogger<ResilientEmailSender> _logger;
    private readonly AppFeatureFlags _flags;

    public ResilientEmailSender(IEmailSender inner, ILogger<ResilientEmailSender> logger, IOptions<AppFeatureFlags> flags)
    {
        _inner = inner;
        _logger = logger;
        _flags = flags.Value;
    }

    public async Task<bool> TrySendAsync(string toEmail, string subject, string? htmlBody, string? textBody = null, string? fromEmail = null, string? fromDisplayName = null, string? replyToEmail = null)
    {
        if (!_flags.ResiliencePoliciesEnabled)
            return await _inner.TrySendAsync(toEmail, subject, htmlBody, textBody, fromEmail, fromDisplayName, replyToEmail);

        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var task = _inner.TrySendAsync(toEmail, subject, htmlBody, textBody, fromEmail, fromDisplayName, replyToEmail);
                var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));
                if (completed != task)
                    throw new TimeoutException("Email send timed out.");

                return task.Result;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                _logger.LogWarning(ex, "Email send attempt {Attempt} failed, retrying...", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }

        return false;
    }
}
