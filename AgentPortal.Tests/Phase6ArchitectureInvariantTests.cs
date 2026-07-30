using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace AgentPortal.Tests;

// Phase 6 — Architecture invariants. These lock in the centralization achieved in
// Phases 3–5 so a future refactor that deletes/renames a shared authority, or an
// application that re-introduces a duplicated core algorithm, fails the build.
// They are reflection/behavioral/narrow-source checks (not brittle formatting greps).
public class Phase6ArchitectureInvariantTests
{
    private static Type Type(string assembly, string fullName)
    {
        var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assembly)
                  ?? Assembly.Load(assembly);
        var t = asm.GetType(fullName);
        Assert.NotNull(t);
        return t!;
    }

    private static void AssertHasMethod(Type type, string method)
        => Assert.True(type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .Any(m => m.Name == method),
            $"{type.FullName} is missing expected authority method '{method}'.");

    // ---- Shared authorities exist with their expected public surface ----

    [Fact]
    public void SharedAuthorities_Exist_WithExpectedSurface()
    {
        var canonical = Type("Shared", "Shared.Auth.UserIdExtensions");
        AssertHasMethod(canonical, "GetCanonicalUserId");
        AssertHasMethod(canonical, "GetEmailCandidate");

        var founder = Type("Shared", "Shared.Auth.FounderAuthority");
        AssertHasMethod(founder, "Evaluate");
        AssertHasMethod(founder, "IsConfiguredAndValid");

        var ownership = Type("Infrastructure", "Infrastructure.Data.OwnershipQueries");
        AssertHasMethod(ownership, "AgentOwnsClientAsync");

        var dp = Type("Infrastructure", "Infrastructure.Security.PlatformDataProtection");
        AssertHasMethod(dp, "AddPlatformDataProtection");

        var headers = Type("Shared", "Shared.Security.PlatformSecurityHeaders");
        AssertHasMethod(headers, "UsePlatformSecurityHeaders");
        AssertHasMethod(headers, "AddPlatformForwardedHeaders");

        var configValidation = Type("Shared", "Shared.Security.PlatformConfigValidation");
        AssertHasMethod(configValidation, "ValidateDataProtection");

        var upload = Type("Infrastructure", "Infrastructure.Security.UploadValidation.UploadValidator");
        AssertHasMethod(upload, "ValidateContent");
        AssertHasMethod(upload, "ValidateImageContent");
        AssertHasMethod(upload, "ValidateMetadata");
        AssertHasMethod(upload, "DetectContentType");

        var rateLimiting = Type("Infrastructure", "Infrastructure.Security.PlatformRateLimiting");
        AssertHasMethod(rateLimiting, "ConfigurePolicies");
        AssertHasMethod(rateLimiting, "AddFixedWindowPolicy");
        AssertHasMethod(rateLimiting, "ResolvePartitionKey");

        var redactor = Type("Shared", "Shared.Security.LogRedactor");
        AssertHasMethod(redactor, "Redact");
        AssertHasMethod(redactor, "RedactHeader");

        var audit = Type("Shared", "Shared.Security.SecurityAuditEvent");
        Assert.NotNull(audit);
    }

    // ---- Canonical identity never falls back to email/UPN/NameIdentifier/sub ----

    [Fact]
    public void CanonicalIdentity_HasNoNonOidFallback()
    {
        var nameIdOnly = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "legacy"), new Claim("sub", "sub-val"),
                    new Claim("preferred_username", "x@y.com"), new Claim("upn", "x@y.com") },
            "TestAuth"));

        Assert.Equal(string.Empty, Shared.Auth.UserIdExtensions.GetCanonicalUserId(nameIdOnly));
        Assert.Null(Shared.Auth.ClaimsExtensions.GetOid(nameIdOnly));
    }

    // ---- Founder guards delegate to the shared fail-closed rule ----

    [Fact]
    public void FounderGuards_Delegate_And_FailClosed()
    {
        // Both app guards must reject an unauthenticated principal (delegation path).
        var anon = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.False(AgentPortal.Security.FounderGuard.IsFounder(anon));
        Assert.False(ParfaitApp.Security.ParfaitFounderGuard.IsFounder(anon));

        // The shared rule fails closed on missing/malformed OID in production.
        Assert.False(Shared.Auth.FounderAuthority.Evaluate(
            new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", Guid.NewGuid().ToString()) }, "T")),
            configuredFounderOid: "", isProduction: true, developmentEmailFallback: _ => true));
    }

    // Note: "applications delegate to shared authorities in their composition
    // roots" and "no inline Azure key-ring wiring / no committed secrets / no
    // skipped security tests" are enforced by the CI workflow's narrow source
    // checks (.github/workflows/security-ci.yml), which run from the repository
    // root. They are intentionally not file-reading unit tests here because this
    // repo redirects bin output to /tmp, so the test assembly cannot resolve the
    // source tree — a file-reading test would be brittle rather than reliable.
}
