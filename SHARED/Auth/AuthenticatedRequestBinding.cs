using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shared.Auth;

/// <summary>
/// A browser session or a bounded, validated-token request binding. A token's
/// unique identifier is never represented as a browser login session.
/// </summary>
public sealed record AuthenticatedRequestBinding(string Id, DateTime? ValidUntilUtc)
{
    private const string ClaimPrefix = "urn:legend:authenticated-request-binding:";
    private const string ClaimIssuer = "LegendServerRequestBinding";

    public static AuthenticatedRequestBinding? Resolve(ClaimsPrincipal principal, DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc || principal.Identity?.IsAuthenticated != true) return null;
        var bindings = principal.Identities.OfType<ServerBindingIdentity>().ToArray();
        if (bindings.Length != 0)
        {
            if (bindings.Length != 1 || bindings[0].Binding is not { } binding ||
                bindings[0].UserId != principal.GetCanonicalUserId() ||
                bindings[0].TenantId != principal.GetCanonicalTenantId() ||
                binding.ValidUntilUtc is not { } expires || expires <= utcNow)
                return null;
            return binding;
        }

        // Marker claims or authentication-type strings supplied by a token do
        // not create the private server identity used above.
        if (principal.Claims.Any(claim => claim.Type.StartsWith(ClaimPrefix, StringComparison.Ordinal)) ||
            principal.Identities.Any(identity => identity.AuthenticationType == "LegendPersistedDelegation")) return null;
        var sessions = principal.Identities.Where(identity => identity.IsAuthenticated)
            .SelectMany(identity => identity.FindAll("sid")).ToArray();
        return sessions.Length == 1 && ValidIdentifier(sessions[0].Value)
            ? new(sessions[0].Value, null) : null;
    }

    /// <summary>Called only from the normal Entra bearer OnTokenValidated event.</summary>
    public static void AttachValidatedEntraToken(ClaimsPrincipal principal, string validatedIssuer,
        DateTime validUntilUtc, DateTime utcNow)
    {
        AuthenticatedRequestBinding? binding = null;
        var tenant = SingleClaim(principal, "tid");
        var user = SingleClaim(principal, "oid");
        var tokenIdentifier = SingleClaim(principal, "uti");
        var tenantId = Guid.TryParse(tenant, out var tenantGuid) && tenantGuid != Guid.Empty ? tenantGuid.ToString("D") : "";
        var userId = Guid.TryParse(user, out var userGuid) && userGuid != Guid.Empty ? userGuid.ToString("D") : "";
        if (principal.Identity?.IsAuthenticated == true && utcNow.Kind == DateTimeKind.Utc &&
            validUntilUtc.Kind == DateTimeKind.Utc && validUntilUtc > utcNow &&
            validatedIssuer is { Length: > 0 and <= 2048 } && Uri.TryCreate(validatedIssuer, UriKind.Absolute, out var issuer) &&
            issuer.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(issuer.UserInfo) &&
            string.IsNullOrEmpty(issuer.Query) && string.IsNullOrEmpty(issuer.Fragment) &&
            tenantId.Length != 0 && userId.Length != 0 && ValidIdentifier(tokenIdentifier) &&
            principal.GetCanonicalTenantId() == tenantId && principal.GetCanonicalUserId() == userId)
        {
            var tuple = JsonSerializer.Serialize(new[] { "legend-entra-token-binding.v1", validatedIssuer, tenantId, userId, tokenIdentifier });
            var id = "entra-token." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tuple)));
            binding = new(id, validUntilUtc);
        }
        // A failed mint still marks this as a mobile token request. It cannot
        // fall back to a raw sid from the token, including within JWT clock skew.
        principal.AddIdentity(new ServerBindingIdentity(binding, userId, tenantId, "entra-token"));
    }

    /// <summary>Review-token authentication never delegates Founder cloud work.</summary>
    public static void DenyDelegation(ClaimsPrincipal principal) =>
        principal.AddIdentity(new ServerBindingIdentity(null, "", "", "denied"));

    /// <summary>
    /// Use only after re-reading the owned, active canonical operation. This
    /// preserves its bounded lease and does not construct a browser sid.
    /// </summary>
    public static ClaimsPrincipal CreatePersistedDelegationPrincipal(string userId, string tenantId,
        string bindingId, DateTime expiresUtc)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("oid", userId), new Claim("tid", tenantId)
        }, "LegendPersistedDelegation"));
        var binding = ValidIdentifier(bindingId) && expiresUtc.Kind == DateTimeKind.Utc
            ? new AuthenticatedRequestBinding(bindingId, expiresUtc) : null;
        principal.AddIdentity(new ServerBindingIdentity(binding, userId, tenantId, "persisted-operation"));
        return principal;
    }

    private static string? SingleClaim(ClaimsPrincipal principal, string type)
    {
        var claims = principal.Identities.Where(identity => identity.IsAuthenticated)
            .SelectMany(identity => identity.FindAll(type)).ToArray();
        return claims.Length == 1 ? claims[0].Value : null;
    }

    private static bool ValidIdentifier(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':' or '@');

    // The runtime type, not claim strings, establishes server provenance.
    // Clone preserves that type when authentication middleware clones identities.
    private sealed class ServerBindingIdentity : ClaimsIdentity
    {
        public AuthenticatedRequestBinding? Binding { get; }
        public string UserId { get; }
        public string TenantId { get; }
        private string Kind { get; }

        public ServerBindingIdentity(AuthenticatedRequestBinding? binding, string userId, string tenantId, string kind)
            : base("LegendServerRequestBinding")
        {
            Binding = binding; UserId = userId; TenantId = tenantId; Kind = kind;
            AddClaim(new(ClaimPrefix + "kind", kind, ClaimValueTypes.String, ClaimIssuer));
            if (binding is not null)
            {
                AddClaim(new(ClaimPrefix + "id", binding.Id, ClaimValueTypes.String, ClaimIssuer));
                AddClaim(new(ClaimPrefix + "valid-until", binding.ValidUntilUtc!.Value.ToString("O", CultureInfo.InvariantCulture),
                    ClaimValueTypes.DateTime, ClaimIssuer));
            }
        }

        public override ClaimsIdentity Clone() => new ServerBindingIdentity(Binding, UserId, TenantId, Kind);
    }
}
