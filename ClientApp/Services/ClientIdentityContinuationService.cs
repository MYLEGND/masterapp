using System.Security.Cryptography;
using System.Text;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ClientApp.Services;

public sealed record ClientIdentityContinuationValidationResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedMessage,
    ClientIdentityContinuation? Continuation = null);

public sealed class ClientIdentityContinuationService
{
    public const string ContinuationCookieName = "clientapp_continuation";
    private readonly MasterAppDbContext _db;
    private readonly IDataProtector _protector;

    public ClientIdentityContinuationService(MasterAppDbContext db, IDataProtectionProvider dataProtectionProvider)
    {
        _db = db;
        _protector = dataProtectionProvider.CreateProtector("MasterApp.ClientApp.IdentityContinuation.v1");
    }

    public async Task<(string ProtectedState, DateTime ExpiresUtc)> CreateProtectedStateAsync(
        Guid clientProfileId,
        string intendedNormalizedEmail,
        string returnUrl,
        ClientIdentityContinuationPurpose purpose,
        Guid? invitationId = null,
        Guid? subscriptionId = null,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;
        var expiresUtc = nowUtc.Add(purpose == ClientIdentityContinuationPurpose.Activation
            ? TimeSpan.FromMinutes(30)
            : TimeSpan.FromMinutes(15));
        var token = CreateOpaqueToken();

        var continuation = new ClientIdentityContinuation
        {
            ClientProfileId = clientProfileId,
            SubscriptionActivationInvitationId = invitationId,
            ClientSubscriptionId = subscriptionId,
            Purpose = purpose,
            TokenHash = Hash(token),
            IntendedNormalizedEmail = NormalizeEmail(intendedNormalizedEmail),
            ReturnUrl = NormalizeReturnUrl(returnUrl),
            ExpiresUtc = expiresUtc,
            CreatedUtc = nowUtc
        };

        _db.ClientIdentityContinuations.Add(continuation);
        await _db.SaveChangesAsync(cancellationToken);

        return (_protector.Protect(token), expiresUtc);
    }

    public async Task<ClientIdentityContinuationValidationResult> ValidateProtectedStateAsync(string protectedState, CancellationToken cancellationToken = default)
    {
        var plainToken = Unprotect(protectedState);
        if (string.IsNullOrWhiteSpace(plainToken))
            return new ClientIdentityContinuationValidationResult(false, "INVALID_CONTINUATION", "The sign-in continuation is no longer valid.");

        return await ValidatePlainTokenAsync(plainToken, cancellationToken);
    }

    public async Task<ClientIdentityContinuationValidationResult> ValidateCookieAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Cookies.TryGetValue(ContinuationCookieName, out var protectedState) ||
            string.IsNullOrWhiteSpace(protectedState))
        {
            return new ClientIdentityContinuationValidationResult(false, "MISSING_CONTINUATION", "A valid sign-in continuation is required.");
        }

        return await ValidateProtectedStateAsync(protectedState, cancellationToken);
    }

    public void StoreCookie(HttpResponse response, string protectedState, DateTime expiresUtc)
    {
        response.Cookies.Append(
            ContinuationCookieName,
            protectedState,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresUtc, DateTimeKind.Utc))
            });
    }

    public void ClearCookie(HttpResponse response)
    {
        response.Cookies.Delete(ContinuationCookieName);
    }

    public async Task ConsumeAsync(ClientIdentityContinuation continuation, CancellationToken cancellationToken = default)
    {
        continuation.ConsumedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ClientIdentityContinuationValidationResult> ValidatePlainTokenAsync(string plainToken, CancellationToken cancellationToken)
    {
        var tokenHash = Hash(plainToken);
        var continuation = await _db.ClientIdentityContinuations
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);

        if (continuation is null)
            return new ClientIdentityContinuationValidationResult(false, "UNKNOWN_CONTINUATION", "The sign-in continuation is no longer valid.");

        if (continuation.ConsumedUtc.HasValue)
            return new ClientIdentityContinuationValidationResult(false, "USED_CONTINUATION", "This sign-in continuation has already been used.");

        if (continuation.ExpiresUtc <= DateTime.UtcNow)
            return new ClientIdentityContinuationValidationResult(false, "EXPIRED_CONTINUATION", "This sign-in continuation has expired.");

        return new ClientIdentityContinuationValidationResult(true, null, null, continuation);
    }

    private string? Unprotect(string? protectedState)
    {
        if (string.IsNullOrWhiteSpace(protectedState))
            return null;

        try
        {
            return _protector.Unprotect(protectedState.Trim());
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? string.Empty : email.Trim().ToLowerInvariant();

    private static string NormalizeReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) &&
        returnUrl.StartsWith("/", StringComparison.Ordinal) &&
        Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
            ? returnUrl
            : "/";

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string CreateOpaqueToken(int byteLength = 32)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
