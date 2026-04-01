using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using System.Text.Encodings.Web;

namespace ParfaitApp.Services;

public interface IGraphMailService
{
    Task SendContactEmailsAsync(
        string firstName,
        string lastName,
        string email,
        string phone,
        string message,
        string requestIp);
}

public class GraphMailService : IGraphMailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<GraphMailService> _logger;

    public GraphMailService(IConfiguration config, ILogger<GraphMailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    private GraphServiceClient BuildClient()
    {
        // Prefer GraphMail section; fall back to AzureAd if needed
        var tenantId = _config["GraphMail:TenantId"] ?? _config["AzureAd:TenantId"] ?? "";
        var clientId = _config["GraphMail:ClientId"] ?? _config["AzureAd:ClientId"] ?? "";
        var clientSecret = _config["GraphMail:ClientSecret"] ?? _config["AzureAd:ClientSecret"] ?? "";

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("Missing TenantId/ClientId/ClientSecret in GraphMail or AzureAd config.");
        }

        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        return new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
    }

    public async Task SendContactEmailsAsync(
        string firstName,
        string lastName,
        string email,
        string phone,
        string message,
        string requestIp)
    {
        var senderUpn = (_config["GraphMail:SenderUpn"] ?? _config["Contact:SenderEmail"] ?? "").Trim();
        var inbox = (_config["Contact:RecipientEmail"] ?? _config["GraphMail:NotifyInbox"] ?? "").Trim();
        var siteName = (_config["Contact:WebsiteName"] ?? "Shop Parfait").Trim();

        if (string.IsNullOrWhiteSpace(senderUpn))
            throw new InvalidOperationException("Missing SenderUpn/Contact:SenderEmail config.");
        if (string.IsNullOrWhiteSpace(inbox))
            throw new InvalidOperationException("Missing Contact:RecipientEmail/NotifyInbox config.");

        var graph = BuildClient();

        // Encode inputs to avoid HTML injection
        var enc = HtmlEncoder.Default;
        var safeFullName = enc.Encode($"{firstName} {lastName}".Trim());
        var safeEmail = enc.Encode(email.Trim());
        var safePhone = enc.Encode(phone.Trim());
        var safeIp = enc.Encode(requestIp.Trim());
        var safeMsg = enc.Encode(message.Trim()).Replace("\n", "<br/>");

        // 1) Internal email to your inbox
        var internalSubject = $"[{siteName} Contact] {firstName} {lastName}";
        var internalHtml =
$@"
<div style='font-family: Inter, Arial, sans-serif; line-height:1.6; color:#111;'>
  <h2 style='margin:0 0 12px;'>New Contact Request — {enc.Encode(siteName)}</h2>

  <div style='padding:14px 16px; border:1px solid #926950; border-radius:14px; background:#fff;'>
    <p style='margin:0 0 8px;'><strong>Name:</strong> {safeFullName}</p>
    <p style='margin:0 0 8px;'><strong>Email:</strong> {safeEmail}</p>
    <p style='margin:0 0 8px;'><strong>Phone:</strong> {safePhone}</p>
    <p style='margin:0 0 8px;'><strong>IP:</strong> {safeIp}</p>

    <hr style='border:none; border-top:1px solid #eee; margin:12px 0;'/>
    <p style='margin:0;'><strong>Message:</strong><br/>{safeMsg}</p>
  </div>

  <p style='margin-top:14px; color:#666; font-size:13px;'>
    Submitted from the Parfait Contact Us page.
  </p>
</div>
";

        await SendMailAsync(graph, senderUpn, inbox, internalSubject, internalHtml);

        // 2) Confirmation to the client (optional but recommended)
        var clientSubject = $"We received your message — {siteName}";
        var clientHtml =
$@"
<div style='font-family: Inter, Arial, sans-serif; line-height:1.65; color:#111;'>
  <h2 style='margin:0 0 10px;'>Hey {enc.Encode(firstName.Trim())},</h2>

  <p style='margin:0 0 12px;'>
    Thank you for reaching out to <strong>{enc.Encode(siteName)}</strong>. We received your message and will respond as soon as possible.
  </p>

  <div style='padding:14px 16px; border:1px solid #926950; border-radius:14px; background:#fff;'>
    <p style='margin:0 0 8px; color:#926950; font-weight:700;'>Your message:</p>
    <p style='margin:0; color:#333;'>{safeMsg}</p>
  </div>

  <p style='margin:14px 0 0; color:#444;'>
    Build your Parfait Body. Own your power.
  </p>

  <p style='margin:14px 0 0; color:#666; font-size:13px;'>
    If you didn’t submit this request, you can ignore this email.
  </p>
</div>
";

        try
        {
            await SendMailAsync(graph, senderUpn, email.Trim(), clientSubject, clientHtml);
        }
        catch (Exception ex)
        {
            // Don't fail the whole request if the client confirmation fails
            _logger.LogWarning(ex, "Client confirmation email failed to send. ClientEmail:{ClientEmail}", email);
        }
    }

    private static async Task SendMailAsync(GraphServiceClient graph, string senderUpn, string to, string subject, string htmlBody)
    {
        var msg = new Message
        {
            Subject = subject,
            Body = new ItemBody
            {
                ContentType = BodyType.Html,
                Content = htmlBody
            },
            ToRecipients = new List<Recipient>
            {
                new Recipient
                {
                    EmailAddress = new EmailAddress { Address = to }
                }
            }
        };

        var body = new SendMailPostRequestBody
        {
            Message = msg,
            SaveToSentItems = true
        };

        await graph.Users[senderUpn].SendMail.PostAsync(body);
    }
}
