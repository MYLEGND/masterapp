using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerUserIdToBookkeeping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RecurringExpenses_AgentUserId_IsActive",
                table: "RecurringExpenses");

            migrationBuilder.DropIndex(
                name: "IX_BookkeepingEntries_AgentUserId_EntryDate",
                table: "BookkeepingEntries");

            if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.AddColumn<string>(
                    name: "OwnerUserId",
                    table: "RecurringExpenses",
                    type: "TEXT",
                    nullable: false,
                    defaultValue: "");

                migrationBuilder.AddColumn<string>(
                    name: "OwnerUserId",
                    table: "BookkeepingEntries",
                    type: "TEXT",
                    nullable: false,
                    defaultValue: "");

                migrationBuilder.CreateIndex(
                    name: "IX_RecurringExpenses_OwnerUserId_IsActive",
                    table: "RecurringExpenses",
                    columns: new[] { "OwnerUserId", "IsActive" });

                migrationBuilder.CreateIndex(
                    name: "IX_BookkeepingEntries_OwnerUserId_EntryDate",
                    table: "BookkeepingEntries",
                    columns: new[] { "OwnerUserId", "EntryDate" });

                return;
            }

            migrationBuilder.AlterColumn<string>(
                name: "AgentUserId",
                table: "RecurringExpenses",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450);

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "RecurringExpenses",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "AgentUserId",
                table: "BookkeepingEntries",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450);

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "BookkeepingEntries",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenses_OwnerUserId_IsActive",
                table: "RecurringExpenses",
                columns: new[] { "OwnerUserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_BookkeepingEntries_OwnerUserId_EntryDate",
                table: "BookkeepingEntries",
                columns: new[] { "OwnerUserId", "EntryDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RecurringExpenses_OwnerUserId_IsActive",
                table: "RecurringExpenses");

            migrationBuilder.DropIndex(
                name: "IX_BookkeepingEntries_OwnerUserId_EntryDate",
                table: "BookkeepingEntries");

            if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.DropColumn(
                    name: "OwnerUserId",
                    table: "RecurringExpenses");

                migrationBuilder.DropColumn(
                    name: "OwnerUserId",
                    table: "BookkeepingEntries");

                migrationBuilder.CreateIndex(
                    name: "IX_RecurringExpenses_AgentUserId_IsActive",
                    table: "RecurringExpenses",
                    columns: new[] { "AgentUserId", "IsActive" });

                migrationBuilder.CreateIndex(
                    name: "IX_BookkeepingEntries_AgentUserId_EntryDate",
                    table: "BookkeepingEntries",
                    columns: new[] { "AgentUserId", "EntryDate" });

                return;
            }

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "RecurringExpenses");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "BookkeepingEntries");

            migrationBuilder.AlterColumn<string>(
                name: "AgentUserId",
                table: "RecurringExpenses",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AgentUserId",
                table: "BookkeepingEntries",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenses_AgentUserId_IsActive",
                table: "RecurringExpenses",
                columns: new[] { "AgentUserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_BookkeepingEntries_AgentUserId_EntryDate",
                table: "BookkeepingEntries",
                columns: new[] { "AgentUserId", "EntryDate" });
        }
    }
}
