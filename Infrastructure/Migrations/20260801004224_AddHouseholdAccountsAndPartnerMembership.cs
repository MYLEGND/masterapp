using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHouseholdAccountsAndPartnerMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FinanceToolStates_ClientProfileId_ToolId",
                table: "FinanceToolStates");

            migrationBuilder.DropIndex(
                name: "IX_ClientFinancialPlans_ClientId_IsDeleted",
                table: "ClientFinancialPlans");

            migrationBuilder.AddColumn<Guid>(
                name: "HouseholdAccountId",
                table: "FinanceToolStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HouseholdAccountId",
                table: "ClientFinancialPlans",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HouseholdAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubscriptionOwnerClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActivatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SuspendedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StatusReasonCode = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false, defaultValueSql: "X''")
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
                name: "HouseholdMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HouseholdAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    ExternalIdentityObjectId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActivatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SuspendedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RemovedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StatusReasonCode = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    UpdatedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false, defaultValueSql: "X''")
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HouseholdAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HouseholdMembershipId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IntendedNormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    InvitedFirstName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    InvitedLastName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AcceptedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeclinedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeclineReasonCode = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false, defaultValueSql: "X''")
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
                name: "IX_FinanceToolStates_HouseholdAccountId_ToolId",
                table: "FinanceToolStates",
                columns: new[] { "HouseholdAccountId", "ToolId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientFinancialPlans_ClientId",
                table: "ClientFinancialPlans",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientFinancialPlans_HouseholdAccountId_IsDeleted",
                table: "ClientFinancialPlans",
                columns: new[] { "HouseholdAccountId", "IsDeleted" },
                unique: true);

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
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdMemberships_ExternalIdentityObjectId_Status",
                table: "HouseholdMemberships",
                columns: new[] { "ExternalIdentityObjectId", "Status" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdMemberships_HouseholdAccountId_NormalizedEmail_Status",
                table: "HouseholdMemberships",
                columns: new[] { "HouseholdAccountId", "NormalizedEmail", "Status" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdMemberships_HouseholdAccountId_Role",
                table: "HouseholdMemberships",
                columns: new[] { "HouseholdAccountId", "Role" },
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
                name: "HouseholdMemberships");

            migrationBuilder.DropTable(
                name: "HouseholdAccounts");

            migrationBuilder.DropIndex(
                name: "IX_FinanceToolStates_HouseholdAccountId_ToolId",
                table: "FinanceToolStates");

            migrationBuilder.DropIndex(
                name: "IX_ClientFinancialPlans_ClientId",
                table: "ClientFinancialPlans");

            migrationBuilder.DropIndex(
                name: "IX_ClientFinancialPlans_HouseholdAccountId_IsDeleted",
                table: "ClientFinancialPlans");

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
                name: "IX_ClientFinancialPlans_ClientId_IsDeleted",
                table: "ClientFinancialPlans",
                columns: new[] { "ClientId", "IsDeleted" },
                unique: true);
        }
    }
}
