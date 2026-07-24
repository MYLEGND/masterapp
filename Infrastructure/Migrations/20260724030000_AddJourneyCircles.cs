using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

public partial class AddJourneyCircles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "JourneyCircleProfiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                IsOptedIn = table.Column<bool>(type: "bit", nullable: false),
                IsDiscoverable = table.Column<bool>(type: "bit", nullable: false),
                AllowSuggestions = table.Column<bool>(type: "bit", nullable: false),
                AllowConnectionRequests = table.Column<bool>(type: "bit", nullable: false),
                DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                LifeStage = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                LocationLabel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                Introduction = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true),
                GoalsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                InterestsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                CircleCodesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                ConnectionTypesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                CommunicationStyle = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                AccountabilityFrequency = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                CommunityAccessState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                ConsentAffirmedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ConnectionKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                RequesterClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RecipientClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                ConnectionReason = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                Introduction = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                RespondedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_JourneyCircleConnections", x => x.Id));

        migrationBuilder.CreateTable(
            name: "JourneyCircleBlocks",
            columns: table => new { Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false), BlockerClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false), BlockedClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false), CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false) },
            constraints: table => table.PrimaryKey("PK_JourneyCircleBlocks", x => x.Id));

        migrationBuilder.CreateTable(
            name: "JourneyCircleReports",
            columns: table => new { Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false), ReporterClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false), ReportedClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false), Category = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false), Detail = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true), Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false), CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false) },
            constraints: table => table.PrimaryKey("PK_JourneyCircleReports", x => x.Id));

        migrationBuilder.CreateTable(
            name: "JourneyCircleModerationEvents",
            columns: table => new { Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false), ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false), Surface = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false), Category = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false), Severity = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false), Action = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false), PolicyVersion = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false), ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true), RequiresReview = table.Column<bool>(type: "bit", nullable: false), CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false) },
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
