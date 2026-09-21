using System.Security.Claims;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace ClientApp.Services;

public sealed record ClientSignInPreparationResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedMessage,
    string ReturnUrl,
    string? ProtectedState = null,
    DateTime? ExpiresUtc = null,
    string? LoginHint = null);

public sealed record ClientSignInCompletionResult(
    bool Success,
    string ReturnUrl,
    string? SafeErrorCode = null,
    string? SanitizedMessage = null,
    string? LoginHint = null);

public sealed class ClientIdentityAccessService
{
    private readonly MasterAppDbContext _db;
    private readonly IBillingEntitlementService _entitlementService;
    private readonly ClientIdentityContinuationService _continuationService;
    private readonly ClientAppReturnUrlNormalizer _returnUrlNormalizer;

    public ClientIdentityAccessService(
        MasterAppDbContext db,
        IBillingEntitlementService entitlementService,
        ClientIdentityContinuationService continuationService,
        ClientAppReturnUrlNormalizer returnUrlNormalizer)
    {
        _db = db;
        _entitlementService = entitlementService;
        _continuationService = continuationService;
        _returnUrlNormalizer = returnUrlNormalizer;
    }

    public static bool IsSupportReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
            return false;

        return returnUrl.StartsWith("/support/", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(returnUrl, "/support", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ClientSignInCompletionResult> ValidateAzureChallengeAsync(
        HttpContext httpContext,
        string? fallbackReturnUrl = null,
        CancellationToken cancellationToken = default)
    {
        var validation = await _continuationService.ValidateCookieAsync(httpContext.Request, cancellationToken);
        var safeFallbackReturnUrl = _returnUrlNormalizer.Normalize(fallbackReturnUrl);
        if (!validation.Success || validation.Continuation is null)
        {
            return new ClientSignInCompletionResult(
                false,
                safeFallbackReturnUrl,
                validation.SafeErrorCode,
                validation.SanitizedMessage);
        }

        var continuation = validation.Continuation;
        var profile = await _db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == continuation.ClientProfileId, cancellationToken);
        if (profile is null)
        {
            return new ClientSignInCompletionResult(
                false,
                safeFallbackReturnUrl,
                "UNKNOWN_CLIENT",
                "The linked client profile could not be found.");
        }

        var entitlement = await _entitlementService.EvaluateAsync(
            new BillingEntitlementEvaluationRequest(
                profile.Id,
                BillingEntitlementKeys.ClientAppFullAccess,
                DateTime.UtcNow),
            cancellationToken);

        return entitlement.Status is ClientEntitlementStatus.Active or ClientEntitlementStatus.GracePeriod
            ? new ClientSignInCompletionResult(true, continuation.ReturnUrl, LoginHint: continuation.IntendedNormalizedEmail)
            : new ClientSignInCompletionResult(
                false,
                continuation.ReturnUrl,
                "INACTIVE_ENTITLEMENT",
                "The client subscription is not active for access.");
    }

    public void StoreChallengeContinuationCookie(HttpResponse response, string protectedState, DateTime expiresUtc)
    {
        _continuationService.StoreCookie(response, protectedState, expiresUtc);
    }

    public void ClearChallengeContinuationCookie(HttpResponse response)
    {
        _continuationService.ClearCookie(response);
    }

    public async Task<ClientSignInPreparationResult> PrepareClientSignInAsync(string email, string returnUrl, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        var safeReturnUrl = _returnUrlNormalizer.Normalize(returnUrl);

        if (string.IsNullOrWhiteSpace(normalizedEmail))
            return new ClientSignInPreparationResult(false, "EMAIL_REQUIRED", "Enter the email address on the client invitation to continue.", safeReturnUrl);

        var profile = await _db.ClientProfiles
            .FirstOrDefaultAsync(x =>
                (x.NormalizedEmail ?? string.Empty).ToLower() == normalizedEmail ||
                (x.Email ?? string.Empty).ToLower() == normalizedEmail,
                cancellationToken);

        if (profile is null || !IsPortalClientProfile(profile))
        {
            return new ClientSignInPreparationResult(false, "CLIENT_NOT_READY", "We could not find an activated client profile for that email yet.", safeReturnUrl);
        }

        var entitlement = await _entitlementService.EvaluateAsync(
            new BillingEntitlementEvaluationRequest(
                profile.Id,
                BillingEntitlementKeys.ClientAppFullAccess,
                DateTime.UtcNow),
            cancellationToken);

        if (entitlement.Status is not (ClientEntitlementStatus.Active or ClientEntitlementStatus.GracePeriod))
        {
            return new ClientSignInPreparationResult(false, "CLIENT_NOT_ACTIVE", "This client account is not active for sign-in yet. Use the activation link or contact your agent for help.", safeReturnUrl);
        }

        var protectedState = await _continuationService.CreateProtectedStateAsync(
            profile.Id,
            normalizedEmail,
            safeReturnUrl,
            ClientIdentityContinuationPurpose.SignIn,
            cancellationToken: cancellationToken);

        return new ClientSignInPreparationResult(
            true,
            null,
            null,
            safeReturnUrl,
            protectedState.ProtectedState,
            protectedState.ExpiresUtc,
            normalizedEmail);
    }

    public async Task<ClientSignInCompletionResult> ValidateAuthenticatedClientSessionAsync(
        ClaimsPrincipal principal,
        string? fallbackReturnUrl = null,
        CancellationToken cancellationToken = default)
    {
        var safeReturnUrl = _returnUrlNormalizer.Normalize(fallbackReturnUrl);
        if (IsSupportReturnUrl(safeReturnUrl) &&
            await IsAgentPrincipalAsync(principal, cancellationToken))
            return new ClientSignInCompletionResult(true, safeReturnUrl);

        // Canonical Entra Object ID only (F19). GetCanonicalUserId reads oid /
        // objectidentifier and normalizes exactly as NormalizeId did, but never
        // falls back to NameIdentifier — which could resolve a different/legacy
        // client profile.
        var oid = principal.GetCanonicalUserId();

        if (string.IsNullOrWhiteSpace(oid))
            return new ClientSignInCompletionResult(false, safeReturnUrl, "MISSING_OBJECT_ID", "A valid client sign-in is required.");

        var profile = await _db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x =>
                (x.ExternalIdentityObjectId ?? string.Empty).ToLower() == oid ||
                (x.ClientUserId ?? string.Empty).ToLower() == oid,
                cancellationToken);

        if (profile is null || !IsPortalClientProfile(profile))
            return new ClientSignInCompletionResult(false, safeReturnUrl, "UNKNOWN_CLIENT", "A valid client subscription is required before opening the portal.");

        var entitlement = await _entitlementService.EvaluateAsync(
            new BillingEntitlementEvaluationRequest(
                profile.Id,
                BillingEntitlementKeys.ClientAppFullAccess,
                DateTime.UtcNow),
            cancellationToken);

        return entitlement.Status is ClientEntitlementStatus.Active or ClientEntitlementStatus.GracePeriod
            ? new ClientSignInCompletionResult(true, safeReturnUrl)
            : new ClientSignInCompletionResult(false, safeReturnUrl, "INACTIVE_ENTITLEMENT", "This client subscription is not active for access.");
    }

