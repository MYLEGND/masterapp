using System.Security.Claims;
using Domain.Billing;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ClientApp.Services;

public sealed record ClientSignInPreparationResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedMessage,
    string ReturnUrl,
    string? ProtectedState = null,
    DateTime? ExpiresUtc = null);

public sealed record ClientSignInCompletionResult(
    bool Success,
    string ReturnUrl,
    string? SafeErrorCode = null,
    string? SanitizedMessage = null);

public sealed class ClientIdentityAccessService
{
    private readonly MasterAppDbContext _db;
    private readonly IBillingEntitlementService _entitlementService;
    private readonly ClientIdentityContinuationService _continuationService;

    public ClientIdentityAccessService(
        MasterAppDbContext db,
        IBillingEntitlementService entitlementService,
        ClientIdentityContinuationService continuationService)
    {
        _db = db;
        _entitlementService = entitlementService;
        _continuationService = continuationService;
    }

    public static bool IsSupportReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
            return false;

        return returnUrl.StartsWith("/support/", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(returnUrl, "/support", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> HasValidChallengeContinuationAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var validation = await _continuationService.ValidateCookieAsync(httpContext.Request, cancellationToken);
        return validation.Success;
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
        var safeReturnUrl = NormalizeReturnUrl(returnUrl);

        if (string.IsNullOrWhiteSpace(normalizedEmail))
            return new ClientSignInPreparationResult(false, "EMAIL_REQUIRED", "Enter the email address on the client invitation to continue.", safeReturnUrl);

        var profile = await _db.ClientProfiles
            .FirstOrDefaultAsync(x =>
                (x.NormalizedEmail ?? string.Empty).ToLower() == normalizedEmail ||
                (x.Email ?? string.Empty).ToLower() == normalizedEmail,
                cancellationToken);

        if (profile is null)
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

        return new ClientSignInPreparationResult(true, null, null, safeReturnUrl, protectedState.ProtectedState, protectedState.ExpiresUtc);
    }

    public async Task<ClientSignInCompletionResult> CompleteClientSignInAsync(HttpContext httpContext, ClaimsPrincipal principal, string? fallbackReturnUrl = null, CancellationToken cancellationToken = default)
    {
        var safeFallbackReturnUrl = NormalizeReturnUrl(fallbackReturnUrl);
        var continuationValidation = await _continuationService.ValidateCookieAsync(httpContext.Request, cancellationToken);
        if (!continuationValidation.Success)
        {
            return IsAgentPrincipal(principal)
                ? new ClientSignInCompletionResult(true, safeFallbackReturnUrl)
                : new ClientSignInCompletionResult(false, safeFallbackReturnUrl, continuationValidation.SafeErrorCode, continuationValidation.SanitizedMessage);
        }

        var continuation = continuationValidation.Continuation!;
        var profile = await _db.ClientProfiles.FirstOrDefaultAsync(x => x.Id == continuation.ClientProfileId, cancellationToken);
        if (profile is null)
            return new ClientSignInCompletionResult(false, "/", "UNKNOWN_CLIENT", "The linked client profile could not be found.");

        var oid = NormalizeId(
            principal.FindFirst("oid")?.Value
            ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value);

        if (string.IsNullOrWhiteSpace(oid))
            return new ClientSignInCompletionResult(false, continuation.ReturnUrl, "MISSING_OBJECT_ID", "The identity provider did not return a stable object ID.");

        var principalEmail = NormalizeEmail(
            principal.FindFirstValue("preferred_username")
            ?? principal.FindFirstValue(ClaimTypes.Upn)
            ?? principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.Identity?.Name);

        if (!string.IsNullOrWhiteSpace(continuation.IntendedNormalizedEmail) &&
            !string.IsNullOrWhiteSpace(principalEmail) &&
            !string.Equals(continuation.IntendedNormalizedEmail, principalEmail, StringComparison.Ordinal))
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
        if (string.IsNullOrWhiteSpace(profile.NormalizedEmail) && !string.IsNullOrWhiteSpace(principalEmail))
            profile.NormalizedEmail = principalEmail;

        await _db.SaveChangesAsync(cancellationToken);
        await _continuationService.ConsumeAsync(continuation, cancellationToken);
        _continuationService.ClearCookie(httpContext.Response);

        var entitlement = await _entitlementService.EvaluateAsync(
            new BillingEntitlementEvaluationRequest(
                profile.Id,
                BillingEntitlementKeys.ClientAppFullAccess,
                DateTime.UtcNow),
            cancellationToken);

        if (entitlement.Status is not (ClientEntitlementStatus.Active or ClientEntitlementStatus.GracePeriod))
        {
            return new ClientSignInCompletionResult(false, continuation.ReturnUrl, "INACTIVE_ENTITLEMENT", "The client subscription is not active for access.");
        }

        return new ClientSignInCompletionResult(true, continuation.ReturnUrl);
    }

    private static bool IsAgentPrincipal(ClaimsPrincipal principal)
    {
        var normalizedEmail = NormalizeEmail(
            principal.FindFirstValue("preferred_username")
            ?? principal.FindFirstValue(ClaimTypes.Upn)
            ?? principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.Identity?.Name);

        return normalizedEmail.EndsWith("@mylegnd.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? string.Empty : email.Trim().ToLowerInvariant();

    private static string NormalizeId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static string NormalizeReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) &&
        returnUrl.StartsWith("/", StringComparison.Ordinal) &&
        Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
            ? returnUrl
            : "/";
}
