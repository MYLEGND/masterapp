using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Shared.Auth;
using Xunit;

namespace AgentPortal.Tests;

// Phase 3 — Identity & Authorization Authority regression coverage.
//   Objective 1: founder authority is fail-closed on canonical Object ID.
//   Objective 2: canonical identity extraction never falls back to
//                NameIdentifier/sub/email/UPN in authoritative helpers.
//   Objective 3: client-ownership decisions flow through the one shared query.
public class Phase3IdentityAuthorityTests
{
    private static ClaimsPrincipal Principal(
        string? oid = null,
        string? email = null,
        string? nameId = null,
        string? upn = null,
        bool authenticated = true)
    {
        var claims = new List<Claim>();
        if (oid != null) claims.Add(new Claim("oid", oid));
        if (email != null) claims.Add(new Claim("preferred_username", email));
        if (nameId != null) claims.Add(new Claim(ClaimTypes.NameIdentifier, nameId));
        if (upn != null) claims.Add(new Claim("upn", upn));

        // A non-null authenticationType makes Identity.IsAuthenticated == true.
        var identity = authenticated
            ? new ClaimsIdentity(claims, "TestAuth")
            : new ClaimsIdentity(claims);
        return new ClaimsPrincipal(identity);
    }

    // =====================================================================
    // OBJECTIVE 1 — Founder authority (shared fail-closed rule)
    // =====================================================================

    [Fact] // (1) Correct configured founder Object ID grants founder access.
    public void Founder_ValidConfiguredOid_Grants()
    {
        var founderOid = Guid.NewGuid().ToString();
        var user = Principal(oid: founderOid);

        Assert.True(FounderAuthority.Evaluate(
            user, founderOid, isProduction: true, developmentEmailFallback: _ => false));
    }

    [Fact] // (2) Matching email with the wrong Object ID does NOT grant access.
    public void Founder_MatchingEmailWrongOid_Denied()
    {
        var configured = Guid.NewGuid().ToString();
        var user = Principal(oid: Guid.NewGuid().ToString(), email: "owner@mylegnd.com");

        // Even with an email fallback that would say "yes", the valid OID path is
        // authoritative and the mismatched OID must lose.
        Assert.False(FounderAuthority.Evaluate(
            user, configured, isProduction: true, developmentEmailFallback: _ => true));
    }

    [Fact] // (3) Missing production FOUNDER_OID fails closed.
    public void Founder_MissingOidInProduction_FailsClosed()
    {
        var user = Principal(oid: Guid.NewGuid().ToString(), email: "owner@mylegnd.com");

        Assert.False(FounderAuthority.Evaluate(
            user, configuredFounderOid: "", isProduction: true, developmentEmailFallback: _ => true));
    }

    [Fact] // (4) Malformed Object ID fails closed (even outside production).
    public void Founder_MalformedOid_FailsClosed()
    {
        var user = Principal(oid: "not-a-guid", email: "owner@mylegnd.com");

        Assert.False(FounderAuthority.Evaluate(
            user, configuredFounderOid: "not-a-guid", isProduction: false, developmentEmailFallback: _ => true));
        Assert.False(FounderAuthority.Evaluate(
            user, configuredFounderOid: "not-a-guid", isProduction: true, developmentEmailFallback: _ => true));
        Assert.False(FounderAuthority.IsConfiguredAndValid("not-a-guid"));
        Assert.True(FounderAuthority.IsConfiguredAndValid(Guid.NewGuid().ToString()));
    }

    [Fact] // (5) The authorized founder is recognized (impersonation gate depends on this).
    public void Founder_ConfiguredFounder_IsAvailableForImpersonationGate()
    {
        var founderOid = Guid.NewGuid().ToString();
        var founder = Principal(oid: founderOid);

        // FounderImpersonationService/Middleware gate on FounderGuard.IsFounder,
        // which delegates here; a valid founder must still evaluate true.
        Assert.True(FounderAuthority.Evaluate(
            founder, founderOid, isProduction: true, developmentEmailFallback: _ => false));
    }

