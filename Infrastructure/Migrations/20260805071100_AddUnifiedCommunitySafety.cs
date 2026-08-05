using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUnifiedCommunitySafety : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JourneyCircleBlocks_BlockerClientProfileId_BlockedClientProfileId",
                table: "JourneyCircleBlocks");

            migrationBuilder.AlterColumn<Guid>(
                name: "ReporterClientProfileId",
                table: "JourneyCircleReports",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "ReportedClientProfileId",
                table: "JourneyCircleReports",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "ReportedParticipantType",
                table: "JourneyCircleReports",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReportedUserId",
                table: "JourneyCircleReports",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReporterParticipantType",
                table: "JourneyCircleReports",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReporterUserId",
                table: "JourneyCircleReports",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Resolution",
                table: "JourneyCircleReports",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedByUserId",
                table: "JourneyCircleReports",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedUtc",
                table: "JourneyCircleReports",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetEntityId",
                table: "JourneyCircleReports",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetKind",
                table: "JourneyCircleReports",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "BlockerClientProfileId",
                table: "JourneyCircleBlocks",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "BlockedClientProfileId",
                table: "JourneyCircleBlocks",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "BlockedParticipantType",
                table: "JourneyCircleBlocks",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BlockedUserId",
                table: "JourneyCircleBlocks",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BlockerParticipantType",
                table: "JourneyCircleBlocks",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BlockerUserId",
                table: "JourneyCircleBlocks",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JourneyCircleReports_ReportedUserId_ReportedParticipantType_Status",
                table: "JourneyCircleReports",
                columns: new[] { "ReportedUserId", "ReportedParticipantType", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_JourneyCircleBlocks_BlockerClientProfileId_BlockedClientProfileId",
                table: "JourneyCircleBlocks",
                columns: new[] { "BlockerClientProfileId", "BlockedClientProfileId" },
                unique: true,
                filter: "[BlockerClientProfileId] IS NOT NULL AND [BlockedClientProfileId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_JourneyCircleBlocks_BlockerUserId_BlockerParticipantType_BlockedUserId_BlockedParticipantType",
                table: "JourneyCircleBlocks",
                columns: new[] { "BlockerUserId", "BlockerParticipantType", "BlockedUserId", "BlockedParticipantType" },
                unique: true,
                filter: "[BlockerUserId] IS NOT NULL AND [BlockerParticipantType] IS NOT NULL AND [BlockedUserId] IS NOT NULL AND [BlockedParticipantType] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JourneyCircleReports_ReportedUserId_ReportedParticipantType_Status",
                table: "JourneyCircleReports");

            migrationBuilder.DropIndex(
                name: "IX_JourneyCircleBlocks_BlockerClientProfileId_BlockedClientProfileId",
                table: "JourneyCircleBlocks");

            migrationBuilder.DropIndex(
                name: "IX_JourneyCircleBlocks_BlockerUserId_BlockerParticipantType_BlockedUserId_BlockedParticipantType",
                table: "JourneyCircleBlocks");

            migrationBuilder.DropColumn(
                name: "ReportedParticipantType",
                table: "JourneyCircleReports");

            migrationBuilder.DropColumn(
                name: "ReportedUserId",
                table: "JourneyCircleReports");

            migrationBuilder.DropColumn(
                name: "ReporterParticipantType",
                table: "JourneyCircleReports");

            migrationBuilder.DropColumn(
                name: "ReporterUserId",
                table: "JourneyCircleReports");

            migrationBuilder.DropColumn(
                name: "Resolution",
                table: "JourneyCircleReports");

            migrationBuilder.DropColumn(
                name: "ResolvedByUserId",
                table: "JourneyCircleReports");

            migrationBuilder.DropColumn(
                name: "ResolvedUtc",
                table: "JourneyCircleReports");

            migrationBuilder.DropColumn(
                name: "TargetEntityId",
                table: "JourneyCircleReports");

            migrationBuilder.DropColumn(
                name: "TargetKind",
                table: "JourneyCircleReports");

            migrationBuilder.DropColumn(
                name: "BlockedParticipantType",
                table: "JourneyCircleBlocks");

            migrationBuilder.DropColumn(
                name: "BlockedUserId",
                table: "JourneyCircleBlocks");

            migrationBuilder.DropColumn(
                name: "BlockerParticipantType",
                table: "JourneyCircleBlocks");

            migrationBuilder.DropColumn(
                name: "BlockerUserId",
                table: "JourneyCircleBlocks");

            migrationBuilder.AlterColumn<Guid>(
                name: "ReporterClientProfileId",
                table: "JourneyCircleReports",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ReportedClientProfileId",
                table: "JourneyCircleReports",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "BlockerClientProfileId",
                table: "JourneyCircleBlocks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "BlockedClientProfileId",
                table: "JourneyCircleBlocks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JourneyCircleBlocks_BlockerClientProfileId_BlockedClientProfileId",
                table: "JourneyCircleBlocks",
                columns: new[] { "BlockerClientProfileId", "BlockedClientProfileId" },
                unique: true);
        }
    }
}
