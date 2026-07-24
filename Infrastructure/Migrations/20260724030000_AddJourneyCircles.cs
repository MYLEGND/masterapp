using Microsoft.EntityFrameworkCore.Migrations;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260724030000_AddJourneyCircles")]
public partial class AddJourneyCircles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "JourneyCircleProfiles",
            columns: table => new
            {
                Id = table.Column<Guid>(nullable: false),
                ClientProfileId = table.Column<Guid>(nullable: false),
                IsOptedIn = table.Column<bool>(nullable: false),
                IsDiscoverable = table.Column<bool>(nullable: false),
                AllowSuggestions = table.Column<bool>(nullable: false),
                AllowConnectionRequests = table.Column<bool>(nullable: false),
                DisplayName = table.Column<string>(maxLength: 100, nullable: false),
                LifeStage = table.Column<string>(maxLength: 80, nullable: true),
                LocationLabel = table.Column<string>(maxLength: 100, nullable: true),
                Introduction = table.Column<string>(maxLength: 600, nullable: true),
                GoalsJson = table.Column<string>(nullable: false),
                InterestsJson = table.Column<string>(nullable: false),
                CircleCodesJson = table.Column<string>(nullable: false),
                ConnectionTypesJson = table.Column<string>(nullable: false),
                CommunicationStyle = table.Column<string>(maxLength: 80, nullable: true),
                AccountabilityFrequency = table.Column<string>(maxLength: 80, nullable: true),
                CommunityAccessState = table.Column<string>(maxLength: 40, nullable: true),
                ConsentAffirmedUtc = table.Column<DateTime>(nullable: true),
                CreatedUtc = table.Column<DateTime>(nullable: false),
                UpdatedUtc = table.Column<DateTime>(nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_JourneyCircleProfiles", x => x.Id);
                table.ForeignKey("FK_JourneyCircleProfiles_ClientProfiles_ClientProfileId", x => x.ClientProfileId, "ClientProfiles", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "JourneyCircleConnections",
            columns: table => new
            {
                Id = table.Column<Guid>(nullable: false),
                ConnectionKey = table.Column<string>(maxLength: 80, nullable: false),
                RequesterClientProfileId = table.Column<Guid>(nullable: false),
                RecipientClientProfileId = table.Column<Guid>(nullable: false),
                Status = table.Column<string>(maxLength: 40, nullable: false),
                ConnectionReason = table.Column<string>(maxLength: 160, nullable: true),
                Introduction = table.Column<string>(maxLength: 600, nullable: true),
                CreatedUtc = table.Column<DateTime>(nullable: false),
                UpdatedUtc = table.Column<DateTime>(nullable: false),
                RespondedUtc = table.Column<DateTime>(nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_JourneyCircleConnections", x => x.Id));

        migrationBuilder.CreateTable(
            name: "JourneyCircleBlocks",
            columns: table => new { Id = table.Column<Guid>(nullable: false), BlockerClientProfileId = table.Column<Guid>(nullable: false), BlockedClientProfileId = table.Column<Guid>(nullable: false), CreatedUtc = table.Column<DateTime>(nullable: false) },
            constraints: table => table.PrimaryKey("PK_JourneyCircleBlocks", x => x.Id));

        migrationBuilder.CreateTable(
            name: "JourneyCircleReports",
            columns: table => new { Id = table.Column<Guid>(nullable: false), ReporterClientProfileId = table.Column<Guid>(nullable: false), ReportedClientProfileId = table.Column<Guid>(nullable: false), Category = table.Column<string>(maxLength: 80, nullable: false), Detail = table.Column<string>(maxLength: 600, nullable: true), Status = table.Column<string>(maxLength: 40, nullable: false), CreatedUtc = table.Column<DateTime>(nullable: false) },
            constraints: table => table.PrimaryKey("PK_JourneyCircleReports", x => x.Id));

        migrationBuilder.CreateTable(
            name: "JourneyCircleModerationEvents",
            columns: table => new { Id = table.Column<Guid>(nullable: false), ActorUserId = table.Column<string>(maxLength: 450, nullable: false), Surface = table.Column<string>(maxLength: 80, nullable: false), Category = table.Column<string>(maxLength: 80, nullable: false), Severity = table.Column<string>(maxLength: 40, nullable: false), Action = table.Column<string>(maxLength: 80, nullable: false), PolicyVersion = table.Column<string>(maxLength: 40, nullable: false), ConnectionId = table.Column<Guid>(nullable: true), RequiresReview = table.Column<bool>(nullable: false), CreatedUtc = table.Column<DateTime>(nullable: false) },
            constraints: table => table.PrimaryKey("PK_JourneyCircleModerationEvents", x => x.Id));

        migrationBuilder.CreateIndex(name: "IX_JourneyCircleProfiles_ClientProfileId", table: "JourneyCircleProfiles", column: "ClientProfileId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_JourneyCircleProfiles_IsOptedIn_IsDiscoverable_AllowSuggestions", table: "JourneyCircleProfiles", columns: new[] { "IsOptedIn", "IsDiscoverable", "AllowSuggestions" });
        migrationBuilder.CreateIndex(name: "IX_JourneyCircleConnections_ConnectionKey", table: "JourneyCircleConnections", column: "ConnectionKey", unique: true);
        migrationBuilder.CreateIndex(name: "IX_JourneyCircleConnections_RecipientClientProfileId_Status", table: "JourneyCircleConnections", columns: new[] { "RecipientClientProfileId", "Status" });
        migrationBuilder.CreateIndex(name: "IX_JourneyCircleConnections_RequesterClientProfileId_Status", table: "JourneyCircleConnections", columns: new[] { "RequesterClientProfileId", "Status" });
        migrationBuilder.CreateIndex(name: "IX_JourneyCircleBlocks_BlockerClientProfileId_BlockedClientProfileId", table: "JourneyCircleBlocks", columns: new[] { "BlockerClientProfileId", "BlockedClientProfileId" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_JourneyCircleReports_Status_CreatedUtc", table: "JourneyCircleReports", columns: new[] { "Status", "CreatedUtc" });
        migrationBuilder.CreateIndex(name: "IX_JourneyCircleModerationEvents_RequiresReview_CreatedUtc", table: "JourneyCircleModerationEvents", columns: new[] { "RequiresReview", "CreatedUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "JourneyCircleBlocks");
        migrationBuilder.DropTable(name: "JourneyCircleConnections");
        migrationBuilder.DropTable(name: "JourneyCircleModerationEvents");
        migrationBuilder.DropTable(name: "JourneyCircleProfiles");
        migrationBuilder.DropTable(name: "JourneyCircleReports");
    }
}
