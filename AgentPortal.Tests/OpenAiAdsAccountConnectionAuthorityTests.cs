using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Infrastructure.Analytics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class OpenAiAdsAccountConnectionAuthorityTests
{
    [Theory]
    [InlineData("founder")]
    [InlineData("agent")]
    [InlineData("business")]
    public async Task VerifiedAccountBindsToExactlyOneMarketingOwner(string ownerKind)
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var authority = new OpenAiAdsAccountConnectionAuthority(db, protector);
        var owner = Owner(ownerKind);

        var snapshot = await authority.BindVerifiedAsync(
            owner,
            Verified("acct_shared", "LEGEND Growth", OpenAiAdsAccountRoles.Admin),
            new("management-secret", "capi-secret"));

        Assert.True(snapshot.Exists);
        Assert.True(snapshot.Connected);
        Assert.Equal(owner.Key, snapshot.Owner.Key);
        Assert.Equal("acct_shared", snapshot.AccountId);
        Assert.Equal(OpenAiAdsAccountRoles.Admin, snapshot.Role);
        Assert.Equal(OpenAiAdsReviewStatuses.Approved, snapshot.ReviewStatus);
        Assert.Equal("pixel_openai_123", snapshot.PixelId);
        Assert.Equal("source_openai_123", snapshot.ConversionDataSourceId);
        Assert.True(snapshot.HasManagementCredential);
        Assert.True(snapshot.HasConversionsApiCredential);

        var row = await db.MarketingConnections.SingleAsync();
        Assert.Equal(owner.Key, row.OwnerKey);
        Assert.Equal(MarketingDestinationKeys.OpenAi, row.Provider);
        Assert.Equal(owner.AgentTrackingProfileId, row.AgentTrackingProfileId);
        Assert.Equal(owner.CommerceBusinessId, row.CommerceBusinessId);
        Assert.DoesNotContain("management-secret", row.AdsAccessTokenCiphertext!);
        Assert.DoesNotContain("capi-secret", row.CapiAccessTokenCiphertext!);
    }

    [Fact]
    public async Task SameOpenAiAdvertiserAccountMayBeAuthorizedForDifferentOwnersWithoutCredentialBleed()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var authority = new OpenAiAdsAccountConnectionAuthority(db, protector);
        var a = MarketingOwnerScope.Business(Guid.NewGuid());
        var b = MarketingOwnerScope.Business(Guid.NewGuid());

        await authority.BindVerifiedAsync(a, Verified("acct_shared", "Shared Account", OpenAiAdsAccountRoles.Member), new("a-management", "a-capi"));
        await authority.BindVerifiedAsync(b, Verified("acct_shared", "Shared Account", OpenAiAdsAccountRoles.Viewer), new("b-management", "b-capi"));

        var rows = await db.MarketingConnections.OrderBy(x => x.OwnerKey).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(MarketingDestinationKeys.OpenAi, row.Provider));

        var rowA = rows.Single(x => x.OwnerKey == a.Key);
        var rowB = rows.Single(x => x.OwnerKey == b.Key);
        Assert.Equal("a-management", protector.Unprotect(a, MarketingDestinationKeys.OpenAi, rowA.AdsAccessTokenCiphertext));
        Assert.Equal("b-management", protector.Unprotect(b, MarketingDestinationKeys.OpenAi, rowB.AdsAccessTokenCiphertext));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(a, MarketingDestinationKeys.OpenAi, rowB.AdsAccessTokenCiphertext));
    }

    [Fact]
    public async Task ProviderBoundCredentialCannotBeReinterpretedAsMetaForSameOwner()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var owner = MarketingOwnerScope.Founder;
        var authority = new OpenAiAdsAccountConnectionAuthority(db, protector);

        await authority.BindVerifiedAsync(owner, Verified("acct_1", "Founder Ads", OpenAiAdsAccountRoles.Admin), new("openai-secret", "conversion-secret"));
        var row = await db.MarketingConnections.SingleAsync();

        Assert.Equal("openai-secret", protector.Unprotect(owner, MarketingDestinationKeys.OpenAi, row.AdsAccessTokenCiphertext));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(owner, row.AdsAccessTokenCiphertext));
    }

    [Fact]
    public async Task VerifiedMetadataRefreshPreservesExistingCredentialsUnlessExplicitlyRotated()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var authority = new OpenAiAdsAccountConnectionAuthority(db, protector);
        var owner = MarketingOwnerScope.Founder;

        var first = await authority.BindVerifiedAsync(
            owner,
            Verified("acct_refresh", "Founder Ads", OpenAiAdsAccountRoles.Admin),
            new("management-secret", "capi-secret"));

        var refreshed = await authority.BindVerifiedAsync(
            owner,
            Verified("acct_refresh", "Founder Ads Updated", OpenAiAdsAccountRoles.Admin) with
            {
                ReviewStatus = OpenAiAdsReviewStatuses.InReview
            },
            new(),
            first.Revision);

        var secrets = await authority.GetSecretsAsync(owner);
        Assert.Equal("management-secret", secrets.ManagementApiKey);
        Assert.Equal("capi-secret", secrets.ConversionsApiKey);
        Assert.Equal(OpenAiAdsReviewStatuses.InReview, refreshed.ReviewStatus);

        var rotated = await authority.BindVerifiedAsync(
            owner,
            Verified("acct_refresh", "Founder Ads Updated", OpenAiAdsAccountRoles.Admin),
            new(ManagementApiKey: "management-rotated"),
            refreshed.Revision);

        var rotatedSecrets = await authority.GetSecretsAsync(owner);
        Assert.Equal("management-rotated", rotatedSecrets.ManagementApiKey);
        Assert.Equal("capi-secret", rotatedSecrets.ConversionsApiKey);
        Assert.Equal(OpenAiAdsReviewStatuses.Approved, rotated.ReviewStatus);
    }

    [Fact]
    public async Task DisconnectClearsCredentialsButRetainsAccountReceiptForAuditAndReconnect()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var authority = new OpenAiAdsAccountConnectionAuthority(db, protector);
        var owner = MarketingOwnerScope.Agent(Guid.NewGuid());

        var connected = await authority.BindVerifiedAsync(owner, Verified("acct_agent", "Agent Ads", OpenAiAdsAccountRoles.Member), new("mgmt", "capi"));
        var disconnected = await authority.DisconnectAsync(owner, connected.Revision);

        Assert.False(disconnected.Connected);
        Assert.Equal("acct_agent", disconnected.AccountId);
        Assert.False(disconnected.HasManagementCredential);
        Assert.False(disconnected.HasConversionsApiCredential);
        Assert.NotNull(disconnected.DisconnectedUtc);

        var reconnected = await authority.BindVerifiedAsync(
            owner,
            Verified("acct_agent", "Agent Ads", OpenAiAdsAccountRoles.Member),
            new("mgmt-2", "capi-2"),
            disconnected.Revision);

        Assert.True(reconnected.Connected);
        Assert.Null(reconnected.DisconnectedUtc);
        Assert.NotEqual(disconnected.Revision, reconnected.Revision);
    }

    [Fact]
    public async Task StaleRevisionCannotOverwriteOrDisconnectOpenAiBinding()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var authority = new OpenAiAdsAccountConnectionAuthority(db, protector);
        var owner = MarketingOwnerScope.Business(Guid.NewGuid());

        var first = await authority.BindVerifiedAsync(owner, Verified("acct_1", "Account One", OpenAiAdsAccountRoles.Admin), new());
        var second = await authority.BindVerifiedAsync(owner, Verified("acct_1", "Account One Updated", OpenAiAdsAccountRoles.Admin), new(), first.Revision);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            authority.BindVerifiedAsync(owner, Verified("acct_2", "Foreign overwrite", OpenAiAdsAccountRoles.Admin), new(), first.Revision));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            authority.DisconnectAsync(owner, first.Revision));

        Assert.Equal(second.Revision, (await authority.GetAsync(owner)).Revision);
        Assert.Equal("acct_1", (await authority.GetAsync(owner)).AccountId);
    }

    [Fact]
    public async Task InvalidRoleReviewStatusAndFutureVerificationFailClosed()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var authority = new OpenAiAdsAccountConnectionAuthority(db, protector);
        var owner = MarketingOwnerScope.Founder;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            authority.BindVerifiedAsync(owner, Verified("acct", "Name", "owner"), new()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            authority.BindVerifiedAsync(owner, Verified("acct", "Name", OpenAiAdsAccountRoles.Admin) with { ReviewStatus = "ready" }, new()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            authority.BindVerifiedAsync(owner, Verified("acct", "Name", OpenAiAdsAccountRoles.Admin) with { VerifiedUtc = DateTime.UtcNow.AddHours(1) }, new()));

        Assert.Empty(db.MarketingConnections);
    }

    [Fact]
    public async Task DestinationUsesCanonicalMappingAndScopedMeasurementReadiness()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var authority = new OpenAiAdsAccountConnectionAuthority(db, protector);
        var owner = MarketingOwnerScope.Founder;
        await authority.BindVerifiedAsync(
            owner,
            Verified("acct", "Founder Ads", OpenAiAdsAccountRoles.Admin),
            new("management-secret", "capi-secret"));

        var destination = new OpenAiMarketingDestination(authority);
        var decision = await destination.EvaluateAsync(owner, new MarketingOutcome("Lead", "event-1", true));

        Assert.True(decision.Supported);
        Assert.True(decision.Configured);
        Assert.True(decision.MappingReady);
        Assert.Equal("mapping_ready", decision.Reason);
    }

    private static MarketingOwnerScope Owner(string kind) => kind switch
    {
        "founder" => MarketingOwnerScope.Founder,
        "agent" => MarketingOwnerScope.Agent(Guid.NewGuid()),
        "business" => MarketingOwnerScope.Business(Guid.NewGuid()),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static VerifiedOpenAiAdsAccount Verified(string id, string name, string role) =>
        new(
            id,
            name,
            role,
            OpenAiAdsReviewStatuses.Approved,
            OpenAiAdsAuthorizationMethods.InvitedOperator,
            ProviderUserId: "user_123",
            ProviderUserEmail: "operator@example.com",
            Permissions: ["ad_account.users.read", "ad_account.campaigns.read"],
            PixelId: "pixel_openai_123",
            ConversionDataSourceId: "source_openai_123",
            VerifiedUtc: DateTime.UtcNow);
}
