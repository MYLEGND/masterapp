using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260516115000_BackfillMissingMetaAttributionColumns")]
public partial class BackfillMissingMetaAttributionColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (string.Equals(ActiveProvider, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
        {
            migrationBuilder.Sql(
                """
                IF COL_LENGTH(N'dbo.AnalyticsEvents', N'UtmId') IS NULL
                    ALTER TABLE [dbo].[AnalyticsEvents] ADD [UtmId] nvarchar(160) NULL;

                IF COL_LENGTH(N'dbo.WebsiteLeads', N'MetaCampaignId') IS NULL
                    ALTER TABLE [dbo].[WebsiteLeads] ADD [MetaCampaignId] nvarchar(200) NULL;

                IF COL_LENGTH(N'dbo.WebsiteLeads', N'MetaAdSetId') IS NULL
                    ALTER TABLE [dbo].[WebsiteLeads] ADD [MetaAdSetId] nvarchar(200) NULL;

                IF COL_LENGTH(N'dbo.WebsiteLeads', N'MetaAdId') IS NULL
                    ALTER TABLE [dbo].[WebsiteLeads] ADD [MetaAdId] nvarchar(200) NULL;

                IF COL_LENGTH(N'dbo.WebsiteLeads', N'UtmId') IS NULL
                    ALTER TABLE [dbo].[WebsiteLeads] ADD [UtmId] nvarchar(160) NULL;

                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = N'IX_AnalyticsEvents_UtmId'
                      AND object_id = OBJECT_ID(N'dbo.AnalyticsEvents')
                )
                    CREATE INDEX [IX_AnalyticsEvents_UtmId] ON [dbo].[AnalyticsEvents] ([UtmId]);

                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = N'IX_WebsiteLeads_MetaCampaignId'
                      AND object_id = OBJECT_ID(N'dbo.WebsiteLeads')
                )
                    CREATE INDEX [IX_WebsiteLeads_MetaCampaignId] ON [dbo].[WebsiteLeads] ([MetaCampaignId]);

                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = N'IX_WebsiteLeads_UtmId'
                      AND object_id = OBJECT_ID(N'dbo.WebsiteLeads')
                )
                    CREATE INDEX [IX_WebsiteLeads_UtmId] ON [dbo].[WebsiteLeads] ([UtmId]);
                """);
            return;
        }

        migrationBuilder.AddColumn<string>(
            name: "UtmId",
            table: "AnalyticsEvents",
            type: "TEXT",
            maxLength: 160,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MetaAdId",
            table: "WebsiteLeads",
            type: "TEXT",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MetaAdSetId",
            table: "WebsiteLeads",
            type: "TEXT",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MetaCampaignId",
            table: "WebsiteLeads",
            type: "TEXT",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "UtmId",
            table: "WebsiteLeads",
            type: "TEXT",
            maxLength: 160,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_AnalyticsEvents_UtmId",
            table: "AnalyticsEvents",
            column: "UtmId");

        migrationBuilder.CreateIndex(
            name: "IX_WebsiteLeads_MetaCampaignId",
            table: "WebsiteLeads",
            column: "MetaCampaignId");

        migrationBuilder.CreateIndex(
            name: "IX_WebsiteLeads_UtmId",
            table: "WebsiteLeads",
            column: "UtmId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (string.Equals(ActiveProvider, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = N'IX_AnalyticsEvents_UtmId'
                      AND object_id = OBJECT_ID(N'dbo.AnalyticsEvents')
                )
                    DROP INDEX [IX_AnalyticsEvents_UtmId] ON [dbo].[AnalyticsEvents];

                IF EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = N'IX_WebsiteLeads_MetaCampaignId'
                      AND object_id = OBJECT_ID(N'dbo.WebsiteLeads')
                )
                    DROP INDEX [IX_WebsiteLeads_MetaCampaignId] ON [dbo].[WebsiteLeads];

                IF EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = N'IX_WebsiteLeads_UtmId'
                      AND object_id = OBJECT_ID(N'dbo.WebsiteLeads')
                )
                    DROP INDEX [IX_WebsiteLeads_UtmId] ON [dbo].[WebsiteLeads];

                IF COL_LENGTH(N'dbo.AnalyticsEvents', N'UtmId') IS NOT NULL
                    ALTER TABLE [dbo].[AnalyticsEvents] DROP COLUMN [UtmId];

                IF COL_LENGTH(N'dbo.WebsiteLeads', N'MetaAdId') IS NOT NULL
                    ALTER TABLE [dbo].[WebsiteLeads] DROP COLUMN [MetaAdId];

                IF COL_LENGTH(N'dbo.WebsiteLeads', N'MetaAdSetId') IS NOT NULL
                    ALTER TABLE [dbo].[WebsiteLeads] DROP COLUMN [MetaAdSetId];

                IF COL_LENGTH(N'dbo.WebsiteLeads', N'MetaCampaignId') IS NOT NULL
                    ALTER TABLE [dbo].[WebsiteLeads] DROP COLUMN [MetaCampaignId];

                IF COL_LENGTH(N'dbo.WebsiteLeads', N'UtmId') IS NOT NULL
                    ALTER TABLE [dbo].[WebsiteLeads] DROP COLUMN [UtmId];
                """);
            return;
        }

        migrationBuilder.DropIndex(
            name: "IX_AnalyticsEvents_UtmId",
            table: "AnalyticsEvents");

        migrationBuilder.DropIndex(
            name: "IX_WebsiteLeads_MetaCampaignId",
            table: "WebsiteLeads");

        migrationBuilder.DropIndex(
            name: "IX_WebsiteLeads_UtmId",
            table: "WebsiteLeads");

        migrationBuilder.DropColumn(
            name: "UtmId",
            table: "AnalyticsEvents");

        migrationBuilder.DropColumn(
            name: "MetaAdId",
            table: "WebsiteLeads");

        migrationBuilder.DropColumn(
            name: "MetaAdSetId",
            table: "WebsiteLeads");

        migrationBuilder.DropColumn(
            name: "MetaCampaignId",
            table: "WebsiteLeads");

        migrationBuilder.DropColumn(
            name: "UtmId",
            table: "WebsiteLeads");
    }
}
