using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260327225000_AddAgentTrackingProfiles")]
public partial class AddAgentTrackingProfiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AgentTrackingProfiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AgentUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                AgentUpn = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                Slug = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                PreferredEnvironment = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AgentTrackingProfiles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AgentTrackingAliases",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AgentTrackingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Slug = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                IsCanonical = table.Column<bool>(type: "bit", nullable: false),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AgentTrackingAliases", x => x.Id);
                table.ForeignKey(
                    name: "FK_AgentTrackingAliases_AgentTrackingProfiles_AgentTrackingProfileId",
                    column: x => x.AgentTrackingProfileId,
                    principalTable: "AgentTrackingProfiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.AddColumn<Guid>(
            name: "AgentTrackingProfileId",
            table: "WebsiteLeads",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AgentSlug",
            table: "WebsiteLeads",
            type: "nvarchar(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "AgentTrackingProfileId",
            table: "AnalyticsEvents",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AgentSlug",
            table: "AnalyticsEvents",
            type: "nvarchar(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_AnalyticsEvents_AgentTrackingProfileId",
            table: "AnalyticsEvents",
            column: "AgentTrackingProfileId");

        migrationBuilder.CreateIndex(
            name: "IX_AnalyticsEvents_AgentSlug",
            table: "AnalyticsEvents",
            column: "AgentSlug");

        migrationBuilder.CreateIndex(
            name: "IX_WebsiteLeads_AgentTrackingProfileId",
            table: "WebsiteLeads",
            column: "AgentTrackingProfileId");

        migrationBuilder.CreateIndex(
            name: "IX_WebsiteLeads_AgentSlug",
            table: "WebsiteLeads",
            column: "AgentSlug");

        migrationBuilder.CreateIndex(
            name: "IX_AgentTrackingAliases_AgentTrackingProfileId_IsCanonical",
            table: "AgentTrackingAliases",
            columns: new[] { "AgentTrackingProfileId", "IsCanonical" });

        migrationBuilder.CreateIndex(
            name: "IX_AgentTrackingAliases_Slug",
            table: "AgentTrackingAliases",
            column: "Slug",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AgentTrackingProfiles_AgentUserId",
            table: "AgentTrackingProfiles",
            column: "AgentUserId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AgentTrackingProfiles_AgentUpn",
            table: "AgentTrackingProfiles",
            column: "AgentUpn");

        migrationBuilder.CreateIndex(
            name: "IX_AgentTrackingProfiles_Slug",
            table: "AgentTrackingProfiles",
            column: "Slug",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AgentTrackingAliases");

        migrationBuilder.DropTable(
            name: "AgentTrackingProfiles");

        migrationBuilder.DropIndex(
            name: "IX_AnalyticsEvents_AgentTrackingProfileId",
            table: "AnalyticsEvents");

        migrationBuilder.DropIndex(
            name: "IX_AnalyticsEvents_AgentSlug",
            table: "AnalyticsEvents");

        migrationBuilder.DropIndex(
            name: "IX_WebsiteLeads_AgentTrackingProfileId",
            table: "WebsiteLeads");

        migrationBuilder.DropIndex(
            name: "IX_WebsiteLeads_AgentSlug",
            table: "WebsiteLeads");

        migrationBuilder.DropColumn(
            name: "AgentTrackingProfileId",
            table: "WebsiteLeads");

        migrationBuilder.DropColumn(
            name: "AgentSlug",
            table: "WebsiteLeads");

        migrationBuilder.DropColumn(
            name: "AgentTrackingProfileId",
            table: "AnalyticsEvents");

        migrationBuilder.DropColumn(
            name: "AgentSlug",
            table: "AnalyticsEvents");
    }
}
