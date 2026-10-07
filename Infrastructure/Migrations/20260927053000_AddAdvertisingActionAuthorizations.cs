using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260927053000_AddAdvertisingActionAuthorizations")]
public partial class AddAdvertisingActionAuthorizations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AdvertisingActionAuthorizations",
            columns: table => new
            {
                Id = table.Column<Guid>(nullable: false),
                OwnerKey = table.Column<string>(maxLength: 64, nullable: false),
                OwnerType = table.Column<string>(maxLength: 16, nullable: false),
                AgentTrackingProfileId = table.Column<Guid>(nullable: true),
                CommerceBusinessId = table.Column<Guid>(nullable: true),
                Provider = table.Column<string>(maxLength: 20, nullable: false),
                ProposalKind = table.Column<string>(maxLength: 40, nullable: false),
                ActionDigest = table.Column<string>(maxLength: 64, nullable: false),
                ExactPlanJson = table.Column<string>(nullable: false),
                SourceSnapshotJson = table.Column<string>(nullable: true),
                State = table.Column<string>(maxLength: 32, nullable: false),
                ProposedByUserId = table.Column<string>(maxLength: 450, nullable: false),
                ProposedUtc = table.Column<DateTime>(nullable: false),
                ApprovedByUserId = table.Column<string>(maxLength: 450, nullable: true),
                ApprovedUtc = table.Column<DateTime>(nullable: true),
                ApprovalExpiresUtc = table.Column<DateTime>(nullable: true),
                RejectedByUserId = table.Column<string>(maxLength: 450, nullable: true),
                RejectedUtc = table.Column<DateTime>(nullable: true),
                ExecutionClaimToken = table.Column<string>(maxLength: 64, nullable: true),
                ExecutionStartedUtc = table.Column<DateTime>(nullable: true),
                CompletedUtc = table.Column<DateTime>(nullable: true),
                ProviderReceiptJson = table.Column<string>(nullable: true),
                ErrorCode = table.Column<string>(maxLength: 120, nullable: true),
                ErrorMessage = table.Column<string>(maxLength: 4000, nullable: true),
                Revision = table.Column<string>(maxLength: 32, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AdvertisingActionAuthorizations", x => x.Id);
                table.ForeignKey(
                    name: "FK_AdvertisingActionAuthorizations_AgentTrackingProfiles_AgentTrackingProfileId",
                    column: x => x.AgentTrackingProfileId,
                    principalTable: "AgentTrackingProfiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_AdvertisingActionAuthorizations_CommerceBusinesses_CommerceBusinessId",
                    column: x => x.CommerceBusinessId,
                    principalTable: "CommerceBusinesses",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AdvertisingActionAuthorizations_AgentTrackingProfileId",
            table: "AdvertisingActionAuthorizations",
            column: "AgentTrackingProfileId");

        migrationBuilder.CreateIndex(
            name: "IX_AdvertisingActionAuthorizations_CommerceBusinessId",
            table: "AdvertisingActionAuthorizations",
            column: "CommerceBusinessId");

        migrationBuilder.CreateIndex(
            name: "IX_AdvertisingActionAuthorizations_ExecutionClaimToken",
            table: "AdvertisingActionAuthorizations",
            column: "ExecutionClaimToken");

        migrationBuilder.CreateIndex(
            name: "IX_AdvertisingActionAuthorizations_OwnerKey_Provider_ActionDigest",
            table: "AdvertisingActionAuthorizations",
            columns: new[] { "OwnerKey", "Provider", "ActionDigest" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AdvertisingActionAuthorizations_OwnerKey_State_ProposedUtc",
            table: "AdvertisingActionAuthorizations",
            columns: new[] { "OwnerKey", "State", "ProposedUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "AdvertisingActionAuthorizations");
}
