using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260927002000_AddMarketingDestinationDeliveries")]
public partial class AddMarketingDestinationDeliveries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "MarketingDestinationDeliveries",
            columns: table => new
            {
                Id = table.Column<Guid>(nullable: false),
                OwnerKey = table.Column<string>(maxLength: 64, nullable: false),
                OwnerType = table.Column<string>(maxLength: 16, nullable: false),
                AgentTrackingProfileId = table.Column<Guid>(nullable: true),
                CommerceBusinessId = table.Column<Guid>(nullable: true),
                Provider = table.Column<string>(maxLength: 20, nullable: false),
                Channel = table.Column<string>(maxLength: 20, nullable: false),
                CanonicalSource = table.Column<string>(maxLength: 80, nullable: false),
                CanonicalEventId = table.Column<string>(maxLength: 160, nullable: false),
                CanonicalEventName = table.Column<string>(maxLength: 120, nullable: false),
                ProviderEventName = table.Column<string>(maxLength: 120, nullable: false),
                PixelId = table.Column<string>(maxLength: 200, nullable: false),
                Status = table.Column<string>(maxLength: 40, nullable: false),
                AttemptCount = table.Column<int>(nullable: false),
                NextAttemptUtc = table.Column<DateTime>(nullable: true),
                LastAttemptUtc = table.Column<DateTime>(nullable: true),
                SentUtc = table.Column<DateTime>(nullable: true),
                LastHttpStatusCode = table.Column<int>(nullable: true),
                ProviderReceiptJson = table.Column<string>(nullable: true),
                ErrorCode = table.Column<string>(maxLength: 120, nullable: true),
                ErrorMessage = table.Column<string>(maxLength: 4000, nullable: true),
                ClaimToken = table.Column<string>(maxLength: 64, nullable: true),
                ClaimExpiresUtc = table.Column<DateTime>(nullable: true),
                CreatedUtc = table.Column<DateTime>(nullable: false),
                UpdatedUtc = table.Column<DateTime>(nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MarketingDestinationDeliveries", x => x.Id);
                table.ForeignKey(
                    name: "FK_MarketingDestinationDeliveries_AgentTrackingProfiles_AgentTrackingProfileId",
                    column: x => x.AgentTrackingProfileId,
                    principalTable: "AgentTrackingProfiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_MarketingDestinationDeliveries_CommerceBusinesses_CommerceBusinessId",
                    column: x => x.CommerceBusinessId,
                    principalTable: "CommerceBusinesses",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_MarketingDestinationDeliveries_AgentTrackingProfileId",
            table: "MarketingDestinationDeliveries",
            column: "AgentTrackingProfileId");
        migrationBuilder.CreateIndex(
            name: "IX_MarketingDestinationDeliveries_CommerceBusinessId",
            table: "MarketingDestinationDeliveries",
            column: "CommerceBusinessId");
        migrationBuilder.CreateIndex(
            name: "IX_MarketingDestinationDeliveries_ClaimExpiresUtc",
            table: "MarketingDestinationDeliveries",
            column: "ClaimExpiresUtc");
        migrationBuilder.CreateIndex(
            name: "IX_MarketingDestinationDeliveries_SentUtc",
            table: "MarketingDestinationDeliveries",
            column: "SentUtc");
        migrationBuilder.CreateIndex(
            name: "IX_MarketingDestinationDeliveries_Provider_Status_NextAttemptUtc",
            table: "MarketingDestinationDeliveries",
            columns: new[] { "Provider", "Status", "NextAttemptUtc" });
        migrationBuilder.CreateIndex(
            name: "UX_MarketingDestinationDeliveries_Owner_Provider_Event",
            table: "MarketingDestinationDeliveries",
            columns: new[] { "OwnerKey", "Provider", "Channel", "CanonicalEventId", "ProviderEventName" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "MarketingDestinationDeliveries");
}
