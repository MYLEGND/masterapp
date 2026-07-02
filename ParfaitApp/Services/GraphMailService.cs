using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using System.Text.Encodings.Web;
using ParfaitApp.Models;

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

    Task SendOrderReceiptAsync(ParfaitOrderRecord order, CancellationToken ct = default);
    Task SendOrderNotificationAsync(ParfaitOrderRecord order, CancellationToken ct = default);
    Task SendAutomationEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default);
    Task SendParfaitTeamInviteAsync(
        string toEmail,
        string displayName,
        string roleLabel,
        IReadOnlyCollection<string> allowedPageTitles,
        string loginUrl,
        string invitedBy,
        CancellationToken ct = default);
}

public class GraphMailService : IGraphMailService
{
    private const string DefaultParfaitOrdersInbox = "parfait@mylegnd.com";
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
        var senderUpn = ResolveSenderUpn();
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

        await SendMailAsync(graph, senderUpn, [inbox], internalSubject, internalHtml);

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
            await SendMailAsync(graph, senderUpn, [email.Trim()], clientSubject, clientHtml);
        }
        catch (Exception ex)
        {
            // Don't fail the whole request if the client confirmation fails
            _logger.LogWarning(ex, "Client confirmation email failed to send. ClientEmail:{ClientEmail}", email);
        }
    }


    public async Task SendOrderReceiptAsync(ParfaitOrderRecord order, CancellationToken ct = default)
    {
        var senderUpn = ResolveSenderUpn();
        var siteName = (_config["Contact:WebsiteName"] ?? "Shop Parfait").Trim();

        if (string.IsNullOrWhiteSpace(senderUpn))
            throw new InvalidOperationException("Missing SenderUpn/Contact:SenderEmail config.");

        var graph = BuildClient();
        var enc = HtmlEncoder.Default;

        static string Money(int cents) => "$" + (cents / 100m).ToString("0.00");

        var itemRows = string.Join("", order.Items.Select(item =>
$@"
<tr>
  <td style='padding:10px 0;border-bottom:1px solid #eee;'>
    <strong>{enc.Encode(item.Name)}</strong><br/>
    <span style='color:#666;'>Size {enc.Encode(item.Size)} · Qty {item.Quantity}</span>
  </td>
  <td style='padding:10px 0;border-bottom:1px solid #eee;text-align:right;'>{Money(item.LineTotalCents)}</td>
</tr>"));

        var html =
$@"
<div style='font-family:Inter,Arial,sans-serif;line-height:1.6;color:#111;'>
  <h2 style='margin:0 0 10px;'>Order Confirmed — {enc.Encode(siteName)}</h2>
  <p style='margin:0 0 14px;'>Thank you for your order, {enc.Encode(order.FirstName)}.</p>

  <div style='padding:14px 16px;border:1px solid #926950;border-radius:14px;background:#fff;'>
    <p style='margin:0 0 8px;'><strong>Order Number:</strong> {enc.Encode(order.OrderNumber)}</p>
    <p style='margin:0 0 8px;'><strong>Payment Status:</strong> {enc.Encode(order.PaymentStatus)}</p>
    {(order.DiscountCents > 0 ? $"<p style='margin:0 0 8px;'><strong>Discount:</strong> -{Money(order.DiscountCents)}{(string.IsNullOrWhiteSpace(order.DiscountCode) ? "" : $" ({enc.Encode(order.DiscountCode)})")}</p>" : "")}
    <p style='margin:0;'><strong>Total:</strong> {Money(order.TotalCents)}</p>
  </div>

  <h3 style='margin:18px 0 8px;'>Items Purchased</h3>
  <table style='width:100%;border-collapse:collapse;'>{itemRows}</table>

  <h3 style='margin:18px 0 8px;'>Shipping To</h3>
  <p style='margin:0;color:#333;'>
    {enc.Encode(order.FirstName)} {enc.Encode(order.LastName)}<br/>
    {enc.Encode(order.AddressLine1)}<br/>
    {(string.IsNullOrWhiteSpace(order.AddressLine2) ? "" : enc.Encode(order.AddressLine2) + "<br/>")}
    {enc.Encode(order.City)}, {enc.Encode(order.State)} {enc.Encode(order.PostalCode)}
  </p>

  <p style='margin-top:18px;color:#666;font-size:13px;'>
    Your payment was processed securely through Square.
  </p>
</div>";

        await SendMailAsync(graph, senderUpn, [order.Email], $"Your {siteName} order {order.OrderNumber}", html, ct);
    }

    public async Task SendOrderNotificationAsync(ParfaitOrderRecord order, CancellationToken ct = default)
    {
        var senderUpn = ResolveSenderUpn();
        var siteName = (_config["Contact:WebsiteName"] ?? "Shop Parfait").Trim();
        var inboxes = ResolveRecipients(
            DefaultParfaitOrdersInbox,
            _config["Commerce:OrdersInbox"],
            _config["Contact:RecipientEmail"],
            _config["GraphMail:NotifyInbox"]);

        if (string.IsNullOrWhiteSpace(senderUpn))
            throw new InvalidOperationException("Missing SenderUpn/Contact:SenderEmail config.");
        if (inboxes.Count == 0)
            throw new InvalidOperationException("Missing Commerce:OrdersInbox/Contact:RecipientEmail/NotifyInbox config.");

        var graph = BuildClient();
        var enc = HtmlEncoder.Default;

        static string Money(int cents) => "$" + (cents / 100m).ToString("0.00");

        var items = string.Join("<br/>", order.Items.Select(item =>
            $"{enc.Encode(item.Name)} — Size {enc.Encode(item.Size)} — Qty {item.Quantity} — {Money(item.LineTotalCents)}"));

        var html =
$@"
<div style='font-family:Inter,Arial,sans-serif;line-height:1.6;color:#111;'>
  <h2 style='margin:0 0 10px;'>New Paid Order — {enc.Encode(siteName)}</h2>

  <div style='padding:14px 16px;border:1px solid #926950;border-radius:14px;background:#fff;'>
    <p style='margin:0 0 8px;'><strong>Order:</strong> {enc.Encode(order.OrderNumber)}</p>
    <p style='margin:0 0 8px;'><strong>Customer:</strong> {enc.Encode(order.FirstName)} {enc.Encode(order.LastName)}</p>
    <p style='margin:0 0 8px;'><strong>Email:</strong> {enc.Encode(order.Email)}</p>
    <p style='margin:0 0 8px;'><strong>Phone:</strong> {enc.Encode(order.Phone)}</p>
    {(order.DiscountCents > 0 ? $"<p style='margin:0 0 8px;'><strong>Discount:</strong> -{Money(order.DiscountCents)}{(string.IsNullOrWhiteSpace(order.DiscountCode) ? "" : $" ({enc.Encode(order.DiscountCode)})")}</p>" : "")}
    <p style='margin:0 0 8px;'><strong>Total:</strong> {Money(order.TotalCents)}</p>
    <p style='margin:0;'><strong>Square Payment:</strong> {enc.Encode(order.SquarePaymentId ?? "")}</p>
  </div>

  <h3 style='margin:18px 0 8px;'>Items</h3>
  <p>{items}</p>

  <h3 style='margin:18px 0 8px;'>Shipping</h3>
  <p>
    {enc.Encode(order.AddressLine1)}<br/>
    {(string.IsNullOrWhiteSpace(order.AddressLine2) ? "" : enc.Encode(order.AddressLine2) + "<br/>")}
    {enc.Encode(order.City)}, {enc.Encode(order.State)} {enc.Encode(order.PostalCode)}
  </p>

  <p style='margin-top:18px;color:#666;font-size:13px;'>
    View orders inside Parfait Internal → Orders.
  </p>
</div>";

        await SendMailAsync(graph, senderUpn, inboxes, $"[{siteName}] New paid order {order.OrderNumber}", html, ct);
    }

    public async Task SendAutomationEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
    {
        var senderUpn = ResolveSenderUpn();
        if (string.IsNullOrWhiteSpace(senderUpn))
            throw new InvalidOperationException("Missing SenderUpn/Contact:SenderEmail config.");

        var graph = BuildClient();
        await SendMailAsync(graph, senderUpn, [toEmail.Trim()], subject.Trim(), htmlBody, ct);
    }

    public async Task SendParfaitTeamInviteAsync(
        string toEmail,
        string displayName,
        string roleLabel,
        IReadOnlyCollection<string> allowedPageTitles,
        string loginUrl,
        string invitedBy,
        CancellationToken ct = default)
    {
        var senderUpn = ResolveSenderUpn(
            _config["GraphMail:ParfaitTeamSenderUpn"],
            _config["GraphMail:SenderUpn"],
            _config["Contact:RecipientEmail"],
            _config["Contact:SenderEmail"]);
        var siteName = (_config["Contact:WebsiteName"] ?? "Parfait App").Trim();

        if (string.IsNullOrWhiteSpace(senderUpn))
            throw new InvalidOperationException("Missing SenderUpn/Contact:SenderEmail config.");

        var graph = BuildClient();
        var enc = HtmlEncoder.Default;
        var safeName = enc.Encode(string.IsNullOrWhiteSpace(displayName) ? toEmail.Trim() : displayName.Trim());
        var safeRole = enc.Encode(roleLabel.Trim());
        var safeInvitedBy = enc.Encode(string.IsNullOrWhiteSpace(invitedBy) ? "Parfait Admin" : invitedBy.Trim());
        var safeUrl = enc.Encode(loginUrl.Trim());
        var accessSummary = allowedPageTitles.Count == 0
            ? "Dashboard"
            : string.Join(", ", allowedPageTitles.Select(title => title.Trim()).Where(title => !string.IsNullOrWhiteSpace(title)));
        var safeAccessSummary = enc.Encode(accessSummary);

        var html =
$@"
<div style='font-family:Inter,Arial,sans-serif;line-height:1.65;color:#111;'>
  <h2 style='margin:0 0 10px;'>You&apos;re invited to Parfait Internal</h2>
  <p style='margin:0 0 12px;'>Hi {safeName},</p>
  <p style='margin:0 0 12px;'>
    {safeInvitedBy} approved your <strong>{enc.Encode(siteName)}</strong> internal access with the
    <strong>{safeRole}</strong> role.
  </p>

  <div style='padding:14px 16px;border:1px solid #926950;border-radius:14px;background:#fff;'>
    <p style='margin:0 0 8px;'><strong>Approved account:</strong> {enc.Encode(toEmail.Trim())}</p>
    <p style='margin:0 0 8px;'><strong>Role:</strong> {safeRole}</p>
    <p style='margin:0;'><strong>Starting access:</strong> {safeAccessSummary}</p>
  </div>

  <p style='margin:16px 0 12px;'>
    Sign in with your <strong>@mylegnd.com</strong> Microsoft account to open your Parfait internal workspace.
  </p>

  <p style='margin:0 0 18px;'>
    <a href='{safeUrl}' style='display:inline-block;padding:12px 18px;border-radius:999px;background:#4f3425;color:#fff;text-decoration:none;font-weight:700;'>
      Open Parfait Internal
    </a>
  </p>

  <p style='margin:0;color:#666;font-size:13px;'>
    If your role or page access needs to change, the founder or a Full Control admin can update it from the Team console.
  </p>
</div>";

        await SendMailAsync(graph, senderUpn, [toEmail.Trim()], $"Your Parfait internal invite", html, ct);
    }

    private static IReadOnlyList<string> ResolveRecipients(params string?[] rawValues)
    {
        var recipients = new List<string>();
        foreach (var rawValue in rawValues)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
                continue;

            foreach (var candidate in rawValue.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!recipients.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    recipients.Add(candidate);
                }
            }
        }

        return recipients;
    }

    private string ResolveSenderUpn(params string?[] preferredValues)
    {
        var candidates = preferredValues.Length == 0
            ? [
                _config["GraphMail:SenderUpn"],
                _config["Contact:RecipientEmail"],
                _config["Contact:SenderEmail"]
            ]
            : preferredValues;

        foreach (var candidate in candidates)
        {
            var cleaned = candidate?.Trim();
            if (!string.IsNullOrWhiteSpace(cleaned))
                return cleaned;
        }

        return string.Empty;
    }

    private static async Task SendMailAsync(
        GraphServiceClient graph,
        string senderUpn,
        IReadOnlyCollection<string> recipients,
        string subject,
        string htmlBody,
        CancellationToken ct = default)
    {
        var msg = new Message
        {
            Subject = subject,
            Body = new ItemBody
            {
                ContentType = BodyType.Html,
                Content = htmlBody
            },
            ToRecipients = recipients
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => new Recipient
                {
                    EmailAddress = new EmailAddress { Address = value.Trim() }
                })
                .ToList()
        };

        var body = new SendMailPostRequestBody
        {
            Message = msg,
            SaveToSentItems = true
        };

        await graph.Users[senderUpn].SendMail.PostAsync(body, cancellationToken: ct);
    }
}
