using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantCostReceiptSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ReceptionistUsageReceipt_Target",
                table: "ReceptionistUsageReceipts");

            migrationBuilder.AddColumn<Guid>(
                name: "AssistantReservationId",
                table: "ReceptionistUsageReceipts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssistantBindingJson",
                table: "ReceptionistCostReservations",
                type: "nvarchar(max)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistUsageReceipts_AssistantReservationId",
                table: "ReceptionistUsageReceipts",
                column: "AssistantReservationId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReceptionistUsageReceipt_Target",
                table: "ReceptionistUsageReceipts",
                sql: "([CallId] IS NOT NULL AND [AccountChargeId] IS NULL AND [AssistantReservationId] IS NULL) OR ([CallId] IS NULL AND [AccountChargeId] IS NOT NULL AND [AssistantReservationId] IS NULL) OR ([CallId] IS NULL AND [AccountChargeId] IS NULL AND [AssistantReservationId] IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_ReceptionistUsageReceipts_ReceptionistCostReservations_AssistantReservationId",
                table: "ReceptionistUsageReceipts",
                column: "AssistantReservationId",
                principalTable: "ReceptionistCostReservations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [ReceptionistUsageReceipts] WHERE [AssistantReservationId] IS NOT NULL) OR EXISTS (SELECT 1 FROM [ReceptionistCostReservations] WHERE [AssistantBindingJson] IS NOT NULL) THROW 51000, 'Assistant settlement evidence exists; retain schema and use reviewed forward recovery.', 1;");

            migrationBuilder.DropForeignKey(
                name: "FK_ReceptionistUsageReceipts_ReceptionistCostReservations_AssistantReservationId",
                table: "ReceptionistUsageReceipts");

            migrationBuilder.DropIndex(
                name: "IX_ReceptionistUsageReceipts_AssistantReservationId",
                table: "ReceptionistUsageReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReceptionistUsageReceipt_Target",
                table: "ReceptionistUsageReceipts");

            migrationBuilder.DropColumn(
                name: "AssistantReservationId",
                table: "ReceptionistUsageReceipts");

            migrationBuilder.DropColumn(
                name: "AssistantBindingJson",
                table: "ReceptionistCostReservations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReceptionistUsageReceipt_Target",
                table: "ReceptionistUsageReceipts",
                sql: "([CallId] IS NOT NULL AND [AccountChargeId] IS NULL) OR ([CallId] IS NULL AND [AccountChargeId] IS NOT NULL)");
        }
    }
}