    [Fact] // (6) Non-founder access remains denied.
    public void Founder_NonFounder_Denied()
    {
        var configured = Guid.NewGuid().ToString();
        var stranger = Principal(oid: Guid.NewGuid().ToString());

        Assert.False(FounderAuthority.Evaluate(
            stranger, configured, isProduction: true, developmentEmailFallback: _ => false));
    }

    [Fact] // Development convenience preserved (only when no OID configured, non-prod).
    public void Founder_DevEmailFallback_OnlyWhenNoOidConfigured_AndNotProduction()
    {
        var user = Principal(email: "owner@mylegnd.com");

        Assert.True(FounderAuthority.Evaluate(
            user, configuredFounderOid: "", isProduction: false, developmentEmailFallback: _ => true));
        // Same situation in production must not use the email fallback.
        Assert.False(FounderAuthority.Evaluate(
            user, configuredFounderOid: "", isProduction: true, developmentEmailFallback: _ => true));
    }

    [Fact] // Regression: a configured OID must never fall through to email on mismatch (Parfait bug).
    public void Founder_ConfiguredOid_DoesNotFallThroughToEmail()
    {
        var configured = Guid.NewGuid().ToString();
        var user = Principal(oid: Guid.NewGuid().ToString(), email: "owner@mylegnd.com");

        Assert.False(FounderAuthority.Evaluate(
            user, configured, isProduction: false, developmentEmailFallback: _ => true));
    }

    [Fact] // Unauthenticated principals are never founders (both guards delegate here).
    public void Founder_Unauthenticated_Denied()
    {
        var anon = Principal(oid: Guid.NewGuid().ToString(), authenticated: false);
        Assert.False(FounderAuthority.Evaluate(
            anon, Guid.NewGuid().ToString(), isProduction: false, developmentEmailFallback: _ => true));

        Assert.False(AgentPortal.Security.FounderGuard.IsFounder(null));
        Assert.False(AgentPortal.Security.FounderGuard.IsFounder(anon));
        Assert.False(ParfaitApp.Security.ParfaitFounderGuard.IsFounder(null));
        Assert.False(ParfaitApp.Security.ParfaitFounderGuard.IsFounder(anon));
    }

    [Fact]
    public void Founder_UnsetEnvironmentTreatedAsProduction()
    {
        Assert.True(FounderAuthority.IsProductionEnvironment(null));
        Assert.True(FounderAuthority.IsProductionEnvironment(""));
        Assert.True(FounderAuthority.IsProductionEnvironment("Production"));
        Assert.False(FounderAuthority.IsProductionEnvironment("Development"));
    }

    // =====================================================================
    // OBJECTIVE 2 — Canonical identity extraction
    // =====================================================================

    [Fact] // (1) Object ID is returned deterministically (normalized).
    public void Identity_CanonicalOid_Deterministic()
    {
        var oid = Guid.NewGuid().ToString().ToUpperInvariant();
        var user = Principal(oid: oid);
        Assert.Equal(oid.ToLowerInvariant(), user.GetCanonicalUserId());
    }

    [Fact] // (2)/(3) Missing OID does not fall back to email or UPN.
    public void Identity_MissingOid_DoesNotFallBackToEmailOrUpn()
    {
        var emailOnly = Principal(email: "someone@mylegnd.com");
        Assert.Equal(string.Empty, emailOnly.GetCanonicalUserId());

        var upnOnly = Principal(upn: "someone@mylegnd.com");
        Assert.Equal(string.Empty, upnOnly.GetCanonicalUserId());
    }

    [Fact] // (4) Authoritative helpers do not fall back to NameIdentifier/sub.
    public void Identity_MissingOid_DoesNotFallBackToNameIdentifier()
    {
        var nameIdOnly = Principal(nameId: "legacy-nameid");
        Assert.Equal(string.Empty, nameIdOnly.GetCanonicalUserId());
        Assert.Null(nameIdOnly.GetOid());

        // Positive control: with an oid present both authoritative reads return it.
        var withOid = Principal(oid: "abc123", nameId: "legacy-nameid");
        Assert.Equal("abc123", withOid.GetCanonicalUserId());
        Assert.Equal("abc123", withOid.GetOid());
    }

