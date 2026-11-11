using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessAssistantScopeAndFunding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReceptionistCostReservations_CallId_ComponentKey",
                table: "ReceptionistCostReservations");

            migrationBuilder.AlterColumn<Guid>(
                name: "CallId",
                table: "ReceptionistCostReservations",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "AssistantOperationId",
                table: "ReceptionistCostReservations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FeatureKey",
                table: "ReceptionistCostReservations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OperationFingerprint",
                table: "ReceptionistCostReservations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssistantConfigurationRevision",
                table: "MessageConversations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssistantConnectionId",
                table: "MessageConversations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssistantProvider",
                table: "MessageConversations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssistantWorkspaceKey",
                table: "MessageConversations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssistantConfigurationJson",
                table: "MarketingConnections",
                type: "nvarchar(max)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssistantControlRevision",
                table: "MarketingConnections",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "AssistantCredentialCiphertext",
                table: "MarketingConnections",
                type: "nvarchar(max)",
                maxLength: 24000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCostReservations_CallId_ComponentKey",
                table: "ReceptionistCostReservations",
                columns: new[] { "CallId", "ComponentKey" },
                unique: true,
                filter: "[CallId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCostReservations_OwnerBudgetAccountId_AssistantOperationId_ComponentKey",
                table: "ReceptionistCostReservations",
                columns: new[] { "OwnerBudgetAccountId", "AssistantOperationId", "ComponentKey" },
                unique: true,
                filter: "[AssistantOperationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCostReservations_OwnerBudgetAccountId_OwnerBudgetPeriodId",
                table: "ReceptionistCostReservations",
                columns: new[] { "OwnerBudgetAccountId", "OwnerBudgetPeriodId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReceptionistCostReservation_Operation",
                table: "ReceptionistCostReservations",
                sql: "([CallId] IS NOT NULL AND [AssistantOperationId] IS NULL AND [FeatureKey] IS NULL AND [OperationFingerprint] IS NULL) OR ([CallId] IS NULL AND [AssistantOperationId] IS NOT NULL AND [FeatureKey] IS NOT NULL AND [FeatureKey] = 'business-assistant' AND [OperationFingerprint] IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_ReceptionistCostReservations_ReceptionistBudgetPeriods_OwnerBudgetAccountId_OwnerBudgetPeriodId",
                table: "ReceptionistCostReservations",
                columns: new[] { "OwnerBudgetAccountId", "OwnerBudgetPeriodId" },
                principalTable: "ReceptionistBudgetPeriods",
                principalColumns: new[] { "BudgetAccountId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Once scoped history, credentials or holds exist, destructive schema
            // rollback would lose business evidence. Retain this additive schema
            // and roll the application forward instead.
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [ReceptionistCostReservations] WHERE [AssistantOperationId] IS NOT NULL) OR EXISTS (SELECT 1 FROM [MessageConversations] WHERE [AssistantWorkspaceKey] IS NOT NULL) OR EXISTS (SELECT 1 FROM [MarketingConnections] WHERE [AssistantConfigurationJson] IS NOT NULL OR [AssistantCredentialCiphertext] IS NOT NULL) THROW 51000, 'Assistant data exists; destructive schema rollback is prohibited. Use reviewed forward recovery.', 1;");

            migrationBuilder.DropForeignKey(
                name: "FK_ReceptionistCostReservations_ReceptionistBudgetPeriods_OwnerBudgetAccountId_OwnerBudgetPeriodId",
                table: "ReceptionistCostReservations");

            migrationBuilder.DropIndex(
                name: "IX_ReceptionistCostReservations_CallId_ComponentKey",
                table: "ReceptionistCostReservations");

            migrationBuilder.DropIndex(
                name: "IX_ReceptionistCostReservations_OwnerBudgetAccountId_AssistantOperationId_ComponentKey",
                table: "ReceptionistCostReservations");

            migrationBuilder.DropIndex(
                name: "IX_ReceptionistCostReservations_OwnerBudgetAccountId_OwnerBudgetPeriodId",
                table: "ReceptionistCostReservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReceptionistCostReservation_Operation",
                table: "ReceptionistCostReservations");

            migrationBuilder.DropColumn(
                name: "AssistantOperationId",
                table: "ReceptionistCostReservations");

            migrationBuilder.DropColumn(
                name: "FeatureKey",
                table: "ReceptionistCostReservations");

            migrationBuilder.DropColumn(
                name: "OperationFingerprint",
                table: "ReceptionistCostReservations");

            migrationBuilder.DropColumn(
                name: "AssistantConfigurationRevision",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "AssistantConnectionId",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "AssistantProvider",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "AssistantWorkspaceKey",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "AssistantConfigurationJson",
                table: "MarketingConnections");

            migrationBuilder.DropColumn(
                name: "AssistantControlRevision",
                table: "MarketingConnections");

            migrationBuilder.DropColumn(
                name: "AssistantCredentialCiphertext",
                table: "MarketingConnections");

            migrationBuilder.AlterColumn<Guid>(
                name: "CallId",
                table: "ReceptionistCostReservations",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCostReservations_CallId_ComponentKey",
                table: "ReceptionistCostReservations",
                columns: new[] { "CallId", "ComponentKey" },
                unique: true);
        }
    }
}
