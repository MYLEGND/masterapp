using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace AgentPortal.Services;

public sealed class SmtpEmailSender : IEmailSender
{
    private static readonly object GraphLock = new();
    private static GraphServiceClient? _cachedGraph;
    private static string? _cachedGraphKey;

    private readonly EmailOptions _options;
    private readonly IConfiguration _config;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailOptions> options, IConfiguration config, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value ?? new EmailOptions();
        _config = config;
        _logger = logger;
    }

    public async Task<bool> TrySendAsync(string toEmail, string subject, string? htmlBody, string? textBody = null, string? fromEmail = null, string? fromDisplayName = null, string? replyToEmail = null)
    {
        var recipients = ParseRecipients(toEmail);
        if (recipients.Count == 0)
        {
            _logger.LogWarning("Email send skipped because recipient was empty.");
            return false;
        }

        // Sender: default to connect@mylegnd.com if not configured
        var senderAddress = _options.From ?? "connect@mylegnd.com";

        var smtpConfigured = !string.IsNullOrWhiteSpace(_options.Host);
        if (smtpConfigured)
        {
            try
            {
                using var message = new MailMessage
                {
                    From = new MailAddress(string.IsNullOrWhiteSpace(fromEmail) ? (_options.From ?? "connect@mylegnd.com") : fromEmail.Trim(), string.IsNullOrWhiteSpace(fromDisplayName) ? null : fromDisplayName.Trim()),
                    Subject = subject ?? "",
                    Body = htmlBody ?? textBody ?? "",
                    IsBodyHtml = !string.IsNullOrWhiteSpace(htmlBody)
                };

                foreach (var r in recipients)
                {
                    message.To.Add(new MailAddress(r));
                }

                var resolvedReplyTo = string.IsNullOrWhiteSpace(replyToEmail) ? fromEmail : replyToEmail;
                if (!string.IsNullOrWhiteSpace(resolvedReplyTo))
                {
                    message.ReplyToList.Add(new MailAddress(
                        resolvedReplyTo.Trim(),
                        string.IsNullOrWhiteSpace(fromDisplayName) ? null : fromDisplayName.Trim()));
                }

                if (!string.IsNullOrWhiteSpace(textBody) && message.IsBodyHtml)
                {
                    var alt = AlternateView.CreateAlternateViewFromString(textBody, null, "text/plain");
                    message.AlternateViews.Add(alt);
                }

                using var client = new SmtpClient(_options.Host, _options.Port)
                {
                    EnableSsl = _options.EnableSsl
                };

                if (!string.IsNullOrWhiteSpace(_options.Username))
                {
                    client.Credentials = new NetworkCredential(_options.Username, _options.Password);
                }

                await client.SendMailAsync(message);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SMTP send failed for {To}. Falling back to Graph email.", toEmail);
            }
        }
        else
        {
            _logger.LogInformation("SMTP not configured (Email:Host/From missing). Attempting Graph email fallback.");
        }

        try
        {
            var sender = GetSetting("Provisioning:SenderMailbox", "Provisioning__SenderMailbox")
                ?? senderAddress;

            if (string.IsNullOrWhiteSpace(sender))
            {
                _logger.LogWarning("Graph fallback unavailable: missing sender mailbox (Provisioning:SenderMailbox / Email:From).");
                return false;
            }

            var graph = BuildGraphClient();
            if (graph == null)
            {
                _logger.LogWarning("Graph fallback unavailable: missing Graph credential configuration.");
                return false;
            }

            var bodyContent = htmlBody ?? WebUtility.HtmlEncode(textBody ?? string.Empty).Replace("\n", "<br/>");
            var bodyType = !string.IsNullOrWhiteSpace(htmlBody) ? BodyType.Html : BodyType.Text;

            var message = new Message
            {
                Subject = subject ?? "",
                Body = new ItemBody { ContentType = bodyType, Content = bodyContent },
                ToRecipients = recipients.Select(r => new Recipient { EmailAddress = new EmailAddress { Address = r } }).ToList()
            };

            await graph.Users[sender].SendMail.PostAsync(new SendMailPostRequestBody
            {
                Message = message,
                SaveToSentItems = true
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {To} via SMTP and Graph fallback.", toEmail);
            return false;
        }
    }

    private string? GetSetting(params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = _config[key];
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private GraphServiceClient? BuildGraphClient()
    {
        var tenantId = GetSetting(
            "GraphProvisioning:TenantId", "GraphProvisioning__TenantId",
            "AzureAd:TenantId", "AzureAd__TenantId");

        var clientId = GetSetting(
            "GraphProvisioning:ClientId", "GraphProvisioning__ClientId",
            "AzureAd:ClientId", "AzureAd__ClientId");

        var clientSecret = GetSetting(
            "GraphProvisioning:ClientSecret", "GraphProvisioning__ClientSecret",
            "AzureAd:ClientSecret", "AzureAd__ClientSecret");

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return null;

        var key = $"{tenantId}|{clientId}|{clientSecret}";

        lock (GraphLock)
        {
            if (_cachedGraph != null && string.Equals(_cachedGraphKey, key, StringComparison.Ordinal))
                return _cachedGraph;

            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            _cachedGraph = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
            _cachedGraphKey = key;
            return _cachedGraph;
        }
    }

    private static List<string> ParseRecipients(string input)
    {
        return (input ?? string.Empty)
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.Trim())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed class EmailOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? From { get; set; }
    public string? FromName { get; set; }
}
