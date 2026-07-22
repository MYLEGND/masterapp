using System.Globalization;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

public sealed class ClientSubscriptionInvitationEmailService
{
    private sealed record AgentInvitationContact(
        string? FullName,
        string? Title,
        string? Email,
        string? Phone,
        string? Npn);

    private readonly MasterAppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly IEmailSender _emailSender;

    public ClientSubscriptionInvitationEmailService(
        MasterAppDbContext db,
        IConfiguration configuration,
        IEmailSender emailSender)
    {
        _db = db;
        _configuration = configuration;
        _emailSender = emailSender;
    }

    public async Task SendAsync(
        ClientProfile client,
        ClientSubscriptionOffer offer,
        SubscriptionActivationInvitation invitation,
        string plainTextToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var toEmail = invitation.IntendedNormalizedEmail;
        if (string.IsNullOrWhiteSpace(toEmail))
            throw new InvalidOperationException("A subscription invitation email address is required.");

        var clientPortalBaseUrl = ResolveClientPortalBaseUrl();
        var activationUrl = $"{clientPortalBaseUrl}/activate/{Uri.EscapeDataString(plainTextToken)}";
        var signInUrl = $"{clientPortalBaseUrl}/Account/Login";
        var monthlyAmount = (offer.MonthlyAmountCents / 100m).ToString("C2", CultureInfo.GetCultureInfo("en-US"));
        var displayName = BuildClientDisplayName(client);
        var signInEmail = FirstNonEmpty(toEmail, client.NormalizedEmail, client.Email) ?? toEmail;
        var invitationExpires = invitation.ExpiresUtc.ToString("MMMM d, yyyy", CultureInfo.GetCultureInfo("en-US"));
        var agent = await ResolveAgentContactAsync(
            offer.OwnerAgentUserId,
            invitation.CreatedByAgentUserId,
            cancellationToken);

        var safeName = HtmlEncode(displayName);
        var safeAmount = HtmlEncode(monthlyAmount);
        var safeAnchor = HtmlEncode(DescribeBillingAnchor(offer));
        var safeInvitationExpires = HtmlEncode(invitationExpires);
        var safeActivationUrl = HtmlEncode(activationUrl);
        var safeSignInUrl = HtmlEncode(signInUrl);
        var safeSignInEmail = HtmlEncode(signInEmail);
        var safeAgentName = HtmlEncode(FirstNonEmpty(agent.FullName, agent.Email, "Legend") ?? "Legend");
        var safeAgentTitle = HtmlEncode(agent.Title ?? string.Empty);
        var safeAgentPhone = HtmlEncode(agent.Phone ?? string.Empty);
        var safeAgentEmail = HtmlEncode(agent.Email ?? string.Empty);
        var safeAgentNpn = HtmlEncode(agent.Npn ?? string.Empty);

        var agentTitleHtml = string.IsNullOrWhiteSpace(agent.Title)
            ? string.Empty
            : $"""<div style="margin:4px 0 0;color:#5b6475;font-size:14px;">{safeAgentTitle}</div>""";
        var agentPhoneHtml = string.IsNullOrWhiteSpace(agent.Phone)
            ? string.Empty
            : $"""<div style="margin:10px 0 0;"><span style="display:inline-block;min-width:56px;color:#7a5f12;font-weight:700;">Phone</span><a href="tel:{safeAgentPhone}" style="color:#0f274d;text-decoration:none;font-weight:700;">{safeAgentPhone}</a></div>""";
        var agentEmailHtml = string.IsNullOrWhiteSpace(agent.Email)
            ? string.Empty
            : $"""<div style="margin:8px 0 0;"><span style="display:inline-block;min-width:56px;color:#7a5f12;font-weight:700;">Email</span><a href="mailto:{safeAgentEmail}" style="color:#0f274d;text-decoration:none;font-weight:700;">{safeAgentEmail}</a></div>""";
        var agentNpnHtml = string.IsNullOrWhiteSpace(agent.Npn)
            ? string.Empty
            : $"""<div style="margin:8px 0 0;"><span style="display:inline-block;min-width:56px;color:#7a5f12;font-weight:700;">NPN</span><span style="color:#0f274d;font-weight:700;">{safeAgentNpn}</span></div>""";
        var replyCopy = string.IsNullOrWhiteSpace(agent.Email)
            ? "If you need help, contact your Legend agent before your invitation expires."
            : "If you need help, reply directly to this email and your agent will pick it up.";

        var subject = "Finish setting up your Legend ClientApp access";
        var htmlBody = $"""
<div style="margin:0;padding:32px 16px;background:#f4efe2;font-family:Inter,Arial,sans-serif;color:#132238;line-height:1.6;">
  <div style="max-width:680px;margin:0 auto;">
    <div style="background:linear-gradient(145deg,#0b1633 0%,#10224a 58%,#18376a 100%);border:1px solid #d4af37;border-radius:28px;overflow:hidden;box-shadow:0 22px 56px rgba(8,17,38,0.18);">
      <div style="padding:30px 32px 24px;color:#f8f2e6;">
        <div style="display:inline-block;padding:6px 12px;border:1px solid rgba(212,175,55,0.45);border-radius:999px;font-size:12px;font-weight:800;letter-spacing:0.14em;text-transform:uppercase;color:#dcb861;background:rgba(255,255,255,0.05);">
          Legend ClientApp
        </div>
        <h1 style="margin:18px 0 12px;font-size:32px;line-height:1.15;font-weight:800;color:#ffffff;">
          Your private client access is ready
        </h1>
        <p style="margin:0;font-size:16px;color:#dfe6f5;">
          Hi {safeName}, use your secure activation link below to confirm billing and finish sign-in with <strong style="color:#ffffff;">{safeSignInEmail}</strong>.
        </p>
      </div>

      <div style="padding:30px 32px 34px;background:#fffdfa;">
        <div style="padding:20px 22px;border:1px solid #e1c46c;border-radius:22px;background:linear-gradient(180deg,#fff8e8 0%,#fffdf7 100%);">
          <div style="font-size:12px;font-weight:800;letter-spacing:0.12em;text-transform:uppercase;color:#8a6a13;">Subscription Summary</div>
          <div style="margin:12px 0 4px;font-size:34px;line-height:1;font-weight:800;color:#10224a;">{safeAmount}<span style="font-size:16px;font-weight:700;color:#6c7484;"> / month</span></div>
          <div style="margin:10px 0 0;color:#38455c;"><strong>Billing day:</strong> {safeAnchor}</div>
          <div style="margin:6px 0 0;color:#38455c;"><strong>Secure link expires:</strong> {safeInvitationExpires}</div>
        </div>

        <div style="margin:26px 0 22px;text-align:center;">
          <a href="{safeActivationUrl}" target="_blank" style="display:inline-block;padding:15px 26px;border-radius:999px;background:#c89a1f;color:#0b1633;text-decoration:none;font-size:16px;font-weight:800;letter-spacing:0.01em;">
            Activate Subscription
          </a>
        </div>

        <div style="padding:18px 20px;border-radius:18px;background:#f6f1e4;border:1px solid #ead7a1;">
          <div style="font-size:13px;font-weight:800;letter-spacing:0.08em;text-transform:uppercase;color:#7a5f12;">What Happens Next</div>
          <div style="margin:10px 0 0;color:#243246;">1. Confirm your recurring billing details and consents.</div>
          <div style="margin:6px 0 0;color:#243246;">2. Finish Microsoft sign-in using <strong>{safeSignInEmail}</strong>.</div>
          <div style="margin:6px 0 0;color:#243246;">3. Open your private client portal and manage your account from there.</div>
        </div>

        <p style="margin:22px 0 0;color:#4d5768;">
          After activation, future sign-ins use this secure client sign-in page:
          <a href="{safeSignInUrl}" target="_blank" style="color:#0f274d;font-weight:700;text-decoration:none;">{safeSignInUrl}</a>
        </p>
        <p style="margin:10px 0 0;color:#4d5768;">{replyCopy}</p>

        <div style="margin:28px 0 0;padding:22px 0 0;border-top:1px solid #eadfbe;">
          <div style="font-size:12px;font-weight:800;letter-spacing:0.12em;text-transform:uppercase;color:#8a6a13;">Your Agent</div>
          <div style="margin:10px 0 0;font-size:22px;font-weight:800;color:#10224a;">{safeAgentName}</div>
          {agentTitleHtml}
          {agentPhoneHtml}
          {agentEmailHtml}
          {agentNpnHtml}
          <div style="margin:18px 0 0;color:#7a5f12;font-weight:800;">Legend™</div>
          <div style="margin:4px 0 0;color:#5b6475;font-style:italic;">Where Your Faith Fuels Your Future &amp; Wellness Meets Wealth</div>
        </div>
      </div>
    </div>
  </div>
</div>
""";

        var textLines = new List<string>
        {
            $"Hi {displayName},",
            string.Empty,
            "Your private Legend ClientApp access is ready.",
            $"Monthly amount: {monthlyAmount}",
            $"Billing day: {DescribeBillingAnchor(offer)}",
            $"Invitation expires: {invitationExpires}",
            $"Activate subscription: {activationUrl}",
            $"After activation, sign in here: {signInUrl}",
            $"Sign-in email: {signInEmail}",
            string.Empty,
            "What happens next:",
            "1. Confirm your recurring billing details and consents.",
            $"2. Finish Microsoft sign-in using {signInEmail}.",
            "3. Open your private client portal.",
            string.Empty,
            string.IsNullOrWhiteSpace(agent.FullName) ? "Your agent:" : $"Your agent: {agent.FullName}"
        };

        if (!string.IsNullOrWhiteSpace(agent.Title))
            textLines.Add(agent.Title);
        if (!string.IsNullOrWhiteSpace(agent.Phone))
            textLines.Add($"Phone: {agent.Phone}");
        if (!string.IsNullOrWhiteSpace(agent.Email))
            textLines.Add($"Email: {agent.Email}");
        if (!string.IsNullOrWhiteSpace(agent.Npn))
            textLines.Add($"NPN: {agent.Npn}");

        textLines.Add("Legend");

        var textBody = string.Join("\n", textLines);

        var sent = await _emailSender.TrySendAsync(
            toEmail,
            subject,
            htmlBody,
            textBody,
            fromDisplayName: BuildFromDisplayName(agent),
            replyToEmail: agent.Email);

        if (!sent)
            throw new InvalidOperationException("The subscription invitation email could not be sent with the current email configuration.");
    }

