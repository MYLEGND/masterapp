using System.Collections.Concurrent;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace Infrastructure.Identity;

public sealed record AzureClientEmailSyncResult(bool Success, bool Skipped, string? Message = null);

public interface IAzureClientEmailSyncService
{
    Task<AzureClientEmailSyncResult> UpdateEmailAsync(
        string userObjectId,
        string newEmail,
        CancellationToken cancellationToken = default);
}

public sealed class AzureClientEmailSyncService : IAzureClientEmailSyncService
{
    private static readonly ConcurrentDictionary<string, GraphServiceClient> GraphClients =
        new(StringComparer.Ordinal);

    private readonly ILogger<AzureClientEmailSyncService> _logger;
    private readonly GraphServiceClient? _graph;
    private readonly string _inviteRedirectUrl;
    private readonly string? _configurationError;

    public AzureClientEmailSyncService(
        IConfiguration config,
        ILogger<AzureClientEmailSyncService> logger)
    {
        _logger = logger;

        _inviteRedirectUrl =
            GetSetting(config,
                "ClientPortal:BaseUrl", "ClientPortal__BaseUrl",
                "Provisioning:ClientPortalBaseUrl", "Provisioning__ClientPortalBaseUrl",
                "GraphProvisioning:InviteRedirectUrl", "GraphProvisioning__InviteRedirectUrl")
            ?? "https://localhost:5221";

        var tenantId =
            GetSetting(config,
                "GraphProvisioning:TenantId", "GraphProvisioning__TenantId",
                "AzureAd:TenantId", "AzureAd__TenantId");

        var clientId =
            GetSetting(config,
                "GraphProvisioning:ClientId", "GraphProvisioning__ClientId",
                "AzureAd:ClientId", "AzureAd__ClientId");

        var clientSecret =
            GetSetting(config,
                "GraphProvisioning:ClientSecret", "GraphProvisioning__ClientSecret",
                "AzureAd:ClientSecret", "AzureAd__ClientSecret");

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            _configurationError =
                "Azure email sync is not configured. Set GraphProvisioning or AzureAd tenant, client, and secret values.";
            return;
        }

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
            _configurationError = $"Azure email sync client initialization failed: {ex.Message}";
            _logger.LogError(ex, "Failed to initialize Graph client for Azure email sync.");
        }
    }

    public async Task<AzureClientEmailSyncResult> UpdateEmailAsync(
        string userObjectId,
        string newEmail,
        CancellationToken cancellationToken = default)
    {
        var objectId = NormalizeToken(userObjectId);
        var targetEmail = NormalizeEmail(newEmail);

        if (string.IsNullOrWhiteSpace(objectId))
            return Failure("Missing Azure client user id.");

        if (!Guid.TryParse(objectId, out _))
            return new AzureClientEmailSyncResult(
                Success: true,
                Skipped: true,
                Message: "This record does not have an Azure-backed client account.");

        if (string.IsNullOrWhiteSpace(targetEmail))
            return Failure("A real email address is required before syncing the Azure client account.");

        if (_graph == null)
            return Failure(_configurationError ?? "Azure email sync is not configured.");

        User? user;
        try
        {
            user = await _graph.Users[objectId].GetAsync(request =>
            {
                request.QueryParameters.Select = new[]
                {
                    "id",
                    "displayName",
                    "mail",
                    "otherMails",
                    "userPrincipalName",
                    "userType"
                };
            }, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Azure user {UserObjectId} before email sync.", objectId);
            return Failure($"Azure email lookup failed: {ex.Message}");
        }

        if (user?.Id == null)
            return Failure("Azure client account was not found.");

        var currentMail = NormalizeEmail(user.Mail);
        var currentOtherMails = NormalizeEmails(user.OtherMails);
        var desiredOtherMails = new List<string> { targetEmail };
        var needsMailPatch =
            !string.Equals(currentMail, targetEmail, StringComparison.Ordinal) ||
            !EmailListsMatch(currentOtherMails, desiredOtherMails);
        var isGuest = IsGuestUser(user);
        var needsRedemptionReset = isGuest && !string.Equals(currentMail, targetEmail, StringComparison.Ordinal);

        if (!needsMailPatch && !needsRedemptionReset)
        {
            return new AzureClientEmailSyncResult(
                Success: true,
                Skipped: false,
                Message: "Azure client email already matches.");
        }

        var previousMail = string.IsNullOrWhiteSpace(user.Mail) ? null : user.Mail.Trim();
        var previousOtherMails = NormalizeEmails(user.OtherMails);

        if (needsMailPatch)
        {
            var patchResult = await PatchUserEmailStateAsync(
                objectId,
                mail: targetEmail,
                otherMails: desiredOtherMails,
                cancellationToken);

            if (!patchResult.Success)
                return patchResult;
        }

        if (!needsRedemptionReset)
        {
            _logger.LogInformation(
                "Azure client email synced without redemption reset. UserObjectId={UserObjectId} Email={Email}",
                objectId,
                targetEmail);
            return new AzureClientEmailSyncResult(Success: true, Skipped: false, Message: null);
        }

        try
        {
            var invitation = new Invitation
            {
                InvitedUserEmailAddress = targetEmail,
                InviteRedirectUrl = _inviteRedirectUrl,
                SendInvitationMessage = false,
                ResetRedemption = true,
                InvitedUser = new User
                {
                    Id = objectId
                }
            };

            await _graph.Invitations.PostAsync(invitation, cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Azure guest redemption reset after email sync. UserObjectId={UserObjectId} Email={Email}",
                objectId,
                targetEmail);

            return new AzureClientEmailSyncResult(Success: true, Skipped: false, Message: null);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Azure guest redemption reset failed after email patch. UserObjectId={UserObjectId} Email={Email}",
                objectId,
                targetEmail);

            var rollbackResult = await PatchUserEmailStateAsync(
                objectId,
                previousMail,
                previousOtherMails,
                cancellationToken);

            var rollbackNote = rollbackResult.Success
                ? "Azure user email changes were rolled back."
                : $"Azure rollback also failed: {rollbackResult.Message}";

            return Failure($"Azure sign-in email update failed: {ex.Message} {rollbackNote}".Trim());
        }
    }

    private async Task<AzureClientEmailSyncResult> PatchUserEmailStateAsync(
        string objectId,
        string? mail,
        IEnumerable<string>? otherMails,
        CancellationToken cancellationToken)
    {
        if (_graph == null)
            return Failure(_configurationError ?? "Azure email sync is not configured.");

        try
        {
            var request = new User
            {
                OtherMails = NormalizeEmails(otherMails)
            };

            if (!string.IsNullOrWhiteSpace(mail))
                request.Mail = mail.Trim();

            await _graph.Users[objectId].PatchAsync(request, cancellationToken: cancellationToken);
            return new AzureClientEmailSyncResult(Success: true, Skipped: false, Message: null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure email patch failed. UserObjectId={UserObjectId} Mail={Mail}", objectId, mail);
            return Failure($"Azure email patch failed: {ex.Message}");
        }
    }

    private static bool IsGuestUser(User user)
    {
        if (string.Equals(user.UserType, "Guest", StringComparison.OrdinalIgnoreCase))
            return true;

        return (user.UserPrincipalName ?? string.Empty).Contains("#EXT#", StringComparison.OrdinalIgnoreCase);
    }

    private static bool EmailListsMatch(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static List<string> NormalizeEmails(IEnumerable<string>? emails)
    {
        return (emails ?? Array.Empty<string>())
            .Select(NormalizeEmail)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList()!;
    }

    private static string? NormalizeEmail(string? email)
    {
        var value = (email ?? string.Empty).Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string NormalizeToken(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string? GetSetting(IConfiguration config, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = config[key];
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static AzureClientEmailSyncResult Failure(string message)
        => new(Success: false, Skipped: false, Message: message);
}
