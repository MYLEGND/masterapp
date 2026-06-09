using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using System.Net.Mail;

namespace ProtectWebsite.Services.Communication;

public sealed class GraphProtectEmailSender : IProtectEmailSender
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<GraphProtectEmailSender> _logger;

    public GraphProtectEmailSender(
        IConfiguration configuration,
        ILogger<GraphProtectEmailSender> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<bool> TrySendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string? textBody = null,
        string? replyToEmail = null,
        bool saveToSentItems = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            _logger.LogWarning("Protect email skipped: missing recipient. subject={Subject}", subject);
            return false;
        }

        var tenantId = _configuration["AzureAd:TenantId"];
        var clientId = _configuration["AzureAd:ClientId"];
        var clientSecret = _configuration["AzureAd:ClientSecret"];
        var senderEmail = _configuration["Contact:SenderEmail"] ?? "connect@mylegnd.com";

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret) ||
            string.IsNullOrWhiteSpace(senderEmail))
        {
            _logger.LogError(
                "Protect email failed: missing config. hasTenant={HasTenant} hasClient={HasClient} hasSecret={HasSecret} sender={Sender}",
                !string.IsNullOrWhiteSpace(tenantId),
                !string.IsNullOrWhiteSpace(clientId),
                !string.IsNullOrWhiteSpace(clientSecret),
                senderEmail);

            return false;
        }

        try
        {
            _ = new MailAddress(toEmail.Trim());

            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var graphClient = new GraphServiceClient(credential);

            var message = new Message
            {
                Subject = subject,
                Body = new ItemBody
                {
                    ContentType = BodyType.Html,
                    Content = htmlBody
                },
                ToRecipients =
                [
                    new Recipient
                    {
                        EmailAddress = new EmailAddress
                        {
                            Address = toEmail.Trim()
                        }
                    }
                ]
            };

            if (!string.IsNullOrWhiteSpace(replyToEmail))
            {
                message.ReplyTo =
                [
                    new Recipient
                    {
                        EmailAddress = new EmailAddress
                        {
                            Address = replyToEmail.Trim()
                        }
                    }
                ];
            }

            await graphClient.Users[senderEmail.Trim()].SendMail.PostAsync(
                new SendMailPostRequestBody
                {
                    Message = message,
                    SaveToSentItems = saveToSentItems
                },
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Protect email sent. to={Recipient} sender={Sender} subject={Subject}",
                toEmail,
                senderEmail,
                subject);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Protect email failed. to={Recipient} sender={Sender} subject={Subject} tenant={TenantId} client={ClientId} error={Error}",
                toEmail,
                senderEmail,
                subject,
                tenantId,
                clientId,
                ex.Message);

            return false;
        }
    }
}