    private string ResolveClientPortalBaseUrl()
    {
        var value =
            _configuration["ClientPortal:BaseUrl"] ??
            _configuration["ClientPortal__BaseUrl"] ??
            _configuration["Provisioning:ClientPortalBaseUrl"] ??
            _configuration["Provisioning__ClientPortalBaseUrl"] ??
            _configuration["ClientPortalBaseUrl"] ??
            "https://client.mylegnd.com";

        return value.Trim().TrimEnd('/');
    }

    private static string DescribeBillingAnchor(ClientSubscriptionOffer offer)
    {
        return offer.BillingAnchorSelectionMode switch
        {
            BillingAnchorSelectionMode.FirstOfMonth => "1st of the month",
            BillingAnchorSelectionMode.FifteenthOfMonth => "15th of the month",
            BillingAnchorSelectionMode.SpecificDayOfMonth when offer.SelectedBillingAnchorDay.HasValue => $"Day {offer.SelectedBillingAnchorDay.Value} of the month",
            BillingAnchorSelectionMode.ClientSelectedIfAllowed => "Client-selected during activation when policy allows it",
            _ => "Provider default"
        };
    }

    private async Task<AgentInvitationContact> ResolveAgentContactAsync(
        string? ownerAgentUserId,
        string? createdByAgentUserId,
        CancellationToken cancellationToken)
    {
        var agentIds = new[] { ownerAgentUserId, createdByAgentUserId }
            .Select(TrimToNull)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (agentIds.Length == 0)
            return new AgentInvitationContact(null, null, null, null, null);

        var profile = await _db.AgentProfiles
            .AsNoTracking()
            .Where(x => agentIds.Contains(x.AgentUserId))
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (profile is null)
            return new AgentInvitationContact(null, null, null, null, null);

        return new AgentInvitationContact(
            TrimToNull(profile.FullName),
            TrimToNull(profile.Title),
            FirstNonEmpty(TrimToNull(profile.NormalizedEmail), TrimToNull(profile.AgentUpn)),
            TrimToNull(profile.Phone),
            TrimToNull(profile.Npn));
    }

    private static string BuildClientDisplayName(ClientProfile client)
    {
        var displayName = string.Join(" ", new[] { client.FirstName, client.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        return string.IsNullOrWhiteSpace(displayName) ? "there" : displayName;
    }

    private static string BuildFromDisplayName(AgentInvitationContact agent)
    {
        var name = FirstNonEmpty(agent.FullName, agent.Email);
        return string.IsNullOrWhiteSpace(name) ? "Legend Client Access" : $"{name} | Legend";
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var trimmed = TrimToNull(value);
            if (!string.IsNullOrWhiteSpace(trimmed))
                return trimmed;
        }

        return null;
    }

    private static string? TrimToNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string HtmlEncode(string value) => System.Net.WebUtility.HtmlEncode(value);
}
