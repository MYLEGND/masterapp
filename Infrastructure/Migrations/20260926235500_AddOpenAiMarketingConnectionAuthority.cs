using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260926235500_AddOpenAiMarketingConnectionAuthority")]
public partial class AddOpenAiMarketingConnectionAuthority : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "LastVerifiedUtc",
            table: "MarketingConnections",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProviderAccountRole",
            table: "MarketingConnections",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProviderAuthorizationMethod",
            table: "MarketingConnections",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProviderDataSourceId",
            table: "MarketingConnections",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProviderPermissionsJson",
            table: "MarketingConnections",
            maxLength: 4000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProviderPixelId",
            table: "MarketingConnections",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProviderReviewStatus",
            table: "MarketingConnections",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProviderUserEmail",
            table: "MarketingConnections",
            maxLength: 320,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProviderUserId",
            table: "MarketingConnections",
            maxLength: 200,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "LastVerifiedUtc", table: "MarketingConnections");
        migrationBuilder.DropColumn(name: "ProviderAccountRole", table: "MarketingConnections");
        migrationBuilder.DropColumn(name: "ProviderAuthorizationMethod", table: "MarketingConnections");
        migrationBuilder.DropColumn(name: "ProviderDataSourceId", table: "MarketingConnections");
        migrationBuilder.DropColumn(name: "ProviderPermissionsJson", table: "MarketingConnections");
        migrationBuilder.DropColumn(name: "ProviderPixelId", table: "MarketingConnections");
        migrationBuilder.DropColumn(name: "ProviderReviewStatus", table: "MarketingConnections");
        migrationBuilder.DropColumn(name: "ProviderUserEmail", table: "MarketingConnections");
        migrationBuilder.DropColumn(name: "ProviderUserId", table: "MarketingConnections");
    }
}
