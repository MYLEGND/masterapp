using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260516113000_BackfillMissingAgentProfileMetaColumns")]
public partial class BackfillMissingAgentProfileMetaColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (string.Equals(ActiveProvider, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
        {
            migrationBuilder.Sql(
                """
                IF COL_LENGTH(N'dbo.AgentProfiles', N'MetaPixelId') IS NULL
                    ALTER TABLE [dbo].[AgentProfiles] ADD [MetaPixelId] nvarchar(64) NULL;

                IF COL_LENGTH(N'dbo.AgentProfiles', N'MetaAccessToken') IS NULL
                    ALTER TABLE [dbo].[AgentProfiles] ADD [MetaAccessToken] nvarchar(2048) NULL;
                """);
            return;
        }

        migrationBuilder.AddColumn<string>(
            name: "MetaPixelId",
            table: "AgentProfiles",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MetaAccessToken",
            table: "AgentProfiles",
            type: "TEXT",
            maxLength: 2048,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (string.Equals(ActiveProvider, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
        {
            migrationBuilder.Sql(
                """
                IF COL_LENGTH(N'dbo.AgentProfiles', N'MetaAccessToken') IS NOT NULL
                    ALTER TABLE [dbo].[AgentProfiles] DROP COLUMN [MetaAccessToken];

                IF COL_LENGTH(N'dbo.AgentProfiles', N'MetaPixelId') IS NOT NULL
                    ALTER TABLE [dbo].[AgentProfiles] DROP COLUMN [MetaPixelId];
                """);
            return;
        }

        migrationBuilder.DropColumn(
            name: "MetaAccessToken",
            table: "AgentProfiles");

        migrationBuilder.DropColumn(
            name: "MetaPixelId",
            table: "AgentProfiles");
    }
}
