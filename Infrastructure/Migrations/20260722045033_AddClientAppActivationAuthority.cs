using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientAppActivationAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BillingTimeZoneId",
                table: "ClientSubscriptions",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "FirstChargeUtc",
                table: "ClientSubscriptions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FirstRecurringRenewalUtc",
                table: "ClientSubscriptions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalIdentityObjectId",
                table: "ClientProfiles",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClientIdentityContinuations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriptionActivationInvitationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClientSubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Purpose = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IntendedNormalizedEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ReturnUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConsumedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientIdentityContinuations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientIdentityContinuations_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientIdentityContinuations_ClientSubscriptions_ClientSubscriptionId",
                        column: x => x.ClientSubscriptionId,
                        principalTable: "ClientSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClientIdentityContinuations_SubscriptionActivationInvitations_SubscriptionActivationInvitationId",
                        column: x => x.SubscriptionActivationInvitationId,
                        principalTable: "SubscriptionActivationInvitations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientProfiles_ExternalIdentityObjectId",
                table: "ClientProfiles",
                column: "ExternalIdentityObjectId",
                unique: true,
                filter: "[ExternalIdentityObjectId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClientIdentityContinuations_ClientProfileId_ConsumedUtc",
                table: "ClientIdentityContinuations",
                columns: new[] { "ClientProfileId", "ConsumedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientIdentityContinuations_ClientProfileId_ExpiresUtc",
                table: "ClientIdentityContinuations",
                columns: new[] { "ClientProfileId", "ExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientIdentityContinuations_ClientSubscriptionId",
                table: "ClientIdentityContinuations",
                column: "ClientSubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientIdentityContinuations_SubscriptionActivationInvitationId",
                table: "ClientIdentityContinuations",
                column: "SubscriptionActivationInvitationId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientIdentityContinuations_TokenHash",
                table: "ClientIdentityContinuations",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientIdentityContinuations");

            migrationBuilder.DropIndex(
                name: "IX_ClientProfiles_ExternalIdentityObjectId",
                table: "ClientProfiles");

            migrationBuilder.DropColumn(
                name: "BillingTimeZoneId",
                table: "ClientSubscriptions");

            migrationBuilder.DropColumn(
                name: "FirstChargeUtc",
                table: "ClientSubscriptions");

            migrationBuilder.DropColumn(
                name: "FirstRecurringRenewalUtc",
                table: "ClientSubscriptions");

            migrationBuilder.DropColumn(
                name: "ExternalIdentityObjectId",
                table: "ClientProfiles");
        }
    }
}
