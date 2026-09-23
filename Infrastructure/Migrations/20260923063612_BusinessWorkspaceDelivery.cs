using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BusinessWorkspaceDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CommerceBusinessId",
                table: "WorkstationLeadProfiles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CommerceBusinessId",
                table: "WebsiteLeadIntakeLinks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LegacyAdsImportedUtc",
                table: "MarketingConnections",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LegacyProfileImportedUtc",
                table: "MarketingConnections",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NotificationAttempts",
                table: "CommerceWebsiteInquiry",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NotificationNextAttemptUtc",
                table: "CommerceWebsiteInquiry",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "NotificationRevision",
                table: "CommerceWebsiteInquiry",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTime>(
                name: "NotificationSentUtc",
                table: "CommerceWebsiteInquiry",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotificationStatus",
                table: "CommerceWebsiteInquiry",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "WebsiteLeadId",
                table: "CommerceWebsiteInquiry",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkspacePreferencesJson",
                table: "CommerceBusinessStorefrontSettings",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_WorkstationLeadProfiles_CommerceBusinessId",
                table: "WorkstationLeadProfiles",
                column: "CommerceBusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeadIntakeLinks_CommerceBusinessId",
                table: "WebsiteLeadIntakeLinks",
                column: "CommerceBusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_CommerceWebsiteInquiry_NotificationStatus_NotificationNextAttemptUtc",
                table: "CommerceWebsiteInquiry",
                columns: new[] { "NotificationStatus", "NotificationNextAttemptUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_WebsiteLeadIntakeLinks_CommerceBusinesses_CommerceBusinessId",
                table: "WebsiteLeadIntakeLinks",
                column: "CommerceBusinessId",
                principalTable: "CommerceBusinesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkstationLeadProfiles_CommerceBusinesses_CommerceBusinessId",
                table: "WorkstationLeadProfiles",
                column: "CommerceBusinessId",
                principalTable: "CommerceBusinesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WebsiteLeadIntakeLinks_CommerceBusinesses_CommerceBusinessId",
                table: "WebsiteLeadIntakeLinks");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkstationLeadProfiles_CommerceBusinesses_CommerceBusinessId",
                table: "WorkstationLeadProfiles");

            migrationBuilder.DropIndex(
                name: "IX_WorkstationLeadProfiles_CommerceBusinessId",
                table: "WorkstationLeadProfiles");

            migrationBuilder.DropIndex(
                name: "IX_WebsiteLeadIntakeLinks_CommerceBusinessId",
                table: "WebsiteLeadIntakeLinks");

            migrationBuilder.DropIndex(
                name: "IX_CommerceWebsiteInquiry_NotificationStatus_NotificationNextAttemptUtc",
                table: "CommerceWebsiteInquiry");

            migrationBuilder.DropColumn(
                name: "CommerceBusinessId",
                table: "WorkstationLeadProfiles");

            migrationBuilder.DropColumn(
                name: "CommerceBusinessId",
                table: "WebsiteLeadIntakeLinks");

            migrationBuilder.DropColumn(
                name: "LegacyAdsImportedUtc",
                table: "MarketingConnections");

            migrationBuilder.DropColumn(
                name: "LegacyProfileImportedUtc",
                table: "MarketingConnections");

            migrationBuilder.DropColumn(
                name: "NotificationAttempts",
                table: "CommerceWebsiteInquiry");

            migrationBuilder.DropColumn(
                name: "NotificationNextAttemptUtc",
                table: "CommerceWebsiteInquiry");

            migrationBuilder.DropColumn(
                name: "NotificationRevision",
                table: "CommerceWebsiteInquiry");

            migrationBuilder.DropColumn(
                name: "NotificationSentUtc",
                table: "CommerceWebsiteInquiry");

            migrationBuilder.DropColumn(
                name: "NotificationStatus",
                table: "CommerceWebsiteInquiry");

            migrationBuilder.DropColumn(
                name: "WebsiteLeadId",
                table: "CommerceWebsiteInquiry");

            migrationBuilder.DropColumn(
                name: "WorkspacePreferencesJson",
                table: "CommerceBusinessStorefrontSettings");
        }
    }
}
