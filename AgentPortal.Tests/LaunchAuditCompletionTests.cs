using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Infrastructure.Leads;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LaunchAuditCompletionTests
{
    [Fact]
    public async Task FailedHandoffRollsBackLeadAndRetryCapturesExactlyOnce()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options;
        await using var db = new MasterAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var token = Guid.NewGuid().ToString();
        WebsiteLead Lead() => new() { LeadId = Guid.NewGuid(), FirstName = "Test", Email = "test@example.test", SourcePageKey = "risk_assessment", CreatedUtc = DateTime.UtcNow };
        await Assert.ThrowsAsync<InvalidOperationException>(() => WebsiteLeadSubmission.TryCreateAsync(db, Lead(), token,
            persistHandoff: _ => throw new InvalidOperationException("Injected CRM failure")));
        db.ChangeTracker.Clear();
        Assert.Empty(await db.WebsiteLeads.ToListAsync());
        var captures = 0;
        Assert.True(await WebsiteLeadSubmission.TryCreateAsync(db, Lead(), token, persistHandoff: _ => { captures++; return Task.CompletedTask; }));
        Assert.False(await WebsiteLeadSubmission.TryCreateAsync(db, Lead(), token, persistHandoff: _ => { captures++; return Task.CompletedTask; }));
        Assert.Single(await db.WebsiteLeads.ToListAsync());
        Assert.Equal(1, captures);
    }

    [Fact]
    public async Task NotificationLeasePreventsConcurrentReplayAndFailureRemainsRetryable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options;
        await using var db = new MasterAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var lead = new WebsiteLead { LeadId = Guid.NewGuid(), FirstName = "Test", Email = "test@example.test", CreatedUtc = DateTime.UtcNow };
        await WebsiteLeadSubmission.TryCreateAsync(db, lead, Guid.NewGuid().ToString());
        Assert.True(await WebsiteLeadSubmission.TryClaimNotificationAsync(db, lead));
        await using var competitor = new MasterAppDbContext(options);
        var replay = await competitor.WebsiteLeads.SingleAsync();
        Assert.False(await WebsiteLeadSubmission.TryClaimNotificationAsync(competitor, replay));
        await WebsiteLeadSubmission.CompleteNotificationAsync(db, lead, false);
        Assert.True(await WebsiteLeadSubmission.TryClaimNotificationAsync(competitor, replay));
        await WebsiteLeadSubmission.CompleteNotificationAsync(competitor, replay, true);
        Assert.False(await WebsiteLeadSubmission.TryClaimNotificationAsync(db, lead));
        Assert.Single(await db.WebsiteLeads.ToListAsync());
    }

    [Fact]
    public void SubmissionIdentityCannotCollideAcrossAgents()
    {
        var token = Guid.NewGuid().ToString();
        var first = new WebsiteLead { Email = "test@example.test", SourcePageKey = "quote_life", AgentTrackingProfileId = Guid.NewGuid() };
        var second = new WebsiteLead { Email = first.Email, SourcePageKey = first.SourcePageKey, AgentTrackingProfileId = Guid.NewGuid() };
        Assert.NotEqual(WebsiteLeadSubmission.ResolveId(first, token), WebsiteLeadSubmission.ResolveId(second, token));
    }

    [Fact]
    public async Task LinkedOwnersRequireExactTotalAndMembershipAuthority()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var first = new ClientProfile { ClientUserId = Guid.NewGuid().ToString(), FirstName = "First", LastName = "Owner", Email = "first@example.test", CrmNotes = "{\"RecordType\":\"Client\"}" };
        var second = new ClientProfile { ClientUserId = Guid.NewGuid().ToString(), FirstName = "Second", LastName = "Owner", Email = "second@example.test", CrmNotes = "{\"RecordType\":\"Client\"}" };
        db.ClientProfiles.AddRange(first, second); await db.SaveChangesAsync();
        var service = new CommerceBusinessProvisioningService(db);
        var business = await service.CreateAsync(new CommerceBusinessProvisioningRequest("Test Entity", "Test Entity", "BusinessClient", first.Email,
            OwnerClientProfileId: first.Id, Owners: new[] { new BusinessOwnerInput(first.Id, first.Email, 60), new BusinessOwnerInput(second.Id, second.Email, 40) }));
        Assert.Equal(100m, await db.CommerceBusinessMembers.SumAsync(x => x.OwnershipPercentage ?? 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateOwnershipAsync(business.Id, first.Id, "Test Entity",
            new[] { new BusinessOwnerInput(first.Id, first.Email, 60), new BusinessOwnerInput(second.Id, second.Email, 60) }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateOwnershipAsync(business.Id, Guid.NewGuid(), "Other",
            new[] { new BusinessOwnerInput(first.Id, first.Email, 100) }));
        await service.UpdateOwnershipAsync(business.Id, first.Id, "Updated Entity", new[] { new BusinessOwnerInput(first.Id, first.Email, 100) });
        Assert.Equal("Revoked", (await db.CommerceBusinessMembers.SingleAsync(x => x.ClientProfileId == second.Id)).Status);
        Assert.Equal("Updated Entity", business.DisplayName);
        Assert.Equal("First", first.FirstName);
    }
    [Fact]
    public async Task EntityNameEditPreservesOwnersAndOtherBusinesses()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var owner = new ClientProfile { ClientUserId = Guid.NewGuid().ToString(), FirstName = "Personal", LastName = "Owner", Email = "owner@example.test" };
        db.ClientProfiles.Add(owner);
        await db.SaveChangesAsync();
        var service = new CommerceBusinessProvisioningService(db);
        var first = await service.CreateAsync(new CommerceBusinessProvisioningRequest("First Business", "First Business LLC", "BusinessClient", owner.Email, OwnerClientProfileId: owner.Id));
        var second = await service.CreateAsync(new CommerceBusinessProvisioningRequest("Second Business", "Second Business", "BusinessClient", owner.Email, OwnerClientProfileId: owner.Id));
        var membership = await db.CommerceBusinessMembers.SingleAsync(x => x.CommerceBusinessId == first.Id);
        var previousName = membership.DisplayName;
        var previousShare = membership.OwnershipPercentage;
        await service.UpdateEntityNameAsync(first.Id, owner.Id, "Renamed Business");
        Assert.Equal("Renamed Business", first.DisplayName);
        Assert.Equal("Second Business", second.DisplayName);
        Assert.Equal("First Business LLC", first.LegalName);
        Assert.Equal("Personal", owner.FirstName);
        Assert.Equal(previousName, membership.DisplayName);
        Assert.Equal(previousShare, membership.OwnershipPercentage);
        Assert.Equal("Active", membership.Status);
        Assert.Contains("Renamed Business", (await BusinessIdentityProjection.LoadLabelsAsync(db, new[] { owner.Id }))[owner.Id]);
        Assert.Contains("First Business", first.OwnershipHistoryJson!);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateEntityNameAsync(first.Id, Guid.NewGuid(), "Unauthorized"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateEntityNameAsync(first.Id, owner.Id, " "));
        Assert.Equal("Renamed Business", first.DisplayName);
    }

    [Fact]
    public void BusinessAccountLabelUsesEntityAndPersonalAccountRetainsPersonName()
    {
        var profile = new ClientProfile { ClientUserId = Guid.NewGuid().ToString(), FirstName = "Personal", LastName = "Owner", CrmNotes = "{\"RecordType\":\"BusinessClient\"}" };
        var businessContext = new ClientApp.Services.EffectiveClientContext
        {
            ClientProfileId = profile.Id, ClientUserId = profile.ClientUserId, Profile = profile,
            IsAgentView = false, EntityName = "Example Services"
        };
        Assert.Equal("Example Services", businessContext.AccountDisplayName);
        var missingName = new ClientApp.Services.EffectiveClientContext
        {
            ClientProfileId = profile.Id, ClientUserId = profile.ClientUserId, Profile = profile, IsAgentView = false
        };
        Assert.Equal("Business account", missingName.AccountDisplayName);
        profile.CrmNotes = "{\"RecordType\":\"Client\"}";
        Assert.Equal("Personal Owner", businessContext.AccountDisplayName);
    }

}
