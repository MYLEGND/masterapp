using Azure.Identity;
using System.Collections.Concurrent;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Graph.Users.Item.SendMail;
using System.Text.RegularExpressions;

namespace AgentPortal.Services;

public class ClientProvisioningService
{
    private static readonly ConcurrentDictionary<string, GraphServiceClient> GraphClients = new(StringComparer.Ordinal);
    private readonly IConfiguration _config;
    private readonly ILogger<ClientProvisioningService> _logger;
    private readonly GraphServiceClient _graph;
    private readonly MasterAppDbContext _db;

    public ClientProvisioningService(
        IConfiguration config,
        ILogger<ClientProvisioningService> logger,
        MasterAppDbContext db)
    {
        _config = config;
        _logger = logger;
        _db = db;

        var tenantId =
            GetSetting(
                "GraphProvisioning:TenantId", "GraphProvisioning__TenantId",
                "AzureAd:TenantId", "AzureAd__TenantId"
            ) ?? "";

        var clientId =
            GetSetting(
                "GraphProvisioning:ClientId", "GraphProvisioning__ClientId",
                "AzureAd:ClientId", "AzureAd__ClientId"
            ) ?? "";

        var clientSecret =
            GetSetting(
                "GraphProvisioning:ClientSecret", "GraphProvisioning__ClientSecret",
                "AzureAd:ClientSecret", "AzureAd__ClientSecret"
            ) ?? "";

        _logger.LogInformation(
            "GraphProvisioning config present? Tenant:{Tenant} Client:{Client} Secret:{Secret}",
            !string.IsNullOrWhiteSpace(tenantId),
            !string.IsNullOrWhiteSpace(clientId),
            !string.IsNullOrWhiteSpace(clientSecret)
        );

        if (string.IsNullOrWhiteSpace(tenantId))
            throw new Exception("Missing TenantId. Expected GraphProvisioning:TenantId (or GraphProvisioning__TenantId) or AzureAd:TenantId (or AzureAd__TenantId).");

        if (string.IsNullOrWhiteSpace(clientId))
            throw new Exception("Missing ClientId. Expected GraphProvisioning:ClientId (or GraphProvisioning__ClientId) or AzureAd:ClientId (or AzureAd__ClientId).");

        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new Exception("Missing ClientSecret. Expected GraphProvisioning:ClientSecret (or GraphProvisioning__ClientSecret) or AzureAd:ClientSecret (or AzureAd__ClientSecret).");

        try
        {
            var cacheKey = $"{tenantId}|{clientId}|{clientSecret}";
            _graph = GraphClients.GetOrAdd(cacheKey, _ =>
            {
                var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                return new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClientSecretCredential authentication failed while building Graph client.");
            throw;
        }
    }

    private string? GetSetting(params string[] keys)
    {
        foreach (var k in keys)
        {
            var v = _config[k];
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return null;
    }

    private static string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant();

    // ==========================================================
    // ✅ CLIENT USERNAME STRATEGY (YOUR RULE)
    // Clients: first3(first) + first3(last) + optional 1-2 digits
    // Example: Zac Owen -> zacowe@mylegnd.com, zacowe1@..., zacowe99@...
    //
    // NOTE: With Guest Invites, client "username" for YOUR app becomes their personal email.
    // These helpers are kept to avoid breaking anything else that might reference them.
    // ==========================================================
    private static string LettersOnly(string v)
    {
        v = (v ?? "").Trim().ToLowerInvariant();
        return Regex.Replace(v, @"[^a-z]", "");
    }

    private static string First3Strict(string v)
    {
        v = LettersOnly(v);
        if (v.Length >= 3) return v.Substring(0, 3);
        if (v.Length == 2) return v + "x";
        if (v.Length == 1) return v + "xx";
        return "xxx";
    }

    private static string BuildClientStem(string firstName, string lastName)
    {
        // ✅ EXACT RULE: first 3 of first + first 3 of last
        var f = First3Strict(firstName);
        var l = First3Strict(lastName);
        return $"{f}{l}";
    }

    private async Task<bool> UpnExistsAsync(string upn)
    {
        var e = (upn ?? "").Trim().Replace("'", "''");
        if (string.IsNullOrWhiteSpace(e)) return false;

        var result = await _graph.Users.GetAsync(req =>
        {
            req.QueryParameters.Filter = $"userPrincipalName eq '{e}'";
            req.QueryParameters.Select = new[] { "id" };
            req.QueryParameters.Top = 1;
            req.Headers.Add("ConsistencyLevel", "eventual");
        });

        return result?.Value?.Any() == true;
    }

    private async Task<bool> MailNicknameExistsAsync(string nick)
    {
        var e = (nick ?? "").Trim().ToLowerInvariant().Replace("'", "''");
        if (string.IsNullOrWhiteSpace(e)) return false;

        var result = await _graph.Users.GetAsync(req =>
        {
            req.QueryParameters.Filter = $"mailNickname eq '{e}'";
            req.QueryParameters.Select = new[] { "id" };
            req.QueryParameters.Top = 1;
            req.Headers.Add("ConsistencyLevel", "eventual");
        });

        return result?.Value?.Any() == true;
    }

    private async Task<string> FindAvailableClientUpnAsync(string firstName, string lastName, string domain)
    {
        domain = (domain ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(domain))
            throw new Exception("Missing tenant domain for client username.");

        var stem = BuildClientStem(firstName, lastName); // e.g. zacowe

        // Try base: zacowe@mylegnd.com
        var baseAlias = stem;
        var baseUpn = $"{baseAlias}@{domain}";

        // ✅ Ensure BOTH are free (UPN + mailNickname)
        if (!await UpnExistsAsync(baseUpn) && !await MailNicknameExistsAsync(baseAlias))
            return baseUpn;

        // Then 1..99 only (max 2 digits)
        for (int n = 1; n <= 99; n++)
        {
            var alias = $"{stem}{n}";
            var upn = $"{alias}@{domain}";

            if (!await UpnExistsAsync(upn) && !await MailNicknameExistsAsync(alias))
                return upn;
        }

        throw new Exception($"USERNAME_TAKEN: {stem}@{domain} through {stem}99@{domain} are taken.");
    }

    // ==========================================================
    // ✅ Find user by PERSONAL EMAIL (mail / otherMails)
    // Works for guests too (once redeemed, they'll have identities / mail fields depending)
    // ==========================================================
    private async Task<User?> FindUserByPersonalEmailAsync(string personalEmail)
    {
        try
        {
            var eMail = (personalEmail ?? "").Trim();
            if (string.IsNullOrWhiteSpace(eMail))
                return null;

            var esc = eMail.Replace("'", "''");
            var filter = $"mail eq '{esc}' or otherMails/any(m:m eq '{esc}')";

            var result = await _graph.Users.GetAsync(req =>
            {
                req.QueryParameters.Filter = filter;
                req.QueryParameters.Select = new[] { "id", "userPrincipalName", "mail", "otherMails" };
                req.QueryParameters.Top = 1;
                req.Headers.Add("ConsistencyLevel", "eventual");
            });

            return result?.Value?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FindUserByPersonalEmailAsync failed.");
            return null;
        }
    }

    // ==========================================================
    // ✅ Guest Invite (B2B)
    // - Creates an invitation for the client's PERSONAL email.
    // - Returns the invited user's objectId and the login identifier (personal email).
    // ==========================================================
    private async Task<(string objectId, string loginEmail)> InviteGuestUserAsync(
        string firstName,
        string lastName,
        string personalEmail,
        string inviteRedirectUrl,
        bool sendMicrosoftInviteEmail = false)
    {
        personalEmail = (personalEmail ?? "").Trim();
        firstName = (firstName ?? "").Trim();
        lastName = (lastName ?? "").Trim();
        inviteRedirectUrl = (inviteRedirectUrl ?? "").Trim();

        if (string.IsNullOrWhiteSpace(personalEmail))
            throw new Exception("Client email is required.");

        if (string.IsNullOrWhiteSpace(inviteRedirectUrl))
            throw new Exception("Missing inviteRedirectUrl for guest invite.");

        var displayName = $"{firstName} {lastName}".Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = personalEmail;

        var invitation = new Invitation
        {
            InvitedUserEmailAddress = personalEmail,
            InvitedUserDisplayName = displayName,
            InviteRedirectUrl = inviteRedirectUrl,
            SendInvitationMessage = sendMicrosoftInviteEmail
        };

        var created = await _graph.Invitations.PostAsync(invitation);
        var objectId = created?.InvitedUser?.Id;

        if (string.IsNullOrWhiteSpace(objectId))
            throw new Exception("Graph did not return an invited user id.");

        return (objectId, personalEmail);
    }

    // ==========================================================
    // ✅ Create client as a GUEST (B2B invite)
    // - If user exists already for that email, reuse it and return login identifier = personalEmail
    // - Else invite guest
    // ==========================================================
    public async Task<(string objectId, string loginUpn)> CreateTenantUserAsync(
        string firstName,
        string lastName,
        string personalEmail,
        string tempPassword)
    {
        firstName = (firstName ?? "").Trim();
        lastName = (lastName ?? "").Trim();
        personalEmail = (personalEmail ?? "").Trim();

        if (string.IsNullOrWhiteSpace(personalEmail))
            throw new Exception("Client email is required.");

        // ✅ For guests, tempPassword is NOT used. Keep parameter to avoid breaking callers.
        // tempPassword = tempPassword ?? "";

        // 1) If a user already exists for this email, reuse it
        var existing = await FindUserByPersonalEmailAsync(personalEmail);
        if (existing?.Id != null)
        {
            // For your app + welcome email, the "username" is always their personal email.
            return (existing.Id, personalEmail);
        }

        // 2) Invite guest
        var clientPortalBaseUrl =
            GetSetting("ClientPortal:BaseUrl", "ClientPortal__BaseUrl",
                       "GraphProvisioning:InviteRedirectUrl", "GraphProvisioning__InviteRedirectUrl")
            ?? "https://client.mylegnd.com";

        // We generally keep SendInvitationMessage=false so your own email controls messaging.
        var invited = await InviteGuestUserAsync(
            firstName: firstName,
            lastName: lastName,
            personalEmail: personalEmail,
            inviteRedirectUrl: clientPortalBaseUrl,
            sendMicrosoftInviteEmail: false
        );

        return (invited.objectId, invited.loginEmail);
    }

    // ✅ Ensure DB rows exist so ClientApp won't say "Client profile not found."
    public async Task EnsureClientProfileAndLinkAsync(
        string agentUserId,
        string clientUserId,
        string firstName,
        string lastName,
        string email,
        string? phone,
        DateTime? dob,
        string? maritalStatus,
        string? soFirstName,
        string? soLastName,
        DateTime? soDob,
        string? soEmail,
        string? soPhone)
    {
        var agentId = Norm(agentUserId);
        var clientId = Norm(clientUserId);

        if (string.IsNullOrWhiteSpace(agentId))
            throw new Exception("Missing agent user id.");
        if (string.IsNullOrWhiteSpace(clientId))
            throw new Exception("Missing client user id.");

        var emailNorm = !string.IsNullOrWhiteSpace(email) ? email.Trim().ToLowerInvariant() : null;

        // Guard: if a different ClientProfile already owns this normalized email, fail explicitly
        // rather than silently creating a duplicate or hitting a raw DB constraint.
        if (emailNorm != null)
        {
            var conflict = await _db.ClientProfiles
                .AsNoTracking()
                .AnyAsync(x => x.NormalizedEmail == emailNorm && (x.ClientUserId ?? "").ToLower() != clientId);
            if (conflict)
                throw new InvalidOperationException(
                    $"A ClientProfile with email '{emailNorm}' already exists under a different ClientUserId. " +
                    "Resolve the duplicate before provisioning.");
        }

        var existingProfile = await _db.ClientProfiles
            .FirstOrDefaultAsync(x => (x.ClientUserId ?? "").ToLower() == clientId);

        if (existingProfile == null)
        {
            existingProfile = new Domain.Entities.ClientProfile
            {
                ClientUserId = clientId,
                FirstName = (firstName ?? "").Trim(),
                LastName = (lastName ?? "").Trim(),
                Email = emailNorm ?? "",
                NormalizedEmail = emailNorm,
                Phone = (phone ?? "").Trim(),
                DOB = dob,
                MaritalStatus = (maritalStatus ?? "").Trim(),

                SignificantOtherFirstName = (soFirstName ?? "").Trim(),
                SignificantOtherLastName = (soLastName ?? "").Trim(),
                SignificantOtherDOB = soDob,
                SignificantOtherEmail = (soEmail ?? "").Trim().ToLowerInvariant(),
                SignificantOtherPhone = (soPhone ?? "").Trim(),

                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

            _db.ClientProfiles.Add(existingProfile);
        }
        else
        {
            existingProfile.FirstName = (firstName ?? "").Trim();
            existingProfile.LastName = (lastName ?? "").Trim();
            existingProfile.Email = emailNorm ?? "";
            existingProfile.NormalizedEmail = emailNorm;
            existingProfile.Phone = (phone ?? "").Trim();
            existingProfile.DOB = dob;
            existingProfile.MaritalStatus = (maritalStatus ?? "").Trim();

            existingProfile.SignificantOtherFirstName = (soFirstName ?? "").Trim();
            existingProfile.SignificantOtherLastName = (soLastName ?? "").Trim();
            existingProfile.SignificantOtherDOB = soDob;
            existingProfile.SignificantOtherEmail = (soEmail ?? "").Trim().ToLowerInvariant();
            existingProfile.SignificantOtherPhone = (soPhone ?? "").Trim();

            existingProfile.UpdatedUtc = DateTime.UtcNow;
        }

        var linkExists = await _db.AgentClients.AnyAsync(x =>
            (x.AgentUserId ?? "").ToLower() == agentId &&
            (x.ClientUserId ?? "").ToLower() == clientId);

        if (!linkExists)
        {
            _db.AgentClients.Add(new Domain.Entities.AgentClient
            {
                AgentUserId = agentId,
                ClientUserId = clientId
            });
        }

        await _db.SaveChangesAsync();
    }

    // ✅ OLD SIGNATURE KEPT (tempPassword ignored for guests)
    public async Task SendClientWelcomeEmailAsync(
        string toEmail,
        string firstName,
        string loginUpn,
        string tempPassword,
        string clientPortalUrl)
    {
        var portalLink = NormalizeClientProfileUrl(clientPortalUrl);

        await SendClientWelcomeEmailInternalAsync(
            toEmail: toEmail,
            firstName: firstName,
            loginUpn: loginUpn,
            tempPassword: tempPassword,
            portalLink: portalLink
        );
    }

    // ✅ NEW OVERLOAD (tempPassword ignored for guests)
    public async Task SendClientWelcomeEmailAsync(
        string toEmail,
        string firstName,
        string loginUpn,
        string tempPassword,
        string clientPortalBaseUrl,
        string clientUserId,
        bool forceIdLink = true)
    {
        toEmail = (toEmail ?? "").Trim();
        firstName = (firstName ?? "").Trim();
        loginUpn = (loginUpn ?? "").Trim();
        tempPassword = tempPassword ?? "";
        clientUserId = (clientUserId ?? "").Trim();

        if (string.IsNullOrWhiteSpace(clientUserId))
            throw new Exception("Missing clientUserId for welcome email link.");

        var portalLink = await NormalizeClientProfileUrlAsync(clientPortalBaseUrl, clientUserId);

        await SendClientWelcomeEmailInternalAsync(
            toEmail: toEmail,
            firstName: firstName,
            loginUpn: loginUpn,
            tempPassword: tempPassword,
            portalLink: portalLink
        );
    }

    private async Task SendClientWelcomeEmailInternalAsync(
        string toEmail,
        string firstName,
        string loginUpn,
        string tempPassword,
        string portalLink)
    {
        toEmail = (toEmail ?? "").Trim();
        firstName = (firstName ?? "").Trim();
        loginUpn = (loginUpn ?? "").Trim();
        portalLink = (portalLink ?? "").Trim();

        var subject = "Your Client Portal Login (Legend™)";

        var safeName = System.Net.WebUtility.HtmlEncode(firstName);
        var safeUpn = System.Net.WebUtility.HtmlEncode(loginUpn);
        var safePortal = System.Net.WebUtility.HtmlEncode(portalLink);

        // ✅ Guest flow: no temp password. They sign in using their email identity.
        var bodyHtml = $@"
<div style='font-family: Inter, Arial, sans-serif; color:#111;'>
  <h2 style='margin:0 0 10px 0;'>Welcome, {safeName}.</h2>
  <p style='margin:0 0 12px 0;'>Your client account has been created.</p>

  <div style='padding:14px;border:1.5px solid #a68023;border-radius:14px;background:#fff;max-width:640px;'>
    <p style='margin:0 0 10px 0;'><strong>Client Portal:</strong>
      <a href='{safePortal}' target='_blank' style='color:#a68023;font-weight:800;text-decoration:none;'>
        Click here to login
      </a>
    </p>
    <p style='margin:0 0 8px 0;'><strong>Username:</strong> {safeUpn}</p>
    <p style='margin:0; font-size:14px; color:#333;'>
      Sign in using this email address. If prompted, complete the Microsoft invitation / verification step.
    </p>
  </div>

  <div style='margin-top:16px; font-family: Inter, -apple-system, BlinkMacSystemFont, Segoe UI, Arial, sans-serif; font-size:13px; line-height:1.35; color:#cecece;'>
    <div style='margin:0 0 10px 0; color:#cecece;'>---</div>

    <div style='margin:0 0 2px 0; font-weight:700; color:#cecece;'>Zac Owen – Chief Executive Officer</div>
    <div style='margin:0 0 10px 0; font-weight:800; color:#cecece;'>Legend™</div>

    <div style='margin:0 0 10px 0; font-style:italic; color:#cecece;'>
      Where Your Faith Fuels Your Future &amp; Wellness Meets Wealth
    </div>

    <div style='margin:0 0 10px 0; color:#cecece;'>
      <a href='tel:+13604990851' style='color:#7ec8ff; text-decoration:none; font-weight:600;'>360-499-0851</a>
      <span style='color:#cecece;'> | </span>
      <a href='mailto:connect@mylegnd.com' style='color:#7ec8ff; text-decoration:none; font-weight:600;'>connect@mylegnd.com</a>
      <span style='color:#cecece;'> | </span>
      <a href='https://protect.mylegnd.com' target='_blank' style='color:#7ec8ff; text-decoration:underline; font-weight:600;'>protect.mylegnd.com</a>
    </div>

    <div style='margin:0 0 6px 0; color:#cecece;'>
      Life &amp; Risk Planning&nbsp;&nbsp;|&nbsp;&nbsp;Faith Based Support
    </div>

    <div style='margin:0 0 10px 0; color:#cecece;'>
      Wellbeing&nbsp;&nbsp;|&nbsp;&nbsp;Wealth Preservation&nbsp;&nbsp;|&nbsp;&nbsp;Legacy &amp; Risk Protection
    </div>

    <div style='margin:0 0 10px 0; color:#cecece;'>
      Insurance License #21546403&nbsp;&nbsp;|&nbsp;&nbsp;Agency License #22068294
    </div>

    <div style='margin:0; font-weight:700; color:#cecece;'>
      All For God's Glory | His Kingdom Come | His Will Be Done
    </div>
  </div>
</div>";

        var message = new Message
        {
            Subject = subject,
            Body = new ItemBody { ContentType = BodyType.Html, Content = bodyHtml },
            ToRecipients = new List<Recipient>
            {
                new Recipient { EmailAddress = new EmailAddress { Address = toEmail } }
            }
        };

        var requestBody = new SendMailPostRequestBody
        {
            Message = message,
            SaveToSentItems = true
        };

        var sender = GetSetting("Provisioning:SenderMailbox", "Provisioning__SenderMailbox");
        if (string.IsNullOrWhiteSpace(sender))
            throw new Exception("Missing Provisioning:SenderMailbox (or Provisioning__SenderMailbox) in configuration.");

        await _graph.Users[sender].SendMail.PostAsync(requestBody);
    }

    public async Task SendOnboardingInviteEmailAsync(string toEmail, string firstName, string onboardingLink)
    {
        toEmail = (toEmail ?? "").Trim().ToLowerInvariant();
        firstName = (firstName ?? "").Trim();
        onboardingLink = (onboardingLink ?? "").Trim();

        if (string.IsNullOrWhiteSpace(toEmail))
            throw new Exception("Onboarding email recipient is required.");

        if (string.IsNullOrWhiteSpace(onboardingLink))
            throw new Exception("Onboarding link is required.");

        var safeName = System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(firstName) ? "there" : firstName);
        var safeLink = System.Net.WebUtility.HtmlEncode(onboardingLink);

        var subject = "Complete your onboarding";
        var bodyHtml = $@"
<div style='font-family: Inter, Arial, sans-serif; color:#111;'>
    <h2 style='margin:0 0 10px 0;'>Hi {safeName},</h2>
    <p style='margin:0 0 12px 0; line-height:1.6;'>
        Please complete your onboarding using the secure link below.
    </p>
    <p style='margin:0 0 12px 0; line-height:1.6;'>
        <a href='{safeLink}' target='_blank' style='color:#a68023;font-weight:800;text-decoration:none;'>Open Onboarding Form</a>
    </p>
    <p style='margin:0;color:#444;line-height:1.6;'>
        If the link expires, contact us and we will send a new invite.
    </p>
</div>";

        var message = new Message
        {
            Subject = subject,
            Body = new ItemBody { ContentType = BodyType.Html, Content = bodyHtml },
            ToRecipients = new List<Recipient>
            {
                new Recipient { EmailAddress = new EmailAddress { Address = toEmail } }
            }
        };

        var requestBody = new SendMailPostRequestBody
        {
            Message = message,
            SaveToSentItems = true
        };

        var sender = GetSetting("Provisioning:SenderMailbox", "Provisioning__SenderMailbox");
        if (string.IsNullOrWhiteSpace(sender))
            throw new Exception("Missing Provisioning:SenderMailbox (or Provisioning__SenderMailbox) in configuration.");

        await _graph.Users[sender].SendMail.PostAsync(requestBody);
    }

        // ==========================================================
        // ✅ Assistant Invite Flow
        // - Invites assistant email as guest (if not already present)
        // - Sends branded assistant invitation email
        // ==========================================================
        public async Task<string> SendAssistantInviteEmailAsync(string toEmail, string firstName, string? inviterName = null)
        {
                toEmail = (toEmail ?? "").Trim().ToLowerInvariant();
                firstName = (firstName ?? "").Trim();
            inviterName = (inviterName ?? "").Trim();

                if (string.IsNullOrWhiteSpace(toEmail))
                        throw new Exception("Assistant email is required.");

                var agentPortalBaseUrl =
                        GetSetting("AgentPortal:BaseUrl", "AgentPortal__BaseUrl")
                        ?? "https://portal.mylegnd.com";

                var inviteRedirectUrl = $"{agentPortalBaseUrl.TrimEnd('/')}/Assistant";

                var existing = await FindUserByPersonalEmailAsync(toEmail);
            string assistantObjectId;
                if (existing?.Id == null)
                {
                var invited = await InviteGuestUserAsync(
                                firstName: firstName,
                                lastName: "Assistant",
                                personalEmail: toEmail,
                                inviteRedirectUrl: inviteRedirectUrl,
                                sendMicrosoftInviteEmail: false);
                assistantObjectId = invited.objectId;
                }
            else
            {
                assistantObjectId = existing.Id;
            }

                var safeName = System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(firstName) ? "Assistant" : firstName);
                var safeLink = System.Net.WebUtility.HtmlEncode(inviteRedirectUrl);
                var safeInviter = System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(inviterName) ? "your agent" : inviterName);

                var subject = "You’re invited to join Legend™ as an Assistant";
                var bodyHtml = $@"
<div style='font-family: Inter, Arial, sans-serif; color:#111;'>
    <h2 style='margin:0 0 10px 0;'>Hi {safeName}, welcome to Legend™.</h2>
    <p style='margin:0 0 12px 0; line-height:1.6;'>
        <strong>{safeInviter}</strong> invited you to collaborate as their assistant in the Agent Portal.
        We’re glad to have you on the team.
    </p>

    <div style='padding:14px;border:1.5px solid #a68023;border-radius:14px;background:#fff;max-width:640px;margin-bottom:10px;'>
        <p style='margin:0 0 10px 0; line-height:1.55;'>
            <strong>Get started:</strong>
            <a href='{safeLink}' target='_blank' style='color:#a68023;font-weight:800;text-decoration:none;'>Open your Assistant Workspace</a>
        </p>
        <p style='margin:0;font-size:14px;color:#333;line-height:1.6;'>
            Your access is focused on <strong>Leads</strong> and <strong>Workstation</strong> so you can jump in quickly and support the day-to-day workflow.
        </p>
    </div>

    <p style='margin:0;color:#444;line-height:1.6;'>
        If this invite reached you by mistake, you can safely ignore this email.
    </p>

    <p style='margin:14px 0 0 0;color:#444;'>
        — Legend™ Team
    </p>
</div>";

                var message = new Message
                {
                        Subject = subject,
                        Body = new ItemBody { ContentType = BodyType.Html, Content = bodyHtml },
                        ToRecipients = new List<Recipient>
                        {
                                new Recipient { EmailAddress = new EmailAddress { Address = toEmail } }
                        }
                };

                var requestBody = new SendMailPostRequestBody
                {
                        Message = message,
                        SaveToSentItems = true
                };

                var sender = GetSetting("Provisioning:SenderMailbox", "Provisioning__SenderMailbox");
                if (string.IsNullOrWhiteSpace(sender))
                        throw new Exception("Missing Provisioning:SenderMailbox (or Provisioning__SenderMailbox) in configuration.");

                await _graph.Users[sender].SendMail.PostAsync(requestBody);

            return assistantObjectId;
        }

    // ==========================================================
    // ✅ DELETE THE ENTRA ACCOUNT (REAL diagnostics)
    // ==========================================================
    public async Task DeleteTenantUserAsync(string objectId)
    {
        objectId = (objectId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(objectId))
            throw new Exception("Missing user objectId for deletion.");

        try
        {
            await _graph.Users[objectId].DeleteAsync();
        }
        catch (ODataError ex)
        {
            var msg = ex.Error?.Message ?? "Microsoft Graph request failed.";

            if (msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("resourcenotfound", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new Exception(msg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteTenantUserAsync failed. ObjectId={ObjectId}", objectId);
            throw;
        }
    }

    public async Task DeleteTenantUserByEmailAsync(string email)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email))
            throw new Exception("Missing email for tenant user deletion.");

        var user = await FindUserByPersonalEmailAsync(email);
        if (user?.Id == null)
            return; // already absent in Entra

        await DeleteTenantUserAsync(user.Id);
    }

    private static string NormalizeClientProfileUrl(string? clientPortalUrl)
    {
        var baseUrl = (clientPortalUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = "https://client.mylegnd.com";

        baseUrl = baseUrl.TrimEnd('/');

        // ✅ If someone already provided a deep link under this site, keep it as-is.
        // ✅ Otherwise, default to the base landing page (NO /profile).
        return baseUrl;
    }

    private async Task<string> NormalizeClientProfileUrlAsync(string? clientPortalBaseUrl, string clientUserId)
    {
        var baseUrl = (clientPortalBaseUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = "https://client.mylegnd.com";

        baseUrl = baseUrl.TrimEnd('/');

        clientUserId = (clientUserId ?? "").Trim();

        if (string.IsNullOrWhiteSpace(clientUserId))
            return baseUrl;

        var clientProfileId = await _db.ClientProfiles
            .AsNoTracking()
            .Where(x => (x.ClientUserId ?? "").Trim().ToLower() == clientUserId.ToLower())
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync();

        if (!clientProfileId.HasValue || clientProfileId.Value == Guid.Empty)
            return baseUrl;

        return $"{baseUrl}/support/view-as-client/{clientProfileId.Value}";
    }
}
