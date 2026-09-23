using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CentralMarketingOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CommerceBusinessId",
                table: "WebsiteLeads",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebsiteBindingId",
                table: "WebsiteLeads",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WebsiteContentVersionId",
                table: "WebsiteLeads",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CommerceBusinessId",
                table: "MetaSignalEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebsiteBindingId",
                table: "MetaSignalEvents",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WebsiteContentVersionId",
                table: "MetaSignalEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BookingCalendarEmail",
                table: "CommerceBusinessStorefrontSettings",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BookingEmbedUrl",
                table: "CommerceBusinessStorefrontSettings",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BookingEnabled",
                table: "CommerceBusinessStorefrontSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "BookingFallbackUrl",
                table: "CommerceBusinessStorefrontSettings",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BookingMailboxId",
                table: "CommerceBusinessStorefrontSettings",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GlobalStoreCheckoutUrl",
                table: "CommerceBusinessStorefrontSettings",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LegacyProfileImportedUtc",
                table: "CommerceBusinessStorefrontSettings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Revision",
                table: "CommerceBusinessStorefrontSettings",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "ShortBio",
                table: "CommerceBusinessStorefrontSettings",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CommerceBusinessId",
                table: "AnalyticsEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebsiteBindingId",
                table: "AnalyticsEvents",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WebsiteContentVersionId",
                table: "AnalyticsEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MarketingConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OwnerType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AgentTrackingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CommerceBusinessId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PixelId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    TestEventCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AdsAccessTokenCiphertext = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CapiAccessTokenCiphertext = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AccessTokenExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AdAccountId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AdAccountName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    MetaBusinessManagerId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    MetaBusinessManagerName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    MetaUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    MetaUserName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ConnectedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DisconnectedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Revision = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketingConnections", x => x.Id);
                    table.CheckConstraint("CK_MarketingConnection_Owner", "([OwnerType] = 'founder' AND [AgentTrackingProfileId] IS NULL AND [CommerceBusinessId] IS NULL) OR ([OwnerType] = 'agent' AND [AgentTrackingProfileId] IS NOT NULL AND [CommerceBusinessId] IS NULL) OR ([OwnerType] = 'business' AND [CommerceBusinessId] IS NOT NULL AND [AgentTrackingProfileId] IS NULL)");
                    table.ForeignKey(
                        name: "FK_MarketingConnections_AgentTrackingProfiles_AgentTrackingProfileId",
                        column: x => x.AgentTrackingProfileId,
                        principalTable: "AgentTrackingProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MarketingConnections_CommerceBusinesses_CommerceBusinessId",
                        column: x => x.CommerceBusinessId,
                        principalTable: "CommerceBusinesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_CommerceBusinessId_CreatedUtc",
                table: "WebsiteLeads",
                columns: new[] { "CommerceBusinessId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MetaSignalEvents_CommerceBusinessId_CreatedUtc",
                table: "MetaSignalEvents",
                columns: new[] { "CommerceBusinessId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_CommerceBusinessId_EventUtc",
                table: "AnalyticsEvents",
                columns: new[] { "CommerceBusinessId", "EventUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketingConnections_AgentTrackingProfileId_Provider",
                table: "MarketingConnections",
                columns: new[] { "AgentTrackingProfileId", "Provider" },
                unique: true,
                filter: "[AgentTrackingProfileId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MarketingConnections_CommerceBusinessId_Provider",
                table: "MarketingConnections",
                columns: new[] { "CommerceBusinessId", "Provider" },
                unique: true,
                filter: "[CommerceBusinessId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MarketingConnections_OwnerKey_Provider",
                table: "MarketingConnections",
                columns: new[] { "OwnerKey", "Provider" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AnalyticsEvents_CommerceBusinesses_CommerceBusinessId",
                table: "AnalyticsEvents",
                column: "CommerceBusinessId",
                principalTable: "CommerceBusinesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MetaSignalEvents_CommerceBusinesses_CommerceBusinessId",
                table: "MetaSignalEvents",
                column: "CommerceBusinessId",
                principalTable: "CommerceBusinesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WebsiteLeads_CommerceBusinesses_CommerceBusinessId",
                table: "WebsiteLeads",
                column: "CommerceBusinessId",
                principalTable: "CommerceBusinesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AnalyticsEvents_CommerceBusinesses_CommerceBusinessId",
                table: "AnalyticsEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_MetaSignalEvents_CommerceBusinesses_CommerceBusinessId",
                table: "MetaSignalEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_WebsiteLeads_CommerceBusinesses_CommerceBusinessId",
                table: "WebsiteLeads");

            migrationBuilder.DropTable(
                name: "MarketingConnections");

            migrationBuilder.DropIndex(
                name: "IX_WebsiteLeads_CommerceBusinessId_CreatedUtc",
                table: "WebsiteLeads");

            migrationBuilder.DropIndex(
                name: "IX_MetaSignalEvents_CommerceBusinessId_CreatedUtc",
                table: "MetaSignalEvents");

            migrationBuilder.DropIndex(
                name: "IX_AnalyticsEvents_CommerceBusinessId_EventUtc",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "CommerceBusinessId",
                table: "WebsiteLeads");

            migrationBuilder.DropColumn(
                name: "WebsiteBindingId",
                table: "WebsiteLeads");

            migrationBuilder.DropColumn(
                name: "WebsiteContentVersionId",
                table: "WebsiteLeads");

            migrationBuilder.DropColumn(
                name: "CommerceBusinessId",
                table: "MetaSignalEvents");

            migrationBuilder.DropColumn(
                name: "WebsiteBindingId",
                table: "MetaSignalEvents");

            migrationBuilder.DropColumn(
                name: "WebsiteContentVersionId",
                table: "MetaSignalEvents");

            migrationBuilder.DropColumn(
                name: "BookingCalendarEmail",
                table: "CommerceBusinessStorefrontSettings");

            migrationBuilder.DropColumn(
                name: "BookingEmbedUrl",
                table: "CommerceBusinessStorefrontSettings");

            migrationBuilder.DropColumn(
                name: "BookingEnabled",
                table: "CommerceBusinessStorefrontSettings");

            migrationBuilder.DropColumn(
                name: "BookingFallbackUrl",
                table: "CommerceBusinessStorefrontSettings");

            migrationBuilder.DropColumn(
                name: "BookingMailboxId",
                table: "CommerceBusinessStorefrontSettings");

            migrationBuilder.DropColumn(
                name: "GlobalStoreCheckoutUrl",
                table: "CommerceBusinessStorefrontSettings");

            migrationBuilder.DropColumn(
                name: "LegacyProfileImportedUtc",
                table: "CommerceBusinessStorefrontSettings");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "CommerceBusinessStorefrontSettings");

            migrationBuilder.DropColumn(
                name: "ShortBio",
                table: "CommerceBusinessStorefrontSettings");

            migrationBuilder.DropColumn(
                name: "CommerceBusinessId",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "WebsiteBindingId",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "WebsiteContentVersionId",
                table: "AnalyticsEvents");
        }
    }
}