    [Fact] // (5) Email candidate is available only through its explicitly named, non-authoritative API.
    public void Identity_EmailCandidate_IsSeparateFromCanonicalId()
    {
        var user = Principal(oid: "oid-123", email: "person@mylegnd.com");

        Assert.Equal("person@mylegnd.com", user.GetEmailCandidate());
        Assert.Equal("oid-123", user.GetCanonicalUserId());
        Assert.NotEqual(user.GetEmailCandidate(), user.GetCanonicalUserId());

        // Email candidate never masquerades as an OID: with no oid it is still
        // an email, while the canonical id is empty.
        var noOid = Principal(email: "person@mylegnd.com");
        Assert.Equal("person@mylegnd.com", noOid.GetEmailCandidate());
        Assert.Equal(string.Empty, noOid.GetCanonicalUserId());
    }

    [Fact] // Migration candidate set intentionally retains legacy ids (compat), separate from canonical.
    public void Identity_MigrationCandidates_RetainLegacyButCanonicalDoesNot()
    {
        var user = Principal(oid: "oid-1", nameId: "legacy-1");
        var candidates = user.GetUserIdCandidates();
        Assert.Contains("oid-1", candidates);
        Assert.Contains("legacy-1", candidates);   // available for self-healing lookups
        Assert.Equal("oid-1", user.GetCanonicalUserId()); // but canonical is oid-only
    }

    // =====================================================================
    // OBJECTIVE 3 — Client ownership authority (shared query)
    // =====================================================================

    private static async Task SeedLink(MasterAppDbContext db, string agentOid, string clientUserId, string? agentUpn = null)
    {
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = agentOid,
            ClientUserId = clientUserId,
            AgentUpn = agentUpn ?? string.Empty,
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Fact] // (1) The rightful agent passes through the shared ownership query.
    public async Task Ownership_RightfulAgent_Passes()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedLink(db, "agent-oid-1", "client-1");

        Assert.True(await db.AgentOwnsClientAsync("agent-oid-1", "client-1"));
    }

    [Fact] // (2) An unrelated agent is rejected.
    public async Task Ownership_UnrelatedAgent_Rejected()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedLink(db, "agent-oid-1", "client-1");

        Assert.False(await db.AgentOwnsClientAsync("agent-oid-2", "client-1"));
    }

    [Fact] // (4) An arbitrary email/UPN cannot override a conflicting Object ID.
    public async Task Ownership_WrongOid_WithNonMatchingUpn_Rejected()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        // The stored link belongs to agent-oid-1 (no UPN captured).
        await SeedLink(db, "agent-oid-1", "client-1");

        // Attacker presents a different canonical OID and an unrelated UPN.
        Assert.False(await db.AgentOwnsClientAsync(
            "attacker-oid",
            "client-1",
            agentUpn: "attacker@mylegnd.com",
            agentIdCandidates: new[] { "attacker-oid", "attacker-nameid" }));
    }

    [Fact] // Explicit, retained UPN compatibility (F20 deferred): a stored UPN link still resolves.
    public async Task Ownership_LegacyUpnLink_ResolvesViaExplicitUpnParameter()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        // Legacy link stored under UPN with no canonical OID backfilled.
        await SeedLink(db, agentOid: "", clientUserId: "client-1", agentUpn: "agent@mylegnd.com");

        Assert.True(await db.AgentOwnsClientAsync(
            "agent-oid-1",              // present canonical OID (doesn't match the empty stored id)
            "client-1",
            agentUpn: "agent@mylegnd.com"));
    }

    [Fact] // (3) The shared query is the single predicate the consolidated call sites depend on.
    public async Task Ownership_SharedQuery_MatchesByCanonicalOidOrCandidate()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedLink(db, "agent-oid-1", "client-1");

        // Match by canonical oid passed as the authoritative agent id.
        Assert.True(await db.AgentOwnsClientAsync("agent-oid-1", "client-1"));
        // Match by legacy candidate (self-healing) while authoritative id differs.
        Assert.True(await db.AgentOwnsClientAsync(
            "agent-oid-1", "client-1", agentIdCandidates: new[] { "agent-oid-1", "legacy" }));
        // No match for an unknown client.
        Assert.False(await db.AgentOwnsClientAsync("agent-oid-1", "client-unknown"));
    }
}