    private async Task<ClientSignInCompletionResult> RecoverActiveClientBindingAsync(
        ClaimsPrincipal principal,
        string safeReturnUrl,
        CancellationToken cancellationToken)
    {
        var oid = principal.GetCanonicalUserId();
        if (string.IsNullOrWhiteSpace(oid))
            return new ClientSignInCompletionResult(
                false,
                safeReturnUrl,
                "MISSING_OBJECT_ID",
                "A valid client sign-in is required.");

        var principalEmails = PrincipalEmailCandidates(principal);
        if (principalEmails.Length == 0)
            return new ClientSignInCompletionResult(
                false,
                safeReturnUrl,
                "MISSING_EMAIL",
                "The Microsoft account did not provide the client email required to recover access.");

        var matches = await _db.ClientProfiles
            .Where(profile =>
                principalEmails.Contains((profile.NormalizedEmail ?? string.Empty).ToLower()) ||
                principalEmails.Contains((profile.Email ?? string.Empty).ToLower()))
            .OrderBy(profile => profile.Id)
            .Take(2)
            .ToListAsync(cancellationToken);

        if (matches.Count == 0)
            return new ClientSignInCompletionResult(
                false,
                safeReturnUrl,
                "UNKNOWN_CLIENT",
                "A valid client subscription is required before opening the portal.");

        if (matches.Count != 1)
            return new ClientSignInCompletionResult(
                false,
                safeReturnUrl,
                "AMBIGUOUS_CLIENT_IDENTITY",
                "This email is linked to more than one client profile. Contact your LEGEND guide before signing in.");

        var profile = matches[0];
        if (!IsPortalClientProfile(profile))
            return new ClientSignInCompletionResult(
                false,
                safeReturnUrl,
                "CLIENT_NOT_READY",
                "This CRM record is not a client portal account.");

        var existingObjectId = NormalizeId(profile.ExternalIdentityObjectId);
        if (!string.IsNullOrWhiteSpace(existingObjectId) &&
            !string.Equals(existingObjectId, oid, StringComparison.Ordinal))
        {
            return new ClientSignInCompletionResult(
                false,
                safeReturnUrl,
                "CLIENT_ALREADY_LINKED",
                "This client profile is already linked to a different Microsoft account.");
        }

        var conflictingProfile = await _db.ClientProfiles
            .AsNoTracking()
            .AnyAsync(other =>
                other.Id != profile.Id &&
                other.ExternalIdentityObjectId != null &&
                other.ExternalIdentityObjectId.ToLower() == oid,
                cancellationToken);

        if (conflictingProfile)
            return new ClientSignInCompletionResult(
                false,
                safeReturnUrl,
                "IDENTITY_CONFLICT",
                "This Microsoft account is already linked to a different client profile.");

        var entitlement = await _entitlementService.EvaluateAsync(
            new BillingEntitlementEvaluationRequest(
                profile.Id,
                BillingEntitlementKeys.ClientAppFullAccess,
                DateTime.UtcNow),
            cancellationToken);

        if (entitlement.Status is not (ClientEntitlementStatus.Active or ClientEntitlementStatus.GracePeriod))
            return new ClientSignInCompletionResult(
                false,
                safeReturnUrl,
                "INACTIVE_ENTITLEMENT",
                "This client subscription is not active for access.");

        if (string.IsNullOrWhiteSpace(existingObjectId))
        {
            profile.ExternalIdentityObjectId = oid;
            profile.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return new ClientSignInCompletionResult(true, safeReturnUrl);
    }

    public async Task<ClientSignInCompletionResult> CompleteClientSignInAsync(HttpContext httpContext, ClaimsPrincipal principal, string? fallbackReturnUrl = null, CancellationToken cancellationToken = default)
    {
        var safeFallbackReturnUrl = _returnUrlNormalizer.Normalize(fallbackReturnUrl);
        var continuationValidation = await _continuationService.ValidateCookieAsync(
            httpContext.Request,
            cancellationToken);

        if (!continuationValidation.Success || continuationValidation.Continuation is null)
        {
            // A continuation is required only when binding a Microsoft identity
            // to a client profile for the first time. An already-bound client
            // can be authenticated directly from the stable Microsoft object ID,
            // provided the linked profile still has an active entitlement.
            var existingClientSession = await ValidateAuthenticatedClientSessionAsync(
                principal,
                safeFallbackReturnUrl,
                cancellationToken);

            if (existingClientSession.Success)
            {
                _continuationService.ClearCookie(httpContext.Response);
                return existingClientSession;
            }

            var isAgent = await IsAgentPrincipalAsync(principal, cancellationToken);
            if (!isAgent)
            {
                var recoveredClientSession = await RecoverActiveClientBindingAsync(
                    principal,
                    safeFallbackReturnUrl,
                    cancellationToken);

                if (recoveredClientSession.Success)
                {
                    _continuationService.ClearCookie(httpContext.Response);
                    return recoveredClientSession;
                }

                return recoveredClientSession;
            }

            return IsSupportReturnUrl(safeFallbackReturnUrl)
                ? new ClientSignInCompletionResult(true, safeFallbackReturnUrl)
                : new ClientSignInCompletionResult(
                    false,
                    safeFallbackReturnUrl,
                    continuationValidation.SafeErrorCode,
                    continuationValidation.SanitizedMessage);
        }

        var continuation = continuationValidation.Continuation;
        var profile = await _db.ClientProfiles.FirstOrDefaultAsync(x => x.Id == continuation.ClientProfileId, cancellationToken);
        if (profile is null)
            return new ClientSignInCompletionResult(false, "/", "UNKNOWN_CLIENT", "The linked client profile could not be found.");

        // A continuation is only a proof that the user passed the initial email
        // gate. Check the live entitlement again before binding or issuing a
        // usable session, so an expired/cancelled subscription cannot race past
        // the sign-in boundary.
        var entitlement = await _entitlementService.EvaluateAsync(
            new BillingEntitlementEvaluationRequest(
                profile.Id,
                BillingEntitlementKeys.ClientAppFullAccess,
                DateTime.UtcNow),
            cancellationToken);

        if (entitlement.Status is not (ClientEntitlementStatus.Active or ClientEntitlementStatus.GracePeriod))
        {
            return new ClientSignInCompletionResult(
                false,
                continuation.ReturnUrl,
                "INACTIVE_ENTITLEMENT",
                "The client subscription is not active for access.");
        }

        var currentProfileEmail = NormalizeEmail(profile.NormalizedEmail ?? profile.Email);
        if (string.IsNullOrWhiteSpace(currentProfileEmail) ||
            !string.Equals(continuation.IntendedNormalizedEmail, currentProfileEmail, StringComparison.Ordinal))
        {
            return new ClientSignInCompletionResult(
                false,
                continuation.ReturnUrl,
                "EMAIL_CHANGED",
                "The email on this client profile changed. Start sign-in again with the current email.");
        }

        var oid = NormalizeId(
            principal.FindFirst("oid")?.Value
            ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value);

        if (string.IsNullOrWhiteSpace(oid))
            return new ClientSignInCompletionResult(false, continuation.ReturnUrl, "MISSING_OBJECT_ID", "The identity provider did not return a stable object ID.");

        var principalEmails = PrincipalEmailCandidates(principal);
        if (!principalEmails.Contains(continuation.IntendedNormalizedEmail))
        {
            return new ClientSignInCompletionResult(false, continuation.ReturnUrl, "EMAIL_MISMATCH", "The Microsoft account email does not match the invited client email.");
        }

        var conflictingProfile = await _db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x =>
                x.Id != profile.Id &&
                (x.ExternalIdentityObjectId ?? string.Empty).ToLower() == oid,
                cancellationToken);

        if (conflictingProfile is not null)
        {
            return new ClientSignInCompletionResult(false, continuation.ReturnUrl, "IDENTITY_CONFLICT", "This Microsoft account is already linked to a different client profile.");
        }

        var currentBoundOid = NormalizeId(profile.ExternalIdentityObjectId);
        if (!string.IsNullOrWhiteSpace(currentBoundOid) &&
            !string.Equals(currentBoundOid, oid, StringComparison.Ordinal))
        {
            return new ClientSignInCompletionResult(false, continuation.ReturnUrl, "CLIENT_ALREADY_LINKED", "This client profile is already linked to a different Microsoft account.");
        }

        profile.ExternalIdentityObjectId = oid;
        if (string.IsNullOrWhiteSpace(profile.NormalizedEmail))
            profile.NormalizedEmail = continuation.IntendedNormalizedEmail;

        await _db.SaveChangesAsync(cancellationToken);
        await _continuationService.ConsumeAsync(continuation, cancellationToken);
        _continuationService.ClearCookie(httpContext.Response);

        return new ClientSignInCompletionResult(true, continuation.ReturnUrl);
    }

