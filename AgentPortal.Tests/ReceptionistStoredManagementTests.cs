using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Reception;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class ReceptionistOperatorTests
{
    private static Task<bool> AllowOwner(MarketingOwnerScope owner, CancellationToken ct) => Task.FromResult(owner == MarketingOwnerScope.Founder);
    private static ReceptionistOperatorService StoredService(Fixture f, IServiceProvider services) =>
        new(f.Store, f.Admission, f.Host, f.Protector, f.Http, f.Clock, services);

    [Fact]
    public async Task StoredCredentialsAreWriteOnlyVersionedAndDoNotActivateOrAlterOperatingSelection()
    {
        await using var f = await Fixture.CreateAsync();
        using var services = new ServiceCollection().AddSingleton(f.Options).BuildServiceProvider();
        var service = StoredService(f, services);
        var before = await service.ManagementAsync(f.Owner, AllowOwner);
        var id = await service.SaveCredentialAsync(f.Owner, new(before.ConnectionRevision, "proj_synthetic", "voice-api", Fixture.ApiSecret), AllowOwner);
        var after = await service.ManagementAsync(f.Owner, AllowOwner);
        Assert.NotEqual(before.ConnectionRevision, after.ConnectionRevision);
        Assert.Equal(id, Assert.Single(after.Credentials).Id);
        Assert.DoesNotContain(Fixture.ApiSecret, JsonSerializer.Serialize(after));
        await using var db = f.Database();
        var row = await db.Set<ReceptionistCredentialVersion>().SingleAsync();
        Assert.NotEqual(Fixture.ApiSecret, row.ProtectedValue);
        Assert.Equal(Fixture.ApiSecret, ReceptionistCredentialEnvelope.Unprotect(f.Protector, f.Owner, "openai", "proj_synthetic", "voice-api", row.ProtectedValue));
        var connection = await db.MarketingConnections.SingleAsync();
        Assert.False(connection.ReceptionistAdmissionEnabled); Assert.Null(connection.ReceptionistActiveConfigurationId);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.SaveCredentialAsync(f.Owner,
            new(before.ConnectionRevision, "proj_synthetic", "voice-api", Fixture.ApiSecret), AllowOwner));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ManagementAsync(MarketingOwnerScope.Agent(Guid.NewGuid()), AllowOwner));
        await service.RevokeCredentialAsync(f.Owner, id, after.ConnectionRevision, AllowOwner);
        Assert.True(Assert.Single((await service.ManagementAsync(f.Owner, AllowOwner)).Credentials).Revoked);
    }

    [Fact]
    public async Task StoredOperationNeedsExactReviewAndTenantReceiptsHideSharedFundingAcrossApplyAndReplay()
    {
        await using var f = await Fixture.CreateAsync();
        using var services = new ServiceCollection().AddSingleton(f.Options).BuildServiceProvider();
        var service = StoredService(f, services);
        var row = await StorePending(f);
        var key = "saved-" + row.Id.ToString("N");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PlanForOwnerAsync(f.Owner, key, row.PayloadSha256, AllowOwner));
        await service.ReviewSavedOperationAsync(row.Id, row.Revision, row.PayloadSha256, true, default);
        // Explicit delegation of this reviewed workspace operation, not Founder identity.
        f.Http.HttpContext!.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "scoped-synthetic-owner")], "test"));
        var plan = await service.PlanForOwnerAsync(f.Owner, key, row.PayloadSha256, AllowOwner);
        Assert.True(plan.CanApply);
        Assert.DoesNotContain(f.FundingId.ToString(), JsonSerializer.Serialize(plan));
        Assert.DoesNotContain("Current cash committed", JsonSerializer.Serialize(plan));
        var receipt = await service.ApplyForOwnerAsync(f.Owner, f.Request(plan), AllowOwner);
        Assert.True(receipt.ReadBackVerified);
        Assert.DoesNotContain(f.FundingId.ToString(), JsonSerializer.Serialize(receipt));
        var replay = await service.ApplyForOwnerAsync(f.Owner, f.Request(plan), AllowOwner);
        Assert.True(replay.ReadBackVerified);
        Assert.DoesNotContain(f.FundingId.ToString(), JsonSerializer.Serialize(replay));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PlanForOwnerAsync(MarketingOwnerScope.Agent(Guid.NewGuid()), key, row.PayloadSha256,
            (_, _) => Task.FromResult(true)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PlanForOwnerAsync(f.Owner, "host-package", row.PayloadSha256, AllowOwner));
    }

    [Fact]
    public async Task RevocationAfterPreviewPreventsStoredBindingApplication()
    {
        await using var f = await Fixture.CreateAsync();
        using var services = new ServiceCollection().AddSingleton(f.Options).BuildServiceProvider();
        var service = StoredService(f, services);
        var row = await StorePending(f);
        await service.ReviewSavedOperationAsync(row.Id, row.Revision, row.PayloadSha256, true, default);
        var plan = await service.PlanForOwnerAsync(f.Owner, "saved-" + row.Id.ToString("N"), row.PayloadSha256, AllowOwner);
        Assert.True(plan.CanApply);
        var current = await service.ManagementAsync(f.Owner, AllowOwner);
        await service.RevokeCredentialAsync(f.Owner, row.ApiCredentialVersionId!.Value, current.ConnectionRevision, AllowOwner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyForOwnerAsync(f.Owner, f.Request(plan), AllowOwner));
        await using var db = f.Database();
        Assert.Null((await db.MarketingConnections.SingleAsync()).ReceptionistActiveConfigurationId);
    }

    [Fact]
    public async Task AlteredPayloadAndNonFounderReviewCannotGrantAcceptance()
    {
        await using var f = await Fixture.CreateAsync();
        using var services = new ServiceCollection().AddSingleton(f.Options).BuildServiceProvider();
        var service = StoredService(f, services);
        var row = await StorePending(f);
        f.Http.HttpContext!.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "not-founder")], "test"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReviewSavedOperationAsync(row.Id, row.Revision, row.PayloadSha256, true, default));
        f.Http.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", Fixture.FounderOid)], "test"));
        await using (var db = f.Database())
        {
            var current = await db.Set<ReceptionistOperationPackage>().SingleAsync(); current.PayloadJson += " "; await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ReviewSavedOperationAsync(row.Id, row.Revision, row.PayloadSha256, true, default));
    }

    private static async Task<ReceptionistOperationPackage> StorePending(Fixture f)
    {
        var package = await f.BindingPackageAsync();
        await using var db = f.Database();
        ReceptionistCredentialVersion Credential(string purpose, string value) => new()
        {
            MarketingConnectionId = package.MarketingConnectionId, Provider = "openai", ProviderProjectKey = Fixture.Project,
            Purpose = purpose, ProtectedValue = ReceptionistCredentialEnvelope.Protect(f.Protector, f.Owner, "openai", Fixture.Project, purpose, value),
            ValueSha256 = ReceptionistConfigurationCodec.Hash(value), CreatedByActorId = Fixture.FounderOid, CreatedUtc = f.Clock.Now
        };
        var api = Credential("voice-api", Fixture.ApiSecret); var webhook = Credential("voice-webhook", Fixture.WebhookSecret);
        package = package with { ReviewedByFounderOid = "", ReviewedUtc = default,
            Binding = package.Binding! with { CredentialReference = "saved-" + api.Id.ToString("N"), WebhookSecretReference = "saved-" + webhook.Id.ToString("N") } };
        var json = JsonSerializer.Serialize(package, ReceptionistConfigurationCodec.Json);
        var row = new ReceptionistOperationPackage
        {
            MarketingConnectionId = package.MarketingConnectionId, ConfigurationVersionId = package.ConfigurationId, Operation = package.Operation,
            PayloadJson = json, PayloadSha256 = ReceptionistConfigurationCodec.Hash(json), ApiCredentialVersionId = api.Id, WebhookCredentialVersionId = webhook.Id,
            CreatedByActorId = Fixture.FounderOid, CreatedUtc = f.Clock.Now, ExpiresUtc = package.ExpiresUtc
        };
        db.AddRange(api, webhook, row); await db.SaveChangesAsync(); return row;
    }
}
