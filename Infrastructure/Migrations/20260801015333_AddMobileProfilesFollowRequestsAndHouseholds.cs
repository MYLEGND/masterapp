using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMobileProfilesFollowRequestsAndHouseholds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FinanceToolStates_ClientProfileId_ToolId",
                table: "FinanceToolStates");

            migrationBuilder.DropIndex(
                name: "IX_ClientFinancialPlans_ClientId",
                table: "ClientFinancialPlans");

            migrationBuilder.AddColumn<DateTime>(
                name: "RespondedUtc",
                table: "SocialFollows",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "SocialFollows",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "HouseholdAccountId",
                table: "FinanceToolStates",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HouseholdAccountId",
                table: "ClientFinancialPlans",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HouseholdAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriptionOwnerClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActivatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SuspendedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StatusReasonCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HouseholdAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HouseholdAccounts_ClientProfiles_SubscriptionOwnerClientProfileId",
                        column: x => x.SubscriptionOwnerClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MobileProfileSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Username = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    NormalizedUsername = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Bio = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Website = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    Location = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    PublicEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    IsEmailVisible = table.Column<bool>(type: "bit", nullable: false),
                    IsPrivate = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MobileProfileSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HouseholdMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ExternalIdentityObjectId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActivatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SuspendedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RemovedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StatusReasonCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HouseholdMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HouseholdMemberships_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HouseholdMemberships_HouseholdAccounts_HouseholdAccountId",
                        column: x => x.HouseholdAccountId,
                        principalTable: "HouseholdAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HouseholdMemberInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IntendedNormalizedEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    InvitedFirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    InvitedLastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcceptedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeclinedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeclineReasonCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HouseholdMemberInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HouseholdMemberInvitations_HouseholdAccounts_HouseholdAccountId",
                        column: x => x.HouseholdAccountId,
                        principalTable: "HouseholdAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HouseholdMemberInvitations_HouseholdMemberships_HouseholdMembershipId",
                        column: x => x.HouseholdMembershipId,
                        principalTable: "HouseholdMemberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SocialFollows_FollowedUserId_FollowedParticipantType_Status",
                table: "SocialFollows",
                columns: new[] { "FollowedUserId", "FollowedParticipantType", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FinanceToolStates_HouseholdAccountId_ToolId",
                table: "FinanceToolStates",
                columns: new[] { "HouseholdAccountId", "ToolId" },
                unique: true,
                filter: "[HouseholdAccountId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClientFinancialPlans_ClientId",
                table: "ClientFinancialPlans",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientFinancialPlans_HouseholdAccountId",
                table: "ClientFinancialPlans",
                column: "HouseholdAccountId",
                unique: true,
                filter: "[HouseholdAccountId] IS NOT NULL AND [IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdAccounts_SubscriptionOwnerClientProfileId",
                table: "HouseholdAccounts",
                column: "SubscriptionOwnerClientProfileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdMemberInvitations_HouseholdAccountId_IntendedNormalizedEmail_Status",
                table: "HouseholdMemberInvitations",
                columns: new[] { "HouseholdAccountId", "IntendedNormalizedEmail", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdMemberInvitations_HouseholdMembershipId",
                table: "HouseholdMemberInvitations",
                column: "HouseholdMembershipId");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdMemberInvitations_TokenHash",
                table: "HouseholdMemberInvitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdMemberships_ClientProfileId",
                table: "HouseholdMemberships",
                column: "ClientProfileId",
                unique: true,
                filter: "[ClientProfileId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdMemberships_ExternalIdentityObjectId",
                table: "HouseholdMemberships",
                column: "ExternalIdentityObjectId",
                unique: true,
                filter: "[ExternalIdentityObjectId] IS NOT NULL AND [Status] <> 'Removed'");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdMemberships_HouseholdAccountId_NormalizedEmail",
                table: "HouseholdMemberships",
                columns: new[] { "HouseholdAccountId", "NormalizedEmail" },
                unique: true,
                filter: "[Status] <> 'Removed'");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdMemberships_HouseholdAccountId_Role",
                table: "HouseholdMemberships",
                columns: new[] { "HouseholdAccountId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MobileProfileSettings_NormalizedUsername",
                table: "MobileProfileSettings",
                column: "NormalizedUsername",
                unique: true,
                filter: "[NormalizedUsername] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MobileProfileSettings_ProfileId_ParticipantType",
                table: "MobileProfileSettings",
                columns: new[] { "ProfileId", "ParticipantType" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientFinancialPlans_HouseholdAccounts_HouseholdAccountId",
                table: "ClientFinancialPlans",
                column: "HouseholdAccountId",
                principalTable: "HouseholdAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FinanceToolStates_HouseholdAccounts_HouseholdAccountId",
                table: "FinanceToolStates",
                column: "HouseholdAccountId",
                principalTable: "HouseholdAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientFinancialPlans_HouseholdAccounts_HouseholdAccountId",
                table: "ClientFinancialPlans");

            migrationBuilder.DropForeignKey(
                name: "FK_FinanceToolStates_HouseholdAccounts_HouseholdAccountId",
                table: "FinanceToolStates");

            migrationBuilder.DropTable(
                name: "HouseholdMemberInvitations");

            migrationBuilder.DropTable(
                name: "MobileProfileSettings");

            migrationBuilder.DropTable(
                name: "HouseholdMemberships");

            migrationBuilder.DropTable(
                name: "HouseholdAccounts");

            migrationBuilder.DropIndex(
                name: "IX_SocialFollows_FollowedUserId_FollowedParticipantType_Status",
                table: "SocialFollows");

            migrationBuilder.DropIndex(
                name: "IX_FinanceToolStates_HouseholdAccountId_ToolId",
                table: "FinanceToolStates");

            migrationBuilder.DropIndex(
                name: "IX_ClientFinancialPlans_ClientId",
                table: "ClientFinancialPlans");

            migrationBuilder.DropIndex(
                name: "IX_ClientFinancialPlans_HouseholdAccountId",
                table: "ClientFinancialPlans");

            migrationBuilder.DropColumn(
                name: "RespondedUtc",
                table: "SocialFollows");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "SocialFollows");

            migrationBuilder.DropColumn(
                name: "HouseholdAccountId",
                table: "FinanceToolStates");

            migrationBuilder.DropColumn(
                name: "HouseholdAccountId",
                table: "ClientFinancialPlans");

            migrationBuilder.CreateIndex(
                name: "IX_FinanceToolStates_ClientProfileId_ToolId",
                table: "FinanceToolStates",
                columns: new[] { "ClientProfileId", "ToolId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientFinancialPlans_ClientId",
                table: "ClientFinancialPlans",
                column: "ClientId",
                unique: true,
                filter: "[IsDeleted] = 0");
        }
    }
}
