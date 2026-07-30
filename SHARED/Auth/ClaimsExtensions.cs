using System.Security.Claims;

namespace Shared.Auth
{
    public static class ClaimsExtensions
    {
        /// <summary>
        /// Returns the canonical Entra Object ID (oid) only. This is an
        /// authoritative identity read: it MUST NOT fall back to
        /// NameIdentifier/sub/email/UPN, which are not stable cross-application
        /// identity keys. Use <see cref="UserIdExtensions.GetEmailCandidate"/>
        /// for non-authoritative email needs.
        /// </summary>
        public static string? GetOid(this ClaimsPrincipal user)
        {
            return user.FindFirst("oid")?.Value
                ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
        }
    }
}
