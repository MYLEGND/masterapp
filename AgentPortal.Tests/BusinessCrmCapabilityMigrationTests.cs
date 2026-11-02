using System;
using System.Collections.Generic;
using System.Linq;
using Infrastructure.Leads;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace AgentPortal.Tests;

public sealed class BusinessCrmCapabilityMigrationTests
{
    [Fact]
    public async Task Compatibility_migration_preserves_all_prior_grants_without_changing_members()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var business = new CommerceBusiness { Key = "synthetic-crm-compatibility" };
        db.CommerceBusinesses.Add(business);
        var expected = new Dictionary<Guid, (bool Grant, string Status, bool Orders, string Role, DateTime Updated)>();
        foreach (var role in new[] { "owner", "platform-owner", "account", "staff", "OWNER", "owner ", "account " })
        foreach (var status in new[] { "Active", "Inactive", "Revoked" })
        foreach (var orders in new[] { false, true })
        {
            var member = new CommerceBusinessMember { CommerceBusinessId = business.Id, RoleKey = role, Status = status,
                CanManageOrders = orders, CanManageCrm = false, NormalizedEmail = Guid.NewGuid().ToString() };
            db.CommerceBusinessMembers.Add(member);
            expected[member.Id] = (orders || role.ToLowerInvariant() is "owner" or "platform-owner" or "account",
                status, orders, role, member.UpdatedUtc);
        }
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE CommerceBusinessMembers DROP COLUMN CanManageCrm");
        var migration = new ExplicitBusinessCrmCapability { ActiveProvider = "Microsoft.EntityFrameworkCore.Sqlite" };
        foreach (var command in db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, db.GetService<IDesignTimeModel>().Model))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        db.ChangeTracker.Clear();
        foreach (var member in await db.CommerceBusinessMembers.ToListAsync())
        {
            var before = expected[member.Id];
            Assert.Equal(before.Grant, member.CanManageCrm);
            Assert.Equal(before.Status, member.Status);
            Assert.Equal(before.Orders, member.CanManageOrders);
            Assert.Equal(before.Role, member.RoleKey);
            Assert.Equal(before.Updated, member.UpdatedUtc);
            Assert.Equal(before.Grant && before.Status == "Active", Infrastructure.Businesses.BusinessWorkspaceAccess.Allows(member, "crm"));
        }
        var newStaff = new CommerceBusinessMember { CommerceBusinessId = business.Id, RoleKey = "staff", CanManageOrders = true };
        db.Add(newStaff);
        await db.SaveChangesAsync();
        Assert.False(newStaff.CanManageCrm);
        Assert.False(Infrastructure.Businesses.BusinessWorkspaceAccess.Allows(newStaff, "crm"));
    }

    [Fact]
    public async Task Recipient_choices_use_crm_authority_without_storefront_or_orders()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "synthetic-recipient", IsActive = true, Status = "Active" };
        db.Add(business);
        Guid allowed = Guid.Empty;
        foreach (var scenario in new[] { "crm", "orders", "revoked", "foreign" })
        {
            var profile = new ClientProfile { ClientUserId = "synthetic-" + scenario, Email = scenario + "@example.invalid" };
            var member = new CommerceBusinessMember { CommerceBusinessId = scenario == "foreign" ? Guid.NewGuid() : business.Id,
                ClientProfileId = profile.Id, RoleKey = "staff", Status = scenario == "revoked" ? "Revoked" : "Active",
                CanManageCrm = scenario != "orders", CanManageStorefront = false, CanManageOrders = scenario == "orders" };
            db.AddRange(profile, member);
            if (scenario == "crm") allowed = member.Id;
        }
        await db.SaveChangesAsync();
        var options = await new WebsiteIntakeRecipientResolver(db, new ConfigurationBuilder().Build()).BusinessOptionsAsync(business.Id);
        Assert.Equal("member:" + allowed.ToString("D"), Assert.Single(options).Key);
    }

    [Fact]
    public void Unsupported_rollback_cannot_restore_implicit_orders_access()
    {
        var migration = new ExplicitBusinessCrmCapability { ActiveProvider = "Microsoft.EntityFrameworkCore.Sqlite" };
        Assert.Throws<NotSupportedException>(() => _ = migration.DownOperations);
    }
}
