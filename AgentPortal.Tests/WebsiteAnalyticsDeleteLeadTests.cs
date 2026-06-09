using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Services;
using AgentPortal.Services.Analytics;
using AgentPortal.Services.Tracking;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public class WebsiteAnalyticsDeleteLeadTests
{
    [Fact]
    public async Task DeleteLead_SoftDeletes_WhenAuditColumnsAreMissing()
    {
        var previousAdminOids = Environment.GetEnvironmentVariable("LEGEND_ADMIN_OIDS");
        Environment.SetEnvironmentVariable("LEGEND_ADMIN_OIDS", "admin-oid");

        try
        {
            await using var conn = new SqliteConnection("Data Source=:memory:");
            await conn.OpenAsync();

            var options = new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseSqlite(conn)
                .Options;

            await using var db = new MasterAppDbContext(options);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "WebsiteLeads" (
                    "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "LeadId" TEXT NOT NULL,
                    "FirstName" TEXT NOT NULL,
                    "Email" TEXT NOT NULL,
                    "CreatedUtc" TEXT NOT NULL,
                    "Status" TEXT NOT NULL,
                    "IsDeleted" INTEGER NOT NULL DEFAULT 0
                );
                """);

            var leadId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "WebsiteLeads" ("LeadId", "FirstName", "Email", "CreatedUtc", "Status", "IsDeleted")
                VALUES ({leadId}, {"Test"}, {"test@example.com"}, {DateTime.UtcNow}, {"New"}, {0});
                """);

            var controller = BuildController(db, "admin-oid");
            var result = await controller.DeleteLead(new WebsiteAnalyticsController.DeleteLeadRequest
            {
                LeadId = leadId,
                Reason = "QA cleanup"
            });

            var json = Assert.IsType<JsonResult>(result);
            Assert.NotNull(json.Value);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT IsDeleted FROM WebsiteLeads WHERE LeadId = $leadId";
            cmd.Parameters.AddWithValue("$leadId", leadId.ToString());

            var scalar = await cmd.ExecuteScalarAsync();
            Assert.Equal(1L, Assert.IsType<long>(scalar));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LEGEND_ADMIN_OIDS", previousAdminOids);
        }
    }

    [Fact]
    public async Task DeleteLead_HardDeletes_WhenSoftDeleteColumnIsMissing()
    {
        var previousAdminOids = Environment.GetEnvironmentVariable("LEGEND_ADMIN_OIDS");
        Environment.SetEnvironmentVariable("LEGEND_ADMIN_OIDS", "admin-oid");

        try
        {
            await using var conn = new SqliteConnection("Data Source=:memory:");
            await conn.OpenAsync();

            var options = new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseSqlite(conn)
                .Options;

            await using var db = new MasterAppDbContext(options);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "WebsiteLeads" (
                    "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "LeadId" TEXT NOT NULL,
                    "FirstName" TEXT NOT NULL,
                    "Email" TEXT NOT NULL,
                    "CreatedUtc" TEXT NOT NULL,
                    "Status" TEXT NOT NULL
                );
                """);

            var leadId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "WebsiteLeads" ("LeadId", "FirstName", "Email", "CreatedUtc", "Status")
                VALUES ({leadId}, {"Test"}, {"test@example.com"}, {DateTime.UtcNow}, {"New"});
                """);

            var controller = BuildController(db, "admin-oid");
            var result = await controller.DeleteLead(new WebsiteAnalyticsController.DeleteLeadRequest
            {
                LeadId = leadId,
                Reason = "QA cleanup"
            });

            var json = Assert.IsType<JsonResult>(result);
            Assert.NotNull(json.Value);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM WebsiteLeads WHERE LeadId = $leadId";
            cmd.Parameters.AddWithValue("$leadId", leadId.ToString());

            var scalar = await cmd.ExecuteScalarAsync();
            Assert.Equal(0L, Assert.IsType<long>(scalar));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LEGEND_ADMIN_OIDS", previousAdminOids);
        }
    }

    private static WebsiteAnalyticsController BuildController(MasterAppDbContext db, string oid)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Founder:Upn"] = "founder@example.com"
            })
            .Build();

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("oid", oid),
            new Claim("preferred_username", "admin@example.com")
        }, "TestAuth"));

        var http = new DefaultHttpContext { User = user };
        var accessor = new HttpContextAccessor { HttpContext = http };
        var tracking = new Mock<IAgentTrackingService>();
        var effective = new EffectiveAgentContext(accessor, tracking.Object, NullLogger<EffectiveAgentContext>.Instance);

        return new WebsiteAnalyticsController(
            Mock.Of<IAnalyticsQueryService>(),
            Mock.Of<IMetaAdsService>(),
            Mock.Of<IMetaAdsOAuthService>(),
            Mock.Of<IMetaAdsConnectionStore>(),
            tracking.Object,
            Mock.Of<IMetaSignalAnalyticsService>(),
            Mock.Of<ILandingRouteDiscoveryService>(),
            Mock.Of<WebsiteAnalyticsAiDataBuilder>(),
            Mock.Of<IVisitorConcentrationService>(),
            Mock.Of<IKpiDetailBreakdownService>(),
            Mock.Of<IVisitorTrustScoringService>(),
            NullLogger<WebsiteAnalyticsController>.Instance,
            db,
            config,
            effective)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }
}