    private async Task<bool> IsAgentPrincipalAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var oid = principal.GetCanonicalUserId();
        if (string.IsNullOrWhiteSpace(oid))
            return false;

        return await _db.AgentProfiles
                   .AsNoTracking()
                   .AnyAsync(profile => (profile.AgentUserId ?? string.Empty).ToLower() == oid, cancellationToken)
               || await _db.AgentTrackingProfiles
                   .AsNoTracking()
                   .AnyAsync(profile => (profile.AgentUserId ?? string.Empty).ToLower() == oid &&
                                        (profile.Status ?? string.Empty).ToLower() == "active", cancellationToken)
               || await _db.AgentClients
                   .AsNoTracking()
                   .AnyAsync(link => (link.AgentUserId ?? string.Empty).ToLower() == oid, cancellationToken);
    }

    private static bool IsPortalClientProfile(ClientProfile profile) =>
        ClientRecordClassification.IsClientOrBusinessClient(
            profile.ClientUserId,
            profile.CrmNotes,
            profile.CrmStatus);

    private static string[] PrincipalEmailCandidates(ClaimsPrincipal principal)
    {
        var values = principal.Claims
            .Where(claim =>
                claim.Type == "preferred_username" ||
                claim.Type == ClaimTypes.Email ||
                claim.Type == "email" ||
                claim.Type == "emails" ||
                claim.Type == "upn" ||
                claim.Type == ClaimTypes.Upn ||
                claim.Type == "unique_name")
            .Select(claim => NormalizeEmail(claim.Value))
            .Append(NormalizeEmail(principal.Identity?.Name))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return values;
    }

    private static string NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? string.Empty : email.Trim().ToLowerInvariant();

    private static string NormalizeId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

}
