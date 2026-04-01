using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScopeAlignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RecurringExpenses_OwnerUserId_IsActive",
                table: "RecurringExpenses");

            migrationBuilder.DropIndex(
                name: "IX_BookkeepingEntries_OwnerUserId_EntryDate",
                table: "BookkeepingEntries");

            migrationBuilder.AddColumn<int>(
                name: "Scope",
                table: "RecurringExpenses",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Scope",
                table: "BookkeepingEntries",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenses_OwnerUserId_Scope_IsActive",
                table: "RecurringExpenses",
                columns: new[] { "OwnerUserId", "Scope", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_BookkeepingEntries_OwnerUserId_Scope_EntryDate",
                table: "BookkeepingEntries",
                columns: new[] { "OwnerUserId", "Scope", "EntryDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RecurringExpenses_OwnerUserId_Scope_IsActive",
                table: "RecurringExpenses");

            migrationBuilder.DropIndex(
                name: "IX_BookkeepingEntries_OwnerUserId_Scope_EntryDate",
                table: "BookkeepingEntries");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "RecurringExpenses");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "BookkeepingEntries");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenses_OwnerUserId_IsActive",
                table: "RecurringExpenses",
                columns: new[] { "OwnerUserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_BookkeepingEntries_OwnerUserId_EntryDate",
                table: "BookkeepingEntries",
                columns: new[] { "OwnerUserId", "EntryDate" });
        }
    }
}
