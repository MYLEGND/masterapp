using System.Security.Claims;

namespace Shared.Auth
{
    public static class ClaimsExtensions
    {
        public static string? GetOid(this ClaimsPrincipal user)
        {
            return user.FindFirst("oid")?.Value
                ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        }
    }
}
