using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260526230124_AddAnalyticsForensicTrafficSignals")]
    public partial class AddAnalyticsForensicTrafficSignals : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.AddColumn<string>(name: "IpAddress", table: "AnalyticsEvents", type: "TEXT", maxLength: 100, nullable: true);
                migrationBuilder.AddColumn<bool>(name: "IsHeadless", table: "AnalyticsEvents", type: "INTEGER", nullable: true);
                migrationBuilder.AddColumn<int>(name: "MouseMoveCount", table: "AnalyticsEvents", type: "INTEGER", nullable: true);
                migrationBuilder.AddColumn<string>(name: "UserAgent", table: "AnalyticsEvents", type: "TEXT", maxLength: 2048, nullable: true);
                migrationBuilder.AddColumn<int>(name: "VisibilityChangeCount", table: "AnalyticsEvents", type: "INTEGER", nullable: true);
                migrationBuilder.AddColumn<bool>(name: "WebDriver", table: "AnalyticsEvents", type: "INTEGER", nullable: true);
                return;
            }

            migrationBuilder.Sql("""
IF COL_LENGTH(N'dbo.AnalyticsEvents', N'IpAddress') IS NULL
    ALTER TABLE [dbo].[AnalyticsEvents] ADD [IpAddress] nvarchar(100) NULL;

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'IsHeadless') IS NULL
    ALTER TABLE [dbo].[AnalyticsEvents] ADD [IsHeadless] bit NULL;

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'MouseMoveCount') IS NULL
    ALTER TABLE [dbo].[AnalyticsEvents] ADD [MouseMoveCount] int NULL;

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'UserAgent') IS NULL
    ALTER TABLE [dbo].[AnalyticsEvents] ADD [UserAgent] nvarchar(2048) NULL;

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'VisibilityChangeCount') IS NULL
    ALTER TABLE [dbo].[AnalyticsEvents] ADD [VisibilityChangeCount] int NULL;

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'WebDriver') IS NULL
    ALTER TABLE [dbo].[AnalyticsEvents] ADD [WebDriver] bit NULL;
""");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (!migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.DropColumn(name: "WebDriver", table: "AnalyticsEvents");
                migrationBuilder.DropColumn(name: "VisibilityChangeCount", table: "AnalyticsEvents");
                migrationBuilder.DropColumn(name: "UserAgent", table: "AnalyticsEvents");
                migrationBuilder.DropColumn(name: "MouseMoveCount", table: "AnalyticsEvents");
                migrationBuilder.DropColumn(name: "IsHeadless", table: "AnalyticsEvents");
                migrationBuilder.DropColumn(name: "IpAddress", table: "AnalyticsEvents");
                return;
            }

            migrationBuilder.Sql("""
IF COL_LENGTH(N'dbo.AnalyticsEvents', N'WebDriver') IS NOT NULL
    ALTER TABLE [dbo].[AnalyticsEvents] DROP COLUMN [WebDriver];

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'VisibilityChangeCount') IS NOT NULL
    ALTER TABLE [dbo].[AnalyticsEvents] DROP COLUMN [VisibilityChangeCount];

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'UserAgent') IS NOT NULL
    ALTER TABLE [dbo].[AnalyticsEvents] DROP COLUMN [UserAgent];

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'MouseMoveCount') IS NOT NULL
    ALTER TABLE [dbo].[AnalyticsEvents] DROP COLUMN [MouseMoveCount];

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'IsHeadless') IS NOT NULL
    ALTER TABLE [dbo].[AnalyticsEvents] DROP COLUMN [IsHeadless];

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'IpAddress') IS NOT NULL
    ALTER TABLE [dbo].[AnalyticsEvents] DROP COLUMN [IpAddress];
""");
        }
    }
}
