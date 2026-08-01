using System.Collections.Concurrent;
using Azure.Identity;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace Infrastructure.Identity;

public sealed record ClientEntraIdentityResult(
    string ObjectId,
    string LoginEmail,
    bool Created,
    bool ApplicationAssignmentCreated);

public sealed record ClientEntraIdentitySynchronizationResult(
    string ObjectId,
    string LoginEmail,
    bool RedemptionReset);

public interface IClientEntraLifecycleService
{
    Task<ClientEntraIdentityResult> EnsureClientIdentityAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default);

    Task<ClientEntraIdentityResult> EnsureExternalIdentityAsync(
        string firstName,
        string lastName,
        string email,
        CancellationToken cancellationToken = default);

    Task<ClientEntraIdentitySynchronizationResult> SynchronizeClientIdentityAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default);

    Task DeleteClientIdentityAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the ClientApp assignment and invalidates active sessions while
    /// preserving the Entra identity. Household member removal uses this;
    /// full profile deletion uses <see cref="DeleteClientIdentityAsync"/>.
    /// </summary>
    Task RevokeClientApplicationAccessAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default);

    Task DeleteExternalIdentityAsync(
        string? objectId,
        string? email,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The single Microsoft Entra lifecycle authority for MASTERAPP client
/// identities. MASTERAPP remains authoritative for account/subscription state;
/// Entra is provisioned and removed as a downstream identity projection.
/// </summary>
public sealed class ClientEntraLifecycleService : IClientEntraLifecycleService
{
    private static readonly ConcurrentDictionary<string, GraphServiceClient>
        GraphClients = new(StringComparer.Ordinal);

    private readonly MasterAppDbContext _db;
    private readonly ILogger<ClientEntraLifecycleService> _logger;
    private readonly GraphServiceClient _graph;
    private readonly string _inviteRedirectUrl;
    private readonly string _clientApplicationId;
    private readonly Guid _clientAppRoleId;

    public ClientEntraLifecycleService(
        MasterAppDbContext db,
        IConfiguration configuration,
        ILogger<ClientEntraLifecycleService> logger)
    {
        _db = db;
        _logger = logger;

        var tenantId = GetRequired(
            configuration,
            "GraphProvisioning:TenantId",
            "GraphProvisioning__TenantId",
            "AzureAd:TenantId",
            "AzureAd__TenantId");

        var provisioningClientId = GetRequired(
            configuration,
            "GraphProvisioning:ClientId",
            "GraphProvisioning__ClientId");

        var provisioningSecret = GetRequired(
            configuration,
            "GraphProvisioning:ClientSecret",
            "GraphProvisioning__ClientSecret");

        _clientApplicationId = GetRequired(
            configuration,
            "GraphProvisioning:ClientApplicationId",
            "GraphProvisioning__ClientApplicationId");

        var configuredRole = GetSetting(
            configuration,
            "GraphProvisioning:ClientAppRoleId",
            "GraphProvisioning__ClientAppRoleId");

        if (!string.IsNullOrWhiteSpace(configuredRole) &&
            !Guid.TryParse(configuredRole, out _clientAppRoleId))
        {
            throw new InvalidOperationException(
                "GraphProvisioning:ClientAppRoleId must be a GUID.");
        }

        _inviteRedirectUrl =
            GetSetting(
                configuration,
                "GraphProvisioning:InviteRedirectUrl",
                "GraphProvisioning__InviteRedirectUrl",
                "ClientPortal:BaseUrl",
                "ClientPortal__BaseUrl",
                "Provisioning:ClientPortalBaseUrl",
                "Provisioning__ClientPortalBaseUrl")
            ?? "https://client.mylegnd.com";

        var cacheKey =
            $"{tenantId}|{provisioningClientId}|{provisioningSecret}";

        _graph = GraphClients.GetOrAdd(cacheKey, _ =>
        {
            var credential = new ClientSecretCredential(
                tenantId,
                provisioningClientId,
                provisioningSecret);

            return new GraphServiceClient(
                credential,
                new[] { "https://graph.microsoft.com/.default" });
        });
    }

    public async Task<ClientEntraIdentityResult> EnsureClientIdentityAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default)
    {
        if (clientProfileId == Guid.Empty)
            throw new ArgumentException(
                "Client profile id is required.",
                nameof(clientProfileId));

        var profile = await _db.ClientProfiles
            .SingleOrDefaultAsync(
                x => x.Id == clientProfileId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                $"Client profile {clientProfileId} was not found.");

        var result = await EnsureExternalIdentityAsync(
            profile.FirstName,
            profile.LastName,
            profile.NormalizedEmail ?? profile.Email,
            cancellationToken);

        var existingObjectId =
            NormalizeToken(profile.ExternalIdentityObjectId);

        if (!string.Equals(
                existingObjectId,
                result.ObjectId,
                StringComparison.Ordinal))
        {
            var conflict = await _db.ClientProfiles
                .AsNoTracking()
                .AnyAsync(
                    x => x.Id != profile.Id &&
                         x.ExternalIdentityObjectId != null &&
                         x.ExternalIdentityObjectId.ToLower() ==
                         result.ObjectId,
                    cancellationToken);

            if (conflict)
            {
                throw new InvalidOperationException(
                    "The Entra identity is already bound to another client profile.");
            }

            profile.ExternalIdentityObjectId = result.ObjectId;
            profile.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return result;
    }

    public async Task<ClientEntraIdentityResult> EnsureExternalIdentityAsync(
        string firstName,
        string lastName,
        string email,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            throw new InvalidOperationException(
                "A client email is required for Entra provisioning.");
        }

        var existing = await FindByEmailAsync(
            normalizedEmail,
            cancellationToken);

        var created = false;

        if (existing?.Id is null)
        {
            var displayName =
                $"{firstName?.Trim()} {lastName?.Trim()}".Trim();

            var invitation = new Invitation
            {
                InvitedUserEmailAddress = normalizedEmail,
                InvitedUserDisplayName =
                    string.IsNullOrWhiteSpace(displayName)
                        ? normalizedEmail
                        : displayName,
                InviteRedirectUrl = _inviteRedirectUrl,
                SendInvitationMessage = false
            };

            var invited = await _graph.Invitations.PostAsync(
                invitation,
                cancellationToken: cancellationToken);

            existing = invited?.InvitedUser;
            created = true;
        }

        var objectId = NormalizeToken(existing?.Id);
        if (string.IsNullOrWhiteSpace(objectId))
        {
            throw new InvalidOperationException(
                "Microsoft Graph did not return a client object id.");
        }

        await SynchronizeUserAsync(
            objectId,
            firstName,
            lastName,
            normalizedEmail,
            cancellationToken);

        var assignmentCreated = await EnsureApplicationAssignmentAsync(
            objectId,
            cancellationToken);

        _logger.LogInformation(
            "Client Entra identity ensured. ObjectId={ObjectId} Email={Email} Created={Created} AssignmentCreated={AssignmentCreated}",
            objectId,
            normalizedEmail,
            created,
            assignmentCreated);

        return new ClientEntraIdentityResult(
            objectId,
            normalizedEmail,
            created,
            assignmentCreated);
    }

    public async Task<ClientEntraIdentitySynchronizationResult> SynchronizeClientIdentityAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default)
    {
        if (clientProfileId == Guid.Empty)
            throw new ArgumentException("Client profile id is required.", nameof(clientProfileId));

        var profile = await _db.ClientProfiles
            .SingleOrDefaultAsync(x => x.Id == clientProfileId, cancellationToken)
            ?? throw new InvalidOperationException($"Client profile {clientProfileId} was not found.");

        var email = NormalizeEmail(profile.NormalizedEmail ?? profile.Email);
        if (string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException("A client email is required for Entra synchronization.");

        var objectId = NormalizeToken(profile.ExternalIdentityObjectId);
        if (string.IsNullOrWhiteSpace(objectId) && Guid.TryParse(profile.ClientUserId, out _))
            objectId = NormalizeToken(profile.ClientUserId);

        if (string.IsNullOrWhiteSpace(objectId))
        {
            var ensured = await EnsureClientIdentityAsync(clientProfileId, cancellationToken);
            return new ClientEntraIdentitySynchronizationResult(
                ensured.ObjectId,
                ensured.LoginEmail,
                RedemptionReset: false);
        }

        User? user;
        try
        {
            user = await _graph.Users[objectId].GetAsync(
                request =>
                {
                    request.QueryParameters.Select = new[]
                    {
                        "id", "mail", "otherMails", "userPrincipalName", "userType"
                    };
                },
                cancellationToken: cancellationToken);
        }
        catch (ODataError error) when (IsNotFound(error))
        {
            var ensured = await EnsureClientIdentityAsync(clientProfileId, cancellationToken);
            return new ClientEntraIdentitySynchronizationResult(
                ensured.ObjectId,
                ensured.LoginEmail,
                RedemptionReset: false);
        }

        if (user?.Id is null)
            throw new InvalidOperationException("Microsoft Graph did not return the client identity.");

        var currentMail = NormalizeEmail(user.Mail);
        await SynchronizeUserAsync(
            objectId,
            profile.FirstName,
            profile.LastName,
            email,
            cancellationToken);

        var redemptionReset = IsGuestUser(user) &&
                             !string.Equals(currentMail, email, StringComparison.Ordinal);
        if (redemptionReset)
        {
            await _graph.Invitations.PostAsync(
                new Invitation
                {
                    InvitedUserEmailAddress = email,
                    InviteRedirectUrl = _inviteRedirectUrl,
                    SendInvitationMessage = false,
                    ResetRedemption = true,
                    InvitedUser = new User { Id = objectId }
                },
                cancellationToken: cancellationToken);
        }

        await EnsureApplicationAssignmentAsync(objectId, cancellationToken);

        if (!string.Equals(profile.ExternalIdentityObjectId, objectId, StringComparison.OrdinalIgnoreCase))
        {
            profile.ExternalIdentityObjectId = objectId;
            profile.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation(
            "Client Entra identity synchronized. ObjectId={ObjectId} Email={Email} RedemptionReset={RedemptionReset}",
            objectId,
            email,
            redemptionReset);

        return new ClientEntraIdentitySynchronizationResult(objectId, email, redemptionReset);
    }

    public async Task DeleteClientIdentityAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default)
    {
        var profile = await _db.ClientProfiles
            .SingleOrDefaultAsync(
                x => x.Id == clientProfileId,
                cancellationToken);

        if (profile is null)
            return;

        await DeleteExternalIdentityAsync(
            profile.ExternalIdentityObjectId,
            email: null,
            cancellationToken);

        profile.ExternalIdentityObjectId = null;
        profile.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokeClientApplicationAccessAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default)
    {
        if (clientProfileId == Guid.Empty)
            throw new ArgumentException("Client profile id is required.", nameof(clientProfileId));

        var objectId = await _db.ClientProfiles
            .AsNoTracking()
            .Where(x => x.Id == clientProfileId)
            .Select(x => x.ExternalIdentityObjectId)
            .SingleOrDefaultAsync(cancellationToken);

        objectId = NormalizeToken(objectId);
        if (string.IsNullOrWhiteSpace(objectId))
            return;

        try
        {
            await RevokeApplicationAccessAndSessionsAsync(objectId, cancellationToken);
        }
        catch (ODataError error) when (IsNotFound(error))
        {
            return;
        }

        _logger.LogInformation(
            "Client Entra application access revoked. ObjectId={ObjectId}",
            objectId);
    }

    public async Task DeleteExternalIdentityAsync(
        string? objectId,
        string? email,
        CancellationToken cancellationToken = default)
    {
        var normalizedObjectId = NormalizeToken(objectId);

        if (string.IsNullOrWhiteSpace(normalizedObjectId))
        {
            var existing = await FindByEmailAsync(
                NormalizeEmail(email),
                cancellationToken);

            normalizedObjectId = NormalizeToken(existing?.Id);
        }

        if (string.IsNullOrWhiteSpace(normalizedObjectId))
            return;

        try
        {
            await RevokeApplicationAccessAndSessionsAsync(
                normalizedObjectId,
                cancellationToken);
            await _graph.Users[normalizedObjectId]
                .DeleteAsync(cancellationToken: cancellationToken);
        }
        catch (ODataError error) when (IsNotFound(error))
        {
            return;
        }

        _logger.LogInformation(
            "Client Entra identity deleted. ObjectId={ObjectId}",
            normalizedObjectId);
    }

    private async Task<User?> FindByEmailAsync(
        string email,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var escaped = email.Replace("'", "''");

        var response = await _graph.Users.GetAsync(
            request =>
            {
                request.QueryParameters.Filter =
                    $"mail eq '{escaped}' or otherMails/any(m:m eq '{escaped}')";
                request.QueryParameters.Select = new[]
                {
                    "id",
                    "displayName",
                    "mail",
                    "otherMails",
                    "userPrincipalName",
                    "userType"
                };
                request.QueryParameters.Top = 1;
                request.Headers.Add("ConsistencyLevel", "eventual");
            },
            cancellationToken);

        return response?.Value?.FirstOrDefault();
    }

    private async Task RevokeApplicationAccessAndSessionsAsync(
        string userObjectId,
        CancellationToken cancellationToken)
    {
        var servicePrincipalId = await FindClientServicePrincipalIdAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(servicePrincipalId))
        {
            var assignments = await _graph.Users[userObjectId]
                .AppRoleAssignments
                .GetAsync(
                    request =>
                    {
                        request.QueryParameters.Filter =
                            $"resourceId eq {servicePrincipalId}";
                        request.QueryParameters.Select = new[] { "id" };
                    },
                    cancellationToken);

            foreach (var assignment in assignments?.Value ?? Enumerable.Empty<AppRoleAssignment>())
            {
                if (!string.IsNullOrWhiteSpace(assignment.Id))
                {
                    await _graph.Users[userObjectId]
                        .AppRoleAssignments[assignment.Id]
                        .DeleteAsync(cancellationToken: cancellationToken);
                }
            }
        }

        await _graph.Users[userObjectId]
            .RevokeSignInSessions
            .PostAsRevokeSignInSessionsPostResponseAsync(cancellationToken: cancellationToken);
    }

    private async Task SynchronizeUserAsync(
        string objectId,
        string? firstName,
        string? lastName,
        string email,
        CancellationToken cancellationToken)
    {
        var displayName =
            $"{firstName?.Trim()} {lastName?.Trim()}".Trim();

        var patch = new User
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? email
                : displayName,
            Mail = email,
            OtherMails = new List<string> { email }
        };

        await _graph.Users[objectId].PatchAsync(
            patch,
            cancellationToken: cancellationToken);
    }

    private async Task<bool> EnsureApplicationAssignmentAsync(
        string userObjectId,
        CancellationToken cancellationToken)
    {
        var resourceId = await FindClientServicePrincipalIdAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(resourceId))
        {
            throw new InvalidOperationException(
                $"The ClientApp enterprise application for app id {_clientApplicationId} was not found.");
        }

        var assignments =
            await _graph.Users[userObjectId]
                .AppRoleAssignments
                .GetAsync(
                    request =>
                    {
                        request.QueryParameters.Filter =
                            $"resourceId eq {resourceId}";
                        request.QueryParameters.Select =
                            new[] { "id", "resourceId", "appRoleId" };
                    },
                    cancellationToken);

        if (assignments?.Value?.Any(
                assignment =>
                    string.Equals(
                        assignment.ResourceId?.ToString(),
                        resourceId,
                        StringComparison.OrdinalIgnoreCase) &&
                    assignment.AppRoleId == _clientAppRoleId) == true)
        {
            return false;
        }

        await _graph.Users[userObjectId]
            .AppRoleAssignments
            .PostAsync(
                new AppRoleAssignment
                {
                    PrincipalId = Guid.Parse(userObjectId),
                    ResourceId = Guid.Parse(resourceId),
                    AppRoleId = _clientAppRoleId
                },
                cancellationToken: cancellationToken);

        return true;
    }

    private async Task<string?> FindClientServicePrincipalIdAsync(CancellationToken cancellationToken)
    {
        var escapedApplicationId = _clientApplicationId.Replace("'", "''");
        var servicePrincipals = await _graph.ServicePrincipals.GetAsync(
            request =>
            {
                request.QueryParameters.Filter = $"appId eq '{escapedApplicationId}'";
                request.QueryParameters.Select = new[] { "id", "appId", "displayName" };
                request.QueryParameters.Top = 1;
            },
            cancellationToken);

        return servicePrincipals?.Value?.FirstOrDefault()?.Id;
    }

    private static bool IsGuestUser(User user)
    {
        if (string.Equals(user.UserType, "Guest", StringComparison.OrdinalIgnoreCase))
            return true;

        return (user.UserPrincipalName ?? string.Empty)
            .Contains("#EXT#", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNotFound(ODataError error)
    {
        var message = error.Error?.Message ?? string.Empty;
        var code = error.Error?.Code ?? string.Empty;

        return code.Contains("notfound", StringComparison.OrdinalIgnoreCase) ||
               code.Contains("resourcenotfound", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRequired(
        IConfiguration configuration,
        params string[] keys)
    {
        return GetSetting(configuration, keys)
            ?? throw new InvalidOperationException(
                $"Missing required configuration: {string.Join(" or ", keys)}");
    }

    private static string? GetSetting(
        IConfiguration configuration,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static string NormalizeEmail(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string NormalizeToken(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();
}
